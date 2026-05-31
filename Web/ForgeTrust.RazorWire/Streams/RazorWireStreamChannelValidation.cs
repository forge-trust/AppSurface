namespace ForgeTrust.RazorWire.Streams;

internal static class RazorWireStreamChannelValidation
{
    public static RazorWireStreamChannelValidationResult Validate(string? channel, RazorWireStreamOptions options)
    {
        if (string.IsNullOrEmpty(channel))
        {
            return RazorWireStreamChannelValidationResult.Invalid(RazorWireStreamAdmissionRejectionReason.InvalidChannelName);
        }

        if (channel.Length > options.MaxChannelNameLength)
        {
            return RazorWireStreamChannelValidationResult.Invalid(RazorWireStreamAdmissionRejectionReason.ChannelNameTooLong);
        }

        if (channel.Any(c => !IsAllowedChannelCharacter(c)))
        {
            return RazorWireStreamChannelValidationResult.Invalid(RazorWireStreamAdmissionRejectionReason.InvalidChannelName);
        }

        return RazorWireStreamChannelValidationResult.Valid;
    }

    private static bool IsAllowedChannelCharacter(char c)
    {
        return c is >= 'A' and <= 'Z'
            || c is >= 'a' and <= 'z'
            || c is >= '0' and <= '9'
            || c is '.'
            || c is '_'
            || c is '-'
            || c is ':';
    }
}

internal readonly record struct RazorWireStreamChannelValidationResult(
    bool IsValid,
    RazorWireStreamAdmissionRejectionReason? RejectionReason)
{
    public static RazorWireStreamChannelValidationResult Valid { get; } = new(true, null);

    public static RazorWireStreamChannelValidationResult Invalid(RazorWireStreamAdmissionRejectionReason reason)
    {
        return new RazorWireStreamChannelValidationResult(false, reason);
    }
}
