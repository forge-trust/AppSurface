# Private pinned SDK distribution preparation

## Validation status and initial handoff history

The initial source handoff was unintegrated and unexecuted. A separately
authorized read-only inspection then downloaded and verified the complete
publisher archive without extraction, establishing the need for bounded GNU
long-name handling. That initial status is historical, not the current parser
validation result.

Current [portable controls](test_trusted_sdk_distribution.py) passed **12/12**,
exit 0, in 0.227 seconds under a 45-second external process-group owner, with no
failures, errors, skips or warnings. A separate actual publisher archive
`audit_archive` call completed with exit 0 in 1.899 seconds under a 60-second
external owner. Both owned groups were confirmed empty; all three source
SHA/mode bindings remained unchanged. The actual archive retained identical
publisher SHA-512 before/after and identical FD/named-file identities.

Private [preparation wiring](prepare.py) is prepared and under peer review and
portable validation; it is not committed or natively validated. Actual root
installation, extraction of the actual SDK, the complete 120-second SDK audit,
SDK builds, consumers and qualification remain **unverified**. Small owned
portable archives do not represent a usable SDK or a successful root handoff.
The [private qualification guide](README.md) describes the separate consumer
and proof requirements.

## Fixed publisher selection

[`trusted_sdk_distribution.install(deadline)`](trusted_sdk_distribution.py)
accepts only a monotonic deadline.
Its source is the complete official Linux x64 SDK 10.0.401 archive, with the
compiled URL and SHA-512 from [Microsoft's release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).
The fresh destination is exactly `/usr/share/issue779-dotnet-10.0.401`. It never
adopts an existing destination, including a dangling link, and never moves or
deletes a preinstalled SDK. Installation is a separate trusted preparation task;
it has no consumer, registration, lease or proof authority.

The root caller must protect `/usr/share` with its existing retained-FD ancestor
bootstrap in [trusted_sdk.py](trusted_sdk.py) before calling this module. Both
`/usr` and `/usr/share` must be root
UID/GID, directories, readable/searchable, without special bits or group/other
write bits. This module rejects a writable ancestor and performs no ancestor
chmod/chown. The integration owner must also bind the new fixed root to the
private bootstrap and launcher's fixed PATH before any consumer is started.

## Bounds and ordering

One cumulative deadline, capped at 180 seconds, covers validation, download,
hashing, metadata traversal, extraction and final rechecks. TLS verification is
enabled; ambient proxies and redirects are disabled. Network operations have a
five-second timeout with budget reserved before each blocking `read1`. Each
iteration makes one bounded read and then rechecks remaining time; it does not
ask `read(CHUNK)` to fill a buffer across repeated inactivity timeouts. Limits are
512 MiB compressed, 16 GiB expanded, 256 MiB per member, 100,000 nodes and depth
32. Implied parent directories count as nodes. All archive bytes are SHA-512
verified before parsing; all metadata is exhausted before destination creation.

Only regular files and directories are accepted. One leading `./` and the root
directory spelling are normalized; inner dot/parent segments, absolute paths,
duplicate/colliding names, links, devices, FIFOs, special permission bits,
unsupported extensions and hidden nonzero trailing payload are rejected. Only a
single GNU long-name (`L`) control may precede a regular file or directory. Its
payload is at most 4096 bytes, contains exactly one terminal NUL, and has zero
block padding. Nested/orphan L, long-link, PAX/global-PAX, sparse and unknown
control types are rejected before delegated extension processing. The resolved
name passes every ordinary path, duplicate, type, mode and bound check. Physical
headers have a separate finite ceiling of twice the filesystem-node bound.

The independent publisher inspection measured 1913 GNU L controls, 5631
filesystem nodes, 240059572 compressed bytes and 640148071 file bytes, with no
other extension/link types or recorded path/mode violations. The subsequent
actual archive audit through this parser verified those same control/node/byte
counts: 4907 files and 724 directories, maximum member 40064808 bytes and maximum
depth 13. It verified the compiled publisher SHA-512 before/after:

```text
51c8b999af9e8dd9998c9edc5944e19a90788862068acd38694e098889054ce8c23d4f0c5cccfa16bf187d044562359e5ee69a9f8ad0bbe913ba90311fbce25b
```

This establishes complete archive parser compatibility, not installer or root
SDK handoff success. The old blanket GNU rejection was deliberately
replaced; the prior eight portable method bodies remain unchanged. No permissive
extraction fallback is available.

Extraction uses exclusive, no-follow writes beneath retained directory FDs;
files receive published ordinary modes with only 022 cleared, preserving execute
bits. Directories must retain all 0555 bits. Root ownership is required in the
installer. Archive stat/name identity and SHA-512 are rechecked after tar use.
Failed extraction leaves a partial protected destination that must be treated
as quarantined; a later invocation rejects it. No rollback or retry-in-place is
claimed. Errors expose only `trusted-sdk-distribution-rejected`.

## Data/procedure controls and receipt

[`audit_archive(fd, expected_sha512, deadline, owner_uid=...)`](trusted_sdk_distribution.py)
is a metadata-only
seam returning frozen `ArchiveAudit`/`Member` records. It makes no destination
mutations. `extract_audited(fd, destination_fd, audit, deadline, owner_uid=...)`
is an owned-FD procedure seam returning a tree SHA-256. These helpers never
choose the production root or enroll their fixture. Their owner/digest arguments
exist for portable owned-file controls; `install` supplies the publisher literal
and root UID directly and accepts no injected downloader, plan or destination.
Neither helper closes the caller's supplied FDs; the caller must retain and
close them. Helpers use the archive's file offset exclusively while operating.
`ArchiveAudit.gnu_longname_headers` binds the observed control count; extraction
must reproduce it as well as every explicit member, identity and publisher hash.

The installation record contains fixed schema/version/RID/root/URL/digest,
compressed and expanded byte counts, explicit and total node/control counts, tree
SHA-256, `complete`, `sdk_audit_completed: false` and `qualification_claim: false`.
It is publisher provenance only. The subsequent existing [full SDK audit](trusted_sdk.py) must
still validate the **entire** selected installation under its unchanged
120-second/four-hash-pass contract before builds. Native loader prerequisites,
completion time, actual root installation and consumer behavior remain unproved.

## Required process owner and fixed CLI

The only CLI argv is `python3 -B trusted_sdk_distribution.py --install`, defined
in [the distribution helper](trusted_sdk_distribution.py). It accepts
no URL, root, archive path, SDK version, environment toggle or selection argument.
Success writes at most 4096 bytes of provenance JSON to stdout. Rejection writes
one fixed closed failure JSON to stderr and returns exit 65, without echoing
arguments, exception text or canaries. Internal `main(argv=None)` supports only
data-level CLI rejection controls; it does not provide another installation kind.

**The parent must enforce an absolute 180-second process-group deadline** using
this fixed argv, reserve kill/reap time, and retain actual terminal/group-exit
facts. DNS, TLS, HTTP header and stream stalls are not fully bounded by Python
inactivity timeouts or monotonic checks alone. The parent must kill/reap on
failure and prohibit all SDK audit/build/consumer dispatch until actual child
success. No portable test claims that external process owner is already wired.
