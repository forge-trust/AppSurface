using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>The immutable Docs evidence reference carried by a publication-start receipt.</summary>
internal sealed record DocsNativeReleaseIdentity(string ArtifactId, string Sha256);

/// <summary>Trusted lookup inputs for a separately downloaded Docs evidence artifact.</summary>
internal sealed record DocsNativeEvidenceInput(string Path, string ArtifactId, string Sha256);

/// <summary>Validates the separate packed Docs runtime and cross-platform release proof.</summary>
internal static class DocsNativeReleaseEvidence
{
    internal const string PackageId = "ForgeTrust.AppSurface.Docs";
    internal const string TargetPath = "buildTransitive/ForgeTrust.AppSurface.Docs.targets";

    private const int MaximumBytes = 128 * 1024;
    private static readonly IReadOnlyDictionary<string, string> HostArchitectures = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["linux-arm"] = "Arm",
        ["linux-arm64"] = "Arm64",
        ["linux-x64"] = "X64",
        ["osx-arm64"] = "Arm64",
        ["osx-x64"] = "X64",
        ["win-arm64"] = "Arm64",
        ["win-x64"] = "X64",
        ["win-x86"] = "X86"
    };

    private static readonly JsonSerializerOptions ProofOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    /// <summary>Checks the uploaded proof against the selected producer and exact Docs package bytes.</summary>
    internal static DocsNativeReleaseIdentity Validate(
        DocsNativeEvidenceInput input,
        string producerArtifactId,
        string sourceCommit,
        string packageVersion,
        string docsPackageSha512)
    {
        var artifactIds = new HashSet<string>(StringComparer.Ordinal) { producerArtifactId };
        RequireDistinctArtifactId(artifactIds, input.ArtifactId);
        RequireDigest(input.Sha256);
        var path = Path.GetFullPath(input.Path);
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new PackageIndexException("Docs native proof must be a regular file.");
        }

        TailwindEvidenceWorkflow.RequireDirectChild(Path.GetDirectoryName(path)!, path);
        var bytes = TailwindEvidenceWorkflow.ReadBounded(path, MaximumBytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(hash, input.Sha256, StringComparison.Ordinal))
        {
            throw new PackageIndexException("Docs native proof SHA-256 differs from the selected artifact.");
        }

        using var document = TailwindEvidenceWorkflow.ParseStrict(bytes, "Docs native proof");
        var proof = document.RootElement.Deserialize<Proof>(ProofOptions);
        if (proof is null
            || proof.Schema != "appsurface-docs-native-proof-v1"
            || proof.SdkVersion != "10.0.402"
            || proof.ProducerArtifactId != producerArtifactId
            || proof.SourceCommit != sourceCommit
            || proof.PackageVersion != packageVersion
            || proof.DocsPackageSha512 != docsPackageSha512)
        {
            throw new PackageIndexException("Docs native proof differs from the selected producer or package.");
        }

        ValidateHosts(proof.Hosts, artifactIds);
        ValidateCrossPlatform(proof.CrossPlatform, artifactIds);
        return new DocsNativeReleaseIdentity(input.ArtifactId, hash);
    }

    private static void ValidateHosts(Host?[] hosts, HashSet<string> artifactIds)
    {
        if (hosts.Length != HostArchitectures.Count)
        {
            throw new PackageIndexException("Docs native proof must contain eight host receipts.");
        }

        var seenRids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in hosts)
        {
            if (host is null || !HostArchitectures.TryGetValue(host.Rid, out var architecture)
                || !seenRids.Add(host.Rid)
                || host.Architecture != architecture
                || !host.DirectBuild || !host.DirectPublish || !host.TransitiveBuild || !host.TransitivePublish)
            {
                throw new PackageIndexException("Docs native proof contains a duplicate, invalid or failed host receipt.");
            }

            RequireDistinctArtifactId(artifactIds, host.ArtifactId);
            RequireDigest(host.ReceiptSha256);
        }
    }

    private static void ValidateCrossPlatform(CrossPlatform transfer, HashSet<string> artifactIds)
    {
        RequireDistinctArtifactId(artifactIds, transfer.SourceArtifactId);
        RequireDistinctArtifactId(artifactIds, transfer.DestinationArtifactId);
        RequireDigest(transfer.DefaultSha256);
        RequireDigest(transfer.KeepAllSha256);
        if (transfer.SourceRid != "linux-x64" || transfer.DestinationRid != "win-x64"
            || transfer.DefaultSha256 == transfer.KeepAllSha256
            || transfer.DefaultSha256 != transfer.ObservedDefaultSha256
            || transfer.KeepAllSha256 != transfer.ObservedKeepAllSha256
            || !transfer.DefaultStartup || !transfer.DefaultParserUnavailable || !transfer.KeepAllHarvest)
        {
            throw new PackageIndexException("Docs native proof does not establish the exact Linux-to-Windows startup gate.");
        }
    }

    private static void RequireArtifactId(string value)
    {
        if (value.Length == 0 || value[0] == '0'
            || value.Any(character => character is not (>= '0' and <= '9'))
            || !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new PackageIndexException("Docs evidence artifact IDs must be positive canonical decimal strings.");
        }
    }

    private static void RequireDistinctArtifactId(HashSet<string> artifactIds, string value)
    {
        RequireArtifactId(value);
        if (!artifactIds.Add(value))
        {
            throw new PackageIndexException("Docs native proof artifact IDs must be distinct.");
        }
    }

    private static void RequireDigest(string value)
    {
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new PackageIndexException("Docs evidence SHA-256 must be lowercase hexadecimal.");
        }
    }

    private sealed record Proof(
        string Schema, string ProducerArtifactId, string SourceCommit, string PackageVersion,
        string DocsPackageSha512, string SdkVersion, Host?[] Hosts, CrossPlatform CrossPlatform);

    private sealed record Host(
        string Rid, string Architecture, string ArtifactId, string ReceiptSha256,
        bool DirectBuild, bool DirectPublish, bool TransitiveBuild, bool TransitivePublish);

    private sealed record CrossPlatform(
        string SourceRid, string DestinationRid, string SourceArtifactId, string DestinationArtifactId,
        string DefaultSha256, string KeepAllSha256, string ObservedDefaultSha256, string ObservedKeepAllSha256,
        bool DefaultStartup, bool DefaultParserUnavailable, bool KeepAllHarvest);
}
