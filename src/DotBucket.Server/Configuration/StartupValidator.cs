// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Configuration;

/// <summary>
/// Validates security- and durability-critical configuration at startup so the server
/// "fails closed": fatal misconfiguration aborts startup in non-Development environments
/// with a clear error, instead of logging-and-continuing into an unsafe state.
/// </summary>
public static class StartupValidator
{
    public record Result(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

    private static readonly string[] WellKnownCredentials = { "minioadmin", "admin", "password" };

    /// <summary>
    /// Validates configuration. In Development, problems are downgraded to warnings so local
    /// iteration stays frictionless; outside Development they are fatal errors.
    /// </summary>
    public static Result Validate(
        bool isDevelopment,
        AuthOptions auth,
        StorageOptions storage,
        ClusterOptions? cluster
    )
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // A problem is fatal outside Development, advisory inside it.
        void Fatal(string message)
        {
            if (isDevelopment)
                warnings.Add(message + " (allowed in Development; fatal in production)");
            else
                errors.Add(message);
        }

        // --- Root credentials ---
        if (string.IsNullOrEmpty(auth.RootAccessKey) || string.IsNullOrEmpty(auth.RootSecretKey))
        {
            Fatal("Auth:RootAccessKey and Auth:RootSecretKey must both be configured.");
        }
        else
        {
            if (WellKnownCredentials.Contains(auth.RootAccessKey, StringComparer.OrdinalIgnoreCase))
                Fatal("Auth:RootAccessKey is set to a well-known default value; change it.");
            if (WellKnownCredentials.Contains(auth.RootSecretKey, StringComparer.OrdinalIgnoreCase))
                Fatal("Auth:RootSecretKey is set to a well-known default value; change it.");
            if (auth.RootSecretKey.Length < 8)
                warnings.Add("Auth:RootSecretKey is shorter than 8 characters.");
        }

        // --- Storage root safety ---
        if (string.IsNullOrWhiteSpace(storage.RootPath))
        {
            Fatal("Storage:RootPath must be configured.");
        }
        else
        {
            var fullRoot = Path.GetFullPath(storage.RootPath);
            var systemRoot = Path.GetPathRoot(fullRoot);
            if (string.Equals(fullRoot, systemRoot, StringComparison.Ordinal))
                Fatal(
                    $"Storage:RootPath ('{fullRoot}') resolves to a filesystem root; refusing to use it."
                );
        }

        // --- Encryption master key (32-byte base64) ---
        if (string.IsNullOrWhiteSpace(storage.MasterKey))
        {
            Fatal("Storage:MasterKey must be configured (base64-encoded 32-byte key).");
        }
        else
        {
            try
            {
                var key = Convert.FromBase64String(storage.MasterKey);
                if (key.Length != 32)
                    errors.Add(
                        $"Storage:MasterKey must decode to exactly 32 bytes (AES-256); got {key.Length}."
                    );
            }
            catch (FormatException)
            {
                errors.Add("Storage:MasterKey is not valid base64.");
            }
        }

        // --- Cluster identity and trust (only when clustering is enabled) ---
        if (cluster?.Enabled == true)
        {
            if (string.IsNullOrWhiteSpace(cluster.NodeId))
                Fatal("Cluster:NodeId must be configured when Cluster:Enabled is true.");
            if (string.IsNullOrWhiteSpace(cluster.AdvertiseAddress))
                Fatal("Cluster:AdvertiseAddress must be configured when Cluster:Enabled is true.");
            if (string.IsNullOrWhiteSpace(cluster.ClusterToken))
                Fatal(
                    "Cluster:ClusterToken must be configured when Cluster:Enabled is true (used to authenticate inter-node calls)."
                );
            else if (cluster.ClusterToken.Length < 16)
                warnings.Add(
                    "Cluster:ClusterToken is shorter than 16 characters; use a stronger shared secret."
                );

            if (cluster.Nodes.Count == 0)
                warnings.Add("Cluster:Enabled is true but Cluster:Nodes is empty.");
            if (cluster.WriteQuorum < 1 || cluster.WriteQuorum > cluster.ReplicationFactor)
                errors.Add(
                    $"Cluster:WriteQuorum ({cluster.WriteQuorum}) must be between 1 and ReplicationFactor ({cluster.ReplicationFactor})."
                );
            if (cluster.ReadQuorum < 1 || cluster.ReadQuorum > cluster.ReplicationFactor)
                errors.Add(
                    $"Cluster:ReadQuorum ({cluster.ReadQuorum}) must be between 1 and ReplicationFactor ({cluster.ReplicationFactor})."
                );
        }

        return new Result(errors, warnings);
    }
}
