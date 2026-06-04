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

    [Fact]
    public async Task AddDeploymentExtra_Should_Register_Absolute_Source_And_PublishPath()
    {
        var tempDir = Directory.CreateTempSubdirectory("razorwire-export-context-").FullName;
        try
        {
            var sourcePath = Path.Join(tempDir, "CNAME");
            await File.WriteAllTextAsync(sourcePath, "docs.example.com");
            var context = new ExportContext("dist", null, "http://localhost:5000");

            context.AddDeploymentExtra(sourcePath, "/CNAME");

            var extra = Assert.Single(context.DeploymentExtras);
            Assert.Equal(sourcePath, extra.SourcePath);
            Assert.Equal("/CNAME", extra.PublishPath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task AddDeploymentExtra_Should_Reject_Duplicate_Targets_Case_Insensitively()
    {
        var tempDir = Directory.CreateTempSubdirectory("razorwire-export-context-").FullName;
        try
        {
            var first = Path.Join(tempDir, "CNAME");
            var second = Path.Join(tempDir, "cname.txt");
            await File.WriteAllTextAsync(first, "docs.example.com");
            await File.WriteAllTextAsync(second, "docs.example.net");
            var context = new ExportContext("dist", null, "http://localhost:5000");

            context.AddDeploymentExtra(first, "/CNAME");
            var exception = Assert.Throws<ExportValidationException>(() => context.AddDeploymentExtra(second, "/cname"));

            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal("RWEXPORT007", diagnostic.Code);
            Assert.Contains("[target-duplicate]", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
