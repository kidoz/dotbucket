// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Iam;

/// <summary>
/// Seeds built-in IAM policies on startup.
/// </summary>
public class IamSeeder(IamStore store, ILogger<IamSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        var builtinPolicies = new (string Name, IamPolicyDocument Document)[]
        {
            (
                "consoleAdmin",
                new IamPolicyDocument
                {
                    Statement =
                    [
                        new IamPolicyStatement
                        {
                            Effect = "Allow",
                            Action = ["s3:*", "admin:*"],
                            Resource = ["arn:aws:s3:::*"],
                        },
                    ],
                }
            ),
            (
                "readwrite",
                new IamPolicyDocument
                {
                    Statement =
                    [
                        new IamPolicyStatement
                        {
                            Effect = "Allow",
                            Action = ["s3:*"],
                            Resource = ["arn:aws:s3:::*"],
                        },
                    ],
                }
            ),
            (
                "readonly",
                new IamPolicyDocument
                {
                    Statement =
                    [
                        new IamPolicyStatement
                        {
                            Effect = "Allow",
                            Action =
                            [
                                "s3:GetBucketLocation",
                                "s3:GetObject",
                                "s3:ListBucket",
                                "s3:ListAllMyBuckets",
                            ],
                            Resource = ["arn:aws:s3:::*"],
                        },
                    ],
                }
            ),
            (
                "writeonly",
                new IamPolicyDocument
                {
                    Statement =
                    [
                        new IamPolicyStatement
                        {
                            Effect = "Allow",
                            Action = ["s3:PutObject"],
                            Resource = ["arn:aws:s3:::*"],
                        },
                    ],
                }
            ),
        };

        foreach (var (name, document) in builtinPolicies)
        {
            if (await store.PolicyExistsAsync(name, ct))
                continue;

            await store.CreatePolicyAsync(name, document, isBuiltin: true, ct: ct);
            logger.LogInformation("Seeded built-in IAM policy: {PolicyName}", name);
        }
    }
}
