using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

/// <summary>Checks candidate binding and required observations in a dedicated Docs native receipt.</summary>
public sealed class DocsNativeReleaseEvidenceTests
{
    private const string ProducerId = "10";
    private const string PackageVersion = "0.2.0-preview.15";
    private static readonly string Commit = new('a', 40);
    private static readonly string PackageHash = new('b', 128);
    private static readonly string DefaultHash = new('c', 64);
    private static readonly string KeepAllHash = new('d', 64);

    [Fact]
    public void FilterAndReleaseWorkflowsAgreeOnTheRequiredHostRids()
    {
        var root = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var target = XDocument.Load(TestPathUtils.PathUnder(root, "Web", "ForgeTrust.AppSurface.Docs", "buildTransitive",
            "ForgeTrust.AppSurface.Docs.targets"));
        var allowed = Assert.Single(target.Descendants("_AppSurfaceDocsTreeSitterHostRids")).Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var workflow = File.ReadAllText(TestPathUtils.PathUnder(root, ".github", "workflows", "docs-native-evidence.yml"));
        var matrix = Regex.Matches(workflow, """\{rid:"(.*?)",""")
            .Select(match => match.Groups[1].Value).ToArray();
        var required = CreateProof()["hosts"]!.AsArray()
            .Select(host => host!["rid"]!.GetValue<string>()).ToArray();

        Assert.Equal(required.Order(StringComparer.Ordinal), allowed.Order(StringComparer.Ordinal));
        Assert.Equal(required.Order(StringComparer.Ordinal), matrix.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Validate_ReturnsTheBoundImmutableArtifact()
    {
        using var work = new ProofWorkspace();
        var input = work.Write(CreateProof().ToJsonString());

        var identity = Validate(input);

        Assert.Equal(input.ArtifactId, identity.ArtifactId);
        Assert.Equal(input.Sha256, identity.Sha256);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("sdkVersion")]
    [InlineData("producerArtifactId")]
    [InlineData("sourceCommit")]
    [InlineData("packageVersion")]
    [InlineData("docsPackageSha512")]
    public void Validate_RejectsWrongCandidateOrToolchain(string field)
    {
        using var work = new ProofWorkspace();
        var proof = CreateProof();
        proof[field] = "changed";
        Assert.Throws<PackageIndexException>(() => Validate(work.Write(proof.ToJsonString())));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate-rid")]
    [InlineData("duplicate-artifact")]
    [InlineData("unsupported-rid")]
    [InlineData("architecture")]
    [InlineData("artifactId")]
    [InlineData("receiptSha256")]
    [InlineData("directBuild")]
    [InlineData("directPublish")]
    [InlineData("transitiveBuild")]
    [InlineData("transitivePublish")]
    [InlineData("null-host")]
    public void Validate_RejectsIncompleteOrFailedHostReceipt(string mutation)
    {
        using var work = new ProofWorkspace();
        var proof = CreateProof();
        var hosts = proof["hosts"]!.AsArray();
        var first = hosts[0]!.AsObject();
        switch (mutation)
        {
            case "missing":
                hosts.RemoveAt(0);
                break;
            case "duplicate-rid":
                hosts[1]!["rid"] = first["rid"]!.GetValue<string>();
                break;
            case "duplicate-artifact":
                hosts[1]!["artifactId"] = first["artifactId"]!.GetValue<string>();
                break;
            case "unsupported-rid":
                first["rid"] = "linux-x86";
                break;
            case "architecture":
                first["architecture"] = "Arm64";
                break;
            case "artifactId":
                first["artifactId"] = "00";
                break;
            case "receiptSha256":
                first["receiptSha256"] = "bad";
                break;
            case "null-host":
                hosts[0] = null;
                break;
            default:
                first[mutation] = false;
                break;
        }

        Assert.Throws<PackageIndexException>(() => Validate(work.Write(proof.ToJsonString())));
    }

    [Theory]
    [InlineData("sourceRid", "osx-arm64")]
    [InlineData("destinationRid", "win-arm64")]
    [InlineData("sourceArtifactId", "99")]
    [InlineData("sourceArtifactId", "20")]
    [InlineData("sourceArtifactId", "")]
    [InlineData("sourceArtifactId", "18446744073709551616")]
    [InlineData("destinationArtifactId", "10")]
    [InlineData("destinationArtifactId", "113")]
    [InlineData("destinationArtifactId", "99\0")]
    [InlineData("defaultSha256", "wrong")]
    [InlineData("defaultSha256", "Cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
    [InlineData("defaultSha256", "gccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
    [InlineData("keepAllSha256", "wrong")]
    [InlineData("observedDefaultSha256", "wrong")]
    [InlineData("observedKeepAllSha256", "wrong")]
    public void Validate_RejectsWrongCrossPlatformProvenance(string field, string value)
    {
        using var work = new ProofWorkspace();
        var proof = CreateProof();
        proof["crossPlatform"]![field] = value;

        Assert.Throws<PackageIndexException>(() => Validate(work.Write(proof.ToJsonString())));
    }

    [Fact]
    public void Validate_RejectsASharedDigestForDefaultAndKeepAll()
    {
        using var work = new ProofWorkspace();
        var proof = CreateProof();
        var transfer = proof["crossPlatform"]!;
        transfer["keepAllSha256"] = DefaultHash;
        transfer["observedKeepAllSha256"] = DefaultHash;

        Assert.Throws<PackageIndexException>(() => Validate(work.Write(proof.ToJsonString())));
    }

    [Theory]
    [InlineData("defaultStartup")]
    [InlineData("defaultParserUnavailable")]
    [InlineData("keepAllHarvest")]
    public void Validate_RejectsUnprovenDestinationBehavior(string field)
    {
        using var work = new ProofWorkspace();
        var proof = CreateProof();
        proof["crossPlatform"]![field] = false;

        Assert.Throws<PackageIndexException>(() => Validate(work.Write(proof.ToJsonString())));
    }

    [Fact]
    public void Validate_RejectsTamperingAndMissingProof()
    {
        using var work = new ProofWorkspace();
        var input = work.Write(CreateProof().ToJsonString());
        File.AppendAllText(input.Path, "\n");
        Assert.Throws<PackageIndexException>(() => Validate(input));

        File.Delete(input.Path);
        Assert.Throws<PackageIndexException>(() => Validate(input));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_RejectsExtraOrMissingJsonFields(bool extra)
    {
        using var work = new ProofWorkspace();
        var proof = CreateProof();
        if (extra)
        {
            proof["surplus"] = "unexpected";
        }
        else
        {
            proof.Remove("schema");
        }

        Assert.Throws<JsonException>(() => Validate(work.Write(proof.ToJsonString())));
    }

    private static DocsNativeReleaseIdentity Validate(DocsNativeEvidenceInput input)
        => DocsNativeReleaseEvidence.Validate(input, ProducerId, Commit, PackageVersion, PackageHash);

    internal static JsonObject CreateProof(
        string producerId = ProducerId,
        string? sourceCommit = null,
        string packageVersion = PackageVersion,
        string? docsPackageHash = null)
    {
        var hosts = new (string Rid, string Architecture)[]
        {
            ("linux-arm", "Arm"), ("linux-arm64", "Arm64"), ("linux-x64", "X64"), ("osx-arm64", "Arm64"),
            ("osx-x64", "X64"), ("win-arm64", "Arm64"), ("win-x64", "X64"), ("win-x86", "X86")
        };
        return JsonSerializer.SerializeToNode(new
        {
            schema = "appsurface-docs-native-proof-v1",
            producerArtifactId = producerId,
            sourceCommit = sourceCommit ?? Commit,
            packageVersion,
            docsPackageSha512 = docsPackageHash ?? PackageHash,
            sdkVersion = "10.0.402",
            hosts = hosts.Select((host, index) => new
            {
                rid = host.Rid,
                architecture = host.Architecture,
                artifactId = (20 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                receiptSha256 = DefaultHash,
                directBuild = true,
                directPublish = true,
                transitiveBuild = true,
                transitivePublish = true
            }),
            crossPlatform = new
            {
                sourceRid = "linux-x64",
                destinationRid = "win-x64",
                sourceArtifactId = "98",
                destinationArtifactId = "99",
                defaultSha256 = DefaultHash,
                keepAllSha256 = KeepAllHash,
                observedDefaultSha256 = DefaultHash,
                observedKeepAllSha256 = KeepAllHash,
                defaultStartup = true,
                defaultParserUnavailable = true,
                keepAllHarvest = true
            }
        })!.AsObject();
    }

    private sealed class ProofWorkspace : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("docs-native-proof-");

        public DocsNativeEvidenceInput Write(string proof)
        {
            var path = TestPathUtils.PathUnder(_directory.FullName, "docs-native-proof.json");
            File.WriteAllText(path, proof);
            return new DocsNativeEvidenceInput(path, "113", Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
