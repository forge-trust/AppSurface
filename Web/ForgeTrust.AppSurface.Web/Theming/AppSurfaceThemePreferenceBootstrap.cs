using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

namespace ForgeTrust.AppSurface.Web.Theming;

internal sealed class AppSurfaceThemePreferenceBootstrap
{
    internal const string Script = """
        (() => { "use strict"; const script = document.currentScript, root = document.documentElement, key = script && script.dataset.asThemeStorageKey; if (!key || root.hasAttribute("data-as-theme-color-scheme-conflict")) return; const mode = value => value === "light" || value === "dark" ? value : "system"; const apply = value => { const selected = mode(value); root.dataset.asThemeMode = selected; return selected; }; let storage; try { storage = window.localStorage; apply(storage.getItem(key)); } catch { apply("system"); } const announce = (selected, persistence, source) => window.dispatchEvent(new CustomEvent("appsurface-theme-preference-change", { detail: { mode: selected, persistence, source } })); const bind = () => { const controls = [...document.querySelectorAll("[data-as-theme-preference-control]")]; const sync = selected => controls.forEach(control => { control.hidden = false; control.querySelectorAll("input[type=radio]").forEach(input => input.checked = input.value === selected); }); const choose = value => { const selected = apply(value); let persistence = "session"; try { if (selected === "system") storage.removeItem(key); else storage.setItem(key, selected); persistence = selected === "system" ? "system" : "stored"; } catch { } sync(selected); announce(selected, persistence, "control"); }; sync(mode(root.dataset.asThemeMode)); controls.forEach(control => control.addEventListener("change", event => { const input = event.target; if (input instanceof HTMLInputElement && input.type === "radio") choose(input.value); })); window.addEventListener("storage", event => { if (event.storageArea === storage && (event.key === key || event.key === null)) { const selected = apply(event.newValue); sync(selected); announce(selected, selected === "system" ? "system" : "stored", "storage"); } }); }; if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", bind, { once: true }); else bind(); })();
        """;

    internal AppSurfaceThemePreferenceBootstrap(AppSurfaceThemePreferenceOptions options)
    {
        StorageKey = options?.StorageKey ?? throw new ArgumentNullException(nameof(options));
    }

    internal string StorageKey { get; }

    internal static string CspHash { get; } = "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Script)));

    internal string Render(string? nonce)
    {
        var builder = new StringBuilder("<script data-as-theme-preference-bootstrap data-as-theme-storage-key=\"")
            .Append(HtmlEncoder.Default.Encode(StorageKey))
            .Append('"');
        if (!string.IsNullOrEmpty(nonce))
        {
            builder.Append(" nonce=\"").Append(HtmlEncoder.Default.Encode(nonce)).Append('"');
        }

        return builder.Append('>').Append(Script).Append("</script>\n").ToString();
    }
}

/// <summary>Provides the stable CSP source hash for the opt-in theme-preference bootstrap.</summary>
/// <remarks>
/// The hash covers only the deterministic inline script emitted by <c>&lt;appsurface-theme-head&gt;</c> after
/// <see cref="AppSurfaceWebThemingServiceCollectionExtensions.AddAppSurfaceWebThemePreferences(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{AppSurfaceThemePreferenceOptions}?)"/>.
/// The configured storage key is supplied by an encoded data attribute, so it does not change this value. A dynamic
/// host can instead supply its request nonce through the TagHelper. Static hosts remain responsible for separately
/// hashing their generated critical styles.
/// </remarks>
public static class AppSurfaceThemePreferenceCsp
{
    /// <summary>Gets the <c>sha256-</c> source expression for the deterministic bootstrap script.</summary>
    public static string ScriptHash => AppSurfaceThemePreferenceBootstrap.CspHash;
}
