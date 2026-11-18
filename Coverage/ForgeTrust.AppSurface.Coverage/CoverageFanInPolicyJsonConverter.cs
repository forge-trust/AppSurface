using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Coverage;

/// <summary>Preserves the closed v2 policy wire shape while exposing v3 selectors when requested.</summary>
internal sealed class CoverageFanInPolicyJsonConverter : JsonConverter<CoverageFanInPolicy>
{
    public override CoverageFanInPolicy Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        var wire = JsonSerializer.Deserialize<CoverageFanInPolicyV3Json>(ref reader, options)
            ?? throw new JsonException("Coverage policy cannot be null.");
        return new CoverageFanInPolicy(wire.Source, wire.Run, wire.TestProjects, wire.Supplements, wire.Gate,
            wire.Instrumentation, wire.Limits)
        {
            ContractVersion = wire.ContractVersion,
            ReportProfileId = wire.ReportProfileId
        };
    }

    public override void Write(Utf8JsonWriter writer, CoverageFanInPolicy value, JsonSerializerOptions options)
    {
        if (value.ContractVersion == 2)
        {
            JsonSerializer.Serialize(writer, new CoverageFanInPolicyV2Json(value.Source, value.Run,
                value.TestProjects, value.Supplements, value.Gate, value.Instrumentation, value.Limits), options);
            return;
        }

        // Serialization remains a projection only. Policy validation belongs to the admission boundary, so even
        // unsupported versions and otherwise invalid values can be serialized for diagnostics or transport.
        JsonSerializer.Serialize(writer, new CoverageFanInPolicyV3Json(value.Source, value.Run,
            value.TestProjects, value.Supplements, value.Gate, value.Instrumentation, value.Limits,
            value.ContractVersion, value.ReportProfileId), options);
    }

    private sealed record CoverageFanInPolicyV3Json(CoverageSourceIdentity Source, CoverageRunIdentity Run,
        IReadOnlyList<string> TestProjects, IReadOnlyList<CoverageSupplement> Supplements, CoverageGatePolicy Gate,
        CoverageInstrumentation? Instrumentation = null, CoverageOperationLimits? Limits = null,
        int ContractVersion = 2, string? ReportProfileId = null);

    private sealed record CoverageFanInPolicyV2Json(CoverageSourceIdentity Source, CoverageRunIdentity Run,
        IReadOnlyList<string> TestProjects, IReadOnlyList<CoverageSupplement> Supplements, CoverageGatePolicy Gate,
        CoverageInstrumentation? Instrumentation = null, CoverageOperationLimits? Limits = null);
}
