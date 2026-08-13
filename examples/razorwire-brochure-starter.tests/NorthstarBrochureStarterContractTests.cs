using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NorthstarBrochureStarter.Tests;

public sealed class NorthstarBrochureStarterContractTests
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public NorthstarBrochureStarterContractTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("/", "Make the signal")]
    [InlineData("/services", "Clarity is a creative advantage")]
    [InlineData("/journal", "Notes from the edge of the map")]
    [InlineData("/journal/field-guide", "How to find your way through the fog")]
    [InlineData("/contact", "Tell us what you are seeing")]
    [InlineData("/thank-you", "The handoff is ready")]
    [InlineData("/thank-you.html", "The handoff is ready")]
    public async Task RequiredRoutes_RenderExpectedContent(string path, string expectedText)
    {
        using var response = await _client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(expectedText, html, StringComparison.Ordinal);
        Assert.Contains("<main id=\"main-content\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HtmlConfirmationAlias_MatchesCanonicalConfirmation()
    {
        var canonical = await _client.GetStringAsync("/thank-you");
        var alias = await _client.GetStringAsync("/thank-you.html");

        Assert.Equal(canonical, alias);
    }

    [Fact]
    public async Task Home_RendersRazorWirePageNavigationAndRuntimeScripts()
    {
        var html = await _client.GetStringAsync("/");

        Assert.Contains("rw-page-nav", html, StringComparison.Ordinal);
        Assert.Contains("rw-page-nav-link", html, StringComparison.Ordinal);
        Assert.Contains("/_content/ForgeTrust.RazorWire/razorwire/razorwire.js", html, StringComparison.Ordinal);
        Assert.Contains("/_content/ForgeTrust.RazorWire/razorwire/razorwire.islands.js", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scripts_AppearOnlyOnPagesWithSamePageNavigation()
    {
        var home = await _client.GetStringAsync("/");
        var fieldGuide = await _client.GetStringAsync("/journal/field-guide");
        var contact = await _client.GetStringAsync("/contact");

        Assert.Contains("page-navigation.js", home, StringComparison.Ordinal);
        Assert.Contains("page-navigation.js", fieldGuide, StringComparison.Ordinal);
        Assert.DoesNotContain("/_content/ForgeTrust.RazorWire/razorwire/razorwire.js", contact, StringComparison.Ordinal);
        Assert.DoesNotContain("page-navigation.js", contact, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContactForm_IsVisibleRequiredAndDemoOnly()
    {
        var html = await _client.GetStringAsync("/contact");
        var form = ExtractElement(html, "form");

        Assert.Contains("data-contact-mode=\"demo\"", form, StringComparison.Ordinal);
        Assert.Contains("method=\"get\"", form, StringComparison.Ordinal);
        Assert.Contains("action=\"/thank-you.html?demo=1\"", form, StringComparison.Ordinal);
        Assert.Contains("Preview the no-delivery confirmation", html, StringComparison.Ordinal);
        Assert.Contains("No message was sent.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("method=\"post\"", form, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-rw-form", form, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-rw-antiforgery", form, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", form, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("endpoint", form, StringComparison.OrdinalIgnoreCase);

        var controls = Regex.Matches(form, "<(?:input|textarea)\\b[^>]*>", RegexOptions.IgnoreCase);
        Assert.True(controls.Count == 3, $"Expected three required form controls, found {controls.Count}.");
        foreach (Match control in controls)
        {
            Assert.Contains("required", control.Value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" name=", control.Value, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("<label for=\"contact-name\">", form, StringComparison.Ordinal);
        Assert.Contains("<label for=\"contact-email\">", form, StringComparison.Ordinal);
        Assert.Contains("<label for=\"contact-context\">", form, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContactAndConfirmation_ExplainThatNothingWasSent()
    {
        var contact = await _client.GetStringAsync("/contact");
        var confirmation = await _client.GetStringAsync("/thank-you");

        Assert.Contains("No message was sent.", contact, StringComparison.Ordinal);
        Assert.Contains("No message was sent.", confirmation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Journal_ProvidesStaticEntriesWithWorkingInternalLinks()
    {
        var journal = await _client.GetStringAsync("/journal");

        Assert.Equal(3, Regex.Matches(journal, "class=\"journal-card", RegexOptions.IgnoreCase).Count);
        Assert.Contains("How to find your way through the fog", journal, StringComparison.Ordinal);

        foreach (var path in GetInternalPaths(journal))
        {
            using var response = await _client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task SharedShell_ProvidesAccessibleLandmarksAndSkipLink()
    {
        var html = await _client.GetStringAsync("/");

        Assert.Contains("<a class=\"skip-link\" href=\"#main-content\">Skip to main content</a>", html, StringComparison.Ordinal);
        Assert.Contains("<header class=\"site-header\">", html, StringComparison.Ordinal);
        Assert.Contains("<nav class=\"site-nav\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Primary navigation\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-rw-page-nav", ExtractElement(html, "header"), StringComparison.Ordinal);
        Assert.Contains("<main id=\"main-content\">", html, StringComparison.Ordinal);
        Assert.Contains("<footer class=\"site-footer\">", html, StringComparison.Ordinal);
        Assert.Contains("lang=\"en\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/css/site.css", "text/css")]
    [InlineData("/images/northstar-field-guide.svg", "image/svg+xml")]
    public async Task EditorialAssets_AreServed(string path, string mediaType)
    {
        using var response = await _client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(body);
    }

    [Fact]
    public void ApplicationProject_IsPackageOnlyAndUsesTheDefaultRazorWireVersion()
    {
        var project = File.ReadAllText(FindRepositoryFile(
            "examples",
            "razorwire-brochure-starter",
            "NorthstarBrochureStarter.csproj"));

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", project, StringComparison.Ordinal);
        Assert.Contains("<RazorWirePackageVersion Condition=\"'$(RazorWirePackageVersion)' == ''\">0.1.0-preview.1</RazorWirePackageVersion>", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"ForgeTrust.RazorWire\" Version=\"$(RazorWirePackageVersion)\" />", project, StringComparison.Ordinal);
        Assert.DoesNotContain("<ProjectReference", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Tailwind", project, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Regex.Matches(project, "<PackageReference\\b", RegexOptions.IgnoreCase));

        var module = File.ReadAllText(FindRepositoryFile(
            "examples",
            "razorwire-brochure-starter",
            "NorthstarModule.cs"));

        Assert.Contains("builder.AddModule<RazorWireWebModule>();", module, StringComparison.Ordinal);
    }

    [Fact]
    public void StarterReadme_ExplainsItsBoundaryAndExportWorkflow()
    {
        var readme = File.ReadAllText(FindRepositoryFile(
            "examples",
            "razorwire-brochure-starter",
            "README.md"));

        Assert.Contains("## When to use this starter", readme, StringComparison.Ordinal);
        Assert.Contains("Plain ASP.NET Core MVC is enough", readme, StringComparison.Ordinal);
        Assert.Contains("razorwire export", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--url http://localhost:5233", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("razorwire export \\\n  --project", readme, StringComparison.Ordinal);
        Assert.Contains("request validation, spam protection, email delivery", readme, StringComparison.Ordinal);
        Assert.Contains("selects no provider", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Stylesheet_DefinesTheStarterComponentVocabulary()
    {
        var stylesheet = File.ReadAllText(FindRepositoryFile(
            "examples",
            "razorwire-brochure-starter",
            "wwwroot",
            "css",
            "site.css"));

        var requiredSelectors = new[]
        {
            ".shell", ".hero", ".section-band", ".split-layout", ".content-column",
            ".page-intro", ".journal-grid", ".journal-card", ".service-row", ".contact-page",
            ".contact-form", ".field-guide", ".article-layout", ".confirmation", ".button-row", ".text-link",
        };

        foreach (var selector in requiredSelectors)
        {
            Assert.Contains(selector, stylesheet, StringComparison.Ordinal);
        }
    }

    private static string ExtractElement(string html, string elementName)
    {
        var match = Regex.Match(
            html,
            $@"<{elementName}\b.*?</{elementName}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        Assert.True(match.Success, $"Expected a {elementName} element in the rendered document.");
        return match.Value;
    }

    private static IEnumerable<string> GetInternalPaths(string html)
    {
        foreach (Match match in Regex.Matches(html, "href=\"(?<path>/[^\"#]*)\"", RegexOptions.IgnoreCase))
        {
            yield return match.Groups["path"].Value;
        }
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)} from {AppContext.BaseDirectory}.");
    }
}
