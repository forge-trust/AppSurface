using System.Globalization;
using System.Numerics;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Validates doctor intent and keeps its resolved connection string private to one command invocation.</summary>
/// <remarks>
/// Doctor accepts only environment-variable names on argv. This object resolves each distinct selected variable once,
/// validates the PostgreSQL settings and configured epoch, and keeps the connection string out of request and result
/// records. See the Durable doctor command reference for the supported grammar and evidence boundary.
/// </remarks>
internal sealed class DurableDoctorInput
{
    internal const string DefaultConnectionEnvironmentName = "APPSURFACE_DURABLE_CONNECTION";
    internal const string DefaultEpochEnvironmentName = "APPSURFACE_DURABLE_RUNTIME_EPOCH";
    internal const string DefaultTimeout = "10s";

    private readonly string _connectionString;

    private DurableDoctorInput(DurableDoctorRequest request, string connectionString)
    {
        Request = request;
        _connectionString = connectionString;
    }

    /// <summary>Gets the validated, secret-free doctor request.</summary>
    internal DurableDoctorRequest Request { get; }

    /// <summary>Calls the configured observation service without exposing connection custody to printable records.</summary>
    internal ValueTask<DurableDoctorObservation> InspectAsync(
        IDurableDoctorService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.InspectAsync(_connectionString, Request, cancellationToken);
    }

    /// <summary>Validates raw CliFx string values and resolves the selected environment variables exactly once.</summary>
    /// <param name="connectionEnvironmentName">Environment-variable name or null to use the documented default.</param>
    /// <param name="epochEnvironmentName">Environment-variable name or null to use the documented default.</param>
    /// <param name="workerId">Optional worker label; it is never trimmed or normalized.</param>
    /// <param name="staleAfter">Optional positive decimal duration paired with <paramref name="workerId"/>.</param>
    /// <param name="timeout">Optional total timeout; null uses the documented ten-second default.</param>
    /// <param name="format">Output format, exactly <c>text</c> or <c>json</c>; null uses text.</param>
    /// <param name="getEnvironmentVariable">Environment lookup seam used by tests and the real entry point.</param>
    /// <returns>A validated invocation whose connection string is private to this instance.</returns>
    /// <exception cref="DurableDoctorInputException">The selected argv or environment values are invalid.</exception>
    internal static DurableDoctorInput Create(
        string? connectionEnvironmentName,
        string? epochEnvironmentName,
        string? workerId,
        string? staleAfter,
        string? timeout,
        string? format,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        var connectionName = ResolveEnvironmentName(
            connectionEnvironmentName,
            DefaultConnectionEnvironmentName);
        var epochName = ResolveEnvironmentName(
            epochEnvironmentName,
            DefaultEpochEnvironmentName);
        var selectedFormat = format ?? "text";
        if (selectedFormat is not ("text" or "json"))
        {
            throw new DurableDoctorInputException();
        }

        if ((workerId is null) != (staleAfter is null))
        {
            throw new DurableDoctorInputException();
        }

        if (workerId is not null && !IsValidWorkerId(workerId))
        {
            throw new DurableDoctorInputException();
        }

        var parsedTimeout = ParseDuration(timeout ?? DefaultTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));
        TimeSpan? parsedStaleAfter = staleAfter is null
            ? null
            : ParseDuration(staleAfter, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1));

        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var resolvedValues = new Dictionary<string, string?>(StringComparer.Ordinal);
        var connectionString = ResolveValue(connectionName);
        var epochValue = ResolveValue(epochName);
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(epochValue))
        {
            throw new DurableDoctorInputException();
        }

        if (!Guid.TryParse(epochValue, out var configuredEpoch) || configuredEpoch == Guid.Empty)
        {
            throw new DurableDoctorInputException();
        }

        try
        {
            var settings = new NpgsqlConnectionStringBuilder(connectionString);
            if (!settings.TryGetValue("Host", out var hostValue)
                || hostValue is not string host
                || string.IsNullOrWhiteSpace(host)
                || settings.Port is < 1 or > 65_535)
            {
                throw new DurableDoctorInputException();
            }
        }
        catch (DurableDoctorInputException)
        {
            throw;
        }
        catch (Exception exception) when (IsConnectionStringFormatFailure(exception))
        {
            throw new DurableDoctorInputException();
        }

        var request = new DurableDoctorRequest(
            configuredEpoch,
            connectionName,
            epochName,
            workerId,
            parsedStaleAfter,
            parsedTimeout,
            selectedFormat);
        return new DurableDoctorInput(request, connectionString);

        string? ResolveValue(string name)
        {
            if (!resolvedValues.TryGetValue(name, out var value))
            {
                value = getEnvironmentVariable(name);
                resolvedValues.Add(name, value);
            }

            return value;
        }
    }

    /// <summary>Parses one positive decimal duration into exact representable TimeSpan ticks.</summary>
    /// <param name="value">A number followed by lowercase <c>ms</c>, <c>s</c>, <c>m</c>, or <c>h</c>.</param>
    /// <param name="minimum">Inclusive lower bound for the selected duration role.</param>
    /// <param name="maximum">Inclusive upper bound for the selected duration role.</param>
    /// <returns>The parsed duration when it is representable and within the requested range.</returns>
    /// <exception cref="DurableDoctorInputException">The token is malformed, unrepresentable, or outside its range.</exception>
    internal static TimeSpan ParseDuration(string value, TimeSpan minimum, TimeSpan maximum)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > 64 || minimum < TimeSpan.Zero || maximum < minimum)
        {
            throw new DurableDoctorInputException();
        }

        var suffixLength = value.EndsWith("ms", StringComparison.Ordinal) ? 2 : 1;
        var suffix = suffixLength == 2 ? "ms" : value[^1..];
        var ticksPerUnit = suffix switch
        {
            "ms" => TimeSpan.TicksPerMillisecond,
            "s" => TimeSpan.TicksPerSecond,
            "m" => TimeSpan.TicksPerMinute,
            "h" => TimeSpan.TicksPerHour,
            _ => throw new DurableDoctorInputException(),
        };

        var number = value.AsSpan(0, value.Length - suffixLength);
        if (!IsPositiveDecimalToken(number))
        {
            throw new DurableDoctorInputException();
        }

        var decimalPointIndex = number.IndexOf('.');
        var fractionalDigits = decimalPointIndex < 0 ? 0 : number.Length - decimalPointIndex - 1;
        var digits = number.ToString().Replace(".", string.Empty, StringComparison.Ordinal);
        if (!BigInteger.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var coefficient)
            || coefficient.IsZero)
        {
            throw new DurableDoctorInputException();
        }

        var denominator = BigInteger.Pow(10, fractionalDigits);
        var tickNumerator = coefficient * ticksPerUnit;
        var ticks = BigInteger.DivRem(tickNumerator, denominator, out var remainder);
        if (!remainder.IsZero || ticks < BigInteger.One || ticks > TimeSpan.MaxValue.Ticks)
        {
            throw new DurableDoctorInputException();
        }

        var duration = TimeSpan.FromTicks((long)ticks);
        if (duration < minimum || duration > maximum)
        {
            throw new DurableDoctorInputException();
        }

        return duration;
    }

    /// <summary>Applies the schema command's environment-name grammar, with the doctor contract's 200-character cap.</summary>
    internal static string ResolveEnvironmentName(string? suppliedName, string defaultName)
    {
        ArgumentNullException.ThrowIfNull(defaultName);
        var name = (suppliedName ?? defaultName).Trim();
        if (name.Length is 0 or > 200
            || !(char.IsLetter(name[0]) || name[0] == '_')
            || !name.All(static character => char.IsLetterOrDigit(character) || character == '_'))
        {
            throw new DurableDoctorInputException();
        }

        return name;
    }

    private static bool IsPositiveDecimalToken(ReadOnlySpan<char> value)
    {
        var decimalPointSeen = false;
        var digitsBeforePoint = 0;
        var digitsAfterPoint = 0;
        foreach (var character in value)
        {
            if (character is >= '0' and <= '9')
            {
                if (decimalPointSeen)
                {
                    digitsAfterPoint++;
                }
                else
                {
                    digitsBeforePoint++;
                }
            }
            else if (character == '.' && !decimalPointSeen)
            {
                decimalPointSeen = true;
            }
            else
            {
                return false;
            }
        }

        return digitsBeforePoint > 0 && (!decimalPointSeen || digitsAfterPoint > 0);
    }

    private static bool IsValidWorkerId(string value)
    {
        if (value.Length is 0 or > 200)
        {
            return false;
        }

        return value.All(static character =>
            character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-' or '_' or '.' or ':');
    }

    private static bool IsConnectionStringFormatFailure(Exception exception) =>
        exception is ArgumentException or FormatException or InvalidOperationException or OverflowException or NpgsqlException;
}

/// <summary>A fixed-category signal for invalid doctor arguments or selected environment values.</summary>
internal sealed class DurableDoctorInputException : Exception
{
}

/// <summary>Classifies raw doctor argv before generic CLI parsing can echo malformed tokens.</summary>
internal static class DurableDoctorArgumentAdmission
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--connection-env",
        "--runtime-epoch-env",
        "--worker-id",
        "--stale-after",
        "--timeout",
        "--format",
    };

    /// <summary>Checks only a leading <c>durable doctor</c> route and its option-token structure.</summary>
    /// <param name="arguments">The original, unnormalized process argv.</param>
    /// <returns>A safe admission decision and the uniquely valid output-format hint, if present.</returns>
    internal static DurableDoctorArgumentAdmissionResult Inspect(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var routeLength = FindRouteLength(arguments);
        if (routeLength == 0)
        {
            return DurableDoctorArgumentAdmissionResult.NotDoctor;
        }

        var remaining = arguments.AsSpan(routeLength);
        var format = FindUniqueFormatHint(remaining);
        var helpCount = remaining.ToArray().Count(static argument => argument is "--help" or "-h");
        if (helpCount > 0)
        {
            var safeHelp = remaining.Length == 1
                && helpCount == 1
                && (remaining[0] is "--help" or "-h");
            return safeHelp
                ? new DurableDoctorArgumentAdmissionResult(true, false, true, "text")
                : new DurableDoctorArgumentAdmissionResult(true, true, false, format);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var valid = true;
        for (var index = 0; index < remaining.Length; index++)
        {
            var argument = remaining[index];
            if (argument == "--" || !argument.StartsWith("-", StringComparison.Ordinal))
            {
                valid = false;
                continue;
            }

            var equalsIndex = argument.IndexOf('=');
            var option = equalsIndex < 0 ? argument : argument[..equalsIndex];
            if (!ValueOptions.Contains(option) || !seen.Add(option))
            {
                valid = false;
                continue;
            }

            if (equalsIndex >= 0)
            {
                var value = argument[(equalsIndex + 1)..];
                if (value.Length == 0 || value.StartsWith("-", StringComparison.Ordinal))
                {
                    valid = false;
                }

                continue;
            }

            if (index + 1 >= remaining.Length || remaining[index + 1].StartsWith("-", StringComparison.Ordinal))
            {
                valid = false;
                continue;
            }

            index++;
        }

        return new DurableDoctorArgumentAdmissionResult(true, !valid, false, format);
    }

    /// <summary>Expands recognized joined value options because CliFx binds their name and value as separate tokens.</summary>
    /// <param name="arguments">The admitted doctor argv.</param>
    /// <returns>Arguments with only known <c>--name=value</c> forms expanded for CliFx binding.</returns>
    internal static string[] NormalizeJoinedValueOptions(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var routeLength = FindRouteLength(arguments);
        if (routeLength == 0)
        {
            return arguments;
        }

        var normalized = new List<string>(arguments.Length + ValueOptions.Count);
        normalized.AddRange(arguments.AsSpan(0, routeLength).ToArray());
        for (var index = routeLength; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            var equalsIndex = argument.IndexOf('=');
            if (equalsIndex > 0 && ValueOptions.Contains(argument[..equalsIndex]))
            {
                normalized.Add(argument[..equalsIndex]);
                normalized.Add(argument[(equalsIndex + 1)..]);
            }
            else
            {
                normalized.Add(argument);
            }
        }

        return normalized.ToArray();
    }

    private static int FindRouteLength(string[] arguments) =>
        arguments.Length >= 2
        && string.Equals(arguments[0], "durable", StringComparison.OrdinalIgnoreCase)
        && string.Equals(arguments[1], "doctor", StringComparison.OrdinalIgnoreCase)
            ? 2
            : arguments.Length >= 1
            && string.Equals(arguments[0], "durable doctor", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;

    private static string FindUniqueFormatHint(ReadOnlySpan<string> arguments)
    {
        var count = 0;
        var selectedFormat = "text";
        var valid = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument.StartsWith("--format=", StringComparison.Ordinal))
            {
                count++;
                var value = argument["--format=".Length..];
                valid = value is "text" or "json";
                selectedFormat = valid ? value : "text";
            }
            else if (string.Equals(argument, "--format", StringComparison.Ordinal))
            {
                count++;
                valid = index + 1 < arguments.Length
                    && arguments[index + 1] is "text" or "json";
                selectedFormat = valid ? arguments[index + 1] : "text";
                if (valid)
                {
                    index++;
                }
            }
        }

        return count == 1 && valid ? selectedFormat : "text";
    }
}

/// <summary>Safe raw-argv doctor route decision produced before CLI normalization and startup-context creation.</summary>
/// <param name="IsDoctor">Whether the argv targets the doctor command.</param>
/// <param name="IsMalformed">Whether doctor-specific admission rejected its token structure.</param>
/// <param name="IsHelp">Whether argv contains only one explicitly supported doctor help flag.</param>
/// <param name="Format">The unique valid text/json hint, falling back to text.</param>
internal sealed record DurableDoctorArgumentAdmissionResult(bool IsDoctor, bool IsMalformed, bool IsHelp, string Format)
{
    internal static DurableDoctorArgumentAdmissionResult NotDoctor { get; } = new(false, false, false, "text");
}
