namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Registry adapter for the built-in AppSurface product-event catalog.
/// </summary>
internal sealed class BuiltInAppSurfaceProductEventRegistry : IAppSurfaceProductEventRegistry
{
    /// <inheritdoc />
    public IReadOnlyList<AppSurfaceProductEventContract> All => AppSurfaceProductEventRegistry.All;

    /// <inheritdoc />
    public IReadOnlySet<string> ForbiddenProperties => AppSurfaceProductEventRegistry.ForbiddenProperties;

    /// <inheritdoc />
    public AppSurfaceProductEventContract? Find(string name)
    {
        return AppSurfaceProductEventRegistry.Find(name);
    }

    /// <inheritdoc />
    public AppSurfaceProductEventValidationResult Validate(AppSurfaceProductEvent productEvent)
    {
        return AppSurfaceProductEventRegistry.Validate(productEvent);
    }
}
