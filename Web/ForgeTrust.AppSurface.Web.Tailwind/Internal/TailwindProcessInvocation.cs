namespace ForgeTrust.AppSurface.Web.Tailwind.Internal;

/// <summary>
/// Describes the executable and ordered argument list used to launch Tailwind.
/// </summary>
/// <param name="FileName">The executable or launcher to start.</param>
/// <param name="Arguments">The complete ordered argument list passed to <paramref name="FileName" />.</param>
internal sealed record TailwindProcessInvocation(string FileName, IReadOnlyList<string> Arguments);
