using System.Collections.Concurrent;
using ForgeTrust.AppSurface.Durable;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurableWorkDefinitionRegistryTests
{
    [Fact]
    public void Startup_registry_exposes_exact_registration_and_definition_views_without_eager_execution()
    {
        var definition = DurableWork.Define("registry.work", "v1",
            new DurableBindingRegistryTestCodec<string>("registry.input"),
            new DurableBindingRegistryTestCodec<string>("registry.result"),
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
        var binding = definition.ExecutedBy<DurableBindingRegistryTestExecutor>();
        var services = new ServiceCollection();
        services.AddDurableWork(binding);

        using var first = services.BuildServiceProvider();
        using var second = services.BuildServiceProvider();
        var firstRegistration = first.GetRequiredService<IDurableWorkRegistry>().GetRequired("registry.work", "v1");
        var secondRegistration = second.GetRequiredService<IDurableWorkRegistry>().GetRequired("registry.work", "v1");
        var codecRegistry = first.GetRequiredService<IDurablePayloadCodecRegistry>();
        var secondCodecRegistry = second.GetRequiredService<IDurablePayloadCodecRegistry>();

        Assert.Same(definition, first.GetRequiredService<DurableWorkDefinition<string, string>>());
        Assert.Same(binding, first.GetRequiredService<DurableWorkBinding<string, string, DurableBindingRegistryTestExecutor>>());
        Assert.Same(definition.WorkCodec, firstRegistration.WorkCodec);
        Assert.Same(definition.ResultCodec, firstRegistration.ResultCodec);
        Assert.NotSame(definition.WorkCodec, codecRegistry.GetRequired("registry.input", "v1"));
        Assert.NotSame(definition.ResultCodec, codecRegistry.GetRequired("registry.result", "v1"));
        Assert.Same(firstRegistration, secondRegistration);
        Assert.NotSame(codecRegistry, secondCodecRegistry);
        Assert.NotSame(codecRegistry.GetRequired("registry.input", "v1"),
            secondCodecRegistry.GetRequired("registry.input", "v1"));
        Assert.NotSame(first.GetRequiredService<DurableBindingRegistryTestExecutor>(),
            first.GetRequiredService<DurableBindingRegistryTestExecutor>());
        Assert.Equal([new DurableWorkContractIdentity("registry.work", "v1")],
            first.GetRequiredService<IDurableWorkRegistry>().RegisteredContracts);
    }

    [Fact]
    public void Separate_collections_can_share_one_static_definition_without_mutating_it()
    {
        var definition = DurableWork.Define("registry.static", "v1",
            new DurableBindingRegistryTestCodec<string>("registry.static.input"),
            new DurableBindingRegistryTestCodec<string>("registry.static.result"),
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
        var firstServices = new ServiceCollection();
        var secondServices = new ServiceCollection();
        firstServices.AddDurableWork(definition.ExecutedBy<DurableBindingRegistryTestExecutor>());
        secondServices.AddDurableWork(definition.ExecutedBy<DurableBindingRegistryTestExecutor>());

        using var first = firstServices.BuildServiceProvider();
        using var second = secondServices.BuildServiceProvider();
        var firstRegistration = first.GetRequiredService<IDurableWorkRegistry>().GetRequired("registry.static", "v1");
        var secondRegistration = second.GetRequiredService<IDurableWorkRegistry>().GetRequired("registry.static", "v1");

        Assert.Same(definition, first.GetRequiredService<DurableWorkDefinition<string, string>>());
        Assert.Same(definition, second.GetRequiredService<DurableWorkDefinition<string, string>>());
        Assert.Same(definition.WorkCodec, firstRegistration.WorkCodec);
        Assert.Same(definition.WorkCodec, secondRegistration.WorkCodec);
        Assert.NotSame(firstRegistration, secondRegistration);
        Assert.NotSame(first.GetRequiredService<IDurableWorkRegistry>(),
            second.GetRequiredService<IDurableWorkRegistry>());
    }

    [Fact]
    public void All_binding_kinds_and_repeated_binding_objects_are_rejected_as_duplicate_work()
    {
        var ordinary = Define("duplicate.matrix", DurableProviderSafety.Idempotent)
            .ExecutedBy<DurableBindingRegistryTestExecutor>();
        var reconciled = Define("duplicate.matrix", DurableProviderSafety.ReconcileBeforeRetry)
            .ExecutedBy<DurableBindingRegistryTestExecutor>()
            .ReconciledBy<DurableBindingRegistryTestReconciler>();
        var exit = Define("duplicate.matrix", DurableProviderSafety.ProviderKeyed)
            .ExecutedByExit<DurableBindingRegistryTestExitExecutor>();

        AssertDuplicate(services =>
        {
            services.AddDurableWork(ordinary);
            services.AddDurableWork(Define("duplicate.matrix", DurableProviderSafety.Idempotent)
                .ExecutedBy<DurableBindingRegistryTestExecutor>());
        });
        AssertDuplicate(services =>
        {
            services.AddDurableWork(reconciled);
            services.AddDurableWork(Define("duplicate.matrix", DurableProviderSafety.ReconcileBeforeRetry)
                .ExecutedBy<DurableBindingRegistryTestExecutor>()
                .ReconciledBy<DurableBindingRegistryTestReconciler>());
        });
        AssertDuplicate(services =>
        {
            services.AddDurableWork(exit);
            services.AddDurableWork(Define("duplicate.matrix", DurableProviderSafety.ProviderKeyed)
                .ExecutedByExit<DurableBindingRegistryTestExitExecutor>());
        });
        AssertDuplicate(services =>
        {
            services.AddDurableWork(ordinary);
            services.AddDurableWork(ordinary);
        });
    }

    [Fact]
    public void Every_mixed_binding_duplicate_pair_is_rejected_in_both_registration_orders()
    {
        foreach (var first in Enum.GetValues<DuplicateStyle>())
            foreach (var second in Enum.GetValues<DuplicateStyle>())
                foreach (var reverse in new[] { false, true })
                {
                    var services = new ServiceCollection();
                    if (reverse)
                    {
                        AddStyle(services, second);
                        AddStyle(services, first);
                    }
                    else
                    {
                        AddStyle(services, first);
                        AddStyle(services, second);
                    }

                    using var provider = services.BuildServiceProvider();
                    var error = Assert.Throws<InvalidOperationException>(() =>
                        provider.GetRequiredService<IDurableWorkRegistry>());
                    Assert.Contains("registered more than once", error.Message, StringComparison.Ordinal);
                }
    }

    [Fact]
    public void Duplicate_work_is_rejected_before_codec_registry_factory_is_evaluated_in_either_order()
    {
        foreach (var reverse in new[] { false, true })
        {
            var first = LegacyRegistration("duplicate.work", "v1", "duplicate.input", "duplicate.result");
            var second = LegacyRegistration("duplicate.work", "v1", "other.input", "other.result");
            var services = new ServiceCollection();
            if (reverse)
            {
                services.AddSingleton<DurableWorkRegistration>(second);
                services.AddSingleton<DurableWorkRegistration>(first);
            }
            else
            {
                services.AddSingleton<DurableWorkRegistration>(first);
                services.AddSingleton<DurableWorkRegistration>(second);
            }

            services.AddSingleton<IDurablePayloadCodec>(_ => throw new InvalidOperationException("codec factory evaluated"));
            DurableRegistryInstallation.AddDefaults(services);

            var error = Assert.Throws<InvalidOperationException>(() =>
                services.BuildServiceProvider().GetRequiredService<IDurablePayloadCodecRegistry>());
            Assert.Contains("registered more than once", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Registry_orders_default_contracts_ordinally_and_preserves_custom_overrides()
    {
        var a = LegacyRegistration("z.work", "v1", "z.input", "z.result");
        var b = LegacyRegistration("a.work", "v1", "a.input", "a.result");
        var defaultServices = new ServiceCollection();
        defaultServices.AddSingleton<DurableWorkRegistration>(a);
        defaultServices.AddSingleton<DurableWorkRegistration>(b);
        DurableServiceCollectionExtensions.AddDurableWork<string, string, DurableBindingRegistryTestExecutor>(
            defaultServices, "z.work", "v2", DurableProviderSafety.Idempotent,
            new DurableBindingRegistryTestCodec<string>("z2.input"),
            new DurableBindingRegistryTestCodec<string>("z2.result"));

        using var defaultProvider = defaultServices.BuildServiceProvider();
        var defaultRegistry = defaultProvider.GetRequiredService<IDurableWorkRegistry>();
        Assert.Equal(
            [new DurableWorkContractIdentity("a.work", "v1"),
                new DurableWorkContractIdentity("z.work", "v1"),
                new DurableWorkContractIdentity("z.work", "v2")],
            defaultRegistry.RegisteredContracts);
        Assert.Same(a, defaultRegistry.GetRequired("z.work", "v1"));
        Assert.Same(b, defaultRegistry.GetRequired("a.work", "v1"));
        Assert.NotSame(a, defaultRegistry.GetRequired("z.work", "v2"));

        var customWork = new RegistryMarkerWorkRegistry();
        var customCodec = new RegistryMarkerCodecRegistry(a.WorkCodec);
        var customServices = new ServiceCollection();
        customServices.AddSingleton<IDurableWorkRegistry>(customWork);
        customServices.AddSingleton<IDurablePayloadCodecRegistry>(customCodec);
        customServices.AddSingleton<DurableWorkRegistration>(a);
        customServices.AddDurableWork(Define("z.work", DurableProviderSafety.Idempotent)
            .ExecutedBy<DurableBindingRegistryTestExecutor>());

        using var customProvider = customServices.BuildServiceProvider();
        Assert.Same(customWork, customProvider.GetRequiredService<IDurableWorkRegistry>());
        Assert.Same(customCodec, customProvider.GetRequiredService<IDurablePayloadCodecRegistry>());
        Assert.Same(a.WorkCodec, customCodec.GetRequired("z.input", "v1"));
        Assert.Equal(1, customServices.Count(descriptor => descriptor.ServiceType == typeof(IDurableWorkRegistry)));
        Assert.Equal(1, customServices.Count(descriptor => descriptor.ServiceType == typeof(IDurablePayloadCodecRegistry)));
    }

    [Fact]
    public void Default_registry_descriptors_are_singletons_and_custom_overrides_are_independent()
    {
        foreach (var customWork in new[] { false, true })
            foreach (var customCodec in new[] { false, true })
            {
                var services = new ServiceCollection();
                var workMarker = new RegistryMarkerWorkRegistry();
                var codecMarker = new RegistryMarkerCodecRegistry();
                if (customWork) services.AddSingleton<IDurableWorkRegistry>(workMarker);
                if (customCodec) services.AddSingleton<IDurablePayloadCodecRegistry>(codecMarker);
                services.AddDurableWork(Define($"override.{customWork}.{customCodec}", DurableProviderSafety.Idempotent)
                    .ExecutedBy<DurableBindingRegistryTestExecutor>());

                Assert.Equal(1, services.Count(static descriptor => descriptor.ServiceType == typeof(IDurableWorkRegistry)));
                Assert.Equal(1, services.Count(static descriptor => descriptor.ServiceType == typeof(IDurablePayloadCodecRegistry)));
                using var provider = services.BuildServiceProvider();
                Assert.Same(customWork ? workMarker : provider.GetRequiredService<IDurableWorkRegistry>(),
                    provider.GetRequiredService<IDurableWorkRegistry>());
                Assert.Same(customCodec ? codecMarker : provider.GetRequiredService<IDurablePayloadCodecRegistry>(),
                    provider.GetRequiredService<IDurablePayloadCodecRegistry>());
            }
    }

    [Fact]
    public void Custom_work_factory_can_depend_on_codec_registry_without_a_default_cycle()
    {
        var services = new ServiceCollection();
        var codecMarker = new RegistryMarkerCodecRegistry();
        var workMarker = new RegistryMarkerWorkRegistry();
        services.AddSingleton<IDurablePayloadCodecRegistry>(codecMarker);
        services.AddSingleton<IDurableWorkRegistry>(provider =>
        {
            Assert.Same(codecMarker, provider.GetRequiredService<IDurablePayloadCodecRegistry>());
            return workMarker;
        });
        services.AddDurableWork(Define("cycle.safe", DurableProviderSafety.Idempotent)
            .ExecutedBy<DurableBindingRegistryTestExecutor>());

        using var provider = services.BuildServiceProvider();
        Assert.Same(workMarker, provider.GetRequiredService<IDurableWorkRegistry>());
        Assert.Same(codecMarker, provider.GetRequiredService<IDurablePayloadCodecRegistry>());
    }

    [Fact]
    public void Both_custom_registries_leave_the_default_catalog_unused_until_requested()
    {
        var services = new ServiceCollection();
        var workMarker = new RegistryMarkerWorkRegistry();
        var codecMarker = new RegistryMarkerCodecRegistry();
        services.AddSingleton<DurableWorkRegistration>(_ =>
            throw new InvalidOperationException("the unused catalog was evaluated"));
        services.AddSingleton<IDurableWorkRegistry>(workMarker);
        services.AddSingleton<IDurablePayloadCodecRegistry>(codecMarker);
        DurableRegistryInstallation.AddDefaults(services);

        using var provider = services.BuildServiceProvider();
        Assert.Same(workMarker, provider.GetRequiredService<IDurableWorkRegistry>());
        Assert.Same(codecMarker, provider.GetRequiredService<IDurablePayloadCodecRegistry>());
    }

    [Fact]
    public void Registry_captures_known_and_unknown_raw_codec_metadata_once_without_polling_later()
    {
        var knownCounts = new ConcurrentDictionary<string, int>();
        var known = new DurableBindingRegistryTestCodec<string>("tests.typed.payload", getterCounts: knownCounts);
        var unknownCounts = new ConcurrentDictionary<string, int>();
        var unknown = new DurableBindingRegistryTestCodec<int>("registry.unknown", getterCounts: unknownCounts);

        var registry = new DurablePayloadCodecRegistry([known, unknown]);
        known.ContractNameValue = "changed";

        Assert.Equal(1, knownCounts[nameof(IDurablePayloadCodec.ContractName)]);
        Assert.Equal(1, unknownCounts[nameof(IDurablePayloadCodec.ContractName)]);
        Assert.Same(known, registry.GetRequired("tests.typed.payload", "v1"));
        Assert.Throws<InvalidOperationException>(() => registry.GetRequired("changed", "v1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Raw_and_view_contributions_use_captured_metadata_without_recapture(bool viewFirst)
    {
        var counts = new ConcurrentDictionary<string, int>();
        var source = new DurableBindingRegistryTestCodec<string>("raw.view", getterCounts: counts);
        var snapshot = DurablePayloadCodecSnapshot.Capture(source);
        var view = snapshot.CreateView();
        var registry = viewFirst
            ? new DurablePayloadCodecRegistry(
                [new DurableCodecContribution(snapshot, true)], [view, source])
            : new DurablePayloadCodecRegistry(
                [new DurableCodecContribution(snapshot, true)], [source, view]);

        Assert.Equal(1, counts[nameof(IDurablePayloadCodec.PayloadType)]);
        Assert.Equal(1, counts[nameof(IDurablePayloadCodec.ContractName)]);
        Assert.Equal(1, counts[nameof(IDurablePayloadCodec.ContractVersion)]);
        Assert.Equal(1, counts[nameof(IDurablePayloadCodec.Classification)]);
        Assert.Equal(1, counts[nameof(IDurablePayloadCodec.RetentionPolicyId)]);
        Assert.Same(registry.GetRequired("raw.view", "v1"), registry.GetRequired(typeof(string)));
    }

    [Fact]
    public void Empty_registry_is_valid_and_same_source_definitions_coalesce_codec_entries()
    {
        var empty = new DurablePayloadCodecRegistry(Array.Empty<IDurablePayloadCodec>());
        Assert.Throws<InvalidOperationException>(() => empty.GetRequired("missing", "v1"));

        var source = new DurableBindingRegistryTestCodec<string>("shared.codec");
        var first = DurableWork.Define("coalesce.one", "v1", source, source,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
        var second = DurableWork.Define("coalesce.two", "v1", source, source,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
        var registry = new DurablePayloadCodecRegistry([
            (IDurablePayloadCodec)first.WorkCodec,
            (IDurablePayloadCodec)second.ResultCodec]);

        Assert.Same(registry.GetRequired("shared.codec", "v1"), registry.GetRequired(typeof(string)));
    }

    [Fact]
    public void Same_contract_from_distinct_sources_has_deterministic_ordinal_conflict()
    {
        var z = new DurableBindingRegistryTestCodec<string>("z.contract");
        var a = new DurableBindingRegistryTestCodec<int>("a.contract");
        var aSame = new DurableBindingRegistryTestCodec<string>("a.contract");

        var error = Assert.Throws<InvalidOperationException>(() =>
            new DurablePayloadCodecRegistry([z, aSame, a]));

        Assert.Equal("Durable contract 'a.contract' version 'v1' is already registered.", error.Message);
    }

    [Fact]
    public void Historical_contracts_are_exactly_selectable_and_type_only_lookup_is_ambiguous()
    {
        var v1 = new DurableBindingRegistryTestCodec<string>("history.codec", "v1");
        var v2 = new DurableBindingRegistryTestCodec<string>("history.codec", "v2");
        var registry = new DurablePayloadCodecRegistry([v1, v2]);

        Assert.Throws<InvalidOperationException>(() => registry.GetRequired(typeof(string)));
        Assert.Same(v1, registry.GetRequired(typeof(string), "history.codec", "v1"));
        Assert.Same(v2, registry.GetRequired(typeof(string), "history.codec", "v2"));
        Assert.Throws<InvalidOperationException>(() => registry.GetRequired(typeof(int), "history.codec", "v1"));
    }

    [Fact]
    public void Same_clr_type_from_distinct_work_definitions_requires_exact_codec_identity()
    {
        var first = Define("enum.first", DurableProviderSafety.Idempotent)
            .ExecutedBy<DurableBindingRegistryTestExecutor>();
        var second = Define("enum.second", DurableProviderSafety.Idempotent)
            .ExecutedBy<DurableBindingRegistryTestExecutor>();
        var services = new ServiceCollection();
        services.AddDurableWork(first);
        services.AddDurableWork(second);

        using var provider = services.BuildServiceProvider();
        var codecs = provider.GetRequiredService<IDurablePayloadCodecRegistry>();
        Assert.Throws<InvalidOperationException>(() => codecs.GetRequired(typeof(string)));
        Assert.NotNull(codecs.GetRequired("enum.first.input", "v1"));
        Assert.NotNull(codecs.GetRequired("enum.second.input", "v1"));
    }

    [Fact]
    public void Same_source_snapshot_conflict_wins_before_contract_collision_diagnostics()
    {
        var source = new DurableBindingRegistryTestCodec<string>("same.source");
        var first = DurablePayloadCodecSnapshot.Capture(source);
        source.ContractNameValue = "changed.source";
        var second = DurablePayloadCodecSnapshot.Capture(source);
        var unrelated = DurablePayloadCodecSnapshot.Capture(
            new DurableBindingRegistryTestCodec<int>("same.source"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            new DurablePayloadCodecRegistry(
                [new DurableCodecContribution(first, false), new DurableCodecContribution(second, false),
                    new DurableCodecContribution(unrelated, false)], []));

        Assert.Equal("A durable payload codec source was contributed with conflicting contract metadata.", error.Message);
    }

    private static DurableWorkRegistration LegacyRegistration(string name, string version, string input, string result) =>
        new DurableWorkRegistration<string, string, DurableBindingRegistryTestExecutor>(name, version,
            DurableProviderSafety.Idempotent, new DurableBindingRegistryTestCodec<string>(input),
            new DurableBindingRegistryTestCodec<string>(result));

    private static DurableWorkDefinition<string, string> Define(string name, DurableProviderSafety safety) =>
        DurableWork.Define(name, "v1", new DurableBindingRegistryTestCodec<string>(name + ".input"),
            new DurableBindingRegistryTestCodec<string>(name + ".result"), safety, DurableWorkRetryPolicy.Default);

    private static void AssertDuplicate(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<IDurableWorkRegistry>());
        Assert.Contains("registered more than once", error.Message, StringComparison.Ordinal);
    }

    private static void AddStyle(IServiceCollection services, DuplicateStyle style)
    {
        switch (style)
        {
            case DuplicateStyle.Ordinary:
                services.AddDurableWork(Define("mixed.duplicate", DurableProviderSafety.Idempotent)
                    .ExecutedBy<DurableBindingRegistryTestExecutor>());
                break;
            case DuplicateStyle.Reconciled:
                services.AddDurableWork(Define("mixed.duplicate", DurableProviderSafety.ReconcileBeforeRetry)
                    .ExecutedBy<DurableBindingRegistryTestExecutor>()
                    .ReconciledBy<DurableBindingRegistryTestReconciler>());
                break;
            case DuplicateStyle.Exit:
                services.AddDurableWork(Define("mixed.duplicate", DurableProviderSafety.ProviderKeyed)
                    .ExecutedByExit<DurableBindingRegistryTestExitExecutor>());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(style));
        }
    }

    private enum DuplicateStyle
    {
        Ordinary,
        Reconciled,
        Exit
    }

    private sealed class RegistryMarkerWorkRegistry : IDurableWorkRegistry
    {
        public IReadOnlyList<DurableWorkContractIdentity> RegisteredContracts => [];
        public DurableWorkRegistration GetRequired(string workName, string workVersion) =>
            throw new InvalidOperationException("marker registry");
    }

    private sealed class RegistryMarkerCodecRegistry(IDurablePayloadCodec? codec = null) : IDurablePayloadCodecRegistry
    {
        private readonly IDurablePayloadCodec? _codec = codec;
        public void Register(IDurablePayloadCodec codec) => throw new NotSupportedException();
        public IDurablePayloadCodec GetRequired(Type payloadType) => _codec ?? throw new InvalidOperationException("marker registry");
        public IDurablePayloadCodec GetRequired(Type payloadType, string contractName, string contractVersion) =>
            _codec ?? throw new InvalidOperationException("marker registry");
        public IDurablePayloadCodec GetRequired(string contractName, string contractVersion) =>
            _codec ?? throw new InvalidOperationException("marker registry");
    }
}
