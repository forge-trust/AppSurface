# Issue 779 root-file failure diagnostics

The [C3 application workflow](../workflows/issue779-checkpoint3-application.yml) preserves a small
diagnostic artifact when the root-file collector step fails. The helper does not rerun the collector,
interpret its outcomes, merge coverage, or establish native or gate acceptance.

`retain_root_file_failure.preserve(source_root, destination_parent, destination_name, *,
destination_uid, destination_gid)` reads only `root-file-driver.log`, optional
`root-file-tests/root-files.trx`, and required `source.json`. Source roots and child files are opened
without following symlinks. The log is streamed under a 32 MiB input ceiling; only its final 256 KiB
are retained. The optional raw TRX is retained unchanged up to 1 MiB, and the source capture unchanged
up to 1 MiB. File identity, type, link count and size are checked before and after reading. A fresh
private destination is created exclusively with mode `0700`, and retained leaves and summary use
`0600`.

The summary records source and retained byte counts and hashes, plus literal false values for native,
gate and acceptance claims. Missing log or TRX files are recorded as absent. Invalid paths, oversized
inputs, substitutions and I/O failures reject preservation; the helper never copies an arbitrary
failure tree. The original workflow step remains failed, and this artifact cannot turn it into a pass.

The main [retention checker](check-retention.py) still enforces the unchanged 8 MiB default leaf cap,
32/40 MiB report caps, 135-leaf count and 96 MiB aggregate limit. It now emits a fixed reason code and
either a known relative leaf path or a short hash alias for an unrecognized name. These diagnostics
identify a failed predicate without changing the accepted limits.

Run the focused data-only tests with:

```sh
python3 -B -m unittest test_retention_diagnostics test_retain_root_file_failure test_retention_supplement
```
