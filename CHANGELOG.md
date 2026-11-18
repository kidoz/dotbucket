# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project intends to follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **Status: experimental.** DotBucket has not cut a tagged release yet. The
> entries below cover everything currently on `main` and will ship in the first
> tagged version. Until then, features, APIs, and the on-disk format may change
> without notice.

## [Unreleased]

### Added

**S3 API**

- S3-compatible bucket and object operations: list buckets, create/head/delete
  bucket, list objects (V2), put/get/head/delete/copy object, and batch delete,
  with standard S3 XML responses and error codes.
- Multipart uploads (initiate, upload part, complete, abort, list parts) with
  tunable buffering and S3 part validation.
- Object versioning with delete markers and version-specific access.
- Object Lock retention and legal hold, enforced on writes and deletes.
- Lifecycle expiration policies backed by a background expiration worker.
- Server-side encryption (SSE-S3, `AES256`) using authenticated AES-GCM, with
  streaming encryption and crash-safe writes.
- Range requests, conditional GET/HEAD, and `x-amz-meta-*` custom metadata.
- Path-style and virtual-hosted-style addressing, configurable advertised
  region, and session-token handling.
- Bucket notification configuration with webhook dispatch.

**Authentication and access control**

- AWS Signature Version 4 authentication (AWS4-HMAC-SHA256), including
  streaming/chunked uploads with per-chunk signature validation.
- IAM users, access keys, groups, and policies with an S3 action resolver and
  deny-by-default authorization.
- Admin API bearer-token authentication and a configurable root credential pair.
- Per-access-key rate limiting for S3 requests and per-client-IP rate limiting
  for admin requests.

**Admin dashboard and API**

- React admin dashboard for managing buckets and objects, with multi-file
  upload, prefix search, versioning controls, notification management, and
  pagination.
- Admin REST and IAM endpoints for bucket, object, user, key, group, and policy
  management.

**Storage and operations**

- Local filesystem storage engine backed by SQLite (WAL) metadata, atomic
  writes, and per-object write locks behind a pluggable `IStorageEngine`
  abstraction.
- Distributed storage engine with Rendezvous (HRW) hashing, leader-coordinated
  bucket metadata, and peer replication.
- Cluster mode with node health monitoring, internal replication endpoints, and
  custom-CA HTTPS trust for inter-node calls.
- Startup bucket provisioning, configurable storage base prefix, and
  configurable multipart buffer size.
- Disk-space guard that refuses object writes and degrades `/health` below a
  configurable free-space threshold.
- Background service that reaps abandoned multipart uploads.
- OpenTelemetry traces and metrics with OTLP export.

### Changed

- Migrated the backend and test projects to .NET 11 / C# 15, including
  ASP.NET Core, `Microsoft.Data.Sqlite`, OpenAPI, and test packages. Docker and
  CI images now use .NET 11.
- Critical configuration is validated at startup, and invalid settings fail
  closed instead of starting in a partially configured state.
- Streaming SigV4 chunk framing reads are buffered instead of reading the
  underlying stream byte by byte.
- Tests migrated from FluentAssertions to MIT-licensed AwesomeAssertions.
- NuGet and npm dependencies updated to their latest stable versions; the
  NuGet refresh includes OpenTelemetry 1.21, AWS SDK for .NET 4.1, xunit 4,
  NSubstitute 6.2, AwesomeAssertions 9.6, and coverlet 10.1.
- Codebase formatted with CSharpier 1.4, with formatting enforced in CI.
- README updated with the current feature set, configuration reference, HTTPS
  guidance, and roadmap.

### Fixed

- SigV4 canonical URI normalization no longer rejects valid client signatures
  when the request path contains double slashes.
- SigV4 signed-header values are canonicalized according to the AWS
  specification.
- Cluster leader state is now published with correct memory visibility across
  threads.
- Multipart part validation and Object Lock retention/legal-hold semantics are
  enforced consistently.

### Security

- Authorization fails closed for unmapped S3 actions and authorizes batch
  deletes per object rather than per request.
- Cluster tokens are compared in constant time.
- Streaming SigV4 uploads validate the signature of every chunk.
- Webhook notifications are limited to HTTPS and reject private/internal
  address targets to prevent server-side request forgery.
- Admin surfaces send a Content-Security-Policy and other hardening headers.
- Updated dependencies and pinned vulnerable transitive packages.

### Documentation

- Documented node-local IAM and cluster-state limitations in cluster mode.
- Documented the startup configuration surface, including storage, auth,
  cluster, lifecycle, and observability options.

### Build and CI

- Added a GitHub Actions CI workflow that builds, formats, and tests the backend
  and frontend, then builds the Docker image.
- Added `global.json` to use the Microsoft.Testing.Platform runner for
  `dotnet test`, as required by xunit v4.
- Added unit, integration, and end-to-end coverage for SigV4, IAM policy
  evaluation, rate limiting, disk-space handling, multipart reaping, and
  storage hardening. End-to-end tests exercise the production server binary.

[Unreleased]: https://github.com/kidoz/dotbucket/commits/main
