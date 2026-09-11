using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurableWorkDefinitionRequestTests
{
    [Fact]
    public void CreateRequest_uses_definition_policy_or_explicit_override_and_normalizes_due_time()
    {
        var codec = new DefinitionTestCodec<string>();
        var defaultPolicy = CreatePolicy("default");
        var overridePolicy = CreatePolicy("override");
        var definition = DurableWork.Define("work", "v1", codec, codec,
            DurableProviderSafety.ProviderKeyed, defaultPolicy);
        var due = new DateTimeOffset(2026, 9, 10, 12, 30, 0, TimeSpan.FromHours(-4));

        var fromDefault = definition.CreateRequest(new("scope"), new("command"), "key", "value", dueAtUtc: due);
        var fromOverride = definition.CreateRequest(new("scope"), new("command-2"), "key-2", "value", overridePolicy, due);

        Assert.Equal(defaultPolicy, fromDefault.RetryPolicy);
        Assert.Equal(overridePolicy, fromOverride.RetryPolicy);
        Assert.Equal(due.ToUniversalTime(), fromDefault.DueAtUtc);
        Assert.Equal(due.ToUniversalTime(), fromOverride.DueAtUtc);
    }

    [Fact]
    public void CreateRequest_preserves_every_retry_policy_field_and_distinguishes_default_from_explicit_due_time()
    {
        var codec = new DefinitionTestCodec<string>();
        var policy = CreatePolicy("all-fields");
        var definition = DurableWork.Define("work", "v1", codec, codec,
            DurableProviderSafety.Idempotent, policy);
        var explicitDue = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.FromHours(3));

        var defaultDue = definition.CreateRequest(new("scope"), new("command"), "key", "value");
        var explicitDueRequest = definition.CreateRequest(new("scope"), new("command-2"), "key-2", "value", dueAtUtc: explicitDue);

        Assert.Equal(policy.MaximumAttempts, defaultDue.RetryPolicy.MaximumAttempts);
        Assert.Equal(policy.MaximumElapsedTime, defaultDue.RetryPolicy.MaximumElapsedTime);
        Assert.Equal(policy.InitialRetryDelay, defaultDue.RetryPolicy.InitialRetryDelay);
        Assert.Equal(policy.MaximumRetryDelay, defaultDue.RetryPolicy.MaximumRetryDelay);
        Assert.Equal(policy.LeaseDuration, defaultDue.RetryPolicy.LeaseDuration);
        Assert.Equal(policy.RenewalCadence, defaultDue.RetryPolicy.RenewalCadence);
        Assert.Equal(policy.MaximumLeaseLifetime, defaultDue.RetryPolicy.MaximumLeaseLifetime);
        Assert.Equal(policy.BackoffAlgorithm, defaultDue.RetryPolicy.BackoffAlgorithm);
        Assert.Null(defaultDue.DueAtUtc);
        Assert.Equal(explicitDue.ToUniversalTime(), explicitDueRequest.DueAtUtc);
    }

    [Fact]
    public void CreateRequest_matches_direct_request_in_every_request_field_and_fingerprint()
    {
        var codec = new DefinitionTestCodec<string>();
        var policy = CreatePolicy("linear");
        var scope = new DurableScopeId("scope");
        var command = new DurableCommandId("command");
        var due = new DateTimeOffset(2026, 9, 10, 12, 30, 0, TimeSpan.FromHours(-4));
        var definition = DurableWork.Define("work", "v1", codec, codec,
            DurableProviderSafety.ReconcileBeforeRetry, policy);
        var actual = definition.CreateRequest(scope, command, "key", "value", policy, due);
        var expectedPayload = codec.Encode("value");
        var expected = new DurableWorkRequest(scope, command, "key", "work", "v1", expectedPayload,
            DurableProviderSafety.ReconcileBeforeRetry, policy, due);

        Assert.Equal(expected.ScopeId, actual.ScopeId);
        Assert.Equal(expected.CommandId, actual.CommandId);
        Assert.Equal(expected.IdempotencyKey, actual.IdempotencyKey);
        Assert.Equal(expected.WorkName, actual.WorkName);
        Assert.Equal(expected.WorkVersion, actual.WorkVersion);
        Assert.Equal(expected.Payload, actual.Payload);
        Assert.Equal(expected.ProviderSafety, actual.ProviderSafety);
        Assert.Equal(expected.RetryPolicy, actual.RetryPolicy);
        Assert.Equal(expected.DueAtUtc, actual.DueAtUtc);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
    }

    [Fact]
    public async Task Concurrent_request_creation_is_isolated_and_encodes_once_per_request()
    {
        var codec = new DefinitionTestCodec<string>();
        var definition = DurableWork.Define("work", "v1", codec, codec,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
        const int requestCount = 32;
        var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        var tasks = Enumerable.Range(0, requestCount).Select(async index =>
        {
            if (Interlocked.Increment(ref readyCount) == requestCount)
            {
                allReady.TrySetResult(true);
            }

            await startGate.Task.ConfigureAwait(false);
            return definition.CreateRequest(
                new DurableScopeId($"scope-{index}"), new DurableCommandId($"command-{index}"),
                $"key-{index}", $"value-{index}");
        }).ToArray();

        await allReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        startGate.SetResult(true);
        var requests = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(requestCount, codec.EncodeCalls);
        Assert.Equal(requestCount, requests.Select(request => request.IdempotencyKey).Distinct().Count());
        Assert.Equal(requestCount, requests.Select(request => request.Fingerprint.Sha256).Distinct().Count());
        Assert.All(requests, request => Assert.Equal($"value-{request.ScopeId.Value[6..]}",
            System.Text.Encoding.UTF8.GetString(request.Payload.Content.Span)));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("command")]
    [InlineData("key")]
    public void Invalid_caller_choices_are_rejected_before_encoding(string invalidChoice)
    {
        var codec = new DefinitionTestCodec<string>();
        var definition = DurableWork.Define("work", "v1", codec, codec,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);

        var scope = invalidChoice == "scope" ? default : new DurableScopeId("scope");
        var command = invalidChoice == "command" ? default : new DurableCommandId("command");
        var key = invalidChoice == "key" ? "\n" : "key";

        Assert.ThrowsAny<ArgumentException>(() => definition.CreateRequest(scope, command, key, "value"));
        Assert.Equal(0, codec.EncodeCalls);
    }

    [Fact]
    public void CreateRequest_rejects_an_idempotency_key_one_past_its_limit_before_encoding()
    {
        var codec = new DefinitionTestCodec<string>();
        var definition = DurableWork.Define("work", "v1", codec, codec,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);

        var exception = Assert.Throws<ArgumentException>(() => definition.CreateRequest(
            new("scope"), new("command"), new string('k', 201), "value"));

        Assert.Equal("idempotencyKey", exception.ParamName);
        Assert.Equal(0, codec.EncodeCalls);
    }

    [Fact]
    public void Null_work_is_rejected_before_encoding()
    {
        var codec = new DefinitionTestCodec<string>();
        var definition = DurableWork.Define("work", "v1", codec, codec,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);

        Assert.Throws<ArgumentNullException>(() => definition.CreateRequest(
            new DurableScopeId("scope"), new DurableCommandId("command"), "key", null!));
        Assert.Equal(0, codec.EncodeCalls);
    }

    [Fact]
    public void CreateRequest_supports_value_types_without_treating_zero_as_null()
    {
        var codec = new DefinitionTestCodec<int>(
            encode: value => new DurableEncodedPayload("tests.typed.payload", "v1",
                DurableDataClassification.Operational, BitConverter.GetBytes(value)),
            decode: payload => BitConverter.ToInt32(payload.Content.Span));
        var definition = DurableWork.Define("work", "v1", codec, codec,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);

        var request = definition.CreateRequest(new("scope"), new("command"), "key", 0);

        Assert.Equal(0, BitConverter.ToInt32(request.Payload.Content.Span));
        Assert.Equal(1, codec.EncodeCalls);
    }

    [Fact]
    public void CreateRequest_does_not_retain_mutable_input_or_expose_mutable_payload_bytes()
    {
        var codec = new DefinitionTestCodec<byte[]>(
            encode: value => new DurableEncodedPayload("tests.typed.payload", "v1",
                DurableDataClassification.Operational, value));
        var definition = DurableWork.Define("work", "v1", codec, codec,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
        var input = new byte[] { 1, 2, 3 };

        var request = definition.CreateRequest(new("scope"), new("command"), "key", input);
        input[0] = 9;
        var exposed = request.Payload.Content.ToArray();
        exposed[1] = 8;

        Assert.Equal(new byte[] { 1, 2, 3 }, request.Payload.Content.ToArray());
    }

    private static DurableWorkRetryPolicy CreatePolicy(string algorithm) => new(
        3, TimeSpan.FromHours(2), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), algorithm);
}
