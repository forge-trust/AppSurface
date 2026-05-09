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

    [Theory]
    [InlineData(null, "message", "/route", "code")]
    [InlineData("", "message", "/route", "code")]
    [InlineData(" ", "message", "/route", "code")]
    [InlineData("RWEXPORT003", null, "/route", "message")]
    [InlineData("RWEXPORT003", "", "/route", "message")]
    [InlineData("RWEXPORT003", " ", "/route", "message")]
    [InlineData("RWEXPORT003", "message", null, "route")]
    [InlineData("RWEXPORT003", "message", "", "route")]
    [InlineData("RWEXPORT003", "message", " ", "route")]
    public void Constructor_Should_Throw_When_Required_Text_Is_Missing(
        string? code,
        string? message,
        string? route,
        string expectedParamName)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => new ExportDiagnostic(code!, message!, route!));

        Assert.Equal(expectedParamName, ex.ParamName);
    }
}
