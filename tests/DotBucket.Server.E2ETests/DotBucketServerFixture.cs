// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DotBucket.Server.E2ETests;

/// <summary>
/// Boots the real DotBucket server binary (Kestrel, production environment, real SigV4
/// authentication — no test doubles) on a free loopback port with an isolated temp
/// storage root, and tears it down after the test class completes.
/// </summary>
public sealed class DotBucketServerFixture : IAsyncLifetime
{
    public const string RootAccessKey = "e2e-root-access-key";
    public const string RootSecretKey = "e2e-root-secret-key-0123456789abcdef";
    public const string AdminToken = "e2e-admin-token-0123456789abcdef";

    private Process? _serverProcess;
    private string _storageRoot = "";
    private readonly StringBuilder _serverLog = new();

    public string BaseUrl { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var serverDll = LocateServerDll();
        var port = GetFreeTcpPort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _storageRoot = Path.Combine(Path.GetTempPath(), $"dotbucket-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { "exec", serverDll },
            WorkingDirectory = Path.GetDirectoryName(serverDll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment =
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["ASPNETCORE_URLS"] = BaseUrl,
                ["Auth__RootAccessKey"] = RootAccessKey,
                ["Auth__RootSecretKey"] = RootSecretKey,
                ["Auth__AdminToken"] = AdminToken,
                ["Storage__RootPath"] = _storageRoot,
                ["Storage__MasterKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            },
        };

        _serverProcess = Process.Start(startInfo)!;
        _serverProcess.OutputDataReceived += (_, e) => AppendLog(e.Data);
        _serverProcess.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        _serverProcess.BeginOutputReadLine();
        _serverProcess.BeginErrorReadLine();

        await WaitForHealthyAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_serverProcess is { HasExited: false })
        {
            _serverProcess.Kill(entireProcessTree: true);
            await _serverProcess.WaitForExitAsync();
        }
        _serverProcess?.Dispose();

        if (Directory.Exists(_storageRoot))
            Directory.Delete(_storageRoot, recursive: true);
    }

    private void AppendLog(string? line)
    {
        if (line == null)
            return;
        lock (_serverLog)
            _serverLog.AppendLine(line);
    }

    private async Task WaitForHealthyAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            if (_serverProcess is { HasExited: true })
                break;

            try
            {
                var response = await http.GetAsync("/health");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
                // Server not listening yet.
            }

            await Task.Delay(250);
        }

        string log;
        lock (_serverLog)
            log = _serverLog.ToString();
        throw new InvalidOperationException(
            $"DotBucket server did not become healthy at {BaseUrl}. Server output:\n{log}"
        );
    }

    private static string LocateServerDll()
    {
        // Walk up from the test output directory to the repo root (marked by the solution
        // file), then resolve the server binary built for the same configuration.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DotBucket.slnx")))
            dir = dir.Parent;

        if (dir == null)
            throw new InvalidOperationException(
                "Could not locate repo root (DotBucket.slnx) above " + AppContext.BaseDirectory
            );

        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}"
        )
            ? "Release"
            : "Debug";

        var serverDll = Path.Combine(
            dir.FullName,
            "src",
            "DotBucket.Server",
            "bin",
            configuration,
            "net10.0",
            "DotBucket.Server.dll"
        );

        if (!File.Exists(serverDll))
            throw new InvalidOperationException(
                $"Server binary not found at {serverDll}. Build the solution first."
            );

        return serverDll;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
