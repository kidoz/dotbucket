# DotBucket

> **⚠️ STATUS: EXPERIMENTAL**  
> This project is in an early, experimental stage. It is under active development, and features, APIs, and the storage format are subject to breaking changes without notice. It is **not** recommended for production use.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

**DotBucket** is a lightweight, high-performance, S3-compatible object storage server built with .NET 10. A true restriction-free alternative to MinIO, released under the **MIT License** -- safe for enterprise, commercial, and closed-source integration without copyleft concerns.

## Features

- **S3 API Compatibility** -- Core S3 operations (PUT, GET, DELETE, LIST) with path-style **and** virtual-hosted-style access. Works with AWS SDK, Boto3, MinIO Client, and other standard S3 tools.
- **MIT Licensed** -- No AGPLv3 restrictions. No commercial license required. Ever.
- **High Performance** -- .NET 10, C# 14, Native AOT compilation. Zero external NuGet dependencies.
- **Built-in Admin Dashboard** -- React-based web UI to manage buckets and objects.
- **AWS SigV4 Authentication** -- Full AWS Signature Version 4 (AWS4-HMAC-SHA256) support, with static access keys and configurable session-token handling.
- **Flexible Addressing & Regions** -- Path-style and virtual-hosted-style URLs; configurable advertised region and optional strict signing-region enforcement.
- **Extensible Storage** -- Pluggable `IStorageEngine` interface. Ships with a robust local filesystem backend (SQLite metadata + atomic FS writes) and is architecturally ready for distributed storage via Rendezvous Hashing.
- **Advanced S3 Features** -- Supports Multipart Uploads (tunable buffering), Object Versioning, Object Locking (Retention & Legal Hold), Lifecycle/Expiration policies, and Server-Side Encryption (AES256).
- **Deployment Friendly** -- Provision buckets from configuration at startup, configurable storage base prefix, in-process HTTPS via Kestrel endpoints (enterprise/self-signed CA), and custom-CA trust for inter-node calls.
- **Docker Ready** -- Multi-stage Alpine image, runs as non-root on port 9000.

## Tech Stack

| Layer | Technology |
|---|---|
| Backend | .NET 10 / C# 14 (Minimal APIs, Native AOT) |
| Frontend | React 19, TypeScript, Tailwind CSS, Vite, Shadcn UI |
| Protocol | REST over HTTP (S3-compatible XML responses) |
| Storage | Local filesystem (extensible via `IStorageEngine`) |

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js](https://nodejs.org/) v20+ (for frontend development)
- [just](https://github.com/casey/just) (optional, but recommended for task automation)

### Quick Start with `just`

```bash
# Build and run the entire project
just build
just run
```

### Running with Docker

```bash
docker build -t dotbucket .
docker run -d -p 9000:9000 -v dotbucket-data:/data dotbucket
```

The server starts on port `9000` with data persisted to `/data`. The admin dashboard is available at `http://localhost:9000`.

### Running Locally (Manual)

```bash
# Clone the repository
git clone https://github.com/kidoz/dotbucket.git
cd dotbucket

# Build the frontend and copy to backend's wwwroot
cd src/DotBucket.UI
npm install
npm run build
mkdir -p ../DotBucket.Server/wwwroot
cp -r dist/* ../DotBucket.Server/wwwroot/

# Run the backend
cd ../DotBucket.Server
dotnet run
```

- Backend: `http://localhost:5236`
- Frontend dev server (if using `npm run dev`): `http://localhost:5173`

### Task Automation with `just`

If you have `just` installed, you can use the following commands:

| Command | Description |
|---|---|
| `just build` | Builds both frontend and backend |
| `just run` | Starts the .NET server |
| `just test` | Runs all tests |
| `just format` | Formats all C# and TypeScript code |
| `just lint` | Runs linter and checks for errors |

## S3 API Reference

Endpoints support **path-style** access (`/{bucket}/{key}`) and, when `S3:VirtualHostedStyle` is enabled, **virtual-hosted-style** (`bucket.{domain}/{key}`).

| Operation | Method | Route | Description |
|---|---|---|---|
| List Buckets | `GET` | `/` | Returns all buckets (XML `ListAllMyBucketsResult`) |
| Create Bucket | `PUT` | `/{bucket}` | Creates a new bucket. Returns `409` if it already exists |
| List Objects | `GET` | `/{bucket}` | Lists objects in a bucket. Supports `?prefix=` filter |
| Put Object | `PUT` | `/{bucket}/{key}` | Uploads an object. Supports `x-amz-meta-*` custom metadata |
| Get Object | `GET` | `/{bucket}/{key}` | Downloads an object with `ETag`, `Last-Modified`, and metadata headers |
| Delete Object | `DELETE` | `/{bucket}/{key}` | Deletes an object. Returns `204` (S3-compatible, even if not found) |

### Usage with AWS CLI

```bash
# Configure credentials (use the values you set for Auth:RootAccessKey / Auth:RootSecretKey)
aws configure set aws_access_key_id <your-access-key>
aws configure set aws_secret_access_key <your-secret-key>

# Create a bucket
aws --endpoint-url http://localhost:9000 s3 mb s3://my-bucket

# Upload a file
aws --endpoint-url http://localhost:9000 s3 cp myfile.txt s3://my-bucket/myfile.txt

# List objects
aws --endpoint-url http://localhost:9000 s3 ls s3://my-bucket

# Download a file
aws --endpoint-url http://localhost:9000 s3 cp s3://my-bucket/myfile.txt downloaded.txt

# Delete a file
aws --endpoint-url http://localhost:9000 s3 rm s3://my-bucket/myfile.txt
```

## Admin API

The admin API is served at `/admin` and requires **Bearer Token authentication**. The token is configured via `Auth:AdminToken`.

| Method | Route | Description |
|---|---|---|
| `GET` | `/admin/buckets` | List all buckets |
| `POST` | `/admin/buckets` | Create a bucket (`{ "name": "..." }`) |
| `GET` | `/admin/buckets/{name}/objects` | List objects in a bucket |
| `DELETE` | `/admin/buckets/{name}/objects/{key}` | Delete an object |

A health check endpoint is also available at `GET /health` (no authentication required).

## Configuration

| Setting | Default | Description |
|---|---|---|
| `Storage:RootPath` | `storage` | Root directory for bucket and object storage |
| `Storage:MasterKey` | `""` (required) | Base64-encoded 32-byte key for server-side encryption |
| `Storage:BasePrefix` | `""` | Optional sub-path under `RootPath` for all bucket data (metadata DB stays at root) |
| `Storage:MultipartBufferSizeBytes` | `81920` | Stream buffer size for multipart upload part I/O |
| `Storage:Buckets` | `[]` | Buckets to provision at startup, e.g. `[{ "Name": "data", "Versioning": "Enabled" }]` |
| `Auth:AdminToken` | `""` (required) | Bearer token for Admin API and Dashboard access |
| `Auth:RootAccessKey` | `""` (required) | Root S3 access key |
| `Auth:RootSecretKey` | `""` (required) | Root S3 secret key |
| `Auth:SessionTokenMode` | `Ignore` | `Ignore` accepts static keys regardless of `x-amz-security-token`; `Reject` denies token-bearing requests |
| `S3:Region` | `us-east-1` | Region advertised in `GetBucketLocation` and `x-amz-bucket-region` |
| `S3:StrictSigningRegion` | `false` | When `true`, only requests signed for `S3:Region` are accepted |
| `S3:VirtualHostedStyle` | `false` | Enable bucket-as-hostname addressing for the configured `S3:Domains` |
| `S3:Domains` | `[]` | Base domains carrying bucket subdomains, e.g. `["s3.example.com"]` |
| `Lifecycle:Enabled` | `true` | Enable the background object-expiration worker |
| `Lifecycle:ScanIntervalSeconds` | `3600` | Interval between lifecycle expiration scans |
| `Cluster:TrustedCaBundlePath` | `""` | PEM CA bundle to trust for inter-node HTTPS (empty = OS trust store) |
| `Observability:OtlpEndpoint` | `""` | OTLP collector endpoint for OpenTelemetry traces/metrics (empty = telemetry disabled); `OTEL_EXPORTER_OTLP_ENDPOINT` also works |
| `Observability:ServiceName` | `dotbucket` | `service.name` resource attribute on exported telemetry |

Configuration can be set via `appsettings.json`, environment variables (`Storage__RootPath`, `Auth__AdminToken`), or command-line arguments.

### HTTPS

DotBucket serves HTTP by default. To terminate TLS in-process (e.g. with an enterprise-CA certificate), configure the built-in ASP.NET Core `Kestrel:Endpoints` section — no code changes required:

```json
"Kestrel": {
  "Endpoints": {
    "Http":  { "Url": "http://0.0.0.0:9000" },
    "Https": {
      "Url": "https://0.0.0.0:9443",
      "Certificate": { "Path": "/certs/server-fullchain.pem", "KeyPath": "/certs/server.key" }
    }
  }
}
```

Use a full-chain PEM (leaf + intermediates) so enterprise-CA chains validate on clients, or a PFX via `Certificate:Path` + `Certificate:Password`. In containers, set `ASPNETCORE_HTTPS_PORTS=9443`. For clusters, point `Cluster:TrustedCaBundlePath` at a CA bundle to trust private CAs on inter-node HTTPS calls.

### Credentials (for S3 API)

There are **no default credentials**. You must configure `Auth:RootAccessKey` and `Auth:RootSecretKey` via environment variables or `appsettings.json` before use.

> **Warning:** Never deploy with weak or example credentials. Generate strong, unique values for all tokens and keys.

## Architecture

```
src/
├── DotBucket.Server/           # .NET backend
│   ├── Auth/                   # SigV4 authentication
│   ├── Configuration/          # Options classes
│   ├── Endpoints/
│   │   ├── Admin/              # Admin REST API (JSON)
│   │   └── S3/                 # S3-compatible API (XML)
│   ├── Middleware/              # S3 auth middleware
│   ├── Models/                 # Domain models
│   └── Storage/                # Storage engine interface + implementations
└── DotBucket.UI/               # React frontend
    ├── src/
    │   ├── components/ui/      # Shadcn UI components
    │   ├── lib/                # API client, utilities
    │   ├── pages/              # Dashboard
    │   └── types/              # TypeScript types
    └── public/                 # Static assets
```

### Storage Architecture

DotBucket is built around the `IStorageEngine` interface, allowing it to seamlessly switch between local and distributed storage modes.

- **Local Storage (`LocalFileSystemStorageEngine`)**:
  - **Metadata**: Uses **SQLite** (configured with WAL mode) for performant, concurrent metadata tracking.
  - **Data Integrity**: Implements atomic writes by first writing to `.tmp` files and then moving them to the final destination.
  - **Concurrency**: Manages per-object write locking using a `ConcurrentDictionary` of `SemaphoreSlim`.
- **Distributed Storage (`DistributedStorageEngine`)**:
  - **Bucket Management**: Uses a **Leader node** to coordinate and replicate bucket metadata changes across all peers.
  - **Object Placement**: Utilizes **Rendezvous Hashing** (Highest Random Weight) via `RendezvousHashRing` for stable, decentralized object-to-node mapping.
  - **Consistency Model**: Operations are proxied to a "Primary Node" (determined by the hash ring), which handles the local write and manages replication to peer nodes. Read requests are served locally if the node is an owner, or proxied to the preference list.

### Storage Layout on Disk

```
{RootPath}/
├── metadata.db                 # SQLite database for all metadata
├── my-bucket/
│   ├── photo.jpg               # Object data (latest version or unversioned)
│   ├── photo.jpg.v1            # Versioned object (if versioning enabled)
│   └── docs/
│       └── readme.txt
└── another-bucket/
```

## Roadmap
[07_Change_Log.md](../../Documents/LIFEOS_Tech_Spec_AI_Architecture_Costs_MobileFirst/07_Change_Log.md)
Planned / future work:

- Noncurrent-version expiration and `AbortIncompleteMultipartUpload` lifecycle actions.
- Cluster-wide lifecycle expiration (currently single-node).

## License

[MIT](LICENSE)
