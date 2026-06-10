// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using DotBucket.Server.Configuration;
using DotBucket.Server.Models;
using DotBucket.Server.Storage;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Services;

/// <summary>
/// Provisions buckets declared in configuration at startup. Idempotent: existing
/// buckets are left untouched (only versioning is (re)applied if specified).
/// </summary>
public class BucketSeeder(
    IStorageEngine storageEngine,
    IOptions<StorageOptions> options,
    ILogger<BucketSeeder> logger
)
{
    private readonly StorageOptions _options = options.Value;

    public async Task SeedAsync(CancellationToken ct = default)
    {
        foreach (var seed in _options.Buckets)
        {
            if (string.IsNullOrWhiteSpace(seed.Name))
                continue;

            try
            {
                if (!await storageEngine.BucketExistsAsync(seed.Name, ct))
                {
                    await storageEngine.CreateBucketAsync(seed.Name, seed.ObjectLock, ct);
                    logger.LogInformation("Provisioned bucket {Bucket} from configuration", seed.Name);
                }

                if (!string.IsNullOrWhiteSpace(seed.Versioning))
                {
                    var status = seed.Versioning switch
                    {
                        "Enabled" => VersioningStatus.Enabled,
                        "Suspended" => VersioningStatus.Suspended,
                        _ => VersioningStatus.Off,
                    };
                    await storageEngine.SetVersioningAsync(seed.Name, status, ct);
                }
            }
            catch (InvalidOperationException)
            {
                // Bucket already exists (race or pre-existing); provisioning is idempotent.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to provision bucket {Bucket}", seed.Name);
            }
        }
    }
}
