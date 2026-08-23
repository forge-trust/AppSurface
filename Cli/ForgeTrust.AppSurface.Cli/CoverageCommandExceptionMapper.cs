using System.Text;
using CliFx;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Converts private coverage-core failures into the stable CliFx command contract.
/// </summary>
internal static class CoverageCommandExceptionMapper
{
    /// <summary>
    /// Maps a core failure without changing its rendered diagnostic or terminal exit code.
    /// </summary>
    public static CommandException Map(CoverageExecutionException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.ExitCode is { } exitCode
            ? new CommandException(exception.Message, exitCode)
            : new CommandException(exception.Message);
    }
}

/// <summary>
/// Creates stable command-layer validation diagnostics before a request enters the coverage core.
/// </summary>
internal static class CoverageRunDiagnostics
{
    /// <summary>
    /// Creates a stable CLI diagnostic using the existing coverage message shape.
    /// </summary>
    public static CommandException Create(
        string code,
        string problem,
        string cause,
        string fix,
        string docs,
        string? logPath = null)
    {
        var builder = new StringBuilder();
        builder.Append(code).Append(' ').Append(problem);
        builder.Append(" Cause: ").Append(cause);
        builder.Append(" Fix: ").Append(fix);
        builder.Append(" Docs: ").Append(docs);
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            builder.Append(" Log: ").Append(logPath);
        }

        return new CommandException(builder.ToString());
    }
}
