using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using Markdig;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Command runner abstraction for git and GitHub CLI calls.
/// </summary>
internal interface ICommandRunner
{
    /// <summary>
    /// Runs a process and captures stdout and stderr.
    /// </summary>
    /// <param name="invocation">Command invocation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Command result.</returns>
    Task<CommandResult> RunAsync(CommandInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>
/// Process command runner used by the default CLI.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Process execution is covered through injected command runners so tests do not depend on local git or gh state.")]
internal sealed class ProcessCommandRunner : ICommandRunner
{
    /// <inheritdoc />
    public async Task<CommandResult> RunAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(invocation.Executable)
        {
            WorkingDirectory = invocation.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new CommandResult(process.ExitCode, await stdout, await stderr);
    }
}

/// <summary>
/// Clock abstraction used by tests to make generated release dates deterministic.
/// </summary>
internal interface IReleaseClock
{
    /// <summary>
    /// Gets today's UTC date.
    /// </summary>
    /// <returns>UTC date.</returns>
    DateOnly TodayUtc();
}

/// <summary>
/// System clock used by the default CLI.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "System clock wiring is covered through deterministic clock seams in workflow tests.")]
internal sealed class SystemReleaseClock : IReleaseClock
{
    /// <inheritdoc />
    public DateOnly TodayUtc() => DateOnly.FromDateTime(DateTime.UtcNow);
}
