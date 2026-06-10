using System.Text;
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

// Configure Storage Engine
builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection(StorageOptions.SectionName)
);

// Configure S3 addressing/region options
builder.Services.Configure<S3Options>(builder.Configuration.GetSection(S3Options.SectionName));

// Configure Cluster
builder.Services.Configure<ClusterOptions>(
    builder.Configuration.GetSection(ClusterOptions.SectionName)
);
var clusterConfig = builder
    .Configuration.GetSection(ClusterOptions.SectionName)
    .Get<ClusterOptions>();
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

// Validate root credentials at startup
if (
    string.IsNullOrEmpty(authOptions.RootAccessKey)
    || string.IsNullOrEmpty(authOptions.RootSecretKey)
)
{
    app.Logger.LogCritical(
        "Auth:RootAccessKey and Auth:RootSecretKey MUST be configured. Server is running without root credentials."
    );
}
else if (authOptions.RootAccessKey == "minioadmin" || authOptions.RootSecretKey == "minioadmin")
{
    app.Logger.LogCritical(
        "Auth:RootAccessKey and Auth:RootSecretKey are using well-known default values. Change them immediately."
    );
}

// HTTPS enforcement
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseHttpsRedirection();

app.UseCors("AllowFrontend");

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

// Health check endpoint with actual readiness probe (bypasses Auth)
app.MapGet(
    "/health",
    async (IStorageEngine storage, CancellationToken ct) =>
    {
        try
        {
            await storage.ListBucketsAsync(ct);
            return Results.Ok(new AdminHealthResponse("Healthy"));
        }
        catch
        {
            return Results.Json(
                new AdminHealthResponse("Unhealthy"),
                StorageObjectJsonContext.Default.AdminHealthResponse,
                statusCode: 503
            );
        }
    }
);

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
