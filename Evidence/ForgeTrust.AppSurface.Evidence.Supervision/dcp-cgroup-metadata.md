# DCP cgroup metadata comparisons

[`LinuxDcpCgroupMetadataPolicy`](LinuxDcpCgroupMetadataPolicy.cs) verifies the existing detached stat fields
used by [DCP selection](LinuxApplicationDcpIdentity.cs). It makes cgroup access and repeated-sample rejection
rules directly verifiable without manufacturing native process ownership.

`Require(metadata, directory)` preserves root UID/GID, nonzero inode, selected directory/regular-file
type, no set-user/set-group or group/other write bits, owner access and link-count rules. Directories
require owner read/search and at least two links; files require owner read and exactly one link.
`SameNode(first, second)` compares device, inode, owner, group and mode. Changing cgroup length or link
count is deliberately not identity, although both actual samples must pass their independent validation.

The native caller still checks cgroup2 filesystem magic, reads `statx` on retained descriptors, reopens
each exact generated name under its retained parent, validates both samples and checks the original
cancellation token. These operations keep their original order. Failure retains the existing fixed
`ASEVD410` diagnostic and no inner exception through the original DCP rejection factory.

The [tests](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxDcpCgroupMetadataPolicyTests.cs) use
detached metadata only. Equality or valid permissions cannot establish a real cgroup, a live process,
the selected DCP executable, resource readiness or cleanup authority. Genuine Linux controls remain
required for those observations.
