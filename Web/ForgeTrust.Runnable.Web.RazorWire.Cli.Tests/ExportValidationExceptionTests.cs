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
    public void Constructor_Should_Throw_When_Diagnostics_Is_Null()
    {
        Assert.Throws<ArgumentNullException>(() => new ExportValidationException(null!));
    }
}
