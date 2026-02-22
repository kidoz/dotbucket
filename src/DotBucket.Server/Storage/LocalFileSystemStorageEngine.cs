// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using DotBucket.Server.Configuration;
using DotBucket.Server.Models;
using DotBucket.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Storage;

public class LocalFileSystemStorageEngine : IStorageEngine
{
    private readonly string _rootPath;
    private readonly string _dbPath;
    private readonly byte[] _masterKey;
    private readonly NotificationDispatcher _dispatcher;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();

    public LocalFileSystemStorageEngine(
        IOptions<StorageOptions> options,
        NotificationDispatcher dispatcher
    )
    {
        _rootPath = options.Value.RootPath;
        if (string.IsNullOrWhiteSpace(_rootPath))
        {
            throw new ArgumentException("Storage root path must be configured.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Value.MasterKey))
        {
            throw new ArgumentException(
                "Encryption MasterKey must be configured.",
                nameof(options)
            );
        }
        _masterKey = Convert.FromBase64String(options.Value.MasterKey);

        if (!Directory.Exists(_rootPath))
            Directory.CreateDirectory(_rootPath);

        _dbPath = Path.Combine(_rootPath, "metadata.db");
        _dispatcher = dispatcher;
        InitializeDatabase();

        // Background task to prune unused locks
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(5));
                foreach (var (key, semaphore) in _writeLocks)
                {
                    if (semaphore.CurrentCount == 1)
                    {
                        _writeLocks.TryRemove(key, out _);
                    }
                }
            }
        });
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText =
            "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        pragmaCmd.ExecuteNonQuery();
        return connection;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default
    )
    {
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(cancellationToken);
        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText =
            "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await pragmaCmd.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private SemaphoreSlim GetWriteLock(string bucketName, string objectKey)
    {
        var lockKey = $"{bucketName}/{objectKey}";
        return _writeLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
    }

    private void InitializeDatabase()
    {
        using var connection = OpenConnection();

        using var migrationsTableCmd = connection.CreateCommand();
        migrationsTableCmd.CommandText =
            "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)";
        migrationsTableCmd.ExecuteNonQuery();

        RunMigration(
            connection,
            1,
            """
                CREATE TABLE IF NOT EXISTS buckets (
                    name TEXT PRIMARY KEY,
                    created_at TEXT NOT NULL,
                    versioning INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS objects (
                    bucket_name TEXT NOT NULL,
                    object_key TEXT NOT NULL,
                    version_id TEXT NOT NULL,
                    is_latest INTEGER NOT NULL DEFAULT 1,
                    is_delete_marker INTEGER NOT NULL DEFAULT 0,
                    size INTEGER NOT NULL,
                    content_type TEXT NOT NULL,
                    etag TEXT NOT NULL,
                    last_modified TEXT NOT NULL,
                    metadata_json TEXT,
                    PRIMARY KEY (bucket_name, object_key, version_id)
                );

                CREATE INDEX IF NOT EXISTS idx_objects_latest ON objects (bucket_name, object_key, is_latest);

                CREATE TABLE IF NOT EXISTS multipart_uploads (
                    upload_id TEXT PRIMARY KEY,
                    bucket_name TEXT NOT NULL,
                    object_key TEXT NOT NULL,
                    content_type TEXT NOT NULL,
                    metadata_json TEXT,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS parts (
                    upload_id TEXT NOT NULL,
                    part_number INTEGER NOT NULL,
                    etag TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    PRIMARY KEY (upload_id, part_number)
                );

                CREATE TABLE IF NOT EXISTS notifications (
                    bucket_name TEXT PRIMARY KEY,
                    config_json TEXT NOT NULL
                );
            """
        );

        RunMigration(
            connection,
            2,
            "ALTER TABLE buckets ADD COLUMN versioning INTEGER NOT NULL DEFAULT 0",
            ignoreErrors: true
        );

        RunMigration(
            connection,
            3,
            """
                CREATE TABLE IF NOT EXISTS iam_users (
                    user_name TEXT PRIMARY KEY,
                    status TEXT NOT NULL DEFAULT 'enabled',
                    created_at TEXT NOT NULL,
                    updated_at TEXT
                );
                CREATE TABLE IF NOT EXISTS iam_access_keys (
                    access_key TEXT PRIMARY KEY,
                    secret_key TEXT NOT NULL,
                    user_name TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'active',
                    created_at TEXT NOT NULL,
                    FOREIGN KEY (user_name) REFERENCES iam_users(user_name) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_iam_access_keys_user ON iam_access_keys(user_name);
                CREATE TABLE IF NOT EXISTS iam_groups (
                    group_name TEXT PRIMARY KEY,
                    status TEXT NOT NULL DEFAULT 'enabled',
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS iam_group_members (
                    group_name TEXT NOT NULL,
                    user_name TEXT NOT NULL,
                    PRIMARY KEY (group_name, user_name),
                    FOREIGN KEY (group_name) REFERENCES iam_groups(group_name) ON DELETE CASCADE,
                    FOREIGN KEY (user_name) REFERENCES iam_users(user_name) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS iam_policies (
                    policy_name TEXT PRIMARY KEY,
                    policy_json TEXT NOT NULL,
                    is_builtin INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT
                );
                CREATE TABLE IF NOT EXISTS iam_policy_attachments (
                    policy_name TEXT NOT NULL,
                    principal_type TEXT NOT NULL,
                    principal_name TEXT NOT NULL,
                    PRIMARY KEY (policy_name, principal_type, principal_name),
                    FOREIGN KEY (policy_name) REFERENCES iam_policies(policy_name) ON DELETE CASCADE
                );
            """
        );

        RunMigration(
            connection,
            4,
            """
                ALTER TABLE objects ADD COLUMN encryption TEXT;
                ALTER TABLE multipart_uploads ADD COLUMN encryption TEXT;
            """,
            ignoreErrors: true
        );

        // Migration 5: Object Locking support
        RunMigration(
            connection,
            5,
            """
                ALTER TABLE buckets ADD COLUMN object_lock_enabled INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE buckets ADD COLUMN object_lock_config_json TEXT;
                ALTER TABLE objects ADD COLUMN retention_mode TEXT;
                ALTER TABLE objects ADD COLUMN retain_until_date TEXT;
                ALTER TABLE objects ADD COLUMN legal_hold INTEGER NOT NULL DEFAULT 0;
            """,
            ignoreErrors: true
        );

        CleanupOrphanedTempFiles();
    }

    private static void RunMigration(
        SqliteConnection connection,
        int version,
        string sql,
        bool ignoreErrors = false
    )
    {
        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT 1 FROM schema_migrations WHERE version = $v";
        checkCmd.Parameters.AddWithValue("$v", version);
        if (checkCmd.ExecuteScalar() != null)
            return;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch when (ignoreErrors) { }

        using var recordCmd = connection.CreateCommand();
        recordCmd.CommandText =
            "INSERT OR IGNORE INTO schema_migrations (version, applied_at) VALUES ($v, $at)";
        recordCmd.Parameters.AddWithValue("$v", version);
        recordCmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        recordCmd.ExecuteNonQuery();
    }

    private void CleanupOrphanedTempFiles()
    {
        try
        {
            foreach (
                var tmpFile in Directory.EnumerateFiles(
                    _rootPath,
                    "*.tmp",
                    SearchOption.AllDirectories
                )
            )
            {
                File.Delete(tmpFile);
            }
        }
        catch { }
    }

    private static string EscapeLikePattern(string prefix)
    {
        return prefix.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
    }

    public async Task<Bucket> CreateBucketAsync(
        string bucketName,
        bool objectLock = false,
        CancellationToken cancellationToken = default
    )
    {
        var bucketPath = GetBucketPath(bucketName);
        if (Directory.Exists(bucketPath))
            throw new InvalidOperationException($"Bucket '{bucketName}' already exists.");

        Directory.CreateDirectory(bucketPath);
        var bucket = new Bucket
        {
            Name = bucketName,
            CreatedAt = DateTime.UtcNow,
            Versioning = objectLock ? VersioningStatus.Enabled : VersioningStatus.Off,
            ObjectLock = new ObjectLockConfig { Enabled = objectLock },
        };

        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO buckets (name, created_at, versioning, object_lock_enabled) VALUES ($name, $created_at, $versioning, $lock)";
        command.Parameters.AddWithValue("$name", bucket.Name);
        command.Parameters.AddWithValue("$created_at", bucket.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$versioning", (int)bucket.Versioning);
        command.Parameters.AddWithValue("$lock", objectLock ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return bucket;
    }

    public async Task<IEnumerable<Bucket>> ListBucketsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var buckets = new List<Bucket>();
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name, created_at, versioning, object_lock_enabled, object_lock_config_json FROM buckets";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var lockEnabled = reader.GetInt32(3) == 1;
            var lockJson = reader.IsDBNull(4) ? null : reader.GetString(4);
            var lockConfig = string.IsNullOrEmpty(lockJson)
                ? new ObjectLockConfig { Enabled = lockEnabled }
                : JsonSerializer.Deserialize(
                    lockJson,
                    StorageObjectJsonContext.Default.ObjectLockConfig
                )!;

            buckets.Add(
                new Bucket
                {
                    Name = reader.GetString(0),
                    CreatedAt = DateTime.Parse(reader.GetString(1)),
                    Versioning = (VersioningStatus)reader.GetInt32(2),
                    ObjectLock = lockConfig,
                }
            );
        }
        return buckets;
    }

    public async Task<Bucket?> GetBucketAsync(
        string bucketName,
        CancellationToken cancellationToken = default
    )
    {
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name, created_at, versioning, object_lock_enabled, object_lock_config_json FROM buckets WHERE name = $name";
        command.Parameters.AddWithValue("$name", bucketName);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var lockEnabled = reader.GetInt32(3) == 1;
            var lockJson = reader.IsDBNull(4) ? null : reader.GetString(4);
            var lockConfig = string.IsNullOrEmpty(lockJson)
                ? new ObjectLockConfig { Enabled = lockEnabled }
                : JsonSerializer.Deserialize(
                    lockJson,
                    StorageObjectJsonContext.Default.ObjectLockConfig
                )!;

            return new Bucket
            {
                Name = reader.GetString(0),
                CreatedAt = DateTime.Parse(reader.GetString(1)),
                Versioning = (VersioningStatus)reader.GetInt32(2),
                ObjectLock = lockConfig,
            };
        }
        return null;
    }

    public async Task<bool> BucketExistsAsync(
        string bucketName,
        CancellationToken cancellationToken = default
    )
    {
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM buckets WHERE name = $name";
        command.Parameters.AddWithValue("$name", bucketName);
        return await command.ExecuteScalarAsync(cancellationToken) != null;
    }

    public async Task SetVersioningAsync(
        string bucketName,
        VersioningStatus status,
        CancellationToken cancellationToken = default
    )
    {
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE buckets SET versioning = $status WHERE name = $name";
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$name", bucketName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetObjectLockConfigAsync(
        string bucketName,
        ObjectLockConfig config,
        CancellationToken cancellationToken = default
    )
    {
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE buckets SET object_lock_enabled = $enabled, object_lock_config_json = $json WHERE name = $name";
        command.Parameters.AddWithValue("$enabled", config.Enabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$json",
            JsonSerializer.Serialize(config, StorageObjectJsonContext.Default.ObjectLockConfig)
        );
        command.Parameters.AddWithValue("$name", bucketName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetObjectRetentionAsync(
        string bucketName,
        string objectKey,
        string? versionId,
        string mode,
        DateTime retainUntil,
        CancellationToken cancellationToken = default
    )
    {
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        var vid = versionId ?? "null";
        command.CommandText =
            "UPDATE objects SET retention_mode = $mode, retain_until_date = $date WHERE bucket_name = $bucket AND object_key = $key AND version_id = $vid";
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$date", retainUntil.ToString("O"));
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", objectKey);
        command.Parameters.AddWithValue("$vid", vid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetObjectLegalHoldAsync(
        string bucketName,
        string objectKey,
        string? versionId,
        bool hold,
        CancellationToken cancellationToken = default
    )
    {
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        var vid = versionId ?? "null";
        command.CommandText =
            "UPDATE objects SET legal_hold = $hold WHERE bucket_name = $bucket AND object_key = $key AND version_id = $vid";
        command.Parameters.AddWithValue("$hold", hold ? 1 : 0);
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", objectKey);
        command.Parameters.AddWithValue("$vid", vid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetNotificationsAsync(
        string bucketName,
        List<NotificationConfiguration> notifications,
        CancellationToken cancellationToken = default
    )
    {
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT OR REPLACE INTO notifications (bucket_name, config_json) VALUES ($bucket, $json)";
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue(
            "$json",
            JsonSerializer.Serialize(
                notifications,
                StorageObjectJsonContext.Default.ListNotificationConfiguration
            )
        );
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<NotificationConfiguration>> GetNotificationsAsync(
        string bucketName,
        CancellationToken cancellationToken = default
    )
    {
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT config_json FROM notifications WHERE bucket_name = $bucket";
        command.Parameters.AddWithValue("$bucket", bucketName);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrEmpty(json))
            return new List<NotificationConfiguration>();
        return JsonSerializer.Deserialize(
                json,
                StorageObjectJsonContext.Default.ListNotificationConfiguration
            ) ?? new List<NotificationConfiguration>();
    }

    public async Task<StorageObject> PutObjectAsync(
        string bucketName,
        string objectKey,
        Stream content,
        string contentType,
        Dictionary<string, string>? metadata = null,
        string? encryption = null,
        CancellationToken cancellationToken = default
    )
    {
        var writeLock = GetWriteLock(bucketName, objectKey);
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            var bucket =
                await GetBucketAsync(bucketName, cancellationToken)
                ?? throw new InvalidOperationException($"Bucket '{bucketName}' does not exist.");

            var versionId = "null";
            if (bucket.Versioning == VersioningStatus.Enabled)
            {
                versionId = Guid.NewGuid().ToString("N");
            }

            var objectPath = GetObjectPath(bucketName, objectKey, versionId);
            var objectDir = Path.GetDirectoryName(objectPath);
            if (objectDir != null && !Directory.Exists(objectDir))
                Directory.CreateDirectory(objectDir);

            var effectiveContent = content;
            if (encryption == "AES256")
            {
                effectiveContent = CreateEncryptionStream(content);
            }

            var (size, eTag) = await WriteAtomicAsync(
                objectPath,
                effectiveContent,
                cancellationToken
            );

            var lockStatus = new ObjectLockStatus();
            if (
                bucket.ObjectLock.Enabled
                && bucket.ObjectLock.DefaultRetentionMode != null
                && bucket.ObjectLock.DefaultRetentionDays > 0
            )
            {
                lockStatus = new ObjectLockStatus
                {
                    RetentionMode = bucket.ObjectLock.DefaultRetentionMode,
                    RetainUntilDate = DateTime.UtcNow.AddDays(
                        bucket.ObjectLock.DefaultRetentionDays.Value
                    ),
                };
            }

            var storageObj = new StorageObject
            {
                BucketName = bucketName,
                ObjectKey = objectKey,
                VersionId = versionId,
                Size = size,
                ContentType = contentType,
                ETag = eTag,
                LastModified = DateTime.UtcNow,
                Metadata = metadata ?? new(),
                Encryption = encryption,
                LockStatus = lockStatus,
            };

            await SaveMetadataAsync(storageObj, cancellationToken);
            await NotifyAsync(bucketName, "s3:ObjectCreated:Put", storageObj, cancellationToken);
            return storageObj;
        }
        finally
        {
            writeLock.Release();
        }
    }

    private Stream CreateEncryptionStream(Stream input)
    {
        var aes = Aes.Create();
        aes.Key = _masterKey;
        aes.GenerateIV();
        var ms = new MemoryStream();
        ms.Write(aes.IV, 0, aes.IV.Length);
        using (var encryptor = aes.CreateEncryptor())
        using (
            var cryptoStream = new CryptoStream(
                ms,
                encryptor,
                CryptoStreamMode.Write,
                leaveOpen: true
            )
        )
        {
            input.CopyTo(cryptoStream);
        }
        ms.Position = 0;
        return ms;
    }

    private Stream CreateDecryptionStream(Stream input)
    {
        var aes = Aes.Create();
        aes.Key = _masterKey;
        var iv = new byte[16];
        int read = input.Read(iv, 0, 16);
        if (read < 16)
            throw new InvalidOperationException("Invalid encrypted file.");
        aes.IV = iv;
        return new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read);
    }

    private static async Task<(long Size, string ETag)> WriteAtomicAsync(
        string finalPath,
        Stream content,
        CancellationToken cancellationToken
    )
    {
        var dir = Path.GetDirectoryName(finalPath) ?? ".";
        var fileName = Path.GetFileName(finalPath);
        var tmpPath = Path.Combine(dir, $".{fileName}.{Guid.NewGuid():N}.tmp");
        long size = 0;
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        try
        {
            await using (
                var fileStream = new FileStream(
                    tmpPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    useAsync: true
                )
            )
            {
                var buffer = new byte[8192];
                int read;
                while (
                    (
                        read = await content.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            cancellationToken
                        )
                    ) > 0
                )
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    md5.AppendData(buffer.AsSpan(0, read));
                    size += read;
                }
            }
            File.Move(tmpPath, finalPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);
            throw;
        }
        var hashBytes = md5.GetHashAndReset();
        var eTag = $"\"{Convert.ToHexString(hashBytes).ToLowerInvariant()}\"";
        return (size, eTag);
    }

    private async Task NotifyAsync(
        string bucketName,
        string eventName,
        StorageObject obj,
        CancellationToken cancellationToken
    )
    {
        var configs = await GetNotificationsAsync(bucketName, cancellationToken);
        if (configs.Count == 0)
            return;
        var s3Event = new S3Event
        {
            EventName = eventName,
            BucketName = bucketName,
            ObjectKey = obj.ObjectKey,
            Size = obj.Size,
            ETag = obj.ETag,
            VersionId = obj.VersionId,
        };
        foreach (var config in configs)
        {
            if (
                config.Events.Contains(eventName)
                || config.Events.Contains("s3:ObjectCreated:*")
                    && eventName.StartsWith("s3:ObjectCreated")
                || config.Events.Contains("s3:ObjectRemoved:*")
                    && eventName.StartsWith("s3:ObjectRemoved")
            )
            {
                if (
                    string.IsNullOrEmpty(config.FilterPrefix)
                    || obj.ObjectKey.StartsWith(config.FilterPrefix)
                )
                {
                    _ = _dispatcher.DispatchAsync(
                        config.WebhookUrl,
                        s3Event,
                        CancellationToken.None
                    );
                }
            }
        }
    }

    private async Task SaveMetadataAsync(StorageObject obj, CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        using var updateCmd = connection.CreateCommand();
        updateCmd.Transaction = transaction;
        updateCmd.CommandText =
            "UPDATE objects SET is_latest = 0 WHERE bucket_name = $bucket AND object_key = $key";
        updateCmd.Parameters.AddWithValue("$bucket", obj.BucketName);
        updateCmd.Parameters.AddWithValue("$key", obj.ObjectKey);
        await updateCmd.ExecuteNonQueryAsync(cancellationToken);

        if (obj.VersionId == "null")
        {
            using var delCmd = connection.CreateCommand();
            delCmd.Transaction = transaction;
            delCmd.CommandText =
                "DELETE FROM objects WHERE bucket_name = $bucket AND object_key = $key AND version_id = 'null'";
            delCmd.Parameters.AddWithValue("$bucket", obj.BucketName);
            delCmd.Parameters.AddWithValue("$key", obj.ObjectKey);
            await delCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        using var insertCmd = connection.CreateCommand();
        insertCmd.Transaction = transaction;
        insertCmd.CommandText = """
                INSERT INTO objects (bucket_name, object_key, version_id, is_latest, is_delete_marker, size, content_type, etag, last_modified, metadata_json, encryption, retention_mode, retain_until_date, legal_hold)
                VALUES ($bucket, $key, $vid, $latest, $del, $size, $type, $etag, $mod, $meta, $enc, $rmode, $rdate, $hold)
            """;
        insertCmd.Parameters.AddWithValue("$bucket", obj.BucketName);
        insertCmd.Parameters.AddWithValue("$key", obj.ObjectKey);
        insertCmd.Parameters.AddWithValue("$vid", obj.VersionId);
        insertCmd.Parameters.AddWithValue("$latest", obj.IsLatest ? 1 : 0);
        insertCmd.Parameters.AddWithValue("$del", obj.IsDeleteMarker ? 1 : 0);
        insertCmd.Parameters.AddWithValue("$size", obj.Size);
        insertCmd.Parameters.AddWithValue("$type", obj.ContentType);
        insertCmd.Parameters.AddWithValue("$etag", obj.ETag);
        insertCmd.Parameters.AddWithValue("$mod", obj.LastModified.ToString("O"));
        insertCmd.Parameters.AddWithValue(
            "$meta",
            JsonSerializer.Serialize(
                obj.Metadata,
                StorageObjectJsonContext.Default.DictionaryStringString
            )
        );
        insertCmd.Parameters.AddWithValue("$enc", (object?)obj.Encryption ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue(
            "$rmode",
            (object?)obj.LockStatus.RetentionMode ?? DBNull.Value
        );
        insertCmd.Parameters.AddWithValue(
            "$rdate",
            (object?)obj.LockStatus.RetainUntilDate?.ToString("O") ?? DBNull.Value
        );
        insertCmd.Parameters.AddWithValue("$hold", obj.LockStatus.LegalHold ? 1 : 0);

        await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<StorageObject?> HeadObjectAsync(
        string bucketName,
        string objectKey,
        string? versionId = null,
        CancellationToken cancellationToken = default
    )
    {
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        if (versionId == null)
            command.CommandText =
                "SELECT * FROM objects WHERE bucket_name = $bucket AND object_key = $key AND is_latest = 1 AND is_delete_marker = 0";
        else
        {
            command.CommandText =
                "SELECT * FROM objects WHERE bucket_name = $bucket AND object_key = $key AND version_id = $vid AND is_delete_marker = 0";
            command.Parameters.AddWithValue("$vid", versionId);
        }
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", objectKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return MapReaderToStorageObject(reader);
    }

    public async Task<(StorageObject Metadata, Stream Content)?> GetObjectAsync(
        string bucketName,
        string objectKey,
        string? versionId = null,
        CancellationToken cancellationToken = default
    )
    {
        var metadata = await HeadObjectAsync(bucketName, objectKey, versionId, cancellationToken);
        if (metadata == null)
            return null;
        var objectPath = GetObjectPath(bucketName, objectKey, metadata.VersionId);
        if (!File.Exists(objectPath))
            return null;
        var fileStream = new FileStream(
            objectPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            useAsync: true
        );
        Stream finalStream = fileStream;
        if (metadata.Encryption == "AES256")
            finalStream = CreateDecryptionStream(fileStream);
        return (metadata, finalStream);
    }

    public async Task<bool> DeleteObjectAsync(
        string bucketName,
        string objectKey,
        string? versionId = null,
        CancellationToken cancellationToken = default
    )
    {
        var bucket =
            await GetBucketAsync(bucketName, cancellationToken)
            ?? throw new InvalidOperationException($"Bucket '{bucketName}' does not exist.");
        StorageObject? targetObj = null;

        // Check object lock before deletion
        var currentMeta = await HeadObjectAsync(
            bucketName,
            objectKey,
            versionId,
            cancellationToken
        );
        if (currentMeta != null)
        {
            if (currentMeta.LockStatus.LegalHold)
                throw new InvalidOperationException("ObjectUnderLegalHold");
            if (
                currentMeta.LockStatus.RetentionMode != null
                && currentMeta.LockStatus.RetainUntilDate > DateTime.UtcNow
            )
                throw new InvalidOperationException("ObjectUnderRetention");
        }

        if (versionId != null)
        {
            using var connection = await OpenConnectionAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            using var findCmd = connection.CreateCommand();
            findCmd.Transaction = transaction;
            findCmd.CommandText =
                "SELECT * FROM objects WHERE bucket_name = $bucket AND object_key = $key AND version_id = $vid";
            findCmd.Parameters.AddWithValue("$bucket", bucketName);
            findCmd.Parameters.AddWithValue("$key", objectKey);
            findCmd.Parameters.AddWithValue("$vid", versionId);
            using var r = await findCmd.ExecuteReaderAsync(cancellationToken);
            if (await r.ReadAsync(cancellationToken))
                targetObj = MapReaderToStorageObject(r);
            await r.CloseAsync();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "DELETE FROM objects WHERE bucket_name = $bucket AND object_key = $key AND version_id = $vid";
            command.Parameters.AddWithValue("$bucket", bucketName);
            command.Parameters.AddWithValue("$key", objectKey);
            command.Parameters.AddWithValue("$vid", versionId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            using var latestCmd = connection.CreateCommand();
            latestCmd.Transaction = transaction;
            latestCmd.CommandText =
                "UPDATE objects SET is_latest = 1 WHERE rowid = (SELECT rowid FROM objects WHERE bucket_name = $bucket AND object_key = $key ORDER BY last_modified DESC LIMIT 1)";
            latestCmd.Parameters.AddWithValue("$bucket", bucketName);
            latestCmd.Parameters.AddWithValue("$key", objectKey);
            await latestCmd.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var objectPath = GetObjectPath(bucketName, objectKey, versionId);
            if (File.Exists(objectPath))
                File.Delete(objectPath);
        }
        else if (bucket.Versioning == VersioningStatus.Enabled)
        {
            var deleteMarker = new StorageObject
            {
                BucketName = bucketName,
                ObjectKey = objectKey,
                VersionId = Guid.NewGuid().ToString("N"),
                IsDeleteMarker = true,
                Size = 0,
                ContentType = "",
                ETag = "",
                LastModified = DateTime.UtcNow,
            };
            await SaveMetadataAsync(deleteMarker, cancellationToken);
            targetObj = deleteMarker;
        }
        else
        {
            using var connection = await OpenConnectionAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            using var findCmd = connection.CreateCommand();
            findCmd.Transaction = transaction;
            findCmd.CommandText =
                "SELECT * FROM objects WHERE bucket_name = $bucket AND object_key = $key AND is_latest = 1";
            findCmd.Parameters.AddWithValue("$bucket", bucketName);
            findCmd.Parameters.AddWithValue("$key", objectKey);
            using var r = await findCmd.ExecuteReaderAsync(cancellationToken);
            if (await r.ReadAsync(cancellationToken))
                targetObj = MapReaderToStorageObject(r);
            await r.CloseAsync();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "DELETE FROM objects WHERE bucket_name = $bucket AND object_key = $key AND version_id = 'null'";
            command.Parameters.AddWithValue("$bucket", bucketName);
            command.Parameters.AddWithValue("$key", objectKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var objectPath = GetObjectPath(bucketName, objectKey, "null");
            if (File.Exists(objectPath))
                File.Delete(objectPath);
        }
        if (targetObj != null)
            await NotifyAsync(
                bucketName,
                targetObj.IsDeleteMarker
                    ? "s3:ObjectRemoved:DeleteMarkerCreated"
                    : "s3:ObjectRemoved:Delete",
                targetObj,
                cancellationToken
            );
        return true;
    }

    public async Task<IEnumerable<StorageObject>> ListObjectsAsync(
        string bucketName,
        string? prefix = null,
        bool versions = false,
        CancellationToken cancellationToken = default
    )
    {
        var objects = new List<StorageObject>();
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        var query = "SELECT * FROM objects WHERE bucket_name = $bucket";
        if (!versions)
            query += " AND is_latest = 1 AND is_delete_marker = 0";
        if (!string.IsNullOrEmpty(prefix))
            query += " AND object_key LIKE $prefix ESCAPE '!'";
        query += " ORDER BY object_key ASC, last_modified DESC";
        command.CommandText = query;
        command.Parameters.AddWithValue("$bucket", bucketName);
        if (!string.IsNullOrEmpty(prefix))
            command.Parameters.AddWithValue("$prefix", EscapeLikePattern(prefix) + "%");
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            objects.Add(MapReaderToStorageObject(reader));
        return objects;
    }

    public async Task<(
        IEnumerable<StorageObject> Objects,
        string? NextContinuationToken,
        bool IsTruncated
    )> ListObjectsPagedAsync(
        string bucketName,
        string? prefix = null,
        string? continuationToken = null,
        string? startAfter = null,
        int maxKeys = 1000,
        CancellationToken cancellationToken = default
    )
    {
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        string? cursorKey = startAfter;
        if (continuationToken != null)
        {
            try
            {
                cursorKey = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(continuationToken)
                );
            }
            catch
            {
                cursorKey = null;
            }
        }
        var query =
            "SELECT * FROM objects WHERE bucket_name = $bucket AND is_latest = 1 AND is_delete_marker = 0";
        if (!string.IsNullOrEmpty(prefix))
            query += " AND object_key LIKE $prefix ESCAPE '!'";
        if (!string.IsNullOrEmpty(cursorKey))
            query += " AND object_key > $cursor";
        query += " ORDER BY object_key ASC LIMIT $limit";
        command.CommandText = query;
        command.Parameters.AddWithValue("$bucket", bucketName);
        if (!string.IsNullOrEmpty(prefix))
            command.Parameters.AddWithValue("$prefix", EscapeLikePattern(prefix) + "%");
        if (!string.IsNullOrEmpty(cursorKey))
            command.Parameters.AddWithValue("$cursor", cursorKey);
        command.Parameters.AddWithValue("$limit", maxKeys + 1);
        var objects = new List<StorageObject>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            objects.Add(MapReaderToStorageObject(reader));
        var isTruncated = objects.Count > maxKeys;
        if (isTruncated)
            objects.RemoveAt(objects.Count - 1);
        string? nextToken = null;
        if (isTruncated && objects.Count > 0)
            nextToken = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(objects[^1].ObjectKey)
            );
        return (objects, nextToken, isTruncated);
    }

    public async Task<StorageObject> CopyObjectAsync(
        string srcBucket,
        string srcKey,
        string? srcVersionId,
        string destBucket,
        string destKey,
        Dictionary<string, string>? metadataOverride = null,
        CancellationToken cancellationToken = default
    )
    {
        var srcResult = await GetObjectAsync(srcBucket, srcKey, srcVersionId, cancellationToken);
        if (srcResult == null)
            throw new InvalidOperationException($"Source object '{srcBucket}/{srcKey}' not found.");
        var (srcMeta, srcContent) = srcResult.Value;
        await using (srcContent)
        {
            var metadata = metadataOverride ?? srcMeta.Metadata;
            return await PutObjectAsync(
                destBucket,
                destKey,
                srcContent,
                srcMeta.ContentType,
                metadata,
                encryption: srcMeta.Encryption,
                cancellationToken: cancellationToken
            );
        }
    }

    public async Task<
        List<(string Key, bool Success, string? ErrorCode, string? ErrorMessage)>
    > DeleteObjectsAsync(
        string bucketName,
        IEnumerable<(string Key, string? VersionId)> objects,
        bool quiet,
        CancellationToken cancellationToken = default
    )
    {
        var results =
            new List<(string Key, bool Success, string? ErrorCode, string? ErrorMessage)>();
        foreach (var (key, versionId) in objects)
        {
            try
            {
                await DeleteObjectAsync(bucketName, key, versionId, cancellationToken);
                results.Add((key, true, null, null));
            }
            catch (Exception ex)
            {
                results.Add(
                    (
                        key,
                        false,
                        ex.Message == "ObjectUnderRetention" ? "AccessDenied" : "InternalError",
                        ex.Message
                    )
                );
            }
        }
        return results;
    }

    public async Task DeleteBucketAsync(
        string bucketName,
        CancellationToken cancellationToken = default
    )
    {
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM objects WHERE bucket_name = $bucket";
        countCmd.Parameters.AddWithValue("$bucket", bucketName);
        var count = (long)(await countCmd.ExecuteScalarAsync(cancellationToken))!;
        if (count > 0)
            throw new InvalidOperationException("BucketNotEmpty");
        using var transaction = connection.BeginTransaction();
        using var delNotifCmd = connection.CreateCommand();
        delNotifCmd.Transaction = transaction;
        delNotifCmd.CommandText = "DELETE FROM notifications WHERE bucket_name = $bucket";
        delNotifCmd.Parameters.AddWithValue("$bucket", bucketName);
        await delNotifCmd.ExecuteNonQueryAsync(cancellationToken);
        using var delBucketCmd = connection.CreateCommand();
        delBucketCmd.Transaction = transaction;
        delBucketCmd.CommandText = "DELETE FROM buckets WHERE name = $bucket";
        delBucketCmd.Parameters.AddWithValue("$bucket", bucketName);
        await delBucketCmd.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var bucketPath = GetBucketPath(bucketName);
        if (Directory.Exists(bucketPath))
            Directory.Delete(bucketPath, true);
    }

    public async Task<IEnumerable<MultipartUploadInfo>> ListMultipartUploadsAsync(
        string bucketName,
        string? prefix = null,
        CancellationToken cancellationToken = default
    )
    {
        var uploads = new List<MultipartUploadInfo>();
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        var query =
            "SELECT upload_id, bucket_name, object_key, created_at FROM multipart_uploads WHERE bucket_name = $bucket";
        if (!string.IsNullOrEmpty(prefix))
            query += " AND object_key LIKE $prefix ESCAPE '!'";
        query += " ORDER BY object_key ASC";
        command.CommandText = query;
        command.Parameters.AddWithValue("$bucket", bucketName);
        if (!string.IsNullOrEmpty(prefix))
            command.Parameters.AddWithValue("$prefix", EscapeLikePattern(prefix) + "%");
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            uploads.Add(
                new MultipartUploadInfo
                {
                    UploadId = reader.GetString(0),
                    BucketName = reader.GetString(1),
                    ObjectKey = reader.GetString(2),
                    Initiated = DateTime.Parse(reader.GetString(3)),
                }
            );
        return uploads;
    }

    public async Task<IEnumerable<PartInfo>> ListPartsAsync(
        string bucketName,
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default
    )
    {
        var parts = new List<PartInfo>();
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT part_number, etag, size FROM parts WHERE upload_id = $id ORDER BY part_number ASC";
        command.Parameters.AddWithValue("$id", uploadId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            parts.Add(
                new PartInfo
                {
                    PartNumber = reader.GetInt32(0),
                    ETag = reader.GetString(1),
                    Size = reader.GetInt64(2),
                }
            );
        return parts;
    }

    private StorageObject MapReaderToStorageObject(SqliteDataReader reader)
    {
        var metaJson = reader.GetString(reader.GetOrdinal("metadata_json"));
        var metadata =
            JsonSerializer.Deserialize(
                metaJson,
                StorageObjectJsonContext.Default.DictionaryStringString
            ) ?? new();
        var retentionMode = reader.IsDBNull(reader.GetOrdinal("retention_mode"))
            ? null
            : reader.GetString(reader.GetOrdinal("retention_mode"));
        var retainUntilStr = reader.IsDBNull(reader.GetOrdinal("retain_until_date"))
            ? null
            : reader.GetString(reader.GetOrdinal("retain_until_date"));
        var retainUntil = retainUntilStr != null ? DateTime.Parse(retainUntilStr) : (DateTime?)null;
        var hold = reader.GetInt32(reader.GetOrdinal("legal_hold")) == 1;

        return new StorageObject
        {
            BucketName = reader.GetString(reader.GetOrdinal("bucket_name")),
            ObjectKey = reader.GetString(reader.GetOrdinal("object_key")),
            VersionId = reader.GetString(reader.GetOrdinal("version_id")),
            IsLatest = reader.GetInt32(reader.GetOrdinal("is_latest")) == 1,
            IsDeleteMarker = reader.GetInt32(reader.GetOrdinal("is_delete_marker")) == 1,
            Size = reader.GetInt64(reader.GetOrdinal("size")),
            ContentType = reader.GetString(reader.GetOrdinal("content_type")),
            ETag = reader.GetString(reader.GetOrdinal("etag")),
            LastModified = DateTime.Parse(reader.GetString(reader.GetOrdinal("last_modified"))),
            Metadata = metadata,
            Encryption = reader.IsDBNull(reader.GetOrdinal("encryption"))
                ? null
                : reader.GetString(reader.GetOrdinal("encryption")),
            LockStatus = new ObjectLockStatus
            {
                RetentionMode = retentionMode,
                RetainUntilDate = retainUntil,
                LegalHold = hold,
            },
        };
    }

    public async Task<string> InitiateMultipartUploadAsync(
        string bucketName,
        string objectKey,
        string contentType,
        Dictionary<string, string>? metadata = null,
        string? encryption = null,
        CancellationToken cancellationToken = default
    )
    {
        var uploadId = Guid.NewGuid().ToString("N");
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO multipart_uploads (upload_id, bucket_name, object_key, content_type, metadata_json, created_at, encryption) VALUES ($id, $bucket, $key, $type, $meta, $created, $enc)";
        command.Parameters.AddWithValue("$id", uploadId);
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", objectKey);
        command.Parameters.AddWithValue("$type", contentType);
        command.Parameters.AddWithValue(
            "$meta",
            JsonSerializer.Serialize(
                metadata ?? new(),
                StorageObjectJsonContext.Default.DictionaryStringString
            )
        );
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$enc", (object?)encryption ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        var partsDir = GetPartsPath(bucketName, uploadId);
        if (!Directory.Exists(partsDir))
            Directory.CreateDirectory(partsDir);
        return uploadId;
    }

    public async Task<string> UploadPartAsync(
        string bucketName,
        string objectKey,
        string uploadId,
        int partNumber,
        Stream content,
        CancellationToken cancellationToken = default
    )
    {
        var partPath = Path.Combine(GetPartsPath(bucketName, uploadId), partNumber.ToString());
        long size = 0;
        using (var sha256 = SHA256.Create())
        using (
            var fileStream = new FileStream(
                partPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true
            )
        )
        {
            var buffer = new byte[8192];
            int read;
            while (
                (read = await content.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0
            )
            {
                await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
                sha256.TransformBlock(buffer, 0, read, null, 0);
                size += read;
            }
            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var etag = $"\"{Convert.ToHexString(sha256.Hash!).ToLowerInvariant()}\"";
            using var connection = await OpenConnectionAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT OR REPLACE INTO parts (upload_id, part_number, etag, size) VALUES ($id, $num, $etag, $size)";
            command.Parameters.AddWithValue("$id", uploadId);
            command.Parameters.AddWithValue("$num", partNumber);
            command.Parameters.AddWithValue("$etag", etag);
            command.Parameters.AddWithValue("$size", size);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return etag;
        }
    }

    public async Task<StorageObject> CompleteMultipartUploadAsync(
        string bucketName,
        string objectKey,
        string uploadId,
        IEnumerable<(int PartNumber, string ETag)> parts,
        CancellationToken cancellationToken = default
    )
    {
        var writeLock = GetWriteLock(bucketName, objectKey);
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = await OpenConnectionAsync(cancellationToken);
            using var uploadCmd = connection.CreateCommand();
            uploadCmd.CommandText = "SELECT * FROM multipart_uploads WHERE upload_id = $id";
            uploadCmd.Parameters.AddWithValue("$id", uploadId);
            using var uploadReader = await uploadCmd.ExecuteReaderAsync(cancellationToken);
            if (!await uploadReader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Upload not found.");
            var contentType = uploadReader.GetString(uploadReader.GetOrdinal("content_type"));
            var metaJson = uploadReader.GetString(uploadReader.GetOrdinal("metadata_json"));
            var encryption = uploadReader.IsDBNull(uploadReader.GetOrdinal("encryption"))
                ? null
                : uploadReader.GetString(uploadReader.GetOrdinal("encryption"));
            var metadata =
                JsonSerializer.Deserialize(
                    metaJson,
                    StorageObjectJsonContext.Default.DictionaryStringString
                ) ?? new();
            await uploadReader.CloseAsync();
            var bucket =
                await GetBucketAsync(bucketName, cancellationToken)
                ?? throw new InvalidOperationException($"Bucket '{bucketName}' missing.");
            var versionId =
                bucket.Versioning == VersioningStatus.Enabled
                    ? Guid.NewGuid().ToString("N")
                    : "null";
            var finalPath = GetObjectPath(bucketName, objectKey, versionId);
            var finalDir = Path.GetDirectoryName(finalPath);
            if (finalDir != null && !Directory.Exists(finalDir))
                Directory.CreateDirectory(finalDir);
            var partsDir = GetPartsPath(bucketName, uploadId);
            long totalSize = 0;
            var partsList = parts.OrderBy(p => p.PartNumber).ToList();
            var tmpPath = finalPath + $".{Guid.NewGuid():N}.tmp";
            using var md5Concat = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            try
            {
                await using (
                    var finalFileStream = new FileStream(
                        tmpPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        4096,
                        useAsync: true
                    )
                )
                {
                    Stream effectiveOutput = finalFileStream;
                    if (encryption == "AES256")
                    {
                        var aes = Aes.Create();
                        aes.Key = _masterKey;
                        aes.GenerateIV();
                        await finalFileStream.WriteAsync(
                            aes.IV,
                            0,
                            aes.IV.Length,
                            cancellationToken
                        );
                        effectiveOutput = new CryptoStream(
                            finalFileStream,
                            aes.CreateEncryptor(),
                            CryptoStreamMode.Write
                        );
                    }
                    foreach (var part in partsList)
                    {
                        var partPath = Path.Combine(partsDir, part.PartNumber.ToString());
                        if (!File.Exists(partPath))
                            throw new InvalidOperationException($"Part {part.PartNumber} missing.");
                        using var partMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                        await using var partStream = new FileStream(
                            partPath,
                            FileMode.Open,
                            FileAccess.Read
                        );
                        var buffer = new byte[8192];
                        int read;
                        while (
                            (
                                read = await partStream.ReadAsync(
                                    buffer.AsMemory(0, buffer.Length),
                                    cancellationToken
                                )
                            ) > 0
                        )
                        {
                            await effectiveOutput.WriteAsync(
                                buffer.AsMemory(0, read),
                                cancellationToken
                            );
                            partMd5.AppendData(buffer.AsSpan(0, read));
                            totalSize += read;
                        }
                        md5Concat.AppendData(partMd5.GetHashAndReset());
                    }
                    if (effectiveOutput is CryptoStream cs)
                        await cs.FlushFinalBlockAsync(cancellationToken);
                }
                File.Move(tmpPath, finalPath, overwrite: true);
            }
            catch
            {
                if (File.Exists(tmpPath))
                    File.Delete(tmpPath);
                throw;
            }
            var concatHash = Convert.ToHexString(md5Concat.GetHashAndReset()).ToLowerInvariant();
            var eTag = $"\"{concatHash}-{partsList.Count}\"";

            var lockStatus = new ObjectLockStatus();
            if (
                bucket.ObjectLock.Enabled
                && bucket.ObjectLock.DefaultRetentionMode != null
                && bucket.ObjectLock.DefaultRetentionDays > 0
            )
            {
                lockStatus = new ObjectLockStatus
                {
                    RetentionMode = bucket.ObjectLock.DefaultRetentionMode,
                    RetainUntilDate = DateTime.UtcNow.AddDays(
                        bucket.ObjectLock.DefaultRetentionDays.Value
                    ),
                };
            }

            var storageObj = new StorageObject
            {
                BucketName = bucketName,
                ObjectKey = objectKey,
                VersionId = versionId,
                Size = totalSize,
                ContentType = contentType,
                ETag = eTag,
                LastModified = DateTime.UtcNow,
                Metadata = metadata,
                Encryption = encryption,
                LockStatus = lockStatus,
            };
            await SaveMetadataAsync(storageObj, cancellationToken);
            await NotifyAsync(
                bucketName,
                "s3:ObjectCreated:CompleteMultipartUpload",
                storageObj,
                cancellationToken
            );
            Directory.Delete(partsDir, true);
            using var cleanupCmd = connection.CreateCommand();
            cleanupCmd.CommandText =
                "DELETE FROM multipart_uploads WHERE upload_id = $id; DELETE FROM parts WHERE upload_id = $id;";
            cleanupCmd.Parameters.AddWithValue("$id", uploadId);
            await cleanupCmd.ExecuteNonQueryAsync(cancellationToken);
            return storageObj;
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task AbortMultipartUploadAsync(
        string bucketName,
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default
    )
    {
        var partsDir = GetPartsPath(bucketName, uploadId);
        if (Directory.Exists(partsDir))
            Directory.Delete(partsDir, true);
        using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM multipart_uploads WHERE upload_id = $id; DELETE FROM parts WHERE upload_id = $id;";
        command.Parameters.AddWithValue("$id", uploadId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string GetBucketPath(string bucketName)
    {
        var path = Path.GetFullPath(Path.Combine(_rootPath, bucketName));
        EnsureWithinRoot(path);
        return path;
    }

    private string GetObjectPath(string bucketName, string objectKey, string versionId)
    {
        var safeKey = objectKey.Replace('/', Path.DirectorySeparatorChar);
        if (versionId != "null")
            safeKey += "." + versionId;
        var path = Path.GetFullPath(Path.Combine(_rootPath, bucketName, safeKey));
        EnsureWithinRoot(path);
        return path;
    }

    private string GetPartsPath(string bucketName, string uploadId)
    {
        var path = Path.GetFullPath(Path.Combine(_rootPath, bucketName, ".uploads", uploadId));
        EnsureWithinRoot(path);
        return path;
    }

    private void EnsureWithinRoot(string path)
    {
        var root = Path.GetFullPath(_rootPath) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException("Path traversal detected.");
    }
}
