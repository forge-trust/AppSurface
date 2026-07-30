# ForgeTrust.AppSurface.Theming

`ForgeTrust.AppSurface.Theming` defines immutable semantic light/dark pairs for AppSurface-owned UI. It is deliberately not a site-wide design system: it does not style application components, choose a user preference, persist a cookie, inspect a request, implement tenant policy, or load remote theme packs.

For an ASP.NET Core application, begin with the [Web theme-pairs quickstart](../Web/ForgeTrust.AppSurface.Web/README.md#theme-pairs-quickstart). `ForgeTrust.AppSurface.Web` references this package and supplies the Razor integration. Install this package directly only when authoring another package-owned adapter.

## Contract

A pair has one canonical lowercase identifier and a complete `Light` and `Dark` set of these roles:

| Role | Used for |
| --- | --- |
| `Canvas`, `Surface`, `RaisedSurface` | Package-owned page and layered surfaces. |
| `Text`, `MutedText`, `Border` | Readable content, secondary content, and structural boundaries. |
| `Accent`, `AccentStrong`, `Focus` | Active states, emphasis, and visible keyboard focus. |
| `Link`, `VisitedLink`, `Danger` | Links and recoverable/destructive error treatment. |

Shared role values must be opaque `#RRGGBB` colors. Configuration seals them at registration time and checks text-role contrast at 4.5:1 plus non-text affordances at 3:1 against every shared surface. The built-in `AppSurfaceThemePair.AppSurface()` pair is the default example.

```csharp
using ForgeTrust.AppSurface.Theming;

services.AddAppSurfaceTheming(options =>
{
    options.DefaultTheme = new AppSurfaceThemeId("appsurface");
    options.DefaultMode = AppSurfaceThemeMode.System;
    options.Pairs.Add(AppSurfaceThemePair.AppSurface());
});
```

`System` emits both branches and lets browser CSS select `prefers-color-scheme`. `Light` and `Dark` emit only the selected branch. This is host configuration, not per-user policy.

## Application-specific settings

An application can provide typed settings to a package-owned adapter through `IAppSurfaceThemeExtensionProvider<TSettings>`. The neutral package treats the setting as opaque: it neither validates, serializes, logs, nor renders it.

```csharp
sealed record AcmeThemeSettings(string ProductMarkUrl, bool ShowReleaseRail);

sealed class AcmeThemeSettingsProvider : IAppSurfaceThemeExtensionProvider<AcmeThemeSettings>
{
    public bool TryGet(AppSurfaceThemeId themeId, out AcmeThemeSettings settings)
    {
        settings = new AcmeThemeSettings("/assets/acme-mark.svg", true);
        return themeId.Value == "appsurface";
    }
}
```

Register the provider and call `AddRequiredThemeExtension<TSettings>()` when every configured pair must have settings. The neutral registry checks provider presence and calls `TryGet` once per sealed pair when it is first resolved; it reports `ASTHEME201` for a missing provider and `ASTHEME202` for a missing or null pair setting.

```csharp
services.AddSingleton<IAppSurfaceThemeExtensionProvider<AcmeThemeSettings>, AcmeThemeSettingsProvider>();
services.AddRequiredThemeExtension<AcmeThemeSettings>();
```

Use this seam when a specific adapter needs app-owned configuration. Do not add arbitrary metadata bags or promote application concepts into shared semantic roles. The application still owns settings-schema, asset, and authorization validation.

## Diagnostics and pitfalls

Every startup diagnostic follows **Problem / Cause / Fix / Docs** ordering and is safe to surface to an operator:

- `ASTHEME001` — no usable pairs or unsupported mode.
- `ASTHEME002` — default pair is not registered.
- `ASTHEME003` — missing/invalid pair identity.
- `ASTHEME004` — duplicate canonical pair id.
- `ASTHEME005` — a role is not one opaque `#RRGGBB` value.
- `ASTHEME101` — a role fails its required contrast ratio.
- `ASTHEME201` — an opted-in extension provider is not registered.
- `ASTHEME202` — an opted-in extension provider has no setting for a registered pair.

The Web adapter preserves a conflicting host `color-scheme` declaration and exposes `data-as-theme-color-scheme-conflict="true"`; resolve the conflict explicitly rather than expecting AppSurface to overwrite host CSS.

Do not use this package for a switcher, local-storage/cookie precedence, tenant lookup, or client-hint selection. Those features require a separate policy, privacy, CSP, caching, and first-paint design. See the repository [deferred work list](../TODOS.md) for the current follow-ons.
