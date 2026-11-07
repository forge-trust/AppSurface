using System.Text;

namespace ForgeTrust.AppSurface.Evidence;

/// <summary>Decodes Git's UTF-8 patch path fields before gate and planner path policy is applied.</summary>
/// <remarks>The independent fan-in verifier parses its own patch headers.</remarks>
internal static class GitPatchPathHeader
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Reads a literal or Git C-quoted path, discarding only an optional tab-delimited suffix.</summary>
    internal static bool TryDecode(string value, out string path)
    {
        path = string.Empty;
        if (!value.StartsWith('"'))
        {
            var tab = value.IndexOf('\t');
            path = tab < 0 ? value.Trim() : value[..tab];
            return path.Length > 0 && !path.Contains('\0');
        }

        using var bytes = new MemoryStream();
        try
        {
            var start = 1;
            for (var i = start; i < value.Length; i++)
            {
                if (value[i] is not ('"' or '\\')) continue;
                if (i > start) bytes.Write(Utf8.GetBytes(value[start..i]));
                if (value[i] == '"')
                {
                    if (i + 1 < value.Length && value[i + 1] != '\t') return false;
                    path = Utf8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
                    return path.Length > 0 && !path.Contains('\0');
                }

                if (++i >= value.Length) return false;
                var c = value[i];
                var decoded = c switch
                {
                    'a' => 7,
                    'b' => 8,
                    't' => 9,
                    'n' => 10,
                    'v' => 11,
                    'f' => 12,
                    'r' => 13,
                    '\\' => '\\',
                    '"' => '"',
                    >= '0' and <= '7' => c - '0',
                    _ => -1
                };
                if (c is >= '0' and <= '7')
                    for (var digits = 1; digits < 3 && i + 1 < value.Length && value[i + 1] is >= '0' and <= '7'; digits++)
                        decoded = decoded * 8 + value[++i] - '0';
                if (decoded is < 0 or > byte.MaxValue) return false;
                bytes.WriteByte((byte)decoded);
                start = i + 1;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
        return false;
    }
}
