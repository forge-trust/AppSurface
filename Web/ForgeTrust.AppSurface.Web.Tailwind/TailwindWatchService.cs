using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Web.Tailwind;

/// <summary>
/// A background service that runs the Tailwind CLI in watch mode during development.
/// </summary>
public class TailwindWatchService : BackgroundService
{
    private readonly TailwindCliManager _cliManager;
    private readonly TailwindOptions _options;
    private readonly ILogger<TailwindWatchService> _logger;
    private readonly IHostEnvironment _environment;

    /// <summary>
    /// Initializes a new instance of the <see cref="TailwindWatchService"/> class.
    /// </summary>
    /// <param name="cliManager">The CLI manager.</param>
    /// <param name="options">The Tailwind options.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="environment">The host environment.</param>
    public TailwindWatchService(
        TailwindCliManager cliManager,
        IOptions<TailwindOptions> options,
        ILogger<TailwindWatchService> logger,
        IHostEnvironment environment)
    {
        _cliManager = cliManager;
        _options = options.Value;
        _logger = logger;
        _environment = environment;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_environment.IsDevelopment() || !_options.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.InputPath))
        {
            _logger.LogError("Tailwind CSS: InputPath is not configured.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.OutputPath))
        {
            _logger.LogError("Tailwind CSS: OutputPath is not configured.");
            return;
        }

        try
        {
            var inputFullPath = Path.GetFullPath(_options.InputPath, _environment.ContentRootPath);
            var outputFullPath = Path.GetFullPath(_options.OutputPath, _environment.ContentRootPath);
            var pathComparison = GetPathComparison();
            if (string.Equals(inputFullPath, outputFullPath, pathComparison))
            {
                _logger.LogError("Tailwind CSS: InputPath and OutputPath must not point to the same file.");
                return;
            }

            var tailwindPath = _cliManager.GetTailwindPath();
            var args = new List<string>
            {
                "-i", _options.InputPath,
                "-o", _options.OutputPath,
                "--watch"
            };
            var invocation = TailwindCliManager.BuildInvocation(tailwindPath, args);

            _logger.LogInformation("Starting Tailwind CSS watch mode: {TailwindPath} {Args}", tailwindPath, string.Join(" ", args));

            var result = await ExecuteTailwindProcessAsync(
                invocation.FileName,
                invocation.Arguments,
                _environment.ContentRootPath,
                stoppingToken);

            if (result.ExitCode != 0 && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogError("Tailwind CSS watch process exited with code {ExitCode}. Check previous log entries for details.", result.ExitCode);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(
                ex,
                "Tailwind CSS watch mode is disabled because the Tailwind CLI could not be found. The app will continue serving existing CSS. Install the platform runtime package or set TailwindCliPath to enable watch mode.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Tailwind CSS watch process.");
        }
    }

    /// <summary>
    /// Executes the Tailwind CLI process.
    /// </summary>
    /// <remarks>
    /// Internal virtual to allow mocking in unit tests.
    /// </remarks>
    /// <param name="fileName">The path to the executable.</param>
    /// <param name="args">The arguments.</param>
    /// <param name="workingDirectory">The working directory.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The command result returned by the Tailwind CLI process.</returns>
    internal virtual Task<CommandResult> ExecuteTailwindProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        return ProcessUtils.ExecuteProcessAsync(
            fileName,
            args,
            workingDirectory,
            _logger,
            cancellationToken,
            streamOutput: true,
            stderrLogLevelSelector: GetTailwindStderrLogLevel);
    }

    /// <summary>
    /// Resolves the path-comparison behavior used when validating Tailwind input/output paths.
    /// </summary>
    /// <returns>The string-comparison behavior used when evaluating Tailwind input and output paths.</returns>
    /// <remarks>
    /// The default implementation only assumes case-insensitive paths on Windows so development hosts on case-sensitive
    /// volumes are not rejected by a false positive same-file check. Override <see cref="HostPathsAreCaseInsensitive"/>
    /// in tests or specialized hosts that know they should compare paths case-insensitively on another platform.
    /// </remarks>
    internal virtual StringComparison GetPathComparison()
    {
        return HostPathsAreCaseInsensitive() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    /// <summary>
    /// Determines whether the current host should treat filesystem paths as case-insensitive for Tailwind path validation.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the host should conservatively compare input and output paths case-insensitively;
    /// otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// The default behavior treats only Windows as case-insensitive. Hosts that run on a known case-insensitive
    /// non-Windows volume can override this method to opt into <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// </remarks>
    internal virtual bool HostPathsAreCaseInsensitive()
    {
        return OperatingSystem.IsWindows();
    }

    private static LogLevel GetTailwindStderrLogLevel(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return LogLevel.Debug;
        }

        if (line.StartsWith("≈ tailwindcss v", StringComparison.Ordinal) ||
            line.StartsWith("Done in ", StringComparison.Ordinal))
        {
            return LogLevel.Information;
        }

        return LogLevel.Error;
    }
}
