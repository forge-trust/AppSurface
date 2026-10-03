# Private qualification subject

[QualificationSubject.csproj](QualificationSubject.csproj) is a standalone `net10.0`
xUnit 2 subject. It has the predictable assembly name
`EvidenceHost.PrivateQualificationSubject`, no Evidence `ProjectReference`, and no
solution dependency. The protected producer can use the root-selected absolute
path to this `.csproj` as its `Solution` value.

## Actual behavior measured

[tests/QualificationSubjectTests.cs](tests/QualificationSubjectTests.cs) contains
six theory cases calling the public calculation in the ordinary non-test
[QualificationCalculation library](calculation/QualificationCalculation.csproj):

- Quantities one and three use the ordinary four-unit price.
- Quantities four and eight use the three-unit bulk price, exercising the exact
  threshold and a larger order.
- Zero and negative quantities throw `ArgumentOutOfRangeException` with the
  expected parameter name.

[FixedPriceQuote.CalculateTotal](calculation/FixedPriceQuote.cs) is public and lives
only in the library assembly `QualificationCalculation`. The library has no
package or project dependencies and explicitly sets `IsTestProject=false`.
The test project removes `calculation/**/*.cs` from its own compile items and
references the library; therefore the calculator is not also compiled into the
test assembly. No `InternalsVisibleTo`, collector setting, inclusion/exclusion
filter or producer argument change is required. Default Coverlet behavior can
measure this non-test assembly while excluding the test assembly.

The nonpositive and bulk conditions are actual executable branches with both
outcomes exercised by the defined cases. The calculation returns real results;
assertions examine those results and the actual exception. There is no process
launching, reflection, tool metadata reading, report writing or fabricated
coverage XML. The real coverage producer must restore and execute these tests
and collect their actual coverage before any execution or coverage result exists.

## Source layout and protected ordering

The parent copies this fixture's contents into a separate protected subject source
root, preserving both the `tests` and `calculation` subdirectories. The planner's changed path relative to
that subject source root is exactly:

```text
tests/QualificationSubjectTests.cs
```

The parent also copies reviewed central build/package metadata from the pinned
source into the isolated subject layout. In particular, the nearest
`Directory.Packages.props` must retain the package versions below. Any copied
`Directory.Build.props`, `Directory.Build.targets` or SDK-selection metadata is
part of the independently bound subject inputs, rather than an implicitly trusted
build hook.

Copying bytes is preparation. **Do not restore, evaluate MSBuild, build, discover
or run the subject before the real supervisor is armed.** The protected producer
owns the actual `dotnet test` invocation and its restore/build under the restricted
subject identity, process accounting, deadline and output quota. A `.csproj`
`Solution` value does not exempt restore/assets from this ordering.

## Locked dependencies

The project uses the existing central versions without local version overrides:

| Package | Central version |
| --- | --- |
| `coverlet.collector` | `10.0.1` |
| `Microsoft.NET.Test.Sdk` | `18.5.1` |
| `xunit` | `2.9.3` |
| `xunit.runner.visualstudio` | `3.1.5` |

[packages.lock.json](packages.lock.json) preserves the complete 14-package graph
and content hashes from the existing
[RuntimeSubject lock](../RuntimeSubject/packages.lock.json), with one additional
no-dependency `qualificationcalculation` project node for the new reference.
The library's [empty lock](calculation/packages.lock.json) records a `net10.0`
framework with no packages; it is an exact copy of the existing
[NativeHttpResource empty lock](../NativeHttpResource/packages.lock.json).
`RestoreLockedMode` is enabled in both projects. These are source-prepared lock
data, not the output of a restore of this fixture; the actual protected restore
must verify them with the copied central metadata. No audit/warning suppression
or package-node regeneration is requested. No additional package, test helper,
Evidence assembly or friend identity is needed.

## Scope

This subject provides executable lines and branches for the separately reviewed
[private metadata formatter](../PrivateQualificationMetadata/README.md). It cannot
select a compiled catalogue, issue admission, authenticate a worker, establish
HTTP readiness, grant authority or supply qualified proof. Test success and
coverage remain subject outcomes; root-owned execution and qualification facts
must be established independently. Source preparation alone is not a native,
Trusted or protected acceptance result.
