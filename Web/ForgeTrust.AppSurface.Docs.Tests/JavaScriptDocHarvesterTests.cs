using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class JavaScriptDocHarvesterTests : IDisposable
{
    private readonly string _testRoot = Directory.CreateTempSubdirectory("razordocs-js-harvester-").FullName;

    [Fact]
    public async Task HarvestAsync_ShouldReturnNoDocs_WhenJavaScriptHarvestingIsDisabled()
    {
        var harvester = CreateHarvester(new RazorDocsOptions());

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ShouldWarn_WhenEnabledWithoutIncludeGlobs()
    {
        await WriteAsync("src/public-api.js", "const ignored = true;");
        var harvester = CreateHarvester(CreateEnabledOptions());

        var docs = await harvester.HarvestAsync(_testRoot);
        var diagnostic = Assert.Single(GetDiagnostics(harvester));

        Assert.Empty(docs);
        Assert.Equal(DocHarvestDiagnosticCodes.JavaScriptMalformedPublicDoclet, diagnostic.Code);
        Assert.Contains("include globs", diagnostic.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Include", diagnostic.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarvestAsync_ShouldIgnoreBlankIncludeGlobs()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Public event.
             * @public
             * @event razorwire:ignored
             */
            """);
        var harvester = CreateHarvester(CreateEnabledOptions(""));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ShouldRejectFixturePathsOutsideTestRoot()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => WriteAsync("../escaped.js", "export const escaped = true;"));

        Assert.Contains("Test fixture paths must stay under the test root.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarvestAsync_ShouldHarvestSupportedPublicDocletsIntoGroupPageAndSearchStubs()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Wires a form into RazorWire failure handling.
             * @public
             * @namespace RazorWire
             * @param {HTMLFormElement} form - Form to wire.
             * @returns {void} Nothing is returned.
             */
            function wireForm(form) {}

            /**
             * Creates a reusable failure listener.
             * @public
             * @namespace RazorWire
             * @param {string} mode - Listener mode.
             */
            const createFailureListener = (mode) => mode;

            /**
             * Default form timeout in milliseconds.
             * @public
             * @namespace RazorWire
             */
            const streamTimeoutMs = 30000;

            /**
             * The form submission failed and custom UI may handle the failure.
             * @public
             * @namespace RazorWire
             * @event razorwire:form:failure
             * @target form
             * @firesWhen a RazorWire-enhanced form receives an unhandled failure response or network error.
             * @bubbles true
             * @cancelable true
             * @property {HTMLFormElement} detail.form - Submitted form.
             * @property {number|null} detail.statusCode - HTTP status code when a response was received.
             * @example
             * form.addEventListener('razorwire:form:failure', event => {
             *   event.preventDefault();
             * });
             */

            /**
             * Failure payload passed through event.detail.
             * @public
             * @namespace RazorWire
             * @typedef {Object} FormFailureDetail
             * @property {HTMLFormElement} form - Submitted form.
             */

            /**
             * Browser global used for RazorWire runtime state.
             * @public
             * @namespace RazorWire
             * @global
             */
            window.RazorWire = {};
            """);
        var harvester = CreateHarvester(CreateEnabledOptions("src/public-api.js"));

        var docs = await harvester.HarvestAsync(_testRoot);

        var page = Assert.Single(docs, doc => string.Equals(doc.Path, "api/javascript/razorwire", StringComparison.Ordinal));
        Assert.Equal("RazorWire JavaScript API", page.Title);
        Assert.Equal("javascript-api", page.Metadata?.PageType);
        Assert.Contains("event-razorwire-form-failure", page.Content, StringComparison.Ordinal);
        Assert.Contains("data-razordocs-symbol-source=\"event-razorwire-form-failure\"", page.Content, StringComparison.Ordinal);
        Assert.Contains(page.Outline!, item => item.Id == "event-razorwire-form-failure" && item.Level == 2);
        Assert.Contains(
            page.SymbolSourceProvenance!,
            provenance => provenance.AnchorId == "event-razorwire-form-failure"
                          && provenance.SourcePath == "src/public-api.js"
                          && provenance.StartLine > 0);

        var eventStub = Assert.Single(docs, doc => string.Equals(
            doc.Path,
            "api/javascript/razorwire#event-razorwire-form-failure",
            StringComparison.Ordinal));
        Assert.Equal("javascript-event", eventStub.Metadata?.PageType);
        Assert.Contains("JavaScript Event", eventStub.Content, StringComparison.Ordinal);
        Assert.Contains("detail.statusCode", eventStub.Content, StringComparison.Ordinal);
        Assert.Contains("form.addEventListener", eventStub.Content, StringComparison.Ordinal);
        Assert.Contains(docs, doc => doc.Path.EndsWith("#function-wireform", StringComparison.Ordinal));
        Assert.Contains(docs, doc => doc.Path.EndsWith("#function-createfailurelistener", StringComparison.Ordinal));
        Assert.Contains(docs, doc => doc.Path.EndsWith("#constant-streamtimeoutms", StringComparison.Ordinal));
        Assert.Contains(docs, doc => doc.Path.EndsWith("#typedef-formfailuredetail", StringComparison.Ordinal));
        Assert.Contains(docs, doc => doc.Path.EndsWith("#global-window-razorwire", StringComparison.Ordinal));
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ShouldInferWindowGlobalGroupWhenNamespaceIsOmitted()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Browser global used for RazorWire runtime state.
             * @public
             * @global
             */
            window.RazorWire = {};
            """);
        var harvester = CreateHarvester(CreateEnabledOptions("src/public-api.js"));

        var docs = await harvester.HarvestAsync(_testRoot);

        var page = Assert.Single(docs, doc => string.Equals(doc.Path, "api/javascript/razorwire", StringComparison.Ordinal));
        Assert.Equal("RazorWire JavaScript API", page.Title);
        Assert.Contains("global-window-razorwire", page.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarvestAsync_ShouldHarvestAttachedDocletsInsideRuntimeClosures()
    {
        await WriteAsync(
            "wwwroot/razorwire/razorwire.js",
            """
            (function () {
                /**
                 * Browser global used for RazorWire runtime state.
                 * @public
                 * @namespace RazorWire
                 * @global
                 */
                window.RazorWire = {};
            })();
            """);
        var harvester = CreateHarvester(CreateEnabledOptions("wwwroot/razorwire/razorwire.js"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, doc => doc.Path.EndsWith("#global-window-razorwire", StringComparison.Ordinal));
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ShouldSkipSharedPublicDocletsOnMultipleVariableDeclarators()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Public timeout value.
             * @public
             * @namespace RazorWire
             */
            const publicTimeoutMs = 30000, internalSecret = "do-not-publish";
            """);
        var harvester = CreateHarvester(CreateEnabledOptions("src/public-api.js"));

        var docs = await harvester.HarvestAsync(_testRoot);
        var diagnostics = GetDiagnostics(harvester);

        Assert.Empty(docs);
        var diagnostic = Assert.Single(
            diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptUnsupportedPublicShape);
        Assert.Contains("Multiple JavaScript declarators", diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarvestAsync_ShouldDogfoodRazorWireRuntimeWhenConfiguredWithSingleFileInclude()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var harvester = CreateHarvester(CreateEnabledOptions("Web/ForgeTrust.RazorWire/wwwroot/razorwire/razorwire.js"));

        var docs = await harvester.HarvestAsync(repoRoot);

        var page = Assert.Single(docs, doc => string.Equals(doc.Path, "api/javascript/razorwire", StringComparison.Ordinal));
        Assert.Contains("event-razorwire-form-submit-start", page.Content, StringComparison.Ordinal);
        Assert.Contains("event-razorwire-form-failure", page.Content, StringComparison.Ordinal);
        Assert.Contains("event-razorwire-form-diagnostic", page.Content, StringComparison.Ordinal);
        Assert.Contains("event-razorwire-form-submit-end", page.Content, StringComparison.Ordinal);
        Assert.Contains("global-window-razorwire", page.Content, StringComparison.Ordinal);
        Assert.Contains(
            page.SymbolSourceProvenance!,
            provenance => provenance.SourcePath == "Web/ForgeTrust.RazorWire/wwwroot/razorwire/razorwire.js"
                          && provenance.AnchorId == "event-razorwire-form-failure");
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ShouldRespectIncludesExcludesAndHardExclusionTags()
    {
        await WriteAsync(
            "src/public.js",
            """
            /**
             * Public event.
             * @public
             * @namespace RazorWire
             * @event razorwire:form:failure
             */
            """);
        await WriteAsync(
            "src/private/hidden.js",
            """
            /**
             * Excluded event.
             * @public
             * @namespace RazorWire
             * @event razorwire:private
             */
            """);
        await WriteAsync(
            "src/internal.js",
            """
            /**
             * Internal helper.
             * @public
             * @private
             * @namespace RazorWire
             */
            function hidden() {}
            """);
        var options = CreateEnabledOptions("src/**/*.js");
        options.Harvest.JavaScript.Exclude = ["src/private/**"];
        var harvester = CreateHarvester(options);

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, doc => doc.Path.EndsWith("#event-razorwire-form-failure", StringComparison.Ordinal));
        Assert.DoesNotContain(docs, doc => doc.Path.Contains("private", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(docs, doc => doc.Path.Contains("hidden", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HarvestAsync_ShouldEmitDiagnostics_ForSkippedAndUnsupportedPublicInputs()
    {
        await WriteAsync("src/too-big.js", "const value = '" + new string('x', 2048) + "';");
        await WriteAsync("src/malformed.js", "function broken( {");
        await WriteAsync(
            "src/unsupported.js",
            """
            /**
             * Public class.
             * @public
             * @namespace RazorWire
             */
            class FailureView {}

            /**
             * Missing event name.
             * @public
             * @event
             */

            /**
             * Duplicate event.
             * @public
             * @namespace RazorWire
             * @event razorwire:duplicate
             */

            /**
             * Duplicate event again.
             * @public
             * @namespace RazorWire
             * @event razorwire:duplicate
             */
            """);
        var options = CreateEnabledOptions("src/**/*.js");
        options.Harvest.JavaScript.MaxFileSizeBytes = 1024;
        var harvester = CreateHarvester(options);

        var docs = await harvester.HarvestAsync(_testRoot);
        var diagnostics = GetDiagnostics(harvester);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptFileTooLarge);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptParseFailed);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptUnsupportedPublicShape);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptMalformedPublicDoclet);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptDuplicateAnchor);
        Assert.Contains(docs, doc => doc.Path.EndsWith("#event-razorwire-duplicate", StringComparison.Ordinal));
        Assert.Contains(docs, doc => doc.Path.EndsWith("#event-razorwire-duplicate-2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HarvestAsync_ShouldEmitDiagnostics_ForMalformedStandaloneDocletsAndCommonJsExports()
    {
        await WriteAsync(
            "src/unsupported.js",
            """
            function undocumented() {}
            const dynamicName = "RazorWire";

            /**
             * CommonJS public export.
             * @public
             */
            module.exports = {};

            /**
             * Missing typedef name.
             * @public
             * @typedef
             */

            /**
             * Standalone public note.
             * @public
             */

            /**
             * Event without recommended contract fields.
             * @public
             * @event razorwire:incomplete
             */

            /**
             * Computed globals are not supported in v1.
             * @public
             * @global
             */
            window[dynamicName] = {};
            """);
        var harvester = CreateHarvester(CreateEnabledOptions("src/unsupported.js"));

        var docs = await harvester.HarvestAsync(_testRoot);
        var diagnostics = GetDiagnostics(harvester);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptUnsupportedPublicShape
            && diagnostic.Cause.Contains("CommonJS", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptMalformedPublicDoclet
            && diagnostic.Cause.Contains("typedef name", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptMalformedPublicDoclet
            && diagnostic.Cause.Contains("standalone public JavaScript doclet", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptIncompletePublicDoclet);
        Assert.Contains(docs, doc => doc.Path.EndsWith("#event-razorwire-incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HarvestAsync_ShouldIncludeTaggedDocletsWhenPublicTagIsNotRequired()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Event with a host-approved non-public doclet.
             * @event razorwire:loose
             * @target form
             * @firesWhen the host disables the public-tag requirement.
             * @param
             * @property {string}
             * @example
             * form.addEventListener('razorwire:loose', event => {});
             */
            """);
        var options = CreateEnabledOptions("src/public-api.js");
        options.Harvest.JavaScript.RequirePublicTag = false;
        var harvester = CreateHarvester(options);

        var docs = await harvester.HarvestAsync(_testRoot);

        var eventStub = Assert.Single(docs, doc => doc.Path.EndsWith("#event-razorwire-loose", StringComparison.Ordinal));
        Assert.Contains("Event with a host-approved non-public doclet.", eventStub.Content, StringComparison.Ordinal);
        Assert.Contains(
            GetDiagnostics(harvester),
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptIncompletePublicDoclet
                          && diagnostic.Fix.Contains("@property detail.*", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HarvestAsync_ShouldRenderOptionalEventMetadataAndMatchQuestionMarkGlobs()
    {
        await WriteAsync(
            "src/public1.js",
            """
            /**
             * Runtime failure event.
             * Extra context for custom failure UI.
             * @public
             * @event razorwire:failure
             * @target form
             * @firesWhen a request fails.
             * @detail none
             * @deprecated Use razorwire:error instead.
             * @example
             * form.addEventListener('razorwire:failure', event => event.preventDefault());
             */
            """);
        var harvester = CreateHarvester(CreateEnabledOptions("src/public?.js"));

        var docs = await harvester.HarvestAsync(_testRoot);

        var eventStub = Assert.Single(docs, doc => doc.Path.EndsWith("#event-razorwire-failure", StringComparison.Ordinal));
        Assert.Contains("Extra context for custom failure UI.", eventStub.Content, StringComparison.Ordinal);
        Assert.Contains("Deprecated. Use razorwire:error instead.", eventStub.Content, StringComparison.Ordinal);
        Assert.Contains("<strong>Detail:</strong> none", eventStub.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSearchIndexPayloadAsync_ShouldIndexJavaScriptEventStubsWithKindLabelsAndDetailFields()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * The form submission failed and custom UI may handle the failure.
             * @public
             * @namespace RazorWire
             * @event razorwire:form:failure
             * @target form
             * @firesWhen a RazorWire-enhanced form receives an unhandled failure response.
             * @property {number|null} detail.statusCode - HTTP status code when a response was received.
             * @example
             * form.addEventListener('razorwire:form:failure', event => event.preventDefault());
             */
            """);
        var options = CreateEnabledOptions("src/public-api.js");
        options.Source.RepositoryRoot = _testRoot;
        options.Contributor.Enabled = false;
        var harvester = CreateHarvester(options);
        var aggregator = new DocAggregator(
            [harvester],
            options,
            new TestWebHostEnvironment(_testRoot),
            new Memo(new MemoryCache(new MemoryCacheOptions())),
            new RazorDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);

        var payload = await aggregator.GetSearchIndexPayloadAsync();

        var document = Assert.Single(
            payload.Documents,
            doc => string.Equals(doc.Title, "razorwire:form:failure", StringComparison.Ordinal));
        Assert.Equal("/docs/api/javascript/razorwire#event-razorwire-form-failure", document.Path);
        Assert.Equal("javascript-event", document.PageType);
        Assert.Equal("JavaScript Event", document.PageTypeLabel);
        Assert.Contains("detail.statusCode", document.BodyText, StringComparison.Ordinal);
        Assert.Contains("razorwire:form:failure", document.BodyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldIncludeJavaScriptHarvesterDiagnostics()
    {
        await WriteAsync("src/too-big.js", "const value = '" + new string('x', 2048) + "';");
        var options = CreateEnabledOptions("src/too-big.js");
        options.Source.RepositoryRoot = _testRoot;
        options.Harvest.JavaScript.MaxFileSizeBytes = 1024;
        var harvester = CreateHarvester(options);
        var aggregator = new DocAggregator(
            [harvester],
            options,
            new TestWebHostEnvironment(_testRoot),
            new Memo(new MemoryCache(new MemoryCacheOptions())),
            new RazorDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);

        var health = await aggregator.GetHarvestHealthAsync();

        var diagnostic = Assert.Single(
            health.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptFileTooLarge);
        Assert.Equal("JavaScriptDocHarvester", diagnostic.HarvesterType);
    }

    public void Dispose()
    {
        Directory.Delete(_testRoot, recursive: true);
    }

    private static JavaScriptDocHarvester CreateHarvester(RazorDocsOptions options)
    {
        return new JavaScriptDocHarvester(options, NullLogger<JavaScriptDocHarvester>.Instance);
    }

    private static RazorDocsOptions CreateEnabledOptions(params string[] include)
    {
        return new RazorDocsOptions
        {
            Harvest = new RazorDocsHarvestOptions
            {
                JavaScript = new RazorDocsJavaScriptHarvestOptions
                {
                    Enabled = true,
                    Include = include,
                    Exclude = [.. RazorDocsJavaScriptHarvestOptions.DefaultExclude]
                }
            }
        };
    }

    private static IReadOnlyList<DocHarvestDiagnostic> GetDiagnostics(JavaScriptDocHarvester harvester)
    {
        return ((IDocHarvesterDiagnosticProvider)harvester).GetHarvestDiagnostics();
    }

    private async Task WriteAsync(string relativePath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelativePath))
        {
            throw new ArgumentException("Test fixture paths must be relative.", nameof(relativePath));
        }

        var safeRelativePath = normalizedRelativePath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(_testRoot);
        var fullPath = Path.GetFullPath(Path.Join(fullRoot, safeRelativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.Equals(fullRoot, comparison)
            && !fullPath.StartsWith(rootPrefix, comparison))
        {
            throw new ArgumentException("Test fixture paths must stay under the test root.", nameof(relativePath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new PhysicalFileProvider(contentRootPath);
            WebRootPath = contentRootPath;
            WebRootFileProvider = new PhysicalFileProvider(contentRootPath);
        }

        public string ApplicationName { get; set; } = "JavaScriptDocHarvesterTests";

        public IFileProvider ContentRootFileProvider { get; set; }

        public string ContentRootPath { get; set; }

        public string EnvironmentName { get; set; } = "Development";

        public string WebRootPath { get; set; }

        public IFileProvider WebRootFileProvider { get; set; }
    }
}
