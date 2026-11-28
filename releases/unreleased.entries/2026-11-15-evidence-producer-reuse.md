<!-- appsurface:unreleased-entry section="included" -->
### Reuse complete local producer evidence

The [Evidence CLI](../../Cli/ForgeTrust.AppSurface.Cli/README.md) and [prepared EvidenceHost](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md) can opt into reuse of complete local producer results. Explicit input declarations, effective tool and fixture identities, verified artifact custody, and actual owned-work settlement govern receipt admission. Prepared hosts start only the resources needed by fresh producers. Existing policies without input declarations keep executing fresh.

Restored results are informational and cannot satisfy protected CI or release gates. Coverage workloads must explicitly adopt the [cooperative child-lifetime contract](../../guides/evidence-reuse.md#cooperative-child-lifetime-promise); unregistered or detached work is outside that promise. The [reuse guide](../../guides/evidence-reuse.md) documents audit, capacity, output, and unknown-execution recovery rules. Coverage thresholds and fresh CI execution requirements are unchanged.
