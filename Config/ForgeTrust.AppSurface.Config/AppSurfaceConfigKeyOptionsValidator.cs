using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Validates the default host key policy without depending on the declaration registry.</summary>
/// <remarks>
/// Its implementation descriptor identifies the framework's startup-validation installation bundle. Other caller
/// validators remain independent. Keeping this validator free of registry dependencies prevents a parser/options cycle.
/// </remarks>
internal sealed class AppSurfaceConfigKeyOptionsValidator : IValidateOptions<AppSurfaceConfigKeyOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AppSurfaceConfigKeyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (name != Options.DefaultName)
        {
            return ValidateOptionsResult.Skip;
        }

        return Enum.IsDefined(options.LegacyDotPathBehavior)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("LegacyDotPathBehavior must name a supported mode.");
    }
}
