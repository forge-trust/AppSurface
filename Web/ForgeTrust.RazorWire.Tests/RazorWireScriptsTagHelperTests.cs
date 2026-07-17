using FakeItEasy;
using ForgeTrust.AppSurface.Intelligence;
using ForgeTrust.RazorWire.TagHelpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.RazorWire.Tests;

public class RazorWireScriptsTagHelperTests
{
    private readonly IFileVersionProvider _fileVersionProvider;
    private readonly RazorWireScriptsTagHelper _helper;
    private readonly TagHelperContext _context;
    private readonly TagHelperOutput _output;
    private readonly ViewContext _viewContext;

    public RazorWireScriptsTagHelperTests()
    {
        _fileVersionProvider = A.Fake<IFileVersionProvider>();
        _helper = new RazorWireScriptsTagHelper(_fileVersionProvider);

        _context = new TagHelperContext(
            new TagHelperAttributeList(),
            new Dictionary<object, object>(),
            Guid.NewGuid().ToString("N"));

        _output = new TagHelperOutput(
            "rw:scripts",
            new TagHelperAttributeList(),
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

        var httpContext = new DefaultHttpContext();
        httpContext.Request.PathBase = "/my-app";
        _viewContext = new ViewContext { HttpContext = httpContext };
        _helper.ViewContext = _viewContext;
    }

    [Fact]
    public void Constructor_WithNullProvider_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new RazorWireScriptsTagHelper(null!));
    }

    [Fact]
    public void Process_GeneratesScriptTagsWithVersionAndPathBase()
    {
        // Arrange
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(
                "/my-app",
                "/_content/ForgeTrust.RazorWire/razorwire/turbo.es2017-umd.js"))
            .Returns("/my-app/_content/ForgeTrust.RazorWire/razorwire/turbo.es2017-umd.js?v=turbo");
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(
                "/my-app",
                "/_content/ForgeTrust.RazorWire/razorwire/razorwire.js"))
            .Returns("/my-app/_content/ForgeTrust.RazorWire/razorwire/razorwire.js?v=123");

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(
                "/my-app",
                "/_content/ForgeTrust.RazorWire/razorwire/razorwire.islands.js"))
            .Returns("/my-app/_content/ForgeTrust.RazorWire/razorwire/razorwire.islands.js?v=456");
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(
                "/my-app",
                "/_content/ForgeTrust.RazorWire/razorwire/behavior-kit.js"))
            .Returns("/my-app/_content/ForgeTrust.RazorWire/razorwire/behavior-kit.js?v=kit");
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(
                "/my-app",
                "/_content/ForgeTrust.RazorWire/razorwire/page-navigation.js"))
            .Returns("/my-app/_content/ForgeTrust.RazorWire/razorwire/page-navigation.js?v=789");
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(
                "/my-app",
                "/_content/ForgeTrust.RazorWire/razorwire/section-copy.js"))
            .Returns("/my-app/_content/ForgeTrust.RazorWire/razorwire/section-copy.js?v=abc");
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(
                "/my-app",
                "/_content/ForgeTrust.RazorWire/razorwire/form-interactions.js"))
            .Returns("/my-app/_content/ForgeTrust.RazorWire/razorwire/form-interactions.js?v=form");
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(
                "/my-app",
                "/_content/ForgeTrust.RazorWire/razorwire/behavior-kit.js"))
            .Returns("/my-app/_content/ForgeTrust.RazorWire/razorwire/behavior-kit.js?v=behavior");

        // Act
        _helper.Process(_context, _output);

        // Assert
        Assert.Null(_output.TagName); // Should remove the wrapper tag

        var content = _output.Content.GetContent();
        Assert.Contains(
            "src=\"/my-app/_content/ForgeTrust.RazorWire/razorwire/turbo.es2017-umd.js?v=turbo\"",
            content);
        Assert.DoesNotContain("cdn.jsdelivr.net", content);
        Assert.DoesNotContain("integrity=", content);
        Assert.DoesNotContain("crossorigin=", content);
        Assert.True(
            content.IndexOf("turbo.es2017-umd.js", StringComparison.Ordinal)
            < content.IndexOf("razorwire.js", StringComparison.Ordinal));
        Assert.Contains(
            "src=\"/my-app/_content/ForgeTrust.RazorWire/razorwire/razorwire.js?v=123\"",
            content);
        Assert.Contains(
            "src=\"/my-app/_content/ForgeTrust.RazorWire/razorwire/razorwire.islands.js?v=456\"",
            content);
        Assert.DoesNotContain("src=\"/my-app/_content/ForgeTrust.RazorWire/razorwire/behavior-kit.js?v=kit\"", content);
        Assert.DoesNotContain("data-rw-behavior-kit-runtime", content);
        Assert.DoesNotContain("src=\"/my-app/_content/ForgeTrust.RazorWire/razorwire/page-navigation.js?v=789\"", content);
        Assert.DoesNotContain("src=\"/my-app/_content/ForgeTrust.RazorWire/razorwire/section-copy.js?v=abc\"", content);
        Assert.DoesNotContain("src=\"/my-app/_content/ForgeTrust.RazorWire/razorwire/form-interactions.js?v=form\"", content);
        Assert.DoesNotContain("src=\"/my-app/_content/ForgeTrust.RazorWire/razorwire/behavior-kit.js?v=behavior\"", content);
        Assert.Contains("const selectors = [\"rw-page-nav\", \"[data-rw-page-nav]\"];", content);
        Assert.Contains("data-rw-page-navigation-runtime", content);
        Assert.Contains("RazorWirePageNavigationInitialized", content);
        Assert.Contains("data-rw-section-copy-runtime", content);
        Assert.Contains("RazorWireSectionCopyInitialized", content);
        Assert.Contains("\"[data-rw-section-copy]\", \"[data-rw-section-copy-target]\"", content);
        Assert.Contains("data-rw-form-interactions-runtime", content);
        Assert.Contains("RazorWireFormInteractionsInitialized", content);
        Assert.Contains("\"[data-rw-form-toggle]\", \"[data-rw-form-collection]\"", content);
        Assert.DoesNotContain("data-rw-behavior-kit-runtime", content);
        Assert.DoesNotContain("data-rw-behavior", content);
        Assert.Contains("turbo:frame-load", content);
    }

    [Fact]
    public void Process_WithCustomTurbo_EmitsVersionedSameOriginScriptFirst()
    {
        var options = new RazorWireOptions();
        options.Turbo.RuntimeMode = RazorWireTurboRuntimeMode.Custom;
        options.Turbo.CustomPath = "/assets/turbo.js";
        var helper = new RazorWireScriptsTagHelper(_fileVersionProvider, options) { ViewContext = _viewContext };
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .ReturnsLazily(call => call.GetArgument<string>(1)!);
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath("/my-app", "/assets/turbo.js"))
            .Returns("/my-app/assets/turbo.js?v=custom");

        helper.Process(_context, _output);

        var content = _output.Content.GetContent();
        Assert.Contains("<script src=\"/my-app/assets/turbo.js?v=custom\"></script>", content);
        Assert.True(
            content.IndexOf("/my-app/assets/turbo.js?v=custom", StringComparison.Ordinal)
            < content.IndexOf("razorwire.js", StringComparison.Ordinal));
        Assert.DoesNotContain("turbo.es2017-umd.js", content);
    }

    [Fact]
    public void Process_WithCustomTurbo_HtmlEncodesVersionProviderResult()
    {
        var options = new RazorWireOptions();
        options.Turbo.RuntimeMode = RazorWireTurboRuntimeMode.Custom;
        options.Turbo.CustomPath = "/assets/turbo.js";
        var helper = new RazorWireScriptsTagHelper(_fileVersionProvider, options) { ViewContext = _viewContext };
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .ReturnsLazily(call => call.GetArgument<string>(1)!);
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath("/my-app", "/assets/turbo.js"))
            .Returns("/assets/turbo.js?value=\"<&");

        helper.Process(_context, _output);

        var content = _output.Content.GetContent();
        Assert.Contains("src=\"/assets/turbo.js?value=&quot;&lt;&amp;\"", content);
        Assert.DoesNotContain("value=\"<&", content);
    }

    [Fact]
    public void Process_WithHostManagedTurbo_OmitsTurboScript()
    {
        var options = new RazorWireOptions();
        options.Turbo.RuntimeMode = RazorWireTurboRuntimeMode.HostManaged;
        var helper = new RazorWireScriptsTagHelper(_fileVersionProvider, options) { ViewContext = _viewContext };
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .ReturnsLazily(call => call.GetArgument<string>(1)!);

        helper.Process(_context, _output);

        var content = _output.Content.GetContent();
        Assert.DoesNotContain("turbo.es2017-umd.js", content);
        Assert.DoesNotContain("cdn.jsdelivr.net", content);
        Assert.Contains("razorwire.js", content);
    }

    [Fact]
    public void Process_WithUndefinedTurboMode_ThrowsActionableInvalidOperationException()
    {
        var options = new RazorWireOptions();
        options.Turbo.RuntimeMode = (RazorWireTurboRuntimeMode)42;
        var helper = new RazorWireScriptsTagHelper(_fileVersionProvider, options) { ViewContext = _viewContext };

        var exception = Assert.Throws<InvalidOperationException>(() => helper.Process(_context, _output));

        Assert.Contains("RazorWireOptions.Turbo.RuntimeMode", exception.Message);
        Assert.Contains("Bundled, Custom, or HostManaged", exception.Message);
        Assert.Contains("42", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://cdn.example.com/turbo.js")]
    public void Process_WithInvalidCustomTurboPath_ThrowsActionableInvalidOperationException(string? customPath)
    {
        var options = new RazorWireOptions();
        options.Turbo.RuntimeMode = RazorWireTurboRuntimeMode.Custom;
        options.Turbo.CustomPath = customPath;
        var helper = new RazorWireScriptsTagHelper(_fileVersionProvider, options) { ViewContext = _viewContext };

        var exception = Assert.Throws<InvalidOperationException>(() => helper.Process(_context, _output));

        Assert.Contains("RazorWireOptions.Turbo.CustomPath", exception.Message);
        Assert.Contains("exactly one '/'", exception.Message);
    }

    [Fact]
    public void Process_WhenBehaviorKitEnabled_RendersEagerScript()
    {
        // Arrange
        var helper = new RazorWireScriptsTagHelper(_fileVersionProvider)
        {
            ViewContext = _viewContext,
            BehaviorKit = true
        };

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .ReturnsLazily(call => call.GetArgument<string>(1)!);
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(
                "/my-app",
                "/_content/ForgeTrust.RazorWire/razorwire/behavior-kit.js"))
            .Returns("/my-app/_content/ForgeTrust.RazorWire/razorwire/behavior-kit.js?v=kit");

        // Act
        helper.Process(_context, _output);

        // Assert
        var content = _output.Content.GetContent();
        Assert.Contains(
            "src=\"/my-app/_content/ForgeTrust.RazorWire/razorwire/behavior-kit.js?v=kit\"",
            content);
        Assert.Contains("data-rw-behavior-kit-runtime=\"eager\"", content);
        Assert.Contains("data-rw-page-navigation-runtime", content);
        Assert.Contains("data-rw-section-copy-runtime", content);
        Assert.Contains("data-rw-form-interactions-runtime", content);
    }

    [Fact]
    public void Process_WhenPageNavigationEnabled_RendersEagerScript()
    {
        // Arrange
        var helper = new RazorWireScriptsTagHelper(_fileVersionProvider)
        {
            ViewContext = _viewContext,
            PageNavigation = true
        };

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .ReturnsLazily(call => call.GetArgument<string>(1)!);
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(
                "/my-app",
                "/_content/ForgeTrust.RazorWire/razorwire/page-navigation.js"))
            .Returns("/my-app/_content/ForgeTrust.RazorWire/razorwire/page-navigation.js?v=789");

        // Act
        helper.Process(_context, _output);

        // Assert
        var content = _output.Content.GetContent();
        Assert.Contains(
            "src=\"/my-app/_content/ForgeTrust.RazorWire/razorwire/page-navigation.js?v=789\"",
            content);
        Assert.Contains("data-rw-page-navigation-runtime=\"eager\"", content);
        Assert.DoesNotContain("const selectors = [\"rw-page-nav\", \"[data-rw-page-nav]\"];", content);
        Assert.Contains("data-rw-section-copy-runtime", content);
        Assert.Contains("data-rw-form-interactions-runtime", content);
    }

    [Fact]
    public void Process_WhenSectionCopyEnabled_RendersEagerScript()
    {
        // Arrange
        var helper = new RazorWireScriptsTagHelper(_fileVersionProvider)
        {
            ViewContext = _viewContext,
            SectionCopy = true
        };

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .ReturnsLazily(call => call.GetArgument<string>(1)!);
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(
                "/my-app",
                "/_content/ForgeTrust.RazorWire/razorwire/section-copy.js"))
            .Returns("/my-app/_content/ForgeTrust.RazorWire/razorwire/section-copy.js?v=abc");

        // Act
        helper.Process(_context, _output);

        // Assert
        var content = _output.Content.GetContent();
        Assert.Contains(
            "src=\"/my-app/_content/ForgeTrust.RazorWire/razorwire/section-copy.js?v=abc\"",
            content);
        Assert.Contains("data-rw-section-copy-runtime=\"eager\"", content);
        Assert.DoesNotContain("\"[data-rw-section-copy]\", \"[data-rw-section-copy-target]\"", content);
        Assert.Contains("data-rw-page-navigation-runtime", content);
        Assert.Contains("data-rw-form-interactions-runtime", content);
    }

    [Fact]
    public void Process_WhenFormInteractionsEnabled_RendersEagerScript()
    {
        // Arrange
        var helper = new RazorWireScriptsTagHelper(_fileVersionProvider)
        {
            ViewContext = _viewContext,
            FormInteractions = true
        };

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .ReturnsLazily(call => call.GetArgument<string>(1)!);
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(
                "/my-app",
                "/_content/ForgeTrust.RazorWire/razorwire/form-interactions.js"))
            .Returns("/my-app/_content/ForgeTrust.RazorWire/razorwire/form-interactions.js?v=form");

        // Act
        helper.Process(_context, _output);

        // Assert
        var content = _output.Content.GetContent();
        Assert.Contains(
            "src=\"/my-app/_content/ForgeTrust.RazorWire/razorwire/form-interactions.js?v=form\"",
            content);
        Assert.Contains("data-rw-form-interactions-runtime=\"eager\"", content);
        Assert.DoesNotContain("\"[data-rw-form-toggle]\", \"[data-rw-form-collection]\"", content);
        Assert.Contains("data-rw-page-navigation-runtime", content);
        Assert.Contains("data-rw-section-copy-runtime", content);
    }

    [Fact]
    public void Process_WhenBehaviorKitEnabled_RendersEagerScriptWithoutLazyDetector()
    {
        // Arrange
        var helper = new RazorWireScriptsTagHelper(_fileVersionProvider)
        {
            ViewContext = _viewContext,
            BehaviorKit = true
        };

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .ReturnsLazily(call => call.GetArgument<string>(1)!);
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(
                "/my-app",
                "/_content/ForgeTrust.RazorWire/razorwire/behavior-kit.js"))
            .Returns("/my-app/_content/ForgeTrust.RazorWire/razorwire/behavior-kit.js?v=behavior");

        // Act
        helper.Process(_context, _output);

        // Assert
        var content = _output.Content.GetContent();
        Assert.Contains(
            "src=\"/my-app/_content/ForgeTrust.RazorWire/razorwire/behavior-kit.js?v=behavior\"",
            content);
        Assert.Contains("data-rw-behavior-kit-runtime=\"eager\"", content);
        Assert.DoesNotContain("RazorWireBehaviorKitInitialized\",", content);
        Assert.DoesNotContain("data-rw-behavior\",", content);
        Assert.Contains("data-rw-page-navigation-runtime", content);
        Assert.Contains("data-rw-section-copy-runtime", content);
        Assert.Contains("data-rw-form-interactions-runtime", content);
    }

    [Fact]
    public void RazorWireProject_DefinesPackOnlyGeneratedAssetGuardWithEmergencyBypass()
    {
        var repoRoot = FindRepositoryRoot();
        var projectPath = Path.Join(
            repoRoot,
            "Web",
            "ForgeTrust.RazorWire",
            "ForgeTrust.RazorWire.csproj");
        var project = File.ReadAllText(projectPath);

        Assert.Contains("BeforeTargets=\"GenerateNuspec\"", project, StringComparison.Ordinal);
        Assert.Contains("VerifyRazorWireGeneratedAssetsBeforePack", project, StringComparison.Ordinal);
        Assert.Contains("assets:razorwire:verify", project, StringComparison.Ordinal);
        Assert.Contains("RWPACK001", project, StringComparison.Ordinal);
        Assert.Contains("razorwire\\behavior-kit.js", project, StringComparison.Ordinal);
        Assert.Contains("razorwire\\section-copy.js", project, StringComparison.Ordinal);
        Assert.Contains("razorwire\\form-interactions.js", project, StringComparison.Ordinal);
        Assert.Contains("""<Content Remove="assets\**\*" />""", project, StringComparison.Ordinal);
        Assert.Contains("""<None Remove="assets\**\*" />""", project, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Join(directory.FullName, "ForgeTrust.AppSurface.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to find repository root from the current test assembly path.");
    }

    [Fact]
    public void Process_InDevelopment_EmitsDiagnosticsConfigOnRuntimeScript()
    {
        // Arrange
        var options = new RazorWireOptions();
        options.Forms.DefaultFailureMessage = "Custom \"failure\" & retry";
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Development };
        var helper = new RazorWireScriptsTagHelper(_fileVersionProvider, options, environment)
        {
            ViewContext = _viewContext
        };

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .ReturnsLazily(call => call.GetArgument<string>(1)!);

        // Act
        helper.Process(_context, _output);

        // Assert
        var content = _output.Content.GetContent();
        Assert.Contains("data-rw-development-diagnostics=\"true\"", content);
        Assert.Contains("data-rw-form-failure-enabled=\"true\"", content);
        Assert.Contains("data-rw-form-failure-mode=\"auto\"", content);
        Assert.Contains("data-rw-default-failure-message=\"Custom &quot;failure&quot; &amp; retry\"", content);
    }

    [Fact]
    public void Process_WhenRazorWireEventIsAllowlisted_EnablesProductIntelligenceRuntimeConfig()
    {
        // Arrange
        var productIntelligenceOptions = new AppSurfaceProductIntelligenceOptions()
            .EnableExperimentalEvents(AppSurfaceProductEventRegistry.RazorWireFormFailed);
        var helper = new RazorWireScriptsTagHelper(
            _fileVersionProvider,
            productIntelligenceOptions: Options.Create(productIntelligenceOptions))
        {
            ViewContext = _viewContext
        };

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .ReturnsLazily(call => call.GetArgument<string>(1)!);

        // Act
        helper.Process(_context, _output);

        // Assert
        var content = _output.Content.GetContent();
        Assert.Contains("data-rw-product-intelligence-enabled=\"true\"", content);
    }

    [Fact]
    public void Process_WhenFailureUxDisabled_EmitsOffRuntimeMode()
    {
        // Arrange
        var options = new RazorWireOptions();
        options.Forms.EnableFailureUx = false;
        options.Forms.FailureMode = RazorWireFormFailureMode.Auto;
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Development };
        var helper = new RazorWireScriptsTagHelper(_fileVersionProvider, options, environment)
        {
            ViewContext = _viewContext
        };

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .ReturnsLazily(call => call.GetArgument<string>(1)!);

        // Act
        helper.Process(_context, _output);

        // Assert
        var content = _output.Content.GetContent();
        Assert.Contains("data-rw-development-diagnostics=\"false\"", content);
        Assert.Contains("data-rw-form-failure-enabled=\"false\"", content);
        Assert.Contains("data-rw-form-failure-mode=\"off\"", content);
    }

    [Fact]
    public void Process_WhenDevelopmentDiagnosticsDisabled_EmitsFailureUxWithoutDiagnostics()
    {
        // Arrange
        var options = new RazorWireOptions();
        options.Forms.EnableDevelopmentDiagnostics = false;
        options.Forms.FailureMode = RazorWireFormFailureMode.Manual;
        options.Forms.DefaultFailureMessage = "Custom failure";
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Development };
        var helper = new RazorWireScriptsTagHelper(_fileVersionProvider, options, environment)
        {
            ViewContext = _viewContext
        };

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .ReturnsLazily(call => call.GetArgument<string>(1)!);

        // Act
        helper.Process(_context, _output);

        // Assert
        var content = _output.Content.GetContent();
        Assert.Contains("data-rw-development-diagnostics=\"false\"", content);
        Assert.Contains("data-rw-form-failure-enabled=\"true\"", content);
        Assert.Contains("data-rw-form-failure-mode=\"manual\"", content);
        Assert.Contains("data-rw-default-failure-message=\"Custom failure\"", content);
    }

    [Fact]
    public void Process_WhenHybridOptionsConfigured_EmitsRuntimeHybridConfig()
    {
        // Arrange
        var options = new RazorWireOptions();
        options.Hybrid.LiveOrigin = "https://api.example.com";
        options.Hybrid.CredentialsMode = RazorWireHybridCredentialsMode.Include;
        options.Forms.Antiforgery.TokenEndpointPath = "/tokens/antiforgery";
        var helper = new RazorWireScriptsTagHelper(_fileVersionProvider, options)
        {
            ViewContext = _viewContext
        };

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .ReturnsLazily(call => call.GetArgument<string>(1)!);

        // Act
        helper.Process(_context, _output);

        // Assert
        var content = _output.Content.GetContent();
        Assert.Contains("data-rw-live-origin=\"https://api.example.com\"", content);
        Assert.Contains("data-rw-hybrid-credentials=\"include\"", content);
        Assert.Contains("data-rw-antiforgery-endpoint=\"/my-app/tokens/antiforgery\"", content);
    }

    [Fact]
    public void Process_WhenHybridCredentialsAutoAndLiveOriginConfigured_EmitsIncludeRuntimeMode()
    {
        // Arrange
        var options = new RazorWireOptions();
        options.Hybrid.LiveOrigin = " https://api.example.com/ ";
        options.Hybrid.CredentialsMode = RazorWireHybridCredentialsMode.Auto;
        var helper = new RazorWireScriptsTagHelper(_fileVersionProvider, options)
        {
            ViewContext = _viewContext
        };

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .ReturnsLazily(call => call.GetArgument<string>(1)!);

        // Act
        helper.Process(_context, _output);

        // Assert
        var content = _output.Content.GetContent();
        Assert.Contains("data-rw-live-origin=\"https://api.example.com\"", content);
        Assert.Contains("data-rw-hybrid-credentials=\"include\"", content);
    }

    [Theory]
    [InlineData("https://api.example.com/path")]
    [InlineData("https://api.example.com?tenant=forge")]
    [InlineData("https://api.example.com#forms")]
    [InlineData("https://user:pass@api.example.com")]
    [InlineData("ftp://api.example.com")]
    [InlineData("not-url")]
    public void Process_WhenHybridLiveOriginIsNotAnOrigin_ThrowsInvalidOperationException(string liveOrigin)
    {
        // Arrange
        var options = new RazorWireOptions();
        options.Hybrid.LiveOrigin = liveOrigin;
        var helper = new RazorWireScriptsTagHelper(_fileVersionProvider, options)
        {
            ViewContext = _viewContext
        };

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .ReturnsLazily(call => call.GetArgument<string>(1)!);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => helper.Process(_context, _output));

        // Assert
        Assert.Contains("RazorWireOptions.Hybrid.LiveOrigin", exception.Message);
        Assert.Contains("absolute http or https origin", exception.Message);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "TestApp";

        public string WebRootPath { get; set; } = string.Empty;

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
