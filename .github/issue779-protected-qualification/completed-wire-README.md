# Completed-run wire handoff

[`completed_wire.py`](completed_wire.py) serializes the already constructed
`CompletedPreparation` and `CompletedRunReplay` values as strict canonical JSON.
It is intended for a narrow boundary where credentialed provider/source preparation
hands ordinary comparison data to a separate replay process, then receives replay
observations for closeout. The codec carries no credential and is not a trusted
source of provider, filesystem, execution, or admission facts.

## API

```python
raw = encode_preparation(preparation)  # bytes, <= 16 MiB
preparation_copy = decode_preparation(raw)

raw = encode_replay(replay)            # bytes, <= 16 MiB
replay_copy = decode_replay(raw)
```

Preparation uses schema `issue779-completed-preparation-v1` and explicitly carries
the completed provider stage (run facts, jobs and 37 artifact bindings), source
binding, full parsed archive/member inventory, stage/source paths, and four root
path plus six-integer node identity tuples. Replay uses schema
`issue779-completed-replay-v1` and carries run identity, the fixed eight ordered
phase observations, retained output paths, and gate job id. Each projection is
embedded as strict base64 so the original projection bytes and digest survive the
handoff exactly. The codec performs explicit field-by-field reconstruction; it
does not serialize arbitrary dataclasses or import types named by input.

The wire format is UTF-8 canonical JSON with sorted keys, compact separators,
ASCII escaping, exact byte-for-byte canonical checks, duplicate/case-alias key
rejection, finite JSON types only, maximum nesting depth 20, at most 1,000,000
nodes, and 16 MiB total size. Phase projection bytes are limited to 1 MiB each.
Paths must be canonical absolute lexical paths, but decoding does not touch the
filesystem. Archive/member counts and byte totals follow the completed-run
inventory caps: exactly 37 artifacts, at most 8,192 members per archive, at most
512 MiB expanded per archive, and only stored/deflated ZIP compression methods.
Member names use the same safe relative-name grammar as archive inspection. The
source-owned stage/provider/job validator and C1 inventory projection matcher
check the full data links during encode/decode; neither check reads staged files.

## Decisions and limits

The codec preserves typed immutable handoff values while remaining independently
portable. Decoding checks shape, digest syntax, source/run/inventory attribution,
phase order, and rejects every key in the controller's complete claim set whenever
it appears recursively with a value other than literal `false`. The replay envelope
also retains its explicit top-level false-claim fields. The codec does **not** revalidate provider
metadata, source captures, archive contents, path existence, ownership, inode
identities, phase semantics, successful workflow execution, gate eligibility, or
physical process settlement. The consumer must authenticate those facts using
the existing source-owned preparation/closeout APIs and its own filesystem/process
boundary. In particular, a six-integer root identity is just serialized metadata
until the caller pins and checks the actual node.

Use the preparation envelope only between the fixed preparation and replay
components. Use the replay envelope only as the output of the fixed replay
controller. Do not accept caller-supplied callbacks, paths, commands, module names,
or arbitrary mappings through this codec. Do not interpret a decoded false claim
as proof that any stage ran; false fields are included to make authority claims
explicitly unavailable in the handoff.

## Pitfalls

- Re-encoding is canonical and deterministic; noncanonical whitespace/key order,
  duplicate keys, nonfinite constants, floats, oversized data, and excessive
  depth reject.
- `bool` is not accepted where an integer is required, despite Python's `bool`
  subclassing `int`.
- Replay phase bytes are base64 encoded verbatim. Parsing and reconstructing a
  projection before encoding would change the bytes being compared.
- Paths and node identities are not filesystem handles. Never skip the consumer's
  fresh `lstat`/open/inode checks because a decoded identity matches an earlier
  value.
- This handoff does not mark validators as executed or establish original workflow,
  tests, merge, gate success, qualification, native acceptance, or admission.

Portable codec tests:

```sh
python3 -B -m unittest discover -s .github/issue779-protected-qualification -p 'test_completed_wire.py'
```
