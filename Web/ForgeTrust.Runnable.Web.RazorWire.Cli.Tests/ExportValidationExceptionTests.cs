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
}
