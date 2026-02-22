# DotBucket

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

**DotBucket** is a lightweight, high-performance, S3-compatible object storage server built with .NET 10. A true restriction-free alternative to MinIO, released under the **MIT License** -- safe for enterprise, commercial, and closed-source integration without copyleft concerns.

## Features

- **S3 API Compatibility** -- Core S3 operations (PUT, GET, DELETE, LIST) with path-style access. Works with AWS SDK, Boto3, MinIO Client, and other standard S3 tools.
- **MIT Licensed** -- No AGPLv3 restrictions. No commercial license required. Ever.
- **High Performance** -- .NET 10, C# 14, Native AOT compilation. Zero external NuGet dependencies.
- **Built-in Admin Dashboard** -- React-based web UI to manage buckets and objects.
- **AWS SigV4 Authentication** -- Full AWS Signature Version 4 (AWS4-HMAC-SHA256) support.
- **Extensible Storage** -- Pluggable `IStorageEngine` interface. Ships with local filesystem backend; ready for distributed storage implementations.
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

### Running with Docker

```bash
docker build -t dotbucket .
docker run -d -p 9000:9000 -v dotbucket-data:/data dotbucket
```

The server starts on port `9000` with data persisted to `/data`. The admin dashboard is available at `http://localhost:9000`.

### Running Locally

```bash
# Clone the repository
git clone https://github.com/kidoz/dotbucket.git
cd dotbucket

# Run the backend
dotnet run --project src/DotBucket.Server

# In a separate terminal, run the frontend dev server
cd src/DotBucket.UI
npm install
npm run dev
```

- Backend: `http://localhost:5236`
- Frontend dev server: `http://localhost:5173`

### Building

```bash
# Build the backend
dotnet build

# Build the frontend
cd src/DotBucket.UI
npm run build
```

## S3 API Reference

All endpoints use **path-style** access (`/{bucket}/{key}`).

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
# Configure credentials (default: admin / admin123)
aws configure set aws_access_key_id admin
aws configure set aws_secret_access_key admin123

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

The admin API is served at `/admin` and requires no authentication. It powers the built-in dashboard.

| Method | Route | Description |
|---|---|---|
| `GET` | `/admin/buckets` | List all buckets |
| `POST` | `/admin/buckets` | Create a bucket (`{ "name": "..." }`) |
| `GET` | `/admin/buckets/{name}/objects` | List objects in a bucket |
| `DELETE` | `/admin/buckets/{name}/objects/{key}` | Delete an object |

A health check endpoint is also available at `GET /health`.

## Configuration

| Setting | Default | Docker Default | Description |
|---|---|---|---|
| `Storage:RootPath` | `storage` | `/data` | Root directory for bucket and object storage |

Configuration can be set via `appsettings.json`, environment variables (`Storage__RootPath`), or command-line arguments.

### Default Credentials

| Access Key | Secret Key |
|---|---|
| `admin` | `admin123` |

> **Warning:** These are hardcoded development credentials. Replace `StaticCredentialStore` with a proper credential store for production use.

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

### Storage Layout on Disk

```
{RootPath}/
├── my-bucket/
│   ├── photo.jpg               # Object data
│   ├── photo.jpg.meta.json     # Object metadata sidecar
│   └── docs/
│       ├── readme.txt
│       └── readme.txt.meta.json
└── another-bucket/
```

## License

[MIT](LICENSE) -- Copyright (c) 2026 Aleksandr Pavlov <ckidoz@gmail.com>