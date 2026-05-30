namespace ForgeTrust.AppSurface.Web.Tailwind.Internal;

/// <summary>
/// Represents the completed result of a Tailwind CLI process.
/// </summary>
/// <param name="ExitCode">The process exit code returned by the Tailwind CLI.</param>
/// <param name="Stdout">The captured standard-output tail, bounded by the caller's capture limit.</param>
/// <param name="Stderr">The captured standard-error tail, bounded by the caller's capture limit.</param>
internal sealed record TailwindCommandResult(int ExitCode, string Stdout, string Stderr);
