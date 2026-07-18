using System.Globalization;
using System.Text;
using System.Threading.RateLimiting;
using System.Xml;
using System.Xml.Linq;
using DotBucket.Server.Auth;
using DotBucket.Server.Cluster;
using DotBucket.Server.Configuration;
using DotBucket.Server.Endpoints.Admin;
using DotBucket.Server.Endpoints.S3;
using DotBucket.Server.Iam;
using DotBucket.Server.Middleware;
using DotBucket.Server.Models;
using DotBucket.Server.Services;
using DotBucket.Server.Storage;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Configure Auth
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
var authOptions =
    builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>()
    ?? new AuthOptions();

// Configure CORS for the frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "AllowFrontend",
        policy =>
        {
            var origins =
                authOptions.AllowedOrigins.Count > 0
                    ? authOptions.AllowedOrigins.ToArray()
                    : new[] { "http://localhost:5173" };
            policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
        }
    );
});

// Register Notification Services
builder.Services.AddHttpClient();
builder
    .Services.AddHttpClient("WebhookClient")
    .ConfigurePrimaryHttpMessageHandler(() => NotificationDispatcher.CreateSsrfSafeHandler());
builder.Services.AddSingleton<NotificationDispatcher>();

// Background lifecycle/expiration worker (self-gates on config + cluster mode)
builder.Services.AddHostedService<LifecycleExpirationService>();

// Background reaper for abandoned multipart uploads (self-gates on config + cluster mode)
builder.Services.AddHostedService<MultipartUploadReaperService>();

// Configure Storage Engine
builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection(StorageOptions.SectionName)
);
var storageOptions =
    builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>()
    ?? new StorageOptions();

// Configure S3 addressing/region options
builder.Services.Configure<S3Options>(builder.Configuration.GetSection(S3Options.SectionName));

// Configure rate limiting (S3 per access key, admin per client IP)
builder.Services.Configure<RateLimitOptions>(
    builder.Configuration.GetSection(RateLimitOptions.SectionName)
);

// Configure lifecycle/expiration options
builder.Services.Configure<LifecycleOptions>(
    builder.Configuration.GetSection(LifecycleOptions.SectionName)
);

// Configure Cluster
builder.Services.Configure<ClusterOptions>(
    builder.Configuration.GetSection(ClusterOptions.SectionName)
);
var clusterConfig = builder
    .Configuration.GetSection(ClusterOptions.SectionName)
    .Get<ClusterOptions>();

// Fail closed on fatal misconfiguration before wiring up services. Outside Development,
// missing/weak credentials, an invalid encryption key, unsafe storage roots, or incomplete
// cluster identity abort startup with a clear error instead of running in an unsafe state.
var startupValidation = StartupValidator.Validate(
    builder.Environment.IsDevelopment(),
    authOptions,
    storageOptions,
    clusterConfig
);
if (startupValidation.Errors.Count > 0)
{
    var detail = string.Join(Environment.NewLine + "  - ", startupValidation.Errors);
    throw new InvalidOperationException(
        "Fatal configuration error(s); refusing to start:" + Environment.NewLine + "  - " + detail
    );
}

if (clusterConfig?.Enabled == true)
{
    builder.Services.AddSingleton<ClusterState>();
    builder.Services.AddSingleton<LocalFileSystemStorageEngine>();
    builder.Services.AddSingleton<DataPlacement>();
    builder.Services.AddSingleton<NodeClient>();
    builder.Services.AddHostedService<HealthMonitorService>();
    builder.Services.AddSingleton<IStorageEngine, DistributedStorageEngine>();

    // Inter-node HTTP client. When a trusted CA bundle is configured, validate peer
    // certificates against that custom root (for private/enterprise CAs); otherwise
    // the default OS trust store is used.
    builder
        .Services.AddHttpClient("ClusterNode")
        .ConfigurePrimaryHttpMessageHandler(sp =>
            ClusterHttpClientFactory.CreateHandler(
                sp.GetRequiredService<IOptions<ClusterOptions>>().Value
            )
        );
}
else
{
    builder.Services.AddSingleton<ClusterState>();
    builder.Services.AddSingleton<IStorageEngine, LocalFileSystemStorageEngine>();
}

// Register Identity and Access Management (IAM) components
builder.Services.AddSingleton<IamStore>();
builder.Services.AddSingleton<PolicyEngine>();
builder.Services.AddSingleton<IamSeeder>();
builder.Services.AddSingleton<BucketSeeder>();
builder.Services.AddSingleton<ICredentialStore, ConfigurableCredentialStore>();
builder.Services.AddSingleton<ISigV4Authenticator, SigV4Authenticator>();
builder.Services.AddScoped<AdminTokenEndpointFilter>();

// Add Native AOT-safe OpenAPI documentation (.NET 10 feature)
builder.Services.AddOpenApi();

// Rate limiting: two named token-bucket policies. The "s3" policy partitions
// per authenticated access key (HttpContext.Items["AccessKey"], set by
// SigV4Authenticator) with a fallback to client IP; the "admin" policy
// partitions per client IP. /health and /_internal are exempt because the
// limiter is wired inside the S3 UseWhen branch and admin groups attach the
// policy explicitly (see MapAdminEndpoints / MapIamEndpoints).
//
// The policies are always registered so endpoint groups can statically declare
// RequireRateLimiting("...") without throwing when RateLimit:Enabled is false.
// Enforcement is gated on app.UseRateLimiter() being added to the pipeline,
// which only happens when Enabled is true.
var rateLimitOptions =
    builder.Configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>()
    ?? new RateLimitOptions();

builder.Services.AddRateLimiter(limiterOptions =>
{
    limiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiterOptions.OnRejected = (ctx, _) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            ctx.HttpContext.Response.Headers.RetryAfter = retryAfter.TotalSeconds.ToString(
                CultureInfo.InvariantCulture
            );
        }
        return ValueTask.CompletedTask;
    };

    // S3 surface: per access key, falls back to client IP.
    limiterOptions.AddPolicy(
        "s3",
        httpContext =>
        {
            var accessKey = httpContext.Items.TryGetValue("AccessKey", out var ak)
                ? ak as string
                : null;
            var partition =
                accessKey ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon";
            return RateLimitPartition.GetTokenBucketLimiter(
                partition,
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = rateLimitOptions.S3Burst,
                    TokensPerPeriod = rateLimitOptions.S3PermitPerSecond,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = rateLimitOptions.S3Burst,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                }
            );
        }
    );

    // Admin/IAM surface: per client IP.
    limiterOptions.AddPolicy(
        "admin",
        httpContext =>
        {
            var partition = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon";
            return RateLimitPartition.GetTokenBucketLimiter(
                partition,
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = rateLimitOptions.AdminBurst,
                    TokensPerPeriod = rateLimitOptions.AdminPermitPerSecond,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = rateLimitOptions.AdminBurst,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                }
            );
        }
    );
});

// OpenTelemetry traces + metrics, exported via OTLP. Enabled only when an endpoint is
// configured (Observability:OtlpEndpoint or the standard OTEL_EXPORTER_OTLP_ENDPOINT).
// Uses the runtime's built-in activity sources and meters instead of instrumentation
// packages to stay Native AOT / full-trim compatible.
var otlpEndpoint =
    builder.Configuration["Observability:OtlpEndpoint"]
    ?? builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
if (!string.IsNullOrEmpty(otlpEndpoint))
{
    var serviceName = builder.Configuration["Observability:ServiceName"] ?? "dotbucket";
    var serviceInstanceId =
        clusterConfig?.Enabled == true ? clusterConfig.NodeId : Environment.MachineName;

    builder
        .Services.AddOpenTelemetry()
        .ConfigureResource(resource =>
            resource.AddService(serviceName, serviceInstanceId: serviceInstanceId)
        )
        .WithTracing(tracing =>
            tracing
                .AddSource("Microsoft.AspNetCore")
                .AddSource("System.Net.Http")
                .AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint))
        )
        .WithMetrics(metrics =>
            metrics
                .AddMeter("Microsoft.AspNetCore.Hosting")
                .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                .AddMeter("System.Net.Http")
                .AddMeter("System.Runtime")
                .AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint))
        );
}

// Configure HSTS for production
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});

// Configure JSON options for Minimal APIs to use the source-generated context
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, StorageObjectJsonContext.Default);
});

var app = builder.Build();

// Surface non-fatal configuration warnings (fatal errors already aborted startup above).
foreach (var warning in startupValidation.Warnings)
{
    app.Logger.LogWarning("Configuration warning: {Warning}", warning);
}

// Several pieces of state are node-local and NOT replicated; surface these limitations loudly
// in cluster mode so operators configure routing/automation accordingly.
if (clusterConfig?.Enabled == true)
{
    app.Logger.LogWarning(
        "Cluster mode is enabled, but IAM state (users, policies, access keys) lives in a node-local SQLite database and is NOT replicated. "
            + "Apply IAM changes to every node, or they will only take effect on the node that received them."
    );
    app.Logger.LogWarning(
        "Cluster mode limitation: multipart uploads are node-local until completion. Initiate, every "
            + "UploadPart, and CompleteMultipartUpload for a given upload MUST be routed to the same node. "
            + "Configure session affinity (sticky routing) on your load balancer, or multipart completes will fail with InvalidPart."
    );
    app.Logger.LogWarning(
        "Cluster mode limitation: bucket lifecycle and notification configuration are node-local and are NOT "
            + "replicated. Apply them on every node (or via sticky routing), or they will only take effect on one node."
    );
}

// HTTPS enforcement
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseHttpsRedirection();

app.UseCors("AllowFrontend");

// Content-Security-Policy: defense-in-depth against XSS in the admin SPA.
// The SPA loads only same-origin scripts/styles/images and the React module
// entry; there are no inline scripts/styles to allow. S3 clients are unaffected
// (they do not interpret response headers). 'connect-src' restricts XHR/fetch
// to same-origin; if you add an external OTLP/console endpoint, extend this.
app.Use(
    async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            // Don't apply to S3 object responses (presigned downloads etc.) —
            // those are opaque bytes and adding a CSP can confuse browsers
            // into blocking downloads in some edge cases. Limit to admin/SPA/health.
            var path = context.Request.Path.Value ?? string.Empty;
            var isHtmlSurface =
                path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/assets", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/", StringComparison.Ordinal)
                || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/dotbucket-logo.svg", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/robots.txt", StringComparison.OrdinalIgnoreCase);

            if (isHtmlSurface && !context.Response.Headers.ContainsKey("Content-Security-Policy"))
            {
                context.Response.Headers.ContentSecurityPolicy =
                    "default-src 'self'; "
                    + "script-src 'self'; "
                    + "style-src 'self'; "
                    + "img-src 'self' data:; "
                    + "font-src 'self'; "
                    + "connect-src 'self'; "
                    + "frame-ancestors 'none'; "
                    + "base-uri 'self'; "
                    + "form-action 'self'; "
                    + "object-src 'none'";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
            }

            return Task.CompletedTask;
        });
        await next();
    }
);

// Map OpenAPI endpoint (Development only)
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Serve static files from the wwwroot directory (the compiled React app)
app.MapStaticAssets();

// S3 Auth and Authorization middlewares apply to ALL non-excluded paths.
// S3AuthMiddleware handles the distinction between S3 requests (auth headers present)
// and SPA requests (no auth headers, only root path allowed through).
app.UseWhen(
    ctx =>
        !ctx.Request.Path.StartsWithSegments("/admin")
        && !ctx.Request.Path.StartsWithSegments("/health")
        && !ctx.Request.Path.StartsWithSegments("/assets")
        && !ctx.Request.Path.StartsWithSegments("/dotbucket-logo.svg")
        && !ctx.Request.Path.StartsWithSegments("/favicon.ico")
        && !ctx.Request.Path.StartsWithSegments("/robots.txt")
        && !ctx.Request.Path.StartsWithSegments("/_internal"),
    appBuilder =>
    {
        appBuilder.UseMiddleware<S3AuthMiddleware>();
        // Rate limiting runs AFTER auth so the "s3" policy can partition on
        // the authenticated access key (HttpContext.Items["AccessKey"]). Activating
        // UseRateLimiter here (rather than globally) auto-exempts /admin, /health,
        // /_internal, /assets because those paths are excluded from this UseWhen
        // branch. The "s3" policy is attached to MapS3Endpoints below.
        if (rateLimitOptions.Enabled)
        {
            appBuilder.UseRateLimiter();
        }
        // Runs AFTER signature verification so rewriting the path to inject the bucket
        // (for virtual-hosted-style requests) cannot invalidate the SigV4 signature.
        appBuilder.UseMiddleware<VirtualHostMiddleware>();
        appBuilder.UseMiddleware<S3AuthorizationMiddleware>();
    }
);

// S3 ListBuckets Middleware (Intersects GET / for authenticated S3 clients only)
// Runs AFTER auth middleware so only authenticated requests can list buckets.
app.Use(
    async (context, next) =>
    {
        if (context.Request.Path == "/" && context.Request.Method == HttpMethods.Get)
        {
            var isS3Request =
                context.Request.Headers.ContainsKey("Authorization")
                || context.Request.Headers.ContainsKey("x-amz-date");

            // Only serve bucket list if this is an authenticated S3 request
            if (isS3Request && context.Items.ContainsKey("AccessKey"))
            {
                var storageEngine = context.RequestServices.GetRequiredService<IStorageEngine>();
                var buckets = await storageEngine.ListBucketsAsync(context.RequestAborted);
                var s3Ns = (XNamespace)"http://s3.amazonaws.com/doc/2006-03-01/";

                var settings = new XmlWriterSettings
                {
                    OmitXmlDeclaration = false,
                    Encoding = Encoding.UTF8,
                };
                using var ms = new MemoryStream();
                using var writer = XmlWriter.Create(ms, settings);

                writer.WriteStartElement("ListAllMyBucketsResult", s3Ns.NamespaceName);
                writer.WriteStartElement("Owner");
                writer.WriteElementString("ID", "dotbucket-owner");
                writer.WriteElementString("DisplayName", "dotbucket-owner");
                writer.WriteEndElement();

                writer.WriteStartElement("Buckets");
                foreach (var bucket in buckets)
                {
                    writer.WriteStartElement("Bucket");
                    writer.WriteElementString("Name", bucket.Name);
                    writer.WriteElementString(
                        "CreationDate",
                        bucket.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                    );
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.Flush();

                context.Response.ContentType = "application/xml";
                await context.Response.Body.WriteAsync(ms.ToArray(), context.RequestAborted);
                return;
            }
        }
        await next();
    }
);

// Map S3 API endpoints
app.MapS3Endpoints();

// Map Admin API endpoints
app.MapAdminEndpoints();

// Map IAM admin API endpoints
app.MapIamEndpoints();

// Map Internal cluster endpoints (conditionally)
if (clusterConfig?.Enabled == true)
{
    app.MapInternalEndpoints();
}

// Health check endpoint with actual readiness probe (bypasses Auth).
// Probes two downstream signals: SQLite liveness (via ListBucketsAsync) and
// free disk space on the storage filesystem (returns 503 LowDiskSpace when
// below the configured threshold so load balancers stop sending writes).
app.MapGet(
    "/health",
    async (IStorageEngine storage, IOptions<StorageOptions> storageOptions, CancellationToken ct) =>
    {
        try
        {
            await storage.ListBucketsAsync(ct);

            // Free-disk-space probe. Best-effort: if DriveInfo can't resolve the
            // root (e.g. a network mount), the probe is skipped and only the DB
            // liveness signal gates healthiness.
            var diskReason = CheckFreeDiskSpace(storage.GetStorageRoot(), storageOptions.Value);
            if (diskReason is not null)
            {
                return Results.Json(
                    new AdminHealthResponse("Unhealthy", diskReason),
                    StorageObjectJsonContext.Default.AdminHealthResponse,
                    statusCode: 503
                );
            }

            return Results.Ok(new AdminHealthResponse("Healthy"));
        }
        catch
        {
            return Results.Json(
                new AdminHealthResponse("Unhealthy", "Storage probe failed."),
                StorageObjectJsonContext.Default.AdminHealthResponse,
                statusCode: 503
            );
        }
    }
);

static string? CheckFreeDiskSpace(string storageRoot, StorageOptions options)
{
    var percent = options.MinFreeSpacePercent;
    var bytes = options.MinFreeSpaceBytes;
    if (percent is null or 0.0 && bytes is null)
        return null;

    DriveInfo drive;
    try
    {
        drive = new DriveInfo(storageRoot);
    }
    catch (ArgumentException)
    {
        // Path doesn't map to a drive (e.g. UNC, network mount); skip the probe.
        return null;
    }

    var available = drive.AvailableFreeSpace;
    var total = drive.TotalSize;

    if (bytes is { } bytesThreshold && available < bytesThreshold)
    {
        return $"LowDiskSpace: {available} bytes free (threshold {bytesThreshold}).";
    }

    if (percent is { } pct and > 0.0 && total > 0)
    {
        var pctFree = (double)available / total * 100.0;
        if (pctFree < pct)
        {
            return $"LowDiskSpace: {pctFree:F1}% free (threshold {pct}%).";
        }
    }

    return null;
}

// Fallback all other non-API routes to serve the React Frontend SPA
app.MapFallbackToFile("index.html");

// Force storage engine initialization (runs DB migrations including IAM tables)
_ = app.Services.GetRequiredService<IStorageEngine>();

// Seed IAM (built-in policies + config credential migration)
var seeder = app.Services.GetRequiredService<IamSeeder>();
await seeder.SeedAsync();

// Provision buckets declared in configuration
var bucketSeeder = app.Services.GetRequiredService<BucketSeeder>();
await bucketSeeder.SeedAsync();

await app.RunAsync();
