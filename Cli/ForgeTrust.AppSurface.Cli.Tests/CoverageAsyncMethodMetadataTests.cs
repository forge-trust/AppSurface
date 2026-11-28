using System.Text;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInAsyncMethodMetadataTests
{
    private static readonly IReadOnlyList<MetadataReference> PlatformReferences =
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable."))
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(static path => MetadataReference.CreateFromFile(path))
        .ToArray();

    [Fact]
    public void Read_CompiledAsyncFixture_UsesBoundMetadataAndOmitsAmbiguousOverloads()
    {
        const string source = """
            using System.Threading.Tasks;
            namespace Fixture;
            public sealed class Sample
            {
                public async Task<int> IncludedAsync(int value)
                {
                    if (value > 0)
                    {
                        await Task.Yield();
                        return value;
                    }
                    return 0;
                }

                public async Task<int> AmbiguousAsync() => await Task.FromResult(1);
                public async Task<int> AmbiguousAsync(int value) => await Task.FromResult(value);
            }
            """;
        using var temporaryDirectory = TestDirectory.Create();
        var sourceRoot = TestPathUtils.PathUnder(temporaryDirectory.Path, "repo");
        var documentPath = TestPathUtils.PathUnder(sourceRoot, "src", "CoverageFixture.cs");
        var fixture = Compile("CoverageFixture", source, documentPath);

        var method = Assert.Single(CoverageAsyncMethodMetadata.Read(fixture.Assembly, fixture.Pdb, sourceRoot));
        Assert.Equal("CoverageFixture", method.Assembly);
        Assert.Equal("Fixture.Sample", method.SourceType);
        Assert.Equal("IncludedAsync", method.SourceMethod);
        Assert.Contains("Fixture.Sample/", method.StateMachineType, StringComparison.Ordinal);
        Assert.Equal("src/CoverageFixture.cs", method.File);
        Assert.Contains(7, method.Lines);
        Assert.Contains(9, method.Lines);
        Assert.StartsWith("sha256:", method.Fingerprint, StringComparison.Ordinal);
        Assert.Equal(71, method.Fingerprint.Length);

        var mapped = Compile("CoverageFixture", source, "/_/src/CoverageFixture.cs");
        var mappedMethod = Assert.Single(CoverageAsyncMethodMetadata.Read(mapped.Assembly, mapped.Pdb, sourceRoot));
        Assert.Equal(method.File, mappedMethod.File);
        Assert.Equal(method.Fingerprint, mappedMethod.Fingerprint);

        var checksumVariant = Compile("CoverageFixture", source + "// checksum variant\n", documentPath);
        var checksumVariantMethod = Assert.Single(CoverageAsyncMethodMetadata.Read(
            checksumVariant.Assembly, checksumVariant.Pdb, sourceRoot));
        Assert.NotEqual(method.Fingerprint, checksumVariantMethod.Fingerprint);

        var other = Compile("DifferentAssembly", "namespace Other; public sealed class TypeName { public void Run() { } }",
            documentPath);
        Assert.Throws<InvalidDataException>(() => CoverageAsyncMethodMetadata.Read(fixture.Assembly, other.Pdb, sourceRoot));
        Assert.Empty(CoverageAsyncMethodMetadata.Read(fixture.Assembly, fixture.Pdb,
            TestPathUtils.PathUnder(temporaryDirectory.Path, "elsewhere")));
    }

    [Fact]
    public void Read_LocalCoverageModule_ProvesAsyncMethods()
    {
        var output = AppContext.BaseDirectory;
        var assemblyPath = Path.Combine(output, "ForgeTrust.AppSurface.Evidence.Coverage.dll");
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        Assert.True(File.Exists(assemblyPath), $"Expected local module at {assemblyPath}.");
        Assert.True(File.Exists(pdbPath), $"Expected local symbols at {pdbPath}.");

        var sourceRoot = FindRepositoryRoot();
        var methods = CoverageAsyncMethodMetadata.Read(File.ReadAllBytes(assemblyPath), File.ReadAllBytes(pdbPath), sourceRoot);
        Assert.NotEmpty(methods);
    }

    private static CompiledFixture Compile(string assemblyName, string source, string documentPath)
    {
        var sourceText = SourceText.From(source, Encoding.UTF8, SourceHashAlgorithm.Sha256);
        var tree = CSharpSyntaxTree.ParseText(sourceText, path: documentPath);
        var compilation = CSharpCompilation.Create(assemblyName, [tree], PlatformReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release, deterministic: true));
        using var assembly = new MemoryStream();
        using var pdb = new MemoryStream();
        var result = compilation.Emit(assembly, pdb,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return new CompiledFixture(assembly.ToArray(), pdb.ToArray());
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not find the repository root containing global.json.");
    }

    private sealed record CompiledFixture(byte[] Assembly, byte[] Pdb);
}
