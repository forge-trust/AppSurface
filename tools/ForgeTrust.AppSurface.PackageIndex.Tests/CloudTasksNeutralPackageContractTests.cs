using System.IO.Compression;
using System.Text;
using ForgeTrust.AppSurface.PackageIndex;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class CloudTasksNeutralPackageContractTests
{
    private const string Version = "0.2.0-issue892.1";

    // protects=baseline and candidate nuspecs differ only in coordinated first-party pins; fails_when=provider ranges or nuspec dependency structure are collapsed; no_claim=does not compare compiled assemblies.
    [Fact]
    public void Read_NormalizesOnlyCoordinatedFirstPartyVersionAndOrdersStableContract()
    {
        using var fixture = new PackageFixture("""
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><dependencies>
              <group targetFramework="net10.0">
                <dependency id="ForgeTrust.AppSurface.Flow" version="0.2.0-issue892.1" exclude="Build,Analyzers" />
                <dependency id="Google.Cloud.Tasks.V2" version="[3.7.0,4.0.0)" />
                <dependency id="ForgeTrust.AppSurface.Core" version="[0.2.0-issue892.1, )" include="Runtime" />
              </group>
            </dependencies></metadata></package>
            """);

        var actual = CloudTasksNeutralPackageContract.Read(fixture.Path, Version);
        using var document = System.Text.Json.JsonDocument.Parse(actual);
        var dependencies = document.RootElement.GetProperty("Dependencies").GetProperty("Groups")[0].GetProperty("Dependencies");

        Assert.Equal("ForgeTrust.AppSurface.Core", dependencies[0].GetProperty("Id").GetString());
        Assert.Equal("[<coordinated>,)", dependencies[0].GetProperty("Version").GetString());
        Assert.Equal("Runtime", dependencies[0].GetProperty("Include").GetString());
        Assert.Equal("ForgeTrust.AppSurface.Flow", dependencies[1].GetProperty("Id").GetString());
        Assert.Equal("<coordinated>", dependencies[1].GetProperty("Version").GetString());
        Assert.Equal("Build,Analyzers", dependencies[1].GetProperty("Exclude").GetString());
        Assert.Equal("Google.Cloud.Tasks.V2", dependencies[2].GetProperty("Id").GetString());
        Assert.Equal("[3.7.0,4.0.0)", dependencies[2].GetProperty("Version").GetString());
    }

    // protects=semantically different first-party version ranges remain visible; fails_when=an exact coordinated pin is treated as equivalent to a bounded or floating range; no_claim=does not implement NuGet range evaluation.
    [Theory]
    [InlineData("[0.2.0-issue892.1,1.0.0)", "[0.2.0-issue892.1,1.0.0)")]
    [InlineData("[0.2.0-issue892.1, )", "[<coordinated>,)")]
    [InlineData("[0.2.0-issue892.1]", "[<coordinated>]")]
    [InlineData("[0.2.0-issue892.1,)", "[<coordinated>,)")]
    public void Read_NormalizesOnlyExactApprovedCoordinatedRangeShapes(string input, string expected)
    {
        using var fixture = new PackageFixture($"<package><metadata><dependencies><dependency id=\"ForgeTrust.AppSurface.Durable\" version=\"{input}\" /></dependencies></metadata></package>");

        using var document = System.Text.Json.JsonDocument.Parse(CloudTasksNeutralPackageContract.Read(fixture.Path, Version));

        Assert.Equal(expected, document.RootElement.GetProperty("Dependencies").GetProperty("Groups")[0].GetProperty("Dependencies")[0].GetProperty("Version").GetString());
    }

    // Value: protects=dependency target frameworks and selection metadata are part of the package contract; fails_when=framework-specific assembly entries collapse or sort inconsistently;
    // why_new=CI patch target identifies the secondary selection-metadata ordering path; seam=none; no_claim=does not prove effective transitive restore.
    [Fact]
    public void Read_PreservesTargetFrameworksRangesAndMetadataAttributes()
    {
        using var fixture = new PackageFixture("""
            <package><metadata><dependencies>
              <group targetFramework="net10.0"><dependency id="System.Text.Json" version="[10.0.0,11.0.0)" include="Runtime" exclude="Build" custom="keep" /></group>
              <group targetFramework="netstandard2.0"><dependency id="System.Text.Json" version="[9.0.0,10.0.0)" /></group>
            </dependencies><frameworkAssemblies><frameworkAssembly assemblyName="System.Xml" targetFramework="net10.0" /><frameworkAssembly assemblyName="System.Xml" targetFramework="net9.0" /></frameworkAssemblies>
            <references><group targetFramework="net10.0"><reference file="AppSurface.dll" condition="x" /></group></references>
            </metadata></package>
            """);

        using var document = System.Text.Json.JsonDocument.Parse(CloudTasksNeutralPackageContract.Read(fixture.Path, Version));
        var root = document.RootElement;
        Assert.Equal("net10.0", root.GetProperty("Dependencies").GetProperty("Groups")[0].GetProperty("TargetFramework").GetString());
        Assert.Equal("[10.0.0,11.0.0)", root.GetProperty("Dependencies").GetProperty("Groups")[0].GetProperty("Dependencies")[0].GetProperty("Version").GetString());
        Assert.Equal("keep", root.GetProperty("Dependencies").GetProperty("Groups")[0].GetProperty("Dependencies")[0].GetProperty("AdditionalAttributes")[0].GetProperty("Value").GetString());
        Assert.Equal("netstandard2.0", root.GetProperty("Dependencies").GetProperty("Groups")[1].GetProperty("TargetFramework").GetString());
        var frameworkAssembly = root.GetProperty("FrameworkAssemblies").GetProperty("Items")[0];
        Assert.Equal("System.Xml", frameworkAssembly.GetProperty("Name").GetString());
        Assert.Contains(frameworkAssembly.GetProperty("Attributes").EnumerateArray(), attribute => attribute.GetProperty("Name").GetString() == "targetFramework" && attribute.GetProperty("Value").GetString() == "net10.0");
        var frameworkAssemblies = root.GetProperty("FrameworkAssemblies").GetProperty("Items");
        Assert.Equal(2, frameworkAssemblies.GetArrayLength());
        Assert.Contains(frameworkAssemblies[1].GetProperty("Attributes").EnumerateArray(), attribute => attribute.GetProperty("Name").GetString() == "targetFramework" && attribute.GetProperty("Value").GetString() == "net9.0");
        var reference = root.GetProperty("References").GetProperty("Groups")[0];
        Assert.Equal("net10.0", reference.GetProperty("TargetFramework").GetString());
        Assert.Contains(reference.GetProperty("References")[0].GetProperty("Attributes").EnumerateArray(), attribute => attribute.GetProperty("Name").GetString() == "condition" && attribute.GetProperty("Value").GetString() == "x");
    }

    // protects=only the four coordinated neutral package IDs receive first-party normalization; fails_when=CloudTasks or external package versions disappear into the placeholder; no_claim=does not validate package ID ownership.
    [Fact]
    public void Read_DoesNotNormalizeCloudTasksOrExternalDependencyVersions()
    {
        using var fixture = new PackageFixture($"<package><metadata><dependencies><group targetFramework=\"net10.0\"><dependency id=\"ForgeTrust.AppSurface.Durable.CloudTasks\" version=\"{Version}\"/><dependency id=\"Google.Cloud.Tasks.V2\" version=\"{Version}\"/></group></dependencies></metadata></package>");

        using var document = System.Text.Json.JsonDocument.Parse(CloudTasksNeutralPackageContract.Read(fixture.Path, Version));
        var dependencies = document.RootElement.GetProperty("Dependencies").GetProperty("Groups")[0].GetProperty("Dependencies");

        Assert.Equal(Version, dependencies[0].GetProperty("Version").GetString());
        Assert.Equal(Version, dependencies[1].GetProperty("Version").GetString());
    }

    // protects=contract serialization reports real packed nuspec drift; fails_when=framework, ID, range or selection attributes change with identical output; no_claim=does not infer restore behavior.
    [Theory]
    [InlineData("targetFramework", "net9.0")]
    [InlineData("id", "System.Text.RegularExpressions")]
    [InlineData("version", "[10.0.0,10.5.0)")]
    [InlineData("include", "Compile")]
    [InlineData("exclude", "Runtime")]
    [InlineData("custom", "changed")]
    public void Read_ProducesDifferentContractForDependencyGraphDrift(string changedAttribute, string changedValue)
    {
        const string original = "<package><metadata><dependencies><group targetFramework=\"net10.0\"><dependency id=\"System.Text.Json\" version=\"[10.0.0,11.0.0)\" include=\"Runtime\" exclude=\"Build\" custom=\"keep\" /></group></dependencies></metadata></package>";
        var changed = original.Replace(changedAttribute + "=\"" + (changedAttribute switch
        {
            "targetFramework" => "net10.0",
            "id" => "System.Text.Json",
            "version" => "[10.0.0,11.0.0)",
            "include" => "Runtime",
            "exclude" => "Build",
            _ => "keep",
        }) + "\"", changedAttribute + "=\"" + changedValue + "\"", StringComparison.Ordinal);
        using var baselinePackage = new PackageFixture(original);
        using var changedPackage = new PackageFixture(changed);

        Assert.NotEqual(
            CloudTasksNeutralPackageContract.Read(baselinePackage.Path, Version),
            CloudTasksNeutralPackageContract.Read(changedPackage.Path, Version));
    }

    // Value: protects=malformed or ambiguous packed nuspecs do not produce evidence; fails_when=invalid XML/root or missing/unknown dependency contracts produce a clean graph;
    // why_new=CI patch targets expose untested malformed XML and unsupported top-level dependency paths; seam=none; no_claim=does not replace ZIP safety limits in package validation.
    [Theory]
    [InlineData("<package>", "malformed XML")]
    [InlineData("<other><metadata><dependencies /></metadata></other>", "root must be 'package'")]
    [InlineData("<package />", "no metadata")]
    [InlineData("<package><metadata></metadata></package>", "no dependencies contract")]
    [InlineData("<package><metadata><dependencies><other /></dependencies></metadata></package>", "unsupported element 'other'")]
    [InlineData("<package><metadata><dependencies><group targetFramework=\"net10.0\"/><group targetFramework=\"net10.0\"/></dependencies></metadata></package>", "repeat targetFramework")]
    [InlineData("<package><metadata><dependencies><group /></dependencies></metadata></package>", "missing targetFramework")]
    [InlineData("<package><metadata><dependencies><group targetFramework=\"net10.0\"><other /></group></dependencies></metadata></package>", "unsupported element")]
    [InlineData("<package><metadata><dependencies><dependency id=\"A\" version=\"1\"/><dependency id=\"a\" version=\"2\"/></dependencies></metadata></package>", "repeats package id")]
    [InlineData("<package><metadata><dependencies><dependency version=\"1\"/></dependencies></metadata></package>", "requires nonblank id and version")]
    [InlineData("<package><metadata><dependencies><dependency id=\"A\"/></dependencies></metadata></package>", "requires nonblank id and version")]
    [InlineData("<package><metadata><dependencies><dependency id=\"A\" version=\"1\"><nested /></dependency></dependencies></metadata></package>", "unexpected child elements")]
    [InlineData("<package><metadata><dependencies /><dependencies /></metadata></package>", "repeats 'dependencies'")]
    [InlineData("<package><metadata /><metadata /></package>", "repeats 'metadata'")]
    public void Read_RejectsMissingOrAmbiguousDependencyContract(string nuspec, string expected)
    {
        using var fixture = new PackageFixture(nuspec);

        var error = Assert.Throws<InvalidDataException>(() => CloudTasksNeutralPackageContract.Read(fixture.Path, Version));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    // protects=namespaced selection metadata remains part of the packed contract; fails_when=reserved local names cause namespace-qualified attributes to disappear; no_claim=does not assign NuGet semantics to extension attributes.
    [Fact]
    public void Read_PreservesNamespacedAttributesWhoseLocalNamesMatchReservedFields()
    {
        using var baseline = new PackageFixture("<package xmlns:x=\"urn:proof\"><metadata><dependencies><group targetFramework=\"net10.0\"><dependency id=\"System.Text.Json\" version=\"10.0.0\" x:version=\"before\" x:include=\"runtime\" /></group></dependencies></metadata></package>");
        using var candidate = new PackageFixture("<package xmlns:x=\"urn:proof\"><metadata><dependencies><group targetFramework=\"net10.0\"><dependency id=\"System.Text.Json\" version=\"10.0.0\" x:version=\"after\" x:include=\"runtime\" /></group></dependencies></metadata></package>");
        var before = CloudTasksNeutralPackageContract.Read(baseline.Path, Version);
        var after = CloudTasksNeutralPackageContract.Read(candidate.Path, Version);
        Assert.NotEqual(before, after);
        using var document = System.Text.Json.JsonDocument.Parse(before);
        var attributes = document.RootElement.GetProperty("Dependencies").GetProperty("Groups")[0].GetProperty("Dependencies")[0].GetProperty("AdditionalAttributes");
        Assert.Contains(attributes.EnumerateArray(), value => value.GetProperty("Name").GetString() == "{urn:proof}include" && value.GetProperty("Value").GetString() == "runtime");
    }

    // Value: protects=framework and reference selection metadata cannot be silently discarded; fails_when=unsupported framework elements or ambiguous records produce a clean contract;
    // why_new=CI patch targets expose the unsupported frameworkAssemblies child rejection; seam=none; no_claim=does not parse framework reference semantics.
    [Theory]
    [InlineData("<frameworkReferences><frameworkReference targetFramework=\"net10.0\" /></frameworkReferences>", "framework references are not supported")]
    [InlineData("<frameworkAssemblies><other /></frameworkAssemblies>", "unsupported element")]
    [InlineData("<frameworkAssemblies><frameworkAssembly assemblyName=\"A\"/><frameworkAssembly assemblyName=\"A\"/></frameworkAssemblies>", "repeats 'A'")]
    [InlineData("<frameworkAssemblies><frameworkAssembly /></frameworkAssemblies>", "requires 'assemblyName'")]
    [InlineData("<references><group /><group /></references>", "missing targetFramework")]
    [InlineData("<references><group targetFramework=\"net10.0\"><other /></group></references>", "unsupported element")]
    [InlineData("<references><group targetFramework=\"net10.0\"/><group targetFramework=\"NET10.0\"/></references>", "repeat targetFramework")]
    [InlineData("<references><reference /></references>", "requires 'file'")]
    [InlineData("<references><reference file=\"A.dll\"/><reference file=\"A.dll\"/></references>", "repeats 'A.dll'")]
    [InlineData("<references><reference file=\"A.dll\"><nested /></reference></references>", "unexpected child elements")]
    [InlineData("<references><other /></references>", "unsupported element")]
    public void Read_RejectsUnsupportedOrAmbiguousFrameworkAndReferenceContracts(string metadataChild, string expected)
    {
        using var fixture = new PackageFixture("<package><metadata><dependencies />" + metadataChild + "</metadata></package>");

        var error = Assert.Throws<InvalidDataException>(() => CloudTasksNeutralPackageContract.Read(fixture.Path, Version));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    // protects=the package contract is read from exactly one bounded root nuspec; fails_when=missing, nested, duplicate, empty or oversized nuspec yields a contract; no_claim=does not inspect unrelated zip entries.
    [Theory]
    [InlineData("missing")]
    [InlineData("nested")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("oversized")]
    public void Read_RejectsInvalidNuspecArchiveShape(string shape)
    {
        using var fixture = new PackageFixture("<package><metadata><dependencies /></metadata></package>", shape);

        var error = Assert.Throws<InvalidDataException>(() => CloudTasksNeutralPackageContract.Read(fixture.Path, Version));

        var expected = shape switch
        {
            "oversized" => "inspection limit",
            "empty" => "empty or exceeds",
            _ => "exactly one root nuspec",
        };
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    private sealed class PackageFixture : IDisposable
    {
        internal PackageFixture(string nuspec, string shape = "normal")
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cloudtasks-neutral-contract-" + Guid.NewGuid().ToString("N") + ".nupkg");
            using var archive = ZipFile.Open(Path, ZipArchiveMode.Create);
            if (shape == "missing") return;
            if (shape == "nested") Write(archive, "nested/Fixture.nuspec", nuspec);
            else if (shape == "oversized") Write(archive, "Fixture.nuspec", new string(' ', 1024 * 1024 + 1));
            else if (shape == "empty") archive.CreateEntry("Fixture.nuspec");
            else
            {
                Write(archive, "Fixture.nuspec", nuspec);
                if (shape == "duplicate") Write(archive, "other.nuspec", nuspec);
            }
        }

        internal string Path { get; }

        public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }

        private static void Write(ZipArchive archive, string name, string value)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
            writer.Write(value);
        }
    }
}
