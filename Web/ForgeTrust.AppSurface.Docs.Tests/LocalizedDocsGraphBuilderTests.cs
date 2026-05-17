using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class LocalizedDocsGraphBuilderTests
{
    [Fact]
    public void Build_ShouldReturnDisabledGraph_WhenLocalizationIsDisabled()
    {
        var graph = RazorDocsLocalizationFixture.BuildGraph(
            new RazorDocsLocalizationOptions(),
            RazorDocsLocalizationFixture.MarkdownDoc("README.md", "Home"));

        Assert.False(graph.Enabled);
        Assert.Empty(graph.DocSets);
        Assert.Empty(graph.Diagnostics);
    }

    [Fact]
    public void Build_ShouldInferColocatedLocaleSuffixAndTranslationKeyFromBaseDocument()
    {
        var options = RazorDocsLocalizationFixture.CreateOptions();

        var graph = RazorDocsLocalizationFixture.BuildGraph(
            options,
            RazorDocsLocalizationFixture.MarkdownDoc("README.md", "Home"),
            RazorDocsLocalizationFixture.MarkdownDoc("README.fr.md", "Accueil"));

        var docSet = Assert.Single(graph.DocSets);
        Assert.Equal("README", docSet.TranslationKey);
        Assert.Equal("README.md", docSet.DefaultLocaleSourcePath);
        Assert.Collection(
            docSet.Variants.OrderBy(variant => variant.Locale, StringComparer.OrdinalIgnoreCase),
            en =>
            {
                Assert.Equal("en", en.Locale);
                Assert.Equal("README.md", en.SourcePath);
                Assert.True(en.LocaleWasInferred);
                Assert.True(en.TranslationKeyWasInferred);
            },
            fr =>
            {
                Assert.Equal("fr", fr.Locale);
                Assert.Equal("README.fr.md", fr.SourcePath);
                Assert.Equal(string.Empty, fr.PublicRoutePath);
                Assert.True(fr.LocaleWasInferred);
                Assert.True(fr.TranslationKeyWasInferred);
            });
        Assert.Empty(graph.Diagnostics);
    }

    [Fact]
    public void Build_ShouldEmitMissingBaseDiagnostic_ForColocatedLocaleSuffixWithoutBase()
    {
        var graph = RazorDocsLocalizationFixture.BuildGraph(
            RazorDocsLocalizationFixture.CreateOptions(),
            RazorDocsLocalizationFixture.MarkdownDoc("guides/configuration.fr.md", "Configuration"));

        Assert.Contains(
            graph.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.LocalizationMissingBase);
        var docSet = Assert.Single(graph.DocSets);
        Assert.Equal("guides/configuration", docSet.TranslationKey);
    }

    [Fact]
    public void Build_ShouldEmitUnsupportedLocaleDiagnostic_ForConfiguredMismatch()
    {
        var graph = RazorDocsLocalizationFixture.BuildGraph(
            RazorDocsLocalizationFixture.CreateOptions(),
            RazorDocsLocalizationFixture.MarkdownDoc("guides/configuration.es.md", "Configuration"));

        Assert.Contains(
            graph.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.LocalizationUnsupportedLocale);
        Assert.Empty(graph.DocSets);
        Assert.False(graph.VariantsBySourcePath.ContainsKey("guides/configuration.es.md"));
    }

    [Fact]
    public void Build_ShouldEmitUnsupportedLocaleDiagnostic_ForExplicitUnsupportedLocale()
    {
        var graph = RazorDocsLocalizationFixture.BuildGraph(
            RazorDocsLocalizationFixture.CreateOptions(),
            RazorDocsLocalizationFixture.MarkdownDoc("guides/configuration.md", "Configuration", locale: "es"));

        Assert.Contains(
            graph.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.LocalizationUnsupportedLocale);
        Assert.Empty(graph.DocSets);
    }

    [Fact]
    public void Build_ShouldIgnoreNonMarkdownLocaleSuffixInference()
    {
        var graph = RazorDocsLocalizationFixture.BuildGraph(
            RazorDocsLocalizationFixture.CreateOptions(),
            RazorDocsLocalizationFixture.MarkdownDoc("api/configuration.fr", "Configuration"));

        var variant = Assert.Single(graph.VariantsBySourcePath.Values);

        Assert.Equal("en", variant.Locale);
        Assert.Equal("api/configuration", variant.TranslationKey);
        Assert.Empty(graph.Diagnostics);
    }

    [Fact]
    public void Build_ShouldTreatUnknownCultureLikeSuffixAsOrdinaryDefaultLocaleDoc()
    {
        var graph = RazorDocsLocalizationFixture.BuildGraph(
            RazorDocsLocalizationFixture.CreateOptions(),
            RazorDocsLocalizationFixture.MarkdownDoc("guides/configuration.not_a_culture.md", "Configuration"));

        var variant = Assert.Single(graph.VariantsBySourcePath.Values);

        Assert.Equal("en", variant.Locale);
        Assert.Equal("guides/configuration.not_a_culture", variant.TranslationKey);
        Assert.Empty(graph.Diagnostics);
    }

    [Fact]
    public void Build_ShouldInferMarkdownExtensionLocaleSuffixes()
    {
        var graph = RazorDocsLocalizationFixture.BuildGraph(
            RazorDocsLocalizationFixture.CreateOptions(),
            RazorDocsLocalizationFixture.MarkdownDoc("guides/configuration.markdown", "Configuration"),
            RazorDocsLocalizationFixture.MarkdownDoc("guides/configuration.fr.markdown", "Configuration"));

        var docSet = Assert.Single(graph.DocSets);

        Assert.Equal("guides/configuration", docSet.TranslationKey);
        Assert.Contains(docSet.Variants, variant => variant.Locale == "fr" && variant.SourcePath == "guides/configuration.fr.markdown");
    }

    [Fact]
    public void Build_ShouldKeepGraphEmpty_WhenDefaultLocaleCannotBeResolved()
    {
        var options = new RazorDocsLocalizationOptions
        {
            Enabled = true,
            DefaultLocale = null!,
            Locales =
            [
                new RazorDocsLocaleOptions { Code = "en" }
            ]
        };

        var graph = RazorDocsLocalizationFixture.BuildGraph(
            options,
            RazorDocsLocalizationFixture.MarkdownDoc("README.md", "Home"));

        Assert.Empty(graph.DocSets);
        Assert.Empty(graph.VariantsBySourcePath);
        Assert.Empty(graph.Diagnostics);
    }

    [Fact]
    public void Build_ShouldResolveLocalizedTitleBeforeMetadataTitle()
    {
        var graph = RazorDocsLocalizationFixture.BuildGraph(
            RazorDocsLocalizationFixture.CreateOptions(),
            RazorDocsLocalizationFixture.MarkdownDoc(
                "guides/configuration.md",
                "Configuration",
                localizedTitle: "Localized configuration"));

        var variant = Assert.Single(graph.VariantsBySourcePath.Values);

        Assert.Equal("Localized configuration", variant.Title);
    }

    [Fact]
    public void Build_ShouldInferFolderLocaleOnlyWhenTranslationKeyIsAuthored()
    {
        var graph = RazorDocsLocalizationFixture.BuildGraph(
            RazorDocsLocalizationFixture.CreateOptions(),
            RazorDocsLocalizationFixture.MarkdownDoc("fr/guides/demarrer.md", "Démarrer"),
            RazorDocsLocalizationFixture.MarkdownDoc(
                "fr/guides/configuration.md",
                "Configuration",
                translationKey: "guides/configuration"));

        var ordinaryFolderDoc = graph.VariantsBySourcePath["fr/guides/demarrer.md"];
        var localizedFolderDoc = graph.VariantsBySourcePath["fr/guides/configuration.md"];

        Assert.Equal("en", ordinaryFolderDoc.Locale);
        Assert.Equal("fr/guides/demarrer", ordinaryFolderDoc.TranslationKey);
        Assert.Equal("fr", localizedFolderDoc.Locale);
        Assert.Equal("guides/configuration", localizedFolderDoc.TranslationKey);
    }

    [Fact]
    public void Build_ShouldEmitFolderConflictDiagnostic_WhenAuthoredLocaleDisagreesWithFolderLocale()
    {
        var graph = RazorDocsLocalizationFixture.BuildGraph(
            RazorDocsLocalizationFixture.CreateOptions(),
            RazorDocsLocalizationFixture.MarkdownDoc(
                "fr/guides/configuration.md",
                "Configuration",
                locale: "en",
                translationKey: "guides/configuration"));

        Assert.Contains(
            graph.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.LocalizationLocaleFolderConflict);
    }

    [Fact]
    public void Build_ShouldEmitDuplicateVariantDiagnostic_ForSameTranslationKeyAndLocale()
    {
        var graph = RazorDocsLocalizationFixture.BuildGraph(
            RazorDocsLocalizationFixture.CreateOptions(),
            RazorDocsLocalizationFixture.MarkdownDoc("guides/one.md", "One", locale: "fr", translationKey: "guides/shared"),
            RazorDocsLocalizationFixture.MarkdownDoc("guides/two.md", "Two", locale: "fr", translationKey: "guides/shared"));

        Assert.Contains(
            graph.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.LocalizationDuplicateVariant);
    }

    [Fact]
    public void Build_ShouldEmitFallbackDisabledMissingVariantDiagnostic()
    {
        var graph = RazorDocsLocalizationFixture.BuildGraph(
            RazorDocsLocalizationFixture.CreateOptions(),
            RazorDocsLocalizationFixture.MarkdownDoc(
                "guides/configuration.md",
                "Configuration",
                fallback: RazorDocsLocaleFallbackMode.Disabled));

        Assert.Contains(
            graph.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.LocalizationFallbackDisabledMissingVariant);
    }

    [Fact]
    public void RouteCatalog_ShouldBuildLocalePrefixedRouteCandidatesFromGraph()
    {
        var options = RazorDocsLocalizationFixture.CreateOptions();
        var docs = new[]
        {
            RazorDocsLocalizationFixture.MarkdownDoc("guides/getting-started.md", "Getting started"),
            RazorDocsLocalizationFixture.MarkdownDoc("guides/getting-started.fr.md", "Démarrer")
        };
        var catalog = DocRouteIdentityCatalog.Create(docs, new DocsUrlBuilder(new RazorDocsOptions()));
        var graph = new LocalizedDocsGraphBuilder(options).Build(docs, catalog);

        var candidates = catalog.BuildLocalizedRouteCandidates(graph, options);

        Assert.Contains(
            candidates,
            candidate => candidate.Locale == "en"
                         && candidate.TranslationKey == "guides/getting-started"
                         && candidate.PublicRoutePath == "en/guides/getting-started");
        Assert.Contains(
            candidates,
            candidate => candidate.Locale == "fr"
                         && candidate.SourcePath == "guides/getting-started.fr.md"
                         && candidate.PublicRoutePath == "fr/guides/getting-started");
    }

    [Fact]
    public void RouteCatalog_ShouldBuildHomeLocaleRouteCandidateWithoutTrailingSlash()
    {
        var options = RazorDocsLocalizationFixture.CreateOptions();
        var docs = new[]
        {
            RazorDocsLocalizationFixture.MarkdownDoc("README.md", "Home")
        };
        var catalog = DocRouteIdentityCatalog.Create(docs, new DocsUrlBuilder(new RazorDocsOptions()));
        var graph = new LocalizedDocsGraphBuilder(options).Build(docs, catalog);

        var candidate = Assert.Single(catalog.BuildLocalizedRouteCandidates(graph, options));

        Assert.Equal("en", candidate.Locale);
        Assert.Equal("en", candidate.PublicRoutePath);
    }

    [Fact]
    public void RouteCatalog_ShouldReturnNoLocalizedRouteCandidates_WhenGraphIsDisabled()
    {
        var options = RazorDocsLocalizationFixture.CreateOptions();
        var docs = new[]
        {
            RazorDocsLocalizationFixture.MarkdownDoc("README.md", "Home")
        };
        var catalog = DocRouteIdentityCatalog.Create(docs, new DocsUrlBuilder(new RazorDocsOptions()));
        var graph = RazorDocsLocalizationFixture.BuildGraph(new RazorDocsLocalizationOptions(), docs);

        var candidates = catalog.BuildLocalizedRouteCandidates(graph, options);

        Assert.Empty(candidates);
    }

    [Fact]
    public void RouteCatalog_ShouldSkipLocalizedRouteCandidatesForUnconfiguredLocale()
    {
        var options = RazorDocsLocalizationFixture.CreateOptions();
        var docs = new[]
        {
            RazorDocsLocalizationFixture.MarkdownDoc("guides/configuration.md", "Configuration")
        };
        var catalog = DocRouteIdentityCatalog.Create(docs, new DocsUrlBuilder(new RazorDocsOptions()));
        var graph = new LocalizedDocsGraph(
            Enabled: true,
            DefaultLocale: "en",
            DocSets:
            [
                new LocalizedDocSet(
                    "guides/configuration",
                    "guides/configuration.md",
                    [
                        new LocalizedDocVariant(
                            "guides/configuration.md",
                            "de",
                            "guides/configuration",
                            "Configuration",
                            "guides/configuration",
                            LocaleFallback: null,
                            LocaleWasInferred: false,
                            TranslationKeyWasInferred: false)
                    ],
                    RazorDocsLocaleFallbackMode.DefaultLocaleWithNotice)
            ],
            VariantsBySourcePath: new Dictionary<string, LocalizedDocVariant>(),
            Diagnostics: []);

        var candidates = catalog.BuildLocalizedRouteCandidates(graph, options);

        Assert.Empty(candidates);
    }

    [Fact]
    public void RouteCatalog_ShouldSkipLocalizedRouteCandidatesWithoutPublicRoute()
    {
        var options = RazorDocsLocalizationFixture.CreateOptions();
        var docs = new[]
        {
            RazorDocsLocalizationFixture.MarkdownDoc(
                "guides/broken.md",
                "Broken",
                locale: "fr",
                translationKey: "guides/broken",
                canonicalSlug: "../broken")
        };
        var catalog = DocRouteIdentityCatalog.Create(docs, new DocsUrlBuilder(new RazorDocsOptions()));
        var graph = new LocalizedDocsGraphBuilder(options).Build(docs, catalog);

        var candidates = catalog.BuildLocalizedRouteCandidates(graph, options);

        Assert.Empty(candidates);
        var docSet = Assert.Single(graph.DocSets);
        var variant = Assert.Single(docSet.Variants);
        Assert.Null(variant.PublicRoutePath);
    }
}
