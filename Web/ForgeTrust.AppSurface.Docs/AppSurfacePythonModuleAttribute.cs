namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Declares that a documented top-level C# type owns one repository-relative Python module in the AppSurface Docs
/// polyglot API reference.
/// </summary>
/// <remarks>
/// Apply this attribute to a documented class, struct, interface, or record when that .NET type is the primary host
/// integration for a Python module. The value must be a literal, repository-relative <c>.py</c> path with forward
/// slashes and without <c>.</c> or <c>..</c> segments, for example <c>python/workflows/worker.py</c>. AppSurface Docs
/// reads the attribute syntax while harvesting C#; it does not construct the attribute, import the Python module, or
/// execute Python source. A reciprocal link is published only when the target is also accepted by the opt-in Python
/// harvester and exactly one documented C# host declares ownership.
/// </remarks>
/// <param name="modulePath">Literal repository-relative path of the owned Python module.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class AppSurfacePythonModuleAttribute(string modulePath) : Attribute
{
    /// <summary>
    /// Gets the literal repository-relative path of the owned Python module.
    /// </summary>
    public string ModulePath { get; } = modulePath;
}
