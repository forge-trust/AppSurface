using Microsoft.Extensions.Options;

namespace ForgeTrust.RazorWire.Tests;

public class RazorWireOptionsTests
{
    [Fact]
    public void TurboRuntimeMode_DefaultsToBundled()
    {
        var options = new RazorWireOptions();

        Assert.Equal(RazorWireTurboRuntimeMode.Bundled, options.Turbo.RuntimeMode);
        Assert.Null(options.Turbo.CustomPath);
    }

    [Theory]
    [InlineData(RazorWireTurboRuntimeMode.Bundled)]
    [InlineData(RazorWireTurboRuntimeMode.HostManaged)]
    public void Validator_AcceptsModesWithoutCustomPath(RazorWireTurboRuntimeMode runtimeMode)
    {
        var options = new RazorWireOptions();
        options.Turbo.RuntimeMode = runtimeMode;

        var result = new RazorWireOptionsValidator().Validate(Options.DefaultName, options);

        Assert.Same(ValidateOptionsResult.Success, result);
    }

    [Theory]
    [InlineData("/assets/turbo.js")]
    [InlineData("/~vendor/Turbo_8-0.12/turbo.js")]
    public void Validator_AcceptsValidCustomTurboPath(string customPath)
    {
        var options = new RazorWireOptions();
        options.Turbo.RuntimeMode = RazorWireTurboRuntimeMode.Custom;
        options.Turbo.CustomPath = customPath;

        var result = new RazorWireOptionsValidator().Validate(Options.DefaultName, options);

        Assert.Same(ValidateOptionsResult.Success, result);
    }

    [Fact]
    public void Validator_RejectsUndefinedTurboRuntimeMode()
    {
        var options = new RazorWireOptions();
        options.Turbo.RuntimeMode = (RazorWireTurboRuntimeMode)42;

        var result = new RazorWireOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            failure => failure.Contains("RazorWireOptions.Turbo.RuntimeMode", StringComparison.Ordinal)
                       && failure.Contains("Bundled, Custom, or HostManaged", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(RazorWireTurboRuntimeMode.Bundled)]
    [InlineData(RazorWireTurboRuntimeMode.HostManaged)]
    public void Validator_RejectsIgnoredCustomPath(RazorWireTurboRuntimeMode runtimeMode)
    {
        var options = new RazorWireOptions();
        options.Turbo.RuntimeMode = runtimeMode;
        options.Turbo.CustomPath = "/assets/turbo.js";

        var result = new RazorWireOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("RazorWireOptions.Turbo.CustomPath", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validator_RejectsMissingCustomTurboPath(string? customPath)
    {
        var options = new RazorWireOptions();
        options.Turbo.RuntimeMode = RazorWireTurboRuntimeMode.Custom;
        options.Turbo.CustomPath = customPath;

        var result = new RazorWireOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("CustomPath is required", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(" /assets/turbo.js")]
    [InlineData("/assets/turbo.js ")]
    [InlineData("//cdn.example.com/turbo.js")]
    [InlineData("https://cdn.example.com/turbo.js")]
    [InlineData("assets/turbo.js")]
    [InlineData("/")]
    [InlineData("/assets/./turbo.js")]
    [InlineData("/assets/../turbo.js")]
    [InlineData("/assets/%74urbo.js")]
    [InlineData("/assets/turbo\\runtime.js")]
    [InlineData("/assets/turbo.js?version=8")]
    [InlineData("/assets/turbo.js#runtime")]
    [InlineData("/assets/\"turbo.js")]
    [InlineData("/assets/<turbo>.js")]
    [InlineData("/assets/turbo&runtime.js")]
    [InlineData("/assets/turbo\t.js")]
    public void Validator_RejectsInvalidCustomTurboPathWithoutTrimming(string customPath)
    {
        var options = new RazorWireOptions();
        options.Turbo.RuntimeMode = RazorWireTurboRuntimeMode.Custom;
        options.Turbo.CustomPath = customPath;

        var result = new RazorWireOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("RazorWireOptions.Turbo.CustomPath", StringComparison.Ordinal));
    }

    [Fact]
    public void StreamsAuthorizationMode_DefaultsToDenyAll()
    {
        var options = new RazorWireOptions();

        Assert.Equal(RazorWireStreamAuthorizationMode.DenyAll, options.Streams.AuthorizationMode);
        Assert.Equal(RazorWireStreamOptions.DefaultMaxChannelNameLength, options.Streams.MaxChannelNameLength);
        Assert.Equal(RazorWireStreamOptions.DefaultMaxLiveChannels, options.Streams.MaxLiveChannels);
        Assert.Equal(RazorWireStreamOptions.DefaultMaxLiveSubscriptions, options.Streams.MaxLiveSubscriptions);
        Assert.Equal(
            RazorWireStreamOptions.DefaultMaxLiveSubscriptionsPerChannel,
            options.Streams.MaxLiveSubscriptionsPerChannel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validator_RejectsNonPositiveStreamAdmissionLimits(int value)
    {
        var options = new RazorWireOptions();
        options.Streams.MaxChannelNameLength = value;
        options.Streams.MaxLiveChannels = value;
        options.Streams.MaxLiveSubscriptions = value;
        options.Streams.MaxLiveSubscriptionsPerChannel = value;

        var result = new RazorWireOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            failure => failure.Contains("RazorWire:Streams:MaxChannelNameLength", StringComparison.Ordinal));
        Assert.Contains(
            result.Failures!,
            failure => failure.Contains("RazorWire:Streams:MaxLiveChannels", StringComparison.Ordinal));
        Assert.Contains(
            result.Failures!,
            failure => failure.Contains("RazorWire:Streams:MaxLiveSubscriptions", StringComparison.Ordinal));
        Assert.Contains(
            result.Failures!,
            failure => failure.Contains("RazorWire:Streams:MaxLiveSubscriptionsPerChannel", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("streams")]
    [InlineData("/_rw/streams/")]
    [InlineData("/_rw/{channel}")]
    [InlineData("/_rw/streams?x=1")]
    [InlineData("/_rw/streams#live")]
    [InlineData("/_rw/stream path")]
    public void Validator_RejectsInvalidStreamBasePath(string basePath)
    {
        var options = new RazorWireOptions();
        options.Streams.BasePath = basePath;

        var result = new RazorWireOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            failure => failure.Contains("RazorWire:Streams:BasePath", StringComparison.Ordinal)
                       || failure.Contains("BasePath must not", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DefaultFailureMessage_WhenAssignedNullOrBlank_UsesSafeFallback(string? value)
    {
        var options = new RazorWireOptions();

        options.Forms.DefaultFailureMessage = value!;

        Assert.Equal("We could not submit this form. Check your input and try again.", options.Forms.DefaultFailureMessage);
    }

    [Fact]
    public void DefaultFailureMessage_WhenAssignedNonBlankValue_PreservesValue()
    {
        var options = new RazorWireOptions();

        options.Forms.DefaultFailureMessage = "Custom message";

        Assert.Equal("Custom message", options.Forms.DefaultFailureMessage);
    }
}
