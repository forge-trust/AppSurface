using FakeItEasy;
using ForgeTrust.Runnable.Web.RazorDocs.Models;
using ForgeTrust.Runnable.Web.RazorDocs.Services;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

public sealed class DocFeaturedPageResolverTests
{
    [Fact]
    public void ResolveGroups_ShouldThrow_WhenDocsIsNull()
    {
        var resolver = new DocFeaturedPageResolver(A.Fake<ILogger<DocFeaturedPageResolver>>());
        var landing = Landing(
            new DocFeaturedPageDefinition
            {
                Path = "guides/intro.md"
            });

        Assert.Throws<ArgumentNullException>(() => resolver.ResolveGroups(landing, null!));
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenLoggerIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new DocFeaturedPageResolver(null!));
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenDocsUrlBuilderIsNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DocFeaturedPageResolver(A.Fake<ILogger<DocFeaturedPageResolver>>(), null!));
    }

    [Fact]
    public void ResolveGroups_ShouldReturnEmpty_WhenLandingDocIsNull()
    {
        var resolver = new DocFeaturedPageResolver(A.Fake<ILogger<DocFeaturedPageResolver>>());

        var groups = resolver.ResolveGroups(null, []);

        Assert.Empty(groups);
    }

    [Fact]
    public void ResolveGroups_ShouldSkipBlankDestinationPath_AndLogWarnings()
    {
        var logger = A.Fake<ILogger<DocFeaturedPageResolver>>();
        var resolver = new DocFeaturedPageResolver(logger);
        var landing = Landing(
            new DocFeaturedPageDefinition
            {
                Path = "   "
            });

        var groups = resolver.ResolveGroups(landing, [landing]);

        Assert.Empty(groups);
        AssertWarningLogged(logger, "has no destination path");
        AssertWarningLogged(logger, "no visible destination pages resolved");
    }

    [Fact]
    public void ResolveGroups_ShouldSkipMissingDestination_AndLogWarnings()
    {
        var logger = A.Fake<ILogger<DocFeaturedPageResolver>>();
        var resolver = new DocFeaturedPageResolver(logger);
        var landing = Landing(
            new DocFeaturedPageDefinition
            {
                Path = "guides/missing.md"
            });

        var groups = resolver.ResolveGroups(landing, [landing]);

        Assert.Empty(groups);
        AssertWarningLogged(logger, "destination page could not be resolved");
        AssertWarningLogged(logger, "no visible destination pages resolved");
    }

    [Fact]
    public void ResolveGroups_ShouldSkipGroupsWithNullPages_AndLogWarnings()
    {
        var logger = A.Fake<ILogger<DocFeaturedPageResolver>>();
        var resolver = new DocFeaturedPageResolver(logger);
        var landing = new DocNode(
            "Home",
            "README.md",
            "<p>Home</p>",
            CanonicalPath: "index.html",
            Metadata: new DocMetadata
            {
                FeaturedPageGroups =
                [
                    new DocFeaturedPageGroupDefinition
                    {
                        Label = "Broken",
                        Pages = null!
                    }
                ]
            });

        var groups = resolver.ResolveGroups(landing, [landing]);

        Assert.Empty(groups);
        AssertWarningLogged(logger, "has no pages");
        AssertWarningLogged(logger, "no visible destination pages resolved");
    }

    [Fact]
    public void ResolveGroups_ShouldSkipNullGroupsBeforeOrdering_AndLogWarnings()
    {
        var logger = A.Fake<ILogger<DocFeaturedPageResolver>>();
        var resolver = new DocFeaturedPageResolver(logger);
        var landing = new DocNode(
            "Home",
            "README.md",
            "<p>Home</p>",
            CanonicalPath: "index.html",
            Metadata: new DocMetadata
            {
                FeaturedPageGroups =
                [
                    null!,
                    new DocFeaturedPageGroupDefinition
                    {
                        Label = "Start",
                        Pages =
                        [
                            new DocFeaturedPageDefinition
                            {
                                Path = "guides/intro.md"
                            }
                        ]
                    }
                ]
            });
        var intro = Doc("Intro", "guides/intro.md");

        var groups = resolver.ResolveGroups(landing, [landing, intro]);

        var page = Assert.Single(Assert.Single(groups).Pages);
        Assert.Equal("Intro", page.Title);
        AssertWarningLogged(logger, "group on README.md at featured_page_groups[0] because it is null");
    }

    [Fact]
    public void ResolveGroups_ShouldSkipNullPageEntries_AndKeepValidPages()
    {
        var logger = A.Fake<ILogger<DocFeaturedPageResolver>>();
        var resolver = new DocFeaturedPageResolver(logger);
        var landing = new DocNode(
            "Home",
            "README.md",
            "<p>Home</p>",
            CanonicalPath: "index.html",
            Metadata: new DocMetadata
            {
                FeaturedPageGroups =
                [
                    new DocFeaturedPageGroupDefinition
                    {
                        Label = "Start",
                        Pages =
                        [
                            null!,
                            new DocFeaturedPageDefinition
                            {
                                Path = "guides/intro.md"
                            }
                        ]
                    }
                ]
            });
        var intro = Doc("Intro", "guides/intro.md");

        var groups = resolver.ResolveGroups(landing, [landing, intro]);

        var page = Assert.Single(Assert.Single(groups).Pages);
        Assert.Equal("Intro", page.Title);
        AssertWarningLogged(logger, "because it is null");
    }

    [Fact]
    public void ResolveGroups_ShouldSkipHiddenDestination_AndLogWarnings()
    {
        var logger = A.Fake<ILogger<DocFeaturedPageResolver>>();
        var resolver = new DocFeaturedPageResolver(logger);
        var landing = Landing(
            new DocFeaturedPageDefinition
            {
                Path = "guides/hidden.md"
            });
        var hidden = Doc(
            "Hidden",
            "guides/hidden.md",
            metadata: new DocMetadata
            {
                HideFromPublicNav = true
            });

        var groups = resolver.ResolveGroups(landing, [landing, hidden]);

        Assert.Empty(groups);
        AssertWarningLogged(logger, "destination page is hidden from public navigation");
        AssertWarningLogged(logger, "no visible destination pages resolved");
    }

    [Fact]
    public void ResolveGroups_ShouldSkipDuplicateDestination_AndKeepFirstPage()
    {
        var logger = A.Fake<ILogger<DocFeaturedPageResolver>>();
        var resolver = new DocFeaturedPageResolver(logger);
        var landing = Landing(
            new DocFeaturedPageDefinition
            {
                Question = "First",
                Path = "guides/intro.md"
            },
            new DocFeaturedPageDefinition
            {
                Question = "Duplicate",
                Path = "guides/intro.md.html"
            });
        var intro = Doc("Intro", "guides/intro.md");

        var groups = resolver.ResolveGroups(landing, [landing, intro]);

        var page = Assert.Single(Assert.Single(groups).Pages);
        Assert.Equal("First", page.Question);
        AssertWarningLogged(logger, "destination is already featured");
    }

    [Theory]
    [InlineData("/docs/guides/intro.md.html")]
    [InlineData("docs/guides/intro.md.html")]
    public void ResolveGroups_ShouldResolveRoutePrefixedCanonicalDestinationPaths(string authoredPath)
    {
        var resolver = new DocFeaturedPageResolver(A.Fake<ILogger<DocFeaturedPageResolver>>());
        var landing = Landing(
            new DocFeaturedPageDefinition
            {
                Path = authoredPath
            });
        var intro = Doc("Intro", "guides/intro.md");

        var groups = resolver.ResolveGroups(landing, [landing, intro]);

        var page = Assert.Single(Assert.Single(groups).Pages);
        Assert.Equal("Intro", page.Title);
        Assert.Equal("/docs/guides/intro.md.html", page.Href);
    }

    [Theory]
    [InlineData("/docs/next/guides/intro.md.html")]
    [InlineData("docs/next/guides/intro.md.html")]
    [InlineData("/docs/guides/intro.md.html")]
    [InlineData("docs/guides/intro.md.html")]
    public void ResolveGroups_ShouldResolveCurrentRootCanonicalDestinationPaths(string authoredPath)
    {
        var resolver = new DocFeaturedPageResolver(
            A.Fake<ILogger<DocFeaturedPageResolver>>(),
            new DocsUrlBuilder(
                new RazorDocsOptions
                {
                    Routing = new RazorDocsRoutingOptions
                    {
                        DocsRootPath = "/docs/next"
                    },
                    Versioning = new RazorDocsVersioningOptions
                    {
                        Enabled = true
                    }
                }));
        var landing = Landing(
            new DocFeaturedPageDefinition
            {
                Path = authoredPath
            });
        var intro = Doc("Intro", "guides/intro.md");

        var groups = resolver.ResolveGroups(landing, [landing, intro]);

        var page = Assert.Single(Assert.Single(groups).Pages);
        Assert.Equal("Intro", page.Title);
        Assert.Equal("/docs/next/guides/intro.md.html", page.Href);
    }

    [Fact]
    public void ResolveGroups_ShouldHonorConfiguredLiveDocsRoot()
    {
        var resolver = new DocFeaturedPageResolver(
            A.Fake<ILogger<DocFeaturedPageResolver>>(),
            new DocsUrlBuilder(
                new RazorDocsOptions
                {
                    Routing = new RazorDocsRoutingOptions
                    {
                        DocsRootPath = "/docs/next"
                    },
                    Versioning = new RazorDocsVersioningOptions
                    {
                        Enabled = true
                    }
                }));
        var landing = Landing(
            new DocFeaturedPageDefinition
            {
                Path = "guides/intro.md"
            });
        var intro = Doc("Intro", "guides/intro.md");

        var groups = resolver.ResolveGroups(landing, [landing, intro]);

        var page = Assert.Single(Assert.Single(groups).Pages);
        Assert.Equal("/docs/next/guides/intro.md.html", page.Href);
    }

    [Theory]
    [InlineData("/foo/bar/guides/intro.md.html")]
    [InlineData("foo/bar/guides/intro.md.html")]
    [InlineData("/foo/bar/next/guides/intro.md.html")]
    public void ResolveGroups_ShouldResolveConfiguredRouteRootCanonicalDestinationPaths(string authoredPath)
    {
        var resolver = new DocFeaturedPageResolver(
            A.Fake<ILogger<DocFeaturedPageResolver>>(),
            new DocsUrlBuilder(
                new RazorDocsOptions
                {
                    Routing = new RazorDocsRoutingOptions
                    {
                        RouteRootPath = "/foo/bar",
                        DocsRootPath = "/foo/bar/next"
                    },
                    Versioning = new RazorDocsVersioningOptions
                    {
                        Enabled = true
                    }
                }));
        var landing = Landing(
            new DocFeaturedPageDefinition
            {
                Path = authoredPath
            });
        var intro = Doc("Intro", "guides/intro.md");

        var groups = resolver.ResolveGroups(landing, [landing, intro]);

        var page = Assert.Single(Assert.Single(groups).Pages);
        Assert.Equal("Intro", page.Title);
        Assert.Equal("/foo/bar/next/guides/intro.md.html", page.Href);
    }

    [Fact]
    public void ResolveGroups_ShouldResolveStableRoutePrefixedPaths_WhenCurrentDocsRootIsRootMounted()
    {
        var resolver = new DocFeaturedPageResolver(
            A.Fake<ILogger<DocFeaturedPageResolver>>(),
            new DocsUrlBuilder(
                new RazorDocsOptions
                {
                    Routing = new RazorDocsRoutingOptions
                    {
                        DocsRootPath = "/"
                    }
                }));
        var landing = Landing(
            new DocFeaturedPageDefinition
            {
                Path = "/docs/guides/intro.md.html"
            });
        var intro = Doc("Intro", "guides/intro.md");

        var groups = resolver.ResolveGroups(landing, [landing, intro]);

        var page = Assert.Single(Assert.Single(groups).Pages);
        Assert.Equal("Intro", page.Title);
        Assert.Equal("/guides/intro.md.html", page.Href);
    }

    [Fact]
    public void ResolveGroups_ShouldSkipMissingStableRoutePrefixedDestination_WhenCurrentDocsRootIsRootMounted()
    {
        var logger = A.Fake<ILogger<DocFeaturedPageResolver>>();
        var resolver = new DocFeaturedPageResolver(
            logger,
            new DocsUrlBuilder(
                new RazorDocsOptions
                {
                    Routing = new RazorDocsRoutingOptions
                    {
                        DocsRootPath = "/"
                    }
                }));
        var landing = Landing(
            new DocFeaturedPageDefinition
            {
                Path = "/docs/guides/missing.md.html"
            });

        var groups = resolver.ResolveGroups(landing, [landing]);

        Assert.Empty(groups);
        AssertWarningLogged(logger, "destination page could not be resolved");
        AssertWarningLogged(logger, "no visible destination pages resolved");
    }

    [Fact]
    public void ResolveGroups_ShouldOrderGroupsAndPages_ByOrderThenAuthoredPosition()
    {
        var resolver = new DocFeaturedPageResolver(A.Fake<ILogger<DocFeaturedPageResolver>>());
        var landing = new DocNode(
            "Home",
            "README.md",
            "<p>Home</p>",
            CanonicalPath: "index.html",
            Metadata: new DocMetadata
            {
                FeaturedPageGroups =
                [
                    new DocFeaturedPageGroupDefinition
                    {
                        Intent = "later",
                        Label = "Later",
                        Order = 20,
                        Pages =
                        [
                            new DocFeaturedPageDefinition
                            {
                                Question = "Second page",
                                Path = "guides/second.md",
                                Order = 20
                            },
                            new DocFeaturedPageDefinition
                            {
                                Question = "First page",
                                Path = "guides/first.md",
                                Order = 10
                            }
                        ]
                    },
                    new DocFeaturedPageGroupDefinition
                    {
                        Intent = "earlier",
                        Label = "Earlier",
                        Order = 10,
                        Pages =
                        [
                            new DocFeaturedPageDefinition
                            {
                                Question = "Early page",
                                Path = "guides/early.md"
                            }
                        ]
                    }
                ]
            });
        var docs = new[]
        {
            landing,
            new DocNode("Second", "guides/second.md", "<p>Second</p>", CanonicalPath: "guides/second.md.html"),
            new DocNode("First", "guides/first.md", "<p>First</p>", CanonicalPath: "guides/first.md.html"),
            new DocNode("Early", "guides/early.md", "<p>Early</p>", CanonicalPath: "guides/early.md.html")
        };

        var groups = resolver.ResolveGroups(landing, docs);

        Assert.Collection(
            groups,
            first =>
            {
                Assert.Equal("Earlier", first.Label);
                Assert.Equal("Early page", Assert.Single(first.Pages).Question);
            },
            second =>
            {
                Assert.Equal("Later", second.Label);
                Assert.Equal(["First page", "Second page"], second.Pages.Select(page => page.Question).ToArray());
            });
    }

    [Fact]
    public void ResolveGroups_ShouldFallbackGroupFields_WhenMetadataBypassesParserNormalization()
    {
        var resolver = new DocFeaturedPageResolver(A.Fake<ILogger<DocFeaturedPageResolver>>());
        var landing = new DocNode(
            "Home",
            "README.md",
            "<p>Home</p>",
            CanonicalPath: "index.html",
            Metadata: new DocMetadata
            {
                FeaturedPageGroups =
                [
                    new DocFeaturedPageGroupDefinition
                    {
                        Pages =
                        [
                            new DocFeaturedPageDefinition
                            {
                                Path = "guides/intro.md"
                            }
                        ]
                    },
                    new DocFeaturedPageGroupDefinition
                    {
                        Intent = "manual-intent",
                        Pages =
                        [
                            new DocFeaturedPageDefinition
                            {
                                Path = "guides/reference.md"
                            }
                        ]
                    }
                ]
            });
        var docs = new[]
        {
            landing,
            new DocNode("Intro", "guides/intro.md", "<p>Intro</p>", CanonicalPath: "guides/intro.md.html"),
            new DocNode("Reference", "guides/reference.md", "<p>Reference</p>", CanonicalPath: "guides/reference.md.html")
        };

        var groups = resolver.ResolveGroups(landing, docs);

        Assert.Collection(
            groups,
            first =>
            {
                Assert.Equal(string.Empty, first.Intent);
                Assert.Equal("Featured", first.Label);
            },
            second =>
            {
                Assert.Equal("manual-intent", second.Intent);
                Assert.Equal("manual-intent", second.Label);
            });
    }

    [Fact]
    public void ResolveGroups_ShouldTrimGroupFields_WhenMetadataBypassesParserNormalization()
    {
        var resolver = new DocFeaturedPageResolver(A.Fake<ILogger<DocFeaturedPageResolver>>());
        var landing = new DocNode(
            "Home",
            "README.md",
            "<p>Home</p>",
            CanonicalPath: "index.html",
            Metadata: new DocMetadata
            {
                FeaturedPageGroups =
                [
                    new DocFeaturedPageGroupDefinition
                    {
                        Intent = "  custom-intent  ",
                        Label = "  Custom label  ",
                        Summary = "  Choose this route.  ",
                        Pages =
                        [
                            new DocFeaturedPageDefinition
                            {
                                Path = "guides/intro.md"
                            }
                        ]
                    },
                    new DocFeaturedPageGroupDefinition
                    {
                        Intent = "  fallback-intent  ",
                        Label = "   ",
                        Summary = "   ",
                        Pages =
                        [
                            new DocFeaturedPageDefinition
                            {
                                Path = "guides/reference.md"
                            }
                        ]
                    }
                ]
            });
        var docs = new[]
        {
            landing,
            new DocNode("Intro", "guides/intro.md", "<p>Intro</p>", CanonicalPath: "guides/intro.md.html"),
            new DocNode("Reference", "guides/reference.md", "<p>Reference</p>", CanonicalPath: "guides/reference.md.html")
        };

        var groups = resolver.ResolveGroups(landing, docs);

        Assert.Collection(
            groups,
            first =>
            {
                Assert.Equal("custom-intent", first.Intent);
                Assert.Equal("Custom label", first.Label);
                Assert.Equal("Choose this route.", first.Summary);
            },
            second =>
            {
                Assert.Equal("fallback-intent", second.Intent);
                Assert.Equal("fallback-intent", second.Label);
                Assert.Null(second.Summary);
            });
    }

    [Fact]
    public void ResolveGroups_ShouldThrow_WhenDestinationIsMissingCanonicalPath()
    {
        var resolver = new DocFeaturedPageResolver(A.Fake<ILogger<DocFeaturedPageResolver>>());
        var landing = new DocNode(
            "Home",
            "README.md",
            "<p>Home</p>",
            CanonicalPath: "index.html",
            Metadata: new DocMetadata
            {
                FeaturedPageGroups =
                [
                    new DocFeaturedPageGroupDefinition
                    {
                        Label = "Start",
                        Pages =
                        [
                            new DocFeaturedPageDefinition
                            {
                                Path = "guides/intro.md"
                            }
                        ]
                    }
                ]
            });
        var docs = new[]
        {
            landing,
            new DocNode("Intro", "guides/intro.md", "<p>Intro</p>")
        };

        var error = Assert.Throws<InvalidOperationException>(() => resolver.ResolveGroups(landing, docs));

        Assert.Contains("missing CanonicalPath", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveGroups_ShouldIgnoreUnselectedDocsWithoutCanonicalPath()
    {
        var resolver = new DocFeaturedPageResolver(A.Fake<ILogger<DocFeaturedPageResolver>>());
        var landing = Landing(
            new DocFeaturedPageDefinition
            {
                Path = "guides/intro.md"
            });
        var intro = Doc("Intro", "guides/intro.md");
        var unrelated = new DocNode("Unrelated", "guides/unrelated.md", "<p>Unrelated</p>");

        var groups = resolver.ResolveGroups(landing, [landing, unrelated, intro]);

        var page = Assert.Single(Assert.Single(groups).Pages);
        Assert.Equal("Intro", page.Title);
    }

    private static DocNode Landing(params DocFeaturedPageDefinition[] pages)
    {
        return new DocNode(
            "Home",
            "README.md",
            "<p>Home</p>",
            CanonicalPath: "index.html",
            Metadata: new DocMetadata
            {
                FeaturedPageGroups =
                [
                    new DocFeaturedPageGroupDefinition
                    {
                        Label = "Start",
                        Pages = pages
                    }
                ]
            });
    }

    private static DocNode Doc(string title, string path, DocMetadata? metadata = null)
    {
        return new DocNode(title, path, $"<p>{title}</p>", CanonicalPath: $"{path}.html", Metadata: metadata);
    }

    private static void AssertWarningLogged(ILogger<DocFeaturedPageResolver> logger, string expectedMessageFragment)
    {
        var logged = Fake.GetCalls(logger)
            .Any(call => IsWarningLog(call, expectedMessageFragment));

        Assert.True(logged, $"Expected warning log containing '{expectedMessageFragment}'.");
    }

    private static bool IsWarningLog(FakeItEasy.Core.IFakeObjectCall call, string expectedMessageFragment)
    {
        if (call.Method.Name != nameof(ILogger.Log) || call.GetArgument<LogLevel>(0) != LogLevel.Warning)
        {
            return false;
        }

        var message = call.GetArgument<object>(2)?.ToString();
        return message?.Contains(expectedMessageFragment, StringComparison.OrdinalIgnoreCase) == true;
    }
}
