// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using DotBucket.Server.Models;

namespace DotBucket.Server.Services;

public class NotificationDispatcher(HttpClient httpClient, ILogger<NotificationDispatcher> logger)
{
    private readonly SemaphoreSlim _concurrencyLimit = new(10, 10);

    public async Task DispatchAsync(
        string webhookUrl,
        S3Event s3Event,
        CancellationToken cancellationToken
    )
    {
        // Apply a strict concurrency limit to prevent task buildup (Issue #10)
        if (!await _concurrencyLimit.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken))
        {
            logger.LogWarning("Skipping notification to {Url} due to high load.", webhookUrl);
            return;
        }

        try
        {
            logger.LogInformation(
                "Dispatching notification to {Url} for event {Event} on {Bucket}/{Key}",
                webhookUrl,
                s3Event.EventName,
                s3Event.BucketName,
                s3Event.ObjectKey
            );

            // Use a short timeout for webhooks to prevent hanging connections
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var response = await httpClient.PostAsJsonAsync(
                webhookUrl,
                s3Event,
                StorageObjectJsonContext.Default.S3Event,
                cts.Token
            );

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Failed to dispatch notification to {Url}. Status: {Status}",
                    webhookUrl,
                    response.StatusCode
                );
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Notification to {Url} timed out.", webhookUrl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error dispatching notification to {Url}", webhookUrl);
        }
        finally
        {
            _concurrencyLimit.Release();
        }
    }
}
