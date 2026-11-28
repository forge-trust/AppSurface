using System.Text.Json;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInCompilerSymbolsTests
{
    [Theory]
    [InlineData("capture", true, true)]
    [InlineData("collect", true, true)]
    [InlineData("capture", false, false)]
    [InlineData("verify", true, false)]
    public void Compiler_symbols_are_admitted_only_at_the_observation_boundary(string operation, bool useAsyncProfile,
        bool accepted)
    {
        using var temp = TestDirectory.Create();
        var assembly = TestPathUtils.PathUnder(temp.Path, "Example.dll");
        var request = new CoverageFanInEngineRequest(Policy(useAsyncProfile), temp.Path)
        {
            CompilerSymbolAssemblies = [assembly]
        };

        if (accepted)
            CoverageFanInCompilerSymbols.ValidateRequest(operation, request);
        else
            Assert.Throws<ArgumentException>(() => CoverageFanInCompilerSymbols.ValidateRequest(operation, request));
    }

    [Fact]
    public void Primary_and_import_must_use_their_declared_compiler_evidence()
    {
        using var temp = TestDirectory.Create();
        var request = new CoverageFanInEngineRequest(Policy(useAsyncProfile: true), temp.Path);
        Assert.Throws<ArgumentException>(() => CoverageFanInCompilerSymbols.ValidateRequest("capture", request));
        Assert.Throws<ArgumentException>(() => CoverageFanInCompilerSymbols.ValidateRequest("capture",
            request with { CompilerSymbolAssemblies = [] }));
        Assert.Throws<ArgumentException>(() => CoverageFanInCompilerSymbols.ValidateRequest("capture",
            request with { CompilerSymbolAssemblies = ["relative.dll"] }));
        var assembly = TestPathUtils.PathUnder(temp.Path, "Example.dll");
        Assert.Throws<ArgumentException>(() => CoverageFanInCompilerSymbols.ValidateRequest("capture",
            request with { CompilerSymbolAssemblies = [assembly, assembly] }));
        var oversized = Enumerable.Repeat(assembly, CoverageFanInCompilerSymbols.MaximumAssemblies + 1).ToArray();
        Assert.Throws<ArgumentException>(() => CoverageFanInCompilerSymbols.ValidateRequest("capture",
            request with { CompilerSymbolAssemblies = oversized }));
        Assert.Throws<InvalidDataException>(() => CoverageFanInCompilerSymbols.RetainedFileNames(
            JsonSerializer.SerializeToElement(Array.Empty<string>())));

        var import = request with { ImportDirectory = TestPathUtils.PathUnder(temp.Path, "import") };
        CoverageFanInCompilerSymbols.ValidateRequest("capture", import);
        Assert.Throws<ArgumentException>(() => CoverageFanInCompilerSymbols.ValidateRequest("capture",
            import with { CompilerSymbolAssemblies = [assembly] }));
    }

    [Theory]
    [InlineData("""{"compiler-0000.dll":"sha"}""")]
    [InlineData("""{"compiler-0001.dll":"sha","compiler-0001.pdb":"sha"}""")]
    [InlineData("""{"compiler-0000.dll":"sha","compiler-0000.dll":"sha"}""")]
    public void Retained_symbol_inventory_requires_exact_contiguous_pairs(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => CoverageFanInCompilerSymbols.RetainedFileNames(document.RootElement));
    }

    [Fact]
    public void Only_the_async_profile_uses_retained_symbols_and_requires_them_for_primary()
    {
        using var retained = JsonDocument.Parse("""{"coverage.cobertura.xml":"sha","compiler-0000.dll":"sha","compiler-0000.pdb":"sha"}""");
        var names = CoverageFanInCompilerSymbols.RetainedFileNames(retained.RootElement);
        Assert.Equal(new[] { "compiler-0000.dll", "compiler-0000.pdb" }, names);
        CoverageFanInCompilerSymbols.ValidateRetainedInventory(Policy(useAsyncProfile: true), "primary", names);
        CoverageFanInCompilerSymbols.ValidateRetainedInventory(Policy(useAsyncProfile: true), "supplement", []);
        Assert.Throws<InvalidDataException>(() =>
            CoverageFanInCompilerSymbols.ValidateRetainedInventory(Policy(useAsyncProfile: true), "primary", []));
        Assert.Throws<InvalidDataException>(() =>
            CoverageFanInCompilerSymbols.ValidateRetainedInventory(Policy(useAsyncProfile: false), "primary", names));
    }

    private static CoverageFanInExpected Policy(bool useAsyncProfile) => new(JsonSerializer.SerializeToElement(new
    {
        contractVersion = 3,
        reportProfileId = useAsyncProfile ? CoverageFanInReportProfiles.CoverletCoberturaAsync : CoverageFanInReportProfiles.CoverletCobertura
    }), "fixture");
}
