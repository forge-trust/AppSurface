using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Web;

internal static class PwaScriptAssets
{
    /// <summary>Gets the shared service-worker lifecycle source.</summary>
    public static string WorkerShared { get; } = Read("pwa-worker-shared.js");

    /// <summary>Gets the optional offline capability source.</summary>
    public static string WorkerOffline { get; } = Read("pwa-worker-offline.js");

    /// <summary>Gets the default version-1 push and notification-click source.</summary>
    public static string WorkerPush { get; } = Read("pwa-worker-push.js");

    /// <summary>Gets the contained custom push-handler import source.</summary>
    public static string WorkerCustomHandler { get; } = Read("pwa-worker-custom-handler.js");

    /// <summary>Gets the inert browser registration-helper source.</summary>
    public static string RegistrationHelper { get; } = Read("pwa-register.js");

    /// <summary>Gets the canonical badging installer factory shared by page and worker wrappers.</summary>
    public static string BadgingFactory { get; } = Read("pwa-badging-factory.js").Trim();

    /// <summary>Gets the inert browser badging-helper source.</summary>
    public static string BadgingHelper { get; } = BuildBadgingWrapper("window", "window.navigator");

    /// <summary>Gets the generated-worker badging adapter source.</summary>
    public static string WorkerBadging { get; } = BuildBadgingWrapper("self", "self.navigator");

    /// <summary>Gets the shared C# and JavaScript path-validation vectors.</summary>
    public static string PathValidationVectors { get; } = Read("pwa-path-vectors.json");

    /// <summary>Gets the content-derived cache version for the registration helper.</summary>
    public static string RegistrationHelperVersion { get; } = BuildVersion(RegistrationHelper);

    /// <summary>Gets the content-derived cache version for the badging page helper.</summary>
    public static string BadgingHelperVersion { get; } = BuildVersion(BadgingHelper);

    private static string Read(string fileName) => Read(typeof(PwaScriptAssets).Assembly, fileName);

    /// <summary>Reads a named PWA resource from an assembly.</summary>
    /// <param name="assembly">The assembly that owns the embedded PWA resource.</param>
    /// <param name="fileName">The final file name beneath the embedded Assets.Pwa namespace.</param>
    /// <returns>The UTF-8 resource contents.</returns>
    /// <exception cref="InvalidOperationException">The named resource cannot be found or opened.</exception>
    internal static string Read(Assembly assembly, string fileName)
    {
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith($".Assets.Pwa.{fileName}", StringComparison.Ordinal));
        if (resourceName is null)
        {
            throw new InvalidOperationException($"The embedded AppSurface PWA script '{fileName}' could not be found.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded AppSurface PWA script '{fileName}' could not be opened.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string BuildVersion(string script)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(script));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string BuildBadgingWrapper(string root, string navigator) =>
        $$"""
        // AppSurface PWA application-icon badging adapter.
        (() => {
          "use strict";
          const install = {{BadgingFactory}};
          install({{root}}, {{navigator}}, () => {
            try {
              console.error("ASPWAJS002");
            } catch {
              // A hostile console must not make namespace containment observable through throws.
            }
          });
        })();
        """;
}
