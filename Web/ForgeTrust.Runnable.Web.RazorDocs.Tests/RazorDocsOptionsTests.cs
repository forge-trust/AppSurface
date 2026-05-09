using System.Text;
using ForgeTrust.Runnable.Web.RazorDocs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

public sealed class RazorDocsOptionsTests
{
    [Fact]
    public void PublicEnums_ShouldPreserveNumericContracts()
    {
        Assert.Equal(0, (int)RazorDocsMode.Source);
        Assert.Equal(1, (int)RazorDocsMode.Bundle);
        Assert.Equal(0, (int)RazorDocsLastUpdatedMode.None);
        Assert.Equal(1, (int)RazorDocsLastUpdatedMode.Git);
        Assert.Equal(0, (int)RazorDocsVersionSupportState.Current);
        Assert.Equal(1, (int)RazorDocsVersionSupportState.Maintained);
        Assert.Equal(2, (int)RazorDocsVersionSupportState.Deprecated);
        Assert.Equal(3, (int)RazorDocsVersionSupportState.Archived);
        Assert.Equal(0, (int)RazorDocsVersionVisibility.Public);
        Assert.Equal(1, (int)RazorDocsVersionVisibility.Hidden);
        Assert.Equal(0, (int)RazorDocsVersionAdvisoryState.None);
        Assert.Equal(1, (int)RazorDocsVersionAdvisoryState.Vulnerable);
        Assert.Equal(2, (int)RazorDocsVersionAdvisoryState.SecurityRisk);
        Assert.Equal(0, (int)DocHarvestHealthStatus.Healthy);
        Assert.Equal(1, (int)DocHarvestHealthStatus.Empty);
        Assert.Equal(2, (int)DocHarvestHealthStatus.Degraded);
        Assert.Equal(3, (int)DocHarvestHealthStatus.Failed);
        Assert.Equal(0, (int)DocHarvesterHealthStatus.Succeeded);
        Assert.Equal(1, (int)DocHarvesterHealthStatus.ReturnedEmpty);
        Assert.Equal(2, (int)DocHarvesterHealthStatus.Failed);
        Assert.Equal(3, (int)DocHarvesterHealthStatus.TimedOut);
        Assert.Equal(4, (int)DocHarvesterHealthStatus.Canceled);
        Assert.Equal(0, (int)DocHarvestDiagnosticSeverity.Information);
        Assert.Equal(1, (int)DocHarvestDiagnosticSeverity.Warning);
        Assert.Equal(2, (int)DocHarvestDiagnosticSeverity.Error);
        Assert.Equal(3, (int)DocHarvestDiagnosticSeverity.Critical);
    }

    [Fact]
    public void DocHarvestDiagnosticCodes_ShouldPreserveStringContracts()
    {
        Assert.Equal("razordocs.harvest.harvester_timed_out", DocHarvestDiagnosticCodes.HarvesterTimedOut);
        Assert.Equal("razordocs.harvest.harvester_canceled", DocHarvestDiagnosticCodes.HarvesterCanceled);
        Assert.Equal("razordocs.harvest.harvester_failed", DocHarvestDiagnosticCodes.HarvesterFailed);
        Assert.Equal("razordocs.harvest.no_harvesters", DocHarvestDiagnosticCodes.NoHarvesters);
        Assert.Equal("razordocs.harvest.all_failed", DocHarvestDiagnosticCodes.AllFailed);
    }

    [Fact]
    public void AddRazorDocs_ShouldFallbackToLegacyRepositoryRootSetting()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RepositoryRoot"] = "/tmp/repo-root"
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Equal("/tmp/repo-root", options.Source.RepositoryRoot);
    }

    [Fact]
    public void RazorDocsOptions_ShouldDefaultCacheExpirationToFiveMinutes()
    {
        var options = new RazorDocsOptions();

        Assert.Equal(5, options.CacheExpirationMinutes);
        Assert.Equal(1d / 60d, RazorDocsOptions.MinCacheExpirationMinutes);
        Assert.Equal((int.MaxValue - 1) / 60d, RazorDocsOptions.MaxCacheExpirationMinutes);
    }

    [Fact]
    public void RazorDocsOptions_ShouldDefaultHarvestFailOnFailureToFalse()
    {
        var options = new RazorDocsOptions();

        Assert.NotNull(options.Harvest);
        Assert.False(options.Harvest.FailOnFailure);
    }

    [Fact]
    public void AddRazorDocs_ShouldBindConfiguredHarvestFailOnFailure()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:Harvest:FailOnFailure"] = "true"
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.True(options.Harvest.FailOnFailure);
    }

    [Fact]
    public void AddRazorDocs_ShouldBindConfiguredCacheExpirationMinutes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:CacheExpirationMinutes"] = "0.5"
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Equal(0.5, options.CacheExpirationMinutes);
    }

    [Fact]
    public void AddRazorDocs_ShouldRejectInvalidConfiguredCacheExpirationMinutes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:CacheExpirationMinutes"] = "-1"
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value);

        Assert.Contains(
            ex.Failures,
            failure => failure.Contains("CacheExpirationMinutes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddRazorDocs_ShouldTrimAndDeduplicateConfiguredNamespacePrefixes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:Sidebar:NamespacePrefixes:0"] = " ForgeTrust.Runnable. ",
                        ["RazorDocs:Sidebar:NamespacePrefixes:1"] = "ForgeTrust.Runnable.",
                        ["RazorDocs:Sidebar:NamespacePrefixes:2"] = "  "
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Equal(["ForgeTrust.Runnable."], options.Sidebar.NamespacePrefixes);
    }

    [Fact]
    public void AddRazorDocs_ShouldTrimConfiguredBundlePath()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:Bundle:Path"] = " /tmp/docs.bundle.json "
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Equal("/tmp/docs.bundle.json", options.Bundle.Path);
    }

    [Fact]
    public void AddRazorDocs_ShouldTrimConfiguredContributorOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:Contributor:DefaultBranch"] = " main ",
                        ["RazorDocs:Contributor:SourceUrlTemplate"] = " https://example.com/blob/{branch}/{path} ",
                        ["RazorDocs:Contributor:EditUrlTemplate"] = " https://example.com/edit/{branch}/{path} ",
                        ["RazorDocs:Contributor:SymbolSourceUrlTemplate"] = " https://example.com/blob/{ref}/{path}#L{line} ",
                        ["RazorDocs:Contributor:SourceRef"] = " abc123 "
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Equal("main", options.Contributor.DefaultBranch);
        Assert.Equal("https://example.com/blob/{branch}/{path}", options.Contributor.SourceUrlTemplate);
        Assert.Equal("https://example.com/edit/{branch}/{path}", options.Contributor.EditUrlTemplate);
        Assert.Equal("https://example.com/blob/{ref}/{path}#L{line}", options.Contributor.SymbolSourceUrlTemplate);
        Assert.Equal("abc123", options.Contributor.SourceRef);
    }

    [Fact]
    public void AddRazorDocs_ShouldDefaultDocsRootToDocs_WhenVersioningIsDisabled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Equal("/docs", options.Routing.DocsRootPath);
    }

    [Fact]
    public void AddRazorDocs_ShouldDefaultDocsRootToDocsNext_WhenVersioningIsEnabled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:Versioning:Enabled"] = "true",
                        ["RazorDocs:Versioning:CatalogPath"] = "catalog.json"
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Equal("/docs/next", options.Routing.DocsRootPath);
    }

    [Fact]
    public void AddRazorDocs_ShouldNormalizeRelativeDocsRootToAppRelativePath()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:Routing:DocsRootPath"] = "docs/preview"
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Equal("/docs/preview", options.Routing.DocsRootPath);
    }

    [Fact]
    public void ContributorOptions_ShouldDefaultLastUpdatedModeToNone()
    {
        var options = new RazorDocsContributorOptions();

        Assert.True(options.Enabled);
        Assert.Equal(RazorDocsLastUpdatedMode.None, options.LastUpdatedMode);
    }

    [Fact]
    public void AddRazorDocs_ShouldRejectExplicitWhitespaceSourceRepositoryRoot()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:Source:RepositoryRoot"] = "   ",
                        ["RepositoryRoot"] = "/tmp/legacy-root"
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value);

        Assert.Contains(
            ex.Failures,
            failure => failure.Contains("RepositoryRoot cannot be whitespace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddRazorDocs_ShouldAllowRootMountedDocsSurface()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:Routing:DocsRootPath"] = "/"
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Equal("/", options.Routing.DocsRootPath);
    }

    [Fact]
    public void AddRazorDocs_ShouldRehydrateNullNestedOptionsObjects()
    {
        var services = new ServiceCollection();
        using var configStream = new MemoryStream(
            Encoding.UTF8.GetBytes(
                """
                {
                  "RazorDocs": {
                    "Source": null,
                    "Harvest": null,
                    "Bundle": null,
                    "Sidebar": null,
                    "Contributor": null
                  }
                }
                """));
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddJsonStream(configStream)
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.NotNull(options.Source);
        Assert.NotNull(options.Harvest);
        Assert.NotNull(options.Bundle);
        Assert.NotNull(options.Sidebar);
        Assert.NotNull(options.Contributor);
        Assert.False(options.Harvest.FailOnFailure);
        Assert.NotNull(options.Sidebar.NamespacePrefixes);
        Assert.Empty(options.Sidebar.NamespacePrefixes);
    }

    [Fact]
    public void AddRazorDocs_ShouldPreserveExistingNestedOptionsObjects()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RepositoryRoot"] = "/tmp/legacy-root"
                    })
                .Build());

        var source = new RazorDocsSourceOptions { RepositoryRoot = " /tmp/configured-root " };
        var harvest = new RazorDocsHarvestOptions { FailOnFailure = true };
        var bundle = new RazorDocsBundleOptions { Path = " /tmp/docs.bundle.json " };
        var sidebar = new RazorDocsSidebarOptions
        {
            NamespacePrefixes = [" Contoso.Product. ", "contoso.product.", " "]
        };
        var contributor = new RazorDocsContributorOptions
        {
            DefaultBranch = " main ",
            SourceUrlTemplate = " https://example.com/blob/{branch}/{path} ",
            EditUrlTemplate = " https://example.com/edit/{branch}/{path} ",
            SymbolSourceUrlTemplate = " https://example.com/blob/{ref}/{path}#L{line} ",
            SourceRef = " abc123 "
        };

        services.Configure<RazorDocsOptions>(
            options =>
            {
                options.Source = source;
                options.Harvest = harvest;
                options.Bundle = bundle;
                options.Sidebar = sidebar;
                options.Contributor = contributor;
            });

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Same(source, options.Source);
        Assert.Same(harvest, options.Harvest);
        Assert.Same(bundle, options.Bundle);
        Assert.Same(sidebar, options.Sidebar);
        Assert.Same(contributor, options.Contributor);
        Assert.Equal("/tmp/configured-root", options.Source.RepositoryRoot);
        Assert.True(options.Harvest.FailOnFailure);
        Assert.Equal("/tmp/docs.bundle.json", options.Bundle.Path);
        Assert.Equal(["Contoso.Product."], options.Sidebar.NamespacePrefixes);
        Assert.Equal("main", options.Contributor.DefaultBranch);
        Assert.Equal("https://example.com/blob/{branch}/{path}", options.Contributor.SourceUrlTemplate);
        Assert.Equal("https://example.com/edit/{branch}/{path}", options.Contributor.EditUrlTemplate);
        Assert.Equal("https://example.com/blob/{ref}/{path}#L{line}", options.Contributor.SymbolSourceUrlTemplate);
        Assert.Equal("abc123", options.Contributor.SourceRef);
    }

    [Fact]
    public void AddRazorDocs_ShouldRehydrateExplicitlyNullNestedOptionsObjects()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddRazorDocs();
        services.Configure<RazorDocsOptions>(
            options =>
            {
                options.Source = null!;
                options.Harvest = null!;
                options.Bundle = null!;
                options.Sidebar = null!;
                options.Contributor = null!;
            });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.NotNull(options.Source);
        Assert.NotNull(options.Harvest);
        Assert.NotNull(options.Bundle);
        Assert.NotNull(options.Sidebar);
        Assert.NotNull(options.Contributor);
        Assert.False(options.Harvest.FailOnFailure);
        Assert.NotNull(options.Sidebar.NamespacePrefixes);
        Assert.Empty(options.Sidebar.NamespacePrefixes);
    }

    [Fact]
    public void AddRazorDocs_ShouldRehydrateNullRoutingAndVersioningOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddRazorDocs();
        services.Configure<RazorDocsOptions>(
            options =>
            {
                options.Routing = null!;
                options.Versioning = null!;
            });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.NotNull(options.Routing);
        Assert.NotNull(options.Versioning);
        Assert.Equal("/docs", options.Routing.DocsRootPath);
        Assert.False(options.Versioning.Enabled);
    }

    [Fact]
    public void AddRazorDocs_ShouldRehydrateExplicitlyNullNamespacePrefixes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddRazorDocs();
        services.Configure<RazorDocsOptions>(
            options =>
            {
                options.Sidebar = new RazorDocsSidebarOptions
                {
                    NamespacePrefixes = null!
                };
            });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.NotNull(options.Sidebar);
        Assert.NotNull(options.Sidebar.NamespacePrefixes);
        Assert.Empty(options.Sidebar.NamespacePrefixes);
    }

    [Fact]
    public void Validator_ShouldRejectNullHarvestOptions()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Harvest = null!
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Harvest must not be null", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRejectUnsupportedModeValue()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Mode = (RazorDocsMode)999
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("Unsupported RazorDocs mode", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    [InlineData(double.Epsilon)]
    [InlineData(0.0001)]
    [InlineData(0.333)]
    [InlineData(double.MaxValue)]
    [InlineData(35791395)]
    public void Validator_ShouldRejectInvalidCacheExpirationMinutes(double cacheExpirationMinutes)
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            CacheExpirationMinutes = cacheExpirationMinutes
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "CacheExpirationMinutes must be a finite number between",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldAllowWholeSecondCacheExpirationMinutes()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            CacheExpirationMinutes = 0.1,
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/docs"
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.False(result.Failed);
    }

    [Fact]
    public void Validator_ShouldAllowMinimumCacheExpirationMinutes()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            CacheExpirationMinutes = RazorDocsOptions.MinCacheExpirationMinutes,
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/docs"
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.False(result.Failed);
    }

    [Fact]
    public void Validator_ShouldAllowMaximumCacheExpirationMinutes()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            CacheExpirationMinutes = RazorDocsOptions.MaxCacheExpirationMinutes,
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/docs"
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.False(result.Failed);
    }

    [Fact]
    public void Validator_ShouldRejectNullNestedOptionObjects()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Source = null!,
            Bundle = null!,
            Sidebar = null!,
            Contributor = null!,
            Routing = null!,
            Versioning = null!
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Source must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Bundle must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Sidebar must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Contributor must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Routing must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Versioning must not be null.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRejectNullNamespacePrefixes()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Sidebar = new RazorDocsSidebarOptions
            {
                NamespacePrefixes = null!
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("NamespacePrefixes must not be null", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRequireBundlePath_WhenBundleModePathIsMissing()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Mode = RazorDocsMode.Bundle,
            Bundle = new RazorDocsBundleOptions { Path = "   " }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("requires RazorDocs:Bundle:Path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRejectWhitespaceSourceRepositoryRoot()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Source = new RazorDocsSourceOptions { RepositoryRoot = "   " }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("RepositoryRoot cannot be whitespace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRejectBundleModeBeforeSliceTwo()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Mode = RazorDocsMode.Bundle,
            Bundle = new RazorDocsBundleOptions { Path = "/tmp/docs.bundle.json" }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("not implemented", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRejectDocsRootAtDocs_WhenVersioningIsEnabled()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/docs"
            },
            Versioning = new RazorDocsVersioningOptions
            {
                Enabled = true,
                CatalogPath = "catalog.json"
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("cannot use '/docs' as the live source docs root", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRejectReservedVersioningPreviewPath()
    {
        var validator = new RazorDocsOptionsValidator();
        foreach (var docsRootPath in new[] { "/docs/v", "/docs/v/1.0.0", "/docs/versions" })
        {
            var options = new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    DocsRootPath = docsRootPath
                },
                Versioning = new RazorDocsVersioningOptions
                {
                    Enabled = true,
                    CatalogPath = "catalog.json"
                }
            };

            var result = validator.Validate(Options.DefaultName, options);

            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures,
                failure => failure.Contains("reserved versioning path", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validator_ShouldAllowRootMountedDocsRootPath(bool versioningEnabled)
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/"
            },
            Versioning = new RazorDocsVersioningOptions
            {
                Enabled = versioningEnabled,
                CatalogPath = versioningEnabled ? "catalog.json" : null
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.False(result.Failed);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("guides")]
    [InlineData("/docsx")]
    [InlineData("/docs-preview")]
    [InlineData("/docs/")]
    public void Validator_ShouldRejectInvalidDocsRootPaths(string docsRootPath)
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = docsRootPath
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("DocsRootPath must be exactly '/' or start with '/docs'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRequireCatalogPath_WhenVersioningIsEnabled()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/docs/next"
            },
            Versioning = new RazorDocsVersioningOptions
            {
                Enabled = true,
                CatalogPath = " "
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("requires RazorDocs:Versioning:CatalogPath", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRequireDefaultBranch_WhenContributorTemplatesAreConfigured()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Contributor = new RazorDocsContributorOptions
            {
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                EditUrlTemplate = "https://example.com/edit/{branch}/{path}",
                DefaultBranch = "   "
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("DefaultBranch is required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRequirePathToken_WhenSourceTemplateIsConfigured()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Contributor = new RazorDocsContributorOptions
            {
                DefaultBranch = "main",
                SourceUrlTemplate = "https://example.com/blob/{branch}/docs-index"
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("SourceUrlTemplate must contain the {path} token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRequirePathToken_WhenEditTemplateIsConfigured()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Contributor = new RazorDocsContributorOptions
            {
                DefaultBranch = "main",
                EditUrlTemplate = "https://example.com/edit/{branch}/docs-index"
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("EditUrlTemplate must contain the {path} token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRequirePathAndLineTokens_WhenSymbolSourceTemplateIsConfigured()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Contributor = new RazorDocsContributorOptions
            {
                SymbolSourceUrlTemplate = "https://example.com/blob/{ref}/docs-index"
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("SymbolSourceUrlTemplate must contain the {path} token", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("SymbolSourceUrlTemplate must contain the {line} token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRequireSourceRefOrDefaultBranch_WhenSymbolSourceTemplateUsesRef()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Contributor = new RazorDocsContributorOptions
            {
                SymbolSourceUrlTemplate = "https://example.com/blob/{ref}/{path}#L{line}"
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("SourceRef or DefaultBranch is required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRequireDefaultBranch_WhenSymbolSourceTemplateUsesBranch()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Contributor = new RazorDocsContributorOptions
            {
                SymbolSourceUrlTemplate = "https://example.com/blob/{branch}/{path}#L{line}"
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("DefaultBranch is required when SymbolSourceUrlTemplate contains the {branch} token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldAllowSymbolSourceTemplate_WhenBranchTokenHasDefaultBranch()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/docs"
            },
            Contributor = new RazorDocsContributorOptions
            {
                DefaultBranch = "main",
                SymbolSourceUrlTemplate = "https://example.com/blob/{branch}/{path}#L{line}"
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.False(result.Failed);
    }

    [Fact]
    public void Validator_ShouldAllowSymbolSourceTemplate_WhenRefTokenHasSourceRef()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/docs"
            },
            Contributor = new RazorDocsContributorOptions
            {
                SourceRef = "abc123",
                SymbolSourceUrlTemplate = "https://example.com/blob/{ref}/{path}#L{line}"
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.False(result.Failed);
    }

    [Fact]
    public void Validator_ShouldRejectUnsupportedSymbolSourceTemplateTokens()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Contributor = new RazorDocsContributorOptions
            {
                SourceRef = "abc123",
                SymbolSourceUrlTemplate = "https://example.com/blob/{commit}/{path}#L{linen}"
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("unsupported token(s): {commit}, {linen}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldSkipContributorTemplateValidation_WhenContributorRenderingIsDisabled()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/docs"
            },
            Contributor = new RazorDocsContributorOptions
            {
                Enabled = false,
                DefaultBranch = "   ",
                SourceUrlTemplate = "https://example.com/blob/{branch}/docs-index",
                EditUrlTemplate = "https://example.com/edit/{branch}/docs-index",
                SymbolSourceUrlTemplate = "https://example.com/blob/{ref}/docs-index"
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.False(result.Failed);
    }

    [Fact]
    public void Validator_ShouldRejectUnsupportedContributorLastUpdatedMode()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Contributor = new RazorDocsContributorOptions
            {
                LastUpdatedMode = (RazorDocsLastUpdatedMode)999
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("Unsupported RazorDocs contributor last-updated mode", StringComparison.OrdinalIgnoreCase));
    }
}
