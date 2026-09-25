<!-- appsurface:unreleased-entry section="included" -->
### Shared-store Durable PostgreSQL role pairs

- The PostgreSQL role recipe now reconciles a complete version-1 `role_pairs_json` manifest, preserving existing pairs and refusing omitted roles. Keep migrations ahead of reconciliation and rerun the recipe with the same complete manifest after schema upgrades. The local integration example proves the forwarding `full` pair remains operational after adding a `work_only` dispatcher, which has no direct Durable table access.
- Separate credentials establish a deployed-lane boundary; they do not provide PostgreSQL row isolation. Work discovery selectors and transaction-local scope values retain their existing semantics. See the [#823 approved design](../../docs/designs/issue-823-shared-store-role-pairs.md) and [implementation plan](../../docs/plans/issue-823-shared-store-role-pairs.md) for the manifest recipe and rollout proof.
