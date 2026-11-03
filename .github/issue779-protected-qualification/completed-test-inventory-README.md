# Retained solution-test inventory

`completed_test_inventory.inspect_completed_test_inventory(stage, inventory,
stage_directory, source_capture, source_root, *, deadline)` performs a bounded,
read-only check of the retained solution-test evidence in the checkpoint-4
coverage archive. It returns `CompletedTestInventory`, with one
`ProjectTestInventory` per discovered test project and SHA-256 bindings for
the source solution, timings, Cobertura XML, project logs, and JUnit XML.

The API requires the typed `CompletedRunStage` and
`ParsedCompletedRunInventory` returned by the existing completed-run evidence
pipeline. It rechecks the exact staged directory, archive checksum, full ZIP
directory projection, source-capture binding, and captured source tree before
reading selected entries. Source files are validated by the trusted C1 binding
helpers; the solution selection rule mirrors the current `CoverageRun`
`IsDiscoveredTestProject` name suffixes (`Tests`, `.Tests`, and
`.IntegrationTests`) over `.csproj` project entries in
`ForgeTrust.AppSurface.slnx`.

For each project, the validator derives the exact slug using the current
`CoverageRun` rule: sanitize the project filename stem, then append the first
eight lowercase hexadecimal digits of SHA-256 over the uppercase normalized
absolute project path. It checks that slug in the timing row, project log and
coverage paths, and the exact `junit-coverage-{originalIndex+1}-{slug}.xml`
name. This prevents a self-consistent timing/JUnit/log rename from changing
the source project identity.

The retained `timings.json` must describe the fixed solution/configuration and
collector run, input-order schedule, parallelism 2, a successful solution
build and merge, and one completed exit-code-zero project row with produced
coverage and parsed JUnit metadata for every source-discovered test project.
The `coverageFiles` and per-project `coverageArtifactStatus=produced` fields
are retained declarations from `timings.json`; the original archive retains
only merged Cobertura XML, so this validator does not claim to inspect each
project's individual coverage XML.
Each corresponding JUnit XML is parsed with DTD/entity declarations rejected,
UTF-8-only decoding, a closed XML element hierarchy, bounded size/depth/node/
test-case counts, and suite counter consistency checks.
Skipped tests are counted. Failures and errors reject the inventory. Project
logs are only retained and hashed; their text is never treated as a success
signal.

The portable public-API test uses a synthetic typed 37-archive stage and
replaces only the trusted source bootstrap with a fixture. It still exercises
the production raw archive read, ZIP projection comparison, source-capture
binding, selected-member bounds, and timing/JUnit/log validation. It is a
mechanism test and makes no claim about provider or protected workflow state.

## Decision and limits

This module establishes a retained-data relationship between the captured
source solution, the timing inventory, and JUnit/log artifacts. It does not
launch `dotnet`, invoke the coverage CLI, evaluate coverage percentages, prove
the workflow/job completed successfully, establish a merge or gate decision,
or create admission/qualification. Those booleans remain literal `False` in
the result. In particular, a successful-looking `timings.json` is not by
itself proof of workflow success.

The artifact upload uses `if: always()`, so presence of a coverage archive
does not imply a successful original run. Authentic run 40483696166 contains
three nonzero test-project exit codes and does not retain the JUnit members
described by its timing JSON; this API must reject it. Synthetic fixtures test
the parser and contract only and are not evidence of a protected run.

## Pitfalls

- Pass the same finite absolute monotonic deadline through the stage and
  source verification path. Expired or malformed deadlines fail closed.
- Do not infer project inventory from archive filenames or trust log text.
  The source-bound `.slnx` selection and unique timing/JUnit project rows are
  the inventory anchors.
- The source path is fixed to the original Linux workflow mount used by the
  captured run. A replay clone at another location needs a separate explicit
  design; this API does not normalize machine paths.
- JUnit `skipped` cases are reported, not counted as failures. A missing file,
  parser status other than `parsed`, nonzero project exit, nonzero merge, or
  missing generated coverage rejects the retained inventory.
- `retained_project_inventory_verified` refers only to the checks in this
  module. It is not a credential, gate result, workflow conclusion, or
  authorization token.

Run the portable parser and contract tests with:

```sh
python3 -m unittest discover -s .github/issue779-protected-qualification -p 'test_completed_test_inventory.py'
```
