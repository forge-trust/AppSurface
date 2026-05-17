using System.Text;
using ForgeTrust.AppSurface.Docs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class RazorDocsOptionsTests
{
    [Fact]
    public void PublicEnums_ShouldPreserveNumericContracts()
    {
        Assert.Equal(0, (int)RazorDocsMode.Source);
        Assert.Equal(1, (int)RazorDocsMode.Bundle);
        Assert.Equal(0, (int)RazorDocsHarvestHealthExposure.DevelopmentOnly);
        Assert.Equal(1, (int)RazorDocsHarvestHealthExposure.Always);
        Assert.Equal(2, (int)RazorDocsHarvestHealthExposure.Never);
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
        Assert.Equal(0, (int)RazorDocsLocaleRouteMode.LocalePrefix);
        Assert.Equal(0, (int)RazorDocsLocaleFallbackMode.DefaultLocaleWithNotice);
        Assert.Equal(1, (int)RazorDocsLocaleFallbackMode.Disabled);
        Assert.Equal(0, (int)RazorDocsLocaleSearchMode.ActiveLocale);
        Assert.Equal(0, (int)RazorDocsTextDirection.Ltr);
        Assert.Equal(1, (int)RazorDocsTextDirection.Rtl);
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
        Assert.Equal("razordocs.routes.reserved_collision", DocHarvestDiagnosticCodes.DocReservedRouteCollision);
        Assert.Equal("razordocs.routes.doc_collision", DocHarvestDiagnosticCodes.DocRouteCollision);
        Assert.Equal("razordocs.routes.redirect_alias_collision", DocHarvestDiagnosticCodes.DocRedirectAliasCollision);
        Assert.Equal("razordocs.routes.invalid_canonical_slug", DocHarvestDiagnosticCodes.DocInvalidCanonicalSlug);
        Assert.Equal("razordocs.routes.invalid_redirect_alias", DocHarvestDiagnosticCodes.DocInvalidRedirectAlias);
        Assert.Equal("razordocs.routes.lossy_slug_normalization", DocHarvestDiagnosticCodes.DocLossySlugNormalization);
        Assert.Equal("razordocs.localization.unsupported_locale", DocHarvestDiagnosticCodes.LocalizationUnsupportedLocale);
        Assert.Equal("razordocs.localization.missing_base", DocHarvestDiagnosticCodes.LocalizationMissingBase);
        Assert.Equal("razordocs.localization.duplicate_variant", DocHarvestDiagnosticCodes.LocalizationDuplicateVariant);
        Assert.Equal("razordocs.localization.locale_folder_conflict", DocHarvestDiagnosticCodes.LocalizationLocaleFolderConflict);
        Assert.Equal("razordocs.localization.fallback_disabled_missing_variant", DocHarvestDiagnosticCodes.LocalizationFallbackDisabledMissingVariant);
        Assert.Equal("razordocs.localization.fallback_conflict", DocHarvestDiagnosticCodes.LocalizationFallbackConflict);
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
    public void RazorDocsOptions_ShouldDefaultHarvestHealthToDevelopmentOnly()
    {
        var options = new RazorDocsOptions();

        Assert.NotNull(options.Harvest.Health);
        Assert.Equal(RazorDocsHarvestHealthExposure.DevelopmentOnly, options.Harvest.Health.ExposeRoutes);
        Assert.Equal(RazorDocsHarvestHealthExposure.DevelopmentOnly, options.Harvest.Health.ShowChrome);
    }

    [Fact]
    public void RazorDocsOptions_ShouldDefaultLocalizationToDisabledEnglish()
    {
        var options = new RazorDocsOptions();

        Assert.NotNull(options.Localization);
        Assert.False(options.Localization.Enabled);
        Assert.Equal("en", options.Localization.DefaultLocale);
        Assert.Empty(options.Localization.Locales);
        Assert.Equal(RazorDocsLocaleRouteMode.LocalePrefix, options.Localization.RouteMode);
        Assert.Equal(RazorDocsLocaleFallbackMode.DefaultLocaleWithNotice, options.Localization.FallbackMode);
        Assert.Equal(RazorDocsLocaleSearchMode.ActiveLocale, options.Localization.SearchMode);
    }

    [Fact]
    public void AddRazorDocs_ShouldBindAndNormalizeConfiguredLocalizationOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:Localization:Enabled"] = "true",
                        ["RazorDocs:Localization:DefaultLocale"] = " en ",
                        ["RazorDocs:Localization:Locales:0:Code"] = " en ",
                        ["RazorDocs:Localization:Locales:0:Label"] = " English ",
                        ["RazorDocs:Localization:Locales:0:Lang"] = " en-US ",
                        ["RazorDocs:Localization:Locales:0:Direction"] = "Ltr",
                        ["RazorDocs:Localization:Locales:0:RoutePrefix"] = " en ",
                        ["RazorDocs:Localization:Locales:1:Code"] = " fr ",
                        ["RazorDocs:Localization:Locales:1:Label"] = " Français ",
                        ["RazorDocs:Localization:Locales:1:Direction"] = "Rtl"
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.True(options.Localization.Enabled);
        Assert.Equal("en", options.Localization.DefaultLocale);
        Assert.Collection(
            options.Localization.Locales,
            en =>
            {
                Assert.Equal("en", en.Code);
                Assert.Equal("English", en.Label);
                Assert.Equal("en-US", en.Lang);
                Assert.Equal(RazorDocsTextDirection.Ltr, en.Direction);
                Assert.Equal("en", en.RoutePrefix);
            },
            fr =>
            {
                Assert.Equal("fr", fr.Code);
                Assert.Equal("Français", fr.Label);
                Assert.Equal(RazorDocsTextDirection.Rtl, fr.Direction);
            });
    }

    [Fact]
    public void AddRazorDocs_ShouldSkipNullLocaleEntriesWhileNormalizingLocalizationOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.Configure<RazorDocsOptions>(
            options =>
            {
                options.Localization.Locales =
                [
                    null!,
                    new RazorDocsLocaleOptions
                    {
                        Code = " fr ",
                        Label = " Français ",
                        Lang = " fr-FR ",
                        RoutePrefix = " français "
                    }
                ];
            });

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Null(options.Localization.Locales[0]);
        var locale = options.Localization.Locales[1];
        Assert.Equal("fr", locale.Code);
        Assert.Equal("Français", locale.Label);
        Assert.Equal("fr-FR", locale.Lang);
        Assert.Equal("français", locale.RoutePrefix);
    }

    [Fact]
    public void AddRazorDocs_ShouldRejectEnabledLocalizationWithoutLocales()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:Localization:Enabled"] = "true"
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value);

        Assert.Contains(
            ex.Failures,
            failure => failure.Contains("at least one configured locale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddRazorDocs_ShouldRejectUnsupportedLocalizationEnumsAndNullLocales()
    {
        var result = new RazorDocsOptionsValidator().Validate(
            null,
            new RazorDocsOptions
            {
                Localization = new RazorDocsLocalizationOptions
                {
                    RouteMode = (RazorDocsLocaleRouteMode)42,
                    FallbackMode = (RazorDocsLocaleFallbackMode)42,
                    SearchMode = (RazorDocsLocaleSearchMode)42,
                    Locales = null!
                }
            });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("route mode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("fallback mode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("search mode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("Localization:Locales", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddRazorDocs_ShouldRejectInvalidLocalizationLocaleDefinitions()
    {
        var result = new RazorDocsOptionsValidator().Validate(
            null,
            new RazorDocsOptions
            {
                Localization = new RazorDocsLocalizationOptions
                {
                    Enabled = true,
                    DefaultLocale = "de",
                    Locales =
                    [
                        null!,
                        new RazorDocsLocaleOptions(),
                        new RazorDocsLocaleOptions
                        {
                            Code = "not_a_culture",
                            Lang = "also_not_a_culture",
                            Direction = (RazorDocsTextDirection)42,
                            RoutePrefix = "shared"
                        },
                        new RazorDocsLocaleOptions
                        {
                            Code = "fr",
                            RoutePrefix = "shared"
                        },
                        new RazorDocsLocaleOptions
                        {
                            Code = "fr",
                            RoutePrefix = "fr-alt"
                        }
                    ]
                }
            });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("Locales:0", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("Code is required", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("valid BCP-47 culture tag", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("Direction", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("configured more than once", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("route prefix 'shared'", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("DefaultLocale must match", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddRazorDocs_ShouldRejectBlankLocalizationDefaultLocale()
    {
        var result = new RazorDocsOptionsValidator().Validate(
            null,
            new RazorDocsOptions
            {
                Localization = new RazorDocsLocalizationOptions
                {
                    Enabled = true,
                    DefaultLocale = " ",
                    Locales =
                    [
                        new RazorDocsLocaleOptions { Code = "en" }
                    ]
                }
            });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("DefaultLocale is required", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("search")]
    [InlineData("search-index.json")]
    [InlineData("_health")]
    [InlineData("_health.json")]
    [InlineData("sections")]
    [InlineData("versions")]
    [InlineData("v")]
    [InlineData("search.css")]
    [InlineData("search-client.js")]
    [InlineData("outline-client.js")]
    [InlineData("minisearch.min.js")]
    [InlineData("fr/docs")]
    [InlineData("..")]
    [InlineData("fr\\docs")]
    [InlineData("fr?docs")]
    [InlineData("fr#docs")]
    [InlineData("fr.docs")]
    public void AddRazorDocs_ShouldRejectInvalidLocalizationRoutePrefixes(string routePrefix)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:Localization:Enabled"] = "true",
                        ["RazorDocs:Localization:DefaultLocale"] = "en",
                        ["RazorDocs:Localization:Locales:0:Code"] = "en",
                        ["RazorDocs:Localization:Locales:0:RoutePrefix"] = routePrefix
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value);

        Assert.NotEmpty(ex.Failures);
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
    public void AddRazorDocs_ShouldBindConfiguredHarvestHealthOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:Harvest:Health:ExposeRoutes"] = "Always",
                        ["RazorDocs:Harvest:Health:ShowChrome"] = "Never"
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Equal(RazorDocsHarvestHealthExposure.Always, options.Harvest.Health.ExposeRoutes);
        Assert.Equal(RazorDocsHarvestHealthExposure.Never, options.Harvest.Health.ShowChrome);
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
                        ["RazorDocs:Sidebar:NamespacePrefixes:0"] = " ForgeTrust.AppSurface. ",
                        ["RazorDocs:Sidebar:NamespacePrefixes:1"] = "ForgeTrust.AppSurface.",
                        ["RazorDocs:Sidebar:NamespacePrefixes:2"] = "  "
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Equal(["ForgeTrust.AppSurface."], options.Sidebar.NamespacePrefixes);
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

        Assert.Equal("/docs", options.Routing.RouteRootPath);
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

        Assert.Equal("/docs", options.Routing.RouteRootPath);
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

        Assert.Equal("/docs/preview", options.Routing.RouteRootPath);
        Assert.Equal("/docs/preview", options.Routing.DocsRootPath);
    }

    [Fact]
    public void AddRazorDocs_ShouldDefaultDocsRootFromCustomRouteRoot_WhenVersioningIsDisabled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:Routing:RouteRootPath"] = "foo/bar"
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Equal("/foo/bar", options.Routing.RouteRootPath);
        Assert.Equal("/foo/bar", options.Routing.DocsRootPath);
    }

    [Fact]
    public void AddRazorDocs_ShouldDefaultDocsRootFromCustomRouteRoot_WhenVersioningIsEnabled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:Routing:RouteRootPath"] = "/foo/bar",
                        ["RazorDocs:Versioning:Enabled"] = "true",
                        ["RazorDocs:Versioning:CatalogPath"] = "catalog.json"
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Equal("/foo/bar", options.Routing.RouteRootPath);
        Assert.Equal("/foo/bar/next", options.Routing.DocsRootPath);
    }

    [Fact]
    public void AddRazorDocs_ShouldSupportRootRouteFamilyWithVersionedPreview()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:Routing:RouteRootPath"] = "/",
                        ["RazorDocs:Versioning:Enabled"] = "true",
                        ["RazorDocs:Versioning:CatalogPath"] = "catalog.json"
                    })
                .Build());

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Equal("/", options.Routing.RouteRootPath);
        Assert.Equal("/next", options.Routing.DocsRootPath);
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
                    "Contributor": null,
                    "Localization": null
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
        Assert.NotNull(options.Harvest.Health);
        Assert.NotNull(options.Bundle);
        Assert.NotNull(options.Sidebar);
        Assert.NotNull(options.Contributor);
        Assert.NotNull(options.Localization);
        Assert.False(options.Harvest.FailOnFailure);
        Assert.Equal(RazorDocsHarvestHealthExposure.DevelopmentOnly, options.Harvest.Health.ExposeRoutes);
        Assert.Equal(RazorDocsHarvestHealthExposure.DevelopmentOnly, options.Harvest.Health.ShowChrome);
        Assert.NotNull(options.Sidebar.NamespacePrefixes);
        Assert.Empty(options.Sidebar.NamespacePrefixes);
        Assert.False(options.Localization.Enabled);
        Assert.Equal("en", options.Localization.DefaultLocale);
        Assert.NotNull(options.Localization.Locales);
        Assert.Empty(options.Localization.Locales);
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
        harvest.Health.ShowChrome = RazorDocsHarvestHealthExposure.Never;
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
        var localization = new RazorDocsLocalizationOptions
        {
            DefaultLocale = " en ",
            Locales =
            [
                new RazorDocsLocaleOptions
                {
                    Code = " en ",
                    Label = " English ",
                    Lang = " en-US ",
                    RoutePrefix = " en "
                }
            ]
        };

        services.Configure<RazorDocsOptions>(
            options =>
            {
                options.Source = source;
                options.Harvest = harvest;
                options.Bundle = bundle;
                options.Sidebar = sidebar;
                options.Contributor = contributor;
                options.Localization = localization;
            });

        services.AddRazorDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.Same(source, options.Source);
        Assert.Same(harvest, options.Harvest);
        Assert.Same(bundle, options.Bundle);
        Assert.Same(sidebar, options.Sidebar);
        Assert.Same(contributor, options.Contributor);
        Assert.Same(localization, options.Localization);
        Assert.Equal("/tmp/configured-root", options.Source.RepositoryRoot);
        Assert.True(options.Harvest.FailOnFailure);
        Assert.Equal(RazorDocsHarvestHealthExposure.Never, options.Harvest.Health.ShowChrome);
        Assert.Equal("/tmp/docs.bundle.json", options.Bundle.Path);
        Assert.Equal(["Contoso.Product."], options.Sidebar.NamespacePrefixes);
        Assert.Equal("main", options.Contributor.DefaultBranch);
        Assert.Equal("https://example.com/blob/{branch}/{path}", options.Contributor.SourceUrlTemplate);
        Assert.Equal("https://example.com/edit/{branch}/{path}", options.Contributor.EditUrlTemplate);
        Assert.Equal("https://example.com/blob/{ref}/{path}#L{line}", options.Contributor.SymbolSourceUrlTemplate);
        Assert.Equal("abc123", options.Contributor.SourceRef);
        Assert.Equal("en", options.Localization.DefaultLocale);
        var locale = Assert.Single(options.Localization.Locales);
        Assert.Equal("en", locale.Code);
        Assert.Equal("English", locale.Label);
        Assert.Equal("en-US", locale.Lang);
        Assert.Equal("en", locale.RoutePrefix);
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
                options.Localization = null!;
            });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RazorDocsOptions>>().Value;

        Assert.NotNull(options.Source);
        Assert.NotNull(options.Harvest);
        Assert.NotNull(options.Harvest.Health);
        Assert.NotNull(options.Bundle);
        Assert.NotNull(options.Sidebar);
        Assert.NotNull(options.Contributor);
        Assert.NotNull(options.Localization);
        Assert.False(options.Harvest.FailOnFailure);
        Assert.Equal(RazorDocsHarvestHealthExposure.DevelopmentOnly, options.Harvest.Health.ExposeRoutes);
        Assert.Equal(RazorDocsHarvestHealthExposure.DevelopmentOnly, options.Harvest.Health.ShowChrome);
        Assert.NotNull(options.Sidebar.NamespacePrefixes);
        Assert.Empty(options.Sidebar.NamespacePrefixes);
        Assert.False(options.Localization.Enabled);
        Assert.Equal("en", options.Localization.DefaultLocale);
        Assert.NotNull(options.Localization.Locales);
        Assert.Empty(options.Localization.Locales);
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
        Assert.Equal("/docs", options.Routing.RouteRootPath);
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
    public void Validator_ShouldRejectNullHarvestHealthOptions()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Harvest = new RazorDocsHarvestOptions
            {
                Health = null!
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Harvest:Health must not be null", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validator_ShouldRejectUnsupportedHarvestHealthExposureValues(bool invalidRoutes)
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Harvest = new RazorDocsHarvestOptions
            {
                Health = new RazorDocsHarvestHealthOptions
                {
                    ExposeRoutes = invalidRoutes
                        ? (RazorDocsHarvestHealthExposure)999
                        : RazorDocsHarvestHealthExposure.DevelopmentOnly,
                    ShowChrome = invalidRoutes
                        ? RazorDocsHarvestHealthExposure.DevelopmentOnly
                        : (RazorDocsHarvestHealthExposure)999
                }
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        var expectedMessage = invalidRoutes
            ? "Unsupported RazorDocs harvest health route exposure mode"
            : "Unsupported RazorDocs harvest health chrome exposure mode";
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase));
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
            Versioning = null!,
            Localization = null!
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Source must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Bundle must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Sidebar must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Contributor must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Routing must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Versioning must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Localization must not be null.", StringComparison.OrdinalIgnoreCase));
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
    public void Validator_ShouldRequireBundlePath_WhenBundleModeBundleOptionsAreMissing()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Mode = RazorDocsMode.Bundle,
            Bundle = null!
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Bundle must not be null", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("requires RazorDocs:Bundle:Path", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("not implemented", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldStillReportVersioningFailures_WhenRoutingCannotNormalize()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                RouteRootPath = "https://example.com/foo/bar"
            },
            Versioning = new RazorDocsVersioningOptions
            {
                Enabled = true,
                CatalogPath = "catalog.json"
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("RouteRootPath", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Failures, failure => failure.Contains("cannot use the route-family root", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Failures, failure => failure.Contains("reserved archive or exact-version child", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldNormalizeRoutingBeforeReportingMissingVersioningOptions()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                RouteRootPath = "/foo/bar",
                DocsRootPath = "/foo/bar/next"
            },
            Versioning = null!
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("RazorDocs:Versioning must not be null", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Failures, failure => failure.Contains("RouteRootPath", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Failures, failure => failure.Contains("DocsRootPath", StringComparison.OrdinalIgnoreCase));
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
        Assert.Contains(result.Failures, failure => failure.Contains("cannot use the route-family root", StringComparison.OrdinalIgnoreCase));
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
                failure => failure.Contains("reserved archive or exact-version child", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Validator_ShouldAllowRootMountedDocsRootPath_WhenVersioningIsDisabled()
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
                Enabled = false
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.False(result.Failed);
    }

    [Fact]
    public void Validator_ShouldAllowRootRouteFamilyWithVersionedPreview()
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                RouteRootPath = "/",
                DocsRootPath = "/next"
            },
            Versioning = new RazorDocsVersioningOptions
            {
                Enabled = true,
                CatalogPath = "catalog.json"
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.False(result.Failed);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("/docs/")]
    [InlineData("https://example.com/docs")]
    [InlineData("//example.com/docs")]
    [InlineData("/docs?view=full")]
    [InlineData("/docs#top")]
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
            failure => failure.Contains("DocsRootPath must be an app-relative path", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("/foo/bar/")]
    [InlineData("https://example.com/foo")]
    [InlineData("//example.com/foo")]
    [InlineData("/foo/bar?view=full")]
    [InlineData("/foo/bar#top")]
    [InlineData("/foo/bar/versions")]
    [InlineData("/foo/bar/v")]
    [InlineData(" foo/bar/v ")]
    public void Validator_ShouldRejectInvalidRouteRootPaths(string routeRootPath)
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                RouteRootPath = routeRootPath
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("RouteRootPath", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("foo/bar", "foo/bar/next")]
    [InlineData(" foo/bar ", " foo/bar/next ")]
    public void Validator_ShouldAllowRelativeLookingRouteRoots(string routeRootPath, string docsRootPath)
    {
        var validator = new RazorDocsOptionsValidator();
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                RouteRootPath = routeRootPath,
                DocsRootPath = docsRootPath
            },
            Versioning = new RazorDocsVersioningOptions
            {
                Enabled = true,
                CatalogPath = "catalog.json"
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.False(result.Failed);
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
