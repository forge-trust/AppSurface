using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Core.Tests;

[Collection("NoParallel")]
public class CriticalServiceTests
{
    [Fact]
    public async Task ExecuteAsync_CompletesSuccessfully_StopsApplication()
    {
        var logger = new TestLogger<TestCriticalService>();
        var lifetime = new TestLifetime();
        var svc = new TestCriticalService(_ => Task.CompletedTask, logger, lifetime);
        var previous = Environment.ExitCode;

        try
        {
            await svc.InvokeExecuteAsync(CancellationToken.None);

            Assert.Equal(1, lifetime.StopCalled);
            Assert.Equal(previous, Environment.ExitCode);
            Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Critical);
        }
        finally
        {
            Environment.ExitCode = previous;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Exception_SetsExitCodeAndLogs()
    {
        var logger = new TestLogger<TestCriticalService>();
        var lifetime = new TestLifetime();
        var svc = new TestCriticalService(_ => throw new InvalidOperationException("fail"), logger, lifetime);
        var previous = Environment.ExitCode;

        try
        {
            await svc.InvokeExecuteAsync(CancellationToken.None);

            Assert.Equal(1, lifetime.StopCalled);
            Assert.Equal(-1, Environment.ExitCode);
            Assert.Contains(
                logger.Entries,
                e => e.Level == LogLevel.Critical && e.Exception is InvalidOperationException);
        }
        finally
        {
            Environment.ExitCode = previous;
        }
    }

    [Fact]
    public async Task ExecuteAsync_OperationCanceledException_Requested_GracefulShutdown()
    {
        var logger = new TestLogger<TestCriticalService>();
        var lifetime = new TestLifetime();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Simulate shutdown requested

        var svc = new TestCriticalService(_ => throw new OperationCanceledException(cts.Token), logger, lifetime);
        var previous = Environment.ExitCode;

        try
        {
            await svc.InvokeExecuteAsync(cts.Token);

            Assert.Equal(1, lifetime.StopCalled);
            Assert.Equal(previous, Environment.ExitCode);
            Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Critical);
        }
        finally
        {
            Environment.ExitCode = previous;
        }
    }

    [Fact]
    public async Task ExecuteAsync_TaskCanceledException_Requested_GracefulShutdown()
    {
        var logger = new TestLogger<TestCriticalService>();
        var lifetime = new TestLifetime();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Simulate shutdown requested

        var svc = new TestCriticalService(
            _ => throw new TaskCanceledException(null, null, cts.Token),
            logger,
            lifetime);
        var previous = Environment.ExitCode;

        try
        {
            await svc.InvokeExecuteAsync(cts.Token);

            Assert.Equal(1, lifetime.StopCalled);
            Assert.Equal(previous, Environment.ExitCode);
            Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Critical);
        }
        finally
        {
            Environment.ExitCode = previous;
        }
    }

    [Fact]
    public async Task ExecuteAsync_OperationCanceledException_NotRequested_CriticalFailure()
    {
        var logger = new TestLogger<TestCriticalService>();
        var lifetime = new TestLifetime();
        var svc = new TestCriticalService(_ => throw new OperationCanceledException("unexpected"), logger, lifetime);
        var previous = Environment.ExitCode;

        try
        {
            await svc.InvokeExecuteAsync(CancellationToken.None);

            Assert.Equal(1, lifetime.StopCalled);
            Assert.Equal(-1, Environment.ExitCode);
            Assert.Contains(
                logger.Entries,
                e => e.Level == LogLevel.Critical && e.Exception is OperationCanceledException);
        }
        finally
        {
            Environment.ExitCode = previous;
        }
    }

    private class TestLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, Exception? Exception)> Entries = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, exception));
        }

        private class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private class TestLifetime : IHostApplicationLifetime
    {
        public int StopCalled;
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => StopCalled++;
    }

    private class TestCriticalService : CriticalService
    {
        private readonly Func<CancellationToken, Task> _run;

        public TestCriticalService(
            Func<CancellationToken, Task> run,
            ILogger logger,
            IHostApplicationLifetime lifetime)
            : base(logger, lifetime)
        {
            _run = run;
        }

        protected override Task RunAsync(CancellationToken stoppingToken) => _run(stoppingToken);
        public Task InvokeExecuteAsync(CancellationToken token) => ExecuteAsync(token);
    }
}
