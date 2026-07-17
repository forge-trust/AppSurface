using Microsoft.Extensions.Options;

namespace ForgeTrust.RazorWire;

internal sealed class RazorWireOptionsValidator : IValidateOptions<RazorWireOptions>
{
    public ValidateOptionsResult Validate(string? name, RazorWireOptions options)
    {
        var failures = new List<string>();
        var streams = options.Streams;

        ValidateTurbo(options.Turbo, failures);
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

    private static void ValidateTurbo(RazorWireTurboOptions turbo, ICollection<string> failures)
    {
        if (!Enum.IsDefined(turbo.RuntimeMode))
        {
            failures.Add(
                "RazorWireOptions.Turbo.RuntimeMode must be Bundled, Custom, or HostManaged " +
                $"(received '{turbo.RuntimeMode}').");
            return;
        }

        if (turbo.RuntimeMode is not RazorWireTurboRuntimeMode.Custom)
        {
            if (turbo.CustomPath is not null)
            {
                failures.Add(
                    $"RazorWireOptions.Turbo.CustomPath must be null when RuntimeMode is {turbo.RuntimeMode}; " +
                    "set RuntimeMode to Custom to use a package-ordered same-origin runtime.");
            }

            return;
        }

        if (string.IsNullOrEmpty(turbo.CustomPath))
        {
            failures.Add("RazorWireOptions.Turbo.CustomPath is required when RuntimeMode is Custom.");
            return;
        }

        if (!IsValidCustomTurboPath(turbo.CustomPath))
        {
            failures.Add(
                "RazorWireOptions.Turbo.CustomPath must be a non-root path beginning with exactly one '/' and contain only ASCII letters, digits, " +
                "'/', '.', '_', '-', or '~', with no '.' or '..' path segments.");
        }
    }

    internal static bool IsValidCustomTurboPath(string path)
    {
        if (path.Length == 1 || path[0] != '/' || path[1] == '/')
        {
            return false;
        }

        for (var index = 1; index < path.Length; index++)
        {
            var character = path[index];
            if (!((character >= 'a' && character <= 'z')
                  || (character >= 'A' && character <= 'Z')
                  || (character >= '0' && character <= '9')
                  || character is '/' or '.' or '_' or '-' or '~'))
            {
                return false;
            }
        }

        return !path[1..].Split('/').Any(segment => segment is "." or "..");
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

        if (basePath.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '{' or '}' or '?' or '#'))
        {
            failures.Add("RazorWire:Streams:BasePath must not contain whitespace, route tokens, query strings, fragments, or control characters.");
            return;
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
