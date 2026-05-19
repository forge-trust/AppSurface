using System.Text;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Docs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsOptionsTests
{
    [Fact]
    public void PublicEnums_ShouldPreserveNumericContracts()
    {
        Assert.Equal(0, (int)AppSurfaceDocsMode.Source);
        Assert.Equal(1, (int)AppSurfaceDocsMode.Bundle);
        Assert.Equal(0, (int)AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly);
        Assert.Equal(1, (int)AppSurfaceDocsHarvestHealthExposure.Always);
        Assert.Equal(2, (int)AppSurfaceDocsHarvestHealthExposure.Never);
        Assert.Equal(0, (int)AppSurfaceDocsLastUpdatedMode.None);
        Assert.Equal(1, (int)AppSurfaceDocsLastUpdatedMode.Git);
        Assert.Equal(0, (int)AppSurfaceDocsVersionSupportState.Current);
        Assert.Equal(1, (int)AppSurfaceDocsVersionSupportState.Maintained);
        Assert.Equal(2, (int)AppSurfaceDocsVersionSupportState.Deprecated);
        Assert.Equal(3, (int)AppSurfaceDocsVersionSupportState.Archived);
        Assert.Equal(0, (int)AppSurfaceDocsVersionVisibility.Public);
        Assert.Equal(1, (int)AppSurfaceDocsVersionVisibility.Hidden);
        Assert.Equal(0, (int)AppSurfaceDocsVersionAdvisoryState.None);
        Assert.Equal(1, (int)AppSurfaceDocsVersionAdvisoryState.Vulnerable);
        Assert.Equal(2, (int)AppSurfaceDocsVersionAdvisoryState.SecurityRisk);
        Assert.Equal(0, (int)DocHarvestHealthStatus.Healthy);
        Assert.Equal(1, (int)DocHarvestHealthStatus.Empty);
        Assert.Equal(2, (int)DocHarvestHealthStatus.Degraded);
        Assert.Equal(3, (int)DocHarvestHealthStatus.Failed);
        Assert.Equal(0, (int)AppSurfaceDocsLocaleRouteMode.LocalePrefix);
        Assert.Equal(0, (int)AppSurfaceDocsLocaleFallbackMode.DefaultLocaleWithNotice);
        Assert.Equal(1, (int)AppSurfaceDocsLocaleFallbackMode.Disabled);
        Assert.Equal(0, (int)AppSurfaceDocsLocaleSearchMode.ActiveLocale);
        Assert.Equal(0, (int)AppSurfaceDocsTextDirection.Ltr);
        Assert.Equal(1, (int)AppSurfaceDocsTextDirection.Rtl);
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
        Assert.Equal("appsurfacedocs.harvest.harvester_timed_out", DocHarvestDiagnosticCodes.HarvesterTimedOut);
        Assert.Equal("appsurfacedocs.harvest.harvester_canceled", DocHarvestDiagnosticCodes.HarvesterCanceled);
        Assert.Equal("appsurfacedocs.harvest.harvester_failed", DocHarvestDiagnosticCodes.HarvesterFailed);
        Assert.Equal("appsurfacedocs.harvest.no_harvesters", DocHarvestDiagnosticCodes.NoHarvesters);
        Assert.Equal("appsurfacedocs.harvest.all_failed", DocHarvestDiagnosticCodes.AllFailed);
        Assert.Equal("appsurfacedocs.routes.reserved_collision", DocHarvestDiagnosticCodes.DocReservedRouteCollision);
        Assert.Equal("appsurfacedocs.routes.doc_collision", DocHarvestDiagnosticCodes.DocRouteCollision);
        Assert.Equal("appsurfacedocs.routes.redirect_alias_collision", DocHarvestDiagnosticCodes.DocRedirectAliasCollision);
        Assert.Equal("appsurfacedocs.routes.invalid_canonical_slug", DocHarvestDiagnosticCodes.DocInvalidCanonicalSlug);
        Assert.Equal("appsurfacedocs.routes.invalid_redirect_alias", DocHarvestDiagnosticCodes.DocInvalidRedirectAlias);
        Assert.Equal("appsurfacedocs.routes.lossy_slug_normalization", DocHarvestDiagnosticCodes.DocLossySlugNormalization);
        Assert.Equal("appsurfacedocs.localization.unsupported_locale", DocHarvestDiagnosticCodes.LocalizationUnsupportedLocale);
        Assert.Equal("appsurfacedocs.localization.missing_base", DocHarvestDiagnosticCodes.LocalizationMissingBase);
        Assert.Equal("appsurfacedocs.localization.duplicate_variant", DocHarvestDiagnosticCodes.LocalizationDuplicateVariant);
        Assert.Equal("appsurfacedocs.localization.locale_folder_conflict", DocHarvestDiagnosticCodes.LocalizationLocaleFolderConflict);
        Assert.Equal("appsurfacedocs.localization.fallback_disabled_missing_variant", DocHarvestDiagnosticCodes.LocalizationFallbackDisabledMissingVariant);
        Assert.Equal("appsurfacedocs.localization.fallback_conflict", DocHarvestDiagnosticCodes.LocalizationFallbackConflict);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldRejectNullServiceCollection()
    {
        IServiceCollection services = null!;

        Assert.Throws<ArgumentNullException>(() => services.AddAppSurfaceDocs());
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldIgnoreLegacyTopLevelRepositoryRootSetting()
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

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.Null(options.Source.RepositoryRoot);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldIgnoreLegacyRazorDocsConfigurationRoot()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RazorDocs:Harvest:FailOnFailure"] = "true",
                        ["RazorDocs:Source:RepositoryRoot"] = "/tmp/legacy-root"
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.False(options.Harvest.FailOnFailure);
        Assert.Null(options.Source.RepositoryRoot);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldNormalizeIdentityOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Identity:DisplayName"] = "  Acme Docs  ",
                        ["AppSurfaceDocs:Identity:HomeHref"] = "  ~/docs  ",
                        ["AppSurfaceDocs:Identity:Wordmark:HighlightText"] = "  Docs  ",
                        ["AppSurfaceDocs:Identity:Wordmark:HighlightColor"] = "  #3B82F6  ",
                        ["AppSurfaceDocs:Identity:Logo:Path"] = "  /brand/logo.svg  ",
                        ["AppSurfaceDocs:Identity:Logo:AltText"] = "  Acme mark  ",
                        ["AppSurfaceDocs:Identity:Favicon:SvgPath"] = "  /brand/favicon.svg  ",
                        ["AppSurfaceDocs:Identity:Favicon:IcoPath"] = "  ~/favicon.ico  ",
                        ["AppSurfaceDocs:Identity:Favicon:PngPath"] = "  /brand/favicon.png  "
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.Equal("Acme Docs", options.Identity.DisplayName);
        Assert.Equal("~/docs", options.Identity.HomeHref);
        Assert.Equal("Docs", options.Identity.Wordmark.HighlightText);
        Assert.Equal("#3b82f6", options.Identity.Wordmark.HighlightColor);
        Assert.Equal("/brand/logo.svg", options.Identity.Logo.Path);
        Assert.Equal("Acme mark", options.Identity.Logo.AltText);
        Assert.Equal("/brand/favicon.svg", options.Identity.Favicon.SvgPath);
        Assert.Equal("~/favicon.ico", options.Identity.Favicon.IcoPath);
        Assert.Equal("/brand/favicon.png", options.Identity.Favicon.PngPath);
    }

    [Theory]
    [InlineData("AppSurfaceDocs:Identity:Wordmark:HighlightColor", "blue", "CSS hex color")]
    [InlineData("AppSurfaceDocs:Identity:Wordmark:HighlightColor", "var(--brand)", "CSS hex color")]
    [InlineData("AppSurfaceDocs:Identity:Wordmark:HighlightColor", "#12345g", "CSS hex color")]
    public void AddAppSurfaceDocs_ShouldRejectInvalidWordmarkHighlightColors(
        string key,
        string value,
        string expectedFailureFragment)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Identity:DisplayName"] = "Acme Docs",
                        ["AppSurfaceDocs:Identity:Wordmark:HighlightText"] = "Docs",
                        [key] = value
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value);

        Assert.Contains(ex.Failures, failure => failure.Contains(expectedFailureFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldRejectWordmarkHighlightColorWithoutHighlightText()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Identity:DisplayName"] = "Acme Docs",
                        ["AppSurfaceDocs:Identity:Wordmark:HighlightColor"] = "#3b82f6"
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value);

        Assert.Contains(
            ex.Failures,
            failure => failure.Contains("HighlightColor requires AppSurfaceDocs:Identity:Wordmark:HighlightText", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldRejectWordmarkHighlightTextOutsideResolvedDisplayName()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Identity:DisplayName"] = "Acme Docs",
                        ["AppSurfaceDocs:Identity:Wordmark:HighlightText"] = "Platform"
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value);

        Assert.Contains(
            ex.Failures,
            failure => failure.Contains("HighlightText must match part of the resolved", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("AppSurfaceDocs:Identity:Logo:Path", "logo.svg")]
    [InlineData("AppSurfaceDocs:Identity:Logo:Path", "https://example.com/logo.svg")]
    [InlineData("AppSurfaceDocs:Identity:Logo:Path", "//example.com/logo.svg")]
    [InlineData("AppSurfaceDocs:Identity:Logo:Path", "data:image/svg+xml,abc")]
    [InlineData("AppSurfaceDocs:Identity:Logo:Path", "/logo.svg?v=1")]
    [InlineData("AppSurfaceDocs:Identity:Logo:Path", "/logo.svg#mark")]
    [InlineData("AppSurfaceDocs:Identity:Logo:Path", "/assets\\logo.svg")]
    [InlineData("AppSurfaceDocs:Identity:Logo:Path", "/../logo.svg")]
    [InlineData("AppSurfaceDocs:Identity:Favicon:SvgPath", "favicon.svg")]
    [InlineData("AppSurfaceDocs:Identity:HomeHref", "docs")]
    [InlineData("AppSurfaceDocs:Identity:HomeHref", "https://example.com/docs")]
    [InlineData("AppSurfaceDocs:Identity:HomeHref", "/docs?tenant=acme")]
    [InlineData("AppSurfaceDocs:Identity:HomeHref", "/docs#top")]
    public void AddAppSurfaceDocs_ShouldRejectInvalidIdentityBrowserPaths(string key, string value)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value);

        Assert.Contains(ex.Failures, failure => failure.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldRegisterIdentityConfigAuditKeys()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var entries = provider.GetServices<ConfigAuditKnownEntry>().ToArray();

        Assert.Contains(entries, entry => entry.Key == "AppSurfaceDocs" && entry.ValueType == typeof(AppSurfaceDocsOptions));
        Assert.Contains(entries, entry => entry.Key == "AppSurfaceDocs.Identity" && entry.ValueType == typeof(AppSurfaceDocsIdentityOptions));
    }

    [Fact]
    public void AppSurfaceDocsOptions_ShouldDefaultCacheExpirationToFiveMinutes()
    {
        var options = new AppSurfaceDocsOptions();

        Assert.Equal(5, options.CacheExpirationMinutes);
        Assert.Equal(1d / 60d, AppSurfaceDocsOptions.MinCacheExpirationMinutes);
        Assert.Equal((int.MaxValue - 1) / 60d, AppSurfaceDocsOptions.MaxCacheExpirationMinutes);
    }

    [Fact]
    public void AppSurfaceDocsOptions_ShouldDefaultHarvestFailOnFailureToFalse()
    {
        var options = new AppSurfaceDocsOptions();

        Assert.NotNull(options.Harvest);
        Assert.False(options.Harvest.FailOnFailure);
    }

    [Fact]
    public void AppSurfaceDocsOptions_ShouldDefaultHarvestHealthToDevelopmentOnly()
    {
        var options = new AppSurfaceDocsOptions();

        Assert.NotNull(options.Harvest.Health);
        Assert.Equal(AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly, options.Harvest.Health.ExposeRoutes);
        Assert.Equal(AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly, options.Harvest.Health.ShowChrome);
    }

    [Fact]
    public void AppSurfaceDocsOptions_ShouldDefaultLocalizationToDisabledEnglish()
    {
        var options = new AppSurfaceDocsOptions();

        Assert.NotNull(options.Localization);
        Assert.False(options.Localization.Enabled);
        Assert.Equal("en", options.Localization.DefaultLocale);
        Assert.Empty(options.Localization.Locales);
        Assert.Equal(AppSurfaceDocsLocaleRouteMode.LocalePrefix, options.Localization.RouteMode);
        Assert.Equal(AppSurfaceDocsLocaleFallbackMode.DefaultLocaleWithNotice, options.Localization.FallbackMode);
        Assert.Equal(AppSurfaceDocsLocaleSearchMode.ActiveLocale, options.Localization.SearchMode);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldBindAndNormalizeConfiguredLocalizationOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Localization:Enabled"] = "true",
                        ["AppSurfaceDocs:Localization:DefaultLocale"] = " en ",
                        ["AppSurfaceDocs:Localization:Locales:0:Code"] = " en ",
                        ["AppSurfaceDocs:Localization:Locales:0:Label"] = " English ",
                        ["AppSurfaceDocs:Localization:Locales:0:Lang"] = " en-US ",
                        ["AppSurfaceDocs:Localization:Locales:0:Direction"] = "Ltr",
                        ["AppSurfaceDocs:Localization:Locales:0:RoutePrefix"] = " en ",
                        ["AppSurfaceDocs:Localization:Locales:1:Code"] = " fr ",
                        ["AppSurfaceDocs:Localization:Locales:1:Label"] = " Français ",
                        ["AppSurfaceDocs:Localization:Locales:1:Direction"] = "Rtl"
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.True(options.Localization.Enabled);
        Assert.Equal("en", options.Localization.DefaultLocale);
        Assert.Collection(
            options.Localization.Locales,
            en =>
            {
                Assert.Equal("en", en.Code);
                Assert.Equal("English", en.Label);
                Assert.Equal("en-US", en.Lang);
                Assert.Equal(AppSurfaceDocsTextDirection.Ltr, en.Direction);
                Assert.Equal("en", en.RoutePrefix);
            },
            fr =>
            {
                Assert.Equal("fr", fr.Code);
                Assert.Equal("Français", fr.Label);
                Assert.Equal(AppSurfaceDocsTextDirection.Rtl, fr.Direction);
            });
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldSkipNullLocaleEntriesWhileNormalizingLocalizationOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.Configure<AppSurfaceDocsOptions>(
            options =>
            {
                options.Localization.Locales =
                [
                    null!,
                    new AppSurfaceDocsLocaleOptions
                    {
                        Code = " fr ",
                        Label = " Français ",
                        Lang = " fr-FR ",
                        RoutePrefix = " français "
                    }
                ];
            });

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.Null(options.Localization.Locales[0]);
        var locale = options.Localization.Locales[1];
        Assert.Equal("fr", locale.Code);
        Assert.Equal("Français", locale.Label);
        Assert.Equal("fr-FR", locale.Lang);
        Assert.Equal("français", locale.RoutePrefix);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldRejectEnabledLocalizationWithoutLocales()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Localization:Enabled"] = "true"
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value);

        Assert.Contains(
            ex.Failures,
            failure => failure.Contains("at least one configured locale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldRejectUnsupportedLocalizationEnumsAndNullLocales()
    {
        var result = new AppSurfaceDocsOptionsValidator().Validate(
            null,
            new AppSurfaceDocsOptions
            {
                Localization = new AppSurfaceDocsLocalizationOptions
                {
                    RouteMode = (AppSurfaceDocsLocaleRouteMode)42,
                    FallbackMode = (AppSurfaceDocsLocaleFallbackMode)42,
                    SearchMode = (AppSurfaceDocsLocaleSearchMode)42,
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
    public void AddAppSurfaceDocs_ShouldRejectInvalidLocalizationLocaleDefinitions()
    {
        var result = new AppSurfaceDocsOptionsValidator().Validate(
            null,
            new AppSurfaceDocsOptions
            {
                Localization = new AppSurfaceDocsLocalizationOptions
                {
                    Enabled = true,
                    DefaultLocale = "de",
                    Locales =
                    [
                        null!,
                        new AppSurfaceDocsLocaleOptions(),
                        new AppSurfaceDocsLocaleOptions
                        {
                            Code = "not_a_culture",
                            Lang = "also_not_a_culture",
                            Direction = (AppSurfaceDocsTextDirection)42,
                            RoutePrefix = "shared"
                        },
                        new AppSurfaceDocsLocaleOptions
                        {
                            Code = "fr",
                            RoutePrefix = "shared"
                        },
                        new AppSurfaceDocsLocaleOptions
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
    public void AddAppSurfaceDocs_ShouldRejectBlankLocalizationDefaultLocale()
    {
        var result = new AppSurfaceDocsOptionsValidator().Validate(
            null,
            new AppSurfaceDocsOptions
            {
                Localization = new AppSurfaceDocsLocalizationOptions
                {
                    Enabled = true,
                    DefaultLocale = " ",
                    Locales =
                    [
                        new AppSurfaceDocsLocaleOptions { Code = "en" }
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
    public void AddAppSurfaceDocs_ShouldRejectInvalidLocalizationRoutePrefixes(string routePrefix)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Localization:Enabled"] = "true",
                        ["AppSurfaceDocs:Localization:DefaultLocale"] = "en",
                        ["AppSurfaceDocs:Localization:Locales:0:Code"] = "en",
                        ["AppSurfaceDocs:Localization:Locales:0:RoutePrefix"] = routePrefix
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value);

        Assert.Contains(
            ex.Failures,
            failure => failure.Contains(":RoutePrefix", StringComparison.Ordinal));
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldBindConfiguredHarvestFailOnFailure()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:FailOnFailure"] = "true"
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.True(options.Harvest.FailOnFailure);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldBindConfiguredHarvestHealthOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always",
                        ["AppSurfaceDocs:Harvest:Health:ShowChrome"] = "Never"
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.Equal(AppSurfaceDocsHarvestHealthExposure.Always, options.Harvest.Health.ExposeRoutes);
        Assert.Equal(AppSurfaceDocsHarvestHealthExposure.Never, options.Harvest.Health.ShowChrome);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldBindConfiguredCacheExpirationMinutes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:CacheExpirationMinutes"] = "0.5"
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.Equal(0.5, options.CacheExpirationMinutes);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldRejectInvalidConfiguredCacheExpirationMinutes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:CacheExpirationMinutes"] = "-1"
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value);

        Assert.Contains(
            ex.Failures,
            failure => failure.Contains("CacheExpirationMinutes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldTrimAndDeduplicateConfiguredNamespacePrefixes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Sidebar:NamespacePrefixes:0"] = " ForgeTrust.AppSurface. ",
                        ["AppSurfaceDocs:Sidebar:NamespacePrefixes:1"] = "ForgeTrust.AppSurface.",
                        ["AppSurfaceDocs:Sidebar:NamespacePrefixes:2"] = "  "
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.Equal(["ForgeTrust.AppSurface."], options.Sidebar.NamespacePrefixes);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldTrimConfiguredBundlePath()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Bundle:Path"] = " /tmp/docs.bundle.json "
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.Equal("/tmp/docs.bundle.json", options.Bundle.Path);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldTrimConfiguredContributorOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Contributor:DefaultBranch"] = " main ",
                        ["AppSurfaceDocs:Contributor:SourceUrlTemplate"] = " https://example.com/blob/{branch}/{path} ",
                        ["AppSurfaceDocs:Contributor:EditUrlTemplate"] = " https://example.com/edit/{branch}/{path} ",
                        ["AppSurfaceDocs:Contributor:SymbolSourceUrlTemplate"] = " https://example.com/blob/{ref}/{path}#L{line} ",
                        ["AppSurfaceDocs:Contributor:SourceRef"] = " abc123 "
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.Equal("main", options.Contributor.DefaultBranch);
        Assert.Equal("https://example.com/blob/{branch}/{path}", options.Contributor.SourceUrlTemplate);
        Assert.Equal("https://example.com/edit/{branch}/{path}", options.Contributor.EditUrlTemplate);
        Assert.Equal("https://example.com/blob/{ref}/{path}#L{line}", options.Contributor.SymbolSourceUrlTemplate);
        Assert.Equal("abc123", options.Contributor.SourceRef);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldDefaultDocsRootToDocs_WhenVersioningIsDisabled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.Equal("/docs", options.Routing.RouteRootPath);
        Assert.Equal("/docs", options.Routing.DocsRootPath);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldDefaultDocsRootToDocsNext_WhenVersioningIsEnabled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Versioning:Enabled"] = "true",
                        ["AppSurfaceDocs:Versioning:CatalogPath"] = "catalog.json"
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.Equal("/docs", options.Routing.RouteRootPath);
        Assert.Equal("/docs/next", options.Routing.DocsRootPath);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldNormalizeRelativeDocsRootToAppRelativePath()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Routing:DocsRootPath"] = "docs/preview"
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.Equal("/docs/preview", options.Routing.RouteRootPath);
        Assert.Equal("/docs/preview", options.Routing.DocsRootPath);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldDefaultDocsRootFromCustomRouteRoot_WhenVersioningIsDisabled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Routing:RouteRootPath"] = "foo/bar"
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.Equal("/foo/bar", options.Routing.RouteRootPath);
        Assert.Equal("/foo/bar", options.Routing.DocsRootPath);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldDefaultDocsRootFromCustomRouteRoot_WhenVersioningIsEnabled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Routing:RouteRootPath"] = "/foo/bar",
                        ["AppSurfaceDocs:Versioning:Enabled"] = "true",
                        ["AppSurfaceDocs:Versioning:CatalogPath"] = "catalog.json"
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.Equal("/foo/bar", options.Routing.RouteRootPath);
        Assert.Equal("/foo/bar/next", options.Routing.DocsRootPath);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldSupportRootRouteFamilyWithVersionedPreview()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Routing:RouteRootPath"] = "/",
                        ["AppSurfaceDocs:Versioning:Enabled"] = "true",
                        ["AppSurfaceDocs:Versioning:CatalogPath"] = "catalog.json"
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.Equal("/", options.Routing.RouteRootPath);
        Assert.Equal("/next", options.Routing.DocsRootPath);
    }

    [Fact]
    public void ContributorOptions_ShouldDefaultLastUpdatedModeToNone()
    {
        var options = new AppSurfaceDocsContributorOptions();

        Assert.True(options.Enabled);
        Assert.Equal(AppSurfaceDocsLastUpdatedMode.None, options.LastUpdatedMode);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldRejectExplicitWhitespaceSourceRepositoryRoot()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Source:RepositoryRoot"] = "   ",
                        ["RepositoryRoot"] = "/tmp/legacy-root"
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value);

        Assert.Contains(
            ex.Failures,
            failure => failure.Contains("RepositoryRoot cannot be whitespace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldAllowRootMountedDocsSurface()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Routing:DocsRootPath"] = "/"
                    })
                .Build());

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.Equal("/", options.Routing.DocsRootPath);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldRehydrateNullNestedOptionsObjects()
    {
        var services = new ServiceCollection();
        using var configStream = new MemoryStream(
            Encoding.UTF8.GetBytes(
                """
                {
                  "AppSurfaceDocs": {
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

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.NotNull(options.Source);
        Assert.NotNull(options.Harvest);
        Assert.NotNull(options.Harvest.Health);
        Assert.NotNull(options.Bundle);
        Assert.NotNull(options.Sidebar);
        Assert.NotNull(options.Contributor);
        Assert.NotNull(options.Localization);
        Assert.False(options.Harvest.FailOnFailure);
        Assert.Equal(AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly, options.Harvest.Health.ExposeRoutes);
        Assert.Equal(AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly, options.Harvest.Health.ShowChrome);
        Assert.NotNull(options.Sidebar.NamespacePrefixes);
        Assert.Empty(options.Sidebar.NamespacePrefixes);
        Assert.False(options.Localization.Enabled);
        Assert.Equal("en", options.Localization.DefaultLocale);
        Assert.NotNull(options.Localization.Locales);
        Assert.Empty(options.Localization.Locales);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldPreserveExistingNestedOptionsObjects()
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

        var source = new AppSurfaceDocsSourceOptions { RepositoryRoot = " /tmp/configured-root " };
        var harvest = new AppSurfaceDocsHarvestOptions { FailOnFailure = true };
        harvest.Health.ShowChrome = AppSurfaceDocsHarvestHealthExposure.Never;
        var bundle = new AppSurfaceDocsBundleOptions { Path = " /tmp/docs.bundle.json " };
        var sidebar = new AppSurfaceDocsSidebarOptions
        {
            NamespacePrefixes = [" Contoso.Product. ", "contoso.product.", " "]
        };
        var contributor = new AppSurfaceDocsContributorOptions
        {
            DefaultBranch = " main ",
            SourceUrlTemplate = " https://example.com/blob/{branch}/{path} ",
            EditUrlTemplate = " https://example.com/edit/{branch}/{path} ",
            SymbolSourceUrlTemplate = " https://example.com/blob/{ref}/{path}#L{line} ",
            SourceRef = " abc123 "
        };
        var localization = new AppSurfaceDocsLocalizationOptions
        {
            DefaultLocale = " en ",
            Locales =
            [
                new AppSurfaceDocsLocaleOptions
                {
                    Code = " en ",
                    Label = " English ",
                    Lang = " en-US ",
                    RoutePrefix = " en "
                }
            ]
        };

        services.Configure<AppSurfaceDocsOptions>(
            options =>
            {
                options.Source = source;
                options.Harvest = harvest;
                options.Bundle = bundle;
                options.Sidebar = sidebar;
                options.Contributor = contributor;
                options.Localization = localization;
            });

        services.AddAppSurfaceDocs();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.Same(source, options.Source);
        Assert.Same(harvest, options.Harvest);
        Assert.Same(bundle, options.Bundle);
        Assert.Same(sidebar, options.Sidebar);
        Assert.Same(contributor, options.Contributor);
        Assert.Same(localization, options.Localization);
        Assert.Equal("/tmp/configured-root", options.Source.RepositoryRoot);
        Assert.True(options.Harvest.FailOnFailure);
        Assert.Equal(AppSurfaceDocsHarvestHealthExposure.Never, options.Harvest.Health.ShowChrome);
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
    public void AddAppSurfaceDocs_ShouldRehydrateExplicitlyNullNestedOptionsObjects()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddAppSurfaceDocs();
        services.Configure<AppSurfaceDocsOptions>(
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
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.NotNull(options.Source);
        Assert.NotNull(options.Harvest);
        Assert.NotNull(options.Harvest.Health);
        Assert.NotNull(options.Bundle);
        Assert.NotNull(options.Sidebar);
        Assert.NotNull(options.Contributor);
        Assert.NotNull(options.Localization);
        Assert.False(options.Harvest.FailOnFailure);
        Assert.Equal(AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly, options.Harvest.Health.ExposeRoutes);
        Assert.Equal(AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly, options.Harvest.Health.ShowChrome);
        Assert.NotNull(options.Sidebar.NamespacePrefixes);
        Assert.Empty(options.Sidebar.NamespacePrefixes);
        Assert.False(options.Localization.Enabled);
        Assert.Equal("en", options.Localization.DefaultLocale);
        Assert.NotNull(options.Localization.Locales);
        Assert.Empty(options.Localization.Locales);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldRehydrateNullRoutingAndVersioningOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddAppSurfaceDocs();
        services.Configure<AppSurfaceDocsOptions>(
            options =>
            {
                options.Routing = null!;
                options.Versioning = null!;
            });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.NotNull(options.Routing);
        Assert.NotNull(options.Versioning);
        Assert.Equal("/docs", options.Routing.RouteRootPath);
        Assert.Equal("/docs", options.Routing.DocsRootPath);
        Assert.False(options.Versioning.Enabled);
    }

    [Fact]
    public void AddAppSurfaceDocs_ShouldRehydrateExplicitlyNullNamespacePrefixes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddAppSurfaceDocs();
        services.Configure<AppSurfaceDocsOptions>(
            options =>
            {
                options.Sidebar = new AppSurfaceDocsSidebarOptions
                {
                    NamespacePrefixes = null!
                };
            });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value;

        Assert.NotNull(options.Sidebar);
        Assert.NotNull(options.Sidebar.NamespacePrefixes);
        Assert.Empty(options.Sidebar.NamespacePrefixes);
    }

    [Fact]
    public void Validator_ShouldRejectNullHarvestOptions()
    {
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Harvest = null!
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("AppSurfaceDocs:Harvest must not be null", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRejectNullHarvestHealthOptions()
    {
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                Health = null!
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("AppSurfaceDocs:Harvest:Health must not be null", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validator_ShouldRejectUnsupportedHarvestHealthExposureValues(bool invalidRoutes)
    {
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                Health = new AppSurfaceDocsHarvestHealthOptions
                {
                    ExposeRoutes = invalidRoutes
                        ? (AppSurfaceDocsHarvestHealthExposure)999
                        : AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly,
                    ShowChrome = invalidRoutes
                        ? AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly
                        : (AppSurfaceDocsHarvestHealthExposure)999
                }
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        var expectedMessage = invalidRoutes
            ? "Unsupported AppSurface Docs harvest health route exposure mode"
            : "Unsupported AppSurface Docs harvest health chrome exposure mode";
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRejectUnsupportedModeValue()
    {
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Mode = (AppSurfaceDocsMode)999
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("Unsupported AppSurface Docs mode", StringComparison.OrdinalIgnoreCase));
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            CacheExpirationMinutes = 0.1,
            Routing = new AppSurfaceDocsRoutingOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            CacheExpirationMinutes = AppSurfaceDocsOptions.MinCacheExpirationMinutes,
            Routing = new AppSurfaceDocsRoutingOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            CacheExpirationMinutes = AppSurfaceDocsOptions.MaxCacheExpirationMinutes,
            Routing = new AppSurfaceDocsRoutingOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Identity = new AppSurfaceDocsIdentityOptions
            {
                Logo = null!,
                Wordmark = null!,
                Favicon = null!
            },
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
        Assert.Contains(result.Failures, failure => failure.Contains("AppSurfaceDocs:Identity:Logo must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("AppSurfaceDocs:Identity:Wordmark must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("AppSurfaceDocs:Identity:Favicon must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("AppSurfaceDocs:Source must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("AppSurfaceDocs:Bundle must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("AppSurfaceDocs:Sidebar must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("AppSurfaceDocs:Contributor must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("AppSurfaceDocs:Routing must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("AppSurfaceDocs:Versioning must not be null.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("AppSurfaceDocs:Localization must not be null.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRejectNullNamespacePrefixes()
    {
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Sidebar = new AppSurfaceDocsSidebarOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Mode = AppSurfaceDocsMode.Bundle,
            Bundle = new AppSurfaceDocsBundleOptions { Path = "   " }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("requires AppSurfaceDocs:Bundle:Path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRejectWhitespaceSourceRepositoryRoot()
    {
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Source = new AppSurfaceDocsSourceOptions { RepositoryRoot = "   " }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("RepositoryRoot cannot be whitespace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRejectBundleModeBeforeSliceTwo()
    {
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Mode = AppSurfaceDocsMode.Bundle,
            Bundle = new AppSurfaceDocsBundleOptions { Path = "/tmp/docs.bundle.json" }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("not implemented", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRequireBundlePath_WhenBundleModeBundleOptionsAreMissing()
    {
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Mode = AppSurfaceDocsMode.Bundle,
            Bundle = null!
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("AppSurfaceDocs:Bundle must not be null", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("requires AppSurfaceDocs:Bundle:Path", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("not implemented", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldStillReportVersioningFailures_WhenRoutingCannotNormalize()
    {
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                RouteRootPath = "https://example.com/foo/bar"
            },
            Versioning = new AppSurfaceDocsVersioningOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                RouteRootPath = "/foo/bar",
                DocsRootPath = "/foo/bar/next"
            },
            Versioning = null!
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("AppSurfaceDocs:Versioning must not be null", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Failures, failure => failure.Contains("RouteRootPath", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Failures, failure => failure.Contains("DocsRootPath", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRejectDocsRootAtDocs_WhenVersioningIsEnabled()
    {
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                DocsRootPath = "/docs"
            },
            Versioning = new AppSurfaceDocsVersioningOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        foreach (var docsRootPath in new[] { "/docs/v", "/docs/v/1.0.0", "/docs/versions" })
        {
            var options = new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    DocsRootPath = docsRootPath
                },
                Versioning = new AppSurfaceDocsVersioningOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                DocsRootPath = "/"
            },
            Versioning = new AppSurfaceDocsVersioningOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                RouteRootPath = "/",
                DocsRootPath = "/next"
            },
            Versioning = new AppSurfaceDocsVersioningOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                RouteRootPath = routeRootPath,
                DocsRootPath = docsRootPath
            },
            Versioning = new AppSurfaceDocsVersioningOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                DocsRootPath = "/docs/next"
            },
            Versioning = new AppSurfaceDocsVersioningOptions
            {
                Enabled = true,
                CatalogPath = " "
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("requires AppSurfaceDocs:Versioning:CatalogPath", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ShouldRequireDefaultBranch_WhenContributorTemplatesAreConfigured()
    {
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Contributor = new AppSurfaceDocsContributorOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Contributor = new AppSurfaceDocsContributorOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Contributor = new AppSurfaceDocsContributorOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Contributor = new AppSurfaceDocsContributorOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Contributor = new AppSurfaceDocsContributorOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Contributor = new AppSurfaceDocsContributorOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                DocsRootPath = "/docs"
            },
            Contributor = new AppSurfaceDocsContributorOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                DocsRootPath = "/docs"
            },
            Contributor = new AppSurfaceDocsContributorOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Contributor = new AppSurfaceDocsContributorOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                DocsRootPath = "/docs"
            },
            Contributor = new AppSurfaceDocsContributorOptions
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
        var validator = new AppSurfaceDocsOptionsValidator();
        var options = new AppSurfaceDocsOptions
        {
            Contributor = new AppSurfaceDocsContributorOptions
            {
                LastUpdatedMode = (AppSurfaceDocsLastUpdatedMode)999
            }
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("Unsupported AppSurface Docs contributor last-updated mode", StringComparison.OrdinalIgnoreCase));
    }
}
