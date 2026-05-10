namespace ForgeTrust.Runnable.Web.RazorWire.Cli.Tests;

public class ExportValidationExceptionTests
{
    [Fact]
    public void Constructor_Should_Format_Empty_Diagnostic_List()
    {
        var ex = new ExportValidationException([]);

        Assert.Equal("CDN export validation failed.", ex.Message);
        Assert.Empty(ex.Diagnostics);
    }

    [Fact]
    public void Constructor_Should_Format_Populated_Diagnostic_List()
    {
        var diagnostics = new[]
        {
            new ExportDiagnostic("RWEXPORT003", "Required asset was not exported.", "/docs/start"),
            new ExportDiagnostic("RWEXPORT004", "Managed URL could not be rewritten.", "/about")
        };

        var ex = new ExportValidationException(diagnostics);

        Assert.Same(diagnostics, ex.Diagnostics);
        Assert.Contains("CDN export validation failed:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RWEXPORT003: Required asset was not exported.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RWEXPORT004: Managed URL could not be rewritten.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_Should_Throw_When_Diagnostics_Is_Null()
    {
        Assert.Throws<ArgumentNullException>(() => new ExportValidationException(null!));
    }
}
