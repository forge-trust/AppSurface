using System.Diagnostics.CodeAnalysis;
using ForgeTrust.AppSurface.Cli;

/// <summary>
/// Process entry point for the AppSurface CLI executable.
/// </summary>
/// <remarks>
/// <see cref="ProgramEntryPoint"/> owns the command runtime seam and is covered by tests; this type only adapts the
/// .NET process entry point to that seam.
/// </remarks>
internal static class Program
{
    [ExcludeFromCodeCoverage(Justification = "Process entry-point trampoline; ProgramEntryPoint tests cover CLI startup behavior.")]
    private static Task Main(string[] args)
    {
        return ProgramEntryPoint.RunAsync(args);
    }
}
