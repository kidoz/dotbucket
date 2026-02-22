// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using DotBucket.Server.Auth;
using DotBucket.Server.Iam;
using Microsoft.Data.Sqlite;

namespace DotBucket.Server.Endpoints.Admin;

public static class IamEndpoints
{
    public static void MapIamEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/iam")
            .WithTags("IAM API")
            .AddEndpointFilter<AdminTokenEndpointFilter>();

        // --- Users ---

        group.MapGet(
            "/users",
            async (IamStore store, CancellationToken ct) =>
            {
                var users = await store.ListUsersAsync(ct);
                return Results.Ok(users);
            }
        );

        group.MapPost(
            "/users",
            async (
                CreateUserRequest request,
                IamStore store,
                PolicyEngine engine,
                CancellationToken ct
            ) =>
            {
                if (string.IsNullOrWhiteSpace(request.UserName))
                    return Results.BadRequest("UserName is required.");

                try
                {
                    var user = await store.CreateUserAsync(request.UserName, ct);
                    engine.InvalidateCache();
                    return Results.Ok(user);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
                {
                    return Results.Conflict($"User '{request.UserName}' already exists.");
                }
            }
        );

        group.MapGet(
            "/users/{userName}",
            async (string userName, IamStore store, CancellationToken ct) =>
            {
                var user = await store.GetUserAsync(userName, ct);
                if (user == null)
                    return Results.NotFound($"User '{userName}' not found.");
                return Results.Ok(user);
            }
        );

        group.MapPut(
            "/users/{userName}/status",
            async (
                string userName,
                SetUserStatusRequest request,
                IamStore store,
                PolicyEngine engine,
                CancellationToken ct
            ) =>
            {
                if (request.Status != "enabled" && request.Status != "disabled")
                    return Results.BadRequest("Status must be 'enabled' or 'disabled'.");

                var user = await store.GetUserAsync(userName, ct);
                if (user == null)
                    return Results.NotFound($"User '{userName}' not found.");

                await store.SetUserStatusAsync(userName, request.Status, ct);
                engine.InvalidateCache();
                return Results.NoContent();
            }
        );

        group.MapDelete(
            "/users/{userName}",
            async (string userName, IamStore store, PolicyEngine engine, CancellationToken ct) =>
            {
                var user = await store.GetUserAsync(userName, ct);
                if (user == null)
                    return Results.NotFound($"User '{userName}' not found.");

                await store.DeleteUserAsync(userName, ct);
                engine.InvalidateCache();
                return Results.NoContent();
            }
        );

        // --- Access Keys ---

        group.MapGet(
            "/users/{userName}/access-keys",
            async (string userName, IamStore store, CancellationToken ct) =>
            {
                var user = await store.GetUserAsync(userName, ct);
                if (user == null)
                    return Results.NotFound($"User '{userName}' not found.");

                var keys = await store.ListAccessKeysAsync(userName, ct);
                var response = keys.Select(k => new IamAccessKeyResponse
                {
                    AccessKey = k.AccessKey,
                    UserName = k.UserName,
                    Status = k.Status,
                    CreatedAt = k.CreatedAt,
                });
                return Results.Ok(response);
            }
        );

        group.MapPost(
            "/users/{userName}/access-keys",
            async (string userName, IamStore store, PolicyEngine engine, CancellationToken ct) =>
            {
                var user = await store.GetUserAsync(userName, ct);
                if (user == null)
                    return Results.NotFound($"User '{userName}' not found.");

                var response = await store.CreateAccessKeyAsync(userName, ct);
                engine.InvalidateCache();
                return Results.Ok(response);
            }
        );

        group.MapPut(
            "/access-keys/{accessKey}/status",
            async (
                string accessKey,
                SetAccessKeyStatusRequest request,
                IamStore store,
                PolicyEngine engine,
                CancellationToken ct
            ) =>
            {
                if (request.Status != "active" && request.Status != "inactive")
                    return Results.BadRequest("Status must be 'active' or 'inactive'.");

                await store.SetAccessKeyStatusAsync(accessKey, request.Status, ct);
                engine.InvalidateCache();
                return Results.NoContent();
            }
        );

        group.MapDelete(
            "/access-keys/{accessKey}",
            async (string accessKey, IamStore store, PolicyEngine engine, CancellationToken ct) =>
            {
                await store.DeleteAccessKeyAsync(accessKey, ct);
                engine.InvalidateCache();
                return Results.NoContent();
            }
        );

        // --- Groups ---

        group.MapGet(
            "/groups",
            async (IamStore store, CancellationToken ct) =>
            {
                var groups = await store.ListGroupsAsync(ct);
                return Results.Ok(groups);
            }
        );

        group.MapPost(
            "/groups",
            async (
                CreateGroupRequest request,
                IamStore store,
                PolicyEngine engine,
                CancellationToken ct
            ) =>
            {
                if (string.IsNullOrWhiteSpace(request.GroupName))
                    return Results.BadRequest("GroupName is required.");

                try
                {
                    var g = await store.CreateGroupAsync(request.GroupName, ct);
                    engine.InvalidateCache();
                    return Results.Ok(g);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    return Results.Conflict($"Group '{request.GroupName}' already exists.");
                }
            }
        );

        group.MapDelete(
            "/groups/{groupName}",
            async (string groupName, IamStore store, PolicyEngine engine, CancellationToken ct) =>
            {
                await store.DeleteGroupAsync(groupName, ct);
                engine.InvalidateCache();
                return Results.NoContent();
            }
        );

        group.MapGet(
            "/groups/{groupName}/members",
            async (string groupName, IamStore store, CancellationToken ct) =>
            {
                var members = await store.ListGroupMembersAsync(groupName, ct);
                return Results.Ok(members);
            }
        );

        group.MapPost(
            "/groups/{groupName}/members",
            async (
                string groupName,
                GroupMemberRequest request,
                IamStore store,
                PolicyEngine engine,
                CancellationToken ct
            ) =>
            {
                if (string.IsNullOrWhiteSpace(request.UserName))
                    return Results.BadRequest("UserName is required.");

                if (await store.GetGroupAsync(groupName, ct) == null)
                    return Results.NotFound($"Group '{groupName}' not found.");

                var user = await store.GetUserAsync(request.UserName, ct);
                if (user == null)
                    return Results.NotFound($"User '{request.UserName}' not found.");

                await store.AddGroupMemberAsync(groupName, request.UserName, ct);
                engine.InvalidateCache();
                return Results.NoContent();
            }
        );

        group.MapDelete(
            "/groups/{groupName}/members/{userName}",
            async (
                string groupName,
                string userName,
                IamStore store,
                PolicyEngine engine,
                CancellationToken ct
            ) =>
            {
                await store.RemoveGroupMemberAsync(groupName, userName, ct);
                engine.InvalidateCache();
                return Results.NoContent();
            }
        );

        // --- Policies ---

        group.MapGet(
            "/policies",
            async (IamStore store, CancellationToken ct) =>
            {
                var policies = await store.ListPoliciesAsync(ct);
                return Results.Ok(policies);
            }
        );

        group.MapPost(
            "/policies",
            async (
                CreatePolicyRequest request,
                IamStore store,
                PolicyEngine engine,
                CancellationToken ct
            ) =>
            {
                if (string.IsNullOrWhiteSpace(request.PolicyName))
                    return Results.BadRequest("PolicyName is required.");

                if (request.PolicyDocument.Statement.Count == 0)
                    return Results.BadRequest("Policy must contain at least one statement.");

                foreach (var stmt in request.PolicyDocument.Statement)
                {
                    if (stmt.Effect != "Allow" && stmt.Effect != "Deny")
                        return Results.BadRequest("Statement Effect must be 'Allow' or 'Deny'.");
                    if (stmt.Action.Count == 0)
                        return Results.BadRequest("Statement must contain at least one Action.");
                    if (stmt.Resource.Count == 0)
                        return Results.BadRequest("Statement must contain at least one Resource.");
                }

                try
                {
                    var policy = await store.CreatePolicyAsync(
                        request.PolicyName,
                        request.PolicyDocument,
                        ct: ct
                    );
                    engine.InvalidateCache();
                    return Results.Ok(policy);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    return Results.Conflict($"Policy '{request.PolicyName}' already exists.");
                }
            }
        );

        group.MapGet(
            "/policies/{policyName}",
            async (string policyName, IamStore store, CancellationToken ct) =>
            {
                var policy = await store.GetPolicyAsync(policyName, ct);
                if (policy == null)
                    return Results.NotFound($"Policy '{policyName}' not found.");
                return Results.Ok(policy);
            }
        );

        group.MapDelete(
            "/policies/{policyName}",
            async (string policyName, IamStore store, PolicyEngine engine, CancellationToken ct) =>
            {
                var policy = await store.GetPolicyAsync(policyName, ct);
                if (policy == null)
                    return Results.NotFound($"Policy '{policyName}' not found.");
                if (policy.IsBuiltin)
                    return Results.BadRequest("Cannot delete built-in policies.");

                await store.DeletePolicyAsync(policyName, ct);
                engine.InvalidateCache();
                return Results.NoContent();
            }
        );

        group.MapPost(
            "/policies/attach",
            async (
                AttachPolicyRequest request,
                IamStore store,
                PolicyEngine engine,
                CancellationToken ct
            ) =>
            {
                if (request.PrincipalType != "user" && request.PrincipalType != "group")
                    return Results.BadRequest("PrincipalType must be 'user' or 'group'.");

                if (!await store.PolicyExistsAsync(request.PolicyName, ct))
                    return Results.NotFound($"Policy '{request.PolicyName}' not found.");

                // Verify principal exists
                if (request.PrincipalType == "user")
                {
                    if (await store.GetUserAsync(request.PrincipalName, ct) == null)
                        return Results.NotFound($"User '{request.PrincipalName}' not found.");
                }
                else
                {
                    if (await store.GetGroupAsync(request.PrincipalName, ct) == null)
                        return Results.NotFound($"Group '{request.PrincipalName}' not found.");
                }

                await store.AttachPolicyAsync(
                    request.PolicyName,
                    request.PrincipalType,
                    request.PrincipalName,
                    ct
                );
                engine.InvalidateCache();
                return Results.NoContent();
            }
        );

        group.MapPost(
            "/policies/detach",
            async (
                AttachPolicyRequest request,
                IamStore store,
                PolicyEngine engine,
                CancellationToken ct
            ) =>
            {
                await store.DetachPolicyAsync(
                    request.PolicyName,
                    request.PrincipalType,
                    request.PrincipalName,
                    ct
                );
                engine.InvalidateCache();
                return Results.NoContent();
            }
        );

        group.MapGet(
            "/policies/{policyName}/attachments",
            async (string policyName, IamStore store, CancellationToken ct) =>
            {
                var attachments = await store.ListPolicyAttachmentsAsync(policyName, ct);
                return Results.Ok(attachments);
            }
        );
    }
}
