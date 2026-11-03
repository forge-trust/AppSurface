# Report custody metadata comparisons

[`LinuxReportCustodyMetadata`](LinuxReportCustodyMetadata.cs) holds the existing detached comparisons used by
[`LinuxProducerArtifactCustody`](LinuxProducerArtifactCustody.cs). The native holder retains all selected
root descriptors, no-follow path opens, producer/owner authentication, deadlines and sticky failures.

`SameFileSample` compares device, inode, UID/GID, mode, link count and length. It deliberately leaves
ctime/mtime to the independent retained report engine. `SameInventory` requires the same count, order,
paths, lengths and digests; a reordered or partially changed inventory is rejected.

`RequireResultsParent` first invokes the original protected ancestor check, then requires root group,
exact directory mode 0711 and nonzero links. `RequireSelectedRoot` first invokes the original report-root
identity check, then requires exact 0700. The original diagnostic ordering and fixed failures remain.
The caller still performs every actual stat, named reopen, no-follow lookup and token check in its
original position. Detached metadata and equality create no selected root, account, producer or custody.

The [portable tests](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxReportCustodyMetadataTests.cs)
verify independent mutations and unchanged neighbors. Genuine Linux retained-file controls remain
required to establish real descriptor/name and producer settlement observations.
