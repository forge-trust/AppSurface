using System.Diagnostics;
using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Identifies a bounded command execution failure without exposing command inputs or environment values.</summary>
public sealed class EvidenceCommandException : Exception
{
    /// <summary>Creates a command exception with a stable, secret-safe code.</summary>
    public EvidenceCommandException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Gets the stable failure classification.</summary>
    public string Code { get; }
}

/// <summary>Describes one explicit, bounded command invocation.</summary>
/// <param name="FileName">Executable name or path passed directly to the operating system.</param>
/// <param name="Arguments">Arguments passed as individual argv entries; no shell parsing is performed.</param>
/// <param name="WorkingDirectory">Absolute working directory for the child process.</param>
/// <param name="Timeout">Positive maximum execution duration, at most 4,294,967,294 milliseconds.</param>
public sealed record EvidenceCommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout)
{
    /// <summary>Gets explicit environment entries added to the child process.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets the combined UTF-8 byte budget for stdout and stderr.</summary>
    public long MaximumOutputBytes { get; init; } = 1024 * 1024;

    /// <summary>Gets an optional consumer budget check polled at most 250 milliseconds apart.</summary>
    public Func<CancellationToken, ValueTask>? CheckBudget { get; init; }
}

/// <summary>Contains bounded output and timing from a completed command.</summary>
/// <param name="ExitCode">Operating-system exit code.</param>
/// <param name="StandardOutput">Captured UTF-8 stdout, truncated by the request's total byte budget.</param>
/// <param name="StandardError">Captured UTF-8 stderr, truncated by the request's total byte budget.</param>
/// <param name="ElapsedMilliseconds">Elapsed wall-clock execution time.</param>
public sealed record EvidenceCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    long ElapsedMilliseconds)
{
    /// <summary>Gets whether the command exceeded its combined output capture budget.</summary>
    public bool OutputTruncated { get; init; }
}

/// <summary>Runs explicit argv commands with bounded output and owned process-tree cleanup.</summary>
public sealed class EvidenceCommandRunner
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);
    private readonly Func<Stream, Stream> _captureStream;

    /// <summary>Creates a runner that captures the child process's output streams.</summary>
    public EvidenceCommandRunner() : this(static stream => stream) { }

    /// <summary>Creates a runner with a stream adapter for deterministic capture-failure verification.</summary>
    internal EvidenceCommandRunner(Func<Stream, Stream> captureStream)
    {
        _captureStream = captureStream;
    }

    /// <summary>Runs the requested command without invoking a shell.</summary>
    /// <param name="request">Explicit command and execution bounds.</param>
    /// <param name="cancellationToken">Caller cancellation; it is rethrown after process cleanup.</param>
    /// <returns>A bounded command result.</returns>
    /// <exception cref="EvidenceCommandException">Thrown for start, timeout, budget, or cleanup failures.</exception>
    public async Task<EvidenceCommandResult> RunAsync(
        EvidenceCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.CheckBudget is not null)
        {
            try { await request.CheckBudget(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                throw CreateBudgetException(exception);
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var pair in request.Environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new EvidenceCommandException("start_failed", "The evidence command could not be started.");
            }
        }
        catch (EvidenceCommandException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new EvidenceCommandException("start_failed", "The evidence command could not be started.");
        }

        var elapsed = Stopwatch.StartNew();
        var output = new OutputBudget(request.MaximumOutputBytes);
        using var captureLifetime = new CancellationTokenSource();
        Task stdout = Task.CompletedTask;
        Task stderr = Task.CompletedTask;
        try
        {
            stdout = CaptureAsync(_captureStream(process.StandardOutput.BaseStream), output, isError: false, captureLifetime.Token);
            stderr = CaptureAsync(_captureStream(process.StandardError.BaseStream), output, isError: true, captureLifetime.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            captureLifetime.Cancel();
            process.StandardOutput.Close();
            process.StandardError.Close();
            await TerminateAndDrainAsync(process, stdout, stderr).ConfigureAwait(false);
            throw new EvidenceCommandException("capture_failed", "The evidence command output capture could not be initialized.");
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(request.Timeout);
        var budgetFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var budgetMonitor = Task.Run(() => MonitorBudgetAsync(request.CheckBudget, lifetime.Token, budgetFailure, lifetime.Cancel));

        EvidenceCommandException? failure = null;
        try
        {
            await process.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
            if (budgetFailure.Task.IsCompletedSuccessfully)
            {
                failure = CreateBudgetException(budgetFailure.Task.Result);
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            captureLifetime.Cancel();
            await TerminateAndDrainAsync(process, stdout, stderr).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            failure = budgetFailure.Task.IsCompletedSuccessfully
                ? CreateBudgetException(budgetFailure.Task.Result)
                : new EvidenceCommandException("timeout", "The evidence command exceeded its deadline.");
            captureLifetime.Cancel();
            await TerminateAndDrainAsync(process, stdout, stderr).ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel();
        }

        if (failure is not null)
        {
            await ObserveBoundedAsync(budgetMonitor).ConfigureAwait(false);
            throw failure;
        }

        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(DrainTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or OperationCanceledException)
        {
            captureLifetime.Cancel();
            process.StandardOutput.Close();
            process.StandardError.Close();
            await TerminateAndDrainAsync(process, stdout, stderr).ConfigureAwait(false);
            await ObserveBoundedAsync(budgetMonitor).ConfigureAwait(false);
            if (exception is OperationCanceledException) throw;
            if (exception is TimeoutException)
                throw new EvidenceCommandException("drain_timeout", "The evidence command did not close its output streams within the cleanup deadline.");
            throw new EvidenceCommandException("capture_failed", "The evidence command output could not be captured completely.");
        }
        await ObserveBoundedAsync(budgetMonitor).ConfigureAwait(false);
        if (request.CheckBudget is not null)
        {
            try { await request.CheckBudget(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                throw CreateBudgetException(exception);
            }
        }
        elapsed.Stop();
        return new EvidenceCommandResult(
            process.ExitCode,
            output.GetOutput(isError: false),
            output.GetOutput(isError: true),
            elapsed.ElapsedMilliseconds)
        { OutputTruncated = output.Truncated };
    }

    private static async Task MonitorBudgetAsync(
        Func<CancellationToken, ValueTask>? checkBudget,
        CancellationToken cancellationToken,
        TaskCompletionSource<Exception> failure,
        Action cancel)
    {
        if (checkBudget is null)
        {
            return;
        }

        try
        {
            while (true)
            {
                await checkBudget(cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (EvidenceWorkspaceBudgetException exception)
        {
            failure.TrySetResult(exception);
            cancel();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            failure.TrySetResult(exception);
            cancel();
        }
    }

    private static EvidenceCommandException CreateBudgetException(Exception exception) =>
        exception is EvidenceWorkspaceBudgetException
            ? new EvidenceCommandException("budget_exceeded", "The evidence workspace budget was exhausted.")
            : new EvidenceCommandException("budget_check_failed", "The evidence workspace budget check failed.");

    private static async Task TerminateAndDrainAsync(Process process, Task stdout, Task stderr)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new EvidenceCommandException("cleanup_failed", "The evidence command process tree could not be terminated.");
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new EvidenceCommandException("cleanup_failed", "The evidence command process did not exit after termination.");
        }

        await ObserveCaptureAsync(stdout).ConfigureAwait(false);
        await ObserveCaptureAsync(stderr).ConfigureAwait(false);
    }

    private static async Task ObserveCaptureAsync(Task task)
    {
        try { await task.WaitAsync(DrainTimeout).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        catch (TimeoutException) { throw new EvidenceCommandException("cleanup_failed", "The evidence command output capture did not stop within its cleanup deadline."); }
    }

    private static async Task ObserveBoundedAsync(Task task)
    {
        try { await task.WaitAsync(DrainTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private static Task CaptureAsync(Stream stream, OutputBudget budget, bool isError, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return;
            }

            budget.Append(buffer.AsSpan(0, count), isError);
        }
    });

    private static void Validate(EvidenceCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FileName)) throw new ArgumentException("A command file name is required.", nameof(request));
        if (request.Arguments is null) throw new ArgumentException("Command arguments are required.", nameof(request));
        if (!Path.IsPathFullyQualified(request.WorkingDirectory)) throw new ArgumentException("The working directory must be absolute.", nameof(request));
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1)) throw new ArgumentOutOfRangeException(nameof(request), "The command timeout must be positive and at most 4,294,967,294 milliseconds.");
        if (request.MaximumOutputBytes <= 0 || request.MaximumOutputBytes > 256L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(request), "The output budget must be positive and at most 256 MiB.");
        if (!Directory.Exists(request.WorkingDirectory)) throw new DirectoryNotFoundException(request.WorkingDirectory);
        foreach (var pair in request.Environment)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Contains('=', StringComparison.Ordinal)) throw new ArgumentException("Environment variable names must be valid.", nameof(request));
        }
    }

    private sealed class OutputBudget(long maximumBytes)
    {
        private readonly MemoryStream _stdout = new();
        private readonly MemoryStream _stderr = new();
        private long _remaining = maximumBytes;
        private readonly object _sync = new();
        public bool Truncated { get; private set; }

        public void Append(ReadOnlySpan<byte> bytes, bool isError)
        {
            lock (_sync)
            {
                var count = (int)Math.Min(bytes.Length, Math.Max(0, _remaining));
                Truncated |= count < bytes.Length;
                if (count == 0) return;
                (isError ? _stderr : _stdout).Write(bytes[..count]);
                _remaining -= count;
            }
        }

        public string GetOutput(bool isError)
        {
            lock (_sync)
            {
                return Encoding.UTF8.GetString((isError ? _stderr : _stdout).ToArray());
            }
        }
    }
}
