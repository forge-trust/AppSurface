using System.Collections.Concurrent;
using System.Text;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurableWorkSnapshotClosureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Known_source_is_not_recaptured_by_bindings_legacy_composition_or_provider_aggregation(bool legacyFirst)
    {
        var source = new ClosureCodec();
        var definition = DurableWork.Define("closure.defined", "v1", source, source,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
        var services = new ServiceCollection();
        var legacy = new DurableWorkRegistration<string, string, Executor>("closure.legacy", "v1",
            DurableProviderSafety.Idempotent, definition.WorkCodec, definition.ResultCodec);
        Assert.Same(definition.WorkCodec, legacy.WorkCodec);
        Assert.Same(definition.ResultCodec, legacy.ResultCodec);
        if (legacyFirst)
        {
            services.AddSingleton<DurableWorkRegistration>(legacy);
        }

        services.AddDurableWork(definition.ExecutedBy<Executor>());
        if (!legacyFirst)
        {
            services.AddSingleton<DurableWorkRegistration>(legacy);
        }

        services.AddSingleton<IDurablePayloadCodec>(source);
        services.AddSingleton<IDurablePayloadCodec>(source);
        using var first = services.BuildServiceProvider();
        using var second = services.BuildServiceProvider();
        var canonical = first.GetRequiredService<IDurablePayloadCodecRegistry>().GetRequired(typeof(string));
        Assert.IsAssignableFrom<IDurablePayloadCodec<string>>(canonical);
        Assert.NotSame(definition.WorkCodec, canonical);
        Assert.NotSame(canonical, second.GetRequiredService<IDurablePayloadCodecRegistry>().GetRequired(typeof(string)));
        Assert.All(source.Reads, count => Assert.Equal(1, count));
        Assert.Equal(0, source.Encodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_known_view_closes_its_raw_source_without_polling_metadata_in_either_argument_order(bool viewFirst)
    {
        var source = new ClosureCodec();
        var original = DurableWork.Define("closure.original", "v1", source, source,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
        source.Retention = "later";
        var next = DurableWork.Define("closure.next", "v1",
            viewFirst ? original.WorkCodec : source, viewFirst ? source : original.ResultCodec,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
        Assert.Same(next.WorkCodec, next.ResultCodec);
        Assert.Equal("original", next.WorkCodec.RetentionPolicyId);
        Assert.All(source.Reads, count => Assert.Equal(1, count));
        Assert.Throws<InvalidOperationException>(() => next.CreateRequest(new("scope"), new("command"), "key", "value"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Source_snapshot_conflict_precedes_contract_collision_in_either_contribution_order(bool reverse)
    {
        var source = new ClosureCodec();
        var original = DurableWork.Define("closure.one", "v1", source, source,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
        source.Retention = "changed";
        var changed = DurableWork.Define("closure.two", "v1", source, source,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
        var services = new ServiceCollection();
        if (reverse)
        {
            services.AddDurableWork(changed.ExecutedBy<Executor>());
            services.AddDurableWork(original.ExecutedBy<Executor>());
        }
        else
        {
            services.AddDurableWork(original.ExecutedBy<Executor>());
            services.AddDurableWork(changed.ExecutedBy<Executor>());
        }

        services.AddSingleton<IDurablePayloadCodec>(new ClosureCodec());
        using var provider = services.BuildServiceProvider();
        Assert.Equal("A durable payload codec source was contributed with conflicting contract metadata.",
            Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IDurablePayloadCodecRegistry>()).Message);
    }

    [Fact]
    public void Concurrent_manual_upgrade_and_conflict_preserve_both_indexes_and_original_contract_facts()
    {
        var source = new ClosureCodec();
        var definition = DurableWork.Define("closure.concurrent", "v1", source, source,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
        var registry = new DurablePayloadCodecRegistry([source]);
        source.Retention = "changed";
        var conflicting = DurableWork.Define("closure.changed", "v1", source, source,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
        var errors = new ConcurrentBag<Exception>();
        Parallel.For(0, 60, index =>
        {
            try
            {
                registry.Register((index % 3) switch
                {
                    0 => source,
                    1 => definition.WorkCodec,
                    _ => conflicting.WorkCodec,
                });
            }
            catch (InvalidOperationException error)
            {
                errors.Add(error);
            }
        });
        Assert.Equal(20, errors.Count);
        var byType = registry.GetRequired(typeof(string));
        Assert.Same(byType, registry.GetRequired(typeof(string), "closure.payload", "v1"));
        Assert.Same(byType, registry.GetRequired("closure.payload", "v1"));
        Assert.Equal("original", byType.RetentionPolicyId);
        Assert.NotSame(source, byType);
    }

    [Fact]
    public void Raw_snapshot_projection_does_not_dispose_caller_owned_codec()
    {
        var source = new ClosureCodec();
        var definition = DurableWork.Define("closure.disposal", "v1", source, source,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
        using (var provider = new ServiceCollection().AddDurableWork(definition.ExecutedBy<Executor>()).BuildServiceProvider())
        {
            _ = provider.GetRequiredService<IDurablePayloadCodecRegistry>();
        }

        Assert.False(source.Disposed);
    }

    private sealed class Executor : IDurableWorkerExecutor<string, string>
    {
        public ValueTask<string> ExecuteAsync(DurableWorkerEnvelope<string> work, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(work.Payload!);
    }

    private sealed class ClosureCodec : IDurablePayloadCodec<string>, IDisposable
    {
        internal int[] Reads { get; } = new int[5];
        internal string Retention { get; set; } = "original";
        internal int Encodes { get; private set; }
        internal bool Disposed { get; private set; }
        public Type PayloadType { get { Reads[0]++; return typeof(string); } }
        public string ContractName { get { Reads[1]++; return "closure.payload"; } }
        public string ContractVersion { get { Reads[2]++; return "v1"; } }
        public DurableDataClassification Classification { get { Reads[3]++; return DurableDataClassification.Operational; } }
        public string RetentionPolicyId { get { Reads[4]++; return Retention; } }
        public DurableEncodedPayload Encode(string value)
        {
            Encodes++;
            return new("closure.payload", "v1", DurableDataClassification.Operational, Encoding.UTF8.GetBytes(value), Retention);
        }
        public string Decode(DurableEncodedPayload payload) => Encoding.UTF8.GetString(payload.Content.Span);
        public DurableEncodedPayload EncodeObject(object value) => Encode((string)value);
        public object DecodeObject(DurableEncodedPayload payload) => Decode(payload);
        public void Dispose() => Disposed = true;
    }
}
