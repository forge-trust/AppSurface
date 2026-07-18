using Microsoft.Extensions.FileProviders;

namespace ForgeTrust.AppSurface.Web;

internal static class PwaStaticFileShadowValidator
{
    /// <summary>
    /// Rejects generated worker/helper routes that static-file middleware would serve first.
    /// </summary>
    /// <param name="options">The validated PWA options.</param>
    /// <param name="webRootFileProvider">The effective web-root file provider.</param>
    public static void ThrowIfInvalid(PwaOptions options, IFileProvider webRootFileProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(webRootFileProvider);

        if (!options.HasAnyGeneratedScriptRoute)
        {
            return;
        }

        if (options.IsWorkerEnabled)
        {
            ThrowIfFileExists(webRootFileProvider, options.Worker.ServiceWorkerPath, "service worker");
        }

        if (options.Push.Enabled)
        {
            ThrowIfFileExists(webRootFileProvider, options.Worker.RegistrationHelperPath, "registration helper");
        }
        if (options.Badging.Enabled)
        {
            ThrowIfFileExists(webRootFileProvider, options.Badging.HelperPath, "badging helper");
        }
    }

    private static void ThrowIfFileExists(IFileProvider fileProvider, string path, string surface)
    {
        if (!PwaOptionsValidator.IsSafeLocalPath(path))
        {
            return;
        }

        var file = fileProvider.GetFileInfo(path.TrimStart('/'));
        if (file.Exists && !file.IsDirectory)
        {
            throw new InvalidOperationException(
                $"AppSurface PWA configuration is invalid: ASPWA024: The generated {surface} route is shadowed by a static web-root file. Remove the static file or configure a different AppSurface path.");
        }
    }
}
