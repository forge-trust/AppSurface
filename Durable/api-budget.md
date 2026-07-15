# Durable slice 2 API budget

This ledger records how the public surface from the original durable-contract commit was treated when the contract was
rebuilt as two preview packages. The checked-in
[Durable](ForgeTrust.AppSurface.Durable/PublicAPI.Shipped.txt) and
[Provider](ForgeTrust.AppSurface.Durable.Provider/PublicAPI.Shipped.txt) snapshots are the exhaustive member-level
source of truth; this ledger explains the intentional package and visibility decisions.

## Retained adopter API

Every public type in the `ForgeTrust.AppSurface.Durable` snapshot remains public in that package unless it appears in
the additions below. These are the adopter-facing Work, Flow, Schedule, serialization, registration, and client
contracts. No adopter-facing public type was removed or internalized.

## Moved provider API

Every public type in the `ForgeTrust.AppSurface.Durable.Provider` snapshot other than `DurableProviderWorkAdapter` was
retained from the original contract and moved from the adopter assembly into the Provider package. This includes the
runtime pump, runtime health, drain, claimed-work, control, recovery, and operator families. The move establishes the
one-way package dependency Provider to Durable and replaces the original internal/friend provider seam.

## Added public API

- `DurableCommandFingerprint` and `DurableCommandFingerprintMatch` add versioned semantic command identity.
- `DurableWorkExecutionContext` and `DurablePreparedWork` expose the adopter side of provider-neutral Work execution.
- `DurableProviderWorkAdapter` exposes the provider side of the Work identity transition without friend access.

## Internalized or removed implementation seams

Canonicalization, identifier validation, provider validation, and fingerprint construction remain internal helpers;
they are implementation details behind validated public constructors. The original internal schedule/provider friend
seam was removed. No public type was deleted outright.

## Compatibility rule

Both packages remain source-only public previews and are machine-blocked from publication. Until the PostgreSQL
provider milestone, changes may still be made to these contracts, but every public member change must update the
appropriate deterministic API snapshot and this ledger when it changes a type's audience, package, or visibility.
