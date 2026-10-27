using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.PackageIndex;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class CloudTasksNeutralSourceProofTests
{
    // protects=only the two exact Cloud Tasks central pins are normalized while all other source and policy hashes remain evidenced; fails_when=another input differs or an approved pin is altered; no_claim=does not assert compiled binaries are byte-identical.
    [Fact]
    public void Verify_AllowsOnlyApprovedCloudTasksPinsAndReturnsSortedHashEvidence()
    {
        using var fixture = new NeutralSourceFixture();
        fixture.WriteCentralPackages(candidate: false);
        fixture.WriteCentralPackages(candidate: true, includeApprovedPins: true);
        fixture.WritePackageGeneratedFiles();

        var evidence = CloudTasksNeutralSourceProof.Verify(fixture.BaselineRoot, fixture.CandidateRoot);

        Assert.Equal(evidence.Keys.Order(StringComparer.Ordinal), evidence.Keys);
        Assert.Contains("baseline/ForgeTrust.AppSurface.Core/Source.cs", evidence.Keys);
        Assert.Contains("candidate/Directory.Packages.props", evidence.Keys);
        Assert.All(evidence.Values, hash => Assert.Matches("^[a-f0-9]{64}$", hash));
        Assert.Equal(Hash(fixture.CandidatePath("ForgeTrust.AppSurface.Core/Source.cs")), evidence["candidate/ForgeTrust.AppSurface.Core/Source.cs"]);
    }

    // protects=generated build outputs cannot hide drift or destabilize authored-source comparison; fails_when=bin or obj contents affect parity; no_claim=does not prove those ignored outputs are safe to package.
    [Fact]
    public void Verify_IgnoresGeneratedBinAndObjTrees()
    {
        using var fixture = new NeutralSourceFixture();
        fixture.WriteCentralPackages(candidate: false);
        fixture.WriteCentralPackages(candidate: true, includeApprovedPins: true);
        fixture.WritePackageGeneratedFiles();

        var evidence = CloudTasksNeutralSourceProof.Verify(fixture.BaselineRoot, fixture.CandidateRoot);

        Assert.DoesNotContain(evidence.Keys, path => path.Contains("/obj/", StringComparison.Ordinal));
        Assert.DoesNotContain(evidence.Keys, path => path.Contains("/bin/", StringComparison.Ordinal));
    }

    // protects=all neutral package sources and ancestor/shared build policies stay identical; fails_when=authored source, a shared policy, or optional SDK input is added, removed, or changed; no_claim=does not prove every unrelated repository input is consumed by these projects.
    [Theory]
    [InlineData("source-bytes")]
    [InlineData("candidate-addition")]
    [InlineData("candidate-removal")]
    [InlineData("shared-build-input")]
    [InlineData("ancestor-build-input")]
    [InlineData("ancestor-build-input-addition")]
    [InlineData("optional-build-input")]
    public void Verify_RejectsNeutralSourceOrSharedInputDrift(string change)
    {
        using var fixture = new NeutralSourceFixture();
        fixture.WriteCentralPackages(candidate: false);
        fixture.WriteCentralPackages(candidate: true, includeApprovedPins: true);

        switch (change)
        {
            case "source-bytes":
                fixture.WriteCandidate("Workers/ForgeTrust.AppSurface.Workers/Source.cs", "changed source");
                break;
            case "candidate-addition":
                fixture.WriteCandidate("Durable/ForgeTrust.AppSurface.Durable/Extra.cs", "new source");
                break;
            case "candidate-removal":
                File.Delete(fixture.CandidatePath("Flow/ForgeTrust.AppSurface.Flow.Generators/Source.cs"));
                break;
            case "shared-build-input":
                fixture.WriteCandidate("Directory.Build.props", "<Project><PropertyGroup><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup></Project>");
                break;
            case "ancestor-build-input":
                fixture.WriteCandidate("Flow/Directory.Build.targets", "<Project><PropertyGroup><NoWarn>all</NoWarn></PropertyGroup></Project>");
                fixture.WriteBaseline("Flow/Directory.Build.targets", "<Project><PropertyGroup><NoWarn>none</NoWarn></PropertyGroup></Project>");
                break;
            case "ancestor-build-input-addition":
                fixture.WriteCandidate("Durable/.editorconfig", "root = true\n");
                break;
            case "optional-build-input":
                fixture.WriteCandidate("global.json", "{ \"sdk\": { \"version\": \"10.0.402\" } }");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }

        var error = Assert.Throws<PackageIndexException>(
            () => CloudTasksNeutralSourceProof.Verify(fixture.BaselineRoot, fixture.CandidateRoot));

        Assert.Contains("Neutral source parity failed", error.Message, StringComparison.Ordinal);
    }

    // protects=central package XML permits only the exact Google Tasks and conditional Grpc pins; fails_when=an unapproved pin, version, condition, attribute, duplicate, or baseline pin is accepted; no_claim=does not resolve the full NuGet graph.
    [Theory]
    [InlineData("unapproved-version")]
    [InlineData("unapproved-package")]
    [InlineData("wrong-task-version")]
    [InlineData("wrong-grpc-condition")]
    [InlineData("extra-pin-attribute")]
    [InlineData("duplicate-pin")]
    [InlineData("baseline-has-pin")]
    public void Verify_RejectsCentralPackageChangesOutsideExactApprovedPins(string mutation)
    {
        using var fixture = new NeutralSourceFixture();
        fixture.WriteCentralPackages(candidate: false, includeApprovedPins: mutation == "baseline-has-pin");
        fixture.WriteCentralPackages(candidate: true, includeApprovedPins: true);

        var candidateProps = File.ReadAllText(fixture.CandidatePath("Directory.Packages.props"));
        switch (mutation)
        {
            case "unapproved-version":
                candidateProps = candidateProps.Replace("Include=\"Existing\" Version=\"1.0.0\"", "Include=\"Existing\" Version=\"1.0.1\"", StringComparison.Ordinal);
                break;
            case "unapproved-package":
                candidateProps = candidateProps.Replace("  </ItemGroup>", "    <PackageVersion Include=\"Unexpected\" Version=\"9.9.9\" />\n  </ItemGroup>", StringComparison.Ordinal);
                break;
            case "wrong-task-version":
                candidateProps = candidateProps.Replace("Google.Cloud.Tasks.V2\" Version=\"3.7.0\"", "Google.Cloud.Tasks.V2\" Version=\"3.7.1\"", StringComparison.Ordinal);
                break;
            case "wrong-grpc-condition":
                candidateProps = candidateProps.Replace("ForgeTrust.AppSurface.Durable.CloudTasks.Tests'", "ForgeTrust.AppSurface.Durable.CloudTasks.Test'", StringComparison.Ordinal);
                break;
            case "extra-pin-attribute":
                candidateProps = candidateProps.Replace("Include=\"Google.Cloud.Tasks.V2\"", "Include=\"Google.Cloud.Tasks.V2\" Condition=\"true\"", StringComparison.Ordinal);
                break;
            case "duplicate-pin":
                candidateProps = candidateProps.Replace("  </ItemGroup>", "    <PackageVersion Include=\"Google.Cloud.Tasks.V2\" Version=\"3.7.0\" />\n  </ItemGroup>", StringComparison.Ordinal);
                break;
            case "baseline-has-pin":
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        File.WriteAllText(fixture.CandidatePath("Directory.Packages.props"), candidateProps, Encoding.UTF8);

        var error = Assert.Throws<PackageIndexException>(
            () => CloudTasksNeutralSourceProof.Verify(fixture.BaselineRoot, fixture.CandidateRoot));

        Assert.Contains("Directory.Packages.props", error.Message, StringComparison.Ordinal);
    }

    // protects=neutral source proof reads regular authored inputs without following file links; fails_when=a linked source file is accepted; no_claim=does not scan directories outside the five declared packages.
    [Fact]
    public void Verify_RejectsSymbolicLinksInNeutralPackageInputs()
    {
        using var fixture = new NeutralSourceFixture();
        fixture.WriteCentralPackages(candidate: false);
        fixture.WriteCentralPackages(candidate: true, includeApprovedPins: true);
        fixture.WriteCandidate("ForgeTrust.AppSurface.Core/target.txt", "private generated test input");
        var target = fixture.CandidatePath("ForgeTrust.AppSurface.Core/target.txt");
        var link = fixture.CandidatePath("ForgeTrust.AppSurface.Core/linked-input.txt");
        File.CreateSymbolicLink(link, target);

        var error = Assert.Throws<PackageIndexException>(
            () => CloudTasksNeutralSourceProof.Verify(fixture.BaselineRoot, fixture.CandidateRoot));

        Assert.Contains("symbolic link or reparse point", error.Message, StringComparison.Ordinal);
    }

    // protects=package source traversal rejects linked ancestors before enumerating their contents; fails_when=a symlinked package parent can supply source inputs; no_claim=does not police filesystem links outside the provided roots.
    [Fact]
    public void Verify_RejectsSymbolicLinkInPackageDirectoryAncestor()
    {
        using var fixture = new NeutralSourceFixture();
        fixture.WriteCentralPackages(candidate: false);
        fixture.WriteCentralPackages(candidate: true, includeApprovedPins: true);
        var candidateFlow = fixture.CandidatePath("Flow");
        var target = Path.Combine(Path.GetDirectoryName(candidateFlow)!, "flow-target");
        Directory.Move(candidateFlow, target);
        Directory.CreateSymbolicLink(candidateFlow, target);

        var error = Assert.Throws<PackageIndexException>(
            () => CloudTasksNeutralSourceProof.Verify(fixture.BaselineRoot, fixture.CandidateRoot));

        Assert.Contains("symbolic link or reparse point", error.Message, StringComparison.Ordinal);
    }

    // protects=required shared policy files and parseable central package XML are prerequisites for parity evidence; fails_when=missing LICENSE or malformed CPM input proceeds; no_claim=does not validate the legal completeness of LICENSE.
    [Fact]
    public void Verify_RejectsMissingRequiredInputsAndMalformedCentralPackageXml()
    {
        using var fixture = new NeutralSourceFixture();
        fixture.WriteCentralPackages(candidate: false);
        fixture.WriteCentralPackages(candidate: true, includeApprovedPins: true);
        File.Delete(fixture.BaselinePath("LICENSE"));
        var missingInput = Assert.Throws<PackageIndexException>(
            () => CloudTasksNeutralSourceProof.Verify(fixture.BaselineRoot, fixture.CandidateRoot));
        Assert.Contains("LICENSE", missingInput.Message, StringComparison.Ordinal);

        fixture.WriteBaseline("LICENSE", "MIT");
        fixture.WriteCandidate("Directory.Packages.props", "<Project>");
        var malformedXml = Assert.Throws<PackageIndexException>(
            () => CloudTasksNeutralSourceProof.Verify(fixture.BaselineRoot, fixture.CandidateRoot));
        Assert.Contains("cannot parse central package file", malformedXml.Message, StringComparison.Ordinal);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class NeutralSourceFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CloudTasksNeutralSourceProofTests", Guid.NewGuid().ToString("N"));
        private static readonly string[] PackageDirectories =
        [
            "ForgeTrust.AppSurface.Core",
            "Flow/ForgeTrust.AppSurface.Flow",
            "Flow/ForgeTrust.AppSurface.Flow.Generators",
            "Workers/ForgeTrust.AppSurface.Workers",
            "Durable/ForgeTrust.AppSurface.Durable"
        ];

        private static readonly string[] SharedFiles =
        [
            "Directory.Build.props",
            "Directory.Build.targets",
            "NuGet.package-gate.config",
            "LICENSE",
            ".editorconfig"
        ];

        internal string BaselineRoot => Path.Combine(_root, "baseline");
        internal string CandidateRoot => Path.Combine(_root, "candidate");

        internal NeutralSourceFixture()
        {
            foreach (var root in new[] { BaselineRoot, CandidateRoot })
            {
                foreach (var directory in PackageDirectories) Write(root, $"{directory}/Source.cs", "namespace Proof; internal sealed class Source;\n");
                foreach (var file in SharedFiles) Write(root, file, file == "LICENSE" ? "MIT\n" : "<Project />\n");
                WriteCentralPackages(root, includeApprovedPins: false);
            }
        }

        internal void WriteCentralPackages(bool candidate, bool includeApprovedPins = false) =>
            Write(candidate ? CandidateRoot : BaselineRoot, "Directory.Packages.props", CentralPackages(includeApprovedPins));

        internal void WritePackageGeneratedFiles()
        {
            Write(BaselineRoot, "ForgeTrust.AppSurface.Core/obj/Generated.cs", "baseline ignored");
            Write(CandidateRoot, "ForgeTrust.AppSurface.Core/obj/Generated.cs", "candidate ignored");
            Write(BaselineRoot, "Workers/ForgeTrust.AppSurface.Workers/bin/Assembly.dll", "baseline ignored");
            Write(CandidateRoot, "Workers/ForgeTrust.AppSurface.Workers/bin/Assembly.dll", "candidate ignored");
        }

        internal string BaselinePath(string relativePath) => TestPathUtils.PathUnder(BaselineRoot, relativePath);
        internal string CandidatePath(string relativePath) => TestPathUtils.PathUnder(CandidateRoot, relativePath);
        internal void WriteBaseline(string relativePath, string contents) => Write(BaselineRoot, relativePath, contents);
        internal void WriteCandidate(string relativePath, string contents) => Write(CandidateRoot, relativePath, contents);

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static string CentralPackages(bool includeApprovedPins)
        {
            var pins = includeApprovedPins
                ? "    <PackageVersion Include=\"Google.Cloud.Tasks.V2\" Version=\"3.7.0\" />\n    <PackageVersion Include=\"Grpc.Net.Client\" Version=\"2.80.0\" Condition=\"'$(MSBuildProjectName)' == 'ForgeTrust.AppSurface.Durable.CloudTasks' OR '$(MSBuildProjectName)' == 'ForgeTrust.AppSurface.Durable.CloudTasks.Tests'\" />\n"
                : string.Empty;
            return $"<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"Existing\" Version=\"1.0.0\" />\n{pins}  </ItemGroup>\n</Project>\n";
        }

        private static void Write(string root, string relativePath, string contents)
        {
            var path = TestPathUtils.PathUnder(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents, Encoding.UTF8);
        }

        private static void WriteCentralPackages(string root, bool includeApprovedPins) =>
            Write(root, "Directory.Packages.props", CentralPackages(includeApprovedPins));
    }
}
