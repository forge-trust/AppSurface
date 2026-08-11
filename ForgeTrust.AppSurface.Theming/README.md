# ForgeTrust.AppSurface.Theming

`ForgeTrust.AppSurface.Theming` defines immutable semantic light/dark pairs for AppSurface-owned UI. It is deliberately not a site-wide design system: it does not style application components, choose a user preference, persist a cookie, inspect a request, implement tenant policy, or load remote theme packs.

For an ASP.NET Core application, begin with the [Web theme-pairs quickstart](../Web/ForgeTrust.AppSurface.Web/README.md#theme-pairs-quickstart). `ForgeTrust.AppSurface.Web` references this package and supplies the Razor integration. Install this package directly only when authoring another package-owned adapter.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->
## Contract

A pair has one canonical lowercase identifier (letter-led, 63 characters or fewer, with only lowercase letters, digits, and single interior hyphens) and a complete `Light` and `Dark` set of these roles:

| Role | Used for |
| --- | --- |
| `Canvas`, `Surface`, `RaisedSurface` | Package-owned page and layered surfaces. |
| `Text`, `MutedText`, `Border` | Readable content, secondary content, and structural boundaries. |
| `Accent`, `AccentStrong`, `Focus` | Active states, emphasis, and visible keyboard focus. |
| `Link`, `VisitedLink`, `Danger` | Links and recoverable/destructive error treatment. |

Shared role values must be opaque `#RRGGBB` colors. Configuration seals them at registration time and checks [WCAG 2.2 text contrast](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html) at 4.5:1 plus [non-text contrast](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast.html) at 3:1 against every shared surface. These checks apply to the shared semantic tokens; they do not certify application-authored CSS. `AppSurfaceThemePair.Graphite()` is an evidence-gated shared Light/Dark pair: use it when the host has evidence for both branches, and let registration fail closed when the pair is incomplete or unsafe. It is distinct from the Docs-local fixed-dark `GraphiteDark` preset.

```csharp
using ForgeTrust.AppSurface.Theming;

services.AddAppSurfaceTheming(options =>
{
    options.DefaultTheme = new AppSurfaceThemeId("graphite");
    options.DefaultMode = AppSurfaceThemeMode.System;
    options.Pairs.Add(AppSurfaceThemePair.Graphite());
});
```

`System` emits both branches and lets browser CSS select `prefers-color-scheme`. `Light` and `Dark` emit only the selected branch. This is host configuration, not per-user policy.

`Graphite` is the shared pair identifier and carries both semantic Light and Dark role sets through the Web and Docs bridge. `GraphiteDark` is not that pair: it is a Docs-local fixed-dark compatibility preset. Browser-local preferences are a separate Web opt-in that stores only a presentation choice in the browser; they do not change the registered pair or make Docs' local preset a shared theme.

For a presentation-only browser choice, use the Web-layer [browser-local preference adapter](../Web/ForgeTrust.AppSurface.Web/README.md#browser-local-theme-preferences). It is an explicit opt-in that preserves one canonical HTML document, stores only `light` or `dark` in browser-local storage, and falls back to this package's System CSS. For an already-authorized request context that must choose a registered pair, use the Web-layer [host-owned selection adapter](../Web/ForgeTrust.AppSurface.Web/README.md#host-owned-theme-selection). Account synchronization, tenant lookup, identity normalization, authorization, mapping storage, cache partitioning, invalidation, consent decisions, and content-varying policy remain application concerns and do not belong in this neutral package.

Package-owned adapters that need a fail-closed boundary can call `AppSurfaceThemeRegistry.IsSafeResolution(resolution)`. It applies the same identity, role, and contrast contract as startup registration without depending on or allocating a Web document.

## Application-specific settings

An application can provide typed settings to a package-owned adapter through `IAppSurfaceThemeExtensionProvider<TSettings>`. The neutral package treats the setting as opaque: it neither validates, serializes, logs, nor renders it.

```csharp
sealed record AcmeThemeSettings(string ProductMarkUrl, bool ShowReleaseRail);

sealed class AcmeThemeSettingsProvider : IAppSurfaceThemeExtensionProvider<AcmeThemeSettings>
{
    public bool TryGet(AppSurfaceThemeId themeId, out AcmeThemeSettings settings)
    {
        if (themeId.Value == "appsurface")
        {
            settings = new AcmeThemeSettings("/assets/acme-mark.svg", true);
            return true;
        }

        settings = null!;
        return false;
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

Do not use this package for a switcher, local-storage/cookie precedence, tenant lookup, or client-hint selection. The supported browser-local and request-aware adapters live in [AppSurface Web](../Web/ForgeTrust.AppSurface.Web/README.md#browser-local-theme-preferences); cookie, account, tenant, client-hint, and content-varying approaches still require a separate privacy, CSP, caching, and first-paint design. Related follow-on policy work is tracked in [#706](https://github.com/forge-trust/AppSurface/issues/706), [#707](https://github.com/forge-trust/AppSurface/issues/707), and [#708](https://github.com/forge-trust/AppSurface/issues/708).
