namespace ForgeTrust.AppSurface.Web.Tests;

public sealed class PwaOptionsTests
{
    [Fact]
    public void DefaultOptions_DisablePwa()
    {
        var options = PwaOptions.Default;

        Assert.False(options.Enabled);
        Assert.Equal("/manifest.webmanifest", options.ManifestPath);
        Assert.False(options.Offline.Enabled);
    }

    [Fact]
    public void Validate_DisabledOptions_HaveNoDiagnostics()
    {
        var diagnostics = PwaOptionsValidator.Validate(new PwaOptions());

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Validate_RejectsNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => PwaOptionsValidator.Validate(null!));
    }

    [Fact]
    public void ThrowIfInvalid_ReportsMissingRequiredFields()
    {
        var options = new PwaOptions { Enabled = true };

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains("ASPWA001", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ASPWA010", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ASPWA011", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("//cdn.example.com/manifest.webmanifest", "ASPWA005")]
    [InlineData("/manifest.webmanifest?version=1", "ASPWA005")]
    [InlineData("/../manifest.webmanifest", "ASPWA005")]
    [InlineData("/%2e%2e/manifest.webmanifest", "ASPWA005")]
    [InlineData("manifest.webmanifest", "ASPWA005")]
    public void ThrowIfInvalid_RejectsUnsafeManifestPaths(string manifestPath, string expectedCode)
    {
        var options = CreateValidOptions();
        options.ManifestPath = manifestPath;

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains(expectedCode, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_RequiresOfflineFallback_WhenOfflineIsEnabled()
    {
        var options = CreateValidOptions();
        options.Offline.Enabled = true;
        options.Offline.OfflineFallbackPath = string.Empty;

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains("ASPWA016", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_ReportsInvalidDisplayIconAndOfflineAssetPaths()
    {
        var options = CreateValidOptions();
        options.Display = (PwaDisplayMode)999;
        options.Icons.Add(new PwaIcon { Source = "https://cdn.example.test/icon.png", Sizes = "0x0", Type = string.Empty });
        options.Offline.Enabled = true;
        options.Offline.ServiceWorkerPath = "//cdn.example.test/service-worker.js";
        options.Offline.OfflineFallbackPath = "/offline.html";
        options.Offline.StaticAssetPaths = ["/css/site.css#hash", "/%2e%2e/secret.txt"];

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains("ASPWA009", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ASPWA012", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ASPWA013", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ASPWA014", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ASPWA015", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ASPWA017", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_AcceptsValidOptions()
    {
        var options = CreateValidOptions();
        options.Offline.Enabled = true;
        options.Offline.OfflineFallbackPath = "/offline.html";
        options.Offline.StaticAssetPaths = ["/css/site.css"];

        var exception = Record.Exception(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Null(exception);
    }

    internal static PwaOptions CreateValidOptions()
    {
        var options = new PwaOptions
        {
            Enabled = true,
            Name = "Field Notes",
            ShortName = "Notes",
            ThemeColor = "#2563eb",
            BackgroundColor = "#ffffff"
        };
        options.Icons.Add(new PwaIcon { Source = "/icons/app-192.png", Sizes = "192x192", Type = "image/png" });
        options.Icons.Add(new PwaIcon { Source = "/icons/app-512.png", Sizes = "512x512", Type = "image/png" });

        return options;
    }
}
