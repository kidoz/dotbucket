// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotBucket.Server.Configuration;
using DotBucket.Server.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Iam;

public class IamStore(IOptions<StorageOptions> options)
{
    private readonly string _dbPath = Path.Combine(options.Value.RootPath, "metadata.db");
    private readonly byte[] _masterKey = ParseMasterKey(options.Value.MasterKey);
    private const string EncryptedSecretPrefix = "enc:v1:";
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(ct);
        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText =
            "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await pragmaCmd.ExecuteNonQueryAsync(ct);
        return connection;
    }

    // --- Users ---

    public async Task<IamUser> CreateUserAsync(string userName, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO iam_users (user_name, status, created_at) VALUES ($name, 'enabled', $created)";
        cmd.Parameters.AddWithValue("$name", userName);
        cmd.Parameters.AddWithValue("$created", now.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
        return new IamUser
        {
            UserName = userName,
            Status = "enabled",
            CreatedAt = now,
        };
    }

    public async Task<IamUser?> GetUserAsync(string userName, CancellationToken ct = default)
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT user_name, status, created_at, updated_at FROM iam_users WHERE user_name = $name";
        cmd.Parameters.AddWithValue("$name", userName);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return MapUser(reader);
    }

    public async Task<List<IamUser>> ListUsersAsync(CancellationToken ct = default)
    {
        var users = new List<IamUser>();
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT user_name, status, created_at, updated_at FROM iam_users ORDER BY user_name";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            users.Add(MapUser(reader));
        return users;
    }

    public async Task SetUserStatusAsync(
        string userName,
        string status,
        CancellationToken ct = default
    )
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE iam_users SET status = $status, updated_at = $now WHERE user_name = $name";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$name", userName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteUserAsync(string userName, CancellationToken ct = default)
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM iam_users WHERE user_name = $name";
        cmd.Parameters.AddWithValue("$name", userName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // --- Access Keys ---

    public async Task<CreateAccessKeyResponse> CreateAccessKeyAsync(
        string userName,
        CancellationToken ct = default
    )
    {
        var accessKey = GenerateRandomString(20);
        var secretKey = GenerateRandomString(40);
        return await CreateAccessKeyWithCredentialsAsync(userName, accessKey, secretKey, ct);
    }

    public async Task<CreateAccessKeyResponse> CreateAccessKeyWithCredentialsAsync(
        string userName,
        string accessKey,
        string secretKey,
        CancellationToken ct = default
    )
    {
        var now = DateTime.UtcNow;
        var encryptedSecretKey = ProtectSecret(secretKey);
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                INSERT INTO iam_access_keys (access_key, secret_key, user_name, status, created_at)
                VALUES ($ak, $sk, $user, 'active', $created)
            """;
        cmd.Parameters.AddWithValue("$ak", accessKey);
        cmd.Parameters.AddWithValue("$sk", encryptedSecretKey);
        cmd.Parameters.AddWithValue("$user", userName);
        cmd.Parameters.AddWithValue("$created", now.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);

        return new CreateAccessKeyResponse
        {
            AccessKey = accessKey,
            SecretKey = secretKey,
            UserName = userName,
        };
    }

    public async Task<List<IamAccessKey>> ListAccessKeysAsync(
        string userName,
        CancellationToken ct = default
    )
    {
        var keys = new List<IamAccessKey>();
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT access_key, secret_key, user_name, status, created_at FROM iam_access_keys WHERE user_name = $user ORDER BY created_at";
        cmd.Parameters.AddWithValue("$user", userName);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = MapAccessKey(reader);
            keys.Add(
                new IamAccessKey
                {
                    AccessKey = key.AccessKey,
                    SecretKey = TryUnprotectSecret(key.SecretKey) ?? string.Empty,
                    UserName = key.UserName,
                    Status = key.Status,
                    CreatedAt = key.CreatedAt,
                }
            );
        }
        return keys;
    }

    public async Task SetAccessKeyStatusAsync(
        string accessKey,
        string status,
        CancellationToken ct = default
    )
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE iam_access_keys SET status = $status WHERE access_key = $ak";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$ak", accessKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAccessKeyAsync(string accessKey, CancellationToken ct = default)
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM iam_access_keys WHERE access_key = $ak";
        cmd.Parameters.AddWithValue("$ak", accessKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> LookupSecretKeyAsync(
        string accessKey,
        CancellationToken ct = default
    )
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                SELECT ak.secret_key
                FROM iam_access_keys ak
                JOIN iam_users u ON ak.user_name = u.user_name
                WHERE ak.access_key = $ak AND ak.status = 'active' AND u.status = 'enabled'
            """;
        cmd.Parameters.AddWithValue("$ak", accessKey);
        var storedSecret = await cmd.ExecuteScalarAsync(ct) as string;
        if (storedSecret == null)
        {
            return null;
        }

        return TryUnprotectSecret(storedSecret);
    }

    public async Task<string?> LookupUserNameByAccessKeyAsync(
        string accessKey,
        CancellationToken ct = default
    )
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                SELECT ak.user_name
                FROM iam_access_keys ak
                JOIN iam_users u ON ak.user_name = u.user_name
                WHERE ak.access_key = $ak AND ak.status = 'active' AND u.status = 'enabled'
            """;
        cmd.Parameters.AddWithValue("$ak", accessKey);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    // --- Groups ---

    public async Task<IamGroup> CreateGroupAsync(string groupName, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO iam_groups (group_name, status, created_at) VALUES ($name, 'enabled', $created)";
        cmd.Parameters.AddWithValue("$name", groupName);
        cmd.Parameters.AddWithValue("$created", now.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
        return new IamGroup
        {
            GroupName = groupName,
            Status = "enabled",
            CreatedAt = now,
        };
    }

    public async Task<IamGroup?> GetGroupAsync(string groupName, CancellationToken ct = default)
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT group_name, status, created_at FROM iam_groups WHERE group_name = $name";
        cmd.Parameters.AddWithValue("$name", groupName);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new IamGroup
        {
            GroupName = reader.GetString(0),
            Status = reader.GetString(1),
            CreatedAt = DateTime.Parse(reader.GetString(2)),
        };
    }

    public async Task<List<IamGroup>> ListGroupsAsync(CancellationToken ct = default)
    {
        var groups = new List<IamGroup>();
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT group_name, status, created_at FROM iam_groups ORDER BY group_name";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            groups.Add(
                new IamGroup
                {
                    GroupName = reader.GetString(0),
                    Status = reader.GetString(1),
                    CreatedAt = DateTime.Parse(reader.GetString(2)),
                }
            );
        return groups;
    }

    public async Task DeleteGroupAsync(string groupName, CancellationToken ct = default)
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM iam_groups WHERE group_name = $name";
        cmd.Parameters.AddWithValue("$name", groupName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddGroupMemberAsync(
        string groupName,
        string userName,
        CancellationToken ct = default
    )
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT OR IGNORE INTO iam_group_members (group_name, user_name) VALUES ($group, $user)";
        cmd.Parameters.AddWithValue("$group", groupName);
        cmd.Parameters.AddWithValue("$user", userName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveGroupMemberAsync(
        string groupName,
        string userName,
        CancellationToken ct = default
    )
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "DELETE FROM iam_group_members WHERE group_name = $group AND user_name = $user";
        cmd.Parameters.AddWithValue("$group", groupName);
        cmd.Parameters.AddWithValue("$user", userName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<string>> ListGroupMembersAsync(
        string groupName,
        CancellationToken ct = default
    )
    {
        var members = new List<string>();
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT user_name FROM iam_group_members WHERE group_name = $group ORDER BY user_name";
        cmd.Parameters.AddWithValue("$group", groupName);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            members.Add(reader.GetString(0));
        return members;
    }

    // --- Policies ---

    public async Task<IamPolicy> CreatePolicyAsync(
        string policyName,
        IamPolicyDocument document,
        bool isBuiltin = false,
        CancellationToken ct = default
    )
    {
        var now = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(
            document,
            StorageObjectJsonContext.Default.IamPolicyDocument
        );
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                INSERT INTO iam_policies (policy_name, policy_json, is_builtin, created_at)
                VALUES ($name, $json, $builtin, $created)
            """;
        cmd.Parameters.AddWithValue("$name", policyName);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$builtin", isBuiltin ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", now.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);

        return new IamPolicy
        {
            PolicyName = policyName,
            PolicyJson = json,
            IsBuiltin = isBuiltin,
            CreatedAt = now,
        };
    }

    public async Task<IamPolicy?> GetPolicyAsync(string policyName, CancellationToken ct = default)
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT policy_name, policy_json, is_builtin, created_at, updated_at FROM iam_policies WHERE policy_name = $name";
        cmd.Parameters.AddWithValue("$name", policyName);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return MapPolicy(reader);
    }

    public async Task<List<IamPolicy>> ListPoliciesAsync(CancellationToken ct = default)
    {
        var policies = new List<IamPolicy>();
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT policy_name, policy_json, is_builtin, created_at, updated_at FROM iam_policies ORDER BY policy_name";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            policies.Add(MapPolicy(reader));
        return policies;
    }

    public async Task DeletePolicyAsync(string policyName, CancellationToken ct = default)
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM iam_policies WHERE policy_name = $name";
        cmd.Parameters.AddWithValue("$name", policyName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> PolicyExistsAsync(string policyName, CancellationToken ct = default)
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM iam_policies WHERE policy_name = $name";
        cmd.Parameters.AddWithValue("$name", policyName);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    // --- Policy Attachments ---

    public async Task AttachPolicyAsync(
        string policyName,
        string principalType,
        string principalName,
        CancellationToken ct = default
    )
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                INSERT OR IGNORE INTO iam_policy_attachments (policy_name, principal_type, principal_name)
                VALUES ($policy, $type, $name)
            """;
        cmd.Parameters.AddWithValue("$policy", policyName);
        cmd.Parameters.AddWithValue("$type", principalType);
        cmd.Parameters.AddWithValue("$name", principalName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DetachPolicyAsync(
        string policyName,
        string principalType,
        string principalName,
        CancellationToken ct = default
    )
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "DELETE FROM iam_policy_attachments WHERE policy_name = $policy AND principal_type = $type AND principal_name = $name";
        cmd.Parameters.AddWithValue("$policy", policyName);
        cmd.Parameters.AddWithValue("$type", principalType);
        cmd.Parameters.AddWithValue("$name", principalName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<IamPolicyAttachment>> ListPolicyAttachmentsAsync(
        string policyName,
        CancellationToken ct = default
    )
    {
        var attachments = new List<IamPolicyAttachment>();
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT policy_name, principal_type, principal_name FROM iam_policy_attachments WHERE policy_name = $policy";
        cmd.Parameters.AddWithValue("$policy", policyName);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            attachments.Add(
                new IamPolicyAttachment
                {
                    PolicyName = reader.GetString(0),
                    PrincipalType = reader.GetString(1),
                    PrincipalName = reader.GetString(2),
                }
            );
        return attachments;
    }

    public async Task<List<IamPolicyDocument>> GetEffectivePoliciesForUserAsync(
        string userName,
        CancellationToken ct = default
    )
    {
        var documents = new List<IamPolicyDocument>();
        using var conn = await OpenConnectionAsync(ct);

        // Direct user policies
        using var directCmd = conn.CreateCommand();
        directCmd.CommandText = """
                SELECT p.policy_json FROM iam_policies p
                JOIN iam_policy_attachments a ON p.policy_name = a.policy_name
                WHERE a.principal_type = 'user' AND a.principal_name = $user
            """;
        directCmd.Parameters.AddWithValue("$user", userName);
        using var directReader = await directCmd.ExecuteReaderAsync(ct);
        while (await directReader.ReadAsync(ct))
        {
            var doc = JsonSerializer.Deserialize(
                directReader.GetString(0),
                StorageObjectJsonContext.Default.IamPolicyDocument
            );
            if (doc != null)
                documents.Add(doc);
        }
        await directReader.CloseAsync();

        // Group-inherited policies
        using var groupCmd = conn.CreateCommand();
        groupCmd.CommandText = """
                SELECT p.policy_json FROM iam_policies p
                JOIN iam_policy_attachments a ON p.policy_name = a.policy_name
                JOIN iam_group_members gm ON a.principal_name = gm.group_name
                WHERE a.principal_type = 'group' AND gm.user_name = $user
            """;
        groupCmd.Parameters.AddWithValue("$user", userName);
        using var groupReader = await groupCmd.ExecuteReaderAsync(ct);
        while (await groupReader.ReadAsync(ct))
        {
            var doc = JsonSerializer.Deserialize(
                groupReader.GetString(0),
                StorageObjectJsonContext.Default.IamPolicyDocument
            );
            if (doc != null)
                documents.Add(doc);
        }

        return documents;
    }

    public async Task<bool> IsIamEmptyAsync(CancellationToken ct = default)
    {
        using var conn = await OpenConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM iam_users";
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return count == 0;
    }

    // --- Helpers ---

    private static string GenerateRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        Span<byte> randomBytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(randomBytes);
        var result = new char[length];
        for (var i = 0; i < length; i++)
            result[i] = chars[randomBytes[i] % chars.Length];
        return new string(result);
    }

    private static IamUser MapUser(SqliteDataReader reader) =>
        new()
        {
            UserName = reader.GetString(0),
            Status = reader.GetString(1),
            CreatedAt = DateTime.Parse(reader.GetString(2)),
            UpdatedAt = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3)),
        };

    private static IamAccessKey MapAccessKey(SqliteDataReader reader) =>
        new()
        {
            AccessKey = reader.GetString(0),
            SecretKey = reader.GetString(1),
            UserName = reader.GetString(2),
            Status = reader.GetString(3),
            CreatedAt = DateTime.Parse(reader.GetString(4)),
        };

    private string ProtectSecret(string secret)
    {
        var plaintext = Encoding.UTF8.GetBytes(secret);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using var aes = new AesGcm(_masterKey, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length + tag.Length, ciphertext.Length);
        return EncryptedSecretPrefix + Convert.ToBase64String(payload);
    }

    private string? TryUnprotectSecret(string storedSecret)
    {
        try
        {
            if (!storedSecret.StartsWith(EncryptedSecretPrefix, StringComparison.Ordinal))
            {
                return storedSecret;
            }

            var payload = Convert.FromBase64String(storedSecret[EncryptedSecretPrefix.Length..]);
            if (payload.Length < NonceSizeBytes + TagSizeBytes)
            {
                return null;
            }

            var nonce = payload.AsSpan(0, NonceSizeBytes);
            var tag = payload.AsSpan(NonceSizeBytes, TagSizeBytes);
            var ciphertext = payload.AsSpan(NonceSizeBytes + TagSizeBytes);
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(_masterKey, TagSizeBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[] ParseMasterKey(string masterKey)
    {
        if (string.IsNullOrWhiteSpace(masterKey))
        {
            throw new ArgumentException(
                "Storage master key must be configured for IAM secret encryption."
            );
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(masterKey);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Storage master key must be valid base64.", ex);
        }

        if (decoded.Length != 32)
        {
            throw new ArgumentException("Storage master key must be exactly 32 bytes.");
        }

        return decoded;
    }

    private static IamPolicy MapPolicy(SqliteDataReader reader) =>
        new()
        {
            PolicyName = reader.GetString(0),
            PolicyJson = reader.GetString(1),
            IsBuiltin = reader.GetInt32(2) == 1,
            CreatedAt = DateTime.Parse(reader.GetString(3)),
            UpdatedAt = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
        };
}
