# EvidenceHost producer example

This is a consumer-side C# excerpt using the public Coverage, Evidence.Aspire, and Evidence.Contracts types. Compile it in the consumer's EvidenceHost project, which references those packages. The dogfood CLI adapter itself stays headless and does not reference Aspire.

```csharp
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

public static class EvidenceCoverageFanInProducerExample
{
    public const string ProducerId = "coverage-fanin";
    public const string AssertionId = "coverage.accepted";
    public const string ResultSlot = "result";

    // Include this declaration in the selected profile and require its assertion in the obligation.
    public static EvidenceProducerDeclaration Declaration { get; } = new(
        ProducerId,
        "coverage",
        "1.0.0",
        RequiredResources: [],
        AssertionIds: [AssertionId],
        ArtifactSlots:
        [
            new EvidenceArtifactSlot(
                ResultSlot,
                "coverage",
                "application/json",
                Required: true,
                MaximumBytes: 1_048_576)
        ],
        TimeoutSeconds: 900,
        CoverageGate: new EvidenceCoverageGateRequirements(
            MinLinePercent: 95,
            MinBranchPercent: 85,
            MinPatchLinePercent: 95,
            MinPatchBranchPercent: 85,
            PatchLineMode: "codecov",
            TolerancePercent: 0.5m));

    public static EvidenceCoverageFanInProducer Create(CoverageAcceptRequest currentRunRequest) =>
        new(
            ProducerId,
            currentRunRequest,
            AssertionId,
            ResultSlot,
            requirePatch: true,
            requireObservedPrimary: true);

    // Register only after resolving a plan whose selected profile contains Declaration.
    public static EvidenceHostBootstrap CreateHost(
        EvidencePlan resolvedPlan,
        CoverageAcceptRequest currentRunRequest) =>
        EvidenceHostBootstrap.Create(
            resolvedPlan,
            registration => registration.AddProducer(Create(currentRunRequest)));
}
```

The patch-required adapter accepts only the current trusted run, an observed primary collection, completed independent acceptance, and successful result delivery. A trusted-producer primary or historical replay leaves the obligation unsatisfied. Keep the declaration's thresholds and patch mode aligned with the accepted request policy.
