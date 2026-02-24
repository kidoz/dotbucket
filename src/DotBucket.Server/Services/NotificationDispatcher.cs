// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Net;
using System.Net.Sockets;
using DotBucket.Server.Models;

namespace DotBucket.Server.Services;

public class NotificationDispatcher(
    IHttpClientFactory httpClientFactory,
    ILogger<NotificationDispatcher> logger
)
{
    private readonly SemaphoreSlim _concurrencyLimit = new(10, 10);

    public async Task DispatchAsync(
        string webhookUrl,
        S3Event s3Event,
        CancellationToken cancellationToken
    )
    {
        if (!IsAllowedWebhookUrl(webhookUrl))
        {
            logger.LogWarning(
                "Blocked webhook notification to disallowed URL {Url} (private/internal address or non-HTTPS scheme).",
                webhookUrl
            );
            return;
        }

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

            using var httpClient = httpClientFactory.CreateClient("WebhookClient");
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

    /// <summary>
    /// Validates that a webhook URL is safe to call (prevents SSRF).
    /// Blocks private/internal IPs, non-HTTPS schemes, and localhost.
    /// DNS is NOT resolved here to avoid TOCTOU — IP validation happens at connect time via CreateSsrfSafeHandler.
    /// </summary>
    private static bool IsAllowedWebhookUrl(string webhookUrl)
    {
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri))
            return false;

        // Only allow HTTPS
        if (uri.Scheme != "https")
            return false;

        // Block localhost and loopback
        var host = uri.Host;
        if (host is "localhost" || host.EndsWith(".localhost"))
            return false;

        // If the host is a literal IP, validate it immediately
        if (IPAddress.TryParse(host, out var ip))
            return !IsPrivateOrReservedIp(ip);

        // Block internal DNS patterns
        if (host.EndsWith(".internal") || host.EndsWith(".local"))
            return false;

        // Hostname DNS resolution is deferred to connect-time callback to prevent TOCTOU rebinding
        return true;
    }

    /// <summary>
    /// Creates a SocketsHttpHandler with a ConnectCallback that validates resolved IPs at connection time,
    /// preventing DNS rebinding TOCTOU attacks.
    /// </summary>
    public static SocketsHttpHandler CreateSsrfSafeHandler()
    {
        return new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(
                    context.DnsEndPoint.Host,
                    cancellationToken
                );

                foreach (var address in addresses)
                {
                    if (IsPrivateOrReservedIp(address))
                    {
                        throw new HttpRequestException(
                            $"Connection to private/reserved IP {address} blocked (SSRF protection)."
                        );
                    }
                }

                // Connect to the first allowed address
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }

    internal static bool IsPrivateOrReservedIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Block link-local (fe80::), unique local (fc00::/7), loopback (::1)
            var bytes = ip.GetAddressBytes();
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
                return true; // link-local
            if ((bytes[0] & 0xfe) == 0xfc)
                return true; // unique local
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (bytes[0] == 10)
                return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;
            // 127.0.0.0/8
            if (bytes[0] == 127)
                return true;
            // 169.254.0.0/16 (link-local)
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;
            // 0.0.0.0/8
            if (bytes[0] == 0)
                return true;
        }

        return false;
    }
}
