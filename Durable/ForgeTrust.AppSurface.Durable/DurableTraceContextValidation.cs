namespace ForgeTrust.AppSurface.Durable;

/// <summary>Validates the bounded W3C fields accepted by the durable trace contract.</summary>
internal static class DurableTraceContextValidation
{
    private const string ZeroTraceId = "00000000000000000000000000000000";
    private const string ZeroSpanId = "0000000000000000";
    private const int MaximumTraceStateMembers = 32;
    private const int MaximumTraceStateKeyLength = 256;
    private const int MaximumTenantIdLength = 241;
    private const int MaximumSystemIdLength = 14;

    internal static bool TryParseTraceParent(
        string? value,
        out string traceParent,
        out string traceId,
        out string spanId,
        out string flags)
    {
        traceParent = string.Empty;
        traceId = string.Empty;
        spanId = string.Empty;
        flags = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length != 55
            || !candidate.StartsWith("00-", StringComparison.Ordinal)
            || candidate[2] != '-'
            || candidate[35] != '-'
            || candidate[52] != '-'
            || !IsLowerHex(candidate.AsSpan(3, 32))
            || !IsLowerHex(candidate.AsSpan(36, 16))
            || !IsLowerHex(candidate.AsSpan(53, 2)))
        {
            return false;
        }

        traceId = candidate.Substring(3, 32).ToLowerInvariant();
        spanId = candidate.Substring(36, 16).ToLowerInvariant();
        flags = candidate.Substring(53, 2).ToLowerInvariant();
        if (traceId == ZeroTraceId || spanId == ZeroSpanId)
        {
            return false;
        }

        traceParent = $"00-{traceId}-{spanId}-{flags}";
        return true;
    }

    internal static bool TryNormalizeTraceState(string? value, out string? traceState)
    {
        traceState = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var candidate = value.Trim();
        if (candidate.Length > 512 || candidate.Contains('\r') || candidate.Contains('\n'))
        {
            return false;
        }

        var members = candidate.Split(',');
        if (members.Length > MaximumTraceStateMembers)
        {
            return false;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            var normalizedMember = member.Trim();
            if (normalizedMember.Length == 0)
            {
                continue;
            }

            var separatorIndex = normalizedMember.IndexOf('=');
            var key = separatorIndex > 0
                ? normalizedMember[..separatorIndex]
                : string.Empty;
            if (separatorIndex <= 0
                || separatorIndex != normalizedMember.LastIndexOf('=')
                || !IsTraceStateKey(key)
                || !IsTraceStateValue(normalizedMember.AsSpan(separatorIndex + 1))
                || !keys.Add(key))
            {
                return false;
            }
        }

        traceState = candidate;
        return true;
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!((character is >= '0' and <= '9') || (character is >= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTraceStateKey(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value.Length > MaximumTraceStateKeyLength)
        {
            return false;
        }

        var atIndex = value.IndexOf('@');
        if (atIndex < 0)
        {
            return IsSimpleTraceStateKey(value);
        }

        if (atIndex != value.LastIndexOf('@'))
        {
            return false;
        }

        return IsTenantId(value[..atIndex])
            && IsSystemId(value[(atIndex + 1)..]);
    }

    private static bool IsSimpleTraceStateKey(ReadOnlySpan<char> value) =>
        IsTraceStateKeyPart(value, MaximumTraceStateKeyLength, allowLeadingDigit: false);

    private static bool IsTenantId(ReadOnlySpan<char> value) =>
        IsTraceStateKeyPart(value, MaximumTenantIdLength, allowLeadingDigit: true);

    private static bool IsSystemId(ReadOnlySpan<char> value) =>
        IsTraceStateKeyPart(value, MaximumSystemIdLength, allowLeadingDigit: false);

    private static bool IsTraceStateKeyPart(
        ReadOnlySpan<char> value,
        int maximumLength,
        bool allowLeadingDigit)
    {
        if (value.IsEmpty
            || value.Length > maximumLength
            || !IsLowerAlpha(value[0]) && !(allowLeadingDigit && IsDigit(value[0])))
        {
            return false;
        }

        foreach (var character in value[1..])
        {
            if (!IsLowerAlphaNumeric(character) && character is not ('_' or '-' or '*' or '/'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTraceStateValue(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value.Length > 256 || value[^1] == ' ')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '\x20' or > '\x7e' || character is ',' or '=')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerAlpha(char character) => character is >= 'a' and <= 'z';

    private static bool IsDigit(char character) => character is >= '0' and <= '9';

    private static bool IsLowerAlphaNumeric(char character) =>
        IsLowerAlpha(character) || IsDigit(character);
}
