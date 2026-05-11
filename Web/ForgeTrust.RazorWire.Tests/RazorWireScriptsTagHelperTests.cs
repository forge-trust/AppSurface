using FakeItEasy;
using ForgeTrust.RazorWire.TagHelpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

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
                "/_content/ForgeTrust.RazorWire/razorwire/razorwire.js"))
            .Returns("/my-app/_content/ForgeTrust.RazorWire/razorwire/razorwire.js?v=123");

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(
                "/my-app",
                "/_content/ForgeTrust.RazorWire/razorwire/razorwire.islands.js"))
            .Returns("/my-app/_content/ForgeTrust.RazorWire/razorwire/razorwire.islands.js?v=456");

        // Act
        _helper.Process(_context, _output);

        // Assert
        Assert.Null(_output.TagName); // Should remove the wrapper tag

        var content = _output.Content.GetContent();
        Assert.Contains("turbo.es2017-umd.js", content);
        Assert.Contains(
            "src=\"/my-app/_content/ForgeTrust.RazorWire/razorwire/razorwire.js?v=123\"",
            content);
        Assert.Contains(
            "src=\"/my-app/_content/ForgeTrust.RazorWire/razorwire/razorwire.islands.js?v=456\"",
            content);
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
