using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed v2 wire projection of independently selected application metadata; issues no admission.</summary>
internal static class EvidenceApplicationDescriptorData
{
    /// <summary>Copies all fourteen descriptor fields and nine closed roles into the existing strict wire schema.</summary>
    internal static byte[] Serialize(EvidenceLinuxApplicationDescriptor value)
    {
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
            value.Producers,
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
