using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed v2 wire projection of independently selected application metadata; issues no admission.</summary>
internal static class EvidenceApplicationDescriptorData
{
    /// <summary>Copies all fourteen descriptor fields and nine closed roles into the existing strict wire schema.</summary>
    /// <remarks>The v2 producer wire has eight fixed fields. Managed retention and omitted execution settings
    /// are its implicit defaults. Other settings reject before projection; adding ordinary producer properties
    /// must not widen this schema or silently discard an unsupported request.</remarks>
    internal static byte[] Serialize(EvidenceLinuxApplicationDescriptor value)
    {
        if (value.Producers.Any(static producer => producer.CoverageExecution is not null
            || !string.Equals(producer.CoverageArtifactRetention, "managed", StringComparison.Ordinal)))
            throw new EvidenceAdmissionException("ASEVD402", "The application producer settings are not supported by the closed v2 descriptor.");
        var roles = new[] { "apphost", "apphost_runtime_configuration", "resource", "resource_runtime_configuration",
            "dcp", "dcp_extension", "dependency", "declared_input", "dependency_manifest" };
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            value.ApplicationId,
            value.ApplicationVersion,
            value.BuildId,
            value.CatalogueDigest,
            value.EntryDigest,
            value.AspireSdkVersion,
            value.Resources,
            Producers = value.Producers.Select(static producer => new
            {
                producer.Id,
                producer.Kind,
                producer.Version,
                producer.RequiredResources,
                producer.AssertionIds,
                producer.ArtifactSlots,
                producer.TimeoutSeconds,
                producer.CoverageGate,
            }).ToArray(),
            BundleFiles = value.BundleFiles.Select(file => new
            {
                file.RelativePath,
                Role = roles[(int)file.Role],
                file.LengthBytes,
                file.Sha256,
                file.Mode
            }).ToArray(),
            value.Capabilities,
            value.ApplicationUid,
            value.ApplicationGid,
            value.ResultsGid,
            value.ResourceAccessGid
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    }
}
