using System.Text;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurableWorkExitTests
{
    [Fact]
    public void Factories_expose_only_the_corresponding_result_or_code()
    {
        var succeeded = DurableWorkExit<string>.Succeeded("sent");
        var retry = DurableWorkExit<string>.RetryBeforeEffect("app.gmail.sender_list_transient");
        var failed = DurableWorkExit<string>.FailedTerminal("app.gmail.sender_list_invalid");
        var ambiguous = DurableWorkExit<string>.AmbiguousExternalOutcome("app.gmail.sender_list_unknown");

        Assert.Equal(DurableWorkExitKind.Succeeded, succeeded.Kind);
        Assert.Null(succeeded.Code);
        Assert.Equal("sent", succeeded.Result);
        Assert.Equal(DurableWorkExitKind.RetryBeforeEffect, retry.Kind);
        Assert.Equal("app.gmail.sender_list_transient", retry.Code);
        Assert.Null(retry.Result);
        Assert.Equal(DurableWorkExitKind.FailedTerminal, failed.Kind);
        Assert.Equal("app.gmail.sender_list_invalid", failed.Code);
        Assert.Null(failed.Result);
        Assert.Equal(DurableWorkExitKind.AmbiguousExternalOutcome, ambiguous.Kind);
        Assert.Equal("app.gmail.sender_list_unknown", ambiguous.Code);
        Assert.Null(ambiguous.Result);
    }

    [Fact]
    public void Factories_reject_missing_results_and_unsafe_codes()
    {
        Assert.Throws<ArgumentNullException>(() => DurableWorkExit<string>.Succeeded(null!));
        Assert.Throws<ArgumentException>(() => DurableWorkExit<string>.RetryBeforeEffect("bad value"));
        Assert.Throws<ArgumentException>(() => DurableWorkExit<string>.FailedTerminal("bad\ncode"));
        Assert.Throws<ArgumentException>(() => DurableWorkExit<string>.AmbiguousExternalOutcome(new string('a', 121)));
        Assert.Throws<ArgumentException>(() => DurableWorkExit<string>.RetryBeforeEffect("ASDUR106"));
        Assert.Throws<ArgumentException>(() => DurableWorkExit<string>.FailedTerminal("asdur500"));
        Assert.Throws<ArgumentException>(() => DurableWorkExit<string>.AmbiguousExternalOutcome("ASDUR999"));
    }

    [Fact]
    public async Task Legacy_prepared_work_is_adapted_to_a_success_exit()
    {
        var payload = new StringCodec("tests.exit.result").Encode("sent");
        var prepared = new LegacyPreparedWork(payload);

        var exit = await prepared.InvokeExitAsync();

        Assert.Equal(DurableWorkExitKind.Succeeded, exit.Kind);
        Assert.Null(exit.Code);
        Assert.Same(payload, exit.Result);
    }

    [Fact]
    public async Task Exit_registration_encodes_explicit_facts_and_fails_safe_through_legacy_boundary()
    {
        var workCodec = new StringCodec("tests.exit.input");
        var resultCodec = new StringCodec("tests.exit.result");
        var registration = new DurableWorkExitRegistration<string, string, RetryExitExecutor>(
            "tests.exit.work",
            "v2",
            workCodec,
            resultCodec);
        await using var services = new ServiceCollection().AddSingleton<RetryExitExecutor>().BuildServiceProvider();
        var prepared = registration.Prepare(services, CreateContext(workCodec));

        var exit = await prepared.InvokeExitAsync();
        var compatibility = await Assert.ThrowsAsync<DurableWorkExitCompatibilityException>(
            async () => await prepared.InvokeAsync());

        Assert.Equal(DurableWorkExitKind.RetryBeforeEffect, exit.Kind);
        Assert.Equal("app.gmail.sender_list_transient", exit.Code);
        Assert.Null(exit.Result);
        Assert.Equal(
            "Exit-aware Work returned a non-success exit, but the provider invoked the legacy success-only boundary. Upgrade the provider to InvokeExitAsync.",
            compatibility.Message);
    }

    [Fact]
    public async Task Exit_registration_encodes_a_success_result_and_is_fixed_to_provider_keyed()
    {
        var workCodec = new StringCodec("tests.exit.input");
        var resultCodec = new StringCodec("tests.exit.result");
        var registration = new DurableWorkExitRegistration<string, string, SuccessExitExecutor>(
            "tests.exit.work",
            "v2",
            workCodec,
            resultCodec);
        await using var services = new ServiceCollection().AddSingleton<SuccessExitExecutor>().BuildServiceProvider();

        var exit = await registration.Prepare(services, CreateContext(workCodec)).InvokeExitAsync();

        Assert.Equal(DurableProviderSafety.ProviderKeyed, registration.ProviderSafety);
        Assert.Equal(DurableWorkExitKind.Succeeded, exit.Kind);
        Assert.Null(exit.Code);
        Assert.Equal("sent:input", resultCodec.Decode(exit.Result!));
    }

    [Fact]
    public async Task Exit_registration_preserves_success_for_the_legacy_registration_boundary()
    {
        var workCodec = new StringCodec("tests.exit.input");
        var resultCodec = new StringCodec("tests.exit.result");
        var registration = new DurableWorkExitRegistration<string, string, SuccessExitExecutor>(
            "tests.exit.work",
            "v2",
            workCodec,
            resultCodec);
        await using var services = new ServiceCollection().AddSingleton<SuccessExitExecutor>().BuildServiceProvider();

        var result = await registration.InvokeAsync(services, CreateContext(workCodec));

        Assert.False(registration.CanReconcile);
        Assert.Equal("sent:input", resultCodec.Decode(result));
    }

    [Fact]
    public async Task Exit_registration_rejects_reconciliation()
    {
        var workCodec = new StringCodec("tests.exit.input");
        var registration = new DurableWorkExitRegistration<string, string, SuccessExitExecutor>(
            "tests.exit.work",
            "v2",
            workCodec,
            new StringCodec("tests.exit.result"));
        await using var services = new ServiceCollection().AddSingleton<SuccessExitExecutor>().BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await registration.ReconcileAsync(services, CreateContext(workCodec)));

        Assert.Equal("This durable work registration has no provider reconciler.", exception.Message);
    }

    [Fact]
    public async Task Exit_registration_rejects_a_codec_that_returns_no_success_payload()
    {
        var workCodec = new StringCodec("tests.exit.input");
        var registration = new DurableWorkExitRegistration<string, string, SuccessExitExecutor>(
            "tests.exit.work",
            "v2",
            workCodec,
            new NullEncodingStringCodec(new StringCodec("tests.exit.result")));
        await using var services = new ServiceCollection().AddSingleton<SuccessExitExecutor>().BuildServiceProvider();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await registration.Prepare(services, CreateContext(workCodec)).InvokeExitAsync());
    }

    [Fact]
    public async Task Exit_registration_forwards_cancellation_to_the_executor()
    {
        var workCodec = new StringCodec("tests.exit.input");
        var registration = new DurableWorkExitRegistration<string, string, CancellationExitExecutor>(
            "tests.exit.work",
            "v2",
            workCodec,
            new StringCodec("tests.exit.result"));
        await using var services = new ServiceCollection().AddSingleton<CancellationExitExecutor>().BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await registration.Prepare(services, CreateContext(workCodec)).InvokeExitAsync(cancellation.Token));
    }

    [Fact]
    public void Service_registration_adds_the_fixed_exit_aware_contract()
    {
        var services = new ServiceCollection();

        services.AddDurableWorkExit<string, string, SuccessExitExecutor>(
            "tests.exit.work",
            "v2",
            new StringCodec("tests.exit.input"),
            new StringCodec("tests.exit.result"));
        using var provider = services.BuildServiceProvider();

        var registration = Assert.IsType<DurableWorkExitRegistration<string, string, SuccessExitExecutor>>(
            Assert.Single(provider.GetServices<DurableWorkRegistration>()));
        Assert.Equal(DurableProviderSafety.ProviderKeyed, registration.ProviderSafety);
        Assert.NotNull(provider.GetRequiredService<SuccessExitExecutor>());
        Assert.IsType<DurableWorkRegistry>(provider.GetRequiredService<IDurableWorkRegistry>());
    }

    private static DurableWorkExecutionContext CreateContext(StringCodec workCodec) => new(
        new DurableScopeId("tests-exit-scope"),
        new DurableWorkId("tests-exit-work"),
        "tests.exit.work",
        "v2",
        workCodec.Encode("input"),
        DurableProviderSafety.ProviderKeyed,
        DurableWorkerExecutionIdentity.Create("tests-exit-activity", 1, 1, 1, "tests-exit-epoch"));

    private sealed class LegacyPreparedWork(DurableEncodedPayload result) : DurablePreparedWork
    {
        public override ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(result);
    }

    private sealed class RetryExitExecutor : IDurableWorkExitExecutor<string, string>
    {
        public ValueTask<DurableWorkExit<string>> ExecuteAsync(
            DurableWorkerEnvelope<string> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DurableWorkExit<string>.RetryBeforeEffect("app.gmail.sender_list_transient"));
    }

    private sealed class SuccessExitExecutor : IDurableWorkExitExecutor<string, string>
    {
        public ValueTask<DurableWorkExit<string>> ExecuteAsync(
            DurableWorkerEnvelope<string> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DurableWorkExit<string>.Succeeded($"sent:{work.Payload}"));
    }

    private sealed class CancellationExitExecutor : IDurableWorkExitExecutor<string, string>
    {
        public ValueTask<DurableWorkExit<string>> ExecuteAsync(
            DurableWorkerEnvelope<string> work,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(DurableWorkExit<string>.Succeeded(work.Payload!));
        }
    }

    private sealed class StringCodec(string contractName) : IDurablePayloadCodec<string>
    {
        public Type PayloadType => typeof(string);

        public string ContractName { get; } = contractName;

        public string ContractVersion => "v1";

        public DurableDataClassification Classification => DurableDataClassification.Operational;

        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

        public DurableEncodedPayload Encode(string value) => new(
            ContractName,
            ContractVersion,
            Classification,
            Encoding.UTF8.GetBytes(value),
            RetentionPolicyId);

        public string Decode(DurableEncodedPayload payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (!string.Equals(payload.ContractName, ContractName, StringComparison.Ordinal)
                || !string.Equals(payload.ContractVersion, ContractVersion, StringComparison.Ordinal))
            {
                throw new ArgumentException("Payload does not match this test codec.", nameof(payload));
            }

            return Encoding.UTF8.GetString(payload.Content.Span);
        }

        public DurableEncodedPayload EncodeObject(object value) => Encode(Assert.IsType<string>(value));

        public object DecodeObject(DurableEncodedPayload payload) => Decode(payload);
    }

    private sealed class NullEncodingStringCodec(StringCodec inner) : IDurablePayloadCodec<string>
    {
        public Type PayloadType => inner.PayloadType;

        public string ContractName => inner.ContractName;

        public string ContractVersion => inner.ContractVersion;

        public DurableDataClassification Classification => inner.Classification;

        public string RetentionPolicyId => inner.RetentionPolicyId;

        public DurableEncodedPayload Encode(string value) => null!;

        public string Decode(DurableEncodedPayload payload) => inner.Decode(payload);

        public DurableEncodedPayload EncodeObject(object value) => Encode(Assert.IsType<string>(value));

        public object DecodeObject(DurableEncodedPayload payload) => inner.DecodeObject(payload);
    }
}
