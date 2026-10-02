using System.Text;

namespace ForgeTrust.AppSurface.Web.Tailwind.Internal;

/// <summary>
/// Classifies Tailwind standard-error output for host-specific logging without changing captured output.
/// </summary>
internal static class TailwindStderrClassifier
{
    /// <summary>
    /// Ignores complete ANSI SGR styling when recognizing informational lines. Other escape sequences and
    /// unrecognized messages retain error severity; callers keep the original line for logging and capture.
    /// </summary>
    /// <param name="line">The original standard-error line.</param>
    /// <returns>The logging severity of the line.</returns>
    public static TailwindOutputLevel Classify(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return TailwindOutputLevel.Debug;
        }

        var classificationLine = StripAnsiSgr(line);
        if (classificationLine.Contains('\u001b'))
        {
            return TailwindOutputLevel.Error;
        }

        if (string.IsNullOrWhiteSpace(classificationLine))
        {
            return TailwindOutputLevel.Debug;
        }

        if (classificationLine.StartsWith("≈ tailwindcss v", StringComparison.Ordinal) ||
            classificationLine.StartsWith("Done in ", StringComparison.Ordinal))
        {
            return TailwindOutputLevel.Information;
        }

        return TailwindOutputLevel.Error;
    }

    /// <summary>Removes only complete ESC [ numeric-parameter m SGR sequences from a classification copy.</summary>
    private static string StripAnsiSgr(string line)
    {
        var firstEscape = line.IndexOf('\u001b');
        if (firstEscape < 0)
        {
            return line;
        }

        var result = new StringBuilder(line.Length);
        var retainedStart = 0;
        for (var index = firstEscape; index < line.Length; index++)
        {
            if (line[index] != '\u001b' || index + 1 >= line.Length || line[index + 1] != '[')
            {
                continue;
            }

            var end = index + 2;
            while (end < line.Length && (char.IsAsciiDigit(line[end]) || line[end] is ';' or ':'))
            {
                end++;
            }

            if (end >= line.Length || line[end] != 'm')
            {
                continue;
            }

            result.Append(line.AsSpan(retainedStart, index - retainedStart));
            retainedStart = end + 1;
            index = end;
        }

        result.Append(line.AsSpan(retainedStart));
        return result.ToString();
    }
}
