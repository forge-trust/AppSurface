namespace ForgeTrust.Runnable.Web.RazorWire.Cli.Tests;

public class ExportDiagnosticTests
{
    [Fact]
    public void Constructor_Should_Populate_Public_Validation_Context()
    {
        var diagnostic = new ExportDiagnostic("RWEXPORT003", "Required asset was not exported.", "/docs/start");

        Assert.Equal("RWEXPORT003", diagnostic.Code);
        Assert.Equal("Required asset was not exported.", diagnostic.Message);
        Assert.Equal("/docs/start", diagnostic.Route);
        Assert.Null(diagnostic.Reference);
    }
}
