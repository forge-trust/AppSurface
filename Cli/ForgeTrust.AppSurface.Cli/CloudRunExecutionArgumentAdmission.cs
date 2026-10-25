using System.Globalization;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Cli;

internal static partial class CloudRunExecutionArgumentAdmission
{
    internal const string InvalidInputCode = "ASDEPLOY170";

    internal static bool IsCanonicalExecution(string? value)
        => value is { Length: <= 512 } && CanonicalExecutionRegex().IsMatch(value)
            && (!char.IsDigit(value[9]) || long.TryParse(value.Split('/')[1], NumberStyles.None, CultureInfo.InvariantCulture, out _));

    internal static bool TryDuration(string? value, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var suffix = value.EndsWith("ms", StringComparison.Ordinal) ? "ms" : value[^1].ToString();
        var numeric = suffix == "ms" ? value[..^2] : value[..^1];
        if (!decimal.TryParse(numeric, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            return false;
        }

        var multiplier = suffix switch
        {
            "ms" => TimeSpan.TicksPerMillisecond,
            "s" => TimeSpan.TicksPerSecond,
            "m" => TimeSpan.TicksPerMinute,
            "h" => TimeSpan.TicksPerHour,
            "d" => TimeSpan.TicksPerDay,
            _ => 0L,
        };
        if (multiplier == 0 || number > 4_294_967_294m * TimeSpan.TicksPerMillisecond / multiplier) return false;
        var ticks = number * multiplier;
        if (ticks < 1 || ticks != decimal.Truncate(ticks)) return false;
        duration = TimeSpan.FromTicks(decimal.ToInt64(ticks));
        return true;
    }

    internal static bool TryValidate(string? bundleDirectory, string? logicalJobId, string? execution, string? timeout, string? pollInterval, out TimeSpan timeoutValue, out TimeSpan pollValue)
    {
        timeoutValue = default;
        pollValue = default;
        if (string.IsNullOrWhiteSpace(bundleDirectory) || logicalJobId is not { Length: > 0 and <= 63 }
            || !Regex.IsMatch(logicalJobId, "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*\\z", RegexOptions.CultureInvariant)
            || !IsCanonicalExecution(execution) || !TryDuration(timeout, out timeoutValue)
            || !TryDuration(pollInterval, out pollValue) || pollValue > timeoutValue)
        {
            return false;
        }

        return true;
    }

    internal static bool IsObserveExecution(string[] args)
        => args.Length >= 3 && string.Equals(args[0], "deployment", StringComparison.OrdinalIgnoreCase)
            && string.Equals(args[1], "gcp-cloud-run", StringComparison.OrdinalIgnoreCase)
            && string.Equals(args[2], "observe-execution", StringComparison.OrdinalIgnoreCase);

    internal static bool IsHelp(string[] args) => args.Skip(3).Any(static value => value is "--help" or "-h");

    internal static Dictionary<string, string>? Parse(string[] args)
    {
        if (!IsObserveExecution(args)) return null;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 3; index < args.Length; index++)
        {
            var pair = args[index].Split('=', 2);
            var name = pair[0];
            if (name == "--json")
            {
                if (pair.Length != 1 || !options.TryAdd(name, "true")) return null;
                continue;
            }
            if (name is not ("--bundle-dir" or "--job" or "--execution" or "--timeout" or "--poll-interval")) return null;
            var value = pair.Length == 2 ? pair[1] : ++index < args.Length ? args[index] : null;
            if (string.IsNullOrEmpty(value) || value.StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(name, value)) return null;
        }
        return TryValidate(options.GetValueOrDefault("--bundle-dir"), options.GetValueOrDefault("--job"), options.GetValueOrDefault("--execution"),
            options.GetValueOrDefault("--timeout", "60s"), options.GetValueOrDefault("--poll-interval", "2s"), out _, out _) ? options : null;
    }

    internal static bool IsMalformedRaw(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return IsObserveExecution(args) && !IsHelp(args) && Parse(args) is null;
    }

    internal static bool IsPathSafe(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && new DirectoryInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            if (File.Exists(current) && new FileInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        }

        return true;
    }

    [GeneratedRegex(@"^projects/(?:[a-z][a-z0-9-]{4,28}[a-z0-9]|[1-9][0-9]{0,18})/locations/[a-z]+(?:-[a-z0-9]+)+[0-9]/jobs/[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?/executions/[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalExecutionRegex();
}
