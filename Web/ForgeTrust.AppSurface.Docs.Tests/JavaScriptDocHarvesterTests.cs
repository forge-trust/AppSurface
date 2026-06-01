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
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Public event.
             * @public
             * @event razorwire:ignored
             */
            """);
        var options = new AppSurfaceDocsOptions();
        options.Harvest.JavaScript.Enabled = false;
        var harvester = CreateHarvester(options);

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ShouldHarvestAnnotatedJavaScriptByDefault()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Public event.
             * @public
             * @namespace RazorWire
             * @event razorwire:default
             * @target document
             * @firesWhen default discovery sees an annotation.
             * @detail none
             */
            """);
        var harvester = CreateHarvester(new AppSurfaceDocsOptions());

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, doc => doc.Path.EndsWith("#event-razorwire-default", StringComparison.Ordinal));
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ShouldIgnoreUnannotatedJavaScriptByDefault()
    {
        await WriteAsync("src/public-api.js", "function undocumented() {}");
        var harvester = CreateHarvester(new AppSurfaceDocsOptions());

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ShouldTreatBlankIncludeGlobsAsDefaultDiscovery()
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
        var harvester = CreateHarvester(new AppSurfaceDocsOptions());

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, doc => doc.Path.EndsWith("#event-razorwire-ignored", StringComparison.Ordinal));
        Assert.Contains(
            GetDiagnostics(harvester),
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptIncompletePublicDoclet);
    }

    [Fact]
    public async Task HarvestAsync_ShouldEmitStrictEventDiagnostic_WhenRequiredPublicEventFieldsAreMissing()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Public event.
             * @public
             * @event razorwire:missing
             */
            """);
        var options = CreateEnabledOptions("src/public-api.js");
        options.Harvest.JavaScript.RequireCompleteEventDoclets = true;
        var harvester = CreateHarvester(options);

        _ = await harvester.HarvestAsync(_testRoot);

        var diagnostic = Assert.Single(
            GetDiagnostics(harvester),
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptIncompletePublicEventDoclet);
        Assert.Equal(DocHarvestDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("@target", diagnostic.Fix, StringComparison.Ordinal);
        Assert.Contains("@firesWhen", diagnostic.Fix, StringComparison.Ordinal);
        Assert.Contains("@property detail.* or @detail none", diagnostic.Fix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("detail.message")]
    [InlineData("[detail.message]")]
    [InlineData("[detail.message=\"fallback\"]")]
    [InlineData("detail.items[]")]
    [InlineData("detail.items[].id")]
    [InlineData("detail.$payload-id")]
    [InlineData("detail.message_2")]
    public async Task HarvestAsync_ShouldAcceptStrictEventDetailPropertyNames(string detailPropertyName)
    {
        await WriteAsync(
            "src/public-api.js",
            $$"""
            /**
             * Public event.
             * @public
             * @event razorwire:valid-detail
             * @target document
             * @firesWhen a valid detail shape is documented.
             * @property {string} {{detailPropertyName}} - Detail field.
             */
            """);
        var options = CreateEnabledOptions("src/public-api.js");
        options.Harvest.JavaScript.RequireCompleteEventDoclets = true;
        var harvester = CreateHarvester(options);

        _ = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(GetDiagnostics(harvester));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("detail.[]")]
    [InlineData("detail.message!")]
    [InlineData("detail.0:")]
    public void IsValidEventDetailPropertyName_ShouldRejectMalformedNames(string detailPropertyName)
    {
        Assert.False(JavaScriptDocHarvester.IsValidEventDetailPropertyName(detailPropertyName));
    }

    [Theory]
    [InlineData("detail")]
    [InlineData("[detail]")]
    [InlineData("detail.")]
    [InlineData("detail..message")]
    [InlineData("detail. message")]
    [InlineData("detail.[x]")]
    [InlineData("Detail.message")]
    [InlineData("form")]
    [InlineData("message")]
    public async Task HarvestAsync_ShouldRejectStrictEventDetailPropertyNames(string detailPropertyName)
    {
        await WriteAsync(
            "src/public-api.js",
            $$"""
            /**
             * Public event.
             * @public
             * @event razorwire:invalid-detail
             * @target document
             * @firesWhen an invalid detail shape is documented.
             * @property {string} {{detailPropertyName}} - Detail field.
             */
            """);
        var options = CreateEnabledOptions("src/public-api.js");
        options.Harvest.JavaScript.RequireCompleteEventDoclets = true;
        var harvester = CreateHarvester(options);

        _ = await harvester.HarvestAsync(_testRoot);

        var diagnostic = Assert.Single(
            GetDiagnostics(harvester),
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptIncompletePublicEventDoclet);
        Assert.Contains("has invalid or contradictory public contract fields", diagnostic.Problem, StringComparison.Ordinal);
        Assert.Contains("Fix @property names to use valid detail.* paths", diagnostic.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain("Add @property detail.*", diagnostic.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarvestAsync_ShouldRejectStrictEventDetailNoneConflict()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Public event.
             * @public
             * @event razorwire:conflict
             * @target document
             * @firesWhen a contradictory detail shape is documented.
             * @detail none
             * @property {string} detail.message - Detail field.
             */
            """);
        var options = CreateEnabledOptions("src/public-api.js");
        options.Harvest.JavaScript.RequireCompleteEventDoclets = true;
        var harvester = CreateHarvester(options);

        _ = await harvester.HarvestAsync(_testRoot);

        var diagnostic = Assert.Single(
            GetDiagnostics(harvester),
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptIncompletePublicEventDoclet);
        Assert.Contains("has invalid or contradictory public contract fields", diagnostic.Problem, StringComparison.Ordinal);
        Assert.Contains("Remove @detail none or remove the event detail @property tags", diagnostic.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain("Add remove", diagnostic.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarvestAsync_ShouldAvoidContradictoryFix_WhenDetailNoneHasInvalidProperty()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Public event.
             * @public
             * @event razorwire:conflict-invalid
             * @target document
             * @firesWhen a contradictory and invalid detail shape is documented.
             * @detail none
             * @property {string} message - Invalid detail field.
             */
            """);
        var options = CreateEnabledOptions("src/public-api.js");
        options.Harvest.JavaScript.RequireCompleteEventDoclets = true;
        var harvester = CreateHarvester(options);

        _ = await harvester.HarvestAsync(_testRoot);

        var diagnostic = Assert.Single(
            GetDiagnostics(harvester),
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptIncompletePublicEventDoclet);
        Assert.Contains("Fix @property names to use valid detail.* paths", diagnostic.Fix, StringComparison.Ordinal);
        Assert.Contains("Remove @detail none or remove the event detail @property tags", diagnostic.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain("Add @property detail.*", diagnostic.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarvestAsync_ShouldDescribeMissingAndInvalidStrictEventFields()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Public event.
             * @public
             * @event razorwire:missing-invalid
             * @property {string} message - Invalid detail field.
             */
            """);
        var options = CreateEnabledOptions("src/public-api.js");
        options.Harvest.JavaScript.RequireCompleteEventDoclets = true;
        var harvester = CreateHarvester(options);

        _ = await harvester.HarvestAsync(_testRoot);

        var diagnostic = Assert.Single(
            GetDiagnostics(harvester),
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptIncompletePublicEventDoclet);
        Assert.Contains("is missing or has invalid public contract fields", diagnostic.Problem, StringComparison.Ordinal);
        Assert.Contains("Add @target, @firesWhen", diagnostic.Fix, StringComparison.Ordinal);
        Assert.Contains("Fix @property names to use valid detail.* paths", diagnostic.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain("Add @property detail.*", diagnostic.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarvestAsync_ShouldNotEmitStrictEventDiagnostic_ForNonPublicEventIncludedByGlob()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Included event without an explicit public contract signal.
             * @event razorwire:internal-include
             */
            """);
        var options = CreateEnabledOptions("src/public-api.js");
        options.Harvest.JavaScript.RequirePublicTag = false;
        options.Harvest.JavaScript.RequireCompleteEventDoclets = true;
        var harvester = CreateHarvester(options);

        _ = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(
            GetDiagnostics(harvester),
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptIncompletePublicDoclet);
        Assert.DoesNotContain(
            GetDiagnostics(harvester),
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptIncompletePublicEventDoclet);
        Assert.DoesNotContain(
            GetDiagnostics(harvester),
            diagnostic => diagnostic.Severity == DocHarvestDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task HarvestAsync_ShouldKeepNonEventCompletenessDiagnosticsAsWarnings_WhenStrictEventDocletsAreEnabled()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Public attribute.
             * @public
             * @attribute data-rw-mode
             */
            """);
        var options = CreateEnabledOptions("src/public-api.js");
        options.Harvest.JavaScript.RequireCompleteEventDoclets = true;
        var harvester = CreateHarvester(options);

        _ = await harvester.HarvestAsync(_testRoot);

        var diagnostic = Assert.Single(GetDiagnostics(harvester));
        Assert.Equal(DocHarvestDiagnosticCodes.JavaScriptIncompletePublicDoclet, diagnostic.Code);
        Assert.Equal(DocHarvestDiagnosticSeverity.Warning, diagnostic.Severity);
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
        Assert.Equal("javascript", page.Metadata?.CodeLanguage);
        Assert.Contains("event-razorwire-form-failure", page.Content, StringComparison.Ordinal);
        Assert.Contains("data-appsurfacedocs-symbol-source=\"event-razorwire-form-failure\"", page.Content, StringComparison.Ordinal);
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
        Assert.Equal("javascript", eventStub.Metadata?.CodeLanguage);
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
    public async Task HarvestAsync_ShouldHarvestBrowserContractDoclets()
    {
        await WriteAsync(
            "src/browser-contracts.js",
            """
            /**
             * Selects manual form-failure rendering.
             * @public
             * @namespace RazorWire
             * @attribute data-rw-form-failure
             * @target form[data-rw-form="true"]
             * @type {"auto"|"manual"|"off"}
             * @default auto
             */

            /**
             * Default reader-facing failure message.
             * @public
             * @namespace RazorWire
             * @config defaultFailureMessage
             * @source window.RazorWire.config.defaultFailureMessage
             * @type {string}
             */

            /**
             * Island modules may export mount to hydrate a server-rendered root.
             * @public
             * @namespace RazorWire
             * @moduleContract mount
             * @target module referenced by data-rw-module
             * @signature mount(root, props)
             * @param {HTMLElement} root - Island root element.
             * @param {Record<string, unknown>} props - Parsed island props.
             */

            /**
             * Controls generated form failure text color.
             * @public
             * @namespace RazorWire
             * @cssCustomProperty --rw-form-error-text
             * @target [data-rw-form-error-generated="true"]
             * @syntax <color>
             * @default #3f3f46
             */

            /**
             * Stable generated error block selector.
             * @public
             * @namespace RazorWire
             * @cssHook [data-rw-form-error-generated="true"]
             * @hookKind data-attribute
             * @target generated form failure UI
             * @stability stable
             */
            """);
        var harvester = CreateHarvester(CreateEnabledOptions("src/browser-contracts.js"));

        var docs = await harvester.HarvestAsync(_testRoot);

        var page = Assert.Single(docs, doc => string.Equals(doc.Path, "api/javascript/razorwire", StringComparison.Ordinal));
        Assert.Contains("attribute-data-rw-form-failure", page.Content, StringComparison.Ordinal);
        Assert.Contains("config-defaultfailuremessage", page.Content, StringComparison.Ordinal);
        Assert.Contains("module-contract-mount", page.Content, StringComparison.Ordinal);
        Assert.Contains("css-custom-property-rw-form-error-text", page.Content, StringComparison.Ordinal);
        Assert.Contains("css-hook-data-rw-form-error-generated-true", page.Content, StringComparison.Ordinal);
        Assert.Contains(page.Outline!, item => item.Id == "css-hook-data-rw-form-error-generated-true");
        Assert.Contains(docs, doc => doc.Metadata?.PageType == "javascript-attribute" && doc.Content.Contains("JavaScript Attribute", StringComparison.Ordinal));
        Assert.Contains(docs, doc => doc.Metadata?.PageType == "javascript-config" && doc.Content.Contains("JavaScript Config Field", StringComparison.Ordinal));
        Assert.Contains(docs, doc => doc.Metadata?.PageType == "javascript-module-contract" && doc.Content.Contains("JavaScript Module Contract", StringComparison.Ordinal));
        Assert.Contains(docs, doc => doc.Metadata?.PageType == "javascript-css-custom-property" && doc.Content.Contains("JavaScript CSS Custom Property", StringComparison.Ordinal));
        Assert.Contains(docs, doc => doc.Metadata?.PageType == "javascript-css-hook" && doc.Content.Contains("JavaScript CSS Hook", StringComparison.Ordinal));
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Theory]
    [InlineData("@attribute", "A public JavaScript attribute doclet is missing an attribute name.")]
    [InlineData("@config", "A public JavaScript config doclet is missing a config field name.")]
    [InlineData("@moduleContract", "A public JavaScript module contract doclet is missing a contract name.")]
    [InlineData("@cssCustomProperty", "A public JavaScript CSS custom property doclet is missing a custom property name.")]
    [InlineData("@cssHook", "A public JavaScript CSS hook doclet is missing a selector.")]
    public async Task HarvestAsync_ShouldDiagnoseMalformedBrowserContractDoclets(string tag, string expectedCause)
    {
        await WriteAsync(
            "src/browser-contracts.js",
            $$"""
            /**
             * Malformed browser contract.
             * @public
             * @namespace RazorWire
             * {{tag}}
             */
            """);
        var harvester = CreateHarvester(CreateEnabledOptions("src/browser-contracts.js"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        Assert.Contains(
            GetDiagnostics(harvester),
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptMalformedPublicDoclet
                          && diagnostic.Cause == expectedCause);
    }

    [Fact]
    public async Task HarvestAsync_ShouldValidateCssHookKinds()
    {
        await WriteAsync(
            "src/browser-contracts.js",
            """
            /**
             * Stable part selector.
             * @public
             * @namespace RazorWire
             * @cssHook ::part(error)
             * @hookKind part
             * @target generated form failure UI
             * @stability stable
             */

            /**
             * Stable state selector.
             * @public
             * @namespace RazorWire
             * @cssHook :state(invalid)
             * @hookKind state
             * @target generated form failure UI
             * @stability stable
             */

            /**
             * Missing hook kind.
             * @public
             * @namespace RazorWire
             * @cssHook .missing-kind
             * @target generated form failure UI
             * @stability stable
             */

            /**
             * Unknown hook kind.
             * @public
             * @namespace RazorWire
             * @cssHook .unknown-kind
             * @hookKind unknown
             * @target generated form failure UI
             * @stability stable
             */
            """);
        var harvester = CreateHarvester(CreateEnabledOptions("src/browser-contracts.js"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, doc => doc.Path.EndsWith("#css-hook-part-error", StringComparison.Ordinal));
        Assert.Contains(docs, doc => doc.Path.EndsWith("#css-hook-state-invalid", StringComparison.Ordinal));
        Assert.Equal(
            2,
            GetDiagnostics(harvester).Count(
                diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptMalformedPublicDoclet
                              && diagnostic.Cause == "A public JavaScript CSS hook doclet must use a supported @hookKind and a narrow selector value."));
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
    public async Task HarvestAsync_ShouldUseConfiguredGroupRules_WhenDocletHasNoExplicitGroup()
    {
        await WriteAsync(
            "src/browser/public-api.js",
            """
            /**
             * Browser lifecycle event.
             * @public
             * @event browser:ready
             * @target document
             * @firesWhen the browser contracts are ready.
             * @detail none
             */
            """);
        var options = CreateEnabledOptions("src/**/*.js");
        options.Harvest.JavaScript.GroupNameRules =
        [
            new AppSurfaceDocsJavaScriptGroupNameRule
            {
                Name = "Browser Contracts",
                IncludeGlobs = ["src/browser/**/*.js"]
            },
            new AppSurfaceDocsJavaScriptGroupNameRule
            {
                Name = "Fallback Contracts",
                IncludeGlobs = ["src/**/*.js"]
            }
        ];
        var harvester = CreateHarvester(options);

        var docs = await harvester.HarvestAsync(_testRoot);

        var page = Assert.Single(docs, doc => string.Equals(doc.Path, "api/javascript/browser-contracts", StringComparison.Ordinal));
        Assert.Equal("Browser Contracts JavaScript API", page.Title);
        Assert.Equal(["API Reference", "JavaScript", "Browser Contracts"], page.Metadata?.Breadcrumbs);
        var eventStub = Assert.Single(docs, doc => string.Equals(
            doc.Path,
            "api/javascript/browser-contracts#event-browser-ready",
            StringComparison.Ordinal));
        Assert.Equal("Browser Contracts", eventStub.Metadata?.Component);
    }

    [Fact]
    public async Task HarvestAsync_ShouldSkipInvalidConfiguredGroupRules_AndUseFallbackWhenNoRuleMatches()
    {
        await WriteAsync(
            "src/browser/public-api.js",
            """
            /**
             * Browser lifecycle event.
             * @public
             * @event browser:ready
             * @target document
             * @firesWhen the browser contracts are ready.
             * @detail none
             */
            """);
        var options = CreateEnabledOptions("src/**/*.js");
        options.Harvest.JavaScript.GroupNameRules =
        [
            null!,
            new AppSurfaceDocsJavaScriptGroupNameRule
            {
                Name = " ",
                IncludeGlobs = ["src/browser/**/*.js"]
            },
            new AppSurfaceDocsJavaScriptGroupNameRule
            {
                Name = "Null Glob Rule",
                IncludeGlobs = null!
            },
            new AppSurfaceDocsJavaScriptGroupNameRule
            {
                Name = "Other Contracts",
                IncludeGlobs = ["src/other/**/*.js"]
            }
        ];
        var harvester = CreateHarvester(options);

        var docs = await harvester.HarvestAsync(_testRoot);

        var page = Assert.Single(docs, doc => string.Equals(doc.Metadata?.PageType, "javascript-api", StringComparison.Ordinal));
        Assert.Equal("api/javascript/browser-public-api", page.Path);
        Assert.Equal("public-api JavaScript API", page.Title);
        Assert.Equal(["API Reference", "JavaScript", "public-api"], page.Metadata?.Breadcrumbs);
        Assert.DoesNotContain(docs, doc => string.Equals(doc.Path, "api/javascript/other-contracts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HarvestAsync_ShouldUseFallbackWhenConfiguredGroupRulesAreNull()
    {
        await WriteAsync(
            "src/browser/public-api.js",
            """
            /**
             * Browser lifecycle event.
             * @public
             * @event browser:ready
             * @target document
             * @firesWhen the browser contracts are ready.
             * @detail none
             */
            """);
        var options = CreateEnabledOptions("src/**/*.js");
        options.Harvest.JavaScript.GroupNameRules = null!;
        var harvester = CreateHarvester(options);

        var docs = await harvester.HarvestAsync(_testRoot);

        var page = Assert.Single(docs, doc => string.Equals(doc.Metadata?.PageType, "javascript-api", StringComparison.Ordinal));
        Assert.Equal("api/javascript/browser-public-api", page.Path);
        Assert.Equal(["API Reference", "JavaScript", "public-api"], page.Metadata?.Breadcrumbs);
    }

    [Fact]
    public async Task HarvestAsync_ShouldPreferExplicitTagsOverConfiguredGroupRules_AndUseFirstNonblankTag()
    {
        await WriteAsync(
            "src/browser/razorwire.js",
            """
            /**
             * RazorWire lifecycle event.
             * @public
             * @namespace RazorWire
             * @event razorwire:ready
             * @target document
             * @firesWhen RazorWire starts.
             * @detail none
             */
            """);
        await WriteAsync(
            "src/browser/module.js",
            """
            /**
             * Module lifecycle event.
             * @public
             * @namespace
             * @module Module Contracts
             * @event module:ready
             * @target document
             * @firesWhen the module starts.
             * @detail none
             */
            """);
        var options = CreateEnabledOptions("src/browser/**/*.js");
        options.Harvest.JavaScript.GroupNameRules =
        [
            new AppSurfaceDocsJavaScriptGroupNameRule
            {
                Name = "Configured Browser",
                IncludeGlobs = ["src/browser/**/*.js"]
            }
        ];
        var harvester = CreateHarvester(options);

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, doc => string.Equals(doc.Path, "api/javascript/razorwire", StringComparison.Ordinal));
        Assert.Contains(docs, doc => string.Equals(doc.Path, "api/javascript/module-contracts", StringComparison.Ordinal));
        Assert.DoesNotContain(docs, doc => string.Equals(doc.Path, "api/javascript/configured-browser", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HarvestAsync_ShouldUsePathAwareFallbackRoute_WhenFileStemIsCurrentlyUnique()
    {
        await WriteAsync(
            "src/widgets/public-api.js",
            """
            /**
             * Widget lifecycle event.
             * @public
             * @event widget:ready
             * @target document
             * @firesWhen widgets are ready.
             * @detail none
             */
            """);
        var harvester = CreateHarvester(CreateEnabledOptions("src/**/*.js"));

        var docs = await harvester.HarvestAsync(_testRoot);

        var page = Assert.Single(docs, doc => string.Equals(doc.Metadata?.PageType, "javascript-api", StringComparison.Ordinal));
        Assert.Equal("api/javascript/widgets-public-api", page.Path);
        Assert.Equal("public-api JavaScript API", page.Title);
        Assert.Equal(["API Reference", "JavaScript", "public-api"], page.Metadata?.Breadcrumbs);
    }

    [Fact]
    public async Task HarvestAsync_ShouldUseGenericFallbackDisplay_WhenFallbackPathHasNoSegments()
    {
        await WriteAsync(
            ".js",
            """
            /**
             * Root fallback lifecycle event.
             * @public
             * @event root:ready
             * @target document
             * @firesWhen the root fallback contract is ready.
             * @detail none
             */
            """);
        var harvester = CreateHarvester(CreateEnabledOptions("*.js"));

        var docs = await harvester.HarvestAsync(_testRoot);

        var page = Assert.Single(docs, doc => string.Equals(doc.Metadata?.PageType, "javascript-api", StringComparison.Ordinal));
        Assert.Equal("api/javascript/javascript", page.Path);
        Assert.Equal("JavaScript JavaScript API", page.Title);
        Assert.Equal(["API Reference", "JavaScript", "JavaScript"], page.Metadata?.Breadcrumbs);
    }

    [Fact]
    public async Task HarvestAsync_ShouldKeepPathFallbackGroupsDistinct_WhenFileStemsMatch()
    {
        await WriteAsync(
            "src/widgets/public-api.js",
            """
            /**
             * Widget lifecycle event.
             * @public
             * @event widget:ready
             * @target document
             * @firesWhen widgets are ready.
             * @detail none
             */
            """);
        await WriteAsync(
            "src/forms/public-api.js",
            """
            /**
             * Form lifecycle event.
             * @public
             * @event form:ready
             * @target document
             * @firesWhen forms are ready.
             * @detail none
             */
            """);
        await WriteAsync(
            "src/admin/admin-api.js",
            """
            /**
             * Admin lifecycle event.
             * @public
             * @event admin:ready
             * @target document
             * @firesWhen admin contracts are ready.
             * @detail none
             */
            """);
        var harvester = CreateHarvester(CreateEnabledOptions("src/**/*.js"));

        var docs = await harvester.HarvestAsync(_testRoot);

        var groupPages = docs
            .Where(doc => string.Equals(doc.Metadata?.PageType, "javascript-api", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, groupPages.Length);
        Assert.Contains(groupPages, doc => string.Equals(doc.Path, "api/javascript/admin-admin-api", StringComparison.Ordinal));
        Assert.Contains(groupPages, doc => string.Equals(doc.Path, "api/javascript/forms-public-api", StringComparison.Ordinal));
        Assert.Contains(groupPages, doc => string.Equals(doc.Path, "api/javascript/widgets-public-api", StringComparison.Ordinal));
        Assert.Contains(groupPages, doc => doc.Metadata?.Breadcrumbs?.SequenceEqual(["API Reference", "JavaScript", "admin-api"]) == true);
        Assert.Contains(groupPages, doc => doc.Metadata?.Breadcrumbs?.SequenceEqual(["API Reference", "JavaScript", "forms/public-api"]) == true);
        Assert.Contains(groupPages, doc => doc.Metadata?.Breadcrumbs?.SequenceEqual(["API Reference", "JavaScript", "widgets/public-api"]) == true);
    }

    [Fact]
    public async Task HarvestAsync_ShouldKeepDistinctGroupPages_WhenNamespaceSlugsCollide()
    {
        await WriteAsync(
            "src/spaced.js",
            """
            /**
             * Public function in a spaced namespace.
             * @public
             * @namespace Foo Bar
             */
            function fromSpacedNamespace() {}
            """);
        await WriteAsync(
            "src/hyphenated.js",
            """
            /**
             * Public function in a hyphenated namespace.
             * @public
             * @namespace Foo-Bar
             */
            function fromHyphenatedNamespace() {}
            """);
        var harvester = CreateHarvester(CreateEnabledOptions("src/*.js"));

        var docs = await harvester.HarvestAsync(_testRoot);

        var groupPages = docs
            .Where(doc => string.Equals(doc.Metadata?.PageType, "javascript-api", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, groupPages.Length);
        Assert.Equal(2, groupPages.Select(doc => doc.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(groupPages, doc => string.Equals(doc.Path, "api/javascript/foo-bar", StringComparison.Ordinal));
        Assert.Contains(
            groupPages,
            doc => doc.Path.StartsWith("api/javascript/foo-bar-", StringComparison.Ordinal)
                   && !string.Equals(doc.Path, "api/javascript/foo-bar", StringComparison.Ordinal));
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
    public async Task HarvestAsync_ShouldParseJavaScriptModules()
    {
        await WriteAsync(
            "src/module.js",
            """
            /**
             * Public ESM helper.
             * @public
             * @namespace RazorWire
             */
            export function mount(root) {
                return root;
            }
            """);
        var harvester = CreateHarvester(new AppSurfaceDocsOptions());

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, doc => doc.Path.EndsWith("#function-mount", StringComparison.Ordinal));
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ShouldHarvestExportedVariableDeclarations()
    {
        await WriteAsync(
            "src/module.js",
            """
            /**
             * Mounts a hydrated island.
             * @public
             * @namespace RazorWire
             * @param {HTMLElement} root - Island root.
             */
            export const mount = (root) => root;
            """);
        var harvester = CreateHarvester(new AppSurfaceDocsOptions());

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, doc => doc.Path.EndsWith("#function-mount", StringComparison.Ordinal));
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ShouldTreatAttachedBrowserContractDocletsAsStandaloneOnly()
    {
        await WriteAsync(
            "src/contract.js",
            """
            /**
             * Form submission started.
             * @public
             * @namespace RazorWire
             * @event razorwire:form:submit-start
             * @target form
             * @firesWhen a form starts submitting.
             * @detail none
             */
            function internalSubmitStartMarker() {}
            """);
        var harvester = CreateHarvester(new AppSurfaceDocsOptions());

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, doc => doc.Path.EndsWith("#event-razorwire-form-submit-start", StringComparison.Ordinal));
        Assert.DoesNotContain(docs, doc => doc.Path.EndsWith("#function-internalsubmitstartmarker", StringComparison.Ordinal));
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
    public async Task HarvestAsync_ShouldDogfoodRazorWirePublicContractManifestWhenConfiguredWithIncludes()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var harvester = CreateHarvester(CreateEnabledOptions(
            "Web/ForgeTrust.RazorWire/assets/contracts/razorwire-public-contracts.js"));

        var docs = await harvester.HarvestAsync(repoRoot);

        var page = Assert.Single(docs, doc => string.Equals(doc.Path, "api/javascript/razorwire", StringComparison.Ordinal));
        Assert.Contains("event-razorwire-form-submit-start", page.Content, StringComparison.Ordinal);
        Assert.Contains("event-razorwire-form-failure", page.Content, StringComparison.Ordinal);
        Assert.Contains("event-razorwire-form-diagnostic", page.Content, StringComparison.Ordinal);
        Assert.Contains("event-razorwire-form-submit-end", page.Content, StringComparison.Ordinal);
        Assert.Contains("attribute-data-rw-form", page.Content, StringComparison.Ordinal);
        Assert.Contains("attribute-data-rw-strategy", page.Content, StringComparison.Ordinal);
        Assert.Contains("config-window-razorwire-config", page.Content, StringComparison.Ordinal);
        Assert.Contains("module-contract-mount", page.Content, StringComparison.Ordinal);
        Assert.Contains("css-hook-data-rw-form-error-generated-true", page.Content, StringComparison.Ordinal);
        Assert.Contains("css-custom-property-rw-form-error-text", page.Content, StringComparison.Ordinal);
        Assert.Contains("global-window-razorwire", page.Content, StringComparison.Ordinal);
        Assert.Contains("{&quot;load&quot;|&quot;idle&quot;|&quot;visible&quot;|&quot;only&quot;}", page.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("{&quot;load&quot;|&quot;idle&quot;|&quot;visible&quot;|&quot;immediate&quot;}", page.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            page.SymbolSourceProvenance!,
            provenance => provenance.SourcePath == "Web/ForgeTrust.RazorWire/assets/contracts/razorwire-public-contracts.js"
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
        options.Harvest.JavaScript.ExcludeGlobs = ["src/private/**"];
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
        await WriteAsync("src/malformed.js", "/**\n * Malformed public JavaScript.\n * @public\n */\nfunction broken( {");
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
        var duplicateAnchorDiagnostic = Assert.Single(
            diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptDuplicateAnchor);
        Assert.Contains("RazorWire", duplicateAnchorDiagnostic.Problem, StringComparison.Ordinal);
        Assert.Contains("name:RazorWire", duplicateAnchorDiagnostic.Problem, StringComparison.Ordinal);
        Assert.Contains(docs, doc => doc.Path.EndsWith("#event-razorwire-duplicate", StringComparison.Ordinal));
        Assert.Contains(docs, doc => doc.Path.EndsWith("#event-razorwire-duplicate-2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HarvestAsync_ShouldReportReparseDiagnostic_WhenMatchedFileIsDanglingSymlink()
    {
        var directory = Path.Join(_testRoot, "src");
        Directory.CreateDirectory(directory);
        var linkPath = Path.Join(directory, "missing.js");
        if (!TryCreateFileSymbolicLink(linkPath, Path.Join(directory, "target-does-not-exist.js")))
        {
            return;
        }

        var harvester = CreateHarvester(CreateEnabledOptions("src/missing.js"));

        var docs = await harvester.HarvestAsync(_testRoot);
        var diagnostic = Assert.Single(GetDiagnostics(harvester));

        Assert.Empty(docs);
        Assert.Equal(DocHarvestDiagnosticCodes.JavaScriptReparsePointSkipped, diagnostic.Code);
        Assert.Contains("file-system link", diagnostic.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HarvestAsync_ShouldReportReadDiagnostic_WhenExactIncludedFileIsMissing()
    {
        var harvester = CreateHarvester(CreateEnabledOptions("src/missing.js"));

        var docs = await harvester.HarvestAsync(_testRoot);
        var diagnostic = Assert.Single(GetDiagnostics(harvester));

        Assert.Empty(docs);
        Assert.Equal(DocHarvestDiagnosticCodes.JavaScriptMissingInclude, diagnostic.Code);
        Assert.Contains("src/missing.js", diagnostic.Problem, StringComparison.Ordinal);
        Assert.Contains("does not exist", diagnostic.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HarvestAsync_ShouldReportReadDiagnostic_WhenExactIncludedFileCannotBeRead()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await WriteAsync(
            "src/unreadable.js",
            """
            /**
             * Unreadable function.
             * @public
             * @namespace RazorWire
             */
            function unreadableApi() {}
            """);
        var filePath = Path.Join(_testRoot, "src", "unreadable.js");
        try
        {
            File.SetUnixFileMode(filePath, UnixFileMode.None);
            var fileReadDenied = false;
            try
            {
                using var probe = File.OpenRead(filePath);
                _ = probe.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                fileReadDenied = true;
            }

            if (!fileReadDenied)
            {
                return;
            }

            var harvester = CreateHarvester(CreateEnabledOptions("src/unreadable.js"));

            var docs = await harvester.HarvestAsync(_testRoot);
            var diagnostic = Assert.Single(GetDiagnostics(harvester));

            Assert.Empty(docs);
            Assert.Equal(DocHarvestDiagnosticCodes.JavaScriptParseFailed, diagnostic.Code);
            Assert.Contains("src/unreadable.js", diagnostic.Problem, StringComparison.Ordinal);
            Assert.Contains("could not be read", diagnostic.Problem, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void ClassifyHarvestCandidate_ShouldReturnOutsideRootWithoutReadingAttributes()
    {
        var outsidePath = Path.GetFullPath(Path.Join(_testRoot, "..", "outside.js"));
        var readAttributes = false;

        var candidate = JavaScriptDocHarvester.ClassifyHarvestCandidate(
            _testRoot,
            outsidePath,
            _ =>
            {
                readAttributes = true;
                return FileAttributes.Normal;
            });

        Assert.Equal(JavaScriptDocHarvester.JavaScriptHarvestCandidateStatus.OutsideRoot, candidate.Status);
        Assert.Equal(outsidePath, candidate.FullPath);
        Assert.False(readAttributes);
    }

    [Fact]
    public void ClassifyHarvestCandidate_ShouldReturnInaccessible_WhenAttributesCannotBeInspected()
    {
        var candidatePath = Path.Join(_testRoot, "src", "blocked.js");

        var candidate = JavaScriptDocHarvester.ClassifyHarvestCandidate(
            _testRoot,
            candidatePath,
            _ => throw new IOException("metadata denied"));

        Assert.Equal(JavaScriptDocHarvester.JavaScriptHarvestCandidateStatus.Inaccessible, candidate.Status);
        Assert.Equal(Path.GetFullPath(candidatePath), candidate.FullPath);
        Assert.Equal("src/blocked.js", candidate.RelativePath);
    }

    [Fact]
    public async Task HarvestAsync_ShouldReportReadDiagnostic_WhenIncludedDirectoryRootCannotBeEnumerated()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var unreadableDirectory = Path.Join(_testRoot, "src", "unreadable");
        Directory.CreateDirectory(unreadableDirectory);
        File.SetUnixFileMode(unreadableDirectory, UnixFileMode.None);
        try
        {
            var directoryEnumerationDenied = false;
            try
            {
                _ = Directory.GetFileSystemEntries(unreadableDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                directoryEnumerationDenied = true;
            }

            if (!directoryEnumerationDenied)
            {
                return;
            }

            var harvester = CreateHarvester(CreateEnabledOptions("src/unreadable/**/*.js"));

            var docs = await harvester.HarvestAsync(_testRoot);
            var diagnostic = Assert.Single(GetDiagnostics(harvester));

            Assert.Empty(docs);
            Assert.Equal(DocHarvestDiagnosticCodes.JavaScriptParseFailed, diagnostic.Code);
            Assert.Contains("src/unreadable", diagnostic.Problem, StringComparison.Ordinal);
            Assert.Contains("could not be inspected", diagnostic.Problem, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.SetUnixFileMode(
                unreadableDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task HarvestAsync_ShouldSkipExactIncludedFileReparsePoint_WithoutReadingExternalTarget()
    {
        var externalRoot = CreateExternalTempDirectory();
        try
        {
            var externalFile = Path.Join(externalRoot, "external.js");
            await File.WriteAllTextAsync(
                externalFile,
                """
                /**
                 * External function.
                 * @public
                 * @namespace External
                 */
                function externalApi() {}
                """);
            Directory.CreateDirectory(Path.Join(_testRoot, "src"));
            var linkPath = Path.Join(_testRoot, "src", "external.js");
            if (!TryCreateFileSymbolicLink(linkPath, externalFile))
            {
                return;
            }

            var harvester = CreateHarvester(CreateEnabledOptions("src/external.js"));

            var docs = await harvester.HarvestAsync(_testRoot);
            var diagnostic = Assert.Single(GetDiagnostics(harvester));

            Assert.Empty(docs);
            Assert.Equal(DocHarvestDiagnosticCodes.JavaScriptReparsePointSkipped, diagnostic.Code);
            Assert.Equal(DocHarvestDiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Contains("src/external.js", diagnostic.Problem, StringComparison.Ordinal);
            Assert.DoesNotContain(externalRoot, diagnostic.Problem, StringComparison.Ordinal);
            Assert.DoesNotContain(externalRoot, diagnostic.Cause, StringComparison.Ordinal);
            Assert.DoesNotContain(externalRoot, diagnostic.Fix, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public async Task HarvestAsync_ShouldSkipExactIncludedFileReparsePoint_WhenTargetStaysInsideRepository()
    {
        await WriteAsync(
            "src/real.js",
            """
            /**
             * Real function.
             * @public
             * @namespace RazorWire
             */
            function realApi() {}
            """);
        var linkPath = Path.Join(_testRoot, "src", "linked.js");
        if (!TryCreateFileSymbolicLink(linkPath, Path.Join(_testRoot, "src", "real.js")))
        {
            return;
        }

        var harvester = CreateHarvester(CreateEnabledOptions("src/linked.js"));

        var docs = await harvester.HarvestAsync(_testRoot);
        var diagnostic = Assert.Single(GetDiagnostics(harvester));

        Assert.Empty(docs);
        Assert.Equal(DocHarvestDiagnosticCodes.JavaScriptReparsePointSkipped, diagnostic.Code);
        Assert.Contains("src/linked.js", diagnostic.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarvestAsync_ShouldReportReparseDiagnostic_WhenExactIncludedFileIsDanglingSymlink()
    {
        Directory.CreateDirectory(Path.Join(_testRoot, "src"));
        var linkPath = Path.Join(_testRoot, "src", "missing.js");
        if (!TryCreateFileSymbolicLink(linkPath, Path.Join(_testRoot, "src", "target-does-not-exist.js")))
        {
            return;
        }

        var harvester = CreateHarvester(CreateEnabledOptions("src/missing.js"));

        var docs = await harvester.HarvestAsync(_testRoot);
        var diagnostic = Assert.Single(GetDiagnostics(harvester));

        Assert.Empty(docs);
        Assert.Equal(DocHarvestDiagnosticCodes.JavaScriptReparsePointSkipped, diagnostic.Code);
        Assert.DoesNotContain("target-does-not-exist", diagnostic.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("target-does-not-exist", diagnostic.Cause, StringComparison.Ordinal);
        Assert.DoesNotContain("target-does-not-exist", diagnostic.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarvestAsync_ShouldSkipGlobbedFileReparsePoint_WithoutDiagnostic()
    {
        var externalRoot = CreateExternalTempDirectory();
        try
        {
            var externalFile = Path.Join(externalRoot, "external.js");
            await File.WriteAllTextAsync(
                externalFile,
                """
                /**
                 * External function.
                 * @public
                 * @namespace External
                 */
                function externalApi() {}
                """);
            Directory.CreateDirectory(Path.Join(_testRoot, "src"));
            var linkPath = Path.Join(_testRoot, "src", "external.js");
            if (!TryCreateFileSymbolicLink(linkPath, externalFile))
            {
                return;
            }

            var harvester = CreateHarvester(CreateEnabledOptions("src/*.js"));

            var docs = await harvester.HarvestAsync(_testRoot);

            Assert.Empty(docs);
            Assert.Empty(GetDiagnostics(harvester));
        }
        finally
        {
            DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public async Task HarvestAsync_ShouldSkipChildDirectoryReparsePoint_WithoutReadingExternalTarget()
    {
        var externalRoot = CreateExternalTempDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(externalRoot, "external.js"),
                """
                /**
                 * External function.
                 * @public
                 * @namespace External
                 */
                function externalApi() {}
                """);
            Directory.CreateDirectory(Path.Join(_testRoot, "src"));
            var linkPath = Path.Join(_testRoot, "src", "linked");
            if (!TryCreateDirectorySymbolicLink(linkPath, externalRoot))
            {
                return;
            }

            var harvester = CreateHarvester(CreateEnabledOptions("src/**/*.js"));

            var docs = await harvester.HarvestAsync(_testRoot);

            Assert.Empty(docs);
            Assert.Empty(GetDiagnostics(harvester));
        }
        finally
        {
            DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public async Task HarvestAsync_ShouldSkipExactIncludeUnderDirectoryReparsePoint_WithoutReadingExternalTarget()
    {
        var externalRoot = CreateExternalTempDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(externalRoot, "api.js"),
                """
                /**
                 * External function.
                 * @public
                 * @namespace External
                 */
                function externalApi() {}
                """);
            var linkPath = Path.Join(_testRoot, "linked-src");
            if (!TryCreateDirectorySymbolicLink(linkPath, externalRoot))
            {
                return;
            }

            var harvester = CreateHarvester(CreateEnabledOptions("linked-src/api.js"));

            var docs = await harvester.HarvestAsync(_testRoot);
            var diagnostic = Assert.Single(GetDiagnostics(harvester));

            Assert.Empty(docs);
            Assert.Equal(DocHarvestDiagnosticCodes.JavaScriptReparsePointSkipped, diagnostic.Code);
            Assert.Contains("linked-src/api.js", diagnostic.Problem, StringComparison.Ordinal);
            Assert.DoesNotContain(externalRoot, diagnostic.Problem, StringComparison.Ordinal);
            Assert.DoesNotContain(externalRoot, diagnostic.Cause, StringComparison.Ordinal);
            Assert.DoesNotContain(externalRoot, diagnostic.Fix, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public async Task HarvestAsync_ShouldSkipGlobbedDirectoryReparsePoint_WithDiagnosticForConfiguredRoot()
    {
        var externalRoot = CreateExternalTempDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(externalRoot, "external.js"),
                """
                /**
                 * External function.
                 * @public
                 * @namespace External
                 */
                function externalApi() {}
                """);
            var linkPath = Path.Join(_testRoot, "linked-src");
            if (!TryCreateDirectorySymbolicLink(linkPath, externalRoot))
            {
                return;
            }

            var harvester = CreateHarvester(CreateEnabledOptions("linked-src/**/*.js"));

            var docs = await harvester.HarvestAsync(_testRoot);
            var diagnostic = Assert.Single(GetDiagnostics(harvester));

            Assert.Empty(docs);
            Assert.Equal(DocHarvestDiagnosticCodes.JavaScriptReparsePointSkipped, diagnostic.Code);
            Assert.Contains("linked-src", diagnostic.Problem, StringComparison.Ordinal);
            Assert.DoesNotContain(externalRoot, diagnostic.Problem, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public async Task HarvestAsync_ShouldSkipGlobalIncludeReparsePoint_WithDiagnostic()
    {
        var externalRoot = CreateExternalTempDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(externalRoot, "external.js"),
                """
                /**
                 * External function.
                 * @public
                 * @namespace External
                 */
                function externalApi() {}
                """);
            Directory.CreateDirectory(Path.Join(_testRoot, "src"));
            var linkPath = Path.Join(_testRoot, "src", "external.js");
            if (!TryCreateFileSymbolicLink(linkPath, Path.Join(externalRoot, "external.js")))
            {
                return;
            }

            var options = new AppSurfaceDocsOptions();
            options.Harvest.JavaScript.Enabled = true;
            options.Harvest.Paths.IncludeGlobs = ["src/external.js"];
            var harvester = CreateHarvester(options);

            var docs = await harvester.HarvestAsync(_testRoot);
            var diagnostic = Assert.Single(GetDiagnostics(harvester));

            Assert.Empty(docs);
            Assert.Equal(DocHarvestDiagnosticCodes.JavaScriptReparsePointSkipped, diagnostic.Code);
            Assert.Contains("src/external.js", diagnostic.Problem, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public async Task HarvestAsync_ShouldIgnoreGlobalNonJavaScriptReparsePoint_WithoutDiagnostic()
    {
        var externalRoot = CreateExternalTempDirectory();
        try
        {
            var externalReadme = Path.Join(externalRoot, "README.md");
            await File.WriteAllTextAsync(externalReadme, "# External notes");
            var linkPath = Path.Join(_testRoot, "README.md");
            if (!TryCreateFileSymbolicLink(linkPath, externalReadme))
            {
                return;
            }

            var options = new AppSurfaceDocsOptions();
            options.Harvest.JavaScript.StrictHealth = true;
            options.Harvest.Paths.IncludeGlobs = ["README.md"];
            var harvester = CreateHarvester(options);

            var docs = await harvester.HarvestAsync(_testRoot);

            Assert.Empty(docs);
            Assert.Empty(GetDiagnostics(harvester));
        }
        finally
        {
            DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public async Task HarvestAsync_ShouldUseBracketGlobToken_WhenResolvingStaticRoot()
    {
        await WriteAsync(
            "src/[ab]/public-api.js",
            """
            /**
             * Bracket glob function.
             * @public
             * @namespace RazorWire
             */
            function bracketGlobApi() {}
            """);
        await WriteAsync(
            "src/c/ignored.js",
            """
            /**
             * Ignored function.
             * @public
             * @namespace Ignored
             */
            function ignoredApi() {}
            """);
        var harvester = CreateHarvester(CreateEnabledOptions("src/[ab]/**/*.js"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, doc => doc.Title == "RazorWire JavaScript API");
        Assert.Contains(docs, doc => doc.Title == "bracketGlobApi");
        Assert.DoesNotContain(docs, doc => doc.Title == "Ignored");
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ShouldIgnoreBlankDuplicateAndEscapingIncludeRoots()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Public function.
             * @public
             * @namespace RazorWire
             */
            function publicApi() {}
            """);
        await WriteAsync(
            "ignored/public-api.js",
            """
            /**
             * Ignored function.
             * @public
             * @namespace RazorWire
             */
            function ignoredApi() {}
            """);
        await WriteAsync(
            "src/private-api.js",
            """
            /**
             * Private function.
             * @public
             * @namespace RazorWire
             */
            function privateApi() {}
            """);
        var harvester = CreateHarvester(CreateEnabledOptions(
            "",
            " ",
            Path.Join(_testRoot, "ignored", "public-api.js"),
            "../outside.js",
            "src/public*.js",
            "src/public*.js"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, doc => doc.Path.EndsWith("#function-publicapi", StringComparison.Ordinal));
        Assert.DoesNotContain(docs, doc => doc.Path.EndsWith("#function-ignoredapi", StringComparison.Ordinal));
        Assert.DoesNotContain(docs, doc => doc.Path.EndsWith("#function-privateapi", StringComparison.Ordinal));
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ShouldPruneRepositoryAndDefaultExcludedDirectories()
    {
        await WriteAsync(
            "src/components/public.js",
            """
            /**
             * Public function.
             * @public
             * @namespace RazorWire
             */
            function publicApi() {}
            """);
        await WriteAsync(".git/broken.js", "function broken( {");
        await WriteAsync("node_modules/broken.js", "function broken( {");
        var harvester = CreateHarvester(CreateEnabledOptions("**/*.js"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, doc => doc.Path.EndsWith("#function-publicapi", StringComparison.Ordinal));
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ShouldContinueWhenTraversalDirectoryCannotBeEnumerated()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await WriteAsync(
            "src/public.js",
            """
            /**
             * Public function.
             * @public
             * @namespace RazorWire
             */
            function publicApi() {}
            """);
        var unreadableDirectory = Path.Join(_testRoot, "src", "unreadable");
        Directory.CreateDirectory(unreadableDirectory);
        File.SetUnixFileMode(unreadableDirectory, UnixFileMode.None);
        var harvester = CreateHarvester(CreateEnabledOptions("src/**/*.js"));

        try
        {
            var directoryEnumerationDenied = false;
            try
            {
                _ = Directory.GetFileSystemEntries(unreadableDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                directoryEnumerationDenied = true;
            }

            if (!directoryEnumerationDenied)
            {
                return;
            }

            var docs = await harvester.HarvestAsync(_testRoot);

            Assert.Contains(docs, doc => doc.Path.EndsWith("#function-publicapi", StringComparison.Ordinal));
            Assert.Empty(GetDiagnostics(harvester));
        }
        finally
        {
            File.SetUnixFileMode(
                unreadableDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task HarvestAsync_ShouldSkipReparsePointDirectoriesWhileTraversing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await WriteAsync(
            "src/public.js",
            """
            /**
             * Public function.
             * @public
             * @namespace RazorWire
             */
            function publicApi() {}
            """);
        var sourceDirectory = Path.Join(_testRoot, "src");
        Directory.CreateSymbolicLink(Path.Join(sourceDirectory, "loop"), sourceDirectory);
        var harvester = CreateHarvester(CreateEnabledOptions("src/**/*.js"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Single(docs, doc => doc.Path.EndsWith("#function-publicapi", StringComparison.Ordinal));
        Assert.Empty(GetDiagnostics(harvester));
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
             * Destructured public binding.
             * @public
             * @namespace RazorWire
             */
            const { visible } = {};

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
            && diagnostic.Cause.Contains("unnamed declaration", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptMalformedPublicDoclet
            && diagnostic.Cause.Contains("typedef name", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptMalformedPublicDoclet
            && diagnostic.Cause.Contains("standalone public JavaScript doclet", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptIncompletePublicDoclet);
        Assert.Contains(docs, doc => doc.Path.EndsWith("#event-razorwire-incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HarvestAsync_ShouldValidateBrowserContractDoclets()
    {
        await WriteAsync(
            "src/contracts.js",
            """
            /**
             * Missing attribute fields.
             * @public
             * @namespace RazorWire
             * @attribute data-rw-form-failure
             */

            /**
             * CSS custom property must begin with dashes.
             * @public
             * @namespace RazorWire
             * @cssCustomProperty rw-form-error-text
             * @target [data-rw-form-error-generated="true"]
             * @syntax <color>
             */

            /**
             * Invalid broad selector hook.
             * @public
             * @namespace RazorWire
             * @cssHook form [data-rw-form-error-generated="true"]
             * @hookKind selector
             * @target generated form failure UI
             * @stability stable
             */

            /**
             * Missing hook stability.
             * @public
             * @namespace RazorWire
             * @cssHook .rw-form-error
             * @hookKind class
             * @target generated form failure UI
             */
            """);
        var harvester = CreateHarvester(CreateEnabledOptions("src/contracts.js"));

        var docs = await harvester.HarvestAsync(_testRoot);
        var diagnostics = GetDiagnostics(harvester);

        Assert.Contains(docs, doc => doc.Path.EndsWith("#attribute-data-rw-form-failure", StringComparison.Ordinal));
        Assert.Contains(docs, doc => doc.Path.EndsWith("#css-hook-rw-form-error", StringComparison.Ordinal));
        Assert.DoesNotContain(docs, doc => doc.Path.Contains("rw-form-error-text", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptIncompletePublicDoclet
            && diagnostic.Fix.Contains("@type", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptIncompletePublicDoclet
            && diagnostic.Fix.Contains("@stability", StringComparison.Ordinal));
        Assert.Equal(2, diagnostics.Count(diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptMalformedPublicDoclet));
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
    public async Task HarvestAsync_ShouldRequirePublicTagDuringBroadDiscoveryEvenWhenPublicTagIsNotRequired()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Event without a public marker.
             * @event razorwire:loose
             * @target form
             * @firesWhen default broad discovery sees ordinary JSDoc.
             * @detail none
             */
            """);
        var options = new AppSurfaceDocsOptions();
        options.Harvest.JavaScript.RequirePublicTag = false;
        var harvester = CreateHarvester(options);

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ShouldTreatPublicTagPrefilterCaseInsensitively()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Event with a mixed-case public marker.
             * @Public
             * @event razorwire:mixed-case-public
             * @target document
             * @firesWhen broad discovery sees a case-insensitive public marker.
             * @detail none
             */
            """);
        var harvester = CreateHarvester(new AppSurfaceDocsOptions());

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, doc => doc.Path.EndsWith("#event-razorwire-mixed-case-public", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HarvestAsync_ShouldRequirePublicTagWhenIncludeGlobsNormalizeEmpty()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Event without a public marker.
             * @event razorwire:invalid-include-loose
             * @target document
             * @firesWhen an invalid include glob should not disable broad-discovery safety.
             * @detail none
             */
            """);
        var options = CreateEnabledOptions("../invalid.js");
        options.Harvest.JavaScript.RequirePublicTag = false;
        var harvester = CreateHarvester(options);

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ShouldUseGlobalIncludeRootsForBroadDiscovery()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Public event inside the global include boundary.
             * @public
             * @event razorwire:global-include
             * @target document
             * @firesWhen broad discovery honors global include roots.
             * @detail none
             */
            """);
        var options = new AppSurfaceDocsOptions();
        options.Harvest.Paths.IncludeGlobs = ["src/**"];
        var harvester = CreateHarvester(options);
        var context = new DocHarvestContext(_testRoot, new ThrowingCandidateEnumerationPathPolicy());

        var docs = await harvester.HarvestAsync(context);

        Assert.Contains(docs, doc => doc.Path.EndsWith("#event-razorwire-global-include", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HarvestAsync_ShouldRenderOptionalEventMetadataAndMatchWildcardGlobs()
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
        var harvester = CreateHarvester(CreateEnabledOptions("src/public*.js"));

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
            new AppSurfaceDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);

        var payload = await aggregator.GetSearchIndexPayloadAsync();

        var document = Assert.Single(
            payload.Documents,
            doc => string.Equals(doc.Title, "razorwire:form:failure", StringComparison.Ordinal));
        Assert.Equal("/docs/api/javascript/razorwire#event-razorwire-form-failure", document.Path);
        Assert.Equal("javascript-event", document.PageType);
        Assert.Equal("JavaScript Event", document.PageTypeLabel);
        Assert.Equal("javascript", document.Language);
        Assert.Equal("JavaScript", document.LanguageLabel);
        Assert.Equal(["API Reference", "JavaScript", "RazorWire"], document.Breadcrumbs);
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
            new AppSurfaceDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);

        var health = await aggregator.GetHarvestHealthAsync();

        var diagnostic = Assert.Single(
            health.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptFileTooLarge);
        Assert.Equal("JavaScriptDocHarvester", diagnostic.HarvesterType);
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldKeepDefaultJavaScriptFailuresOutOfStrictHealth()
    {
        await WriteAsync("src/malformed.js", "/**\n * Broken public source.\n * @public\n */\nfunction broken( {");
        var options = new AppSurfaceDocsOptions();
        options.Source.RepositoryRoot = _testRoot;
        options.Contributor.Enabled = false;
        var harvester = CreateHarvester(options);
        var aggregator = new DocAggregator(
            [new StaticHarvester([new DocNode("Guide", "docs/guide.md", "<p>Guide</p>")]), harvester],
            options,
            new TestWebHostEnvironment(_testRoot),
            new Memo(new MemoryCache(new MemoryCacheOptions())),
            new AppSurfaceDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);

        var health = await aggregator.GetHarvestHealthAsync();

        Assert.Equal(DocHarvestHealthStatus.Healthy, health.Status);
        Assert.Equal(1, health.TotalHarvesters);
        Assert.Equal(1, health.SuccessfulHarvesters);
        Assert.Equal(0, health.FailedHarvesters);
        Assert.Contains(health.Harvesters, item => item.HarvesterType == nameof(JavaScriptDocHarvester)
            && item.Status == DocHarvesterHealthStatus.ReturnedEmpty);
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldKeepInvalidJavaScriptIncludeGlobsOutOfStrictHealth()
    {
        await WriteAsync("src/malformed.js", "/**\n * Broken public source.\n * @public\n */\nfunction broken( {");
        var options = CreateEnabledOptions("../invalid.js");
        options.Source.RepositoryRoot = _testRoot;
        options.Contributor.Enabled = false;
        var harvester = CreateHarvester(options);
        var aggregator = new DocAggregator(
            [new StaticHarvester([new DocNode("Guide", "docs/guide.md", "<p>Guide</p>")]), harvester],
            options,
            new TestWebHostEnvironment(_testRoot),
            new Memo(new MemoryCache(new MemoryCacheOptions())),
            new AppSurfaceDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);

        var health = await aggregator.GetHarvestHealthAsync();

        Assert.Equal(DocHarvestHealthStatus.Healthy, health.Status);
        Assert.Equal(1, health.TotalHarvesters);
        Assert.Equal(1, health.SuccessfulHarvesters);
        Assert.Equal(0, health.FailedHarvesters);
        Assert.Contains(health.Harvesters, item => item.HarvesterType == nameof(JavaScriptDocHarvester)
            && item.Status == DocHarvesterHealthStatus.ReturnedEmpty);
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldCountJavaScriptInStrictHealth_WhenStrictHealthIsEnabled()
    {
        await WriteAsync("src/too-big.js", "/**\n * Too large.\n * @public\n */\nconst value = '" + new string('x', 2048) + "';");
        var options = new AppSurfaceDocsOptions();
        options.Source.RepositoryRoot = _testRoot;
        options.Harvest.JavaScript.StrictHealth = true;
        options.Harvest.JavaScript.MaxFileSizeBytes = 1024;
        options.Contributor.Enabled = false;
        var harvester = CreateHarvester(options);
        var aggregator = new DocAggregator(
            [new StaticHarvester([new DocNode("Guide", "docs/guide.md", "<p>Guide</p>")]), harvester],
            options,
            new TestWebHostEnvironment(_testRoot),
            new Memo(new MemoryCache(new MemoryCacheOptions())),
            new AppSurfaceDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);

        var health = await aggregator.GetHarvestHealthAsync();

        Assert.Equal(DocHarvestHealthStatus.Degraded, health.Status);
        Assert.Equal(2, health.TotalHarvesters);
        Assert.Equal(1, health.SuccessfulHarvesters);
        Assert.Equal(1, health.FailedHarvesters);
        Assert.Contains(health.Harvesters, item => item.HarvesterType == nameof(JavaScriptDocHarvester)
            && item.Status == DocHarvesterHealthStatus.Failed);
        Assert.Contains(health.Diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptFileTooLarge);
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldFailStrictJavaScript_WhenExactIncludeIsReparsePoint()
    {
        var externalRoot = CreateExternalTempDirectory();
        try
        {
            var externalFile = Path.Join(externalRoot, "external.js");
            await File.WriteAllTextAsync(
                externalFile,
                """
                /**
                 * External function.
                 * @public
                 * @namespace External
                 */
                function externalApi() {}
                """);
            Directory.CreateDirectory(Path.Join(_testRoot, "src"));
            var linkPath = Path.Join(_testRoot, "src", "external.js");
            if (!TryCreateFileSymbolicLink(linkPath, externalFile))
            {
                return;
            }

            var options = CreateEnabledOptions("src/external.js");
            options.Source.RepositoryRoot = _testRoot;
            options.Contributor.Enabled = false;
            var harvester = CreateHarvester(options);
            var aggregator = new DocAggregator(
                [new StaticHarvester([new DocNode("Guide", "docs/guide.md", "<p>Guide</p>")]), harvester],
                options,
                new TestWebHostEnvironment(_testRoot),
                new Memo(new MemoryCache(new MemoryCacheOptions())),
                new AppSurfaceDocsHtmlSanitizer(),
                NullLogger<DocAggregator>.Instance);

            var health = await aggregator.GetHarvestHealthAsync();

            Assert.Equal(DocHarvestHealthStatus.Degraded, health.Status);
            Assert.Equal(2, health.TotalHarvesters);
            Assert.Equal(1, health.SuccessfulHarvesters);
            Assert.Equal(1, health.FailedHarvesters);
            Assert.Contains(health.Harvesters, item => item.HarvesterType == nameof(JavaScriptDocHarvester)
                && item.Status == DocHarvesterHealthStatus.Failed);
            Assert.Contains(
                health.Diagnostics,
                diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptReparsePointSkipped
                              && diagnostic.Severity == DocHarvestDiagnosticSeverity.Error);
        }
        finally
        {
            DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldFailStrictJavaScriptEventDocletsWithoutStrictHealth()
    {
        await WriteAsync(
            "src/public-api.js",
            """
            /**
             * Public event.
             * @public
             * @event razorwire:missing
             */
            """);
        var options = new AppSurfaceDocsOptions();
        options.Source.RepositoryRoot = _testRoot;
        options.Harvest.JavaScript.RequireCompleteEventDoclets = true;
        options.Contributor.Enabled = false;
        var harvester = CreateHarvester(options);
        var aggregator = new DocAggregator(
            [new StaticHarvester([new DocNode("Guide", "docs/guide.md", "<p>Guide</p>")]), harvester],
            options,
            new TestWebHostEnvironment(_testRoot),
            new Memo(new MemoryCache(new MemoryCacheOptions())),
            new AppSurfaceDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);

        var health = await aggregator.GetHarvestHealthAsync();
        var response = AppSurfaceDocsHarvestHealthResponse.FromSnapshot(health);

        Assert.Equal(DocHarvestHealthStatus.Degraded, health.Status);
        Assert.Equal(2, health.TotalHarvesters);
        Assert.Equal(1, health.SuccessfulHarvesters);
        Assert.Equal(1, health.FailedHarvesters);
        Assert.False(response.Verification.Ok);
        Assert.Equal(503, response.Verification.HttpStatusCode);
        Assert.Contains(health.Harvesters, item => item.HarvesterType == nameof(JavaScriptDocHarvester)
            && item.Status == DocHarvesterHealthStatus.Failed);
        Assert.Contains(
            health.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptIncompletePublicEventDoclet
                          && diagnostic.Severity == DocHarvestDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldFailStrictJavaScript_WhenSomeDocsStillRender()
    {
        await WriteAsync(
            "src/good.js",
            """
            /**
             * Public event.
             * @public
             * @event razorwire:good
             * @target document
             * @firesWhen strict health sees a valid public contract.
             * @detail none
             */
            """);
        await WriteAsync("src/bad.js", "/**\n * Broken public source.\n * @public\n */\nfunction broken( {");
        var options = CreateEnabledOptions("src/*.js");
        options.Source.RepositoryRoot = _testRoot;
        options.Contributor.Enabled = false;
        var harvester = CreateHarvester(options);
        var aggregator = new DocAggregator(
            [new StaticHarvester([new DocNode("Guide", "docs/guide.md", "<p>Guide</p>")]), harvester],
            options,
            new TestWebHostEnvironment(_testRoot),
            new Memo(new MemoryCache(new MemoryCacheOptions())),
            new AppSurfaceDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);

        var health = await aggregator.GetHarvestHealthAsync();

        Assert.Equal(DocHarvestHealthStatus.Degraded, health.Status);
        Assert.Equal(2, health.TotalHarvesters);
        Assert.Equal(1, health.SuccessfulHarvesters);
        Assert.Equal(1, health.FailedHarvesters);
        Assert.Contains(health.Harvesters, item => item.HarvesterType == nameof(JavaScriptDocHarvester)
            && item.Status == DocHarvesterHealthStatus.Failed
            && item.DocCount > 0);
        Assert.Contains(health.Diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.JavaScriptParseFailed);
    }

    public void Dispose()
    {
        DeleteDirectory(_testRoot);
    }

    private static JavaScriptDocHarvester CreateHarvester(AppSurfaceDocsOptions options)
    {
        return new JavaScriptDocHarvester(options, NullLogger<JavaScriptDocHarvester>.Instance);
    }

    private static AppSurfaceDocsOptions CreateEnabledOptions(params string[] include)
    {
        return new AppSurfaceDocsOptions
        {
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                JavaScript = new AppSurfaceDocsJavaScriptHarvestOptions
                {
                    Enabled = true,
                    IncludeGlobs = include,
                    ExcludeGlobs = [.. AppSurfaceDocsJavaScriptHarvestOptions.DefaultExcludeGlobs]
                }
            }
        };
    }

    private static IReadOnlyList<DocHarvestDiagnostic> GetDiagnostics(JavaScriptDocHarvester harvester)
    {
        return ((IDocHarvesterDiagnosticProvider)harvester).GetHarvestDiagnostics();
    }

    private static string CreateExternalTempDirectory()
    {
        var path = Path.Join(Path.GetTempPath(), "AppSurfaceDocsTests_JS_External", Guid.NewGuid().ToString());
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup for temporary symlink tests.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup for temporary symlink tests.
        }
        catch (PlatformNotSupportedException)
        {
            // Best effort cleanup for temporary symlink tests.
        }
    }

    private sealed class ThrowingCandidateEnumerationPathPolicy : IHarvestPathPolicy
    {
        public AppSurfaceDocsHarvestPathDecision Evaluate(
            string relativePath,
            AppSurfaceDocsHarvestSourceKind sourceKind)
        {
            var included = ShouldIncludeFilePath(relativePath, sourceKind);
            return new AppSurfaceDocsHarvestPathDecision(
                included,
                relativePath,
                sourceKind,
                included
                    ? AppSurfaceDocsHarvestPathDecisionCode.IncludedByGlobalInclude
                    : AppSurfaceDocsHarvestPathDecisionCode.ExcludedByGlobalIncludeMiss,
                [],
                []);
        }

        public bool ShouldIncludeFilePath(
            string relativePath,
            AppSurfaceDocsHarvestSourceKind sourceKind)
        {
            return relativePath.StartsWith("src/", StringComparison.Ordinal);
        }

        public bool ShouldPruneDirectory(
            string relativeDirectory,
            AppSurfaceDocsHarvestSourceKind sourceKind)
        {
            return false;
        }

        public IEnumerable<string> EnumerateCandidateFiles(
            string rootPath,
            AppSurfaceDocsHarvestSourceKind sourceKind,
            string searchPattern,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Broad JavaScript discovery should use configured global include roots.");
        }
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

    private sealed class StaticHarvester(IReadOnlyList<DocNode> docs) : IDocHarvester
    {
        public Task<IReadOnlyList<DocNode>> HarvestAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(docs);
        }
    }
}
