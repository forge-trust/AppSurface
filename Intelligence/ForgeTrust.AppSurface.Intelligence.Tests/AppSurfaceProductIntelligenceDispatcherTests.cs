using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Intelligence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Intelligence.Tests;

public sealed class AppSurfaceProductIntelligenceDispatcherTests
{
    public static TheoryData<string> UnsafeRoutes => new()
    {
        new string('a', 161),
        "https://example.test/docs",
        "//example.test/docs",
        "/docs/search?query=raw",
        "/docs/search#results",
        "/docs/search/{slug}@"
    };

    [Fact]
    public void Constructor_RejectsMissingDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceProductIntelligenceDispatcher(
            null!,
            Array.Empty<IAppSurfaceProductIntelligenceSink>()));

        Assert.Throws<ArgumentNullException>(() => new AppSurfaceProductIntelligenceDispatcher(
            Options.Create(new AppSurfaceProductIntelligenceOptions()),
            null!,
            Array.Empty<IAppSurfaceProductIntelligenceSink>()));

        Assert.Throws<ArgumentNullException>(() => new AppSurfaceProductIntelligenceDispatcher(
            Options.Create(new AppSurfaceProductIntelligenceOptions()),
            new TestRegistry(),
            null!));
    }

    [Fact]
    public async Task CaptureAsync_TwoArgumentConstructor_UsesBuiltInRegistry()
    {
        var sink = new RecordingSink();
        var dispatcher = new AppSurfaceProductIntelligenceDispatcher(
            Options.Create(new AppSurfaceProductIntelligenceOptions().EnableExperimentalEvents()),
            [sink]);

        await dispatcher.CaptureAsync(CreateStreamRejectedEvent());

        Assert.Single(sink.Events);
    }

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
    public async Task CaptureAsync_EnabledExperimentalEvents_DropsOverlongEnvelopeValues()
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
                actorId: new string('a', 129),
                sessionId: "session+unsafe",
                correlationId: "trace 1"));

        var captured = Assert.Single(sink.Events);
        Assert.Null(captured.ActorId);
        Assert.Null(captured.SessionId);
        Assert.Null(captured.CorrelationId);
    }

    [Theory]
    [MemberData(nameof(UnsafeRoutes))]
    public async Task CaptureAsync_EnabledExperimentalEvents_DropsUnsafeRoutes(string route)
    {
        var sink = new RecordingSink();
        using var provider = CreateServices(
            services => services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sink),
            options => options.EnableExperimentalEvents());
        var intelligence = provider.GetRequiredService<IAppSurfaceProductIntelligence>();

        await intelligence.CaptureAsync(
            new AppSurfaceProductEvent(
                AppSurfaceProductEventRegistry.DocsSearchSubmitted,
                DateTimeOffset.UnixEpoch,
                new Dictionary<string, string>
                {
                    ["surface"] = "search_page",
                    ["result_count"] = "1"
                },
                route: route));

        var captured = Assert.Single(sink.Events);
        Assert.Null(captured.Route);
    }

    [Fact]
    public async Task CaptureAsync_EnabledExperimentalEvents_PreservesSafeOptionalRouteTemplate()
    {
        var sink = new RecordingSink();
        using var provider = CreateServices(
            services => services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sink),
            options => options.EnableExperimentalEvents());
        var intelligence = provider.GetRequiredService<IAppSurfaceProductIntelligence>();

        await intelligence.CaptureAsync(
            new AppSurfaceProductEvent(
                AppSurfaceProductEventRegistry.DocsSearchSubmitted,
                DateTimeOffset.UnixEpoch,
                new Dictionary<string, string>
                {
                    ["surface"] = "search_page",
                    ["result_count"] = "1"
                },
                route: "/docs/{slug?}"));

        var captured = Assert.Single(sink.Events);
        Assert.Equal("/docs/{slug?}", captured.Route);
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
    public async Task CaptureAsync_CustomStableContract_EmitsByRegistration()
    {
        var sink = new RecordingSink();
        using var provider = CreateServices(
            services => services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sink),
            options => options.RegisterEventContracts(CreateSkoolieContract(AppSurfaceProductEventLifecycle.Stable)));
        var intelligence = provider.GetRequiredService<IAppSurfaceProductIntelligence>();

        await intelligence.CaptureAsync(CreateSkoolieEvent());

        var captured = Assert.Single(sink.Events);
        Assert.Equal("skoolie.card.generated", captured.Name);
        Assert.Equal("3", captured.Properties["attachment_count"]);
        Assert.Equal("queued", captured.Properties["delivery_state"]);
    }

    [Fact]
    public async Task CaptureAsync_CustomExperimentalContract_RequiresAllowlist()
    {
        var sink = new RecordingSink();
        using var provider = CreateServices(
            services => services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sink),
            options => options.RegisterEventContracts(CreateSkoolieContract(AppSurfaceProductEventLifecycle.Experimental)));
        var intelligence = provider.GetRequiredService<IAppSurfaceProductIntelligence>();

        await intelligence.CaptureAsync(CreateSkoolieEvent());

        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task CaptureAsync_CustomExperimentalContract_EmitsWhenAllowlisted()
    {
        var sink = new RecordingSink();
        using var provider = CreateServices(
            services => services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sink),
            options => options
                .RegisterEventContracts(CreateSkoolieContract(AppSurfaceProductEventLifecycle.Experimental))
                .EnableExperimentalEvents("skoolie.card.generated"));
        var intelligence = provider.GetRequiredService<IAppSurfaceProductIntelligence>();

        await intelligence.CaptureAsync(CreateSkoolieEvent());

        Assert.Single(sink.Events);
    }

    [Fact]
    public async Task CaptureAsync_ThrowOnInvalidEvents_ThrowsSafeExceptionForUnknownEvents()
    {
        var sink = new RecordingSink();
        using var provider = CreateServices(
            services => services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sink),
            options => options.ThrowOnInvalidEvents());
        var intelligence = provider.GetRequiredService<IAppSurfaceProductIntelligence>();

        var exception = await Assert.ThrowsAsync<AppSurfaceProductEventValidationException>(
            async () => await intelligence.CaptureAsync(
                new AppSurfaceProductEvent(
                    "skoolie.card.generated",
                    DateTimeOffset.UnixEpoch,
                    new Dictionary<string, string>
                    {
                        ["recipient"] = "andrew@example.test",
                        ["token"] = "token=secret-value"
                    })));

        Assert.Equal("skoolie.card.generated", exception.EventName);
        Assert.Contains(AppSurfaceProductEventValidationFailureReason.EventNotRegistered, exception.ReasonCodes);
        Assert.Empty(exception.RejectedProperties);
        Assert.Contains("README", exception.DocsLink, StringComparison.Ordinal);
        Assert.Contains("Register", exception.FixHint, StringComparison.Ordinal);
        Assert.DoesNotContain("andrew@example.test", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", exception.Message, StringComparison.Ordinal);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task CaptureAsync_ThrowOnInvalidEvents_ScrubsUnsafeEventNames()
    {
        var sink = new RecordingSink();
        using var provider = CreateServices(
            services => services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sink),
            options => options.ThrowOnInvalidEvents());
        var intelligence = provider.GetRequiredService<IAppSurfaceProductIntelligence>();

        var exception = await Assert.ThrowsAsync<AppSurfaceProductEventValidationException>(
            async () => await intelligence.CaptureAsync(
                new AppSurfaceProductEvent("child@example.test", DateTimeOffset.UnixEpoch)));

        Assert.Equal("[unsafe-property-name]", exception.EventName);
        Assert.Contains("[unsafe-property-name]", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("child@example.test", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureAsync_ThrowOnInvalidEvents_DoesNotThrowForSanitizedOptionalProperties()
    {
        var sink = new RecordingSink();
        using var provider = CreateServices(
            services => services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sink),
            options => options
                .RegisterEventContracts(CreateSkoolieContract(AppSurfaceProductEventLifecycle.Stable))
                .ThrowOnInvalidEvents());
        var intelligence = provider.GetRequiredService<IAppSurfaceProductIntelligence>();

        await intelligence.CaptureAsync(
            new AppSurfaceProductEvent(
                "skoolie.card.generated",
                DateTimeOffset.UnixEpoch,
                new Dictionary<string, string>
                {
                    ["launch_surface"] = "dashboard",
                    ["attachment_count"] = "3",
                    ["delivery_state"] = "queued",
                    ["raw_child_email"] = "child@example.test"
                }));

        var captured = Assert.Single(sink.Events);
        Assert.DoesNotContain("raw_child_email", captured.Properties.Keys);
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
    public async Task CaptureAsync_SinkCancelsRequestedToken_ThrowsAndStopsEmission()
    {
        var recordingSink = new RecordingSink();
        using var cts = new CancellationTokenSource();
        using var provider = CreateServices(
            services =>
            {
                services.AddSingleton<IAppSurfaceProductIntelligenceSink>(new CancelingSink(cts));
                services.AddSingleton<IAppSurfaceProductIntelligenceSink>(recordingSink);
            },
            options => options.EnableExperimentalEvents());
        var intelligence = provider.GetRequiredService<IAppSurfaceProductIntelligence>();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await intelligence.CaptureAsync(CreateStreamRejectedEvent(), cts.Token));
        Assert.Empty(recordingSink.Events);
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

    [Fact]
    public void Module_ConfigureServices_RegistersPassiveDispatcherWithoutDependencies()
    {
        var module = new AppSurfaceProductIntelligenceModule();
        var services = new ServiceCollection();
        var dependencies = new ModuleDependencyBuilder();

        module.RegisterDependentModules(dependencies);
        module.ConfigureServices(new StartupContext([], new TestHostModule()), services);

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
        Assert.Empty(dependencies.Modules);
        using var scope = provider.CreateScope();
        Assert.IsType<AppSurfaceProductIntelligenceDispatcher>(
            scope.ServiceProvider.GetRequiredService<IAppSurfaceProductIntelligence>());
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

    private static AppSurfaceProductEventContract CreateSkoolieContract(AppSurfaceProductEventLifecycle lifecycle)
    {
        return new AppSurfaceProductEventContract(
            "skoolie.card.generated",
            lifecycle,
            "Measure whether launch-card generation moves safely into send review.",
            "Skoolie",
            "Short launch-quality retention; aggregate before long-term storage.",
            [
                new AppSurfaceProductEventPropertyContract(
                    "launch_surface",
                    "Host-owned surface that generated the card.",
                    AppSurfaceProductEventSensitivity.Operational,
                    AppSurfaceProductEventCardinality.Low,
                    required: true,
                    valueShape: AppSurfaceProductEventValueShape.Token),
                new AppSurfaceProductEventPropertyContract(
                    "attachment_count",
                    "Number of safe launch artifacts attached to the generated card.",
                    AppSurfaceProductEventSensitivity.Operational,
                    AppSurfaceProductEventCardinality.Medium,
                    valueShape: AppSurfaceProductEventValueShape.NonNegativeInteger),
                new AppSurfaceProductEventPropertyContract(
                    "delivery_state",
                    "Normalized downstream state.",
                    AppSurfaceProductEventSensitivity.Operational,
                    AppSurfaceProductEventCardinality.Low,
                    required: true,
                    allowedValues: ["queued", "sent"],
                    valueShape: AppSurfaceProductEventValueShape.AllowedValue)
            ],
            ["child identity", "email body", "raw attachment"]);
    }

    private static AppSurfaceProductEvent CreateSkoolieEvent()
    {
        return new AppSurfaceProductEvent(
            "skoolie.card.generated",
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>
            {
                ["launch_surface"] = "dashboard",
                ["attachment_count"] = "003",
                ["delivery_state"] = "queued"
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

    private sealed class TestRegistry : IAppSurfaceProductEventRegistry
    {
        public IReadOnlyList<AppSurfaceProductEventContract> All => AppSurfaceProductEventRegistry.All;

        public IReadOnlySet<string> ForbiddenProperties => AppSurfaceProductEventRegistry.ForbiddenProperties;

        public AppSurfaceProductEventContract? Find(string name)
        {
            return AppSurfaceProductEventRegistry.Find(name);
        }

        public AppSurfaceProductEventValidationResult Validate(AppSurfaceProductEvent productEvent)
        {
            return AppSurfaceProductEventRegistry.Validate(productEvent);
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

    private sealed class CancelingSink(CancellationTokenSource cancellationTokenSource) : IAppSurfaceProductIntelligenceSink
    {
        public ValueTask CaptureAsync(
            AppSurfaceProductEvent productEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationTokenSource.Cancel();
            throw new OperationCanceledException(cancellationToken);
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

    private sealed class TestHostModule : IAppSurfaceHostModule
    {
        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }
    }
}
