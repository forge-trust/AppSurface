using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>A shell-free child process request.</summary>
/// <param name="FileName">Executable name or path.</param>
/// <param name="Arguments">Arguments passed as a list, never through a shell.</param>
/// <param name="WorkingDirectory">Working directory of the child.</param>
internal sealed record ChildProcessRequest(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

/// <summary>The captured result of one child process.</summary>
/// <param name="Started">False when the executable could not be started; other values are then defaults.</param>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="StandardOutput">Captured standard output, at most the capture cap.</param>
/// <param name="StandardError">Captured standard error, at most the capture cap.</param>
/// <param name="OutputTruncated">True when either stream reached the capture cap or was abandoned at the drain bound.</param>
internal sealed record ChildProcessResult(bool Started, int ExitCode, string StandardOutput, string StandardError, bool OutputTruncated);

/// <summary>Starts child processes for the preview <c>test</c> producer.</summary>
internal interface IChildProcessRunner
{
    /// <summary>Runs one child to completion.</summary>
    /// <param name="request">Executable, arguments and working directory.</param>
    /// <param name="cancellationToken">Stops the whole process tree when cancelled; the call then throws.</param>
    /// <returns>The captured result.</returns>
    Task<ChildProcessResult> RunAsync(ChildProcessRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The one child runner of the <c>test</c> producer: closed standard input, a fixed environment, bounded in-memory
/// capture, process-tree kill, and a bounded drain.
/// </summary>
/// <remarks>
/// <para>
/// Why a separate type: the coverage runner needs a watchdog lease and streams output to log files, while this
/// producer must keep child output out of every file and console because it can contain test names.
/// </para>
/// <para>
/// Pitfalls it handles: a child that reads standard input would otherwise block until the deadline; an MSBuild node
/// or test host grandchild can hold the output pipes open after the child exits, so the drain is bounded by
/// <see cref="DrainTimeout"/> and then abandoned; build servers are switched off so no long-lived process inherits
/// the pipes.
/// </para>
/// </remarks>
internal sealed class BoundedChildProcessRunner : IChildProcessRunner
{
    /// <summary>Maximum bytes captured per stream, 8 MiB. Reaching it marks the result truncated.</summary>
    public const int MaximumCapturedBytesPerStream = 8 * 1024 * 1024;

    /// <summary>Environment applied to every child so output is English, quiet, and free of build servers.</summary>
    internal static readonly IReadOnlyDictionary<string, string> ChildEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        ["VSLANG"] = "1033",
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
    };

    private readonly TimeSpan _drainTimeout;

    /// <summary>Creates a runner.</summary>
    /// <param name="drainTimeout">Longest wait for output after exit or kill; defaults to <see cref="DrainTimeout"/>.</param>
    public BoundedChildProcessRunner(TimeSpan? drainTimeout = null)
    {
        _drainTimeout = drainTimeout ?? DrainTimeout;
    }

    /// <summary>Longest wait for output streams to close after the child exits or is killed: 5 seconds.</summary>
    public static TimeSpan DrainTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<ChildProcessResult> RunAsync(ChildProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in ChildEnvironment)
        {
            startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            return new ChildProcessResult(false, -1, string.Empty, string.Empty, false);
        }

        process.StandardInput.Close();
        var standardOutput = new BoundedCapture();
        var standardError = new BoundedCapture();
        var drain = Task.WhenAll(
            standardOutput.ReadAsync(process.StandardOutput.BaseStream),
            standardError.ReadAsync(process.StandardError.BaseStream));
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await DrainAsync(drain).ConfigureAwait(false);
            throw;
        }

        var drained = await DrainAsync(drain).ConfigureAwait(false);

        // Cancellation can arrive after the child exits and during the drain; honor it so the caller never treats a
        // cancelled step as a completed one.
        cancellationToken.ThrowIfCancellationRequested();
        return new ChildProcessResult(
            true,
            process.ExitCode,
            standardOutput.GetText(),
            standardError.GetText(),
            !drained || standardOutput.Truncated || standardError.Truncated);
    }

    private async Task<bool> DrainAsync(Task drain)
    {
        var completed = await Task.WhenAny(drain, Task.Delay(_drainTimeout)).ConfigureAwait(false);
        return ReferenceEquals(completed, drain);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process already exited or cannot be signalled; the drain bound still applies.
        }
    }

    private sealed class BoundedCapture
    {
        private readonly MemoryStream _buffer = new();
        private readonly object _sync = new();

        public bool Truncated { get; private set; }

        public async Task ReadAsync(Stream stream)
        {
            var chunk = new byte[16 * 1024];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
                {
                    lock (_sync)
                    {
                        var room = MaximumCapturedBytesPerStream - (int)_buffer.Length;
                        if (read > room)
                        {
                            Truncated = true;
                        }

                        if (room > 0)
                        {
                            _buffer.Write(chunk, 0, Math.Min(read, room));
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // The pipe closed while the process was being torn down.
            }
        }

        public string GetText()
        {
            lock (_sync)
            {
                return Encoding.UTF8.GetString(_buffer.GetBuffer(), 0, (int)_buffer.Length);
            }
        }
    }
}
