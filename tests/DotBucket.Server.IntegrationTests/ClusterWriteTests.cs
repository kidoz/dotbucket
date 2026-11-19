// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using DotBucket.Server.Cluster;
using DotBucket.Server.Configuration;
using DotBucket.Server.Models;
using DotBucket.Server.Services;
using DotBucket.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.IntegrationTests;

public sealed class ClusterWriteTests
{
    [Fact]
    public async Task ForwardedWritesAndMultipartReachOwnersAndFailedQuorumPreservesPriorData()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("dotbucket-cluster-");
        var token = Guid.NewGuid().ToString("N");
        var entries = new List<ClusterOptions.NodeEntry>();
        var outage = new CommitOutage();
        var hosts = new List<WebApplication>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                var id = $"node-{i}";
                var options = new ClusterOptions
                {
                    Enabled = true,
                    NodeId = id,
                    LeaderNodeId = "node-0",
                    ClusterToken = token,
                    Nodes = entries,
                    ReplicationFactor = 3,
                    WriteQuorum = 3,
                    HealthCheckIntervalMs = 100,
                };
                builder.Services.AddSingleton(Options.Create(options));
                builder.Services.AddSingleton(
                    Options.Create(
                        new StorageOptions
                        {
                            RootPath = Path.Combine(root.FullName, id),
                            MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                        }
                    )
                );
                builder
                    .Services.AddHttpClient("ClusterNode")
                    .AddHttpMessageHandler(() => new CommitHandler(outage));
                builder.Services.AddSingleton<ClusterState>();
                builder.Services.AddSingleton<DataPlacement>();
                builder.Services.AddSingleton<NodeClient>();
                builder.Services.AddSingleton<LocalFileSystemStorageEngine>();
                builder.Services.AddSingleton<DistributedStorageEngine>();
                builder.Services.AddSingleton<NotificationDispatcher>();
                var app = builder.Build();
                hosts.Add(app);
                app.MapInternalEndpoints();
                await app.StartAsync(ct);
                options.AdvertiseAddress = app.Urls.Single();
                entries.Add(
                    new ClusterOptions.NodeEntry { NodeId = id, Address = options.AdvertiseAddress }
                );
            }

            var stores = hosts
                .Select(h => h.Services.GetRequiredService<LocalFileSystemStorageEngine>())
                .ToArray();
            var ingress = hosts[1].Services.GetRequiredService<DistributedStorageEngine>();
            await ingress.CreateBucketAsync("locked", true, ct);
            foreach (var store in stores)
                Assert.True((await store.GetBucketAsync("locked", ct))!.ObjectLock.Enabled);

            const string bucket = "documents";
            const string key = "report.tmp";
            await ingress.CreateBucketAsync(bucket, false, ct);
            var primary = hosts.Single(h =>
                h.Services.GetRequiredService<DataPlacement>().IsPrimaryOwner(bucket, key)
            );
            var follower = hosts.First(h => h != primary);
            ingress = follower.Services.GetRequiredService<DistributedStorageEngine>();
            var original = RandomNumberGenerator.GetBytes(64 * 1024);
            await ingress.PutObjectAsync(
                bucket,
                key,
                new MemoryStream(original),
                "application/pdf",
                encryption: "AES256",
                cancellationToken: ct
            );
            foreach (var store in stores)
                await AssertBytesAsync(store, bucket, key, original, ct);

            var assembled = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
            var upload = await ingress.InitiateMultipartUploadAsync(
                bucket,
                key,
                "application/pdf",
                encryption: "AES256",
                cancellationToken: ct
            );
            var etag = await ingress.UploadPartAsync(
                bucket,
                key,
                upload,
                1,
                new MemoryStream(assembled),
                ct
            );
            var completed = await ingress.CompleteMultipartUploadAsync(
                bucket,
                key,
                upload,
                [(1, etag)],
                ct
            );
            foreach (var store in stores)
            {
                Assert.Equal(
                    completed.ETag,
                    (await store.HeadObjectAsync(bucket, key, null, ct))!.ETag
                );
                await AssertBytesAsync(store, bucket, key, assembled, ct);
            }

            // Drop one commit request after a quorum has durably prepared. Start the normal
            // recovery worker after restoring transport and check that it delivers the decision.
            outage.Address = entries[hosts.IndexOf(follower)].Address;
            var coordinator = primary.Services.GetRequiredService<DistributedStorageEngine>();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator.PutObjectAsync(
                    bucket,
                    key,
                    new MemoryStream(original),
                    "application/pdf",
                    cancellationToken: ct
                )
            );
            outage.Address = null;
            using (
                var recovery = ActivatorUtilities.CreateInstance<HealthMonitorService>(
                    primary.Services
                )
            )
            {
                await recovery.StartAsync(ct);
                var limit = DateTime.UtcNow.AddSeconds(10);
                var delayed = follower.Services.GetRequiredService<LocalFileSystemStorageEngine>();
                while (
                    (await delayed.HeadObjectAsync(bucket, key, null, ct))?.Size != original.Length
                    && DateTime.UtcNow < limit
                )
                    await Task.Delay(50, ct);
                foreach (var store in stores)
                    await AssertBytesAsync(store, bucket, key, original, ct);
                await recovery.StopAsync(ct);
            }

            var down = hosts.Single(h => h != primary && h != follower);
            await down.StopAsync(ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                primary
                    .Services.GetRequiredService<DistributedStorageEngine>()
                    .PutObjectAsync(
                        bucket,
                        key,
                        new MemoryStream(assembled),
                        "application/pdf",
                        cancellationToken: ct
                    )
            );
            foreach (var store in stores)
                await AssertBytesAsync(store, bucket, key, original, ct);
        }
        finally
        {
            foreach (var host in hosts)
                await host.DisposeAsync();
            root.Delete(true);
        }
    }

    private sealed class CommitOutage
    {
        public volatile string? Address;
    }

    private sealed class CommitHandler(CommitOutage outage) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.StartsWith("/_internal/staged/")
                && request.RequestUri.GetLeftPart(UriPartial.Authority) == outage.Address
            )
                return Task.FromResult(
                    new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                );
            return base.SendAsync(request, ct);
        }
    }

    private static async Task AssertBytesAsync(
        LocalFileSystemStorageEngine store,
        string bucket,
        string key,
        byte[] expected,
        CancellationToken ct
    )
    {
        var read = await store.GetObjectAsync(bucket, key, cancellationToken: ct);
        Assert.NotNull(read);
        await using var content = read.Value.Content;
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, ct);
        Assert.Equal(expected, copy.ToArray());
    }
}
