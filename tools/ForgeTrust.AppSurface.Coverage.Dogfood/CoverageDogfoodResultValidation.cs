using System.Text.Json;
using ForgeTrust.AppSurface.Coverage;

/// <summary>Validates the installed CLI's current v3 acceptance and verification result envelope.</summary>
internal static class CoverageDogfoodResultValidation
{
    internal static void RequireV3AcceptedResult(string path, string operation, CoverageGateMode gateMode)
    {
        if (operation is not ("accept" or "verify"))
            throw new InvalidDataException("The v3 result operation is not an acceptance or verification operation.");

        using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        var result = root.GetProperty("result");
        var patchRequired = gateMode == CoverageGateMode.AggregateAndPatch;
        if (gateMode is not (CoverageGateMode.AggregateOnly or CoverageGateMode.AggregateAndPatch)
            || root.GetProperty("schemaVersion").GetInt32() != 3 || root.GetProperty("operation").GetString() != operation
            || root.GetProperty("status").GetString() != "succeeded"
            || result.GetProperty("outcome").GetString() != "succeeded"
            || !result.GetProperty("verified").GetBoolean()
            || operation == "accept" && !result.GetProperty("accepted").GetBoolean()
            || result.GetProperty("patchOutcome").GetString() != (patchRequired ? "PatchAccepted" : "PatchNotRequested"))
            throw new InvalidDataException("V3 result did not include a successful, verified current outcome for its gate mode.");

        var hasApplicability = result.TryGetProperty("patchApplicability", out var applicability);
        if (patchRequired && (!hasApplicability || applicability.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("The v3 CLI accepted result omitted its typed patch applicability claim.");
        if (!patchRequired && hasApplicability && applicability.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("The v3 aggregate-only result included an unrequested patch applicability claim.");
    }
}
