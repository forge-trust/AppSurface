using System.Globalization;

namespace ForgeTrust.AppSurface.Theming;

/// <summary>Configures registered theme pairs and the host's default rendering mode.</summary>
public sealed class AppSurfaceThemeRegistryOptions
{
    /// <summary>Gets or sets the pair used by <see cref="IAppSurfaceThemeResolver.ResolveDefault"/>.</summary>
    public AppSurfaceThemeId DefaultTheme { get; set; } = new("appsurface");

    /// <summary>Gets or sets the host's default browser rendering mode.</summary>
    public AppSurfaceThemeMode DefaultMode { get; set; } = AppSurfaceThemeMode.System;

    /// <summary>Gets the registered semantic light/dark pairs.</summary>
    public IList<AppSurfaceThemePair> Pairs { get; } = [];
}

/// <summary>Looks up validated registered pairs by identifier.</summary>
public interface IAppSurfaceThemeRegistry
{
    /// <summary>Gets the non-empty, ordinally unique canonical ids of every sealed registered pair in registration order.</summary>
    /// <remarks>
    /// Each id returned by this collection must resolve through <see cref="GetRequired"/> to a pair whose
    /// <see cref="AppSurfaceThemePair.Id"/> equals that id. Consumers may reject implementations that do not
    /// maintain this sealed-snapshot contract.
    /// </remarks>
    IReadOnlyCollection<AppSurfaceThemeId> ThemeIds { get; }

    /// <summary>Gets a sealed pair by its canonical identifier.</summary>
    /// <param name="id">Registered pair identifier.</param>
    /// <returns>The sealed semantic pair whose <see cref="AppSurfaceThemePair.Id"/> equals <paramref name="id"/>.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when <paramref name="id"/> is not registered.</exception>
    AppSurfaceThemePair GetRequired(AppSurfaceThemeId id);
}

/// <summary>Resolves the host-selected default theme pair.</summary>
public interface IAppSurfaceThemeResolver
{
    /// <summary>Resolves the sealed pair and rendering mode configured by the host.</summary>
    /// <returns>The host's default semantic theme resolution.</returns>
    AppSurfaceThemeResolution ResolveDefault();
}

/// <summary>Classifies the safety impact of a theme diagnostic.</summary>
public enum AppSurfaceThemeDiagnosticSeverity
{
    /// <summary>Configuration cannot safely produce a theme payload.</summary>
    Error = 0,

    /// <summary>Host integration needs an explicit decision but can continue rendering.</summary>
    Warning = 1,
}

/// <summary>Provides a stable, safe theme configuration diagnostic.</summary>
/// <param name="Code">Stable <c>ASTHEME</c> diagnostic code.</param>
/// <param name="Severity">Safety impact of the condition.</param>
/// <param name="Problem">Safe one-line failure description.</param>
/// <param name="Cause">Safe explanation with applicable pair, role, or configuration path.</param>
/// <param name="Fix">Concrete remediation.</param>
/// <param name="Documentation">Canonical troubleshooting link.</param>
public sealed record AppSurfaceThemeDiagnostic(
    string Code,
    AppSurfaceThemeDiagnosticSeverity Severity,
    string Problem,
    string Cause,
    string Fix,
    Uri Documentation)
{
    /// <summary>Creates a diagnostic linked to the canonical theme troubleshooting reference.</summary>
    /// <param name="code">Stable diagnostic code.</param>
    /// <param name="problem">Safe one-line failure description.</param>
    /// <param name="cause">Safe explanation.</param>
    /// <param name="fix">Concrete remediation.</param>
    /// <param name="severity">Safety impact of the condition.</param>
    /// <returns>A stable diagnostic with its canonical documentation link.</returns>
    public static AppSurfaceThemeDiagnostic Create(
        string code,
        string problem,
        string cause,
        string fix,
        AppSurfaceThemeDiagnosticSeverity severity = AppSurfaceThemeDiagnosticSeverity.Error) =>
        new(
            code,
            severity,
            problem,
            cause,
            fix,
            new Uri($"https://appsurface.dev/docs/theming/diagnostics#{code.ToLowerInvariant()}"));
}

/// <summary>Thrown when a theme registry cannot produce a safe sealed snapshot.</summary>
public sealed class AppSurfaceThemeValidationException : InvalidOperationException
{
    /// <summary>Initializes an exception from one or more safe diagnostics.</summary>
    /// <param name="diagnostics">Diagnostics that prevented registry creation.</param>
    public AppSurfaceThemeValidationException(IReadOnlyList<AppSurfaceThemeDiagnostic> diagnostics)
        : base(CreateMessage(diagnostics))
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        Diagnostics = diagnostics.ToArray();
    }

    /// <summary>Gets all safe diagnostics that prevented registry creation.</summary>
    public IReadOnlyList<AppSurfaceThemeDiagnostic> Diagnostics { get; }

    private static string CreateMessage(IReadOnlyList<AppSurfaceThemeDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return "AppSurface theme configuration is invalid: "
            + string.Join(
                " ",
                diagnostics.Select(
                    diagnostic => $"{diagnostic.Code}: {diagnostic.Problem} Cause: {diagnostic.Cause} Fix: {diagnostic.Fix} Docs: {diagnostic.Documentation}"));
    }
}

/// <summary>Validates and resolves immutable semantic theme pairs.</summary>
public sealed class AppSurfaceThemeRegistry : IAppSurfaceThemeRegistry, IAppSurfaceThemeResolver
{
    private const double TextContrastRatio = 4.5d;
    private const double UserInterfaceContrastRatio = 3d;
    private readonly IReadOnlyDictionary<string, AppSurfaceThemePair> _pairs;
    private readonly IReadOnlyCollection<AppSurfaceThemeId> _themeIds;
    private readonly AppSurfaceThemeResolution _defaultResolution;

    /// <summary>Initializes a sealed registry from host configuration.</summary>
    /// <param name="options">Host-configured pairs and default mode.</param>
    /// <exception cref="AppSurfaceThemeValidationException">Thrown when configuration cannot safely produce a complete semantic pair.</exception>
    public AppSurfaceThemeRegistry(AppSurfaceThemeRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = Validate(options);
        if (diagnostics.Count > 0)
        {
            throw new AppSurfaceThemeValidationException(diagnostics);
        }

        var sealedPairs = options.Pairs.Select(ClonePair).ToArray();
        var pairs = sealedPairs.ToDictionary(pair => pair.Id.Value, StringComparer.Ordinal);
        _pairs = pairs;
        _themeIds = Array.AsReadOnly(sealedPairs.Select(pair => pair.Id).ToArray());

        var defaultPair = pairs[options.DefaultTheme.Value];
        _defaultResolution = new AppSurfaceThemeResolution(
            defaultPair.Id,
            options.DefaultMode,
            CloneRoles(defaultPair.Light),
            CloneRoles(defaultPair.Dark));
    }

    /// <inheritdoc />
    public IReadOnlyCollection<AppSurfaceThemeId> ThemeIds => _themeIds;

    /// <summary>Gets a sealed pair by its canonical identifier.</summary>
    /// <param name="id">Registered pair identifier.</param>
    /// <returns>A defensive snapshot of the registered pair.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when <paramref name="id"/> is not registered.</exception>
    public AppSurfaceThemePair GetRequired(AppSurfaceThemeId id)
    {
        if (string.IsNullOrEmpty(id.Value) || !_pairs.TryGetValue(id.Value, out var pair))
        {
            throw new KeyNotFoundException($"ASTHEME002: theme pair '{id}' is not registered.");
        }

        return ClonePair(pair);
    }

    /// <summary>Resolves the host-selected default pair and mode.</summary>
    /// <returns>A defensive snapshot of the default resolution.</returns>
    public AppSurfaceThemeResolution ResolveDefault() =>
        new(
            _defaultResolution.Id,
            _defaultResolution.Mode,
            CloneRoles(_defaultResolution.Light),
            CloneRoles(_defaultResolution.Dark));

    /// <summary>Validates a candidate registry configuration without creating a registry.</summary>
    /// <param name="options">Candidate host configuration.</param>
    /// <returns>All stable diagnostics that prevent safe registry creation.</returns>
    public static IReadOnlyList<AppSurfaceThemeDiagnostic> Validate(AppSurfaceThemeRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<AppSurfaceThemeDiagnostic>();
        if (!Enum.IsDefined(options.DefaultMode))
        {
            diagnostics.Add(
                AppSurfaceThemeDiagnostic.Create(
                    "ASTHEME001",
                    "Theme mode is not supported.",
                    "AppSurfaceThemeRegistryOptions.DefaultMode is outside the System, Light, Dark contract.",
                    "Set DefaultMode to System, Light, or Dark."));
        }

        if (options.Pairs.Count == 0)
        {
            diagnostics.Add(
                AppSurfaceThemeDiagnostic.Create(
                    "ASTHEME001",
                    "At least one semantic theme pair is required.",
                    "AppSurfaceThemeRegistryOptions.Pairs is empty.",
                    "Add a complete light/dark pair, such as AppSurfaceThemePair.AppSurface()."));
            return diagnostics;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in options.Pairs)
        {
            if (pair is null)
            {
                diagnostics.Add(
                    AppSurfaceThemeDiagnostic.Create(
                        "ASTHEME003",
                        "A theme pair cannot be null.",
                        "AppSurfaceThemeRegistryOptions.Pairs contains a null entry.",
                        "Replace the null entry with a complete AppSurfaceThemePair."));
                continue;
            }

            if (string.IsNullOrEmpty(pair.Id.Value))
            {
                diagnostics.Add(
                    AppSurfaceThemeDiagnostic.Create(
                        "ASTHEME003",
                        "Theme pair id is required.",
                        "A configured AppSurfaceThemePair has the default identifier value.",
                        "Create the pair with a canonical AppSurfaceThemeId."));
                continue;
            }

            if (!ids.Add(pair.Id.Value))
            {
                diagnostics.Add(
                    AppSurfaceThemeDiagnostic.Create(
                        "ASTHEME004",
                        $"Theme pair '{pair.Id}' is registered more than once.",
                        "Theme ids are compared using ordinal equality.",
                        "Keep exactly one pair for each canonical id."));
                continue;
            }

            ValidateRoles(pair.Id, "Light", pair.Light, diagnostics);
            ValidateRoles(pair.Id, "Dark", pair.Dark, diagnostics);
        }

        if (string.IsNullOrEmpty(options.DefaultTheme.Value) || !ids.Contains(options.DefaultTheme.Value))
        {
            diagnostics.Add(
                AppSurfaceThemeDiagnostic.Create(
                    "ASTHEME002",
                    "The default theme pair is not registered.",
                    $"AppSurfaceThemeRegistryOptions.DefaultTheme is '{options.DefaultTheme}'.",
                    "Set DefaultTheme to one of the registered canonical pair ids."));
        }

        return diagnostics;
    }

    /// <summary>
    /// Determines whether a sealed resolution can be safely consumed by a package-owned adapter.
    /// </summary>
    /// <param name="resolution">The resolution to validate.</param>
    /// <returns><see langword="true"/> when the resolution satisfies the neutral pair contract; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// This predicate is intended for adapters that need a fail-closed boundary without serializing a Web document.
    /// It applies the same identifier, mode, role, and contrast checks used for registry registration.
    /// </remarks>
    public static bool IsSafeResolution(AppSurfaceThemeResolution? resolution)
    {
        if (resolution is null
            || !Enum.IsDefined(resolution.Mode)
            || string.IsNullOrEmpty(resolution.Id.Value))
        {
            return false;
        }

        var options = new AppSurfaceThemeRegistryOptions
        {
            DefaultTheme = resolution.Id,
            DefaultMode = resolution.Mode
        };
        options.Pairs.Add(new AppSurfaceThemePair(resolution.Id, resolution.Light, resolution.Dark));
        return Validate(options).Count == 0;
    }

    private static void ValidateRoles(
        AppSurfaceThemeId id,
        string branch,
        AppSurfaceThemeRoles roles,
        List<AppSurfaceThemeDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var parsed = new Dictionary<string, RgbColor>(StringComparer.Ordinal);
        foreach (var (name, value) in EnumerateRoles(roles))
        {
            if (!RgbColor.TryParse(value, out var color))
            {
                diagnostics.Add(
                    AppSurfaceThemeDiagnostic.Create(
                        "ASTHEME005",
                        $"Theme pair '{id}' has an invalid {branch} {name} color.",
                        $"{branch}.{name} must be one opaque #RRGGBB value; configured value is '{value ?? "<null>"}'.",
                        "Use a six-digit opaque CSS hex color, for example #2563eb."));
                continue;
            }

            parsed.Add(name, color);
        }

        if (parsed.Count != 12)
        {
            return;
        }

        foreach (var textRole in new[] { "Text", "MutedText", "Link", "VisitedLink", "Danger" })
        {
            ValidateContrast(id, branch, textRole, parsed[textRole], TextContrastRatio, parsed, diagnostics);
        }

        foreach (var nonTextRole in new[] { "Border", "Accent", "AccentStrong", "Focus" })
        {
            ValidateContrast(id, branch, nonTextRole, parsed[nonTextRole], UserInterfaceContrastRatio, parsed, diagnostics);
        }
    }

    private static void ValidateContrast(
        AppSurfaceThemeId id,
        string branch,
        string role,
        RgbColor color,
        double requiredRatio,
        IReadOnlyDictionary<string, RgbColor> roles,
        List<AppSurfaceThemeDiagnostic> diagnostics)
    {
        foreach (var background in new[] { "Canvas", "Surface", "RaisedSurface" })
        {
            var actualRatio = RgbColor.ContrastRatio(color, roles[background]);
            if (actualRatio >= requiredRatio)
            {
                continue;
            }

            diagnostics.Add(
                AppSurfaceThemeDiagnostic.Create(
                    "ASTHEME101",
                    $"Theme pair '{id}' cannot render the {branch} {role} role safely.",
                    $"{branch}.{role} contrasts {background} at {actualRatio.ToString("0.00", CultureInfo.InvariantCulture)}:1; the required ratio is {requiredRatio.ToString("0.0", CultureInfo.InvariantCulture)}:1.",
                    "Choose an opaque #RRGGBB role value with sufficient contrast against Canvas, Surface, and RaisedSurface."));
        }
    }

    private static IEnumerable<(string Name, string? Value)> EnumerateRoles(AppSurfaceThemeRoles roles)
    {
        yield return ("Canvas", roles.Canvas);
        yield return ("Surface", roles.Surface);
        yield return ("RaisedSurface", roles.RaisedSurface);
        yield return ("Text", roles.Text);
        yield return ("MutedText", roles.MutedText);
        yield return ("Border", roles.Border);
        yield return ("Accent", roles.Accent);
        yield return ("AccentStrong", roles.AccentStrong);
        yield return ("Link", roles.Link);
        yield return ("VisitedLink", roles.VisitedLink);
        yield return ("Danger", roles.Danger);
        yield return ("Focus", roles.Focus);
    }

    private static AppSurfaceThemePair ClonePair(AppSurfaceThemePair pair) =>
        new(pair.Id, CloneRoles(pair.Light), CloneRoles(pair.Dark));

    private static AppSurfaceThemeRoles CloneRoles(AppSurfaceThemeRoles roles) =>
        new(
            roles.Canvas,
            roles.Surface,
            roles.RaisedSurface,
            roles.Text,
            roles.MutedText,
            roles.Border,
            roles.Accent,
            roles.AccentStrong,
            roles.Link,
            roles.VisitedLink,
            roles.Danger,
            roles.Focus);

    private readonly record struct RgbColor(byte Red, byte Green, byte Blue)
    {
        public static bool TryParse(string? value, out RgbColor color)
        {
            color = default;
            if (value is null || value.Length != 7 || value[0] != '#')
            {
                return false;
            }

            return byte.TryParse(value.AsSpan(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var red)
                && byte.TryParse(value.AsSpan(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var green)
                && byte.TryParse(value.AsSpan(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var blue)
                && Assign(red, green, blue, out color);
        }

        public static double ContrastRatio(RgbColor left, RgbColor right)
        {
            var leftLuminance = RelativeLuminance(left);
            var rightLuminance = RelativeLuminance(right);
            return (Math.Max(leftLuminance, rightLuminance) + 0.05) / (Math.Min(leftLuminance, rightLuminance) + 0.05);
        }

        private static bool Assign(byte red, byte green, byte blue, out RgbColor color)
        {
            color = new RgbColor(red, green, blue);
            return true;
        }

        private static double RelativeLuminance(RgbColor color)
        {
            var red = Linearize(color.Red / 255d);
            var green = Linearize(color.Green / 255d);
            var blue = Linearize(color.Blue / 255d);
            return (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
        }

        private static double Linearize(double channel) =>
            channel <= 0.04045
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }
}
