namespace ForgeTrust.AppSurface.Durable.CloudTasks;

/// <summary>Implements bounded, provider-independent syntax checks for Cloud Tasks caller values.</summary>
/// <remarks>Validation is local only. It never establishes provider resource existence, IAM, or queue configuration.</remarks>
internal static class CloudTasksValidation
{
    private const int MaxProjectIdLength = 128;
    private const int MaxLocationIdLength = 63;
    private const int MaxQueueIdLength = 100;
    private const int MaxTaskIdLength = 500;
    private const int MaxAudienceLength = 2_048;
    private const int MaxEndpointLength = 2_083;
    private const int MaxEmailLength = 254;
    private const int MaxTokenLength = 8_192;
    private static readonly TimeSpan MaximumTimerTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    /// <summary>Creates the required-field exception without exposing any supplied value.</summary>
    internal static ArgumentException Required(string parameterName, string constraint) => CreateValidationException(
        parameterName,
        "ASCTV001",
        constraint,
        "Provide a nonblank value that meets this field's documented syntax and length limits.");

    /// <summary>Creates the syntax exception without exposing any supplied value.</summary>
    internal static ArgumentException Syntax(string parameterName, string constraint) => CreateValidationException(
        parameterName,
        "ASCTV002",
        constraint,
        "Use the documented syntax for this field and remove controls or resource separators.");

    /// <summary>Creates the range exception without exposing any supplied value.</summary>
    internal static ArgumentOutOfRangeException Range(string parameterName, string constraint, string safeFix) =>
        new(parameterName, $"{safeFix} See [Cloud Tasks activation validation](https://github.com/forge-trust/AppSurface/blob/main/Durable/cloudtasks-activation.md#validation).")
        {
            Data = { ["CloudTasksValidationCode"] = "ASCTV003", ["CloudTasksConstraint"] = constraint },
        };

    /// <summary>Validates a timeout against positive finite and timer-range policy.</summary>
    internal static TimeSpan ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw Range(parameterName, "positive finite timeout", "Choose a positive finite timeout.");
        }

        if (timeout > MaximumTimerTimeout)
        {
            throw Range(parameterName, "timeout within uint.MaxValue minus one milliseconds", "Choose a timeout within the .NET timer range.");
        }

        return timeout;
    }

    /// <summary>Validates the project resource segment, including supported numeric and legacy forms.</summary>
    internal static string ValidateProjectId(string? value, string parameterName)
    {
        RequireNonblank(value, parameterName, "project identifier");
        if (value!.Length > MaxProjectIdLength)
        {
            throw Range(parameterName, "project identifier length 1 through 128 characters", "Use a project identifier of at most 128 characters.");
        }

        if (!IsAsciiAlphaNumeric(value[0]) || !All(value, static c => IsAsciiAlphaNumeric(c) || c is '-' or '.' or ':' or '~'))
        {
            throw Syntax(parameterName, "project identifier segment (ASCII letters/digits, hyphen, dot, colon, or tilde)");
        }

        return value;
    }

    /// <summary>Validates a lowercase location resource segment.</summary>
    internal static string ValidateLocationId(string? value, string parameterName)
    {
        RequireNonblank(value, parameterName, "location identifier");
        if (value!.Length > MaxLocationIdLength)
        {
            throw Range(parameterName, "location identifier length 1 through 63 characters", "Use a location identifier of at most 63 characters.");
        }

        if (!IsLowerAlphaNumeric(value[0]) || !All(value, static c => IsLowerAlphaNumeric(c) || c == '-'))
        {
            throw Syntax(parameterName, "lowercase location segment (ASCII lowercase letters/digits and hyphen)");
        }

        return value;
    }

    /// <summary>Validates a provider-style queue ID.</summary>
    internal static string ValidateQueueId(string? value, string parameterName)
    {
        RequireNonblank(value, parameterName, "queue identifier");
        if (value!.Length > MaxQueueIdLength)
        {
            throw Range(parameterName, "queue identifier length 1 through 100 characters", "Use a queue identifier of at most 100 characters.");
        }

        if (!All(value, static c => IsAsciiAlphaNumeric(c) || c is '-' or '_'))
        {
            throw Syntax(parameterName, "queue ID (ASCII letters/digits, hyphen, or underscore)");
        }

        return value;
    }

    /// <summary>Validates a task ID accepted by the package contract.</summary>
    internal static string ValidateTaskId(string? value, string parameterName)
    {
        RequireNonblank(value, parameterName, "task identifier");
        if (value!.Length > MaxTaskIdLength)
        {
            throw Range(parameterName, "task identifier length 1 through 500 characters", "Use a task identifier of at most 500 characters.");
        }

        if (!All(value, static c => IsAsciiAlphaNumeric(c) || c is '-' or '_'))
        {
            throw Syntax(parameterName, "task ID (one or more ASCII letters, digits, hyphens, or underscores)");
        }

        return value;
    }

    /// <summary>Validates the worker endpoint shape and encoded URI length.</summary>
    internal static Uri ValidateEndpoint(Uri? value, string parameterName)
    {
        if (value is null)
        {
            throw Required(parameterName, "absolute HTTPS endpoint");
        }

        if (value.OriginalString.Any(char.IsControl)
            || !value.IsAbsoluteUri || !string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(value.Host) || !string.IsNullOrEmpty(value.UserInfo) || !string.IsNullOrEmpty(value.Fragment))
        {
            throw Syntax(parameterName, "absolute HTTPS endpoint with host, no user info, and no fragment");
        }

        if (value.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped).Length > MaxEndpointLength)
        {
            throw Range(parameterName, "encoded endpoint length at most 2083 characters", "Use an HTTPS endpoint whose escaped absolute form is at most 2083 characters.");
        }

        return value;
    }

    /// <summary>Validates a bounded ASCII service account email without network or ownership checks.</summary>
    internal static string ValidateEmail(string? value, string parameterName)
    {
        RequireNonblank(value, parameterName, "OIDC service account email");
        if (value!.Length > MaxEmailLength)
        {
            throw Range(parameterName, "email length at most 254 ASCII characters", "Use an email address of at most 254 ASCII characters.");
        }

        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1
            || !All(value, static c => c <= 0x7f && !char.IsControl(c) && !char.IsWhiteSpace(c)))
        {
            throw Syntax(parameterName, "ASCII email address with one @, nonempty local/domain parts, and no whitespace");
        }

        var local = value[..at];
        var domain = value[(at + 1)..];
        if (local[0] == '.' || local[^1] == '.' || local.Contains("..", StringComparison.Ordinal)
            || !All(local, static c => IsAsciiAlphaNumeric(c) || c is '.' or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '/' or '=' or '?' or '^' or '_' or '`' or '{' or '|' or '}' or '~' or '-'))
        {
            throw Syntax(parameterName, "ASCII email local part without leading, trailing, or repeated dots");
        }

        var labels = domain.Split('.');
        if (labels.Length < 2 || labels.Any(static label => label.Length is 0 or > 63
                || !IsAsciiAlphaNumeric(label[0]) || !IsAsciiAlphaNumeric(label[^1])
                || !All(label, static c => IsAsciiAlphaNumeric(c) || c == '-')))
        {
            throw Syntax(parameterName, "ASCII email domain with DNS-style labels and at least one dot");
        }

        return value;
    }

    /// <summary>Validates an explicit bounded OIDC audience without inferring it from an endpoint.</summary>
    internal static string ValidateAudience(string? value, string parameterName)
    {
        RequireNonblank(value, parameterName, "OIDC audience");
        if (value!.Length > MaxAudienceLength)
        {
            throw Range(parameterName, "audience length at most 2048 characters", "Use an audience of at most 2048 characters.");
        }

        if (HasControl(value))
        {
            throw Syntax(parameterName, "nonblank audience without control characters");
        }

        return value;
    }

    /// <summary>Validates Google worker dispatch deadline range and precision.</summary>
    internal static TimeSpan ValidateDispatchDeadline(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.FromSeconds(15) || value > TimeSpan.FromMinutes(30))
        {
            throw Range(parameterName, "worker dispatch deadline from 15 seconds through 30 minutes", "Choose a deadline from 15 seconds through 30 minutes.");
        }

        if (value.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw Syntax(parameterName, "worker dispatch deadline with whole-millisecond precision");
        }

        return value;
    }

    /// <summary>Validates an expected canonical task resource name, never a provider-returned name.</summary>
    internal static string ValidateTaskName(string? value, string parameterName)
    {
        RequireNonblank(value, parameterName, "expected task resource name");
        if (value!.Length > MaxProjectIdLength + MaxLocationIdLength + MaxQueueIdLength + MaxTaskIdLength + 48)
        {
            throw Range(parameterName, "expected task resource name within bounded segment lengths", "Use a task resource name composed of bounded validated segments.");
        }

        var parts = value.Split('/');
        if (parts.Length != 8 || parts[0] != "projects" || parts[2] != "locations" || parts[4] != "queues" || parts[6] != "tasks")
        {
            throw Syntax(parameterName, "canonical projects/{project}/locations/{location}/queues/{queue}/tasks/{task} resource name");
        }

        _ = ValidateProjectId(parts[1], parameterName);
        _ = ValidateLocationId(parts[3], parameterName);
        _ = ValidateQueueId(parts[5], parameterName);
        _ = ValidateTaskId(parts[7], parameterName);
        return value;
    }

    /// <summary>Checks the bare RFC 6750 bearer token syntax and package byte limit.</summary>
    internal static bool IsValidToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength || token[0] == '=')
        {
            return false;
        }

        var sawPadding = false;
        foreach (var c in token)
        {
            if (c == '=')
            {
                sawPadding = true;
            }
            else if (sawPadding || !(IsAsciiAlphaNumeric(c) || c is '-' or '.' or '_' or '~' or '+' or '/'))
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireNonblank(string? value, string parameterName, string constraint)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Required(parameterName, constraint);
        }
    }

    private static bool HasControl(string value) => value.Any(char.IsControl);

    private static bool IsAsciiAlphaNumeric(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static bool IsLowerAlphaNumeric(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool All(string value, Func<char, bool> predicate)
    {
        foreach (var c in value)
        {
            if (!predicate(c)) return false;
        }

        return true;
    }

    private static ArgumentException CreateValidationException(string parameterName, string code, string constraint, string fix) =>
        new($"{fix} See [Cloud Tasks activation validation](https://github.com/forge-trust/AppSurface/blob/main/Durable/cloudtasks-activation.md#validation).", parameterName)
        {
            Data = { ["CloudTasksValidationCode"] = code, ["CloudTasksConstraint"] = constraint },
        };
}
