<!-- appsurface:unreleased-entry section="migration-watch" -->

### Complete runtime-set PostgreSQL preflight

- [`appsurface durable schema preflight`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#durable-postgresql-schema-commands) now requires `--role-pairs-file` and `--migration-owner-role`, including for one-pair stores. It checks every runtime in the unchanged complete manifest in one fenced read-only snapshot, reports the credential's pair and manifest hash, and rejects policy, grant, owner or privilege drift. Update existing invocations to supply both reviewed inputs.
- A successful owner diagnostic cannot replace runtime credential evidence. The [schema-11 operations checklist](../../Durable/heartbeat-retention-operations.md#complete-runtime-set-preflight-and-proof-checklist) requires matching StoreId and epoch, one pass per distinct runtime, both lane proofs and a continuous owner guard through activation. Candidate and public package proofs are separate gates; structural preflight does not initialize the epoch or authorize a later deployment.
