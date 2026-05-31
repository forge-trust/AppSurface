using Microsoft.Extensions.Options;

namespace ForgeTrust.RazorWire;

internal sealed class RazorWireOptionsValidator : IValidateOptions<RazorWireOptions>
{
    public ValidateOptionsResult Validate(string? name, RazorWireOptions options)
    {
        var failures = new List<string>();
        var streams = options.Streams;

        ValidateBasePath(streams.BasePath, failures);
        ValidatePositive(
            streams.MaxChannelNameLength,
            nameof(RazorWireOptions.Streams),
            nameof(RazorWireStreamOptions.MaxChannelNameLength),
            failures);
        ValidatePositive(
            streams.MaxLiveChannels,
            nameof(RazorWireOptions.Streams),
            nameof(RazorWireStreamOptions.MaxLiveChannels),
            failures);
        ValidatePositive(
            streams.MaxLiveSubscriptions,
            nameof(RazorWireOptions.Streams),
            nameof(RazorWireStreamOptions.MaxLiveSubscriptions),
            failures);
        ValidatePositive(
            streams.MaxLiveSubscriptionsPerChannel,
            nameof(RazorWireOptions.Streams),
            nameof(RazorWireStreamOptions.MaxLiveSubscriptionsPerChannel),
            failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateBasePath(string? basePath, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            failures.Add("RazorWire:Streams:BasePath must be a non-empty absolute path.");
            return;
        }

        if (!basePath.StartsWith('/'))
        {
            failures.Add("RazorWire:Streams:BasePath must start with '/'.");
        }

        if (basePath == "/" || basePath.EndsWith('/'))
        {
            failures.Add("RazorWire:Streams:BasePath must not end with '/'.");
        }

        foreach (var c in basePath)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c is '{' or '}' or '?' or '#')
            {
                failures.Add("RazorWire:Streams:BasePath must not contain whitespace, route tokens, query strings, fragments, or control characters.");
                return;
            }
        }
    }

    private static void ValidatePositive(int value, string section, string property, ICollection<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"RazorWire:{section}:{property} must be greater than zero.");
        }
    }
}
