using System.Collections.Concurrent;
using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurableWorkDefinitionTests
{
    [Fact]
    public void Define_captures_contract_and_codec_facts_without_encoding()
    {
        var counts = new ConcurrentDictionary<string, int>();
        var codec = new DefinitionTestCodec<string>(getterCounts: counts);
        var policy = CreatePolicy();

        var definition = DurableWork.Define("tests.work", "v7", codec, codec,
            DurableProviderSafety.Idempotent, policy);

        Assert.Equal("tests.work", definition.WorkName);
        Assert.Equal("v7", definition.WorkVersion);
        Assert.Equal(DurableProviderSafety.Idempotent, definition.ProviderSafety);
        Assert.Equal(policy, definition.DefaultRetryPolicy);
        Assert.Same(definition.WorkCodec, definition.ResultCodec);
        Assert.Equal(0, codec.EncodeCalls);
        Assert.All(counts.Values, count => Assert.Equal(1, count));
    }

    [Theory]
    [InlineData(null, "v1", "workName")]
    [InlineData("", "v1", "workName")]
    [InlineData("   ", "v1", "workName")]
    [InlineData("v", "", "workVersion")]
    [InlineData("v", "   ", "workVersion")]
    [InlineData("v", "line\nbreak", "workVersion")]
    public void Define_rejects_invalid_work_identity(string? name, string version, string parameter)
    {
        var codec = new DefinitionTestCodec<string>();
        var exception = Assert.ThrowsAny<ArgumentException>(() => DurableWork.Define(
            name!, version, codec, codec, DurableProviderSafety.Idempotent, CreatePolicy()));

        Assert.Equal(parameter, exception.ParamName);
    }

    [Fact]
    public void Define_rejects_undefined_safety_and_null_retry_policy()
    {
        var codec = new DefinitionTestCodec<string>();

        Assert.Throws<ArgumentOutOfRangeException>(() => DurableWork.Define(
            "work", "v1", codec, codec, (DurableProviderSafety)999, CreatePolicy()));
        Assert.Throws<ArgumentNullException>(() => DurableWork.Define(
            "work", "v1", codec, codec, DurableProviderSafety.Idempotent, null!));
    }

    [Fact]
    public void Define_rejects_null_codecs_before_any_codec_metadata_is_read()
    {
        var codec = new DefinitionTestCodec<string>();

        var workException = Assert.Throws<ArgumentNullException>(() => DurableWork.Define<string, string>(
            "work", "v1", null!, codec, DurableProviderSafety.Idempotent, CreatePolicy()));
        var resultException = Assert.Throws<ArgumentNullException>(() => DurableWork.Define<string, string>(
            "work", "v1", codec, null!, DurableProviderSafety.Idempotent, CreatePolicy()));
        Assert.Equal("workCodec", workException.ParamName);
        Assert.Equal("resultCodec", resultException.ParamName);
        Assert.Equal(0, codec.EncodeCalls);

        var retryException = Assert.Throws<ArgumentNullException>(() => DurableWork.Define<string, string>(
            "work", "v1", codec, codec, DurableProviderSafety.Idempotent, null!));
        Assert.Equal("defaultRetryPolicy", retryException.ParamName);
    }

    [Fact]
    public void Define_accepts_exact_identifier_limits()
    {
        var codec = new DefinitionTestCodec<string>();

        var definition = DurableWork.Define(
            new string('w', 200), new string('v', 100), codec, codec,
            DurableProviderSafety.Idempotent, CreatePolicy());

        Assert.Equal(200, definition.WorkName.Length);
        Assert.Equal(100, definition.WorkVersion.Length);
    }

    [Fact]
    public void Define_captures_two_distinct_sources_once_each_and_rejects_wrong_result_type()
    {
        var workCounts = new ConcurrentDictionary<string, int>();
        var resultCounts = new ConcurrentDictionary<string, int>();
        var work = new DefinitionTestCodec<string>(getterCounts: workCounts);
        var result = new DefinitionTestCodec<string>(payloadType: typeof(int), getterCounts: resultCounts);

        Assert.Throws<ArgumentException>(() => DurableWork.Define(
            "work", "v1", work, result, DurableProviderSafety.Idempotent, CreatePolicy()));

        foreach (var counts in new[] { workCounts, resultCounts })
        {
            Assert.All(
                new[] { "PayloadType", "ContractName", "ContractVersion", "Classification", "RetentionPolicyId" },
                property => Assert.Equal(1, counts[property]));
        }
    }

    [Fact]
    public void Define_rejects_a_wrong_input_payload_type_independently_of_contract_names()
    {
        var work = new DefinitionTestCodec<string>(
            contractName: "input.contract", payloadType: typeof(int));
        var result = new DefinitionTestCodec<string>(contractName: "result.contract");

        var exception = Assert.Throws<ArgumentException>(() => DurableWork.Define(
            "work", "v1", work, result, DurableProviderSafety.Idempotent, CreatePolicy()));

        Assert.Equal("codec", exception.ParamName);
    }

    [Fact]
    public void Define_rejects_work_and_version_at_one_past_their_limits_with_stable_names()
    {
        var codec = new DefinitionTestCodec<string>();

        var workNameException = Assert.Throws<ArgumentException>(() => DurableWork.Define(
            new string('w', 201), "v1", codec, codec, DurableProviderSafety.Idempotent, CreatePolicy()));
        var versionException = Assert.Throws<ArgumentException>(() => DurableWork.Define(
            "work", new string('v', 101), codec, codec, DurableProviderSafety.Idempotent, CreatePolicy()));

        Assert.Equal("workName", workNameException.ParamName);
        Assert.Equal("workVersion", versionException.ParamName);
    }

    private static DurableWorkRetryPolicy CreatePolicy() => new(
        3, TimeSpan.FromHours(2), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), "linear-v1");
}
