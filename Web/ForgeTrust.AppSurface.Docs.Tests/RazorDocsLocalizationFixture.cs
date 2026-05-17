using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

internal static class RazorDocsLocalizationFixture
{
    internal static RazorDocsLocalizationOptions CreateOptions()
    {
        return new RazorDocsLocalizationOptions
        {
            Enabled = true,
            DefaultLocale = "en",
            Locales =
            [
                new RazorDocsLocaleOptions
                {
                    Code = "en",
                    Label = "English"
                },
                new RazorDocsLocaleOptions
                {
                    Code = "fr",
                    Label = "Français"
                }
            ]
        };
    }

    internal static DocNode MarkdownDoc(
        string path,
        string title,
        string? locale = null,
        string? translationKey = null,
        RazorDocsLocaleFallbackMode? fallback = null,
        string? canonicalSlug = null)
    {
        return new DocNode(
            title,
            path,
            $"<h1>{title}</h1><p>Body</p>",
            Metadata: new DocMetadata
            {
                Title = title,
                CanonicalSlug = canonicalSlug,
                Localization = locale is null && translationKey is null && fallback is null
                    ? null
                    : new DocLocalizationMetadata
                    {
                        Locale = locale,
                        TranslationKey = translationKey,
                        LocaleFallback = fallback
                    }
            });
    }

    internal static LocalizedDocsGraph BuildGraph(
        RazorDocsLocalizationOptions options,
        params DocNode[] docs)
    {
        var catalog = DocRouteIdentityCatalog.Create(docs, new DocsUrlBuilder(new RazorDocsOptions()));
        return new LocalizedDocsGraphBuilder(options).Build(docs, catalog);
    }
}
