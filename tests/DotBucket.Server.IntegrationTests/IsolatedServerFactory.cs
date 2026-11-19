// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using DotBucket.Server.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DotBucket.Server.IntegrationTests;

public sealed class IsolatedServerFactory : WebApplicationFactory<Program>
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory(
        "dotbucket-integration-"
    );

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureServices(services =>
            services.PostConfigure<StorageOptions>(options => options.RootPath = _root.FullName)
        );

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_root.Exists)
            _root.Delete(recursive: true);
    }
}
