namespace ForgeTrust.RazorWire.Cli.Tests;

public class ExportContextTests
{
    [Fact]
    public void Constructor_Should_Normalize_Origins_And_Preserve_Hybrid_Credentials()
    {
        var context = new ExportContext(
            outputPath: "dist",
            seedRoutesPath: null,
            initialSeedRoutes: null,
            baseUrl: "http://localhost:5000/",
            mode: ExportMode.Hybrid,
            redirectStrategy: ExportRedirectStrategy.Html,
            hybridOptions: new ExportHybridOptions
            {
                LiveOrigin = " https://api.example.com/ ",
                CredentialsMode = RazorWireHybridCredentialsMode.Include
            },
            publicOrigin: " https://docs.example.com/ ");

        Assert.Equal("http://localhost:5000", context.BaseUrl);
        Assert.Equal("https://api.example.com", context.Hybrid.LiveOrigin);
        Assert.Equal(RazorWireHybridCredentialsMode.Include, context.Hybrid.CredentialsMode);
        Assert.Equal("https://docs.example.com", context.PublicOrigin);
    }

    [Theory]
    [InlineData("https://api.example.com/path")]
    [InlineData("https://api.example.com?token=1")]
    [InlineData("https://user:pass@api.example.com")]
    [InlineData("ftp://api.example.com")]
    public void Constructor_Should_Reject_Invalid_Hybrid_Live_Origin(string liveOrigin)
    {
        var exception = Assert.Throws<ArgumentException>(() => new ExportContext(
            outputPath: "dist",
            seedRoutesPath: null,
            initialSeedRoutes: null,
            baseUrl: "http://localhost:5000",
            mode: ExportMode.Hybrid,
            redirectStrategy: ExportRedirectStrategy.Html,
            hybridOptions: new ExportHybridOptions { LiveOrigin = liveOrigin }));

        Assert.Equal("hybridOptions", exception.ParamName);
    }

    [Theory]
    [InlineData("https://docs.example.com/path")]
    [InlineData("https://docs.example.com#intro")]
    [InlineData("https://user@docs.example.com")]
    [InlineData("file:///tmp/docs")]
    public void Constructor_Should_Reject_Invalid_Public_Origin(string publicOrigin)
    {
        var exception = Assert.Throws<ArgumentException>(() => new ExportContext(
            outputPath: "dist",
            seedRoutesPath: null,
            initialSeedRoutes: null,
            baseUrl: "http://localhost:5000",
            mode: ExportMode.Cdn,
            redirectStrategy: ExportRedirectStrategy.Html,
            hybridOptions: null,
            publicOrigin: publicOrigin));

        Assert.Equal("publicOrigin", exception.ParamName);
    }
}
