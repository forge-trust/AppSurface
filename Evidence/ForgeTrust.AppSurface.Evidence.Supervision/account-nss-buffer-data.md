# NSS buffer data boundary

[`LinuxAccountNssBufferData`](LinuxAccountNssBufferData.cs) contains only the original pointer-range predicate
and strict UTF-8, NUL-terminated field decoder used by [`LinuxRunAccounts`](LinuxRunAccounts.cs). `User` and
`Group` still call libc NSS through their existing pinned buffers, preserve the 1 KiB through 64 KiB retry
sequence, retain the existing field limits, and keep the group member pointer-array check in the native caller.

The `address` argument is valid only while it points into the retained caller buffer returned by that NSS call.
The helper does not own or retain the address, allocate an NSS buffer, perform a lookup, inspect the kernel, or
authenticate an account. Its `LinuxRunAccountFailure.NssFailed` exception is the same fixed diagnostic used by
the original parser and carries no raw bytes or inner exception.

[`LinuxAccountNssBufferDataTests`](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxAccountNssBufferDataTests.cs)
uses small pinned managed buffers to cover interior and boundary ranges, NUL termination, strict UTF-8, invalid
UTF-8, null/out-of-range pointers, and an overlong field. These are data-only tests and do not create a root,
NSS, process, group, or authority fixture.
