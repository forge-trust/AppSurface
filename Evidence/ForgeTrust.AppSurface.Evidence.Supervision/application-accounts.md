# Application account identities

[`LinuxRunAccounts`](LinuxRunAccounts.cs) owns the system accounts used by a supervised run. Its internal factory keeps the existing two-user/results-group profile by default. The [application execution flow](application-execution.md) opts into a separate application identity with the exact call shape:

```csharp
LinuxRunAccounts.CreateAsync(owner, token, includeApplication: true)
```

`includeApplication` defaults to `false`. The default still checks and creates only the worker user, subject user, and results group. Application code should opt in only when it needs the dedicated application UID and GIDs. The account factory requires the live [`LinuxOwnerActivation`](LinuxOwnerActivation.cs); snapshot data or caller-supplied numbers cannot create or authenticate accounts.

## Identities and verification

For the owner’s 28-character lowercase hexadecimal run suffix, names use these roles:

| Name | Role |
| --- | --- |
| `eva<suffix>` | Application user and its private primary group |
| `evg<suffix>` | Empty resource-access group |

The application user uses the same fixed `useradd` policy as the worker and subject: system account, private user group, no home directory, and `/usr/sbin/nologin`. The resource-access group uses the fixed system `groupadd` policy. Numeric IDs are allocated by the operating system; no command accepts a requested UID, GID, or supplementary group list.

Before dispatch, the account owner checks every selected account and group name through NSS. After creation, it verifies the application name-to-UID mapping and reverse UID-to-name mapping, its private primary group and reverse GID mapping, and the resource group’s forward and reverse mapping. It also requires exactly one process group for the application user: its private primary group. Both new groups must have no registered members. The application UID must differ from the worker and subject UIDs, and its private GID and resource-access GID must differ from all three existing GIDs and from each other. The results group remains separate and grants no application membership.

`LinuxRunAccountSnapshot` retains trailing application fields with a default value of zero so existing workspace and worker snapshot construction remains compatible. Zero means “not included” only for those legacy snapshot callers. The NSS parser requires the complete application user, private group, and resource group together when the application profile is enabled. The live `LinuxRunAccounts.ApplicationUid`, `ApplicationGid`, and `ResourceAccessGid` accessors also require enabled, active account ownership and throw when the application profile is absent or the accounts have closed.

## Reservation, rollback, and storage custody

The application profile creates five utilities in order: worker user, subject user, results group, application user, resource-access group. Before each utility dispatch, the reservation ledger records every corresponding deletion. A user reservation includes its implicit private group, so reverse cleanup deletes the user before trying to delete that group. Cleanup processes all reservations in reverse order and checks actual NSS absence before skipping a private group that `userdel` already removed. Any utility whose physical exit is uncertain keeps the account set quarantined; its reserved names are not treated as safely released.

Before creating mutable application-owned storage, call `RetainApplicationForCustody(workspace, owner)` with the exact workspace and owner. During account cleanup, `RequireCustody` calls `workspace.RequireSealedForAccountRelease(this, owner, token)` before each account-ID deletion, after each utility attempt, and after final NSS absence. This repeated native check keeps application storage attached to the identities that owned it until the workspace is sealed. One failed or unverifiable check latches the close attempt into quarantine, so a later passing check cannot release more IDs. See the related [workspace custody ordering](producer-workspace.md) and [producer artifact custody](producer-artifacts.md) guidance.

## Decision and sharp edges

The opt-in preserves the old account count and NSS work for workflows that do not launch an application. A dedicated application UID separates its process identity; the distinct empty resource group gives storage code a named group identity to assign when the application workflow requires it. Creating that group alone grants no access. Keep it separate from `ResultsGid` and do not add it to application supplementary groups.

Do not read the application accessors on the default profile, pass zero-valued legacy snapshot fields as an application identity, or derive identity values from a fixture. Retain the actual application workspace before its first mutable storage operation; closing workspace descriptors or reporting a successful application run is not proof that storage is sealed. Account IDs are reusable only after all cleanup utilities settle, every required NSS name is absent, and every retained storage guard succeeds.
