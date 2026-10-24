# Selected report byte transfer

[`SupervisionReportChunkTransfer.ReadValidated`](SupervisionReportChunkTransfer.cs) joins up to two original
64 KiB reads into the caller's already validated range of at most 128 KiB. It exists to keep byte assembly
and failure ordering independently verifiable while [artifact custody](producer-artifacts.md) retains
every native selection and authority check.

The [native holder](LinuxProducerArtifactCustody.cs) keeps its original lock, open/failure checks, complete
owner/account/workspace/deadline check, exact inventory path, range checks and retained report-set lookup.
It supplies only its original synchronous `CheckWork` and `ReadChunk` operations. The helper accepts no
path, root, process, owner or admission. It cannot construct or replace a retained report set, reopen
custody, extend a deadline, issue a request or change failure latches.

For an empty report the original reader is still called exactly once to establish EOF, followed by the
original final work check. For a nonempty range each read is preceded by a work check; a short piece
rejects immediately with the existing fixed `IOException`. A final check occurs before any result is
returned. Reader, cancellation and check failures propagate without retry or detached work. The native
holder's unchanged catch marks its failure sticky. Validated length, offset and count are preconditions;
callers must not treat this internal copying procedure as a range validator or read capability.

The [portable tests](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/SupervisionReportChunkTransferTests.cs)
use ordinary local files and controlled synchronous reads/checks. They verify exact offsets, piece bounds,
empty-report checks, fault precedence and no result after cancellation. They authenticate no Linux
workspace, native producer or report, and establish no coverage acceptance.
