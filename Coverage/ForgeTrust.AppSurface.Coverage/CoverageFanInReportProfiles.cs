using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Coverage;

/// <summary>Report profiles supported by the version 3 coverage fan-in contract.</summary>
public static class CoverageFanInReportProfiles
{
    /// <summary>Uses the original Coverlet Cobertura record identities without compiler-symbol pairing.</summary>
    public const string CoverletCobertura = "coverlet-collector-cobertura-v1";

    /// <summary>Pairs matching async observations only when the retained compiler symbols prove their identity.</summary>
    /// <remarks>The profile preserves the original primary aggregate gate and every branch denominator.</remarks>
    public const string CoverletCoberturaAsync = CoverageFanInCompilerSymbols.ReportProfileId;
}
