using FakeItEasy;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.Docs.ViewComponents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class SidebarViewComponentTests
{
    [Fact]
    public void Constructor_ShouldThrow_WhenAggregatorIsNull()
    {
        var options = new RazorDocsOptions();

        Assert.Throws<ArgumentNullException>(() => new SidebarViewComponent(null!, options));
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenOptionsIsNull()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var memo = new Memo(cache);
        var harvester = A.Fake<IDocHarvester>();
        var env = A.Fake<IWebHostEnvironment>();
        var sanitizer = A.Fake<IRazorDocsHtmlSanitizer>();
        var logger = A.Fake<ILogger<DocAggregator>>();
        A.CallTo(() => env.ContentRootPath).Returns(Path.GetTempPath());
        A.CallTo(() => sanitizer.Sanitize(A<string>._))
            .ReturnsLazily((string html) => html);
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(Array.Empty<DocNode>());

        var aggregator = new DocAggregator(new[] { harvester }, new RazorDocsOptions(), env, memo, sanitizer, logger);

        Assert.Throws<ArgumentNullException>(() => new SidebarViewComponent(aggregator, null!));
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenSidebarConfigurationIsNull()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var memo = new Memo(cache);
        var harvester = A.Fake<IDocHarvester>();
        var env = A.Fake<IWebHostEnvironment>();
        var sanitizer = A.Fake<IRazorDocsHtmlSanitizer>();
        var logger = A.Fake<ILogger<DocAggregator>>();
        A.CallTo(() => env.ContentRootPath).Returns(Path.GetTempPath());
        A.CallTo(() => sanitizer.Sanitize(A<string>._))
            .ReturnsLazily((string html) => html);
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(Array.Empty<DocNode>());

        var aggregator = new DocAggregator(new[] { harvester }, new RazorDocsOptions(), env, memo, sanitizer, logger);
        var options = new RazorDocsOptions { Sidebar = null! };

        Assert.Throws<ArgumentNullException>(() => new SidebarViewComponent(aggregator, options));
    }

    [Fact]
    public async Task InvokeAsync_ShouldGroupDocsByDirectory_AndOrderGroups()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Concepts", "concepts/model.md", "Concepts"),
                CreateDoc("Quickstart", "guides/start.md", "Start Here"),
                CreateDoc("Web", "Namespaces/Contoso.Product.Web", "API Reference")
            ]);
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            Assert.Equal(
                new[] { "Start Here", "Concepts", "API Reference" },
                model.Sections.Select(section => section.Label).ToArray());
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldRespectMetadataNavGroup_AndHideFlag()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Quickstart", "guides/start.md", "Start Here", order: 1),
                CreateDoc("Hidden", "guides/hidden.md", "Start Here", hideFromPublicNav: true)
            ]);
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            Assert.Equal(DocPublicSection.StartHere, section.Section);

            var group = Assert.Single(section.Groups);
            var link = Assert.Single(group.Links);
            Assert.Equal("Quickstart", link.Title);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldKeepNamespaceDocsInNamespacesGroup_WhenMetadataUsesApiReferenceNavGroup()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Web", "Namespaces/ForgeTrust.AppSurface.Web", "API Reference")
            ]);
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            Assert.Equal(DocPublicSection.ApiReference, section.Section);

            var group = Assert.Single(section.Groups);
            Assert.Equal("Web", group.Title);
            Assert.Equal("Web", Assert.Single(group.Links).Title);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldTrimMetadataNavGroup_WhenGroupingDocs()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Quickstart", "guides/start.md", " Start Here ")
            ]);
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            Assert.Equal("Start Here", section.Label);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldGroupRootNamespacesUnderNamespacesGroup()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Namespaces", "Namespaces", "API Reference"),
                CreateDoc("Web", "Namespaces/ForgeTrust.AppSurface.Web", "API Reference")
            ]);
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            Assert.Equal(DocPublicSection.ApiReference, section.Section);
            Assert.Equal("Namespaces", Assert.Single(section.Groups[0].Links).Title);
            Assert.Equal("Web", Assert.Single(section.Groups[1].Links).Title);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldKeepApiReferenceSidebarAtNamespaceLevel()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Web", "Namespaces/ForgeTrust.AppSurface.Web", "API Reference"),
                new DocNode(
                    "AspireApp",
                    "Namespaces/ForgeTrust.AppSurface.Web#AspireApp",
                    string.Empty,
                    ParentPath: "Namespaces/ForgeTrust.AppSurface.Web",
                    Metadata: new DocMetadata { NavGroup = "API Reference" })
            ]);
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            var namespaceLink = Assert.Single(Assert.Single(section.Groups).Links);
            Assert.Equal("Web", namespaceLink.Title);
            Assert.Empty(namespaceLink.Children);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldNestDeepApiNamespacesUnderParent()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Core", "Namespaces/ForgeTrust.AppSurface.Core", "API Reference"),
                CreateDoc("Defaults", "Namespaces/ForgeTrust.AppSurface.Core.Defaults", "API Reference"),
                CreateDoc("Extensions", "Namespaces/ForgeTrust.AppSurface.Core.Extensions", "API Reference"),
                CreateDoc("Aspire", "Namespaces/ForgeTrust.AppSurface.Aspire", "API Reference"),
                CreateDoc("Configuration", "Namespaces/Microsoft.Extensions.Configuration", "API Reference")
            ]);
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            var links = section.Groups.SelectMany(group => group.Links).ToList();
            var coreLink = Assert.Single(links, link => link.Title == "AppSurface.Core");

            Assert.Equal(new[] { "Defaults", "Extensions" }, coreLink.Children.Select(child => child.Title).ToArray());
            Assert.Contains(links, link => link.Title == "AppSurface.Aspire");
            Assert.DoesNotContain(links, link => link.Title == "AppSurface.Core.Defaults");
            Assert.DoesNotContain(links, link => link.Title == "AppSurface.Core.Extensions");
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldExposeConfiguredNamespacePrefixes_WhenProvided()
    {
        var options = new RazorDocsOptions
        {
            Sidebar = new RazorDocsSidebarOptions
            {
                NamespacePrefixes = [" ", "Contoso.Product.", "Contoso.Product"]
            }
        };

        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("One", "Namespaces/Contoso.Product.Feature.One", "API Reference"),
                CreateDoc("Two", "Namespaces/Contoso.Product.Feature.Two", "API Reference")
            ],
            options);
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            var featureGroup = Assert.Single(section.Groups);
            Assert.Equal("Feature", featureGroup.Title);
            Assert.Equal(new[] { "One", "Two" }, featureGroup.Links.Select(link => link.Title).ToArray());
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldDeriveSharedNamespacePrefix_WhenConfigIsMissing()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("One", "Namespaces/ForgeTrust.AppSurface.Feature.One", "API Reference"),
                CreateDoc("Two", "Namespaces/ForgeTrust.AppSurface.Feature.Two", "API Reference")
            ]);
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            Assert.Equal(new[] { "One", "Two" }, section.Groups.Select(group => group.Title).ToArray());
            Assert.Equal(new[] { "One", "Two" }, section.Groups.SelectMany(group => group.Links).Select(link => link.Title).ToArray());
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldDeriveNoPrefix_WhenNamespacesShareNoRoot()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("One", "Namespaces/Alpha.One", "API Reference"),
                CreateDoc("Two", "Namespaces/Beta.Two", "API Reference")
            ]);
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            Assert.Equal(new[] { "Alpha", "Beta" }, section.Groups.Select(group => group.Title).ToArray());
            Assert.Equal(new[] { "One", "Two" }, section.Groups.SelectMany(group => group.Links).Select(link => link.Title).ToArray());
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldDeriveNoPrefix_WhenNoNamespacesExist()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Home", "docs/readme.md", "Start Here")
            ]);
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            Assert.Equal(DocPublicSection.StartHere, section.Section);
            Assert.Equal("Home", Assert.Single(Assert.Single(section.Groups).Links).Title);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldDerivePrefixes_WhenNamespacePrefixSectionIsPresentButEmpty()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Web", "Namespaces/ForgeTrust.AppSurface.Web", "API Reference"),
                CreateDoc("Core", "Namespaces/ForgeTrust.AppSurface.Core", "API Reference")
            ],
            new RazorDocsOptions
            {
                Sidebar = new RazorDocsSidebarOptions { NamespacePrefixes = [] }
            });
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            Assert.Equal(new[] { "Core", "Web" }, section.Groups.Select(group => group.Title).ToArray());
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldDerivePrefixes_WhenNamespacePrefixSectionIsMissing()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Web", "Namespaces/ForgeTrust.AppSurface.Web", "API Reference"),
                CreateDoc("Core", "Namespaces/ForgeTrust.AppSurface.Core", "API Reference")
            ]);
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            Assert.Equal(new[] { "Core", "Web" }, section.Groups.Select(group => group.Title).ToArray());
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldMarkSectionActive_WhenCurrentPathIsSectionRoute()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Overview", "concepts/overview.md", "Concepts")
            ]);
        SetRequestPath(component, "/docs/sections/concepts");
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            Assert.Equal(DocPublicSection.Concepts, section.Section);
            Assert.True(section.IsActive);
            Assert.True(section.IsExpanded);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldNotMarkSectionActive_WhenSectionRouteIsUnknown()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Overview", "concepts/overview.md", "Concepts")
            ]);
        SetRequestPath(component, "/docs/sections/unknown-section");
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            Assert.False(section.IsActive);
            Assert.False(section.IsExpanded);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldNotMarkSectionActive_ForSearchRoutes()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Quickstart", "guides/start.md", "Start Here")
            ]);
        SetRequestPath(component, "/docs/search");
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            Assert.False(section.IsActive);
            Assert.False(section.IsExpanded);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldNotMarkSectionActive_WhenSearchRouteHasTrailingSlash()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Quickstart", "guides/start.md", "Start Here")
            ]);
        SetRequestPath(component, "/docs/search/");
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            Assert.False(section.IsActive);
            Assert.False(section.IsExpanded);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldMarkDocSectionActive_WhenCurrentPathMatchesDoc()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Overview", "concepts/overview.md", "Concepts")
            ]);
        SetRequestPath(component, "/docs/concepts/overview.md.html");
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            var link = Assert.Single(Assert.Single(section.Groups).Links);
            Assert.True(section.IsActive);
            Assert.True(section.IsExpanded);
            Assert.True(link.IsCurrent);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldMarkDocSectionActive_WhenCurrentPathMatchesDocWithTrailingSlash()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Overview", "concepts/overview.md", "Concepts")
            ]);
        SetRequestPath(component, "/docs/concepts/overview.md.html/");
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            var link = Assert.Single(Assert.Single(section.Groups).Links);
            Assert.True(section.IsActive);
            Assert.True(section.IsExpanded);
            Assert.True(link.IsCurrent);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldMarkSectionActive_ForRootMountedSectionRoutes()
    {
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/"
            }
        };
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Overview", "concepts/overview.md", "Concepts")
            ],
            options);
        SetRequestPath(component, "/sections/concepts");
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            Assert.Equal("/sections/concepts", section.Href);
            Assert.True(section.IsActive);
            Assert.True(section.IsExpanded);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldMarkSectionActive_ForRootMountedSectionRoutesWithTrailingSlash()
    {
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/"
            }
        };
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Overview", "concepts/overview.md", "Concepts")
            ],
            options);
        SetRequestPath(component, "/sections/concepts/");
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            Assert.Equal("/sections/concepts", section.Href);
            Assert.True(section.IsActive);
            Assert.True(section.IsExpanded);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldMarkDocSectionActive_ForRootMountedDocRoutes()
    {
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/"
            }
        };
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Overview", "concepts/overview.md", "Concepts")
            ],
            options);
        SetRequestPath(component, "/concepts/overview.md.html");
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            var link = Assert.Single(Assert.Single(section.Groups).Links);
            Assert.Equal("/sections/concepts", section.Href);
            Assert.Equal("/concepts/overview.md.html", link.Href);
            Assert.True(section.IsActive);
            Assert.True(section.IsExpanded);
            Assert.True(link.IsCurrent);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldMarkDocSectionActive_ForRootMountedDocRoutesWithTrailingSlash()
    {
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/"
            }
        };
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Overview", "concepts/overview.md", "Concepts")
            ],
            options);
        SetRequestPath(component, "/concepts/overview.md.html/");
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            var link = Assert.Single(Assert.Single(section.Groups).Links);
            Assert.Equal("/sections/concepts", section.Href);
            Assert.Equal("/concepts/overview.md.html", link.Href);
            Assert.True(section.IsActive);
            Assert.True(section.IsExpanded);
            Assert.True(link.IsCurrent);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldMarkStartHereSectionActive_ForRootMountedHomeRoute()
    {
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/"
            }
        };
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Quickstart", "guides/start.md", "Start Here"),
                CreateDoc("Overview", "concepts/overview.md", "Concepts")
            ],
            options);
        SetRequestPath(component, "/");
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var activeSection = Assert.Single(model.Sections, section => section.IsActive);
            Assert.Equal(DocPublicSection.StartHere, activeSection.Section);
            Assert.True(activeSection.IsExpanded);
        }
    }

    [Theory]
    [InlineData("/search")]
    [InlineData("/search/")]
    [InlineData("/search-index.json")]
    public async Task InvokeAsync_ShouldKeepSectionsInactive_ForRootMountedSearchRoutes(string requestPath)
    {
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/"
            }
        };
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Quickstart", "guides/start.md", "Start Here"),
                CreateDoc("Overview", "concepts/overview.md", "Concepts")
            ],
            options);
        SetRequestPath(component, requestPath);
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            Assert.All(model.Sections, section => Assert.False(section.IsActive));
            Assert.All(model.Sections, section => Assert.False(section.IsExpanded));
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldMarkRootMountedPlainHtmlDocRoutes_AsCurrent()
    {
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/"
            }
        };
        var (component, cache, memo) = CreateComponent(
            [
                new DocNode(
                    "Guides",
                    "guides",
                    "<p>Guides</p>",
                    CanonicalPath: "guides.html",
                    Metadata: new DocMetadata
                    {
                        NavGroup = "How-to Guides"
                    })
            ],
            options);
        SetRequestPath(component, "/guides.html");
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            var link = Assert.Single(Assert.Single(section.Groups).Links);
            Assert.True(section.IsActive);
            Assert.True(section.IsExpanded);
            Assert.True(link.IsCurrent);
            Assert.Equal("/guides.html", link.Href);
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldKeepSectionsInactive_WhenDocsPathDoesNotResolve()
    {
        var (component, cache, memo) = CreateComponent(
            [
                CreateDoc("Quickstart", "guides/start.md", "Start Here")
            ]);
        SetRequestPath(component, "/docs/missing.md.html");
        using (memo)
        using (cache)
        {
            var model = await GetModelAsync(component);

            var section = Assert.Single(model.Sections);
            Assert.False(section.IsActive);
            Assert.False(section.IsExpanded);
        }
    }

    private static DocNode CreateDoc(
        string title,
        string path,
        string navGroup,
        int? order = null,
        bool hideFromPublicNav = false)
    {
        return new DocNode(
            title,
            path,
            $"<p>{title}</p>",
            Metadata: new DocMetadata
            {
                NavGroup = navGroup,
                Order = order,
                HideFromPublicNav = hideFromPublicNav
            });
    }

    private static async Task<DocSidebarViewModel> GetModelAsync(SidebarViewComponent component)
    {
        var result = await component.InvokeAsync();
        var viewResult = Assert.IsType<ViewViewComponentResult>(result);
        return Assert.IsType<DocSidebarViewModel>(viewResult.ViewData!.Model);
    }

    private static void SetRequestPath(SidebarViewComponent component, string path)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        component.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext
            {
                HttpContext = httpContext
            }
        };
    }

    private static (SidebarViewComponent Component, MemoryCache Cache, Memo Memo) CreateComponent(
        IEnumerable<DocNode> docs,
        RazorDocsOptions? options = null)
    {
        var harvester = A.Fake<IDocHarvester>();
        var env = A.Fake<IWebHostEnvironment>();
        var sanitizer = A.Fake<IRazorDocsHtmlSanitizer>();
        var logger = A.Fake<ILogger<DocAggregator>>();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var memo = new Memo(cache);
        var docsOptions = options ?? new RazorDocsOptions();

        A.CallTo(() => env.ContentRootPath).Returns(Path.GetTempPath());
        A.CallTo(() => sanitizer.Sanitize(A<string>._))
            .ReturnsLazily((string html) => html);
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(docs.ToArray());

        var aggregator = new DocAggregator(
            new[] { harvester },
            docsOptions,
            env,
            memo,
            sanitizer,
            logger);

        var component = new SidebarViewComponent(aggregator, docsOptions);
        return (component, cache, memo);
    }
}
