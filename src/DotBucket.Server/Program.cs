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
builder.Services.AddSingleton<NotificationDispatcher>();

// Configure Storage Engine
builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection(StorageOptions.SectionName)
);

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
builder.Services.AddSingleton<ICredentialStore, ConfigurableCredentialStore>();
builder.Services.AddSingleton<ISigV4Authenticator, SigV4Authenticator>();
builder.Services.AddScoped<AdminTokenEndpointFilter>();

// Add Native AOT-safe OpenAPI documentation (.NET 10 feature)
builder.Services.AddOpenApi();

// Configure JSON options for Minimal APIs to use the source-generated context
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, StorageObjectJsonContext.Default);
});

var app = builder.Build();

app.UseCors("AllowFrontend");

// Map OpenAPI endpoint
app.MapOpenApi();

// S3 ListBuckets Middleware (Intersects GET / for S3 clients only)
app.Use(
    async (context, next) =>
    {
        if (context.Request.Path == "/" && context.Request.Method == HttpMethods.Get)
        {
            var isS3Request =
                context.Request.Headers.ContainsKey("Authorization")
                || context.Request.Headers.ContainsKey("x-amz-date");

            if (isS3Request)
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

// Serve static files from the wwwroot directory (the compiled React app)
app.MapStaticAssets();

// Add the S3 Auth and Authorization middlewares only for non-UI/API paths
app.UseWhen(
    ctx =>
        !ctx.Request.Path.StartsWithSegments("/admin")
        && !ctx.Request.Path.StartsWithSegments("/health")
        && !ctx.Request.Path.StartsWithSegments("/assets")
        && !ctx.Request.Path.StartsWithSegments("/_internal")
        && !ctx.Request.Path.StartsWithSegments("/openapi")
        && !ctx.Request.Path.Value!.EndsWith(".js")
        && !ctx.Request.Path.Value!.EndsWith(".css")
        && !ctx.Request.Path.Value!.EndsWith(".svg")
        && !ctx.Request.Path.Value!.EndsWith(".ico")
        && !ctx.Request.Headers.Accept.ToString().Contains("text/html"),
    appBuilder =>
    {
        appBuilder.UseMiddleware<S3AuthMiddleware>();
        appBuilder.UseMiddleware<S3AuthorizationMiddleware>();
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

// Basic health check endpoint (bypasses Auth)
app.MapGet("/health", () => Results.Ok(new AdminHealthResponse("Healthy")));

// Fallback all other non-API routes to serve the React Frontend SPA
app.MapFallbackToFile("index.html");

// Force storage engine initialization (runs DB migrations including IAM tables)
_ = app.Services.GetRequiredService<IStorageEngine>();

// Seed IAM (built-in policies + config credential migration)
var seeder = app.Services.GetRequiredService<IamSeeder>();
await seeder.SeedAsync();

await app.RunAsync();
