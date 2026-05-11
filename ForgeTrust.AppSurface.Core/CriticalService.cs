using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Core;

/// <summary>
/// A base class for background services that are critical to the application's operation.
/// If a critical service fails or exits, the entire application will be shut down.
/// </summary>
public abstract partial class CriticalService : BackgroundService
{
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger _logger;
    private readonly string _serviceType;

    /// <summary>
    /// Initializes a new instance of the <see cref="CriticalService"/> class.
    /// </summary>
    /// <param name="logger">The logger to use for service events.</param>
    /// <param name="applicationLifetime">The application lifetime to signal shutdown.</param>
    protected CriticalService(
        ILogger logger,
        IHostApplicationLifetime applicationLifetime)
    {
        _logger = logger;
        _serviceType = GetType().Name;
        _applicationLifetime = applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime));
    }

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            LogInitialize(_serviceType);
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            LogException(ex, _serviceType);
            Environment.ExitCode = -1;
        }
        finally
        {
            LogStopping(_serviceType);
            _applicationLifetime.StopApplication();
        }
    }

    /// <summary>
    /// Implements the core logic of the critical service.
    /// </summary>
    /// <param name="stoppingToken">A token that is signaled when the service should stop.</param>
    /// <returns>A task that represents the service's execution.</returns>
    protected abstract Task RunAsync(CancellationToken stoppingToken);

    /// <summary>
    /// Logs that the critical service is initializing.
    /// </summary>
    /// <param name="serviceType">The name of the service type.</param>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "{ServiceType}: Initializing Critical Service")]
    public partial void LogInitialize(string serviceType);

    /// <summary>
    /// Logs that the critical service is stopping.
    /// </summary>
    /// <param name="serviceType">The name of the service type.</param>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "{ServiceType}: Stopping Critical Service")]
    public partial void LogStopping(string serviceType);

    /// <summary>
    /// Logs an unhandled exception that occurred in the critical service.
    /// </summary>
    /// <param name="exception">The unhandled exception.</param>
    /// <param name="serviceType">The name of the service type.</param>
    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "{ServiceType}: Unhandled Exception.")]
    public partial void LogException(Exception exception, string serviceType);
}
