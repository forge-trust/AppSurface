namespace ForgeTrust.AppSurface.Flow.DurableTask.Tests;

public sealed class FlowContextSerializationValidatorTests
{
    [Fact]
    public void Validate_WithSerializableRecord_ReturnsSuccess()
    {
        var validator = new FlowContextSerializationValidator(new SystemTextJsonFlowContextSerializer());

        var result = validator.Validate(new TestState("ready"));

        Assert.True(result.Succeeded);
        Assert.Null(result.Exception);
    }

    [Fact]
    public void Validate_WithThrowingSerializer_ReturnsFailure()
    {
        var validator = new FlowContextSerializationValidator(new ThrowingSerializer());

        var result = validator.Validate(new TestState("ready"));

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Exception);
        Assert.Contains("round-trip", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemTextJsonSerializer_RoundTripsSerializableRecord()
    {
        var serializer = new SystemTextJsonFlowContextSerializer();

        var payload = serializer.Serialize(new TestState("ready"));
        var result = serializer.Deserialize<TestState>(payload);

        Assert.Equal(new TestState("ready"), result);
    }

    [Fact]
    public void SystemTextJsonSerializer_WithNullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SystemTextJsonFlowContextSerializer(null!));
    }

    [Fact]
    public void SystemTextJsonSerializer_WhenPayloadIsNull_ThrowsJsonException()
    {
        var serializer = new SystemTextJsonFlowContextSerializer();

        Assert.Throws<System.Text.Json.JsonException>(() => serializer.Deserialize<TestState>("null"));
    }

    private sealed record TestState(string Value);

    private sealed class ThrowingSerializer : IFlowContextSerializer
    {
        public string Serialize<TContext>(TContext context) => throw new InvalidOperationException("Nope.");

        public TContext Deserialize<TContext>(string payload) => throw new InvalidOperationException("Nope.");
    }
}
