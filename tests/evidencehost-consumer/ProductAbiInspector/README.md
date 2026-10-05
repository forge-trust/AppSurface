# Product ABI metadata inspector

## Status and purpose

This is a **fresh source reconstruction**, not a recovery of the historical
inspector bytes. The original source compiled successfully. Actual repeated
inspector-image pairs rejected at the unchanged 100000-row bound. After the
serialization correction, actual smaller OfficialTaskHost pairs produced a
bounded report (22567 charged rows; 1208065 bytes), and a wrong final pin rejected
without output. That control established report serialization, not product ABI
compatibility. The generic-shape source then compiled, and the small TaskHost data
control completed at 23237 charged rows. The metadata-blob guard compiled, but
actual pristine five-pair input then rejected at the unchanged row bound before
output. Main subsequently reported that the parsing/index reuse build still
rejected its actual five-pair trial at `Cli/common`: rows100000 and
phase-start-rows99943. This is a cumulative metadata/common-comparison limit;
dependency caching cannot resolve that stage. Main's subsequent pristine partition
trial completed Contracts, Planner, Cli and Aspire, but Coverage rejected at
common rows100000, phase-start-rows86619, with no report. Those four outputs do not
form a complete five-role audit. The Coverage inventory/common split below is
reported by main to have compiled and produced an actual Coverage inventory at
86621 rows/11574740 bytes. Common then reached100000 rows with a post-decoding
phase start of77614. Main counted6290 member rows, so reusing only the index after
decoding both full image copies would not address the cumulative budget. The
compact shared-image inventory v2 below is source preparation only, unbuilt and
unexecuted in this handoff. It retains
the per-process limits rather than making a compatibility or completeness claim.
This is a
standalone, dependency-free `net10.0` program using
[PEReader](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.pereader)
and [System.Reflection.Metadata](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata).
The project disables inherited Directory.Build props/targets so qualification
generation and repository project references do not become hidden dependencies.
The source project introduces no package reference; main owns restore and any
generated lock file.

The inspector reads immutable, explicitly hash-pinned DLL and portable-PDB bytes.
It never loads an inspected assembly, invokes its code, uses runtime reflection,
installs a tool or instruments a file. `System.Reflection` enum constants describe
metadata access/attribute flags; there are no reflective assembly/type/member
lookups. Its own output records use System.Text.Json source generation rather than
reflective serialization. It writes data for a trusted caller's separate binary-reuse review.
The generated context includes both serialization fast paths and metadata for
the synchronous stream serializer's fallback. That metadata is generated for
the inspector's own report records; it does not load or reflect over inspected
assemblies. A successful build does not establish successful report serialization.

Use it to identify a constructor/member signature that disappeared, changed access
flags, declared friend assemblies, missing direct definitions, or mismatched debug
identity. For example, adding an optional fifth argument to an existing four-argument
constructor emits a different signature. An optional parameter is not an emitted
four-argument overload and cannot satisfy its old MemberRef by itself.

Do not use the report as runtime compatibility, Evidence admission, qualification,
Trust, source equivalence or coverage credit. Exact matches remain observations.

## Command and exact input schema

After a separately authorized build, the command is:

```text
ProductAbiInspector --input /absolute/pairs.json --output /absolute/fresh-report.json
```

Only those four arguments in that order are accepted. The output must not exist;
it is created exclusively, with mode0600 on Unix. Main must own and protect its
parent directory. A failed partial output is retained for diagnosis and must never
be treated as a completed report. There is no automatic retry or overwrite.

Input schema `issue779-product-abi-input-v1` has exactly `schema` and `pairs`.
There must be five unique, case-sensitive pair names: `Contracts`, `Planner`, `Cli`,
`Aspire`, `Coverage`. Each pair contains exactly `name`, `baseline`, `candidate`.
Both sides contain exactly `dll`, `pdb`, `dll_sha256`, `pdb_sha256`. Unknown,
duplicate or missing properties are rejected. Paths must be fully qualified and
normalized; SHA-256 pins are mandatory lowercase64-hex strings.

This single-pair fragment describes the shape; the actual array must contain all
five names, with real paths and computed pins:

```json
{
  "schema": "issue779-product-abi-input-v1",
  "pairs": [
    {
      "name": "Contracts",
      "baseline": {
        "dll": "/absolute/pristine/ForgeTrust.AppSurface.Evidence.Contracts.dll",
        "pdb": "/absolute/pristine/ForgeTrust.AppSurface.Evidence.Contracts.pdb",
        "dll_sha256": "<actual lowercase SHA-256>",
        "pdb_sha256": "<actual lowercase SHA-256>"
      },
      "candidate": {
        "dll": "/absolute/private/ForgeTrust.AppSurface.Evidence.Contracts.dll",
        "pdb": "/absolute/private/ForgeTrust.AppSurface.Evidence.Contracts.pdb",
        "dll_sha256": "<actual lowercase SHA-256>",
        "pdb_sha256": "<actual lowercase SHA-256>"
      }
    }
  ]
}
```

Pair names are caller-selected inventory labels, not assembly authority. The tool
requires equal assembly simple names across each pair and records actual metadata
identities. Main must authenticate those identities against its intended fixed
five assemblies and separately bind source/build provenance.

All twenty DLL/PDB pins are verified **before any PE or PDB metadata reader is
constructed**. Parsing then uses those in-memory bytes, so a later path replacement
cannot change the parsed image. File reads reject final symlinks/reparse points,
directories, device attributes, empty/oversized files and changed length/time;
chunks are bounded. These checks are not an inode-custody API or protection against
an adversarial mutable parent directory. Main must supply already-owned regular
files and authenticated physical maps. No uploaded path grants a runtime capability.

After **all twenty pins** have passed, a pair may reuse its baseline `ImageReport`
for the candidate only when both absolute paths, both verified hashes, both byte
lengths, and both actual in-memory byte sequences are equal. Actual-byte equality
is checked in charged 64 KiB chunks under the original deadline. Equal hashes
alone, equal paths alone, or matching metadata fields never enable reuse. A path
difference still causes an independent parse even for equal bytes, so recorded
input paths are never substituted. Both complete image inventories remain in the
output; there is no fabricated pair or omitted metadata.

### Single-pair partition invocation

The same four command arguments accept a second, closed input schema,
`issue779-product-abi-pair-input-v1`, with **exactly** these fields:

| Field | Required value |
|---|---|
| `schema` | `issue779-product-abi-pair-input-v1` |
| `manifest` | Fully qualified, normalized path to the root-selected full five-pair JSON in [the existing input schema](#command-and-exact-input-schema) |
| `manifest_sha256` | Lowercase64-hex SHA-256 of those exact manifest bytes |
| `pair_name` | Exactly one of `Contracts`, `Planner`, `Cli`, `Aspire`, `Coverage` |

The small invocation JSON and the manifest each have the unchanged 128 KiB/depth12
limit. Manifest bytes are read once and their supplied digest is checked **before
the manifest is parsed**. Its schema must be the existing
`issue779-product-abi-input-v1`, with all five unique pairs and all twenty binary
pins; another partition invocation cannot be used as a manifest. Unknown,
duplicate, missing or incorrectly typed properties reject. There is no recursive
manifest selection, relative path, optional pin or uploaded runtime authority.

All twenty DLL/PDB inputs are read and hash-verified under the existing 128 MiB
binary-input total **before any image is analyzed**, including the sixteen files
outside the selected pair. The selected pair then uses the same complete
[`ImageReport` inventory and `CommonReport` comparison](#output-schema-and-observations),
including IL length/hash, generic constraints, declared interfaces, access flags,
portable-PDB documents and sequence points. The same verified-byte reuse and
object-identity index cache apply. No metadata fields or common comparison rows
are omitted to make the partition fit.

Use five independently supervised invocations when the full five-pair mode exceeds
its cumulative row limit. Use the existing full mode when it fits and its direct
dependency reconciliation is wanted in one report. A single-pair result is an
unfinished component of a separate trusted preparation data review, not a
replacement for that review. A selected pair can still exceed the unchanged
100000-row or 32 MiB-output cap; such failure stays failure.

Partition example, requiring actual pins and root-selected paths:

```json
{
  "schema": "issue779-product-abi-pair-input-v1",
  "manifest": "/absolute/full-five-pair-manifest.json",
  "manifest_sha256": "<actual lowercase SHA-256 of manifest bytes>",
  "pair_name": "Cli"
}
```

## Output schema and observations

Output uses lowercase snake_case schema `issue779-product-abi-metadata-v1`:

| Field | Content |
|---|---|
| `input_sha256` | Hash of the exact input JSON bytes |
| `pairs` | The five pairs in fixed Contracts/Planner/Cli/Aspire/Coverage order |
| `direct_dependency_reference_matches` | Each selected core MemberRef compared with direct baseline/candidate Contracts or Planner definitions |
| `unresolved_direct_dependency_rows` | Number of those rows without exactly one match on both sides |
| `charged_rows`, `input_bytes` | Aggregate accounting at report construction |
| `runtime_compatibility_proven`, `coverage_credit`, `qualification_claim` | Always false |
| `limitations` | Explicit unresolved interpretation and authority boundaries |

Each pair contains `name`, full `baseline`/`candidate` inventories,
`dll_bytes_equal`, `pdb_bytes_equal`, and `common` comparison data. Byte equality is
only equality of the supplied pinned binaries; it does not prove their source or
build origin. The caller authenticates those independently.

### Partition report wrapper

Partition output is a separate generated record with schema
`issue779-product-abi-pair-metadata-v1`; the existing
`issue779-product-abi-metadata-v1` record and five-pair output fields remain
unchanged. The partition wrapper has exactly:

| Field | Content |
|---|---|
| `schema` | `issue779-product-abi-pair-metadata-v1` |
| `input_sha256` | Hash of the exact **small invocation** JSON bytes, not the manifest |
| `partition_name` | The selected closed role |
| `manifest_sha256` | The verified hash of the full five-pair manifest bytes |
| `pairs` | Exactly one unchanged complete `PairReport`; its `name` equals `partition_name` |
| `direct_dependency_reference_matches` | Empty array: reconciliation was not performed |
| `unresolved_direct_dependency_rows` | JSON **null**: reconciliation was not performed |
| `charged_rows`, `input_bytes` | Actual per-process accounting, including verification of all twenty binary inputs |
| `runtime_compatibility_proven`, `coverage_credit`, `qualification_claim` | Always false |
| `limitations` | Data-only boundaries and the explicit missing reconciliation |

Null is deliberately serialized, not omitted or changed to zero. An empty match
array with null unresolved count cannot establish that dependencies resolved.
Neither a partition's exit0 nor five exit0 reports establish aggregate completion.
There is no sixth C# reconciliation mode in this source change. The parent owns
the separate bounded reconciliation procedure; it must reject selected-dependency
TypeSpec parents, unresolved/ambiguous direct matches and unsupported access
interpretations as described [below](#canonical-signatures-and-access-boundaries).

The parent must bind all five successful reports to one authenticated manifest,
the exact invocation hashes, the separately selected inspector bytes and actual
exit records. Require one role each, no extra/duplicate role, a matching one-pair
name, actual image paths and hashes matching the manifest, complete inventories
and unchanged claim-false fields. Report files must be digest-verified before
their data is used. A missing, stale, partial, rejected or late partition blocks
the aggregate; no empty table is a substitute.

The parent contract is bounded to five pair jobs at 100000 charged rows each plus
one reconciliation allowance of 100000 rows: at most **600000 rows** total. Its
combined reports, reconciliation and index must fit **32 MiB**, not 32 MiB per
partition multiplied by five. The parent keeps its original 1200-second Runner
deadline and remaining cleanup allowance; every inspector invocation remains
at most30 seconds, bounded by the remaining original allowance. These aggregate
constraints belong to the parent's reviewed implementation and are not enforced
by independent inspector processes. They add no worker write path, unit policy,
capability, runtime authority or renewed execution allowance.

Each image includes:

- Paths and SHA-256 pins; assembly name/version/culture/public-key-or-token bytes
  and flags; module MVID.
- All AssemblyRef and TypeRef records, including scope kind/token; TypeDefs with
  attributes, visibility, base type, declared interface names and actual generic
  parameter rows.
- Method and field definitions with canonical signatures, attributes and access
  flags; method static/implementation flags, IL length/SHA-256, parameter flags and
  raw metadata default constants and method generic parameter rows.
  Properties/events include their accessor method
  tokens; access comes from those method rows, not the property/event flags alone.
- All method/field MemberRefs with canonical declaring type, parent kind and
  signature. These are metadata references, not a statement that every reference
  is an IL callsite or executes.
- Declared `InternalsVisibleToAttribute` constructor type and serialized friend
  string, including any public-key qualification. No friend grant is fabricated.
- Exported type/forwarder observations and implementation kind/token.
- PE debug-directory records and portable-PDB identity, documents and sequence
  points. Document hash algorithm/hash/language are recorded without opening source
  files. Hidden sequence points are explicitly marked.

PDB CodeView GUID, stamp and age1 are compared with the portable-PDB20-byte ID.
`one_matching_code_view` requires exactly one CodeView row and exactly one match.
Checksum observations retain declared algorithm/checksum plus SHA1/SHA256 raw-file
and zeroed-ID hashes and separate match fields. Zeroed-ID hashing replaces only the
portable PDB header's20-byte ID with zeros. The report does not automatically select
one checksum interpretation as an integrity policy. Unsupported algorithms remain
unresolved. The caller must inspect debug identity and checksum observations rather
than infer correctness from a path or matching MVID alone.

### Canonical signatures and access boundaries

Canonical signatures preserve method header/calling convention, instance/static
signature bit, generic arity, required parameter count, return/parameter types,
class/value kind, generic parameter positions, arrays including rank/bounds,
pointers/byrefs, function pointers, pinned types and required/optional modifiers.
Type names include their assembly **simple name** and nested type hierarchy.
AssemblyRef full identities are recorded separately: signature text equality does
not resolve version/culture/public-key binding or unify type identities.

TypeDefs and every member definition carry `generic_parameters`. Each row has
`position` (the actual GenericParam index), `name`, numeric `attributes`, and
`constraints`, a list decoded from actual GenericParamConstraint type handles
through the same canonical signature provider. This includes variance and special
constraint flags; method arity alone is not treated as equivalent generic shape.
Fields, properties and events have an empty list; their declaring type's parameters
remain in its TypeDef row. Every parameter and constraint is charged before its
metadata is read or appended.

`common.common_types` reports shared TypeDef names and `attributes_equal`,
`base_type_equal`, `interfaces_equal`, `generic_parameters_equal`, and
`generic_parameter_names_equal`, with both access observations.
`common.candidate_members` compares each candidate definition against exact-key
baseline definitions and retains all matching access, static and implementation
flags. It now also records `candidate_impl_attributes`. Each `baseline_matches`
row adds `attributes_equal`, `static_equal`, `impl_attributes_equal`,
`generic_parameters_equal`, and `generic_parameter_names_equal`.
`common.baseline_only_members` records baseline
signatures absent from the candidate. Added signatures can have zero baseline
matches without being malformed; removals and changes require caller review.

The equality observations compare emitted lists in metadata order, including
positions, flags and decoded constraint types. They conservatively report false
if ordering differs; they do not prove semantic constraint or interface
equivalence. Parameter names have their own equality observation and are excluded
from shape equality because renaming a parameter does not change its emitted
signature. Each compared parameter/interface/constraint is charged even after an
earlier mismatch. Runtime constraint satisfaction, inherited interfaces and
virtual/interface dispatch remain unresolved.

Direct dependency rows examine both baseline and candidate core references against
both dependency definition inventories. A key includes declaring type, member kind,
name and full canonical signature. Matching definitions' access and static flags
are observations; no inherited-member search or effective access decision occurs.
TypeSpecification parents conservatively mentioning a selected dependency are
reported unresolved because generic substitution is not implemented. Forwarding,
inherited definitions, missing direct definitions and ambiguous matches are not
silently accepted. The trusted root caller **must fail its binary-reuse data review
when `unresolved_direct_dependency_rows` is nonzero**, then investigate source or
emitted MemberRefs instead of issuing an automatic compatibility verdict.

Declared friend names alone cannot establish effective internal access. Strong-name
rules, enclosing type visibility, inheritance, generic constraint satisfaction/substitution,
virtual/interface dispatch, module references, forwarders, loader binding and actual
runtime behavior remain outside this inspector. IL hashes preserve original token
bytes and do not prove normalized IL equality across rebuilt token layouts. Sequence
points and IL bytes are not a reconstructed branch map or permission for coverage
credit. No source substitution, `#line` trick, reflection or new friend assembly is
part of this tool.

## Contracts and Coverage inventory/common split

The inventory/common modes accept exactly the case-sensitive roles `Contracts`
and `Coverage`. They split cumulative metadata/common work while retaining the
old full/pair schemas, metadata, generic constraints, interfaces, access flags,
IL hashes, PDB documents and sequence points. The selected name binds the manifest
pair, both report wrappers and both common progress checkpoints.

The retained mixed Contracts pair failed at100000 rows, with common beginning at
79222. Unlike pristine same-image pairs, its private candidate requires a separate
complete inventory. This generalization permits that inventory and its existing
common comparison to run in separate bounded jobs. A successful report still
requires the actual supervised exit and final digest; no fit or compatibility is
inferred from source accounting.

### Inventory stage

Input schema `issue779-product-abi-inventory-input-v1` has exactly `schema`,
`manifest`, `manifest_sha256`, `pair_name`. The manifest follows the existing
[partition-input constraints](#single-pair-partition-invocation): absolute path,
mandatory digest,128 KiB/depth12, complete old five-pair schema. Its hash is
checked before parsing; **all twenty binary pins pass before PE/PDB readers**.
The selected pair's baseline/candidate images retain full original parsing and the existing
verified-byte reuse condition. No common/index work is performed.

Output schema `issue779-product-abi-inventory-metadata-v2` has exactly:
`schema`, `input_sha256`, `partition_name`, `manifest_sha256`, `pairs`,
`direct_dependency_reference_matches`, `unresolved_direct_dependency_rows`,
`charged_rows`, `input_bytes`, `runtime_compatibility_proven`, `coverage_credit`,
`qualification_claim`, `limitations`.
`pairs` contains exactly one object: `name`, `baseline`, `candidate`,
`dll_bytes_equal`, `pdb_bytes_equal`, `candidate_is_baseline`, `common`. Name is
the selected `Contracts` or `Coverage`; common is JSON null, dependencies empty, unresolved count null, and
all three claims false. This is an unfinished inventory, not an old pair report
with a fabricated successful common table.

`baseline` is always the complete unchanged `ImageReport` object. Exactly one of
these candidate forms is valid:

- `candidate_is_baseline: true`, `candidate: null`: the logical candidate is the
  **entire actual baseline object**, including every access/generic/IL/PDB/source
  path/token/document/sequence field. The producer emits this only when the
  existing `SameVerifiedImage` passed all twenty pins plus equal DLL/PDB paths,
  verified hashes, lengths and charged actual-byte comparisons, and the candidate
  is the actual same object reference. Flags are both true. No metadata is lost;
  v2 encodes an explicit full-image reference instead of duplicating the graph.
- `candidate_is_baseline: false`, nonnull `candidate`: the complete actual
  candidate image is retained separately, with all original fields. Different
  paths, pins, lengths or bytes never select shared form, even when a subset of
  metadata appears equal. There is no record/value equality or hash-only gate.

Old full/pair reports and all their record declarations remain unchanged. The
inventory input schema stays v1, but this producer now emits metadata **v2**.
Common-only accepts only v2; the retained v1 trial remains historical and is not
silently reinterpreted. Separate generated V2 record types retain the prior
record definitions without adding null fields to legacy full/pair outputs.

### Common stage

Input schema `issue779-product-abi-common-input-v1` has exactly `schema`,
`inventory`, `inventory_sha256`, `manifest`, `manifest_sha256`, `pair_name`.
Both paths are absolute/normalized and both digests mandatory lowercase64-hex.
The manifest is capped at128 KiB; the retained inventory at32 MiB. Each digest is
checked **before its JSON is parsed**. The manifest must contain all five pairs.
Common-only checks the selected retained image paths/hashes against it, equal pair
assembly names and the hash-equality flags. It does not reread binaries or
construct PE/PDB readers. The root caller must separately bind the authenticated
inventory producer, actual exit0, input invocation, inspector bytes and report
digest. Uploaded data/hash equality is not execution or runtime authority.

Before DOM construction or source-generated typed deserialization, a bounded
`Utf8JsonReader` walk charges every record object, array/list container and scalar
list item to the same100000-row budget. This includes all encoded image members,
parameters/generics, interface/constraint strings, accessors, AssemblyRefs,
TypeRefs/MemberRefs, debug/checksum/document and every sequence-point row.
Each token checks the original deadline. Depth remains12; text is bounded before
copying, allowing8192 decoded characters for the hex of a legal4096-byte blob.
Original metadata-parser text/blob bounds are unchanged. Every nested record's
required members/list shapes are checked, including duplicate/unknown/missing
fields and invalid nulls. PDB documents remain capped at4096 per invocation.

The generated `InventoryMetadataReportV2` deserializer reconstructs every encoded
original `ImageReport` field. Common/unresolved must be null, dependencies empty, claims
false, role/name/count exact, and producer accounting within its limits. Then the
**unchanged `Comparisons.Common`** uses those complete actual inventories and
original index/duplicate/cardinality/interface/generic comparisons. Before
comparison, shared form requires null candidate, both flags true, and exact
manifest equality of both DLL/PDB paths **and** both hash pins; the full baseline
is bound against both manifest sides. Only then does common use that exact object
for both logical images. Unshared form requires a nonnull, fully validated and
manifest-bound candidate. Invalid flag/null/pin combinations reject before Common.
The root caller still must authenticate the actual producer and its successful
exit; flags and manifest alone are not proof of actual producer byte checks.

Every actually encoded record/container/item is charged before materialization.
Shared form charges the complete graph once because only one graph is encoded;
it does not decode a second graph and silently discount its cost. The original
reference-identity index cache then shares the one actual index. All original
common loops and actual signature buckets/duplicate match cardinality remain.
The checkpoint resets after decoding/validation, immediately before comparison.
Comparison charges are additional to all decoding charges. No deep-list shortcut,
metadata-name cache or Python comparison port is added.

Output schema `issue779-product-abi-common-metadata-v1` has exactly `schema`,
`input_sha256`, `inventory_sha256`, `manifest_sha256`, `partition_name`, `common`,
`charged_rows`, `input_bytes`, `runtime_compatibility_proven`, `coverage_credit`,
`qualification_claim`, `limitations`. `common` remains the actual `CommonReport`
with `common_types`, `candidate_members`, `baseline_only_members` and unchanged
nested fields. Input hash binds the small invocation; input bytes count the
retained inventory bytes, not purported DLL reads. All three claims are false.
Dependency reconciliation is still separate; no resolution verdict is emitted.

### Aggregate ordering and control plans

The mixed parent retains/digest-binds Contracts inventory/common, Coverage
inventory/common and the three ordinary Planner/Cli/Aspire pair reports. Its
separate Python child has two independently bounded100000-row phases. Missing,
failed or late stages block completion. Seven inspector processes plus that
Python child give **eight processes and at most900000 charged rows**: seven
100000-row inspector jobs plus two100000-row Python phases. Combined reports,
reconciliation and index stay **32 MiB**.
The original1200-second Runner deadline and cleanup allowance remain; every
inspector retains30 seconds,100000 rows and32 MiB output. This parent-selected
split supersedes earlier600000/700000-row layouts only in its reviewed adapter.
Actual new-stage fit is unverified. No worker write path, permission, unit
property, runtime cap, acceptance policy or execution allowance is changed.

Required parent controls, **not run by this source handoff**:

- Genuine Contracts and Coverage inventory/common; compare common data with the original pair
  mode on a separately selected small fixture that fits.
- Wrong manifest digest, selected/nonselected binary pin or role outside Contracts/Coverage
  rejects before metadata/output.
- Wrong inventory digest, unknown/duplicate/missing fields, wrong count/name,
  nonnull common/unresolved, dependencies, true claims or mismatched image pins
  reject without usable output.
- A100001-row token walk,4097 documents, over-depth JSON, oversized report/text
  or malformed nested list rejects before typed comparison.
- Parent rejects changed/expired/failed producer receipts independently of parsed
  data. Preserve failures and partial output; no empty table substitutes for work.
- Verify actual generated serialization and late-publication behavior; compilation
  alone establishes neither.
- Shared v2 positive uses genuine same-path/same-pin/same-byte inputs and must
  retain complete baseline metadata and actual duplicate signature buckets.
  True with a nonnull candidate, false with null, mismatched manifest candidate
  path/hash, false byte flags or wrong schema reject. Genuine distinct image paths
  retain the full unshared candidate even when binary hashes happen to match.
  Check genuine common output against the ordinary full representation on a small
  fixture. These are planned data controls, not executed proof of fit or authority.

## Bounds, failures and supervision

| Bound | Value |
|---|---|
| Input JSON | 128 KiB; depth12; partition manifest independently has the same cap |
| Retained inventory JSON, common-only | 32 MiB; depth12; fully charged before typed materialization |
| Each DLL | 32 MiB |
| Each portable PDB | 16 MiB |
| Aggregate DLL/PDB bytes | 128 MiB, counting repeated pair inputs |
| Output | 32 MiB, enforced during streaming serialization before writes |
| PDB documents | 4096 across the complete invocation |
| Aggregate charged rows | 100000 across all parsing/indexing/comparison work in either mode |
| Metadata text/blob | 4096 characters/bytes per bounded field |
| Type/signature nesting | 64 for recursive type names/TypeSpecs |
| Original elapsed allowance | 30 seconds |

Accounting charges every metadata/comparison/index/match row **before** adding it
to a list or dictionary. Every direct match is charged individually; there is no
uncharged Select/ToArray materialization. Signature provider operations also consume
the same row budget. The cap can reject a large legitimate input; it is never raised
automatically. Output serialization streams through a cap rather than first
allocating an unbounded JSON string. Charged rows are not an estimated method count.
Assembly public keys, AssemblyRef keys/tokens, metadata constants and portable-PDB
document hashes use `GetBlobReader(handle).Length` to reject blobs above 4096 bytes
**before** allocating/copying their bytes or converting to hex. The existing hex
guard remains a second check. This preserves the recorded field shape and uses the
original elapsed budget; no input, output or row limit is raised.

One invocation owns one `Comparisons.IndexCache`, keyed by `ImageReport` **object
reference identity** using `ReferenceEqualityComparer.Instance`, not record/value
equality. `Get` charges every lookup (including a cache hit) and a new cache entry
before constructing it. The original index builder still charges every member,
bucket, and append. At most ten distinct parsed images can be cached. A reused
image has one index shared by common and dependency comparisons; no index is
shared between different image instances. Parsed inventories and cached indexes
are not mutated after construction. Every common/dependency comparison and emitted
match remains charged and retains its original order. Dependency-reference
classification caching is not implemented.

Exit0 means only that a bounded data report was written and the elapsed check after
file close passed. It may still contain unresolved matches, changed binaries or
debug-identity mismatches. Exit1 reports a fixed category on stderr; native exception
text and paths are not echoed. Failure stderr appends a closed `;stage=` label:
`input`, `input-pins`, `metadata-and-comparison`, `output-create`,
`output-serialize`, `output-flush-close`, or `publication`. The label marks the
operation being attempted, not its successful completion or a causal diagnosis.
For failure during `metadata-and-comparison`, stderr also contains the last fixed
pair/phase checkpoint and numeric accounting:
`;pair=Contracts;phase=baseline;rows=100000;phase-start-rows=123`.
Pair labels are only `Contracts`, `Planner`, `Cli`, `Aspire`, or `Coverage`; phases
are only `baseline`, `candidate`, `common`, or `dependency`. Checkpoints are charged
when entered, and dependency-result counting updates the caller checkpoint too.
The diagnostic contains no paths, arbitrary names, messages, or success status;
it is produced only on failure and is not part of the data report schema.
Output serialization failure retains its partial file and returns exit1.
A late/truncated/partial output remains unusable;
the outer caller must collect the actual exit and bind the final report hash.

The30-second checks are cooperative. Main must supervise the entire process with
an absolute deadline and owned process-group cleanup to bound blocked filesystem or
parser calls. The tool is not a kernel sandbox or hostile-file execution boundary.
No root unit, registry, grant, scope lease, worker permission or acceptance policy
is changed. Successful inspection alone cannot authorize swapping baseline binaries
into a private executable or claim genuine coverage.

## Handoff

The three source files are frozen and mirrored byte-for-byte into the assigned
ignored recovery backup directory with private700/600 modes. Historical source
hashes are not claimed. Parent owns the next bounded build and metadata data
control. This source correction used no build, test or inspected-code execution.
The 100000-row cap and generated serialization-plus-metadata modes remain
unchanged. The original five-pair output schema and every nested image/common
record remain unchanged; the explicitly separate partition input/output wrappers
above require their own caller schema binding. No default-mode pass or partition
execution is claimed by this source handoff.
