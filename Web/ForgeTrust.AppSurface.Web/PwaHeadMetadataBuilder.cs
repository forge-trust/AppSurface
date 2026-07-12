using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace ForgeTrust.AppSurface.Web;

internal static class PwaHeadMetadataBuilder
{
    /// <summary>
    /// Builds the exact encoded PWA head markup shared by the Razor TagHelper and development diagnostics.
    /// </summary>
    /// <param name="pathBase">The request path base prepended to app-root-relative values.</param>
    /// <param name="options">The validated PWA options.</param>
    /// <param name="fileVersionProvider">An optional provider for versioning install icon URLs.</param>
    /// <returns>Encoded head markup, or an empty string when no PWA surface is active.</returns>
    public static string Build(PathString pathBase, PwaOptions options, IFileVersionProvider? fileVersionProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = new StringBuilder();
        if (options.Enabled)
        {
            AppendInstallMetadata(builder, pathBase, options, fileVersionProvider);
        }

        if (options.IsWorkerEnabled)
        {
            var workerPath = AddPathBase(pathBase, options.Worker.ServiceWorkerPath);
            var workerScope = AddPathBase(pathBase, options.Scope);
            AppendMeta(builder, "appsurface:pwa-service-worker", workerPath);
            AppendMeta(builder, "appsurface:pwa-service-worker-scope", workerScope);

            if (options.Push.Enabled)
            {
                var helperPath = AddPathBase(pathBase, options.Worker.RegistrationHelperPath);
                var separator = helperPath.Contains('?', StringComparison.Ordinal) ? '&' : '?';
                builder.Append("<script defer src=\"");
                builder.Append(Escape($"{helperPath}{separator}v={PwaScriptAssets.RegistrationHelperVersion}"));
                builder.Append("\" data-appsurface-pwa-worker=\"");
                builder.Append(Escape(workerPath));
                builder.Append("\" data-appsurface-pwa-scope=\"");
                builder.Append(Escape(workerScope));
                builder.AppendLine("\"></script>");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendInstallMetadata(
        StringBuilder builder,
        PathString pathBase,
        PwaOptions options,
        IFileVersionProvider? fileVersionProvider)
    {
        builder.Append("<link rel=\"manifest\" href=\"");
        builder.Append(Escape(AddPathBase(pathBase, options.ManifestPath)));
        builder.AppendLine("\" />");
        AppendMeta(builder, "theme-color", options.ThemeColor);
        AppendMeta(builder, "application-name", options.Name);
        AppendMeta(builder, "apple-mobile-web-app-capable", "yes");
        AppendMeta(builder, "apple-mobile-web-app-title", options.ShortName);

        foreach (var icon in options.Icons)
        {
            var href = icon.Source;
            if (fileVersionProvider is not null && PwaOptionsValidator.IsSafeLocalPath(href))
            {
                href = fileVersionProvider.AddFileVersionToPath(pathBase, href);
            }

            builder.Append("<link rel=\"icon\" href=\"");
            builder.Append(Escape(href));
            builder.Append("\" sizes=\"");
            builder.Append(Escape(icon.Sizes));
            builder.Append("\" type=\"");
            builder.Append(Escape(icon.Type));
            builder.AppendLine("\" />");
        }
    }

    private static void AppendMeta(StringBuilder builder, string name, string content)
    {
        builder.Append("<meta name=\"");
        builder.Append(Escape(name));
        builder.Append("\" content=\"");
        builder.Append(Escape(content));
        builder.AppendLine("\" />");
    }

    private static string AddPathBase(PathString pathBase, string path) =>
        pathBase.Add(new PathString(path)).Value ?? path;

    private static string Escape(string value) => HtmlEncoder.Default.Encode(value);
}
