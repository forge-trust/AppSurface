namespace ForgeTrust.RazorWire.Cli.Tests;

public class ExportHybridOptionsTests
{
    [Fact]
    public void Default_Should_Return_Fresh_Mutable_Instance()
    {
        var first = ExportHybridOptions.Default;
        first.LiveOrigin = "https://api.example.com";

        var second = ExportHybridOptions.Default;

        Assert.NotSame(first, second);
        Assert.Null(second.LiveOrigin);
    }
}
