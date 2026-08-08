using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Theming;

/// <summary>Identifies the host-selected rendering behavior for a semantic theme pair.</summary>
public enum AppSurfaceThemeMode
{
    /// <summary>Emits both branches and lets browser CSS select the operating-system preference.</summary>
    System = 0,

    /// <summary>Emits only the light branch.</summary>
    Light = 1,

    /// <summary>Emits only the dark branch.</summary>
    Dark = 2,
}

/// <summary>Identifies a registered semantic theme pair.</summary>
public readonly record struct AppSurfaceThemeId
{
    private static readonly Regex Pattern = new("^[a-z](?:[a-z0-9]|-(?=[a-z0-9])){0,62}\\z", RegexOptions.CultureInvariant);

    /// <summary>Initializes a canonical theme-pair identifier.</summary>
    /// <param name="value">A lowercase identifier beginning with a letter and containing only lowercase letters, digits, and single interior hyphens.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is not a canonical theme-pair identifier.</exception>
    public AppSurfaceThemeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Pattern.IsMatch(value))
        {
            throw new ArgumentException(
                "ASTHEME003: theme ids must begin with a lowercase letter, contain only lowercase letters or digits separated by single interior hyphens, and be at most 63 characters.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the canonical identifier value.</summary>
    public string Value { get; }

    /// <summary>Creates a canonical theme-pair identifier from a string literal.</summary>
    /// <param name="value">Canonical theme-pair identifier text.</param>
    /// <returns>The canonical identifier.</returns>
    public static implicit operator AppSurfaceThemeId(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Defines the minimum semantic color roles consumed by AppSurface-owned UI.</summary>
public sealed record AppSurfaceThemeRoles
{
    /// <summary>Initializes a complete semantic role set.</summary>
    /// <param name="canvas">Primary page canvas color.</param>
    /// <param name="surface">Default content-surface color.</param>
    /// <param name="raisedSurface">Raised content-surface color.</param>
    /// <param name="text">Primary readable text color.</param>
    /// <param name="mutedText">Secondary readable text color.</param>
    /// <param name="border">Visible border and divider color.</param>
    /// <param name="accent">Default accent color.</param>
    /// <param name="accentStrong">High-emphasis accent color.</param>
    /// <param name="link">Unvisited link color.</param>
    /// <param name="visitedLink">Visited link color.</param>
    /// <param name="danger">Error and destructive-action color.</param>
    /// <param name="focus">Keyboard-visible focus color.</param>
    public AppSurfaceThemeRoles(
        string canvas,
        string surface,
        string raisedSurface,
        string text,
        string mutedText,
        string border,
        string accent,
        string accentStrong,
        string link,
        string visitedLink,
        string danger,
        string focus)
    {
        Canvas = canvas;
        Surface = surface;
        RaisedSurface = raisedSurface;
        Text = text;
        MutedText = mutedText;
        Border = border;
        Accent = accent;
        AccentStrong = accentStrong;
        Link = link;
        VisitedLink = visitedLink;
        Danger = danger;
        Focus = focus;
    }

    /// <summary>Gets the primary page canvas color.</summary>
    public string Canvas { get; }

    /// <summary>Gets the default content-surface color.</summary>
    public string Surface { get; }

    /// <summary>Gets the raised content-surface color.</summary>
    public string RaisedSurface { get; }

    /// <summary>Gets the primary readable text color.</summary>
    public string Text { get; }

    /// <summary>Gets the secondary readable text color.</summary>
    public string MutedText { get; }

    /// <summary>Gets the visible border and divider color.</summary>
    public string Border { get; }

    /// <summary>Gets the default accent color.</summary>
    public string Accent { get; }

    /// <summary>Gets the high-emphasis accent color.</summary>
    public string AccentStrong { get; }

    /// <summary>Gets the unvisited link color.</summary>
    public string Link { get; }

    /// <summary>Gets the visited link color.</summary>
    public string VisitedLink { get; }

    /// <summary>Gets the error and destructive-action color.</summary>
    public string Danger { get; }

    /// <summary>Gets the keyboard-visible focus color.</summary>
    public string Focus { get; }
}

/// <summary>Pairs complete light and dark semantic role sets under one identifier.</summary>
public sealed record AppSurfaceThemePair
{
    /// <summary>Initializes a semantic light/dark pair.</summary>
    /// <param name="id">Canonical pair identifier.</param>
    /// <param name="light">Complete light role set.</param>
    /// <param name="dark">Complete dark role set.</param>
    public AppSurfaceThemePair(AppSurfaceThemeId id, AppSurfaceThemeRoles light, AppSurfaceThemeRoles dark)
    {
        Id = id;
        Light = light ?? throw new ArgumentNullException(nameof(light));
        Dark = dark ?? throw new ArgumentNullException(nameof(dark));
    }

    /// <summary>Gets the canonical pair identifier.</summary>
    public AppSurfaceThemeId Id { get; }

    /// <summary>Gets the complete light role set.</summary>
    public AppSurfaceThemeRoles Light { get; }

    /// <summary>Gets the complete dark role set.</summary>
    public AppSurfaceThemeRoles Dark { get; }

    /// <summary>Creates AppSurface's built-in accessible semantic pair.</summary>
    /// <returns>A new immutable pair instance named <c>appsurface</c>.</returns>
    public static AppSurfaceThemePair AppSurface() =>
        new(
            new AppSurfaceThemeId("appsurface"),
            new AppSurfaceThemeRoles(
                "#f8fafc", "#ffffff", "#f1f5f9", "#0f172a", "#475569", "#64748b",
                "#2563eb", "#1d4ed8", "#1d4ed8", "#6d28d9", "#b91c1c", "#1d4ed8"),
            new AppSurfaceThemeRoles(
                "#0f172a", "#172554", "#1e293b", "#f8fafc", "#cbd5e1", "#94a3b8",
                "#60a5fa", "#93c5fd", "#93c5fd", "#c4b5fd", "#fca5a5", "#facc15"));

    /// <summary>Creates the built-in Graphite semantic light/dark pair.</summary>
    /// <returns>A new immutable pair instance named <c>graphite</c>.</returns>
    /// <remarks>
    /// Graphite is a shared semantic pair for hosts that register AppSurface theming. It is distinct from the
    /// Docs-local fixed-dark <c>GraphiteDark</c> compatibility preset, which does not register a shared pair.
    /// </remarks>
    public static AppSurfaceThemePair Graphite() =>
        new(
            new AppSurfaceThemeId("graphite"),
            new AppSurfaceThemeRoles(
                "#f7f7f8", "#ffffff", "#eef0f2", "#17212b", "#52606d", "#6b7280",
                "#0369a1", "#075985", "#075985", "#6b21a8", "#b42318", "#0369a1"),
            new AppSurfaceThemeRoles(
                "#080a0d", "#101216", "#151820", "#f8fafc", "#c9ced8", "#647085",
                "#38bdf8", "#818cf8", "#93c5fd", "#c4b5fd", "#fda4af", "#facc15"));
}

/// <summary>Represents the sealed default pair together with its host-selected mode.</summary>
public sealed record AppSurfaceThemeResolution
{
    /// <summary>Initializes a sealed theme resolution.</summary>
    /// <param name="id">Resolved pair identifier.</param>
    /// <param name="mode">Host-selected rendering mode.</param>
    /// <param name="light">Sealed light role set.</param>
    /// <param name="dark">Sealed dark role set.</param>
    public AppSurfaceThemeResolution(
        AppSurfaceThemeId id,
        AppSurfaceThemeMode mode,
        AppSurfaceThemeRoles light,
        AppSurfaceThemeRoles dark)
    {
        Id = id;
        Mode = mode;
        Light = light ?? throw new ArgumentNullException(nameof(light));
        Dark = dark ?? throw new ArgumentNullException(nameof(dark));
    }

    /// <summary>Gets the resolved pair identifier.</summary>
    public AppSurfaceThemeId Id { get; }

    /// <summary>Gets the host-selected rendering mode.</summary>
    public AppSurfaceThemeMode Mode { get; }

    /// <summary>Gets the sealed light role set.</summary>
    public AppSurfaceThemeRoles Light { get; }

    /// <summary>Gets the sealed dark role set.</summary>
    public AppSurfaceThemeRoles Dark { get; }
}

/// <summary>Allows an adapter to obtain application-owned settings for a registered pair.</summary>
/// <typeparam name="TSettings">Application-owned settings type. Neutral theming neither validates nor serializes it.</typeparam>
public interface IAppSurfaceThemeExtensionProvider<TSettings>
    where TSettings : notnull
{
    /// <summary>Attempts to get settings for the supplied sealed pair.</summary>
    /// <param name="themeId">Registered theme-pair identifier.</param>
    /// <param name="settings">Application-owned settings when available.</param>
    /// <returns><see langword="true"/> when settings exist for <paramref name="themeId"/>; otherwise <see langword="false"/>.</returns>
    bool TryGet(AppSurfaceThemeId themeId, [MaybeNullWhen(false)] out TSettings settings);
}
