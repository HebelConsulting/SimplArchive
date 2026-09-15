using SimplArchive.Api.Errors.Exceptions.Concurrency;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Domain.Workflow;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Concurrency;

// One named verb contract per concurrency-tracked entity (ADR 0795). Each is a single line because everything
// they share lives in EntityVerbContract<T> and the only per-entity difference — which refusal a stale
// precondition answers with — is a constructor lambda.
//
// They exist to be NAMED. A shared helper is invisible when somebody forgets to call it; `DocumentVerbs` in a
// controller's constructor is not, and a mutation that writes a Document without taking one is greppable. That
// is the whole distinction between this and the ConcurrencyHeaders it wraps.
//
// Registered in Program.cs. A new tracked entity adds a line here and a line there.

/// <summary>The verb contract for <see cref="Document"/> — the most-mutated entity in the archive.</summary>
public sealed class DocumentVerbs(SimplArchiveDbContext dbContext)
    : EntityVerbContract<Document>(dbContext, EtagMismatchException.ForDocument);

/// <summary>The verb contract for <see cref="Tenant"/> — nine admin settings PUTs share one row.</summary>
public sealed class TenantVerbs(SimplArchiveDbContext dbContext)
    : EntityVerbContract<Tenant>(dbContext, EtagMismatchException.ForTenant);

/// <summary>The verb contract for <see cref="User"/> — written from the admin side and the user's own side.</summary>
public sealed class UserVerbs(SimplArchiveDbContext dbContext)
    : EntityVerbContract<User>(dbContext, EtagMismatchException.ForUser);

/// <summary>The verb contract for <see cref="ServiceAccount"/> — whose PUT is a full replace.</summary>
public sealed class ServiceAccountVerbs(SimplArchiveDbContext dbContext)
    : EntityVerbContract<ServiceAccount>(dbContext, EtagMismatchException.ForServiceAccount);

/// <summary>The verb contract for <see cref="AclEntry"/> — two admins on one permission dialog.</summary>
public sealed class AclEntryVerbs(SimplArchiveDbContext dbContext)
    : EntityVerbContract<AclEntry>(dbContext, EtagMismatchException.ForAclEntry);

/// <summary>The verb contract for <see cref="WorkflowState"/> — two approvers resolving one workflow.</summary>
public sealed class WorkflowStateVerbs(SimplArchiveDbContext dbContext)
    : EntityVerbContract<WorkflowState>(dbContext, EtagMismatchException.ForWorkflow);

/// <summary>The verb contract for <see cref="Group"/> — renamed and re-granted from the admin form (#1220).</summary>
public sealed class GroupVerbs(SimplArchiveDbContext dbContext)
    : EntityVerbContract<Group>(dbContext, EtagMismatchException.ForGroup);

/// <summary>The verb contract for <see cref="TagDefinition"/> — the catalog's name and colour (#1220).</summary>
public sealed class TagDefinitionVerbs(SimplArchiveDbContext dbContext)
    : EntityVerbContract<TagDefinition>(dbContext, EtagMismatchException.ForTag);
