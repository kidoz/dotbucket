// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using DotBucket.Server.Cluster;
using DotBucket.Server.Configuration;
using DotBucket.Server.Models;
using DotBucket.Server.Storage;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Services;

/// <summary>
/// Periodically scans buckets and expires objects whose lifecycle rules have elapsed.
/// Expiration reuses <see cref="IStorageEngine.DeleteObjectAsync"/>, so versioned
/// buckets get delete markers, unversioned buckets are hard-deleted, and objects under
/// legal hold / retention are never removed (their delete throws and is skipped).
/// </summary>
public class LifecycleExpirationService(
    IStorageEngine storageEngine,
    ClusterState cluster,
    IOptions<LifecycleOptions> options,
    ILogger<LifecycleExpirationService> logger
) : BackgroundService
{
    private readonly LifecycleOptions _options = options.Value;

    // Safety cap on batches per rule per pass, to bound a single scan's work.
    private const int MaxBatchesPerRulePerPass = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogDebug("Lifecycle expiration is disabled.");
            return;
        }

        // v1: single-node only. Cluster-wide expiration is a follow-up.
        if (cluster.IsDistributed)
        {
            logger.LogInformation("Lifecycle expiration is disabled in cluster mode (v1).");
            return;
        }

        logger.LogInformation(
            "Lifecycle expiration started, scanning every {Interval}s.",
            _options.ScanIntervalSeconds
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.ScanIntervalSeconds), stoppingToken);

            try
            {
                await ScanPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Lifecycle expiration scan pass failed; will retry next interval."
                );
            }
        }
    }

    private async Task ScanPassAsync(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var buckets = await storageEngine.ListBucketsAsync(ct);

        foreach (var bucket in buckets)
        {
            ct.ThrowIfCancellationRequested();

            var config = await storageEngine.GetLifecycleAsync(bucket.Name, ct);
            if (config == null || config.Rules.Count == 0)
                continue;

            foreach (var rule in config.Rules)
            {
                if (!rule.Enabled)
                    continue;

                // Determine the cutoff: objects with last_modified <= cutoff are due.
                DateTime cutoff;
                if (rule.ExpirationDays is int days)
                {
                    cutoff = nowUtc.AddDays(-days);
                }
                else if (rule.ExpirationDate is DateTime date)
                {
                    if (nowUtc < date)
                        continue; // date not yet reached
                    cutoff = nowUtc; // all matching objects are due
                }
                else
                {
                    continue; // no expiration action
                }

                var deleted = await ExpireRuleAsync(bucket.Name, rule, cutoff, ct);
                if (deleted > 0)
                {
                    logger.LogInformation(
                        "Lifecycle expired {Count} object(s) in bucket {Bucket} (rule {Rule}).",
                        deleted,
                        bucket.Name,
                        rule.Id ?? "(unnamed)"
                    );
                }
            }
        }
    }

    private async Task<int> ExpireRuleAsync(
        string bucket,
        LifecycleRule rule,
        DateTime cutoff,
        CancellationToken ct
    )
    {
        var totalDeleted = 0;
        for (var batch = 0; batch < MaxBatchesPerRulePerPass; batch++)
        {
            var due = (
                await storageEngine.ListExpiredObjectsAsync(
                    bucket,
                    rule.Prefix,
                    cutoff,
                    _options.BatchSize,
                    ct
                )
            ).ToList();

            if (due.Count == 0)
                break;

            var deletedThisBatch = 0;
            foreach (var (key, _) in due)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // versionId: null => versioned buckets get a delete marker, others hard-delete.
                    await storageEngine.DeleteObjectAsync(bucket, key, null, ct);
                    totalDeleted++;
                    deletedThisBatch++;
                }
                catch (InvalidOperationException ex)
                    when (ex.Message is "ObjectUnderLegalHold" or "ObjectUnderRetention")
                {
                    logger.LogDebug(
                        "Skipping locked object {Bucket}/{Key} during expiration.",
                        bucket,
                        key
                    );
                }
            }

            // If the batch wasn't full, there's nothing more to drain for this rule.
            if (due.Count < _options.BatchSize)
                break;

            // No progress (all remaining matches are locked) — re-querying would loop.
            if (deletedThisBatch == 0)
                break;
        }

        return totalDeleted;
    }
}
