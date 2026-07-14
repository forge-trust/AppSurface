using Microsoft.Extensions.Configuration;

namespace ForgeTrust.AppSurface.Web.Tests;

public sealed class PwaOptionsTests
{
    [Fact]
    public void ScriptAssets_ReadRejectsMissingEmbeddedResource()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => PwaScriptAssets.Read(typeof(PwaOptionsTests).Assembly, "missing.js"));

        Assert.Contains("missing.js", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedPathVectors_MatchCSharpValidation()
    {
        using var document = System.Text.Json.JsonDocument.Parse(PwaScriptAssets.PathValidationVectors);
        foreach (var vector in document.RootElement.GetProperty("assetPaths").EnumerateArray())
        {
            Assert.Equal(
                vector.GetProperty("valid").GetBoolean(),
                PwaOptionsValidator.IsSafeLocalPath(vector.GetProperty("value").GetString()));
        }

        foreach (var vector in document.RootElement.GetProperty("destinationPaths").EnumerateArray())
        {
            Assert.Equal(
                vector.GetProperty("valid").GetBoolean(),
                PwaOptionsValidator.IsSafeLocalStartUrl(vector.GetProperty("value").GetString()));
        }
    }

    [Fact]
    public void DefaultOptions_DisablePwa()
    {
        var options = PwaOptions.Default;

        Assert.False(options.Enabled);
        Assert.Equal("/manifest.webmanifest", options.ManifestPath);
        Assert.False(options.Offline.Enabled);
        Assert.False(options.Push.Enabled);
        Assert.Equal("/service-worker.js", options.Worker.ServiceWorkerPath);
        Assert.Equal("/service-worker.js", options.Offline.ServiceWorkerPath);
        Assert.Equal("/_appsurface/pwa/register.js", options.Worker.RegistrationHelperPath);
    }

    [Fact]
    public void Validate_PushOnly_DoesNotRequireInstallMetadata()
    {
        var options = new PwaOptions();
        options.Push.Enabled = true;

        var diagnostics = PwaOptionsValidator.Validate(options);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code is "ASPWA001" or "ASPWA010" or "ASPWA011");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == PwaDiagnosticSeverity.Error);
    }

    [Fact]
    public void WorkerPath_NewPropertyUpdatesCompatibilityAlias()
    {
        var options = new PwaOptions();

        options.Worker.ServiceWorkerPath = "/workers/push.js";

        Assert.Equal("/workers/push.js", options.Offline.ServiceWorkerPath);
    }

    [Fact]
    public void WorkerPath_CompatibilityAliasUpdatesNewProperty()
    {
        var options = new PwaOptions();

        options.Offline.ServiceWorkerPath = "/workers/offline.js";

        Assert.Equal("/workers/offline.js", options.Worker.ServiceWorkerPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WorkerPath_EqualExplicitValuesAreAssignmentOrderIndependent(bool legacyFirst)
    {
        var options = new PwaOptions();
        if (legacyFirst)
        {
            options.Offline.ServiceWorkerPath = "/workers/app.js";
            options.Worker.ServiceWorkerPath = "/workers/app.js";
        }
        else
        {
            options.Worker.ServiceWorkerPath = "/workers/app.js";
            options.Offline.ServiceWorkerPath = "/workers/app.js";
        }

        options.Push.Enabled = true;

        Assert.Null(Record.Exception(() => PwaOptionsValidator.ThrowIfInvalid(options)));
        Assert.Equal("/workers/app.js", options.Worker.ServiceWorkerPath);
        Assert.Equal("/workers/app.js", options.Offline.ServiceWorkerPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WorkerPath_ConflictingExplicitValuesFailRegardlessOfAssignmentOrder(bool legacyFirst)
    {
        var options = new PwaOptions();
        if (legacyFirst)
        {
            options.Offline.ServiceWorkerPath = "/workers/legacy.js";
            options.Worker.ServiceWorkerPath = "/workers/current.js";
        }
        else
        {
            options.Worker.ServiceWorkerPath = "/workers/current.js";
            options.Offline.ServiceWorkerPath = "/workers/legacy.js";
        }

        options.Push.Enabled = true;

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));
        Assert.Contains("ASPWA020", exception.Message, StringComparison.Ordinal);
        Assert.Equal("/workers/current.js", options.Worker.ServiceWorkerPath);
    }

    [Fact]
    public void StandaloneOfflineOptionsRetainIndependentWorkerPath()
    {
        var offline = new PwaOfflineOptions();

        offline.ServiceWorkerPath = "/standalone.js";

        Assert.Equal("/standalone.js", offline.ServiceWorkerPath);
        Assert.Equal("/service-worker.js", new PwaWorkerOptions().ServiceWorkerPath);
    }

    [Fact]
    public void ConfigurationBindingPopulatesNestedWorkerPushAndLegacyOptions()
    {
        var options = new PwaOptions();
        Bind(
            options,
            new Dictionary<string, string?>
            {
                ["Worker:ServiceWorkerPath"] = "/workers/app.js",
                ["Worker:RegistrationHelperPath"] = "/workers/register.js",
                ["Offline:ServiceWorkerPath"] = "/workers/app.js",
                ["Push:Enabled"] = "true",
                ["Push:HandlerScriptPath"] = "/workers/push.js"
            });

        Assert.Equal("/workers/app.js", options.Worker.ServiceWorkerPath);
        Assert.Equal("/workers/app.js", options.Offline.ServiceWorkerPath);
        Assert.Equal("/workers/register.js", options.Worker.RegistrationHelperPath);
        Assert.True(options.Push.Enabled);
        Assert.Equal("/workers/push.js", options.Push.HandlerScriptPath);
        Assert.Null(Record.Exception(() => PwaOptionsValidator.ThrowIfInvalid(options)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfigurationBindingDetectsAliasConflictsInEitherBindingOrder(bool legacyFirst)
    {
        var options = new PwaOptions();
        var legacy = new Dictionary<string, string?> { ["Offline:ServiceWorkerPath"] = "/workers/legacy.js" };
        var current = new Dictionary<string, string?> { ["Worker:ServiceWorkerPath"] = "/workers/current.js" };

        Bind(options, legacyFirst ? legacy : current);
        Bind(options, legacyFirst ? current : legacy);

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));
        Assert.Contains("ASPWA020", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_DisabledOptionsRejectConflictingWorkerPathAliases()
    {
        var options = new PwaOptions();
        options.Worker.ServiceWorkerPath = "/workers/current.js";
        options.Offline.ServiceWorkerPath = "/workers/legacy.js";

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains("ASPWA020", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationBindingSupportsStandaloneLegacyOptions()
    {
        var options = new PwaOfflineOptions();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ServiceWorkerPath"] = "/workers/legacy.js" })
            .Build();

        configuration.Bind(options);

        Assert.Equal("/workers/legacy.js", options.ServiceWorkerPath);
    }

    [Theory]
    [InlineData("worker", "//cdn.example.test/worker.js", "ASPWA015")]
    [InlineData("worker", "/workers/%66oo.js", "ASPWA015")]
    [InlineData("helper", "/register.js?version=1", "ASPWA021")]
    [InlineData("helper", "/register/%66oo.js", "ASPWA021")]
    [InlineData("handler", "/%2e%2e/custom.js", "ASPWA022")]
    public void ThrowIfInvalid_RejectsUnsafeWorkerPaths(string target, string path, string expectedCode)
    {
        var options = new PwaOptions();
        options.Push.Enabled = true;
        switch (target)
        {
            case "worker":
                options.Worker.ServiceWorkerPath = path;
                break;
            case "helper":
                options.Worker.RegistrationHelperPath = path;
                break;
            default:
                options.Push.HandlerScriptPath = path;
                break;
        }

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains(expectedCode, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_RejectsKnownPwaRouteCollision()
    {
        var options = new PwaOptions();
        options.Push.Enabled = true;
        options.Worker.ServiceWorkerPath = "/_APPSURFACE/pwa/register.js/";

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains("ASPWA023", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_RejectsKnownPwaRouteCollisionAtRoot()
    {
        var options = new PwaOptions();
        options.Push.Enabled = true;
        options.Worker.ServiceWorkerPath = "/";
        options.Worker.RegistrationHelperPath = "/";

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains("ASPWA023", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/app%2fnotes/")]
    [InlineData("/%61pp/")]
    public void ThrowIfInvalid_RejectsPercentEscapedActiveWorkerScope(string scope)
    {
        var options = new PwaOptions { Scope = scope };
        options.Push.Enabled = true;

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains("ASPWA007", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_InstallOnlyPreservesSafePercentEscapedManifestScopeCompatibility()
    {
        var options = CreateValidOptions();
        options.StartUrl = "/%61pp/";
        options.Scope = "/%61pp/";

        Assert.Null(Record.Exception(() => PwaOptionsValidator.ThrowIfInvalid(options)));
    }

    [Fact]
    public void Validate_DisabledOptions_HaveNoDiagnostics()
    {
        var diagnostics = PwaOptionsValidator.Validate(new PwaOptions());

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ThrowIfInvalid_InstallOnlyRejectsUnsafeOrConflictingConfiguredWorkerPath()
    {
        var unsafeOptions = CreateValidOptions();
        unsafeOptions.Worker.ServiceWorkerPath = "/workers/%66oo.js";
        var unsafeException = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(unsafeOptions));
        Assert.Contains("ASPWA015", unsafeException.Message, StringComparison.Ordinal);

        var conflictingOptions = CreateValidOptions();
        conflictingOptions.Worker.ServiceWorkerPath = "/workers/current.js";
        conflictingOptions.Offline.ServiceWorkerPath = "/workers/legacy.js";
        var conflictException = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(conflictingOptions));
        Assert.Contains("ASPWA020", conflictException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_RejectsPercentEscapedDiagnosticsEndpoint()
    {
        var options = CreateValidOptions();
        options.DiagnosticsPath = "/_appsurface/%70wa";

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains("ASPWA008", exception.Message, StringComparison.Ordinal);
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
    [InlineData(PwaDisplayMode.Browser, "browser")]
    [InlineData(PwaDisplayMode.MinimalUi, "minimal-ui")]
    [InlineData(PwaDisplayMode.Standalone, "standalone")]
    [InlineData(PwaDisplayMode.Fullscreen, "fullscreen")]
    [InlineData((PwaDisplayMode)999, "999")]
    public void FormatDisplayMode_ReturnsManifestDisplayValue(PwaDisplayMode displayMode, string expected)
    {
        Assert.Equal(expected, PwaOptionsValidator.FormatDisplayMode(displayMode));
    }

    [Theory]
    [InlineData("//cdn.example.com/manifest.webmanifest", "ASPWA005")]
    [InlineData("/manifest.webmanifest?version=1", "ASPWA005")]
    [InlineData("/manifest {tenant}.webmanifest", "ASPWA005")]
    [InlineData("/../manifest.webmanifest", "ASPWA005")]
    [InlineData("/%2e%2e/manifest.webmanifest", "ASPWA005")]
    [InlineData("/%/manifest.webmanifest", "ASPWA005")]
    [InlineData("/%zz/manifest.webmanifest", "ASPWA005")]
    [InlineData("manifest.webmanifest", "ASPWA005")]
    [InlineData(" /manifest.webmanifest", "ASPWA005")]
    [InlineData("/manifest.webmanifest ", "ASPWA005")]
    public void ThrowIfInvalid_RejectsUnsafeManifestPaths(string manifestPath, string expectedCode)
    {
        var options = CreateValidOptions();
        options.ManifestPath = manifestPath;

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains(expectedCode, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_ReportsUnsafeUrlsDiagnosticsPathAndColors()
    {
        var options = CreateValidOptions();
        options.ThemeColor = "blue";
        options.BackgroundColor = "#12";
        options.StartUrl = "https://example.test/start";
        options.Scope = "//cdn.example.test/";
        options.DiagnosticsPath = "/_appsurface/pwa#status";

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains("ASPWA003", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ASPWA004", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ASPWA006", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ASPWA007", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ASPWA008", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_AcceptsStartUrlWithQuery()
    {
        var options = CreateValidOptions();
        options.StartUrl = "/?source=pwa";

        var exception = Record.Exception(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Null(exception);
    }

    [Fact]
    public void ThrowIfInvalid_RejectsStartUrlOutsideScope()
    {
        var options = CreateValidOptions();
        options.StartUrl = "/admin/?source=pwa";
        options.Scope = "/app/";

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains("ASPWA019", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_AcceptsStartUrlInsideScope()
    {
        var options = CreateValidOptions();
        options.StartUrl = "/app/dashboard?source=pwa";
        options.Scope = "/app/";

        var exception = Record.Exception(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Null(exception);
    }

    [Fact]
    public void ThrowIfInvalid_AcceptsBrowserPrefixScopeWithoutTrailingSlash()
    {
        var options = CreateValidOptions();
        options.StartUrl = "/app";
        options.Scope = "/app";

        var exception = Record.Exception(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Null(exception);
    }

    [Fact]
    public void ThrowIfInvalid_UsesBrowserRawPrefixScopeSemantics()
    {
        var options = CreateValidOptions();
        options.StartUrl = "/application";
        options.Scope = "/app";

        Assert.Null(Record.Exception(() => PwaOptionsValidator.ThrowIfInvalid(options)));
    }

    [Fact]
    public void ThrowIfInvalid_RejectsStartUrlEqualToScopeWithoutRequiredTrailingSlash()
    {
        var options = CreateValidOptions();
        options.StartUrl = "/app";
        options.Scope = "/app/";

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains("ASPWA019", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_AcceptsSafeEscapedStartUrlButRejectsEscapedEndpoint()
    {
        var options = CreateValidOptions();
        options.ManifestPath = "/manifests/%66ield.webmanifest";
        options.StartUrl = "/start%2fnotes?source=pwa";

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains("ASPWA005", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("//cdn.example.test/start")]
    [InlineData("https://example.test/start")]
    [InlineData("/start#launch")]
    [InlineData("/start {tenant}?source=pwa")]
    [InlineData("/../start?source=pwa")]
    [InlineData("/%2e%2e/start?source=pwa")]
    [InlineData("/%/start?source=pwa")]
    [InlineData("/%zz/start?source=pwa")]
    [InlineData("/start\\admin?source=pwa")]
    [InlineData(" /start?source=pwa")]
    [InlineData("/start?source=pwa ")]
    public void ThrowIfInvalid_RejectsUnsafeStartUrls(string startUrl)
    {
        var options = CreateValidOptions();
        options.StartUrl = startUrl;

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains("ASPWA006", exception.Message, StringComparison.Ordinal);
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

    [Fact]
    public void ThrowIfInvalid_AcceptsMultipleIconSizeTokens()
    {
        var options = CreateValidOptions();
        options.Icons.Clear();
        options.Icons.Add(new PwaIcon { Source = "/icons/app.png", Sizes = "192x192 512x512", Type = "image/png" });

        var exception = Record.Exception(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("")]
    [InlineData("192x192 nope")]
    public void ThrowIfInvalid_RejectsMissingOrMalformedIconSizeTokens(string sizes)
    {
        var options = CreateValidOptions();
        options.Icons.Clear();
        options.Icons.Add(new PwaIcon { Source = "/icons/app.png", Sizes = sizes, Type = "image/png" });

        var exception = Assert.Throws<InvalidOperationException>(() => PwaOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains("ASPWA013", exception.Message, StringComparison.Ordinal);
    }

    private static void Bind(PwaOptions options, IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        configuration.Bind(options);
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
