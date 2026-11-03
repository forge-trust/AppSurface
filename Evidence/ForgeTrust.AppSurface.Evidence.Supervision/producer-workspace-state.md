# Producer workspace state policy

`LinuxProducerWorkspaceStatePolicy` is a small internal, data-only policy used by the
[producer workspace](LinuxProducerWorkspace.cs). The native workspace caller samples
the existing flags while holding its existing `_gate`, then performs the original
owner/reference, account, control-identity, deadline, and descriptor checks in the
same order. The policy does not create a workspace, authenticate an owner, acquire a
lease, open a descriptor, or grant capability.

The policy preserves five existing conjunctions: producer retention, active producer
work, cleanup control ownership, one-shot results-token preparation, and sealed
account release. `LinuxProducerWorkspaceStatePolicyTests` exercises valid adjacent
states and one changed required field for each conjunction. Native path checks,
handle closure, quarantine, cleanup latches, token validation, and account custody
remain in the caller and are intentionally outside this pure policy.

The facts must be sampled under the caller's existing lock. Do not pass live owner,
account, token, handle, lease, or native metadata objects into this policy, and do
not infer authority from a policy result. Its fixed failure remains the original
`ASEVD402` producer workspace admission error.
