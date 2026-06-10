// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Text.Json.Serialization;
using DotBucket.Server.Cluster;
using DotBucket.Server.Configuration;
using DotBucket.Server.Iam;

namespace DotBucket.Server.Models;

public record InternalCreateBucketRequest(string Name);

public record InternalSetVersioningRequest(string Status);

public record InternalSetObjectLockConfigRequest(bool Enabled, string? DefaultRetentionMode, int? DefaultRetentionDays);

public record InternalSetObjectRetentionRequest(string Mode, DateTime RetainUntil);

public record InternalSetObjectLegalHoldRequest(bool Hold);

public record CreateBucketRequest(string Name);

public record SetVersioningRequest(string Status);

public record AdminBucketListResponse(IEnumerable<Bucket> Buckets);

public record AdminObjectListResponse(List<StorageObject> Objects, int TotalCount);

public record AdminHealthResponse(string Status);

public record AdminVersioningResponse(string Status);

/// <summary>
/// Source generator context for JSON serialization to support Native AOT.
/// This context includes all types that need to be serialized/deserialized via JSON.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StorageObject))]
[JsonSerializable(typeof(List<StorageObject>))]
[JsonSerializable(typeof(IEnumerable<StorageObject>))]
[JsonSerializable(typeof(Bucket))]
[JsonSerializable(typeof(ObjectLockConfig))]
[JsonSerializable(typeof(ObjectLockStatus))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<NotificationConfiguration>))]
[JsonSerializable(typeof(S3Event))]
[JsonSerializable(typeof(LifecycleConfiguration))]
[JsonSerializable(typeof(LifecycleRule))]
[JsonSerializable(typeof(List<LifecycleRule>))]
[JsonSerializable(typeof(AuthOptions))]
[JsonSerializable(typeof(S3Options))]
[JsonSerializable(typeof(ClusterOptions))]
[JsonSerializable(typeof(ClusterOptions.NodeEntry))]
[JsonSerializable(typeof(List<ClusterOptions.NodeEntry>))]
[JsonSerializable(typeof(ClusterState))]
[JsonSerializable(typeof(InternalCreateBucketRequest))]
[JsonSerializable(typeof(InternalSetVersioningRequest))]
[JsonSerializable(typeof(InternalSetObjectLockConfigRequest))]
[JsonSerializable(typeof(InternalSetObjectRetentionRequest))]
[JsonSerializable(typeof(InternalSetObjectLegalHoldRequest))]
[JsonSerializable(typeof(ClusterStatusResponse))]
[JsonSerializable(typeof(ClusterNodeStatusResponse))]
[JsonSerializable(typeof(List<ClusterNodeStatusResponse>))]
[JsonSerializable(typeof(IamUser))]
[JsonSerializable(typeof(List<IamUser>))]
[JsonSerializable(typeof(IamAccessKey))]
[JsonSerializable(typeof(List<IamAccessKey>))]
[JsonSerializable(typeof(IamAccessKeyResponse))]
[JsonSerializable(typeof(List<IamAccessKeyResponse>))]
[JsonSerializable(typeof(IEnumerable<IamAccessKeyResponse>))]
[JsonSerializable(typeof(IamGroup))]
[JsonSerializable(typeof(List<IamGroup>))]
[JsonSerializable(typeof(IamPolicy))]
[JsonSerializable(typeof(List<IamPolicy>))]
[JsonSerializable(typeof(IamPolicyAttachment))]
[JsonSerializable(typeof(List<IamPolicyAttachment>))]
[JsonSerializable(typeof(IamPolicyDocument))]
[JsonSerializable(typeof(IamPolicyStatement))]
[JsonSerializable(typeof(List<IamPolicyStatement>))]
[JsonSerializable(typeof(CreateUserRequest))]
[JsonSerializable(typeof(CreateAccessKeyResponse))]
[JsonSerializable(typeof(CreateGroupRequest))]
[JsonSerializable(typeof(GroupMemberRequest))]
[JsonSerializable(typeof(CreatePolicyRequest))]
[JsonSerializable(typeof(AttachPolicyRequest))]
[JsonSerializable(typeof(SetUserStatusRequest))]
[JsonSerializable(typeof(SetAccessKeyStatusRequest))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(CreateBucketRequest))]
[JsonSerializable(typeof(SetVersioningRequest))]
[JsonSerializable(typeof(AdminBucketListResponse))]
[JsonSerializable(typeof(AdminObjectListResponse))]
[JsonSerializable(typeof(AdminHealthResponse))]
[JsonSerializable(typeof(AdminVersioningResponse))]
public partial class StorageObjectJsonContext : JsonSerializerContext { }
