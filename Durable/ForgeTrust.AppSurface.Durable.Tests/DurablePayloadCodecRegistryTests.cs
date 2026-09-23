using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurablePayloadCodecRegistryTests
{
    [Fact]
    public void Manual_register_equivalent_views_is_idempotent_and_frozen_view_upgrades_raw_entry()
    {
        var source = new DurableBindingRegistryTestCodec<string>("manual.codec");
        var registry = new DurablePayloadCodecRegistry();
        registry.Register(source);
        var raw = registry.GetRequired(typeof(string));
        var view = DurablePayloadCodecSnapshot.Capture(source).CreateView();

        registry.Register(view);
        registry.Register(source);

        Assert.Same(raw, source);
        Assert.NotSame(source, registry.GetRequired("manual.codec", "v1"));
        Assert.Same(registry.GetRequired(typeof(string)), registry.GetRequired("manual.codec", "v1"));
    }

    [Fact]
    public void Failed_source_conflict_leaves_all_indexes_and_prior_reference_unchanged()
    {
        var source = new DurableBindingRegistryTestCodec<string>("conflict.codec");
        var registry = new DurablePayloadCodecRegistry([source]);
        var prior = registry.GetRequired(typeof(string));
        source.ContractNameValue = "conflict.changed";
        var conflictingView = DurablePayloadCodecSnapshot.Capture(source).CreateView();

        var error = Assert.Throws<InvalidOperationException>(() => registry.Register(conflictingView));

        Assert.Contains("conflicting contract metadata", error.Message, StringComparison.Ordinal);
        Assert.Same(prior, registry.GetRequired(typeof(string)));
        Assert.Same(prior, registry.GetRequired("conflict.codec", "v1"));
    }

    [Fact]
    public void Failed_contract_duplicate_leaves_the_existing_contract_and_type_indexes_unchanged()
    {
        var existing = new DurableBindingRegistryTestCodec<string>("duplicate.codec");
        var duplicate = new DurableBindingRegistryTestCodec<int>("duplicate.codec");
        var registry = new DurablePayloadCodecRegistry([existing]);
        var prior = registry.GetRequired("duplicate.codec", "v1");

        Assert.Throws<InvalidOperationException>(() => registry.Register(duplicate));

        Assert.Same(prior, registry.GetRequired("duplicate.codec", "v1"));
        Assert.Same(prior, registry.GetRequired(typeof(string)));
        Assert.Throws<InvalidOperationException>(() => registry.GetRequired(typeof(int)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Multiple_contract_collisions_report_the_ordinal_first_key_independent_of_input_order(bool reverse)
    {
        var bFirst = new DurableBindingRegistryTestCodec<string>("b.collision");
        var bSecond = new DurableBindingRegistryTestCodec<int>("b.collision");
        var aFirst = new DurableBindingRegistryTestCodec<string>("a.collision");
        var aSecond = new DurableBindingRegistryTestCodec<int>("a.collision");
        IDurablePayloadCodec[] codecs = reverse
            ? [aSecond, bFirst, bSecond, aFirst]
            : [bFirst, bSecond, aFirst, aSecond];

        var error = Assert.Throws<InvalidOperationException>(() => new DurablePayloadCodecRegistry(codecs));

        Assert.Equal("Durable contract 'a.collision' version 'v1' is already registered.", error.Message);
    }

    [Fact]
    public async Task Concurrent_equivalent_registrations_publish_one_consistent_entry()
    {
        var source = new DurableBindingRegistryTestCodec<string>("concurrent.codec");
        var views = Enumerable.Range(0, 16)
            .Select(_ => DurablePayloadCodecSnapshot.Capture(source).CreateView()).ToArray();
        var registry = new DurablePayloadCodecRegistry();

        await Task.WhenAll(views.Select(view => Task.Run(() => registry.Register(view))));

        var byType = registry.GetRequired(typeof(string));
        var byContract = registry.GetRequired("concurrent.codec", "v1");
        Assert.Same(byType, byContract);
        Assert.NotSame(source, byType);
    }

    [Fact]
    public async Task Concurrent_frozen_upgrade_and_conflicting_contract_attempts_leave_one_consistent_entry()
    {
        var source = new DurableBindingRegistryTestCodec<string>("concurrent.upgrade");
        var registry = new DurablePayloadCodecRegistry([source]);
        var views = Enumerable.Range(0, 16)
            .Select(_ => DurablePayloadCodecSnapshot.Capture(source).CreateView()).ToArray();

        await Task.WhenAll(views.Select(view => Task.Run(() => registry.Register(view))));

        var canonical = registry.GetRequired("concurrent.upgrade", "v1");
        var conflictOne = new DurableBindingRegistryTestCodec<int>("concurrent.upgrade");
        var conflictTwo = new DurableBindingRegistryTestCodec<byte[]>("concurrent.upgrade");
        var attempts = await Task.WhenAll(
            Task.Run(() => RecordFailure(() => registry.Register(conflictOne))),
            Task.Run(() => RecordFailure(() => registry.Register(conflictTwo))));

        Assert.All(attempts, message => Assert.Equal("Durable contract 'concurrent.upgrade' version 'v1' is already registered.", message));
        Assert.Same(canonical, registry.GetRequired(typeof(string)));
        Assert.Same(canonical, registry.GetRequired("concurrent.upgrade", "v1"));
        Assert.Throws<InvalidOperationException>(() => registry.GetRequired(typeof(int)));
        Assert.Throws<InvalidOperationException>(() => registry.GetRequired(typeof(byte[])));
    }

    private static string RecordFailure(Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        return error.Message;
    }
}
