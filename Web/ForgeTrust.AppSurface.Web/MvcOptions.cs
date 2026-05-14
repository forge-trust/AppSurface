using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Represents configuration options for ASP.NET Core MVC services and features.
/// </summary>
public record MvcOptions
{
    /// <summary>
    /// Gets a new default instance of <see cref="MvcOptions"/> configured with <see cref="MvcSupport.Controllers"/>.
    /// </summary>
    public static MvcOptions Default => new();

    /// <summary>
    /// Gets the level of MVC support to register (e.g., Controllers only, or Controllers with Views).
    /// </summary>
    public MvcSupport MvcSupportLevel { get; init; } = MvcSupport.Controllers;

    /// <summary>
    /// Gets an optional delegate for performing advanced configuration of the <see cref="IMvcBuilder"/>.
    /// </summary>
    public Action<IMvcBuilder>? ConfigureMvc { get; init; }
}

/// <summary>
/// Specifies the level of MVC feature support to enable in the web application.
/// </summary>
/// <remarks>
/// The numeric values are explicit because this public enum may be persisted, serialized, or bound by
/// applications. New values should be appended without changing the values documented here.
/// </remarks>
public enum MvcSupport
{
    /// <summary>
    /// No MVC services are registered.
    /// </summary>
    None = 0,

    /// <summary>
    /// Only basic controller support (without views) is registered via <c>AddControllers()</c>.
    /// </summary>
    Controllers = 1,

    /// <summary>
    /// Controller and Razor View support is registered via <c>AddControllersWithViews()</c>.
    /// </summary>
    ControllersWithViews = 2,

    /// <summary>
    /// Full MVC support, including Pages and other features, is registered via <c>AddMvc()</c>.
    /// </summary>
    Full = 3
}
