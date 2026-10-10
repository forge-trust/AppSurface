using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using CliFx.Binding;
using ForgeTrust.AppSurface.Aspire;
using ForgeTrust.AppSurface.Aspire.Testing;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

[Collection(AspireEnvironmentCollection.Name)]
public sealed class ConfiguredAspireTestingBuilderTests : IDisposable
{
    public const string ActivationSettingKey = "ConfiguredTesting:Activation";
    public const string AspireSettingKey = "ConfiguredTesting:Aspire";
    public const string ReceiptsEnabledKey = "Skoolit__Inbox__MonitoringReceipt__Enabled";
    public const string ReceiptsCohortKey = "Skoolit__Inbox__MonitoringReceipt__Cohort";
    public const string SeededHouseholdKey = "Skoolit__Inbox__MonitoringReceipt__SeededInternalHouseholdIds__0";

    private readonly string? _originalDcpPath = Environment.GetEnvironmentVariable("ASPIRE_DCP_PATH");
    private readonly string? _originalDashboardPath = Environment.GetEnvironmentVariable("ASPIRE_DASHBOARD_PATH");
    private readonly string? _originalPollingWatcher = Environment.GetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER");

    public ConfiguredAspireTestingBuilderTests()
    {
        Environment.SetEnvironmentVariable("ASPIRE_DCP_PATH", "dummy");
        Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_PATH", "dummy");
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1");
    }

    [Fact]
    public async Task CreateConfiguredAsync_ValidatesOptionsBeforeCancellationAndRunsNoHooksForInvalidCalls()
    {
        var calls = new List<string>();
        var options = CreateCountingOptions(calls);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            AppSurfaceAspireTestingBuilder.CreateConfiguredAsync<
                ConfiguredTestAppHost,
                ConfiguredTestModule,
                ConfiguredEmptyProfile>(null!, cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AppSurfaceAspireTestingBuilder.CreateConfiguredAsync<
                ConfiguredTestAppHost,
                ConfiguredTestModule,
                ConfiguredEmptyProfile>(options, cancellation.Token));
        Assert.Empty(calls);

        var invalidMarker = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateConfiguredAsync<
                string,
                ConfiguredTestModule,
                ConfiguredEmptyProfile>(options));
        Assert.Contains("same AppHost assembly", invalidMarker.Message, StringComparison.Ordinal);
        Assert.Empty(calls);

        var invalidProfile = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateConfiguredAsync<
                ConfiguredTestAppHost,
                ConfiguredTestModule,
                ConfiguredProfileWithoutCommand>(options));
        Assert.Contains("[Command]", invalidProfile.Message, StringComparison.Ordinal);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task CreateConfiguredAsync_RunsOnlyPresentHooksOnceInContractOrder(int hookMask)
    {
        var calls = new List<string>();
        var options = new AppSurfaceAspireTestingOptions
        {
            ConfigureActivationConfiguration = (hookMask & 1) == 0
                ? null
                : configuration =>
                {
                    calls.Add("configuration");
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { [ActivationSettingKey] = "from-configuration-hook" });
                },
            ConfigureActivationServices = (hookMask & 2) == 0
                ? null
                : services =>
                {
                    calls.Add("services");
                    Assert.Contains(
                        services,
                        descriptor => descriptor.ServiceType == typeof(ConfiguredModuleRegistration));
                    Assert.Contains(
                        services,
                        descriptor => descriptor.ServiceType == typeof(ConfiguredAfterHostRegistration));
                },
            ConfigureAspireBuilder = (hookMask & 4) == 0
                ? null
                : builder =>
                {
                    calls.Add("aspire");
                    builder.Configuration[AspireSettingKey] = "from-aspire-hook";
                }
        };

        await using var builder = await AppSurfaceAspireTestingBuilder.CreateConfiguredAsync<
            ConfiguredTestAppHost,
            ConfiguredTestModule,
            ConfiguredEmptyProfile>(options);

        var expected = new List<string>();
        if ((hookMask & 1) != 0)
        {
            expected.Add("configuration");
        }

        if ((hookMask & 2) != 0)
        {
            expected.Add("services");
        }

        if ((hookMask & 4) != 0)
        {
            expected.Add("aspire");
            Assert.Equal("from-aspire-hook", builder.Configuration[AspireSettingKey]);
        }

        Assert.Equal(expected, calls);
    }

    [Fact]
    public async Task CreateConfiguredAsync_UsesConstructorInputsAndSamePreparingBuilderThroughComposition()
    {
        var scenario = new ConfiguredGraphScenario(
            MonitoringReceiptsEnabled: true,
            ForwardingEnabled: false,
            SourceOnlyEvidenceEnabled: true,
            StripeSandboxEnabled: false);
        var probe = scenario.Probe;
        var hostedService = new ConfiguredHostedServiceSentinel();
        var options = new AppSurfaceAspireTestingOptions
        {
            ConfigureActivationConfiguration = configuration =>
            {
                probe.Events.Enqueue("configuration");
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { [ActivationSettingKey] = "from-configuration-hook" });
            },
            ConfigureActivationServices = services =>
            {
                probe.Events.Enqueue("services");
                Assert.Contains(
                    services,
                    descriptor => descriptor.ServiceType == typeof(ConfiguredModuleRegistration));
                Assert.Contains(
                    services,
                    descriptor => descriptor.ServiceType == typeof(ConfiguredAfterHostRegistration));
                services.RemoveAll<ConfiguredGraphScenario>();
                services.AddSingleton(scenario);
                services.AddSingleton(scenario.Probe);
                services.AddSingleton<IHostedService>(hostedService);
            },
            ConfigureAspireBuilder = builder =>
            {
                var preparing = Assert.IsType<AppSurfaceAspireProfileTestingBuilder>(builder);
                probe.AspireHookBuilder = builder;
                probe.Events.Enqueue("aspire");
                AssertPreparingPropertiesAvailable(preparing);
                builder.Configuration[AspireSettingKey] = "from-aspire-hook";

                var resource = new ParameterResource("configured-hook-resource", _ => "hook", secret: false);
                var added = preparing.AddResource(resource);
                Assert.Same(preparing, added.ApplicationBuilder);
                var annotated = added.WithAnnotation(new ConfiguredProbeAnnotation("hook"));
                Assert.Same(added, annotated);
                var created = preparing.CreateResourceBuilder(resource);
                Assert.Same(preparing, created.ApplicationBuilder);

                AssertPreparingLifecycleRoutesReject(
                    preparing,
                    annotated.ApplicationBuilder,
                    "ConfigureAspireBuilder");
                AssertResourceBuilderOwnerLifecycleRoutesReject(
                    created.ApplicationBuilder,
                    "CreateResourceBuilder during ConfigureAspireBuilder");
            }
        };

        await using var builder = await AppSurfaceAspireTestingBuilder.CreateConfiguredAsync<
            ConfiguredTestAppHost,
            ConfiguredTestModule,
            ConfiguredGraphProfile>(options);

        Assert.Equal("from-configuration-hook", probe.ActivationConfigurationSeen);
        Assert.Equal("from-aspire-hook", builder.Configuration[AspireSettingKey]);
        Assert.Same(scenario, probe.ScenarioSeen);
        AssertDefaultActivationProvider(probe);
        Assert.Same(probe.AspireHookBuilder, probe.ComponentBuilder);
        Assert.Equal(
            ["configuration", "services", "profile", "aspire", "compose"],
            probe.Events.ToArray());
        Assert.Equal(0, hostedService.StartCount);
        Assert.Contains(builder.Resources, resource => resource.Name == "configured-hook-resource");
        Assert.Contains(builder.Resources, resource => resource.Name == "skoolit-web");
        await AssertReceiptAnnotationsAsync(builder, scenario);

        await using (var application = await builder.BuildAsync())
        {
            Assert.NotNull(application);
            await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync());
            Assert.Throws<InvalidOperationException>(() => _ = builder.Configuration);
        }

        await builder.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => _ = builder.Configuration);
        Assert.Equal(0, hostedService.StartCount);
    }

    [Fact]
    public async Task CreateConfiguredAsync_EscapedPreparingBuildFailureAbortsAndInvalidatesCapturedBuilder()
    {
        AppSurfaceAspireProfileTestingBuilder? captured = null;
        var options = new AppSurfaceAspireTestingOptions
        {
            ConfigureAspireBuilder = builder =>
            {
                captured = Assert.IsType<AppSurfaceAspireProfileTestingBuilder>(builder);
                ((IDistributedApplicationBuilder)builder).Build();
            }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateConfiguredAsync<
                ConfiguredTestAppHost,
                ConfiguredTestModule,
                ConfiguredEmptyProfile>(options));

        Assert.Contains("Aspire builder failed", exception.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.NotNull(captured);
        Assert.Throws<ObjectDisposedException>(() => _ = captured.Configuration);
    }

    [Fact]
    public async Task CreateConfiguredAsync_PreservesEachFailurePhaseTypeAdviceAndOriginalCause()
    {
        const string secretValue = "must-not-appear-in-outer-diagnostic";

        var configurationCause = new ConfiguredPhaseException(secretValue);
        var configurationFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateConfiguredAsync<
                ConfiguredTestAppHost,
                ConfiguredTestModule,
                ConfiguredEmptyProfile>(
                new AppSurfaceAspireTestingOptions
                {
                    ConfigureActivationConfiguration = _ => throw configurationCause
                }));
        AssertConfiguredFailure(
            configurationFailure,
            "Activation configuration",
            nameof(AppSurfaceAspireTestingOptions.ConfigureActivationConfiguration),
            secretValue);
        Assert.Contains(typeof(ConfiguredEmptyProfile).FullName!, configurationFailure.Message, StringComparison.Ordinal);
        Assert.Same(configurationCause, configurationFailure.InnerException);

        var servicesCause = new ConfiguredPhaseException(secretValue);
        var servicesFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateConfiguredAsync<
                ConfiguredTestAppHost,
                ConfiguredTestModule,
                ConfiguredEmptyProfile>(
                new AppSurfaceAspireTestingOptions
                {
                    ConfigureActivationServices = _ => throw servicesCause
                }));
        AssertConfiguredFailure(
            servicesFailure,
            "Activation services",
            nameof(AppSurfaceAspireTestingOptions.ConfigureActivationServices),
            secretValue);
        Assert.Contains(typeof(ConfiguredEmptyProfile).FullName!, servicesFailure.Message, StringComparison.Ordinal);
        Assert.Same(servicesCause, servicesFailure.InnerException);

        var activationCause = new ConfiguredPhaseException(secretValue);
        var activationFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateConfiguredAsync<
                ConfiguredTestAppHost,
                ConfiguredTestModule,
                ConfiguredActivationFailureProfile>(
                new AppSurfaceAspireTestingOptions
                {
                    ConfigureActivationServices = services =>
                        services.AddSingleton<ConfiguredActivationFault>(_ => throw activationCause)
                }));
        AssertConfiguredFailure(
            activationFailure,
            "Profile activation",
            "Register",
            secretValue);
        Assert.Contains(typeof(ConfiguredActivationFailureProfile).FullName!, activationFailure.Message, StringComparison.Ordinal);
        AssertContainsException<ConfiguredPhaseException>(activationFailure);

        var aspireCause = new ConfiguredPhaseException(secretValue);
        var cleanupProbe = new ConfiguredThrowingCleanupProbe();
        var aspireFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateConfiguredAsync<
                ConfiguredTestAppHost,
                ConfiguredTestModule,
                ConfiguredCleanupFailureProfile>(
                new AppSurfaceAspireTestingOptions
                {
                    ConfigureActivationServices = services =>
                        services.AddSingleton<ConfiguredThrowingCleanupProbe>(_ => cleanupProbe),
                    ConfigureAspireBuilder = _ => throw aspireCause
                }));
        AssertConfiguredFailure(
            aspireFailure,
            "Aspire builder",
            "Customize",
            secretValue);
        Assert.Contains(typeof(ConfiguredCleanupFailureProfile).FullName!, aspireFailure.Message, StringComparison.Ordinal);
        Assert.Same(aspireCause, aspireFailure.InnerException);
        Assert.Equal(1, cleanupProbe.DisposeCount);

        var compositionCause = new ConfiguredPhaseException(secretValue);
        var compositionFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppSurfaceAspireTestingBuilder.CreateConfiguredAsync<
                ConfiguredTestAppHost,
                ConfiguredTestModule,
                ConfiguredCompositionFailureProfile>(
                new AppSurfaceAspireTestingOptions
                {
                    ConfigureActivationServices = services =>
                        services.AddSingleton(compositionCause)
                }));
        AssertConfiguredFailure(
            compositionFailure,
            "Profile composition",
            "Inspect profile dependencies and components",
            secretValue);
        Assert.Contains(typeof(ConfiguredCompositionFailureProfile).FullName!, compositionFailure.Message, StringComparison.Ordinal);
        Assert.Same(compositionCause, compositionFailure.InnerException);
    }

    [Fact]
    public async Task CreateConfiguredAsync_DoesNotWrapOperationCanceledExceptionThrownByCallback()
    {
        var callbackCancellation = new OperationCanceledException("callback cancelled independently");

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AppSurfaceAspireTestingBuilder.CreateConfiguredAsync<
                ConfiguredTestAppHost,
                ConfiguredTestModule,
                ConfiguredEmptyProfile>(
                new AppSurfaceAspireTestingOptions
                {
                    ConfigureActivationConfiguration = _ => throw callbackCancellation
                }));

        Assert.Same(callbackCancellation, exception);
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("services")]
    [InlineData("profile")]
    [InlineData("aspire")]
    [InlineData("composition")]
    public async Task CreateConfiguredAsync_ObservesCancellationAfterEachSynchronousPhase(string cancelAfter)
    {
        using var cancellation = new CancellationTokenSource();
        var scenario = new ConfiguredGraphScenario(false, false, false, false);
        var probe = scenario.Probe;
        probe.CancellationSource = cancellation;
        probe.CancelAfterProfileActivation = string.Equals(cancelAfter, "profile", StringComparison.Ordinal);
        probe.CancelAfterComposition = string.Equals(cancelAfter, "composition", StringComparison.Ordinal);
        var calls = new ConcurrentQueue<string>();
        var options = new AppSurfaceAspireTestingOptions
        {
            ConfigureActivationConfiguration = configuration =>
            {
                calls.Enqueue("configuration");
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { [ActivationSettingKey] = "cancel-test" });
                if (cancelAfter == "configuration")
                {
                    cancellation.Cancel();
                }
            },
            ConfigureActivationServices = services =>
            {
                calls.Enqueue("services");
                services.RemoveAll<ConfiguredGraphScenario>();
                services.AddSingleton(scenario);
                services.AddSingleton(scenario.Probe);
                if (cancelAfter == "services")
                {
                    cancellation.Cancel();
                }
            },
            ConfigureAspireBuilder = builder =>
            {
                calls.Enqueue("aspire");
                probe.AspireHookBuilder = builder;
                if (cancelAfter == "aspire")
                {
                    cancellation.Cancel();
                }
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AppSurfaceAspireTestingBuilder.CreateConfiguredAsync<
                ConfiguredTestAppHost,
                ConfiguredTestModule,
                ConfiguredGraphProfile>(options, cancellation.Token));

        var expected = cancelAfter switch
        {
            "configuration" => new[] { "configuration" },
            "services" => new[] { "configuration", "services" },
            "profile" => new[] { "configuration", "services" },
            "aspire" => new[] { "configuration", "services", "aspire" },
            "composition" => new[] { "configuration", "services", "aspire" },
            _ => throw new ArgumentOutOfRangeException(nameof(cancelAfter), cancelAfter, "Unknown cancellation phase.")
        };
        if (cancelAfter == "composition")
        {
            Assert.Equal(["profile", "compose"], probe.Events.ToArray());
            Assert.Equal("cancel-test", probe.ActivationConfigurationSeen);
        }
        else if (cancelAfter is "profile" or "aspire")
        {
            Assert.Equal(["profile"], probe.Events.ToArray());
            Assert.Equal("cancel-test", probe.ActivationConfigurationSeen);
        }

        Assert.Equal(expected, calls.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateConfiguredAsync_OverlapsReceiptScenariosInEitherCompletionOrder(bool receiptsOnCompletesFirst)
    {
        string[] scenarioEnvironmentKeys =
        [
            "SKOOLIT_QA_FORWARDING_SENDER_SCENARIO",
            "SKOOLIT_QA_MONITORING_RECEIPT_ENABLED",
            "SKOOLIT_QA_SOURCE_ONLY_EVIDENCE",
            "SKOOLIT_QA_STRIPE_SANDBOX_ENABLED"
        ];
        var originalScenarioEnvironment = scenarioEnvironmentKeys.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        var receiptsOn = new OverlapWorker(
            "receipts-on",
            new ConfiguredGraphScenario(true, false, true, false),
            "overlap-on");
        var receiptsOff = new OverlapWorker(
            "receipts-off",
            new ConfiguredGraphScenario(false, true, false, true),
            "overlap-off");
        var stopwatch = Stopwatch.StartNew();
        AppSurfaceAspireProfileTestingBuilder? receiptsOnBuilder = null;
        AppSurfaceAspireProfileTestingBuilder? receiptsOffBuilder = null;
        var receiptsOnJoined = false;
        var receiptsOffJoined = false;
        var completedNormally = false;

        try
        {
            receiptsOn.Start();
            receiptsOff.Start();
            WaitForArrival(receiptsOn, stopwatch);
            WaitForArrival(receiptsOff, stopwatch);
            Assert.True(receiptsOn.Arrived.IsSet, "receipts-on did not reach ConfigureAspireBuilder.");
            Assert.True(receiptsOff.Arrived.IsSet, "receipts-off did not reach ConfigureAspireBuilder.");

            var first = receiptsOnCompletesFirst ? receiptsOn : receiptsOff;
            var second = receiptsOnCompletesFirst ? receiptsOff : receiptsOn;
            first.Release.Set();
            var firstBuilder = await WaitForCompletionAsync(first, stopwatch, "after releasing its hook");
            if (receiptsOnCompletesFirst)
            {
                receiptsOnBuilder = firstBuilder;
            }
            else
            {
                receiptsOffBuilder = firstBuilder;
            }

            Assert.False(
                second.Completion.Task.IsCompleted,
                $"{second.Name} completed before its ConfigureAspireBuilder release gate was opened.");
            second.Release.Set();
            var secondBuilder = await WaitForCompletionAsync(second, stopwatch, "after releasing its hook");
            if (receiptsOnCompletesFirst)
            {
                receiptsOffBuilder = secondBuilder;
            }
            else
            {
                receiptsOnBuilder = secondBuilder;
            }

            var completedOnBuilder = receiptsOnBuilder ??
                throw new InvalidOperationException("Worker receipts-on completed without a builder.");
            var completedOffBuilder = receiptsOffBuilder ??
                throw new InvalidOperationException("Worker receipts-off completed without a builder.");
            AssertWorkerGraph(receiptsOn, completedOnBuilder);
            AssertWorkerGraph(receiptsOff, completedOffBuilder);
            await AssertReceiptAnnotationsAsync(completedOnBuilder, receiptsOn.Scenario);
            await AssertReceiptAnnotationsAsync(completedOffBuilder, receiptsOff.Scenario);
            foreach (var key in scenarioEnvironmentKeys)
            {
                Assert.Equal(originalScenarioEnvironment[key], Environment.GetEnvironmentVariable(key));
            }
            Assert.True(
                stopwatch.Elapsed <= TimeSpan.FromSeconds(30),
                $"Both receipt scenarios did not complete within the 30 second orchestration budget ({stopwatch.Elapsed}).");
            completedNormally = true;
        }
        finally
        {
            receiptsOn.Release.Set();
            receiptsOff.Release.Set();
            receiptsOnJoined = receiptsOn.WorkerThread.Join(CleanupBudget(stopwatch, TimeSpan.FromSeconds(5)));
            receiptsOffJoined = receiptsOff.WorkerThread.Join(CleanupBudget(stopwatch, TimeSpan.FromSeconds(5)));
            receiptsOnBuilder ??= receiptsOn.GetCompletedBuilder();
            receiptsOffBuilder ??= receiptsOff.GetCompletedBuilder();
            Exception? cleanupFailure = null;
            try
            {
                await DisposeBuilderWithinBudgetAsync(receiptsOnBuilder, receiptsOn.Name, stopwatch);
            }
            catch (Exception error)
            {
                cleanupFailure = error;
            }

            try
            {
                await DisposeBuilderWithinBudgetAsync(receiptsOffBuilder, receiptsOff.Name, stopwatch);
            }
            catch (Exception error)
            {
                cleanupFailure ??= error;
            }

            if (receiptsOnJoined && receiptsOffJoined)
            {
                receiptsOn.Dispose();
                receiptsOff.Dispose();
            }

            if (completedNormally && cleanupFailure is not null)
            {
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        }

        Assert.True(receiptsOnJoined, "Worker 'receipts-on' did not join within the bounded cleanup wait.");
        Assert.True(receiptsOffJoined, "Worker 'receipts-off' did not join within the bounded cleanup wait.");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASPIRE_DCP_PATH", _originalDcpPath);
        Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_PATH", _originalDashboardPath);
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", _originalPollingWatcher);
    }

    private static AppSurfaceAspireTestingOptions CreateCountingOptions(ICollection<string> calls) =>
        new()
        {
            ConfigureActivationConfiguration = _ => calls.Add("configuration"),
            ConfigureActivationServices = _ => calls.Add("services"),
            ConfigureAspireBuilder = _ => calls.Add("aspire")
        };

    private static void AssertPreparingPropertiesAvailable(AppSurfaceAspireProfileTestingBuilder builder)
    {
        Assert.NotNull(builder.Configuration);
        Assert.Equal(Path.GetFullPath(ConfiguredTestAppHost.ProjectPath), builder.AppHostDirectory);
        Assert.Same(typeof(ConfiguredTestAppHost).Assembly, builder.AppHostAssembly);
        Assert.NotNull(builder.Environment);
        Assert.NotNull(builder.Services);
        Assert.NotNull(builder.ExecutionContext);
        Assert.NotNull(builder.Eventing);
        Assert.NotNull(builder.Pipeline);
        Assert.NotNull(builder.Resources);
        Assert.NotNull(builder.FileSystemService);
        Assert.NotNull(builder.UserSecretsManager);
    }

    internal static void AssertPreparingLifecycleRoutesReject(
        AppSurfaceAspireProfileTestingBuilder preparing,
        IDistributedApplicationBuilder resourceBuilderOwner,
        string phase)
    {
        var routes = new (string Name, Action Invoke)[]
        {
            ("concrete BuildAsync", () => preparing.BuildAsync().GetAwaiter().GetResult()),
            ("testing-interface BuildAsync", () =>
                ((IDistributedApplicationTestingBuilder)preparing).BuildAsync().GetAwaiter().GetResult()),
            ("builder-interface Build", () => ((IDistributedApplicationBuilder)preparing).Build()),
            ("concrete Dispose", preparing.Dispose),
            ("disposable-interface Dispose", ((IDisposable)preparing).Dispose),
            ("concrete DisposeAsync", () => preparing.DisposeAsync().AsTask().GetAwaiter().GetResult()),
            ("async-disposable-interface DisposeAsync", () =>
                ((IAsyncDisposable)preparing).DisposeAsync().AsTask().GetAwaiter().GetResult()),
            ("resource-owner Build", () => resourceBuilderOwner.Build()),
            ("resource-owner testing-interface BuildAsync", () =>
                ((IDistributedApplicationTestingBuilder)resourceBuilderOwner).BuildAsync().GetAwaiter().GetResult()),
            ("resource-owner Dispose", () => ((IDisposable)resourceBuilderOwner).Dispose()),
            ("resource-owner DisposeAsync", () =>
                ((IAsyncDisposable)resourceBuilderOwner).DisposeAsync().AsTask().GetAwaiter().GetResult())
        };

        foreach (var (name, invoke) in routes)
        {
            var exception = Record.Exception(invoke) ??
                throw new InvalidOperationException($"{phase} route '{name}' returned without an exception.");
            Assert.True(
                exception is InvalidOperationException && exception is not ObjectDisposedException,
                $"{phase} route '{name}' should reject with InvalidOperationException; actual: {exception.GetType().FullName}.");
            Assert.Contains("preparation", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertResourceBuilderOwnerLifecycleRoutesReject(
        IDistributedApplicationBuilder resourceBuilderOwner,
        string phase)
    {
        var routes = new (string Name, Action Invoke)[]
        {
            ("CreateResourceBuilder owner Build", () => resourceBuilderOwner.Build()),
            ("CreateResourceBuilder owner BuildAsync", () =>
                ((IDistributedApplicationTestingBuilder)resourceBuilderOwner).BuildAsync().GetAwaiter().GetResult()),
            ("CreateResourceBuilder owner Dispose", () => ((IDisposable)resourceBuilderOwner).Dispose()),
            ("CreateResourceBuilder owner DisposeAsync", () =>
                ((IAsyncDisposable)resourceBuilderOwner).DisposeAsync().AsTask().GetAwaiter().GetResult())
        };

        foreach (var (name, invoke) in routes)
        {
            var exception = Record.Exception(invoke) ??
                throw new InvalidOperationException($"{phase} route '{name}' returned without an exception.");
            Assert.True(
                exception is InvalidOperationException && exception is not ObjectDisposedException,
                $"{phase} route '{name}' should reject with InvalidOperationException; actual: {exception.GetType().FullName}.");
            Assert.Contains("preparation", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task AssertReceiptAnnotationsAsync(
        AppSurfaceAspireProfileTestingBuilder builder,
        ConfiguredGraphScenario scenario)
    {
        var resource = Assert.Single(builder.Resources, candidate => candidate.Name == "skoolit-web");
        var resourceWithEnvironment = Assert.IsAssignableFrom<IResourceWithEnvironment>(resource);
        var executionConfiguration = await ExecutionConfigurationBuilder
            .Create(resourceWithEnvironment)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(builder.ExecutionContext, NullLogger.Instance);
        Assert.Null(executionConfiguration.Exception);
        var values = executionConfiguration.EnvironmentVariables
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        AssertEnvironmentValue(
            values,
            ReceiptsEnabledKey,
            scenario.MonitoringReceiptsEnabled ? "true" : "false");
        AssertEnvironmentValue(
            values,
            ReceiptsCohortKey,
            scenario.MonitoringReceiptsEnabled ? "SeededInternal" : "Disabled");
        AssertEnvironmentValue(
            values,
            SeededHouseholdKey,
            scenario.MonitoringReceiptsEnabled ? "household_dev_billing" : string.Empty);
    }

    private static void AssertEnvironmentValue(
        IReadOnlyDictionary<string, string> values,
        string key,
        string expected)
    {
        Assert.True(values.TryGetValue(key, out var actual), $"Graph did not emit environment key '{key}'.");
        Assert.Equal(expected, actual);
    }

    private static void AssertDefaultActivationProvider(ConfiguredAspireProbe probe)
    {
        var providerType = probe.ActivationProviderType ??
            throw new InvalidOperationException("Profile did not record its activation service provider.");
        Assert.Same(typeof(ServiceProvider).Assembly, providerType.Assembly);
        Assert.Same(probe, probe.ActivationProbeFromProvider);
    }

    private static void AssertConfiguredFailure(
        InvalidOperationException exception,
        string phase,
        string advice,
        string secretValue)
    {
        Assert.Contains($"{phase} failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(ConfiguredTestAppHost).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(ConfiguredTestModule).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains(advice, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, exception.Message, StringComparison.Ordinal);
    }

    private static void AssertContainsException<TException>(Exception exception)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
            {
                return;
            }
        }

        throw new InvalidOperationException($"Exception chain did not retain {typeof(TException).FullName}.");
    }

    private static void AssertWorkerGraph(
        OverlapWorker worker,
        AppSurfaceAspireProfileTestingBuilder builder)
    {
        Assert.Same(worker.Scenario, worker.Probe.ScenarioSeen);
        Assert.Equal(worker.ConfigurationValue, worker.Probe.ActivationConfigurationSeen);
        AssertDefaultActivationProvider(worker.Probe);
        Assert.Same(worker.Probe.AspireHookBuilder, worker.Probe.ComponentBuilder);
        Assert.Contains("profile", worker.Probe.Events);
        Assert.Contains("aspire", worker.Probe.Events);
        Assert.Contains("compose", worker.Probe.Events);
        Assert.Contains(builder.Resources, resource => resource.Name == "skoolit-web");
    }

    private static void WaitForArrival(OverlapWorker worker, Stopwatch stopwatch)
    {
        var timeout = RemainingBudget(stopwatch, TimeSpan.FromSeconds(10));
        var signal = WaitHandle.WaitAny(
            [worker.Arrived.WaitHandle, worker.Finished.WaitHandle],
            timeout);
        if (signal == 0 || worker.Arrived.IsSet)
        {
            return;
        }

        if (signal == 1)
        {
            try
            {
                worker.Completion.Task.GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"Worker '{worker.Name}' failed before reaching ConfigureAspireBuilder.",
                    error);
            }
        }

        throw new TimeoutException(
            $"Timed out after {timeout} waiting for worker '{worker.Name}' to reach ConfigureAspireBuilder.");
    }

    private static async Task<AppSurfaceAspireProfileTestingBuilder> WaitForCompletionAsync(
        OverlapWorker worker,
        Stopwatch stopwatch,
        string phase)
    {
        var timeout = RemainingBudget(stopwatch, TimeSpan.FromSeconds(10));
        try
        {
            return await worker.Completion.Task.WaitAsync(timeout);
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException(
                $"Timed out after {timeout} waiting for worker '{worker.Name}' to complete {phase}.",
                error);
        }
    }

    private static TimeSpan RemainingBudget(Stopwatch stopwatch, TimeSpan perWaitLimit)
    {
        var overallRemaining = TimeSpan.FromSeconds(30) - stopwatch.Elapsed;
        if (overallRemaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(
                $"Configured receipt overlap exceeded its 30 second overall budget at {stopwatch.Elapsed}.");
        }

        return overallRemaining < perWaitLimit ? overallRemaining : perWaitLimit;
    }

    private static TimeSpan CleanupBudget(Stopwatch stopwatch, TimeSpan perWaitLimit)
    {
        var overallRemaining = TimeSpan.FromSeconds(30) - stopwatch.Elapsed;
        if (overallRemaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return overallRemaining < perWaitLimit ? overallRemaining : perWaitLimit;
    }

    private static async Task DisposeBuilderWithinBudgetAsync(
        AppSurfaceAspireProfileTestingBuilder? builder,
        string workerName,
        Stopwatch stopwatch)
    {
        if (builder is null)
        {
            return;
        }

        try
        {
            await builder.DisposeAsync().AsTask().WaitAsync(CleanupBudget(stopwatch, TimeSpan.FromSeconds(5)));
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException(
                $"Timed out disposing the returned builder for worker '{workerName}'.",
                error);
        }
    }

}

public sealed class ConfiguredTestAppHost
{
    private ConfiguredTestAppHost()
    {
    }

    public static string ProjectPath => AppContext.BaseDirectory;
}

public sealed class ConfiguredTestModule : IAppSurfaceHostModule
{
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddSingleton(new ConfiguredGraphScenario(false, false, false, false));
        services.AddSingleton<ConfiguredModuleRegistration>();
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [ConfiguredAspireTestingBuilderTests.ActivationSettingKey] = "from-module"
                }));
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
        builder.ConfigureServices((_, services) => services.AddSingleton<ConfiguredAfterHostRegistration>());
    }
}

public sealed record ConfiguredGraphScenario(
    bool MonitoringReceiptsEnabled,
    bool ForwardingEnabled,
    bool SourceOnlyEvidenceEnabled,
    bool StripeSandboxEnabled)
{
    public ConfiguredAspireProbe Probe { get; init; } = new();
}

public sealed class ConfiguredAspireProbe
{
    public ConcurrentQueue<string> Events { get; } = new();

    public ConfiguredGraphScenario? ScenarioSeen { get; set; }

    public string? ActivationConfigurationSeen { get; set; }

    public Type? ActivationProviderType { get; set; }

    public ConfiguredAspireProbe? ActivationProbeFromProvider { get; set; }

    public IDistributedApplicationBuilder? AspireHookBuilder { get; set; }

    public IDistributedApplicationBuilder? ComponentBuilder { get; set; }

    public CancellationTokenSource? CancellationSource { get; set; }

    public bool CancelAfterComposition { get; set; }

    public bool CancelAfterProfileActivation { get; set; }
}

public sealed class ConfiguredGraphComponent(ConfiguredGraphScenario scenario) : IAspireComponent<ContainerResource>
{
    public IResourceBuilder<ContainerResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder)
    {
        var probe = scenario.Probe;
        probe.Events.Enqueue("compose");
        probe.ComponentBuilder = appBuilder;
        if (!ReferenceEquals(probe.AspireHookBuilder, appBuilder))
        {
            throw new InvalidOperationException("The Aspire hook and profile component received different builders.");
        }

        var resourceBuilder = appBuilder.AddContainer("skoolit-web", "mcr.microsoft.com/dotnet/aspnet", "10.0")
            .WithEnvironment(
                ConfiguredAspireTestingBuilderTests.ReceiptsEnabledKey,
                scenario.MonitoringReceiptsEnabled ? "true" : "false")
            .WithEnvironment(
                ConfiguredAspireTestingBuilderTests.ReceiptsCohortKey,
                scenario.MonitoringReceiptsEnabled ? "SeededInternal" : "Disabled")
            .WithEnvironment(
                ConfiguredAspireTestingBuilderTests.SeededHouseholdKey,
                scenario.MonitoringReceiptsEnabled ? "household_dev_billing" : string.Empty);

        var withProbeAnnotation = resourceBuilder.WithAnnotation(new ConfiguredProbeAnnotation("component"));
        if (!ReferenceEquals(resourceBuilder, withProbeAnnotation))
        {
            throw new InvalidOperationException("The guarded resource annotation operation replaced its builder.");
        }

        ConfiguredAspireTestingBuilderTests.AssertPreparingLifecycleRoutesReject(
            Assert.IsType<AppSurfaceAspireProfileTestingBuilder>(appBuilder),
            resourceBuilder.ApplicationBuilder,
            "Profile composition");

        if (probe.CancelAfterComposition)
        {
            probe.CancellationSource?.Cancel();
        }

        return resourceBuilder;
    }
}

[Command("configured-empty")]
public sealed partial class ConfiguredEmptyProfile : AspireProfile
{
    public ConfiguredEmptyProfile(ILogger<ConfiguredEmptyProfile> logger)
        : base(logger)
    {
    }

    public override IEnumerable<IAspireComponent> GetComponents() => [];
}

[Command("configured-graph")]
public sealed partial class ConfiguredGraphProfile : AspireProfile
{
    private readonly ConfiguredGraphComponent _component;

    public ConfiguredGraphProfile(
        IConfiguration configuration,
        ConfiguredGraphScenario scenario,
        ConfiguredGraphComponent component,
        IServiceProvider serviceProvider,
        ILogger<ConfiguredGraphProfile> logger)
        : base(logger)
    {
        _component = component;
        var probe = scenario.Probe;
        probe.Events.Enqueue("profile");
        probe.ScenarioSeen = scenario;
        probe.ActivationConfigurationSeen =
            configuration[ConfiguredAspireTestingBuilderTests.ActivationSettingKey];
        probe.ActivationProviderType = serviceProvider.GetType();
        probe.ActivationProbeFromProvider = serviceProvider.GetRequiredService<ConfiguredAspireProbe>();
        if (probe.CancelAfterProfileActivation)
        {
            probe.CancellationSource?.Cancel();
        }
    }

    public override IEnumerable<IAspireComponent> GetComponents()
    {
        yield return _component;
    }
}

[Command("configured-activation-failure")]
public sealed partial class ConfiguredActivationFailureProfile : AspireProfile
{
    public ConfiguredActivationFailureProfile(
        ConfiguredActivationFault fault,
        ILogger<ConfiguredActivationFailureProfile> logger)
        : base(logger)
    {
        _ = fault;
    }

    public override IEnumerable<IAspireComponent> GetComponents() => [];
}

[Command("configured-cleanup-failure")]
public sealed partial class ConfiguredCleanupFailureProfile : AspireProfile
{
    public ConfiguredCleanupFailureProfile(
        ConfiguredThrowingCleanupProbe cleanupProbe,
        ILogger<ConfiguredCleanupFailureProfile> logger)
        : base(logger)
    {
        _ = cleanupProbe;
    }

    public override IEnumerable<IAspireComponent> GetComponents() => [];
}

[Command("configured-composition-failure")]
public sealed partial class ConfiguredCompositionFailureProfile : AspireProfile
{
    private readonly ConfiguredThrowingComponent _component;

    public ConfiguredCompositionFailureProfile(
        ConfiguredThrowingComponent component,
        ILogger<ConfiguredCompositionFailureProfile> logger)
        : base(logger)
    {
        _component = component;
    }

    public override IEnumerable<IAspireComponent> GetComponents()
    {
        yield return _component;
    }
}

public sealed class ConfiguredThrowingComponent(ConfiguredPhaseException failure)
    : IAspireComponent<ParameterResource>
{
    public IResourceBuilder<ParameterResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder) =>
        throw failure;
}

public sealed class ConfiguredActivationFault
{
}

public sealed class ConfiguredThrowingCleanupProbe : IAsyncDisposable
{
    private int _disposeCount;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        await Task.CompletedTask;
        throw new ConfiguredCleanupException();
    }
}

public sealed class ConfiguredCleanupException : Exception
{
}

public sealed class ConfiguredModuleRegistration
{
}

public sealed class ConfiguredAfterHostRegistration
{
}

public sealed class ConfiguredProbeAnnotation(string phase) : IResourceAnnotation
{
    public string Phase { get; } = phase;
}

public sealed class ConfiguredPhaseException(string value) : Exception(value)
{
}

public sealed class ConfiguredProfileWithoutCommand : AspireProfile
{
    public ConfiguredProfileWithoutCommand(ILogger<ConfiguredProfileWithoutCommand> logger)
        : base(logger)
    {
    }

    public override IEnumerable<IAspireComponent> GetComponents() => [];
}

internal sealed class ConfiguredHostedServiceSentinel : IHostedService
{
    private int _startCount;

    public int StartCount => Volatile.Read(ref _startCount);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _startCount);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class OverlapWorker : IDisposable
{
    private readonly AppSurfaceAspireTestingOptions _options;

    public OverlapWorker(string name, ConfiguredGraphScenario scenario, string configurationValue)
    {
        Name = name;
        Scenario = scenario;
        ConfigurationValue = configurationValue;
        _options = new AppSurfaceAspireTestingOptions
        {
            ConfigureActivationConfiguration = configuration =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [ConfiguredAspireTestingBuilderTests.ActivationSettingKey] = ConfigurationValue
                    }),
            ConfigureActivationServices = services =>
            {
                services.RemoveAll<ConfiguredGraphScenario>();
                services.AddSingleton(Scenario);
                services.AddSingleton(Scenario.Probe);
            },
            ConfigureAspireBuilder = builder =>
            {
                Probe.AspireHookBuilder = builder;
                Probe.Events.Enqueue("aspire");
                Arrived.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException(
                        $"Worker '{Name}' timed out at ConfigureAspireBuilder waiting for its release gate.");
                }
            }
        };

        WorkerThread = new System.Threading.Thread(Run)
        {
            IsBackground = true,
            Name = $"Configured Aspire overlap: {name}"
        };
    }

    public string Name { get; }

    public ConfiguredGraphScenario Scenario { get; }

    public string ConfigurationValue { get; }

    public ConfiguredAspireProbe Probe => Scenario.Probe;

    public ManualResetEventSlim Arrived { get; } = new(false);

    public ManualResetEventSlim Release { get; } = new(false);

    public ManualResetEventSlim Finished { get; } = new(false);

    public TaskCompletionSource<AppSurfaceAspireProfileTestingBuilder> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public System.Threading.Thread WorkerThread { get; }

    public void Start() => WorkerThread.Start();

    public AppSurfaceAspireProfileTestingBuilder? GetCompletedBuilder() =>
        Completion.Task.IsCompletedSuccessfully ? Completion.Task.Result : null;

    public void Dispose()
    {
        Arrived.Dispose();
        Release.Dispose();
        Finished.Dispose();
    }

    private void Run()
    {
        try
        {
            var builder = AppSurfaceAspireTestingBuilder.CreateConfiguredAsync<
                    ConfiguredTestAppHost,
                    ConfiguredTestModule,
                    ConfiguredGraphProfile>(_options)
                .GetAwaiter()
                .GetResult();
            Completion.TrySetResult(builder);
        }
        catch (Exception error)
        {
            Completion.TrySetException(error);
        }
        finally
        {
            Finished.Set();
        }
    }
}
