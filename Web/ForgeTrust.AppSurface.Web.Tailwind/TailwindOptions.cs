namespace ForgeTrust.AppSurface.Web.Tailwind;

/// <summary>
/// Configuration options for the Tailwind CSS integration.
/// </summary>
/// <remarks>
/// Use these options with <c>services.AddTailwind(...)</c> to control both build-time compilation and
/// development watch behavior.
/// Defaults:
/// <list type="bullet">
/// <item><description><see cref="Enabled"/> defaults to <c>true</c>.</description></item>
/// <item><description><see cref="InputPath"/> defaults to <c>wwwroot/css/app.css</c>.</description></item>
/// <item><description><see cref="OutputPath"/> defaults to <c>wwwroot/css/site.gen.css</c>.</description></item>
/// </list>
/// Paths are resolved relative to the app content root. Common misconfigurations include pointing at a missing
/// input file, using whitespace-only paths, or choosing an output path whose parent directory is not writable.
/// </remarks>
public class TailwindOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether Tailwind CSS integration is enabled.
    /// </summary>
    /// <remarks>
    /// Leave this enabled for normal development and build pipelines. Set it to <c>false</c> only when a host
    /// intentionally opts out of Tailwind compilation or provides CSS through another mechanism.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the path to the input CSS file.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>wwwroot/css/app.css</c>. The value should be a non-empty relative path to a readable
    /// <c>.css</c> file under the application content root.
    /// </remarks>
    public string InputPath { get; set; } = "wwwroot/css/app.css";

    /// <summary>
    /// Gets or sets the path to the output CSS file.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>wwwroot/css/site.gen.css</c>. The value should be a non-empty relative path whose parent
    /// directory exists and is writable. Keep the output under <c>wwwroot/</c> when the generated stylesheet
    /// needs to participate in ASP.NET Core static web asset discovery for build and publish output. Avoid
    /// pointing this at the same file as <see cref="InputPath"/>.
    /// </remarks>
    public string OutputPath { get; set; } = "wwwroot/css/site.gen.css";
}
