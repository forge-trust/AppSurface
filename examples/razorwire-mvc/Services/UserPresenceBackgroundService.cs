using ForgeTrust.AppSurface.Core;
using ForgeTrust.RazorWire.Bridge;
using ForgeTrust.RazorWire.Streams;

namespace RazorWireWebExample.Services;

/// <summary>
/// A background service that periodically pulses the user presence service and publishes updates to connected clients.
/// </summary>
public class UserPresenceBackgroundService : CriticalService
{
    private readonly IUserPresenceService _presence;
    private readonly IRazorWireStreamHub _hub;
    private readonly ILogger<UserPresenceBackgroundService> _logger;
    private readonly IRazorPartialRenderer _renderer;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(2.5);

    /// <summary>
    /// Initializes a background service that monitors user presence and publishes stream updates to connected clients.
    /// </summary>
    /// <param name="presence">Service that pulses presence and provides removed users and the current active user count.</param>
    /// <param name="hub">Hub used to publish RazorWire streams to connected clients.</param>
    /// <param name="logger">Logger for service diagnostics.</param>
    /// <param name="renderer">Renderer used to produce Razor partials as strings.</param>
    /// <param name="applicationLifetime">Lifetime of the host application.</param>
    public UserPresenceBackgroundService(
        IUserPresenceService presence,
        IRazorWireStreamHub hub,
        ILogger<UserPresenceBackgroundService> logger,
        IRazorPartialRenderer renderer,
        IHostApplicationLifetime applicationLifetime)
        : base(logger, applicationLifetime)
    {
        _presence = presence;
        _hub = hub;
        _logger = logger;
        _renderer = renderer;
    }

    /// <summary>
    /// Periodically pulses user presence, removes departed users from the reactive stream, and publishes updates to connected clients.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token used to stop the background loop.</param>
    /// <returns>A task that completes when the background loop stops.</returns>
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_checkInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var (removed, activeCount) = _presence.Pulse();

                var stream = RazorWireBridge.CreateStream();
                var anyRemoved = false;
                foreach (var user in removed)
                {
                    stream.Remove($"user-{user.SafeUsername}");
                    anyRemoved = true;
                }

                if (anyRemoved)
                {
                    stream.Update("user-count", $"{activeCount} ONLINE");

                    if (activeCount == 0)
                    {
                        var emptyHtml = await _renderer.RenderPartialToStringAsync(
                            "~/Views/Shared/_UserListEmpty.cshtml",
                            cancellationToken: stoppingToken);

                        stream.Append("active-user-list", emptyHtml);
                    }

                    await _hub.PublishAsync("reactivity", stream.Build());
                }
            }
            catch (OperationCanceledException)
            {
                break; // Graceful shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while pulsing user presence.");
            }
        }
    }
}
