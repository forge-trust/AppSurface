namespace System.Runtime.CompilerServices;

/// <summary>
/// Internal compatibility shim that enables init-only members when the target framework does not provide
/// <c>System.Runtime.CompilerServices.IsExternalInit</c>.
/// </summary>
/// <remarks>
/// This type is implementation detail for the generator assembly only. Keep it internal, minimal, and behavior-free;
/// it must not become part of any public AppSurface contract.
/// </remarks>
internal static class IsExternalInit
{
}
