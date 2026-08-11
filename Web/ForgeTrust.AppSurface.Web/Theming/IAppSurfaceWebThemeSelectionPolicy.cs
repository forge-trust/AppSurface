using ForgeTrust.AppSurface.Theming;

namespace ForgeTrust.AppSurface.Web.Theming;

/// <summary>
/// Selects one registered AppSurface theme pair from already-authorized host context.
/// </summary>
/// <remarks>
/// <para>
/// Implement this scoped policy in the Web host when the rendered pair depends on application-owned request context,
/// such as an authorized tenant. The policy is a presentation seam only: it does not establish tenancy, authenticate
/// callers, authorize access, read a cache, or validate a theme identifier supplied by untrusted input.
/// </para>
/// <para>
/// Return <see langword="false"/> when the host intentionally wants the configured default pair. Return
/// <see langword="true"/> only with a registered pair identifier. The Web adapter validates that result against the
/// sealed neutral registry before rendering; an empty or unknown id fails closed. A host that wants a missing or
/// unauthorized context to fail owns that decision before this policy returns.
/// </para>
/// </remarks>
public interface IAppSurfaceWebThemeSelectionPolicy
{
    /// <summary>
    /// Attempts to select a registered theme pair for the current host context.
    /// </summary>
    /// <param name="themeId">
    /// The selected registered pair when this method returns <see langword="true"/>; otherwise ignored.
    /// </param>
    /// <returns>
    /// <see langword="true"/> to render <paramref name="themeId"/>, or <see langword="false"/> to render the
    /// configured default pair.
    /// </returns>
    bool TrySelect(out AppSurfaceThemeId themeId);
}
