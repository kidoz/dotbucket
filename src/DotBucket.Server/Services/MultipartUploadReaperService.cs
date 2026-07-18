// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using DotBucket.Server.Cluster;
using DotBucket.Server.Configuration;
using DotBucket.Server.Storage;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Services;

/// <summary>
/// Periodically aborts in-progress multipart uploads older than the configured
/// retention window. Closes the disk-leak vector where a client initiates
/// uploads and never completes or aborts them: each abandoned upload holds a
/// <c>multipart_uploads</c> row plus a <c>.uploads/{uploadId}/</c> parts
/// directory whose files can be up to 5 GiB each, with no prior TTL.
///
/// Aborts reuse <see cref="IStorageEngine.AbortMultipartUploadAsync"/>, which
/// is idempotent (deletes the parts directory and both DB rows; no-op if the
/// upload is already gone). Like <see cref="LifecycleExpirationService"/>,
/// this service is single-node only in v1: in cluster mode, multipart state is
/// node-local and must be reaped per node.
/// </summary>
public class MultipartUploadReaperService(
    IStorageEngine storageEngine,
    ClusterState cluster,
    IOptions<LifecycleOptions> options,
    ILogger<MultipartUploadReaperService> logger
) : BackgroundService
{
    private readonly LifecycleOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ReaperEnabled)
        {
            logger.LogDebug("Multipart-upload reaper is disabled.");
            return;
        }

        // v1: single-node only. Multipart state is node-local in cluster mode.
        if (cluster.IsDistributed)
        {
            logger.LogInformation("Multipart-upload reaper is disabled in cluster mode (v1).");
            return;
        }

        logger.LogInformation(
            "Multipart-upload reaper started, scanning every {Interval}s, retention {Days} day(s).",
            _options.ScanIntervalSeconds,
            _options.AbortedMultipartUploadRetentionDays
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
                    "Multipart-upload reaper scan pass failed; will retry next interval."
                );
            }
        }
    }

    private async Task ScanPassAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_options.AbortedMultipartUploadRetentionDays);
        var buckets = await storageEngine.ListBucketsAsync(ct);

        var totalAborted = 0;
        foreach (var bucket in buckets)
        {
            ct.ThrowIfCancellationRequested();

            var uploads = await storageEngine.ListMultipartUploadsAsync(bucket.Name, null, ct);
            foreach (var upload in uploads)
            {
                ct.ThrowIfCancellationRequested();
                if (upload.Initiated >= cutoff)
                    continue;

                try
                {
                    // objectKey is unused by AbortMultipartUploadAsync; passing "" is safe.
                    await storageEngine.AbortMultipartUploadAsync(
                        bucket.Name,
                        "",
                        upload.UploadId,
                        ct
                    );
                    totalAborted++;
                    logger.LogInformation(
                        "Reaped abandoned multipart upload {UploadId} in bucket {Bucket} (initiated {Initiated:O}).",
                        upload.UploadId,
                        bucket.Name,
                        upload.Initiated
                    );
                }
                catch (Exception ex)
                {
                    // Abort is idempotent and should not throw for missing rows; log and continue.
                    logger.LogWarning(
                        ex,
                        "Failed to reap abandoned multipart upload {UploadId} in bucket {Bucket}; will retry next pass.",
                        upload.UploadId,
                        bucket.Name
                    );
                }
            }
        }

        if (totalAborted > 0)
        {
            logger.LogInformation(
                "Multipart-upload reaper aborted {Count} abandoned upload(s).",
                totalAborted
            );
        }
    }
}
