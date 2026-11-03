# Issue 779 audit failure retention helper

This isolated helper preserves diagnostics from an already completed or failed audit attempt. It is not
an audit runner, test-result interpreter, coverage collector, native proof, or acceptance decision.

## Fixed interface

`preserve(source_root, destination_parent, destination_name, *, destination_uid, destination_gid)` reads only
these relative source paths:

| Relative path | Required | Maximum |
| --- | --- | ---: |
| `application-bundle-driver.log` | yes; an empty regular file is valid | 8 MiB |
| `application-bundle-tests/application-bundle.trx` | no; absence is recorded as absent | 1 MiB |
| `source.json` | yes | 1 MiB |

The caller supplies the already trusted fixture roots, a fresh one-component destination name, and the
destination ownership. The helper does not discover paths or accept alternate filenames. Root paths are
opened component by component without following symlinks. Each source is opened without following symlinks,
must be a regular single-link file, is read under its byte limit, then checked again for stable descriptor
metadata and name-to-inode identity. Source bytes are never truncated or rewritten.

The new diagnostic directory is created exclusively with mode `0700`; retained files and the structured
summary use mode `0600`. Existing destinations fail closed. The summary contains only fixed relative names,
presence, byte counts and SHA-256 values. It explicitly records `native_claimed`, `gate_pass_claimed`, and
`accepted` as false; those fields are not evidence of any test or gate outcome. Raw bytes are kept only in
the private retained files, not repeated in the summary or errors.

## Use and limitations

Use this only after the named audit output exists, when a rejected run leaves useful diagnostics outside
its ordinary retention path. Check the saved file hashes before interpreting content through the separate
measurement workflow. An absent TRX remains absent; do not infer that tests ran or passed. The helper does
not copy report XML, modify product limits, establish file ownership of its source, or grant native or
acceptance authority. The caller supplies the intended owner/group IDs and must provide a trusted parent
directory that permits exclusive creation; the helper applies those IDs to the retained directory and files.
Failures are reported without a success summary.

Run the data-only helper tests with:

```sh
python3 -B -m unittest test_retain_audit_failure.py
```
