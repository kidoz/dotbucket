// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Iam;

// --- Domain records ---

public record IamUser
{
    public required string UserName { get; init; }
    public string Status { get; init; } = "enabled";
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

public record IamAccessKey
{
    public required string AccessKey { get; init; }
    public string SecretKey { get; init; } = string.Empty;
    public required string UserName { get; init; }
    public string Status { get; init; } = "active";
    public DateTime CreatedAt { get; init; }
}

public record IamGroup
{
    public required string GroupName { get; init; }
    public string Status { get; init; } = "enabled";
    public DateTime CreatedAt { get; init; }
}

public record IamPolicy
{
    public required string PolicyName { get; init; }
    public string PolicyJson { get; init; } = string.Empty;
    public bool IsBuiltin { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

public record IamPolicyAttachment
{
    public required string PolicyName { get; init; }
    public required string PrincipalType { get; init; }
    public required string PrincipalName { get; init; }
}

// --- Policy document records ---

public record IamPolicyDocument
{
    public string Version { get; init; } = "2012-10-17";
    public List<IamPolicyStatement> Statement { get; init; } = [];
}

public record IamPolicyStatement
{
    public string Effect { get; init; } = "Allow";
    public List<string> Action { get; init; } = [];
    public List<string> Resource { get; init; } = [];
}

// --- Admin API DTOs ---

public record IamAccessKeyResponse
{
    public required string AccessKey { get; init; }
    public required string UserName { get; init; }
    public string Status { get; init; } = "active";
    public DateTime CreatedAt { get; init; }
}

public record CreateUserRequest(string UserName);

public record CreateAccessKeyResponse
{
    public required string AccessKey { get; init; }
    public required string SecretKey { get; init; }
    public required string UserName { get; init; }
}

public record CreateGroupRequest(string GroupName);

public record GroupMemberRequest(string UserName);

public record CreatePolicyRequest
{
    public required string PolicyName { get; init; }
    public required IamPolicyDocument PolicyDocument { get; init; }
}

public record AttachPolicyRequest
{
    public required string PolicyName { get; init; }
    public required string PrincipalType { get; init; }
    public required string PrincipalName { get; init; }
}

public record SetUserStatusRequest(string Status);

public record SetAccessKeyStatusRequest(string Status);

// --- Auth context ---

public record S3AuthorizationContext
{
    public required string Action { get; init; }
    public required string Resource { get; init; }
    public required string AccessKey { get; init; }
}

public enum AuthorizationResult
{
    Allow,
    Deny,
    ImplicitDeny,
}
