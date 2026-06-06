using ForgeTrust.AppSurface.Intelligence;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Intelligence.Tests;

public sealed class AppSurfaceProductIntelligenceDispatcherTests
{
    [Fact]
    public async Task CaptureAsync_DefaultExperimentalEventsDisabled_DoesNotEmit()
    {
        var sink = new RecordingSink();
        using var provider = CreateServices(services => services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sink));
        var intelligence = provider.GetRequiredService<IAppSurfaceProductIntelligence>();

        await intelligence.CaptureAsync(CreateStreamRejectedEvent());

        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task CaptureAsync_EnabledExperimentalEvents_EmitsSanitizedEvent()
    {
        var sink = new RecordingSink();
        using var provider = CreateServices(
            services => services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sink),
            options => options.EnableExperimentalEvents());
        var intelligence = provider.GetRequiredService<IAppSurfaceProductIntelligence>();

        await intelligence.CaptureAsync(
            new AppSurfaceProductEvent(
                AppSurfaceProductEventRegistry.RazorWireStreamAdmissionRejected,
                DateTimeOffset.UnixEpoch,
                new Dictionary<string, string>
                {
                    ["rejection_reason"] = "TooManyLiveChannels",
                    ["limit_name"] = "max_live_channels",
                    ["current_count"] = "1",
                    ["channel"] = "tenant-secret-42",
                    ["token"] = "token-value"
                },
                actorId: "actor-1",
                sessionId: "session-1",
                correlationId: "trace-1",
                route: "/_rw/streams/{channel}"));

        var captured = Assert.Single(sink.Events);
        Assert.Equal(AppSurfaceProductEventRegistry.RazorWireStreamAdmissionRejected, captured.Name);
        Assert.Equal("actor-1", captured.ActorId);
        Assert.Equal("session-1", captured.SessionId);
        Assert.Equal("trace-1", captured.CorrelationId);
        Assert.Equal("/_rw/streams/{channel}", captured.Route);
        Assert.Contains("rejection_reason", captured.Properties.Keys);
        Assert.Contains("limit_name", captured.Properties.Keys);
        Assert.DoesNotContain("channel", captured.Properties.Keys);
        Assert.DoesNotContain("token", captured.Properties.Keys);
        Assert.DoesNotContain("tenant-secret-42", string.Join(" ", captured.Properties.Values), StringComparison.Ordinal);
        Assert.DoesNotContain("token-value", string.Join(" ", captured.Properties.Values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureAsync_EnabledExperimentalEvents_DropsUnsafeEnvelopeValues()
    {
        var sink = new RecordingSink();
        using var provider = CreateServices(
            services => services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sink),
            options => options.EnableExperimentalEvents());
        var intelligence = provider.GetRequiredService<IAppSurfaceProductIntelligence>();

        await intelligence.CaptureAsync(
            new AppSurfaceProductEvent(
                AppSurfaceProductEventRegistry.RazorWireStreamAdmissionRejected,
                DateTimeOffset.UnixEpoch,
                new Dictionary<string, string>
                {
                    ["rejection_reason"] = "TooManyLiveChannels",
                    ["limit_name"] = "max_live_channels",
                    ["current_count"] = "1"
                },
                actorId: "andrew@example.test",
                sessionId: "session token=secret-session",
                correlationId: "Bearer abc123",
                route: "https://example.test/_rw/streams/tenant-secret-42?token=abc123"));

        var captured = Assert.Single(sink.Events);
        Assert.Null(captured.ActorId);
        Assert.Null(captured.SessionId);
        Assert.Null(captured.CorrelationId);
        Assert.Null(captured.Route);

        var rendered = System.Text.Json.JsonSerializer.Serialize(captured);
        Assert.DoesNotContain("andrew@example.test", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-session", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret-42", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureAsync_UnknownEvent_DoesNotEmit()
    {
        var sink = new RecordingSink();
        using var provider = CreateServices(
            services => services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sink),
            options => options.EnableExperimentalEvents());
        var intelligence = provider.GetRequiredService<IAppSurfaceProductIntelligence>();

        await intelligence.CaptureAsync(new AppSurfaceProductEvent("unknown.event", DateTimeOffset.UnixEpoch));

        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task CaptureAsync_SinkFailure_DoesNotBreakRequestPath()
    {
        var recordingSink = new RecordingSink();
        using var provider = CreateServices(
            services =>
            {
                services.AddSingleton<IAppSurfaceProductIntelligenceSink>(new ThrowingSink());
                services.AddSingleton<IAppSurfaceProductIntelligenceSink>(recordingSink);
            },
            options => options.EnableExperimentalEvents());
        var intelligence = provider.GetRequiredService<IAppSurfaceProductIntelligence>();

        await intelligence.CaptureAsync(CreateStreamRejectedEvent());

        Assert.Single(recordingSink.Events);
    }

    [Fact]
    public async Task CaptureAsync_CancellationBeforeEmission_ThrowsAndDoesNotEmit()
    {
        var sink = new RecordingSink();
        using var provider = CreateServices(
            services => services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sink),
            options => options.EnableExperimentalEvents());
        var intelligence = provider.GetRequiredService<IAppSurfaceProductIntelligence>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await intelligence.CaptureAsync(CreateStreamRejectedEvent(), cts.Token));
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task CaptureAsync_SupportsScopedHostOwnedSinks()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceProductIntelligence(options => options.EnableExperimentalEvents());
        services.AddScoped<ScopedMarker>();
        services.AddScoped<IAppSurfaceProductIntelligenceSink, ScopedSink>();
        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

        using var scope = provider.CreateScope();
        var intelligence = scope.ServiceProvider.GetRequiredService<IAppSurfaceProductIntelligence>();

        await intelligence.CaptureAsync(CreateStreamRejectedEvent());

        var sink = (ScopedSink)scope.ServiceProvider.GetRequiredService<IAppSurfaceProductIntelligenceSink>();
        Assert.Single(sink.Events);
    }

    private static ServiceProvider CreateServices(
        Action<IServiceCollection> configureServices,
        Action<AppSurfaceProductIntelligenceOptions>? configureOptions = null)
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceProductIntelligence(configureOptions);
        configureServices(services);
        return services.BuildServiceProvider();
    }

    private static AppSurfaceProductEvent CreateStreamRejectedEvent()
    {
        return new AppSurfaceProductEvent(
            AppSurfaceProductEventRegistry.RazorWireStreamAdmissionRejected,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>
            {
                ["rejection_reason"] = "TooManyLiveSubscriptions",
                ["limit_name"] = "max_live_subscriptions",
                ["current_count"] = "32"
            });
    }

    private sealed class RecordingSink : IAppSurfaceProductIntelligenceSink
    {
        public List<AppSurfaceProductEvent> Events { get; } = [];

        public ValueTask CaptureAsync(
            AppSurfaceProductEvent productEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(productEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingSink : IAppSurfaceProductIntelligenceSink
    {
        public ValueTask CaptureAsync(
            AppSurfaceProductEvent productEvent,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("sink failed");
        }
    }

    private sealed class ScopedMarker
    {
    }

    private sealed class ScopedSink(ScopedMarker marker) : IAppSurfaceProductIntelligenceSink
    {
        public List<AppSurfaceProductEvent> Events { get; } = [];

        public ValueTask CaptureAsync(
            AppSurfaceProductEvent productEvent,
            CancellationToken cancellationToken = default)
        {
            Assert.NotNull(marker);
            Events.Add(productEvent);
            return ValueTask.CompletedTask;
        }
    }
}
