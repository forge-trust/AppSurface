using System.Security.Claims;
using System.Text.Json;
using AngleSharp;
using FakeItEasy;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Controllers;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.RazorWire.Bridge;
using ForgeTrust.RazorWire.Streams;
using Ganss.Xss;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace ForgeTrust.AppSurface.Docs.Tests;

public class DocsControllerTests : IDisposable
{
    private readonly List<string> _temporaryCatalogRoots = [];
    private readonly DocAggregator _aggregator;
    private readonly DocsController _controller;
    private readonly IDocHarvester _harvesterFake;
    private readonly IMemoryCache _cache;
    private readonly IMemo _memo;
    private readonly ILogger<DocsController> _controllerLoggerFake;
    private readonly ILogger<DocFeaturedPageResolver> _featuredPageResolverLoggerFake;
    private readonly IAppSurfaceDocsHtmlSanitizer _sanitizerFake;

    public DocsControllerTests()
    {
        // Mock Aggregator dependencies
        _harvesterFake = A.Fake<IDocHarvester>();
        var loggerFake = A.Fake<ILogger<DocAggregator>>();
        _controllerLoggerFake = A.Fake<ILogger<DocsController>>();
        _featuredPageResolverLoggerFake = A.Fake<ILogger<DocFeaturedPageResolver>>();
        var options = new AppSurfaceDocsOptions();
        _cache = new MemoryCache(new MemoryCacheOptions());
        var envFake = A.Fake<IWebHostEnvironment>();
        _sanitizerFake = A.Fake<IAppSurfaceDocsHtmlSanitizer>();
        A.CallTo(() => envFake.ContentRootPath).Returns(Path.GetTempPath());
        A.CallTo(() => _sanitizerFake.Sanitize(A<string>._))
            .ReturnsLazily((string input) => input);
        _memo = new Memo(_cache);

        // Use real Aggregator with fake dependencies (or we could fake Aggregator but it's a concrete class)
        // Since Controller takes concrete DocAggregator, we instantiate it.
        _aggregator = new DocAggregator(
            new[] { _harvesterFake },
            options,
            envFake,
            _memo,
            _sanitizerFake,
            loggerFake
        );

        _controller = new DocsController(
            _aggregator,
            new DocFeaturedPageResolver(_featuredPageResolverLoggerFake),
            _controllerLoggerFake)
        {
            ControllerContext = CreateControllerContext(new DefaultHttpContext())
        };
        _controller.Url = new UrlHelper(_controller.ControllerContext);
    }

    [Fact]
    public async Task Index_ShouldReturnLandingViewModelWithFeaturedPages()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    Title = "AppSurface",
                    Summary = "Start with the proof paths that matter most.",
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Question = "How does composition work?",
                                Path = "guides/composition.md",
                                Order = 10
                            })
                    ]
                }),
            new(
                "Composition",
                "guides/composition.md",
                "<p>Guide body</p>",
                Metadata: new DocMetadata
                {
                    Summary = "See the composition model.",
                    PageType = "guide"
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        Assert.True(model.HasFeaturedPages);
        Assert.Equal("AppSurface", model.Heading);
        Assert.Equal("Start with the proof paths that matter most.", model.Description);
        var featuredPage = SingleFeaturedPage(model);
        Assert.Equal("How does composition work?", featuredPage.Question);
        Assert.Equal("Composition", featuredPage.Title);
        Assert.Equal("/docs/guides/composition", featuredPage.Href);
        Assert.Equal("guide", featuredPage.PageType);
        Assert.Equal("See the composition model.", featuredPage.SupportingText);
    }

    [Fact]
    public async Task Index_ShouldUseDocTitleForCuratedHeading_WhenMetadataTitleIsMissing()
    {
        var docs = new List<DocNode>
        {
            new(
                "AppSurface",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Path = "guides/composition.md"
                            })
                    ]
                }),
            new("Composition", "guides/composition.md", "<p>Guide body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        Assert.Equal("AppSurface", model.Heading);
    }

    [Fact]
    public async Task Index_ShouldReturnNeutralLanding_WhenRootReadmeIsMissing()
    {
        var docs = new List<DocNode>
        {
            new("Guide", "guides/intro.md", "<p>Guide body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        Assert.False(model.HasFeaturedPages);
        Assert.Equal("Documentation", model.Heading);
        Assert.Equal(
            "Start with the strongest proof path, then branch into guides, examples, and reference once you know where you want to go deeper.",
            model.Description);
        Assert.Null(model.StartHereHref);
        Assert.Single(model.VisibleDocs);
    }

    [Fact]
    public async Task Index_ShouldReturnNeutralLanding_WhenRootReadmeHasNoMetadata()
    {
        var docs = new List<DocNode>
        {
            new("Home", "README.md", "<p>Home</p>"),
            new("Guide", "guides/intro.md", "<p>Guide body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        Assert.False(model.HasFeaturedPages);
        Assert.Equal("Documentation", model.Heading);
        Assert.Null(model.StartHereHref);
        Assert.Equal(2, model.VisibleDocs.Count);
    }

    [Fact]
    public async Task Index_ShouldReturnNeutralLanding_WhenRootReadmeHasNoFeaturedPages()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    Summary = "Intro"
                }),
            new("Guide", "guides/intro.md", "<p>Guide body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        Assert.False(model.HasFeaturedPages);
        Assert.Null(model.StartHereHref);
        Assert.Equal(2, model.VisibleDocs.Count);
    }

    [Fact]
    public async Task Index_ShouldExposeStartHereHref_WhenStartHereSectionExists()
    {
        var docs = new List<DocNode>
        {
            new("Home", "README.md", "<p>Home</p>"),
            new(
                "Quickstart",
                "guides/quickstart.md",
                "<p>Quickstart body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    Summary = "Start here."
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        Assert.Equal("/docs/sections/start-here", model.StartHereHref);
    }

    [Fact]
    public async Task Index_ShouldUseFeaturedQuestionAsSecondaryEyebrow_WhenGroupLabelIsBlank()
    {
        var docs = new List<DocNode>
        {
            new("Home", "README.md", "<p>Home</p>"),
            new(
                "Concept Landing",
                "concepts/README.md",
                "<p>Concepts</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts",
                    SectionLanding = true,
                    FeaturedPageGroups =
                    [
                        new DocFeaturedPageGroupDefinition
                        {
                            Label = " ",
                            Pages =
                            [
                                new DocFeaturedPageDefinition
                                {
                                    Question = "Need the model?",
                                    Path = "concepts/model.md",
                                    SupportingCopy = "Read the model first."
                                }
                            ]
                        }
                    ]
                }),
            new(
                "Model",
                "concepts/model.md",
                "<p>Model</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts"
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        var concepts = Assert.Single(model.SecondarySections, section => section.Section == DocPublicSection.Concepts);
        var keyRoute = Assert.Single(concepts.KeyRoutes);
        Assert.Equal("Need the model?", keyRoute.Eyebrow);
        Assert.Equal("Read the model first.", keyRoute.Summary);
    }

    [Fact]
    public async Task Index_ShouldNotUseRootReadmeAsFallbackFeaturedPage()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    Summary = "Start here."
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        Assert.False(model.HasFeaturedPages);
        Assert.Empty(model.FeaturedPageGroups);
    }

    [Fact]
    public async Task Index_ShouldNotUseStartHereLandingDocAsFallbackFeaturedPage()
    {
        var docs = new List<DocNode>
        {
            new("Home", "README.md", "<p>Home</p>"),
            new(
                "Start Here",
                "guides/start-here.md",
                "<p>Start here landing</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    Order = 0,
                    SectionLanding = true,
                    Summary = "Section wrapper."
                }),
            new(
                "Install",
                "guides/install.md",
                "<p>Install</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    Order = 10,
                    Summary = "Install first."
                }),
            new(
                "Build",
                "guides/build.md",
                "<p>Build</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    Order = 20,
                    Summary = "Build next."
                }),
            new(
                "Quickstart",
                "quickstart.md",
                "<p>Quickstart</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    Order = 30
                }),
            new(
                "Deploy",
                "guides/deploy.md",
                "<p>Deploy</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    Order = 40,
                    Summary = "Ship it."
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        var group = Assert.Single(model.FeaturedPageGroups);
        Assert.Equal(["Install", "Build", "Quickstart"], group.Pages.Select(page => page.Title).ToArray());
        Assert.Equal(["Understand", "See Proof", "Adopt Next"], group.Pages.Select(page => page.Question).ToArray());
        Assert.Equal(3, group.Pages.Count);
        Assert.DoesNotContain(group.Pages, page => page.Title == "Deploy");
        Assert.DoesNotContain(group.Pages, page => page.Title == "Start Here");
    }

    [Fact]
    public async Task Section_ShouldNotExposeStartHereHref_WhenStartHereSectionIsUnavailable()
    {
        var docs = new List<DocNode>
        {
            new(
                "Conceptual Overview",
                "concepts/overview.md",
                "<p>Concept body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts",
                    Summary = "Understand the concepts."
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Section("concepts");

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocSectionPageViewModel>(viewResult.Model);
        Assert.False(model.IsUnavailable);
        Assert.Null(model.StartHereHref);
        Assert.Equal("/docs", model.DocsHomeHref);
    }

    [Fact]
    public async Task Section_ShouldNotExposeStartHereHref_OnUnavailablePage_WhenStartHereSectionIsUnavailable()
    {
        var docs = new List<DocNode>
        {
            new(
                "Conceptual Overview",
                "concepts/overview.md",
                "<p>Concept body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts"
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Section("start-here");

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocSectionPageViewModel>(viewResult.Model);
        Assert.True(model.IsUnavailable);
        Assert.Null(model.StartHereHref);
        Assert.Equal("/docs", model.DocsHomeHref);
    }

    [Fact]
    public async Task Section_ShouldBuildSparseRoutes_WhenOnlyOrderedPageExists()
    {
        var docs = new List<DocNode>
        {
            new(
                "Conceptual Overview",
                "concepts/overview.md",
                "<p>Concept body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts",
                    Order = 10
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Section("concepts");

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocSectionPageViewModel>(viewResult.Model);
        Assert.True(model.IsSparse);
        var keyRoute = Assert.Single(model.KeyRoutes);
        Assert.Equal("Conceptual Overview", keyRoute.Title);
        Assert.Null(keyRoute.Summary);
    }

    [Fact]
    public async Task Section_ShouldReturnUnavailableView_ForUnknownSlugs()
    {
        var docs = new List<DocNode>
        {
            new("Guide", "guides/intro.md", "<p>Guide body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Section("definitely-not-a-section");

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocSectionPageViewModel>(viewResult.Model);
        Assert.True(model.IsUnavailable);
        Assert.Null(model.Section);
    }

    [Fact]
    public async Task Section_ShouldRedirectToLandingDoc_WhenSectionHasAuthoredLanding()
    {
        var docs = new List<DocNode>
        {
            new(
                "Concept Landing",
                "concepts/landing.md",
                "<p>Landing body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts",
                    SectionLanding = true
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Section("concepts");

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/docs/concepts/landing", redirect.Url);
    }

    [Fact]
    public void VersionEntry_ShouldPrefixRequestPathBaseInRedirect_WhenVersioningIsDisabled()
    {
        SetControllerHttpContext(pathBase: "/some-base");

        var result = _controller.VersionEntry();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/some-base/docs", redirect.Url);
    }

    [Fact]
    public void VersionEntry_ShouldAppendTrailingSlash_WhenRootMountedDocsHomeUsesPathBase()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(new List<DocNode>());
        var (controller, cache, memo) = CreateController(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    DocsRootPath = "/"
                },
                Versioning = new AppSurfaceDocsVersioningOptions
                {
                    Enabled = false
                }
            },
            harvester);

        using (memo)
        using (cache)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.PathBase = "/some-base";
            controller.ControllerContext = CreateControllerContext(httpContext);
            controller.Url = new UrlHelper(controller.ControllerContext);

            var result = controller.VersionEntry();

            var redirect = Assert.IsType<RedirectResult>(result);
            Assert.Equal("/some-base/", redirect.Url);
        }
    }

    [Theory]
    [InlineData("api")]
    [InlineData("reference")]
    [InlineData("API Reference")]
    public async Task Section_ShouldRedirectAliasSectionRequests_ToCanonicalSlug(string requestedSlug)
    {
        var docs = new List<DocNode>
        {
            new(
                "Service API",
                "api/service.md",
                "<p>API body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "API Reference"
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Section(requestedSlug);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/docs/sections/api-reference", redirect.Url);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("reference")]
    [InlineData("API Reference")]
    public async Task Section_ShouldPrefixRequestPathBaseInAliasRedirects(string requestedSlug)
    {
        var docs = new List<DocNode>
        {
            new(
                "Service API",
                "api/service.md",
                "<p>API body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "API Reference"
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);
        SetControllerHttpContext(pathBase: "/some-base");

        var result = await _controller.Section(requestedSlug);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/some-base/docs/sections/api-reference", redirect.Url);
    }

    [Fact]
    public async Task Section_ShouldPrefixRequestPathBaseInLandingDocRedirects()
    {
        var docs = new List<DocNode>
        {
            new(
                "Concept Landing",
                "concepts/landing.md",
                "<p>Landing body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts",
                    SectionLanding = true
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);
        SetControllerHttpContext(pathBase: "/some-base");

        var result = await _controller.Section("concepts");

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/some-base/docs/concepts/landing", redirect.Url);
    }

    [Fact]
    public async Task Index_ShouldSkipHiddenFeaturedPages_AndFallbackWhenNoVisibleEntriesRemain()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Question = "How does composition work?",
                                Path = "guides/composition.md"
                            })
                    ]
                }),
            new(
                "Composition",
                "guides/composition.md",
                "<p>Guide body</p>",
                Metadata: new DocMetadata
                {
                    HideFromPublicNav = true,
                    Summary = "Hidden"
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        Assert.False(model.HasFeaturedPages);
        AssertWarningLogged("destination page is hidden from public navigation");
    }

    [Fact]
    public async Task Index_ShouldBuildSecondaryKeyRoutes_ForUnorderedSectionPages()
    {
        var docs = new List<DocNode>
        {
            new("Home", "README.md", "<p>Home</p>"),
            new(
                "Concept Landing",
                "concepts/landing.md",
                "<p>Landing body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts",
                    SectionLanding = true
                }),
            new(
                "Conceptual Overview",
                "concepts/overview.md",
                "<p>Concept body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts"
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        var secondarySection = Assert.Single(model.SecondarySections);
        var keyRoute = Assert.Single(secondarySection.KeyRoutes);
        Assert.Equal("Conceptual Overview", keyRoute.Title);
        Assert.Null(keyRoute.Summary);
    }

    [Fact]
    public async Task Index_ShouldSkipFeaturedPagesWithoutDestinationPath_AndLogWarning()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Question = "Where do I start?"
                            })
                    ]
                }),
            new("Guide", "guides/intro.md", "<p>Guide body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        Assert.False(model.HasFeaturedPages);
        AssertWarningLogged("has no destination path");
    }

    [Fact]
    public async Task Index_ShouldSkipMissingFeaturedPages_AndLogWarning()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Question = "Show me an example",
                                Path = "examples/missing.md"
                            })
                    ]
                }),
            new("Guide", "guides/intro.md", "<p>Guide body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        Assert.False(model.HasFeaturedPages);
        AssertWarningLogged("destination page could not be resolved");
    }

    [Fact]
    public async Task Index_ShouldSkipDuplicateFeaturedPages_AndKeepFirstResolvedEntry()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    Title = "AppSurface",
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Question = "Start here",
                                Path = "guides/composition.md"
                            },
                            new DocFeaturedPageDefinition
                            {
                                Question = "Duplicate",
                                Path = "guides/composition"
                            })
                    ]
                }),
            new(
                "Composition",
                "guides/composition.md",
                "<p>Guide body</p>",
                Metadata: new DocMetadata
                {
                    Summary = "Composition summary."
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        var featuredPage = SingleFeaturedPage(model);
        Assert.Equal("Start here", featuredPage.Question);
        AssertWarningLogged("destination is already featured");
    }

    [Fact]
    public async Task Index_ShouldUseLandingDocAsFallbackKeyRoute_WhenSecondarySectionHasNoOtherVisiblePages()
    {
        var docs = new List<DocNode>
        {
            new("Home", "README.md", "<p>Home</p>"),
            new(
                "Concept Landing",
                "concepts/landing.md",
                "<p>Landing body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts",
                    SectionLanding = true
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        var secondarySection = Assert.Single(model.SecondarySections);
        var keyRoute = Assert.Single(secondarySection.KeyRoutes);
        Assert.Equal("Concept Landing", keyRoute.Title);
        Assert.Equal("/docs/concepts/landing", keyRoute.Href);
    }

    [Fact]
    public async Task Index_ShouldPreferAuthoredSupportingCopy_AndResolveCanonicalFeaturedPaths()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Path = "guides/composition",
                                SupportingCopy = "Authored copy wins."
                            })
                    ]
                }),
            new(
                "Composition",
                "guides/composition.md",
                "<p>Guide body</p>",
                Metadata: new DocMetadata
                {
                    Summary = "Destination summary should lose.",
                    PageType = "guide"
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        var featuredPage = SingleFeaturedPage(model);
        Assert.Equal("Composition", featuredPage.Question);
        Assert.Equal("Authored copy wins.", featuredPage.SupportingText);
    }

    [Fact]
    public async Task Index_ShouldUseCuratedFallbacks_WhenLandingMetadataUsesHomeDefaults()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    Title = " Home ",
                    Summary = "   ",
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Path = "guides/composition.md"
                            })
                    ]
                }),
            new(
                "Composition",
                "guides/composition.md",
                "<p>Guide body</p>",
                Metadata: new DocMetadata
                {
                    Title = "Composition Guide",
                    Summary = "Destination summary should still appear on the card.",
                    PageType = "guide"
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        var featuredPage = SingleFeaturedPage(model);
        Assert.Equal("Documentation", model.Heading);
        Assert.Equal(
            "Start with the proof path that answers the first evaluator questions, then move into the sections that fit your next decision.",
            model.Description);
        Assert.Equal("Composition Guide", featuredPage.Question);
        Assert.Equal("Composition Guide", featuredPage.Title);
    }

    [Fact]
    public async Task Index_ShouldUseNeutralHeading_WhenLandingDocTitleIsWhitespace()
    {
        var docs = new List<DocNode>
        {
            new(
                "   ",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    Title = "   ",
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Path = "guides/composition.md"
                            })
                    ]
                }),
            new("Composition", "guides/composition.md", "<p>Guide body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        Assert.True(model.HasFeaturedPages);
        Assert.Equal("Documentation", model.Heading);
    }

    [Fact]
    public async Task Index_ShouldResolveFeaturedPathsWithLeadingWindowsSeparators()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Path = "\\guides\\composition.md"
                            })
                    ]
                }),
            new("Composition", "guides/composition.md", "<p>Guide body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        var featuredPage = SingleFeaturedPage(model);
        Assert.Equal("/docs/guides/composition", featuredPage.Href);
    }

    [Fact]
    public async Task Index_ShouldHonorConfiguredLiveDocsRoot_ForCuratedFeaturedPages()
    {
        var harvester = A.Fake<IDocHarvester>();
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                DocsRootPath = "/docs/next"
            },
            Versioning = new AppSurfaceDocsVersioningOptions
            {
                Enabled = true
            }
        };
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Path = "guides/composition.md"
                            })
                    ]
                }),
            new("Composition", "guides/composition.md", "<p>Guide body</p>")
        };
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var (controller, cache, memo) = CreateController(options, harvester);
        using (memo)
        using (cache)
        {
            var result = await controller.Index();

            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
            var featuredPage = SingleFeaturedPage(model);
            Assert.Equal("/docs/next/guides/composition", featuredPage.Href);
        }
    }

    [Fact]
    public async Task Index_ShouldResolveConfiguredRootCanonicalFeaturedPaths()
    {
        var harvester = A.Fake<IDocHarvester>();
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                DocsRootPath = "/docs/next"
            },
            Versioning = new AppSurfaceDocsVersioningOptions
            {
                Enabled = true
            }
        };
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Path = "/docs/next/guides/composition"
                            })
                    ]
                }),
            new("Composition", "guides/composition.md", "<p>Guide body</p>")
        };
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var (controller, cache, memo) = CreateController(options, harvester);
        using (memo)
        using (cache)
        {
            var result = await controller.Index();

            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
            var featuredPage = SingleFeaturedPage(model);
            Assert.Equal("/docs/next/guides/composition", featuredPage.Href);
        }
    }

    [Fact]
    public async Task Index_ShouldPreferBestFallbackCandidate_WhenFeaturedPathHasNoExactCanonicalMatch()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Path = "guides/intro.md#missing-fragment"
                            })
                    ]
                }),
            new("Guide Root", "guides/intro.md", "<p>Root body</p>"),
            new("Guide Empty Anchor", "guides/intro.md#details", "   "),
            new("Guide Filled Anchor", "guides/intro.md#setup", "<p>Setup body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        var featuredPage = SingleFeaturedPage(model);
        Assert.Equal("Guide Root", featuredPage.Question);
        Assert.Equal("Guide Root", featuredPage.Title);
        Assert.Equal("/docs/guides/intro", featuredPage.Href);
    }

    [Fact]
    public async Task Index_ShouldPreferNonEmptyFallbackCandidate_WhenAllFallbackEntriesUseFragments()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Path = "guides/advanced.md#missing-fragment"
                            })
                    ]
                }),
            new("Guide Empty Fragment", "guides/advanced.md#details", "   "),
            new("Guide Rich Fragment", "guides/advanced.md#setup", "<p>Setup body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocLandingViewModel>(viewResult.Model);
        var featuredPage = SingleFeaturedPage(model);
        Assert.Equal("Guide Rich Fragment", featuredPage.Question);
        Assert.Equal("Guide Rich Fragment", featuredPage.Title);
        Assert.Equal("/docs/guides/advanced#setup", featuredPage.Href);
    }

    [Fact]
    public async Task Details_ShouldReturnView_WhenDocExists()
    {
        // Arrange
        var docs = new List<DocNode> { new DocNode("Title", "target-path", "content") };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        // Act
        var result = await _controller.Details("target-path.html");

        // Assert
        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocDetailsViewModel>(viewResult.Model);
        Assert.Equal("Title", model.Title);
        Assert.Equal("Title", model.Document.Title);
        Assert.Equal("/docs/target-path.html", model.CanonicalUrl);
    }

    [Fact]
    public async Task Details_ShouldPopulateFeaturedGroupsAndSectionGroups_WhenDocIsSectionLanding()
    {
        var docs = new List<DocNode>
        {
            new(
                "Start Here",
                "start-here/README.md",
                "<p>Start here</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    SectionLanding = true,
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Question = "Install next?",
                                Path = "guides/install.md",
                                SupportingCopy = "Create the first AppSurface app."
                            })
                    ]
                }),
            new(
                "Install",
                "guides/install.md",
                "<p>Install</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    Summary = "Create the first AppSurface app.",
                    Order = 10
                }),
            new(
                "Configure",
                "guides/configure.md",
                "<p>Configure</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    Summary = "Tune the first app.",
                    Order = 20
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Details("start-here");

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocDetailsViewModel>(viewResult.Model);
        Assert.True(model.IsSectionLanding);
        Assert.Equal(DocPublicSection.StartHere, model.PublicSection);
        Assert.Equal("Start Here", model.PublicSectionLabel);

        var featuredGroup = Assert.Single(model.FeaturedPageGroups);
        var featuredPage = Assert.Single(featuredGroup.Pages);
        Assert.Equal("Install next?", featuredPage.Question);
        Assert.Equal("Install", featuredPage.Title);
        Assert.Equal("/docs/guides/install", featuredPage.Href);

        var sectionGroup = Assert.Single(model.SectionGroups);
        Assert.Equal(
            ["Install", "Configure"],
            sectionGroup.Links.Select(link => link.Title).ToArray());
        Assert.DoesNotContain(sectionGroup.Links, link => link.Title == "Start Here");
    }

    [Fact]
    public async Task Details_ShouldLeaveSectionLandingGroupsEmpty_WhenNoFeaturedOrRemainingPagesResolve()
    {
        var docs = new List<DocNode>
        {
            new(
                "Start Here",
                "start-here/README.md",
                "<p>Start here</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    SectionLanding = true,
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Path = "guides/missing.md"
                            })
                    ]
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Details("start-here");

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocDetailsViewModel>(viewResult.Model);
        Assert.True(model.IsSectionLanding);
        Assert.Equal(DocPublicSection.StartHere, model.PublicSection);
        Assert.Empty(model.FeaturedPageGroups);
        Assert.Empty(model.SectionGroups);
        AssertWarningLogged("destination page could not be resolved");
    }

    [Fact]
    public async Task Details_ShouldKeepRemainingSectionPages_WhenNoFeaturedPagesResolve()
    {
        var docs = new List<DocNode>
        {
            new(
                "Start Here",
                "start-here/README.md",
                "<p>Start here</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    SectionLanding = true,
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Path = "guides/missing.md"
                            })
                    ]
                }),
            new(
                "Install",
                "guides/install.md",
                "<p>Install</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    Order = 10
                }),
            new(
                "Configure",
                "guides/configure.md",
                "<p>Configure</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    Order = 20
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Details("start-here");

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocDetailsViewModel>(viewResult.Model);
        Assert.True(model.IsSectionLanding);
        Assert.Equal(DocPublicSection.StartHere, model.PublicSection);
        Assert.Empty(model.FeaturedPageGroups);
        var sectionGroup = Assert.Single(model.SectionGroups);
        Assert.Equal(["Install", "Configure"], sectionGroup.Links.Select(link => link.Title).ToArray());
        Assert.DoesNotContain(sectionGroup.Links, link => link.Title == "Start Here");
        AssertWarningLogged("destination page could not be resolved");
    }

    [Fact]
    public async Task Details_ShouldLinkReleaseBreadcrumbParentToSectionRoute_WhenParsedParentDocRouteIsSynthetic()
    {
        var docs = new List<DocNode>
        {
            new(
                "Releases",
                "releases/README.md",
                "<p>Release index</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Releases",
                    Breadcrumbs = ["Releases"],
                    BreadcrumbsMatchPathTargets = true,
                    SectionLanding = true
                }),
            new(
                "Unreleased",
                "releases/unreleased.md",
                "<p>Current release notes</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Releases",
                    Breadcrumbs = ["release-notes", "Unreleased"],
                    BreadcrumbsMatchPathTargets = true
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Details("releases/unreleased");

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocDetailsViewModel>(viewResult.Model);
        Assert.Equal(DocPublicSection.Releases, model.PublicSection);
        Assert.Equal(
            ["release-notes", "Unreleased"],
            model.Breadcrumbs.Select(breadcrumb => breadcrumb.Label).ToArray());
        Assert.Equal("/docs/sections/releases", model.Breadcrumbs[0].Href);
        Assert.DoesNotContain(model.Breadcrumbs, breadcrumb => breadcrumb.Href == "/docs/releases.html");
        AssertNoWarningsLogged();
    }

    [Fact]
    public async Task Details_ShouldPreserveConfiguredDocsRoot_WhenResolvedBreadcrumbTargetIsPublishedDoc()
    {
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                DocsRootPath = "/docs/next"
            }
        };
        var harvester = A.Fake<IDocHarvester>();
        var docs = new List<DocNode>
        {
            new(
                "Guides",
                "guides",
                "<p>Guides landing</p>"),
            new(
                "Start",
                "guides/start.md",
                "<p>Start here</p>",
                Metadata: new DocMetadata
                {
                    Breadcrumbs = ["guides", "Start"],
                    BreadcrumbsMatchPathTargets = true
                })
        };
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);
        var (controller, cache, memo) = CreateController(options, harvester);
        using (memo)
        using (cache)
        {
            var result = await controller.Details("guides/start");

            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<DocDetailsViewModel>(viewResult.Model);
            Assert.Equal(["guides", "Start"], model.Breadcrumbs.Select(breadcrumb => breadcrumb.Label).ToArray());
            Assert.Equal("/docs/next/guides.html", model.Breadcrumbs[0].Href);
        }
    }

    [Fact]
    public async Task Details_ShouldSuppressSyntheticParentDocBreadcrumbHrefs_WhenNoPublishedDocMatches()
    {
        var docs = new List<DocNode>
        {
            new(
                "Web",
                "Web/ForgeTrust.AppSurface.Web/README.md",
                "<p>Web package docs</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "How-to Guides",
                    Breadcrumbs = ["Web", "ForgeTrust.AppSurface.Web"],
                    BreadcrumbsMatchPathTargets = true
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Details("web/forgetrust.appsurface.web");

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocDetailsViewModel>(viewResult.Model);
        Assert.Equal(
            ["Web", "ForgeTrust.AppSurface.Web"],
            model.Breadcrumbs.Select(breadcrumb => breadcrumb.Label).ToArray());
        Assert.Null(model.Breadcrumbs[0].Href);
        Assert.DoesNotContain(model.Breadcrumbs, breadcrumb => breadcrumb.Href == "/docs/Web.html");
        AssertNoWarningsLogged();
    }

    [Fact]
    public async Task Details_ShouldReturnTurboFramePartial_WhenPartialSuffixRequested()
    {
        var docs = new List<DocNode> { new("Title", "target-path", "content") };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Details("target-path.partial.html");

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("RazorWire/_TurboFrame", partial.ViewName);
        var frame = Assert.IsType<TurboFrameViewModel>(partial.Model);
        Assert.Equal("DetailsFrame", frame.PartialView);
        Assert.Equal("doc-content", frame.Id);
    }

    [Fact]
    public async Task Details_ShouldReturnTurboFramePartial_WhenTrailingSlashPartialPathRequested()
    {
        var docs = new List<DocNode> { new("Title", "target-path", "content") };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Details("target-path.html/index.partial.html");

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("RazorWire/_TurboFrame", partial.ViewName);
        var frame = Assert.IsType<TurboFrameViewModel>(partial.Model);
        Assert.Equal("DetailsFrame", frame.PartialView);
        Assert.Equal("doc-content", frame.Id);
    }

    [Fact]
    public async Task Details_ShouldReturnTurboFramePartial_WhenAliasPartialPathRequested()
    {
        var docs = new List<DocNode>
        {
            new(
                "Legacy",
                "legacy-path.md",
                "content",
                Metadata: new DocMetadata
                {
                    RedirectAliases = ["old-path"]
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Details("old-path.partial.html");

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("RazorWire/_TurboFrame", partial.ViewName);
        var frame = Assert.IsType<TurboFrameViewModel>(partial.Model);
        Assert.Equal("DetailsFrame", frame.PartialView);
        Assert.Equal("doc-content", frame.Id);
    }

    [Fact]
    public async Task Details_ShouldReturnNotFound_WhenDocDoesNotExist()
    {
        // Arrange
        var docs = new List<DocNode> { new DocNode("Title", "other-path", "content") };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        // Act
        var result = await _controller.Details("missing-path");

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Details_ShouldReturnView_WhenDocRequestedByCanonicalPath()
    {
        // Arrange
        var docs = new List<DocNode> { new("Title", "target-path.md", "content") };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        // Act
        var result = await _controller.Details("target-path");

        // Assert
        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocDetailsViewModel>(viewResult.Model);
        Assert.Equal("Title", model.Title);
        Assert.Equal("Title", model.Document.Title);
    }

    [Fact]
    public async Task Details_ShouldRedirectDeclaredSourceAlias_ToCanonicalPath()
    {
        // Arrange
        var docs = new List<DocNode>
        {
            new(
                "Legacy",
                "legacy-path.md",
                "content",
                Metadata: new DocMetadata
                {
                    RedirectAliases = ["legacy-path.md"]
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        // Act
        var result = await _controller.Details("legacy-path.md");

        // Assert
        var redirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.True(redirect.Permanent);
        Assert.Equal("/docs/legacy-path", redirect.Url);
    }

    [Fact]
    public async Task Details_ShouldRedirectSourceShapedMarkdownPath_ToCanonicalPath()
    {
        // Arrange
        var docs = new List<DocNode> { new("Legacy", "legacy-path.md", "content") };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        // Act
        var result = await _controller.Details("legacy-path.md");

        // Assert
        var redirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.True(redirect.Permanent);
        Assert.Equal("/docs/legacy-path", redirect.Url);
    }

    [Fact]
    public async Task Details_ShouldRedirectSourceShapedReadmePath_ToCollapsedCanonicalPath()
    {
        var docs = new List<DocNode> { new("Package Chooser", "packages/README.md", "content") };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.PathBase = new PathString("/some-base");
        httpContext.Request.QueryString = new QueryString("?from=github");
        _controller.ControllerContext = CreateControllerContext(httpContext);
        _controller.Url = new UrlHelper(_controller.ControllerContext);

        var result = await _controller.Details("packages/README.md");

        var redirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.True(redirect.Permanent);
        Assert.Equal("/some-base/docs/packages?from=github", redirect.Url);
    }

    [Fact]
    public async Task Details_ShouldReturnNotFound_WhenNonMarkdownSourcePathRequested()
    {
        var docs = new List<DocNode> { new("Namespace", "Namespaces/Foo", "content") };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Details("Namespaces/Foo");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Details_ShouldRedirectSourceShapedFragmentPath_WithQueryBeforeFragment()
    {
        var docs = new List<DocNode> { new("Setup", "guides/intro.md#setup", "content") };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?from=github");
        _controller.ControllerContext = CreateControllerContext(httpContext);
        _controller.Url = new UrlHelper(_controller.ControllerContext);

        var result = await _controller.Details("guides/intro.md");

        var redirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.True(redirect.Permanent);
        Assert.Equal("/docs/guides/intro?from=github#setup", redirect.Url);
    }

    [Fact]
    public async Task Details_ShouldRedirectDeclaredAlias_ToCanonicalPath()
    {
        var docs = new List<DocNode>
        {
            new(
                "Legacy",
                "legacy-path.md",
                "content",
                Metadata: new DocMetadata
                {
                    RedirectAliases = ["legacy-path.md.html"]
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?utm=dogfood");
        _controller.ControllerContext = CreateControllerContext(httpContext);
        _controller.Url = new UrlHelper(_controller.ControllerContext);

        var result = await _controller.Details("legacy-path.md.html");

        var redirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.True(redirect.Permanent);
        Assert.Equal("/docs/legacy-path?utm=dogfood", redirect.Url);
    }

    [Fact]
    public async Task Details_ShouldReturnView_WhenDocRequestedByBackslashSeparatedPath()
    {
        var docs = new List<DocNode> { new("Guide", "guides/intro.md", "content") };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Details("guides\\intro");

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocDetailsViewModel>(viewResult.Model);
        Assert.Equal("Guide", model.Title);
        Assert.Equal("Guide", model.Document.Title);
    }

    [Fact]
    public async Task Details_ShouldSuppressDerivedSummary()
    {
        var docs = new List<DocNode>
        {
            new(
                "Guide",
                "guides/intro.md",
                "content",
                Metadata: new DocMetadata
                {
                    Summary = "Derived from the first paragraph.",
                    SummaryIsDerived = true
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Details("guides/intro");

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocDetailsViewModel>(viewResult.Model);
        Assert.Equal("Derived from the first paragraph.", model.Summary);
        Assert.False(model.ShowSummary);
    }

    [Fact]
    public async Task Details_ShouldHonorMetadataBreadcrumbLabels_AndSuppressSyntheticParentDocHref()
    {
        var docs = new List<DocNode>
        {
            new(
                "Quickstart",
                "guides/quickstart.md",
                "content",
                Metadata: DocMetadataFactory.CreateMarkdownMetadata(
                    "guides/quickstart.md",
                    "Quickstart",
                    new DocMetadata
                    {
                        NavGroup = "How-to Guides",
                        Breadcrumbs = ["Get Started", "Quickstart"]
                    },
                    derivedSummary: null))
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Details("guides/quickstart");

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocDetailsViewModel>(viewResult.Model);
        Assert.Equal(["Get Started", "Quickstart"], model.Breadcrumbs.Select(crumb => crumb.Label).ToArray());
        Assert.Null(model.Breadcrumbs[0].Href);
        Assert.Null(model.Breadcrumbs[1].Href);
    }

    [Fact]
    public async Task Details_ShouldReturnNotFound_WhenPathIsWhitespace()
    {
        var result = await _controller.Details("   ");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenAggregatorIsNull()
    {
        var logger = A.Fake<ILogger<DocsController>>();
        var resolver = new DocFeaturedPageResolver(A.Fake<ILogger<DocFeaturedPageResolver>>());
        Assert.Throws<ArgumentNullException>(() => new DocsController(null!, resolver, logger));
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenFeaturedPageResolverIsNull()
    {
        var logger = A.Fake<ILogger<DocsController>>();
        Assert.Throws<ArgumentNullException>(() => new DocsController(_aggregator, null!, logger));
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenLoggerIsNull()
    {
        var resolver = new DocFeaturedPageResolver(A.Fake<ILogger<DocFeaturedPageResolver>>());
        Assert.Throws<ArgumentNullException>(() => new DocsController(_aggregator, resolver, null!));
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenDocsUrlBuilderIsNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DocsController(
                _aggregator,
                null!,
                CreateDefaultVersionCatalogService(new AppSurfaceDocsOptions()),
                _controllerLoggerFake));
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenVersionCatalogServiceIsNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DocsController(
                _aggregator,
                new DocsUrlBuilder(new AppSurfaceDocsOptions()),
                null!,
                _controllerLoggerFake));
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenOptionsIsNull()
    {
        var docsUrlBuilder = new DocsUrlBuilder(new AppSurfaceDocsOptions());

        Assert.Throws<ArgumentNullException>(
            () => new DocsController(
                _aggregator,
                docsUrlBuilder,
                CreateDefaultVersionCatalogService(new AppSurfaceDocsOptions()),
                new DocFeaturedPageResolver(_featuredPageResolverLoggerFake, docsUrlBuilder),
                null!,
                A.Fake<IWebHostEnvironment>(),
                _controllerLoggerFake));
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenEnvironmentIsNull()
    {
        var options = new AppSurfaceDocsOptions();
        var docsUrlBuilder = new DocsUrlBuilder(options);

        Assert.Throws<ArgumentNullException>(
            () => new DocsController(
                _aggregator,
                docsUrlBuilder,
                CreateDefaultVersionCatalogService(options),
                new DocFeaturedPageResolver(_featuredPageResolverLoggerFake, docsUrlBuilder),
                options,
                null!,
                _controllerLoggerFake));
    }

    [Fact]
    public void VersionEntry_ShouldRedirectToStableDocsHome_WhenUsingAggregatorOnlyConstructor()
    {
        var controller = new DocsController(_aggregator, _controllerLoggerFake)
        {
            ControllerContext = CreateControllerContext(new DefaultHttpContext())
        };
        controller.Url = new UrlHelper(controller.ControllerContext);

        var result = controller.VersionEntry();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/docs", redirect.Url);
    }

    [Fact]
    public async Task Details_ShouldReturnNotFound_WhenPartialSuffixResolvesToWhitespacePath()
    {
        var result = await _controller.Details(".partial.html");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Search_ShouldReturnViewModelWithFallbackLinks()
    {
        var docs = new List<DocNode>
        {
            new(
                "Guide",
                "guides/start.md",
                "<p>Guide body</p>",
                Metadata: new DocMetadata
                {
                    PageType = "guide",
                    Order = 1
                }),
            new(
                "Example",
                "examples/hello",
                "<p>Example body</p>",
                Metadata: new DocMetadata
                {
                    PageType = "example",
                    Order = 2
                }),
            new(
                "API",
                "Namespaces/ForgeTrust.AppSurface.Web",
                "<p>API body</p>",
                Metadata: new DocMetadata
                {
                    PageType = "api-reference",
                    Order = 3
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Search();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SearchPageViewModel>(viewResult.Model);
        Assert.Equal("Search Documentation", model.Title);
        Assert.Equal(4, model.FailureFallbackLinks.Count);
        Assert.Contains(model.FailureFallbackLinks, link => link.Title == "Start Here" && link.Href == "/docs/guides/start" && link.UsesDocsFrame);
        Assert.Contains(model.FailureFallbackLinks, link => link.Title == "Examples" && link.UsesDocsFrame);
        Assert.Contains(model.FailureFallbackLinks, link => link.Title == "API Reference" && link.UsesDocsFrame);
        Assert.Contains(model.FailureFallbackLinks, link => link.Title == "Documentation index" && link.Href == "/docs" && !link.UsesDocsFrame);
    }

    [Fact]
    public async Task Search_ShouldBuildOrderedServerRenderedRecoveryBuckets()
    {
        var docs = new List<DocNode>
        {
            new(
                "Start Landing",
                "start/index.md",
                "<p>Start body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    SectionLanding = true,
                    Order = 1
                }),
            new(
                "Example",
                "examples/hello.md",
                "<p>Example body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Examples",
                    Order = 2
                }),
            new(
                "Packages",
                "packages/README.md",
                "<p>Package body</p>",
                Metadata: new DocMetadata
                {
                    Order = 3
                }),
            new(
                "Troubleshooting",
                "troubleshooting/search.md",
                "<p>Troubleshooting body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Troubleshooting",
                    Order = 4
                }),
            new(
                "API",
                "Namespaces/ForgeTrust.AppSurface.Web",
                "<p>API body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "API Reference",
                    Order = 5
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Search();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SearchPageViewModel>(viewResult.Model);
        Assert.Collection(
            model.FailureFallbackLinks,
            link =>
            {
                Assert.Equal("Start Here", link.Title);
                Assert.Equal("/docs/sections/start-here", link.Href);
            },
            link =>
            {
                Assert.Equal("Examples", link.Title);
                Assert.Equal("/docs/sections/examples", link.Href);
            },
            link =>
            {
                Assert.Equal("Packages", link.Title);
                Assert.Equal("/docs/packages", link.Href);
            },
            link =>
            {
                Assert.Equal("Troubleshooting", link.Title);
                Assert.Equal("/docs/sections/troubleshooting", link.Href);
            },
            link =>
            {
                Assert.Equal("API Reference", link.Title);
                Assert.Equal("/docs/sections/api-reference", link.Href);
            });
        Assert.All(model.FailureFallbackLinks, link => Assert.True(link.UsesDocsFrame));
    }

    [Fact]
    public async Task Search_ShouldAddDocsHomeFallback_WhenSomeRecoveryBucketsAreMissing()
    {
        var docs = new List<DocNode>
        {
            new(
                "Start Landing",
                "start/index.md",
                "<p>Start body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    SectionLanding = true,
                    Order = 1
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Search();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SearchPageViewModel>(viewResult.Model);
        Assert.Collection(
            model.FailureFallbackLinks,
            link =>
            {
                Assert.Equal("Start Here", link.Title);
                Assert.Equal("/docs/sections/start-here", link.Href);
                Assert.True(link.UsesDocsFrame);
            },
            link =>
            {
                Assert.Equal("Documentation index", link.Title);
                Assert.Equal("/docs", link.Href);
                Assert.False(link.UsesDocsFrame);
            });
    }

    [Fact]
    public async Task Search_ShouldOrderRepresentativeRecoveryDocsByLandingThenOrder()
    {
        var docs = new List<DocNode>
        {
            new(
                "No Order Guide",
                "guides/no-order.md",
                "<p>No order body</p>",
                Metadata: new DocMetadata
                {
                    PageType = "guide"
                }),
            new(
                "Ordered Guide",
                "guides/ordered.md",
                "<p>Ordered body</p>",
                Metadata: new DocMetadata
                {
                    PageType = "guide",
                    Order = 2
                }),
            new(
                "Landing Concept",
                "concepts/landing.md",
                "<p>Landing body</p>",
                Metadata: new DocMetadata
                {
                    PageType = "concept",
                    SectionLanding = true,
                    Order = 50
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Search();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SearchPageViewModel>(viewResult.Model);
        var startLink = Assert.Single(model.FailureFallbackLinks, link => link.Title == "Start Here");

        Assert.Equal("/docs/concepts/landing", startLink.Href);
        Assert.True(startLink.UsesDocsFrame);
    }

    [Fact]
    public async Task Search_ShouldUseTroubleshootingHeuristics_WhenSectionRouteIsUnavailable()
    {
        var docs = new List<DocNode>
        {
            new(
                "Troubleshooting Runbook",
                "operations/runbook.md",
                "<p>Runbook body</p>",
                Metadata: new DocMetadata
                {
                    PageType = "troubleshooting",
                    Order = 2
                }),
            new(
                "Troubleshoot Error",
                "operations/troubleshoot-error.md",
                "<p>Error body</p>",
                Metadata: new DocMetadata
                {
                    PageType = "reference",
                    Order = 1
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Search();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SearchPageViewModel>(viewResult.Model);
        var troubleshootingLink = Assert.Single(model.FailureFallbackLinks, link => link.Title == "Troubleshooting");

        Assert.Equal("/docs/operations/troubleshoot-error", troubleshootingLink.Href);
        Assert.True(troubleshootingLink.UsesDocsFrame);
    }

    [Fact]
    public async Task Search_ShouldStillRenderShell_WhenDocAggregationFails()
    {
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
                [
                    new(
                        "Home",
                        "README.md",
                        "<p>Home</p>")
                ]);
        A.CallTo(() => _sanitizerFake.Sanitize(A<string>._)).Throws(new InvalidOperationException("boom"));

        var result = await _controller.Search();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SearchPageViewModel>(viewResult.Model);
        Assert.Equal("Search Documentation", model.Title);
        Assert.Contains(model.FailureFallbackLinks, link => link.Href == "/docs" && !link.UsesDocsFrame);
        AssertWarningLogged("fallback link generation failed");
    }

    [Fact]
    public async Task Search_ShouldStillRenderShell_WhenDocAggregationTimesOut()
    {
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (string _, CancellationToken cancellationToken) =>
                {
                    // Block until the controller's fallback budget cancels the request so this test
                    // exercises the timeout path deterministically on fast and slow runners.
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return (IReadOnlyList<DocNode>)Array.Empty<DocNode>();
                });

        var result = await _controller.Search();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SearchPageViewModel>(viewResult.Model);
        Assert.Equal("Search Documentation", model.Title);
        Assert.Contains(model.FailureFallbackLinks, link => link.Href == "/docs" && !link.UsesDocsFrame);
        AssertWarningLogged();
    }

    [Fact]
    public async Task Search_ShouldRenderHarvesting_WhenInitialHarvestIsStillPending()
    {
        await using var pending = CreatePendingHarvestController("/docs/search");

        var result = await pending.Controller.Search();

        AssertHarvestingView(result, "/docs/search");
    }

    [Fact]
    public async Task Search_ShouldUseDocsHomeReturnUrl_WhenCurrentRequestIsNotSafeAppRelative()
    {
        await using var pending = CreatePendingHarvestController("//evil.example/docs/search");

        var result = await pending.Controller.Search();

        AssertHarvestingView(result, "/docs");
    }

    [Fact]
    public async Task Section_ShouldRenderHarvesting_WhenInitialHarvestIsStillPending()
    {
        await using var pending = CreatePendingHarvestController("/docs/section/guides");

        var result = await pending.Controller.Section("guides");

        AssertHarvestingView(result, "/docs/section/guides");
    }

    [Fact]
    public async Task Details_ShouldRenderHarvesting_WhenInitialHarvestIsStillPending()
    {
        await using var pending = CreatePendingHarvestController("/docs/guides/composition");

        var result = await pending.Controller.Details("guides/composition");

        AssertHarvestingView(result, "/docs/guides/composition");
    }

    [Fact]
    public async Task Details_ShouldRenderHarvestingForMarkdownRoute_WhenInitialHarvestIsStillPending()
    {
        await using var pending = CreatePendingHarvestController("/docs/guides/composition.md");

        var result = await pending.Controller.Details("guides/composition.md");

        AssertHarvestingView(result, "/docs/guides/composition.md");
    }

    [Fact]
    public async Task Search_ShouldSkipHiddenNamespacesFallback_WhenBuildingRecoveryLinks()
    {
        var docs = new List<DocNode>
        {
            new(
                "Namespaces",
                "Namespaces",
                "<p>Namespace index</p>",
                Metadata: new DocMetadata
                {
                    HideFromPublicNav = true
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Search();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SearchPageViewModel>(viewResult.Model);
        Assert.DoesNotContain(model.FailureFallbackLinks, link => link.Title == "API Reference" && link.Href.Contains("Namespaces", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(model.FailureFallbackLinks, link => link.Href == "/docs" && !link.UsesDocsFrame);
    }

    [Fact]
    public async Task Search_ShouldSkipHiddenFromSearchFallback_WhenBuildingRecoveryLinks()
    {
        var docs = new List<DocNode>
        {
            new(
                "Hidden guide",
                "guides/hidden-guide.md",
                "<p>Guide body</p>",
                Metadata: new DocMetadata
                {
                    PageType = "guide",
                    HideFromSearch = true
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Search();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SearchPageViewModel>(viewResult.Model);
        Assert.DoesNotContain(model.FailureFallbackLinks, link => link.Href == "/docs/guides/hidden-guide");
        Assert.DoesNotContain(model.FailureFallbackLinks, link => link.Href == "/docs/guides/hidden-guide.md");
        Assert.Contains(model.FailureFallbackLinks, link => link.Href == "/docs" && !link.UsesDocsFrame);
    }

    [Fact]
    public async Task Search_ShouldSkipDuplicateFallbackLinks_WhenOneDocMatchesMultipleBuckets()
    {
        var docs = new List<DocNode>
        {
            new(
                "Shared Example",
                "guides/shared-example.md",
                "<p>Shared body</p>",
                Metadata: new DocMetadata
                {
                    PageType = "example"
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Search();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SearchPageViewModel>(viewResult.Model);
        var sharedHref = DocAggregator.BuildSearchDocUrl("guides/shared-example");

        Assert.Equal(1, model.FailureFallbackLinks.Count(link => link.Href == sharedHref));
        Assert.Contains(model.FailureFallbackLinks, link => link.Title == "Start Here");
        Assert.DoesNotContain(model.FailureFallbackLinks, link => link.Title == "Examples");
    }

    [Fact]
    public async Task Search_ShouldUseNextFallbackDoc_WhenFirstMatchHasNoPublicRoute()
    {
        var docs = new List<DocNode>
        {
            new(
                "Winner",
                "guides/winner.md",
                "<p>Winner body</p>",
                Metadata: new DocMetadata
                {
                    CanonicalSlug = "guides/shared",
                    PageType = "guide",
                    Order = 2
                }),
            new(
                "Collision loser",
                "guides/loser.md",
                "<p>Loser body</p>",
                Metadata: new DocMetadata
                {
                    CanonicalSlug = "guides/shared",
                    PageType = "guide",
                    Order = 1
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Search();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SearchPageViewModel>(viewResult.Model);
        Assert.Contains(
            model.FailureFallbackLinks,
            link => link.Title == "Start Here" && link.Href == "/docs/guides/shared" && link.UsesDocsFrame);
    }

    [Fact]
    public async Task Search_ShouldMarkRootMountedPlainHtmlFallbackLinks_AsDocsFrameLinks()
    {
        var docs = new List<DocNode>
        {
            new(
                "HTML guide",
                "docs/page.html",
                "<p>Guide body</p>",
                Metadata: new DocMetadata
                {
                    PageType = "guide"
                })
        };
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);
        var (controller, cache, memo) = CreateController(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    DocsRootPath = "/"
                }
            },
            harvester);

        using (memo)
        using (cache)
        {
            var result = await controller.Search();

            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<SearchPageViewModel>(viewResult.Model);
            Assert.Contains(model.FailureFallbackLinks, link => link.Href == "/docs/page.html" && link.UsesDocsFrame);
        }
    }

    [Theory]
    [InlineData("/guides.html")]
    [InlineData("/guides.html?view=compact")]
    [InlineData("/search?q=foo")]
    public async Task Details_ShouldMarkRootMountedTrustMigrationLinks_AsDocsFrameLinks(string migrationHref)
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "home.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    Trust = new DocTrustMetadata
                    {
                        Migration = new DocTrustLink
                        {
                            Label = "Open guide index",
                            Href = migrationHref
                        }
                    }
                }),
            new(
                "Guides",
                "guides",
                "<p>Guides</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "How-to Guides"
                })
        };
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);
        var (controller, cache, memo) = CreateController(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    DocsRootPath = "/"
                }
            },
            harvester);

        using (memo)
        using (cache)
        {
            var result = await controller.Details("home");

            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<DocDetailsViewModel>(viewResult.Model);
            Assert.True(model.TrustMigrationUsesTurbo);
        }
    }

    [Fact]
    public async Task Details_ShouldNotMarkDocsHomeMigrationLinks_AsDocsFrameLinks_WhenHrefHasQueryAndFragment()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "home.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    Trust = new DocTrustMetadata
                    {
                        Migration = new DocTrustLink
                        {
                            Label = "Open docs home",
                            Href = "/docs?tab=home#summary"
                        }
                    }
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.Details("home");

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DocDetailsViewModel>(viewResult.Model);
        Assert.False(model.TrustMigrationUsesTurbo);
    }

    [Fact]
    public async Task Details_ShouldNotMarkUnknownRootMountedContributorLinks_AsDocsFrameLinks()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "home.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    Contributor = new DocContributorMetadata
                    {
                        SourceUrlOverride = "/missing.html"
                    }
                })
        };
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);
        var (controller, cache, memo) = CreateController(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    DocsRootPath = "/"
                }
            },
            harvester);

        using (memo)
        using (cache)
        {
            var result = await controller.Details("home");

            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<DocDetailsViewModel>(viewResult.Model);
            Assert.False(model.ContributorSourceUsesTurbo);
        }
    }

    [Fact]
    public async Task Details_ShouldMarkRootMountedContributorLinks_AsDocsFrameLinks_WhenCanonicalLookupFallsBackToFragments()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "home.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    Contributor = new DocContributorMetadata
                    {
                        SourceUrlOverride = "/guides.html?view=compact"
                    }
                }),
            new(
                "Guides",
                "guides#summary",
                "<p>Guides summary</p>",
                CanonicalPath: "guides.html#"),
            new(
                "Guides section",
                "guides#details",
                "<p>Guides details</p>",
                CanonicalPath: "guides.html#details")
        };
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);
        var (controller, cache, memo) = CreateController(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    DocsRootPath = "/"
                }
            },
            harvester);

        using (memo)
        using (cache)
        {
            var result = await controller.Details("home");

            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<DocDetailsViewModel>(viewResult.Model);
            Assert.True(model.ContributorSourceUsesTurbo);
        }
    }

    [Fact]
    public async Task SearchIndex_ShouldReturnJsonPayload_WithNormalizedPageTypeBadgeFields()
    {
        var docs = new List<DocNode>
        {
            new(
                "Getting Started",
                "guides/start",
                "<h2>Install</h2><p>First steps.</p>",
                Metadata: new DocMetadata
                {
                    Summary = "Get started quickly.",
                    PageType = "guide",
                    Audience = "developer",
                    Component = "AppSurface",
                    Aliases = ["quickstart"],
                    Keywords = ["install"],
                    Status = "stable",
                    NavGroup = "Start Here",
                    Order = 7,
                    SequenceKey = "getting-started",
                    RelatedPages = ["examples/hello-world", "Namespaces/ForgeTrust.AppSurface"],
                    Breadcrumbs = ["Guides", "Getting Started"]
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.SearchIndex();
        var json = Assert.IsType<JsonResult>(result);

        var payload = JsonSerializer.Serialize(json.Value);
        using var doc = JsonDocument.Parse(payload);
        var documents = doc.RootElement.GetProperty("documents");
        var document = Assert.Single(documents.EnumerateArray());
        Assert.Equal("Get started quickly.", document.GetProperty("summary").GetString());
        Assert.Equal("guide", document.GetProperty("pageType").GetString());
        Assert.Equal("Guide", document.GetProperty("pageTypeLabel").GetString());
        Assert.Equal("guide", document.GetProperty("pageTypeVariant").GetString());
        Assert.Equal("developer", document.GetProperty("audience").GetString());
        Assert.Equal("AppSurface", document.GetProperty("component").GetString());
        Assert.Equal("stable", document.GetProperty("status").GetString());
        Assert.Equal("Start Here", document.GetProperty("navGroup").GetString());
        Assert.Equal(7, document.GetProperty("order").GetInt32());
        Assert.Equal("getting-started", document.GetProperty("sequenceKey").GetString());
        Assert.Equal("quickstart", document.GetProperty("aliases").EnumerateArray().Single().GetString());
        Assert.Equal("install", document.GetProperty("keywords").EnumerateArray().Single().GetString());
        Assert.Equal(
            ["examples/hello-world", "Namespaces/ForgeTrust.AppSurface"],
            document.GetProperty("relatedPages").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray());
        Assert.Equal(
            ["Guides", "Getting Started"],
            document.GetProperty("breadcrumbs").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task SearchIndex_ShouldMarkOnlyResolvedSectionLandingDoc_AsSectionLanding()
    {
        var docs = new List<DocNode>
        {
            new(
                "Alpha",
                "guides/alpha.md",
                "<p>Alpha</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    SectionLanding = true,
                    Order = 20
                }),
            new(
                "Beta",
                "guides/beta.md",
                "<p>Beta</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    SectionLanding = true,
                    Order = 10
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.SearchIndex();
        var json = Assert.IsType<JsonResult>(result);

        var payload = JsonSerializer.Serialize(json.Value);
        using var doc = JsonDocument.Parse(payload);
        var documents = doc.RootElement.GetProperty("documents")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("id").GetString() ?? string.Empty,
                item => item.GetProperty("isSectionLanding").GetBoolean(),
                StringComparer.OrdinalIgnoreCase);

        Assert.False(documents["guides/alpha"]);
        Assert.True(documents["guides/beta"]);
    }

    [Fact]
    public async Task SearchIndex_ShouldSuppressDerivedAudienceAndComponentFields()
    {
        var docs = new List<DocNode>
        {
            new(
                "Getting Started",
                "guides/start",
                "<h2>Install</h2><p>First steps.</p>",
                Metadata: new DocMetadata
                {
                    Summary = "Get started quickly.",
                    PageType = "guide",
                    Component = "AppSurface",
                    ComponentIsDerived = true,
                    Audience = "implementer",
                    AudienceIsDerived = true
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = await _controller.SearchIndex();
        var json = Assert.IsType<JsonResult>(result);

        var payload = JsonSerializer.Serialize(json.Value);
        using var doc = JsonDocument.Parse(payload);
        var document = Assert.Single(doc.RootElement.GetProperty("documents").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, document.GetProperty("component").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.GetProperty("audience").ValueKind);
        Assert.Equal("Guide", document.GetProperty("pageTypeLabel").GetString());
        Assert.Equal("guide", document.GetProperty("pageTypeVariant").GetString());
    }

    [Fact]
    public async Task SearchIndex_ShouldReuseCachedPayload()
    {
        var docs = new List<DocNode>
        {
            new("Getting Started", "guides/start", "<h2>Install</h2><p>First steps.</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var first = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var second = Assert.IsType<JsonResult>(await _controller.SearchIndex());

        var firstPayload = JsonSerializer.Serialize(first.Value);
        var secondPayload = JsonSerializer.Serialize(second.Value);

        using var firstDoc = JsonDocument.Parse(firstPayload);
        using var secondDoc = JsonDocument.Parse(secondPayload);

        var firstGenerated = firstDoc.RootElement.GetProperty("metadata").GetProperty("generatedAtUtc").GetString();
        var secondGenerated = secondDoc.RootElement.GetProperty("metadata").GetProperty("generatedAtUtc").GetString();

        Assert.Equal(firstGenerated, secondGenerated);
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SearchIndex_ShouldSetCacheControlHeader()
    {
        var docs = new List<DocNode>
        {
            new("Getting Started", "guides/start", "<p>First steps.</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        _ = await _controller.SearchIndex();

        Assert.Equal("private,max-age=300", _controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task SearchIndex_ShouldUseConfiguredSnapshotCacheExpirationForCacheControlHeader()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Getting Started", "guides/start", "<p>First steps.</p>")]);

        var env = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => env.ContentRootPath).Returns(Path.GetTempPath());
        var sanitizer = A.Fake<IAppSurfaceDocsHtmlSanitizer>();
        A.CallTo(() => sanitizer.Sanitize(A<string>._))
            .ReturnsLazily((string input) => input);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var memo = new Memo(cache);
        var aggregator = new DocAggregator(
            [harvester],
            new AppSurfaceDocsOptions
            {
                CacheExpirationMinutes = 0.5,
                Source = new AppSurfaceDocsSourceOptions
                {
                    RepositoryRoot = Path.GetTempPath()
                }
            },
            env,
            memo,
            sanitizer,
            A.Fake<ILogger<DocAggregator>>());
        var controller = new DocsController(
            aggregator,
            new DocFeaturedPageResolver(_featuredPageResolverLoggerFake),
            _controllerLoggerFake)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        _ = await controller.SearchIndex();

        Assert.Equal("private,max-age=30", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task HarvestHealthJson_ShouldReturnOkTrueAndNoStore_WhenHarvestIsHealthy()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Getting Started", "guides/start", "<p>First steps.</p>")]);
        var (controller, cache, memo) = CreateController(new AppSurfaceDocsOptions(), harvester);
        using (memo)
        using (cache)
        {
            var result = Assert.IsType<JsonResult>(await controller.HarvestHealthJson());
            var response = Assert.IsType<AppSurfaceDocsHarvestHealthResponse>(result.Value);
            var serialized = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var customSerialized = JsonSerializer.Serialize(
                response,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = new PrefixJsonNamingPolicy()
                });
            using var document = JsonDocument.Parse(serialized);
            using var customDocument = JsonDocument.Parse(customSerialized);

            Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
            Assert.True(response.Verification.Ok);
            Assert.Equal("Healthy", response.Status);
            Assert.Equal("no-store, no-cache", controller.Response.Headers.CacheControl.ToString());
            Assert.True(document.RootElement.TryGetProperty("status", out var status));
            Assert.Equal("Healthy", status.GetString());
            Assert.False(document.RootElement.TryGetProperty("Status", out _));
            Assert.True(document.RootElement.GetProperty("verification").GetProperty("ok").GetBoolean());
            Assert.Equal(
                StatusCodes.Status200OK,
                document.RootElement.GetProperty("verification").GetProperty("httpStatusCode").GetInt32());
            Assert.True(customDocument.RootElement.TryGetProperty("status", out _));
            Assert.False(customDocument.RootElement.TryGetProperty("x_Status", out _));
            Assert.True(customDocument.RootElement.GetProperty("verification").TryGetProperty("httpStatusCode", out _));
        }
    }

    [Fact]
    public async Task HarvestHealthJson_ShouldTreatEmptyHarvestAsOk()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([]);
        var (controller, cache, memo) = CreateController(new AppSurfaceDocsOptions(), harvester);
        using (memo)
        using (cache)
        {
            var result = Assert.IsType<JsonResult>(await controller.HarvestHealthJson());
            var response = Assert.IsType<AppSurfaceDocsHarvestHealthResponse>(result.Value);

            Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
            Assert.True(response.Verification.Ok);
            Assert.Equal("Empty", response.Status);
        }
    }

    [Fact]
    public async Task HarvestHealthJson_ShouldReturnServiceUnavailableAndRedactedResponse_WhenHarvestFails()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("boom at /tmp/secret/root"));
        var options = new AppSurfaceDocsOptions
        {
            Source = new AppSurfaceDocsSourceOptions
            {
                RepositoryRoot = "/tmp/secret/root"
            }
        };
        var (controller, cache, memo) = CreateController(options, harvester);
        using (memo)
        using (cache)
        {
            var result = Assert.IsType<JsonResult>(await controller.HarvestHealthJson());
            var response = Assert.IsType<AppSurfaceDocsHarvestHealthResponse>(result.Value);
            var serialized = JsonSerializer.Serialize(response);

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
            Assert.False(response.Verification.Ok);
            Assert.Equal("Failed", response.Status);
            Assert.DoesNotContain("RepositoryRoot", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/tmp/secret/root", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Cause", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("boom", serialized, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task HarvestHealthJson_ShouldReturnServiceUnavailable_WhenHarvestIsDegraded()
    {
        var failingHarvester = A.Fake<IDocHarvester>();
        var workingHarvester = A.Fake<IDocHarvester>();
        A.CallTo(() => failingHarvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("boom"));
        A.CallTo(() => workingHarvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Recovered", "recovered.md", "<p>Recovered</p>")]);
        var (controller, cache, memo) = CreateController(new AppSurfaceDocsOptions(), [failingHarvester, workingHarvester]);
        using (memo)
        using (cache)
        {
            var result = Assert.IsType<JsonResult>(await controller.HarvestHealthJson());
            var response = Assert.IsType<AppSurfaceDocsHarvestHealthResponse>(result.Value);

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
            Assert.False(response.Verification.Ok);
            Assert.Equal("Degraded", response.Status);
            Assert.Equal(1, response.SuccessfulHarvesters);
            Assert.Equal(1, response.FailedHarvesters);
        }
    }

    [Fact]
    public async Task HarvestHealth_ShouldRenderViewWithServiceUnavailableStatus_WhenHarvestFails()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("boom"));
        var (controller, cache, memo) = CreateController(new AppSurfaceDocsOptions(), harvester);
        using (memo)
        using (cache)
        {
            var result = Assert.IsType<ViewResult>(await controller.HarvestHealth());
            var model = Assert.IsType<AppSurfaceDocsHarvestHealthResponse>(result.Model);

            Assert.Equal("HarvestHealth", result.ViewName);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
            Assert.False(model.Verification.Ok);
        }
    }

    [Fact]
    public async Task HarvestHealth_ShouldReturnNotFound_WhenRoutesAreNotExposedForEnvironment()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Getting Started", "guides/start", "<p>First steps.</p>")]);
        var options = new AppSurfaceDocsOptions();
        var (controller, cache, memo) = CreateController(options, harvester, Environments.Production);
        using (memo)
        using (cache)
        {
            var result = await controller.HarvestHealth();

            Assert.IsType<NotFoundResult>(result);
        }
    }

    [Fact]
    public async Task HarvestHealthJson_ShouldReturnNotFound_WhenRoutesAreNotExposedForEnvironment()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Getting Started", "guides/start", "<p>First steps.</p>")]);
        var options = new AppSurfaceDocsOptions();
        var (controller, cache, memo) = CreateController(options, harvester, Environments.Production);
        using (memo)
        using (cache)
        {
            var result = await controller.HarvestHealthJson();

            Assert.IsType<NotFoundResult>(result);
        }
    }

    [Fact]
    public async Task HarvestHealthActions_ShouldReturnNotFound_InDevelopmentWhenExplicitlyDisabled()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Getting Started", "guides/start", "<p>First steps.</p>")]);
        var options = new AppSurfaceDocsOptions
        {
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                Health = new AppSurfaceDocsHarvestHealthOptions
                {
                    ExposeRoutes = AppSurfaceDocsHarvestHealthExposure.Never
                }
            }
        };
        var (controller, cache, memo) = CreateController(options, harvester, Environments.Development);
        using (memo)
        using (cache)
        {
            var htmlResult = await controller.HarvestHealth();
            var jsonResult = await controller.HarvestHealthJson();

            Assert.IsType<NotFoundResult>(htmlResult);
            Assert.IsType<NotFoundResult>(jsonResult);
        }
    }

    [Fact]
    public async Task HarvestHealthActions_ShouldAllowRoutes_InProductionWhenExplicitlyEnabled()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Getting Started", "guides/start", "<p>First steps.</p>")]);
        var options = new AppSurfaceDocsOptions
        {
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                Health = new AppSurfaceDocsHarvestHealthOptions
                {
                    ExposeRoutes = AppSurfaceDocsHarvestHealthExposure.Always
                }
            }
        };
        var (controller, cache, memo) = CreateController(options, harvester, Environments.Production);
        using (memo)
        using (cache)
        {
            var htmlResult = await controller.HarvestHealth();
            var jsonResult = await controller.HarvestHealthJson();

            Assert.IsType<ViewResult>(htmlResult);
            Assert.IsType<JsonResult>(jsonResult);
        }
    }

    [Fact]
    public async Task RouteInspectorJson_ShouldReturnManifestAndProbeAlias()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
                [
                    new DocNode("Package", "packages/README.md", "<p>Package</p>"),
                    new DocNode(
                        "Intro",
                        "docs/intro.md",
                        "<p>Intro</p>",
                        Metadata: new DocMetadata
                        {
                            CanonicalSlug = "start-here/intro",
                            RedirectAliases = ["legacy/intro"]
                        })
                ]);
        var (controller, cache, memo) = CreateController(new AppSurfaceDocsOptions(), harvester);
        using (memo)
        using (cache)
        {
            var result = Assert.IsType<JsonResult>(await controller.RouteInspectorJson("packages/README.md"));
            var response = Assert.IsType<AppSurfaceDocsRouteInspectorResponse>(result.Value);
            var serialized = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var document = JsonDocument.Parse(serialized);

            Assert.Equal("no-store, no-cache", controller.Response.Headers.CacheControl.ToString());
            Assert.NotNull(response.Probe);
            Assert.Equal("AliasRedirect", response.Probe.Kind);
            Assert.Equal("packages/README.md", response.Probe.NormalizedPath);
            Assert.Equal("packages", response.Probe.CanonicalRoutePath);
            Assert.Equal("/docs/packages", response.Probe.CanonicalLiveUrl);
            Assert.Contains(response.Entries, entry => entry.SourcePath == "packages/README.md" && entry.CanonicalRoutePath == "packages");
            var intro = Assert.Single(response.Entries, entry => entry.SourcePath == "docs/intro.md");
            Assert.Contains(intro.DeclaredAliases, alias => alias.RoutePath == "legacy/intro" && alias.Kind == "DeclaredRedirect");
            Assert.True(document.RootElement.TryGetProperty("probe", out var probe));
            Assert.Equal("AliasRedirect", probe.GetProperty("kind").GetString());
            Assert.False(document.RootElement.TryGetProperty("Probe", out _));
        }
    }

    [Fact]
    public async Task RouteInspectorJson_ShouldProbeAppRelativePath_WithPathBase()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Package", "packages/README.md", "<p>Package</p>")]);
        var (controller, cache, memo) = CreateController(new AppSurfaceDocsOptions(), harvester);
        controller.Request.PathBase = "/preview";
        using (memo)
        using (cache)
        {
            var result = Assert.IsType<JsonResult>(await controller.RouteInspectorJson("/preview/docs/packages"));
            var response = Assert.IsType<AppSurfaceDocsRouteInspectorResponse>(result.Value);

            Assert.NotNull(response.Probe);
            Assert.Equal("Canonical", response.Probe.Kind);
            Assert.Equal("packages", response.Probe.NormalizedPath);
            Assert.Equal("/docs/packages", response.Probe.CanonicalLiveUrl);
        }
    }

    [Fact]
    public async Task RouteInspectorJson_ShouldReturnInvalidProbe_ForPathBaseOnlyPath()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Package", "packages/README.md", "<p>Package</p>")]);
        var (controller, cache, memo) = CreateController(new AppSurfaceDocsOptions(), harvester);
        controller.Request.PathBase = "/preview";
        using (memo)
        using (cache)
        {
            var result = Assert.IsType<JsonResult>(await controller.RouteInspectorJson("/preview"));
            var response = Assert.IsType<AppSurfaceDocsRouteInspectorResponse>(result.Value);

            Assert.NotNull(response.Probe);
            Assert.Equal("InvalidInput", response.Probe.Kind);
            Assert.Contains("active docs root", response.Probe.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task RouteInspectorJson_ShouldProbeAppRelativePath_WhenDocsRootIsRootMounted()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Package", "packages/README.md", "<p>Package</p>")]);
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                DocsRootPath = "/"
            }
        };
        var (controller, cache, memo) = CreateController(options, harvester);
        using (memo)
        using (cache)
        {
            var result = Assert.IsType<JsonResult>(await controller.RouteInspectorJson("/packages"));
            var response = Assert.IsType<AppSurfaceDocsRouteInspectorResponse>(result.Value);

            Assert.NotNull(response.Probe);
            Assert.Equal("Canonical", response.Probe.Kind);
            Assert.Equal("packages", response.Probe.NormalizedPath);
            Assert.Equal("/packages", response.Probe.CanonicalLiveUrl);
        }
    }

    [Fact]
    public async Task RouteInspectorJson_ShouldReturnInvalidProbe_ForOutsideAppRelativePath()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Package", "packages/README.md", "<p>Package</p>")]);
        var (controller, cache, memo) = CreateController(new AppSurfaceDocsOptions(), harvester);
        using (memo)
        using (cache)
        {
            var result = Assert.IsType<JsonResult>(await controller.RouteInspectorJson("/other/packages"));
            var response = Assert.IsType<AppSurfaceDocsRouteInspectorResponse>(result.Value);

            Assert.NotNull(response.Probe);
            Assert.Equal("InvalidInput", response.Probe.Kind);
            Assert.Null(response.Probe.NormalizedPath);
            Assert.Contains("active docs root", response.Probe.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task RouteInspectorJson_ShouldReturnManifestWithoutProbe_WhenPathIsOmitted()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Package", "packages/README.md", "<p>Package</p>")]);
        var (controller, cache, memo) = CreateController(new AppSurfaceDocsOptions(), harvester);
        using (memo)
        using (cache)
        {
            var result = Assert.IsType<JsonResult>(await controller.RouteInspectorJson());
            var response = Assert.IsType<AppSurfaceDocsRouteInspectorResponse>(result.Value);

            Assert.Null(response.Probe);
            Assert.Single(response.Entries);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/docs/packages")]
    [InlineData("//example.com/docs/packages")]
    [InlineData("?path=packages")]
    [InlineData("guides/../secret")]
    public async Task RouteInspectorJson_ShouldReturnInvalidProbe_ForUnsafeInputs(string path)
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Package", "packages/README.md", "<p>Package</p>")]);
        var (controller, cache, memo) = CreateController(new AppSurfaceDocsOptions(), harvester);
        using (memo)
        using (cache)
        {
            var result = Assert.IsType<JsonResult>(await controller.RouteInspectorJson(path));
            var response = Assert.IsType<AppSurfaceDocsRouteInspectorResponse>(result.Value);

            Assert.NotNull(response.Probe);
            Assert.Equal("InvalidInput", response.Probe.Kind);
            Assert.Null(response.Probe.NormalizedPath);
            Assert.False(string.IsNullOrWhiteSpace(response.Probe.Message));
        }
    }

    [Fact]
    public async Task RouteInspectorJson_ShouldProbeDocsRootAsHomeRoute()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Home", "README.md", "<p>Home</p>")]);
        var (controller, cache, memo) = CreateController(new AppSurfaceDocsOptions(), harvester);
        using (memo)
        using (cache)
        {
            var result = Assert.IsType<JsonResult>(await controller.RouteInspectorJson("/docs"));
            var response = Assert.IsType<AppSurfaceDocsRouteInspectorResponse>(result.Value);

            Assert.NotNull(response.Probe);
            Assert.Equal("Canonical", response.Probe.Kind);
            Assert.Equal(string.Empty, response.Probe.NormalizedPath);
            Assert.Equal("/docs", response.Probe.CanonicalLiveUrl);
        }
    }

    [Theory]
    [InlineData("_routes", "ReservedRoute")]
    [InlineData("missing/page", "NotFound")]
    [InlineData("Namespaces/Foo", "InternalSourceMatch")]
    public async Task RouteInspectorJson_ShouldDescribeReservedMissingAndInternalRouteKinds(string path, string expectedKind)
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("API", "Namespaces/Foo", "<p>API</p>")]);
        var (controller, cache, memo) = CreateController(new AppSurfaceDocsOptions(), harvester);
        using (memo)
        using (cache)
        {
            var result = Assert.IsType<JsonResult>(await controller.RouteInspectorJson(path));
            var response = Assert.IsType<AppSurfaceDocsRouteInspectorResponse>(result.Value);

            Assert.NotNull(response.Probe);
            Assert.Equal(expectedKind, response.Probe.Kind);
            Assert.False(string.IsNullOrWhiteSpace(response.Probe.Message));
        }
    }

    [Fact]
    public async Task RouteInspectorJson_ShouldStripQueryAndFragment_FromProbePath()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Package", "packages/README.md", "<p>Package</p>")]);
        var (controller, cache, memo) = CreateController(new AppSurfaceDocsOptions(), harvester);
        using (memo)
        using (cache)
        {
            var result = Assert.IsType<JsonResult>(await controller.RouteInspectorJson("/docs/packages?utm=1#top"));
            var response = Assert.IsType<AppSurfaceDocsRouteInspectorResponse>(result.Value);

            Assert.NotNull(response.Probe);
            Assert.Equal("Canonical", response.Probe.Kind);
            Assert.Equal("packages", response.Probe.NormalizedPath);
        }
    }

    [Theory]
    [InlineData((int)DocRouteResolutionKind.CollisionLoser, "This path belongs to a document that lost canonical route ownership.")]
    [InlineData(999, "The route result is not recognized by this AppSurface Docs version.")]
    public void RouteProbeResponse_ShouldDescribeUncommonResolutionKinds(
        int kindValue,
        string expectedMessage)
    {
        var kind = (DocRouteResolutionKind)kindValue;
        var response = AppSurfaceDocsRouteProbeResponse.FromResolution(
            "input",
            "normalized",
            new DocRouteResolution(kind, "source.md", "source"),
            new DocsUrlBuilder(new AppSurfaceDocsOptions()));

        Assert.Equal(kind.ToString(), response.Kind);
        Assert.Equal(expectedMessage, response.Message);
        Assert.Equal("/docs/source", response.CanonicalLiveUrl);
    }

    [Theory]
    [InlineData(AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly, "Development", true)]
    [InlineData(AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly, "Production", false)]
    [InlineData(AppSurfaceDocsHarvestHealthExposure.Always, "Production", true)]
    [InlineData(AppSurfaceDocsHarvestHealthExposure.Never, "Development", false)]
    [InlineData((AppSurfaceDocsHarvestHealthExposure)999, "Development", false)]
    public void AppSurfaceDocsDiagnosticsVisibility_ShouldResolveExposure(
        AppSurfaceDocsHarvestHealthExposure exposure,
        string environmentName,
        bool expected)
    {
        var environment = A.Fake<IHostEnvironment>();
        A.CallTo(() => environment.EnvironmentName).Returns(environmentName);
        var options = new AppSurfaceDocsOptions
        {
            Diagnostics = new AppSurfaceDocsDiagnosticsOptions
            {
                ExposeRouteInspector = exposure
            }
        };

        var exposed = AppSurfaceDocsDiagnosticsVisibility.IsRouteInspectorExposed(options, environment);

        Assert.Equal(expected, exposed);
    }

    [Fact]
    public void AppSurfaceDocsDiagnosticsVisibility_ShouldDefaultToDevelopmentOnly_WhenDiagnosticsOptionsAreNull()
    {
        var environment = A.Fake<IHostEnvironment>();
        A.CallTo(() => environment.EnvironmentName).Returns(Environments.Development);
        var options = new AppSurfaceDocsOptions
        {
            Diagnostics = null!
        };

        var exposed = AppSurfaceDocsDiagnosticsVisibility.IsRouteInspectorExposed(options, environment);

        Assert.True(exposed);
    }

    [Fact]
    public async Task RouteInspector_ShouldRenderViewWithManifest()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Getting Started", "guides/start.md", "<p>First steps.</p>")]);
        var (controller, cache, memo) = CreateController(new AppSurfaceDocsOptions(), harvester);
        using (memo)
        using (cache)
        {
            var result = Assert.IsType<ViewResult>(await controller.RouteInspector("guides/start.md"));
            var model = Assert.IsType<AppSurfaceDocsRouteInspectorResponse>(result.Model);

            Assert.Equal("RouteInspector", result.ViewName);
            Assert.Equal("AliasRedirect", model.Probe?.Kind);
            Assert.Single(model.Entries);
        }
    }

    [Fact]
    public async Task RouteInspectorActions_ShouldReturnNotFound_InProductionByDefault()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Getting Started", "guides/start.md", "<p>First steps.</p>")]);
        var (controller, cache, memo) = CreateController(new AppSurfaceDocsOptions(), harvester, Environments.Production);
        using (memo)
        using (cache)
        {
            var htmlResult = await controller.RouteInspector();
            var jsonResult = await controller.RouteInspectorJson();

            Assert.IsType<NotFoundResult>(htmlResult);
            Assert.IsType<NotFoundResult>(jsonResult);
        }
    }

    [Fact]
    public async Task RouteInspectorActions_ShouldReturnNotFound_InDevelopmentWhenExplicitlyDisabled()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Getting Started", "guides/start.md", "<p>First steps.</p>")]);
        var options = new AppSurfaceDocsOptions
        {
            Diagnostics = new AppSurfaceDocsDiagnosticsOptions
            {
                ExposeRouteInspector = AppSurfaceDocsHarvestHealthExposure.Never
            }
        };
        var (controller, cache, memo) = CreateController(options, harvester, Environments.Development);
        using (memo)
        using (cache)
        {
            var htmlResult = await controller.RouteInspector();
            var jsonResult = await controller.RouteInspectorJson();

            Assert.IsType<NotFoundResult>(htmlResult);
            Assert.IsType<NotFoundResult>(jsonResult);
        }
    }

    [Fact]
    public async Task RouteInspectorActions_ShouldAllowRoutes_InProductionWhenExplicitlyEnabled()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Getting Started", "guides/start.md", "<p>First steps.</p>")]);
        var options = new AppSurfaceDocsOptions
        {
            Diagnostics = new AppSurfaceDocsDiagnosticsOptions
            {
                ExposeRouteInspector = AppSurfaceDocsHarvestHealthExposure.Always
            }
        };
        var (controller, cache, memo) = CreateController(options, harvester, Environments.Production);
        using (memo)
        using (cache)
        {
            var htmlResult = await controller.RouteInspector();
            var jsonResult = await controller.RouteInspectorJson();

            Assert.IsType<ViewResult>(htmlResult);
            Assert.IsType<JsonResult>(jsonResult);
        }
    }

    [Fact]
    public async Task SearchIndex_ShouldRefreshCache_WhenAuthenticatedRefreshRequested()
    {
        var docs = new List<DocNode>
        {
            new("Getting Started", "guides/start", "<p>First steps.</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var first = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var firstPayload = JsonSerializer.Serialize(first.Value);
        using var firstDoc = JsonDocument.Parse(firstPayload);
        var firstGenerated = firstDoc.RootElement.GetProperty("metadata").GetProperty("generatedAtUtc").GetString();

        var refreshedHttpContext = new DefaultHttpContext();
        refreshedHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "test-user") },
            authenticationType: "test-auth"));
        refreshedHttpContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["refresh"] = "1"
        });
        _controller.ControllerContext = new ControllerContext { HttpContext = refreshedHttpContext };

        var second = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var secondPayload = JsonSerializer.Serialize(second.Value);
        using var secondDoc = JsonDocument.Parse(secondPayload);
        var secondGenerated = secondDoc.RootElement.GetProperty("metadata").GetProperty("generatedAtUtc").GetString();

        Assert.NotEqual(firstGenerated, secondGenerated);
    }

    [Fact]
    public async Task SearchIndex_ShouldRefreshCache_WhenAuthenticatedRefreshTrueRequested()
    {
        var docs = new List<DocNode>
        {
            new("Getting Started", "guides/start", "<p>First steps.</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var first = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var firstPayload = JsonSerializer.Serialize(first.Value);
        using var firstDoc = JsonDocument.Parse(firstPayload);
        var firstGenerated = firstDoc.RootElement.GetProperty("metadata").GetProperty("generatedAtUtc").GetString();

        var refreshedHttpContext = new DefaultHttpContext();
        refreshedHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "test-user") },
            authenticationType: "test-auth"));
        refreshedHttpContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["refresh"] = "true"
        });
        _controller.ControllerContext = new ControllerContext { HttpContext = refreshedHttpContext };

        var second = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var secondPayload = JsonSerializer.Serialize(second.Value);
        using var secondDoc = JsonDocument.Parse(secondPayload);
        var secondGenerated = secondDoc.RootElement.GetProperty("metadata").GetProperty("generatedAtUtc").GetString();

        Assert.NotEqual(firstGenerated, secondGenerated);
    }

    [Fact]
    public async Task SearchIndex_ShouldIgnoreRefresh_WhenUnauthenticatedRefreshRequested()
    {
        var docs = new List<DocNode>
        {
            new("Getting Started", "guides/start", "<p>First steps.</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var first = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var firstPayload = JsonSerializer.Serialize(first.Value);
        using var firstDoc = JsonDocument.Parse(firstPayload);
        var firstGenerated = firstDoc.RootElement.GetProperty("metadata").GetProperty("generatedAtUtc").GetString();

        var refreshedHttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity())
        };
        refreshedHttpContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["refresh"] = "true"
        });
        _controller.ControllerContext = new ControllerContext { HttpContext = refreshedHttpContext };

        var second = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var secondPayload = JsonSerializer.Serialize(second.Value);
        using var secondDoc = JsonDocument.Parse(secondPayload);
        var secondGenerated = secondDoc.RootElement.GetProperty("metadata").GetProperty("generatedAtUtc").GetString();

        Assert.Equal(firstGenerated, secondGenerated);
    }

    [Fact]
    public async Task SearchIndex_ShouldIgnoreRefreshRequest_WhenUnauthenticated()
    {
        var docs = new List<DocNode>
        {
            new("Getting Started", "guides/start", "<p>First steps.</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var first = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var firstPayload = JsonSerializer.Serialize(first.Value);
        using var firstDoc = JsonDocument.Parse(firstPayload);
        var firstGenerated = firstDoc.RootElement.GetProperty("metadata").GetProperty("generatedAtUtc").GetString();

        var refreshRequestContext = new DefaultHttpContext();
        refreshRequestContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["refresh"] = "1"
        });
        _controller.ControllerContext = new ControllerContext { HttpContext = refreshRequestContext };

        var second = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var secondPayload = JsonSerializer.Serialize(second.Value);
        using var secondDoc = JsonDocument.Parse(secondPayload);
        var secondGenerated = secondDoc.RootElement.GetProperty("metadata").GetProperty("generatedAtUtc").GetString();

        Assert.Equal(firstGenerated, secondGenerated);
    }

    [Fact]
    public async Task SearchIndex_ShouldEncodeDocPathInUrl()
    {
        var docs = new List<DocNode>
        {
            new("Special Path", "guides/space path#member name", "<p>content</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var payload = JsonSerializer.Serialize(result.Value);
        using var doc = JsonDocument.Parse(payload);

        var firstPath = doc.RootElement
            .GetProperty("documents")
            .EnumerateArray()
            .First()
            .GetProperty("path")
            .GetString();

        Assert.Equal("/docs/guides/space%20path.html#member%20name", firstPath);
    }

    [Fact]
    public async Task SearchIndex_ShouldPrefixRequestPathBaseInDocumentUrls()
    {
        var docs = new List<DocNode>
        {
            new("Getting Started", "guides/start.md", "<p>content</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        _controller.ControllerContext.HttpContext.Request.PathBase = new PathString("/some-base");

        var result = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var payload = JsonSerializer.Serialize(result.Value);
        using var doc = JsonDocument.Parse(payload);

        var firstPath = doc.RootElement
            .GetProperty("documents")
            .EnumerateArray()
            .First()
            .GetProperty("path")
            .GetString();

        Assert.Equal("/some-base/docs/guides/start", firstPath);
    }

    [Fact]
    public async Task SearchIndex_ShouldLeaveDocumentUrlsUnchanged_WhenPathBaseIsEmpty()
    {
        var docs = new List<DocNode>
        {
            new("Getting Started", "guides/start.md", "<p>content</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        SetControllerHttpContext();

        var result = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var payload = JsonSerializer.Serialize(result.Value);
        using var doc = JsonDocument.Parse(payload);

        var firstPath = doc.RootElement
            .GetProperty("documents")
            .EnumerateArray()
            .First()
            .GetProperty("path")
            .GetString();

        Assert.Equal("/docs/guides/start", firstPath);
    }

    [Fact]
    public async Task SearchIndex_ShouldLeaveDocumentUrlsUnchanged_WhenPathBaseIsRoot()
    {
        var docs = new List<DocNode>
        {
            new("Getting Started", "guides/start.md", "<p>content</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        SetControllerHttpContext(pathBase: "/");

        var result = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var payload = JsonSerializer.Serialize(result.Value);
        using var doc = JsonDocument.Parse(payload);

        var firstPath = doc.RootElement
            .GetProperty("documents")
            .EnumerateArray()
            .First()
            .GetProperty("path")
            .GetString();

        Assert.Equal("/docs/guides/start", firstPath);
    }

    [Fact]
    public void PrefixSearchIndexPathsForPathBase_ShouldRewriteOnlyRootedDocumentPaths()
    {
        var payload = new DocsSearchIndexPayload(
            new DocsSearchIndexMetadata("2026-05-03T00:00:00.0000000+00:00", "1", "minisearch"),
            [
                new DocsSearchIndexDocument(
                    Id: "relative",
                    Path: "guide.html",
                    Title: "Relative",
                    Summary: "Summary",
                    Headings: [],
                    BodyText: string.Empty,
                    Snippet: string.Empty,
                    PageType: null,
                    PageTypeLabel: null,
                    PageTypeVariant: null,
                    Audience: null,
                    Component: null,
                    Aliases: [],
                    Keywords: [],
                    Status: null,
                    NavGroup: null,
                    PublicSection: null,
                    PublicSectionLabel: null,
                    IsSectionLanding: false,
                    Order: null,
                    SequenceKey: null,
                    CanonicalSlug: null,
                    RelatedPages: [],
                    Breadcrumbs: []),
                new DocsSearchIndexDocument(
                    Id: "blank",
                    Path: string.Empty,
                    Title: "Blank",
                    Summary: "Summary",
                    Headings: [],
                    BodyText: string.Empty,
                    Snippet: string.Empty,
                    PageType: null,
                    PageTypeLabel: null,
                    PageTypeVariant: null,
                    Audience: null,
                    Component: null,
                    Aliases: [],
                    Keywords: [],
                    Status: null,
                    NavGroup: null,
                    PublicSection: null,
                    PublicSectionLabel: null,
                    IsSectionLanding: false,
                    Order: null,
                    SequenceKey: null,
                    CanonicalSlug: null,
                    RelatedPages: [],
                    Breadcrumbs: []),
                new DocsSearchIndexDocument(
                    Id: "rooted",
                    Path: "/docs/already",
                    Title: "Rooted",
                    Summary: "Summary",
                    Headings: [],
                    BodyText: string.Empty,
                    Snippet: string.Empty,
                    PageType: null,
                    PageTypeLabel: null,
                    PageTypeVariant: null,
                    Audience: null,
                    Component: null,
                    Aliases: [],
                    Keywords: [],
                    Status: null,
                    NavGroup: null,
                    PublicSection: null,
                    PublicSectionLabel: null,
                    IsSectionLanding: false,
                    Order: null,
                    SequenceKey: null,
                    CanonicalSlug: null,
                    RelatedPages: [],
                    Breadcrumbs: [])
            ]);

        var rewritten = DocsController.PrefixSearchIndexPathsForPathBase(payload, "/some-base");

        Assert.NotSame(payload, rewritten);
        Assert.Equal("guide.html", payload.Documents[0].Path);
        Assert.Equal(string.Empty, payload.Documents[1].Path);
        Assert.Equal("/docs/already", payload.Documents[2].Path);
        Assert.Equal("guide.html", rewritten.Documents[0].Path);
        Assert.Equal(string.Empty, rewritten.Documents[1].Path);
        Assert.Equal("/some-base/docs/already", rewritten.Documents[2].Path);
    }

    [Fact]
    public void PrefixSearchIndexPathsForPathBase_ShouldReturnSamePayload_WhenPathBaseIsEmptyOrRoot()
    {
        var payload = new DocsSearchIndexPayload(
            new DocsSearchIndexMetadata("2026-05-03T00:00:00.0000000+00:00", "1", "minisearch"),
            [
                new DocsSearchIndexDocument(
                    Id: "rooted",
                    Path: "/docs/already",
                    Title: "Rooted",
                    Summary: "Summary",
                    Headings: [],
                    BodyText: string.Empty,
                    Snippet: string.Empty,
                    PageType: null,
                    PageTypeLabel: null,
                    PageTypeVariant: null,
                    Audience: null,
                    Component: null,
                    Aliases: [],
                    Keywords: [],
                    Status: null,
                    NavGroup: null,
                    PublicSection: null,
                    PublicSectionLabel: null,
                    IsSectionLanding: false,
                    Order: null,
                    SequenceKey: null,
                    CanonicalSlug: null,
                    RelatedPages: [],
                    Breadcrumbs: [])
            ]);

        var unchangedEmpty = DocsController.PrefixSearchIndexPathsForPathBase(payload, null);
        var unchangedRoot = DocsController.PrefixSearchIndexPathsForPathBase(payload, "/");

        Assert.Same(payload, unchangedEmpty);
        Assert.Same(payload, unchangedRoot);
    }

    [Fact]
    public void PrefixSearchIndexPathsForPathBase_ShouldReturnSamePayload_WhenNoRootedPathsNeedRewrite()
    {
        var payload = new DocsSearchIndexPayload(
            new DocsSearchIndexMetadata("2026-05-03T00:00:00.0000000+00:00", "1", "minisearch"),
            [
                new DocsSearchIndexDocument(
                    Id: "relative",
                    Path: "guide.html",
                    Title: "Relative",
                    Summary: "Summary",
                    Headings: [],
                    BodyText: string.Empty,
                    Snippet: string.Empty,
                    PageType: null,
                    PageTypeLabel: null,
                    PageTypeVariant: null,
                    Audience: null,
                    Component: null,
                    Aliases: [],
                    Keywords: [],
                    Status: null,
                    NavGroup: null,
                    PublicSection: null,
                    PublicSectionLabel: null,
                    IsSectionLanding: false,
                    Order: null,
                    SequenceKey: null,
                    CanonicalSlug: null,
                    RelatedPages: [],
                    Breadcrumbs: []),
                new DocsSearchIndexDocument(
                    Id: "blank",
                    Path: string.Empty,
                    Title: "Blank",
                    Summary: "Summary",
                    Headings: [],
                    BodyText: string.Empty,
                    Snippet: string.Empty,
                    PageType: null,
                    PageTypeLabel: null,
                    PageTypeVariant: null,
                    Audience: null,
                    Component: null,
                    Aliases: [],
                    Keywords: [],
                    Status: null,
                    NavGroup: null,
                    PublicSection: null,
                    PublicSectionLabel: null,
                    IsSectionLanding: false,
                    Order: null,
                    SequenceKey: null,
                    CanonicalSlug: null,
                    RelatedPages: [],
                    Breadcrumbs: [])
            ]);

        var unchanged = DocsController.PrefixSearchIndexPathsForPathBase(payload, "/some-base");

        Assert.Same(payload, unchanged);
    }

    [Fact]
    public async Task SearchIndex_ShouldTruncateSnippetAtWordBoundary()
    {
        var longWordyContent = "<p>" + string.Join(" ", Enumerable.Repeat("word", 80)) + "</p>";
        var docs = new List<DocNode> { new("Long", "guides/long", longWordyContent) };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var payload = JsonSerializer.Serialize(result.Value);
        using var doc = JsonDocument.Parse(payload);

        var snippet = doc.RootElement
            .GetProperty("documents")
            .EnumerateArray()
            .First()
            .GetProperty("snippet")
            .GetString();

        Assert.NotNull(snippet);
        Assert.EndsWith("...", snippet);
        Assert.DoesNotContain(" ...", snippet);
        Assert.Equal(snippet.TrimEnd(), snippet);
        Assert.True(snippet.Length <= 220, $"Snippet length {snippet.Length} exceeds 220.");
    }

    [Fact]
    public async Task SearchIndex_ShouldSynthesizeFallbackTitle_WhenDocumentBodyIsEmpty()
    {
        var docs = new List<DocNode>
        {
            new("", "guides/empty.md", "<script>alert('x')</script><style>body{}</style>"),
            new("Kept", "guides/kept.md", "<p>Visible body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var payload = JsonSerializer.Serialize(result.Value);
        using var document = JsonDocument.Parse(payload);

        var items = document.RootElement.GetProperty("documents").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        var synthesizedTitleDocument = Assert.Single(items, item => item.GetProperty("path").GetString() == "/docs/guides/empty");
        Assert.Equal("empty.md", synthesizedTitleDocument.GetProperty("title").GetString());

        var keptDocument = Assert.Single(items, item => item.GetProperty("path").GetString() == "/docs/guides/kept");
        Assert.Equal("Kept", keptDocument.GetProperty("title").GetString());
    }

    [Fact]
    public async Task SearchIndex_ShouldExcludeDocumentsHiddenFromSearch()
    {
        var docs = new List<DocNode>
        {
            new(
                "Hidden",
                "guides/hidden",
                "<p>Body</p>",
                Metadata: new DocMetadata
                {
                    HideFromSearch = true
                }),
            new("Visible", "guides/visible", "<p>Body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var payload = JsonSerializer.Serialize(result.Value);
        using var document = JsonDocument.Parse(payload);

        var items = document.RootElement.GetProperty("documents").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("Visible", items[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task SearchIndex_ShouldCollapseDuplicatePaths_AndHandleNullContent()
    {
        var docs = new List<DocNode>
        {
            new("First", "guides/dup", null!),
            new("Second", "guides/dup", "<p>Body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var payload = JsonSerializer.Serialize(result.Value);
        using var document = JsonDocument.Parse(payload);

        var items = document.RootElement.GetProperty("documents").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("First", items[0].GetProperty("title").GetString());
        Assert.Equal(string.Empty, items[0].GetProperty("bodyText").GetString());
    }

    [Fact]
    public async Task SearchIndex_ShouldMapWhitespaceAndFragmentOnlyPaths_ToDocsRootUrl()
    {
        var docs = new List<DocNode>
        {
            new("Root", "   ", "<p>Body</p>"),
            new("Fragment", "#overview", "<p>Body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);

        var result = Assert.IsType<JsonResult>(await _controller.SearchIndex());
        var payload = JsonSerializer.Serialize(result.Value);
        using var document = JsonDocument.Parse(payload);

        var paths = document.RootElement
            .GetProperty("documents")
            .EnumerateArray()
            .Select(e => e.GetProperty("path").GetString())
            .ToList();

        Assert.Contains("/docs", paths);
        Assert.Contains("/docs#overview", paths);
    }

    [Fact]
    public void PrivateHelpers_ShouldHandleNullAndUnbrokenTextBranches()
    {
        var normalized = DocAggregator.NormalizeSearchText(null!);
        var rootUrl = DocAggregator.BuildSearchDocUrl(" ");
        var truncated = DocAggregator.TruncateSnippetAtWordBoundary(new string('a', 260), 220);

        Assert.Equal(string.Empty, normalized);
        Assert.Equal("/docs", rootUrl);
        Assert.Equal(220, truncated.Length);
        Assert.EndsWith("...", truncated);
    }

    [Fact]
    public void TruncateSnippetAtWordBoundary_ShouldRespectTinyLimits()
    {
        Assert.Equal("...", DocAggregator.TruncateSnippetAtWordBoundary("abcdef", 3));
        Assert.Equal(".", DocAggregator.TruncateSnippetAtWordBoundary("abcdef", 1));
        Assert.Equal(string.Empty, DocAggregator.TruncateSnippetAtWordBoundary("abcdef", 0));
    }

    [Fact]
    public void CanRefreshCache_ShouldReturnFalse_WhenUserOrIdentityIsMissing()
    {
        _controller.ControllerContext = new ControllerContext();
        var nullContextResult = _controller.CanRefreshCache();

        var noIdentityHttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal()
        };
        _controller.ControllerContext = new ControllerContext { HttpContext = noIdentityHttpContext };
        var noIdentityResult = _controller.CanRefreshCache();

        Assert.False(nullContextResult);
        Assert.False(noIdentityResult);
    }

    public void Dispose()
    {
        foreach (var directory in _temporaryCatalogRoots)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        (_memo as IDisposable)?.Dispose();
        _cache.Dispose();
    }

    private void SetControllerHttpContext(string? pathBase = null)
    {
        var httpContext = new DefaultHttpContext();
        if (!string.IsNullOrWhiteSpace(pathBase))
        {
            httpContext.Request.PathBase = new PathString(pathBase);
        }

        _controller.ControllerContext = CreateControllerContext(httpContext);
        _controller.Url = new UrlHelper(_controller.ControllerContext);
    }

    private static ControllerContext CreateControllerContext(HttpContext httpContext)
    {
        return new ControllerContext
        {
            HttpContext = httpContext,
            RouteData = new RouteData(),
            ActionDescriptor = new ControllerActionDescriptor()
        };
    }

    private (DocsController Controller, IMemoryCache Cache, Memo Memo) CreateController(
        AppSurfaceDocsOptions options,
        IDocHarvester harvester)
    {
        return CreateController(options, [harvester]);
    }

    private (DocsController Controller, IMemoryCache Cache, Memo Memo) CreateController(
        AppSurfaceDocsOptions options,
        IDocHarvester harvester,
        string environmentName)
    {
        return CreateController(options, [harvester], environmentName);
    }

    private (DocsController Controller, IMemoryCache Cache, Memo Memo) CreateController(
        AppSurfaceDocsOptions options,
        IReadOnlyList<IDocHarvester> harvesters,
        string? environmentName = null)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var memo = new Memo(cache);
        var environment = A.Fake<IWebHostEnvironment>();
        var sanitizer = A.Fake<IAppSurfaceDocsHtmlSanitizer>();
        var aggregatorLogger = A.Fake<ILogger<DocAggregator>>();
        var controllerLogger = A.Fake<ILogger<DocsController>>();

        A.CallTo(() => environment.ContentRootPath).Returns(Path.GetTempPath());
        A.CallTo(() => environment.EnvironmentName).Returns(environmentName ?? Environments.Development);
        A.CallTo(() => sanitizer.Sanitize(A<string>._)).ReturnsLazily((string input) => input);

        var aggregator = new DocAggregator(
            harvesters,
            options,
            environment,
            memo,
            sanitizer,
            aggregatorLogger);
        var docsUrlBuilder = new DocsUrlBuilder(options);
        var versionCatalogService = CreateDefaultVersionCatalogService(options);

        var controller = new DocsController(
            aggregator,
            docsUrlBuilder,
            versionCatalogService,
            new DocFeaturedPageResolver(_featuredPageResolverLoggerFake, docsUrlBuilder),
            options,
            environment,
            controllerLogger)
        {
            ControllerContext = CreateControllerContext(new DefaultHttpContext())
        };
        controller.Url = new UrlHelper(controller.ControllerContext);

        return (controller, cache, memo);
    }

    private PendingHarvestController CreatePendingHarvestController(string requestPath)
    {
        var release = new TaskCompletionSource<IReadOnlyList<DocNode>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(release.Task);
        var options = new AppSurfaceDocsOptions
        {
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                InitialRequestWaitBudgetMilliseconds = 0
            }
        };
        var cache = new MemoryCache(new MemoryCacheOptions());
        var memo = new Memo(cache);
        var environment = A.Fake<IWebHostEnvironment>();
        var sanitizer = A.Fake<IAppSurfaceDocsHtmlSanitizer>();
        var aggregatorLogger = A.Fake<ILogger<DocAggregator>>();
        var controllerLogger = A.Fake<ILogger<DocsController>>();
        var services = A.Fake<IServiceProvider>();
        A.CallTo(() => environment.ContentRootPath).Returns(Path.GetTempPath());
        A.CallTo(() => sanitizer.Sanitize(A<string>._)).ReturnsLazily((string input) => input);
        A.CallTo(() => services.GetService(typeof(IRazorWireStreamHub))).Returns(null);

        var aggregator = new DocAggregator(
            [harvester],
            options,
            environment,
            memo,
            sanitizer,
            aggregatorLogger);
        var progress = new AppSurfaceDocsHarvestProgressReporter(
            services,
            A.Fake<ILogger<AppSurfaceDocsHarvestProgressReporter>>());
        var coordinator = new AppSurfaceDocsHarvestCoordinator(aggregator, progress);
        var initialHarvest = coordinator.EnsureStarted();
        var docsUrlBuilder = new DocsUrlBuilder(options);
        var controller = new DocsController(
            aggregator,
            docsUrlBuilder,
            CreateDefaultVersionCatalogService(options),
            new DocFeaturedPageResolver(_featuredPageResolverLoggerFake, docsUrlBuilder),
            options,
            environment,
            controllerLogger,
            coordinator)
        {
            ControllerContext = CreateControllerContext(new DefaultHttpContext())
        };
        controller.ControllerContext.HttpContext.Request.Path = requestPath;
        controller.Url = new UrlHelper(controller.ControllerContext);

        return new PendingHarvestController(controller, release, initialHarvest, cache, memo);
    }

    private static void AssertHarvestingView(IActionResult result, string expectedReturnUrl)
    {
        var viewResult = Assert.IsType<ViewResult>(result);
        Assert.Equal("Harvesting", viewResult.ViewName);
        var model = Assert.IsType<AppSurfaceDocsHarvestingViewModel>(viewResult.Model);
        Assert.Equal(expectedReturnUrl, model.ReturnUrl);
    }

    private static DocFeaturedPageGroupDefinition FeaturedGroup(params DocFeaturedPageDefinition[] pages)
    {
        return new DocFeaturedPageGroupDefinition
        {
            Intent = "test",
            Label = "Test",
            Pages = pages
        };
    }

    private static DocLandingFeaturedPageViewModel SingleFeaturedPage(DocLandingViewModel model)
    {
        var group = Assert.Single(model.FeaturedPageGroups);
        return Assert.Single(group.Pages);
    }

    private void AssertWarningLogged(string? expectedMessageFragment = null)
    {
        var controllerLogged = Fake.GetCalls(_controllerLoggerFake)
            .Any(call => IsWarningLog(call, expectedMessageFragment));
        var resolverLogged = Fake.GetCalls(_featuredPageResolverLoggerFake)
            .Any(call => IsWarningLog(call, expectedMessageFragment));

        Assert.True(
            controllerLogged || resolverLogged,
            expectedMessageFragment is null
                ? "Expected a warning log to be emitted."
                : $"Expected warning log containing '{expectedMessageFragment}'.");
    }

    private void AssertNoWarningsLogged()
    {
        var controllerLoggedWarning = Fake.GetCalls(_controllerLoggerFake)
            .Any(call => IsWarningLog(call));
        var resolverLoggedWarning = Fake.GetCalls(_featuredPageResolverLoggerFake)
            .Any(call => IsWarningLog(call));

        Assert.False(controllerLoggedWarning || resolverLoggedWarning, "Expected no warning logs.");
    }

    private static bool IsWarningLog(FakeItEasy.Core.IFakeObjectCall call, string? expectedMessageFragment)
    {
        if (!IsWarningLog(call))
        {
            return false;
        }

        if (expectedMessageFragment is null)
        {
            return true;
        }

        var message = call.GetArgument<object>(2)?.ToString();
        return message?.Contains(expectedMessageFragment, StringComparison.OrdinalIgnoreCase) == true;
    }

    private AppSurfaceDocsVersionCatalogService CreateDefaultVersionCatalogService(AppSurfaceDocsOptions options)
    {
        var emptyCatalogRoot = Path.Combine(
            Path.GetTempPath(),
            "appsurfacedocs-controller-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyCatalogRoot);
        _temporaryCatalogRoots.Add(emptyCatalogRoot);

        var environment = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => environment.ContentRootPath).Returns(emptyCatalogRoot);

        return new AppSurfaceDocsVersionCatalogService(
            options,
            environment,
            NullLogger<AppSurfaceDocsVersionCatalogService>.Instance);
    }

    private static bool IsWarningLog(FakeItEasy.Core.IFakeObjectCall call)
    {
        return call.Method.Name == nameof(ILogger.Log)
               && call.GetArgument<LogLevel>(0) == LogLevel.Warning;
    }

    private sealed class PrefixJsonNamingPolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name)
        {
            return $"x_{name}";
        }
    }

    private sealed class PendingHarvestController : IAsyncDisposable
    {
        private readonly TaskCompletionSource<IReadOnlyList<DocNode>> _release;
        private readonly Task<DocHarvestHealthSnapshot> _initialHarvest;
        private readonly IMemoryCache _cache;
        private readonly Memo _memo;

        public PendingHarvestController(
            DocsController controller,
            TaskCompletionSource<IReadOnlyList<DocNode>> release,
            Task<DocHarvestHealthSnapshot> initialHarvest,
            IMemoryCache cache,
            Memo memo)
        {
            Controller = controller;
            _release = release;
            _initialHarvest = initialHarvest;
            _cache = cache;
            _memo = memo;
        }

        public DocsController Controller { get; }

        public async ValueTask DisposeAsync()
        {
            using var cache = _cache;
            using var memo = _memo;

            _release.TrySetResult([]);
            await _initialHarvest.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }
}
