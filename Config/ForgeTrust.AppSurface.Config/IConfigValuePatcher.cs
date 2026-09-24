namespace ForgeTrust.AppSurface.Config;

/// <summary>Supplies an isolated object transaction from present child values in the request's snapshot.</summary>
internal interface IConfigValuePatcher
{
    /// <summary>Validates every present candidate before publishing one or more successfully bound children.</summary>
    /// <remarks>Never mutate currentValue. A failed child makes the entire patch terminal.</remarks>
    ConfigPatchResult<T> Patch<T>(ConfigProviderRequest request, T? currentValue);
}
