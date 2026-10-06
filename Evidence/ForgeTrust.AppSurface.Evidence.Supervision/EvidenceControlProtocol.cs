using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Names the closed control operations; a parsed operation grants no execution authority.</summary>
internal enum EvidenceControlOperation
{
    /// <summary>Requests protected descriptor data.</summary>
    Ready,
    /// <summary>Requests admission closure and stopping.</summary>
    Stop,
    /// <summary>Requests an owned-exit observation.</summary>
    Wait,
    /// <summary>Requests completion acknowledgement.</summary>
    Exit,
    /// <summary>Supplies subject-command data for later root procedure validation.</summary>
    Run,
    /// <summary>Requests a bounded artifact inventory.</summary>
    Artifacts,
    /// <summary>Requests a bounded artifact byte range.</summary>
    Artifact,
    /// <summary>Identifies an application for later compiled registration selection.</summary>
    ApplicationStart,
    /// <summary>Identifies a resource for later authenticated readiness observation.</summary>
    ResourceWait,
}

/// <summary>Immutable decoded request data, without authentication, admission, or a lease.</summary>
internal abstract record EvidenceControlRequest
{
    /// <summary>Gets the operation fixed by the concrete request type.</summary>
    internal abstract EvidenceControlOperation Operation { get; }
}

/// <summary>Operation-only descriptor request.</summary>
internal sealed record EvidenceReadyControlRequest : EvidenceControlRequest
{
    /// <inheritdoc />
    internal override EvidenceControlOperation Operation => EvidenceControlOperation.Ready;
}

/// <summary>Operation-only stopping request.</summary>
internal sealed record EvidenceStopControlRequest : EvidenceControlRequest
{
    /// <inheritdoc />
    internal override EvidenceControlOperation Operation => EvidenceControlOperation.Stop;
}

/// <summary>Operation-only owned-exit request.</summary>
internal sealed record EvidenceWaitControlRequest : EvidenceControlRequest
{
    /// <inheritdoc />
    internal override EvidenceControlOperation Operation => EvidenceControlOperation.Wait;
}

/// <summary>Operation-only completion request.</summary>
internal sealed record EvidenceExitControlRequest : EvidenceControlRequest
{
    /// <inheritdoc />
    internal override EvidenceControlOperation Operation => EvidenceControlOperation.Exit;
}

/// <summary>Command data which the root must independently match to a compiled procedure before execution.</summary>
internal sealed record EvidenceRunControlRequest : EvidenceControlRequest
{
    /// <summary>Creates detached command data without validating or dispatching a procedure.</summary>
    /// <param name="executable">Decoded executable token.</param>
    /// <param name="arguments">Arguments copied into a read-only snapshot.</param>
    /// <param name="workingDirectory">Decoded working-directory token.</param>
    internal EvidenceRunControlRequest(string executable, IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        Executable = executable;
        Arguments = Array.AsReadOnly(arguments.ToArray());
        WorkingDirectory = workingDirectory;
    }

    /// <inheritdoc />
    internal override EvidenceControlOperation Operation => EvidenceControlOperation.Run;
    /// <summary>Gets executable data, not an executable allowlist decision.</summary>
    internal string Executable { get; }
    /// <summary>Gets the detached, immutable argument sequence.</summary>
    internal IReadOnlyList<string> Arguments { get; }
    /// <summary>Gets working-directory data, not a protected filesystem handle.</summary>
    internal string WorkingDirectory { get; }
}

/// <summary>Artifact inventory data; the relative root must still be authorized by the broker.</summary>
/// <param name="RelativeRoot">One normalized ASCII results-directory segment selected by the run procedure.</param>
internal sealed record EvidenceArtifactsControlRequest(string RelativeRoot) : EvidenceControlRequest
{
    /// <inheritdoc />
    internal override EvidenceControlOperation Operation => EvidenceControlOperation.Artifacts;
}

/// <summary>Artifact read data without an opened or authenticated path; the server selects the chunk size.</summary>
/// <param name="RelativeRoot">One normalized ASCII results-directory segment selected by the run procedure.</param>
/// <param name="Path">Normalized relative artifact path decoded from the exact relative_path wire field.</param>
/// <param name="Offset">Nonnegative signed 64-bit byte offset.</param>
internal sealed record EvidenceArtifactControlRequest(string RelativeRoot, string Path, long Offset)
    : EvidenceControlRequest
{
    /// <inheritdoc />
    internal override EvidenceControlOperation Operation => EvidenceControlOperation.Artifact;
}

/// <summary>Application identity data; matching metadata never enrolls or starts an application.</summary>
/// <param name="ApplicationId">Normalized ASCII application identifier.</param>
/// <param name="EntryDigest">Exactly 64 lowercase hexadecimal digest characters.</param>
internal sealed record EvidenceApplicationStartControlRequest(string ApplicationId, string EntryDigest)
    : EvidenceControlRequest
{
    /// <inheritdoc />
    internal override EvidenceControlOperation Operation => EvidenceControlOperation.ApplicationStart;
}

/// <summary>Resource identity data; parsing does not authenticate a lease or readiness result.</summary>
/// <param name="LeaseId">Exactly 32 lowercase hexadecimal identifier characters.</param>
/// <param name="ResourceId">Normalized ASCII resource identifier.</param>
internal sealed record EvidenceResourceWaitControlRequest(string LeaseId, string ResourceId) : EvidenceControlRequest
{
    /// <inheritdoc />
    internal override EvidenceControlOperation Operation => EvidenceControlOperation.ResourceWait;
}

/// <summary>Parses a single bounded JSON request into detached data with an exact closed schema.</summary>
/// <remarks>
/// Transport framing, peer authentication, compiled procedure selection and execution belong to the owning server.
/// Leading and trailing JSON whitespace is counted in the request bound and is not a framing guarantee.
/// Unknown, duplicate and case-alias fields reject. Run arguments contain zero through 128 strings; empty argument
/// strings are legal, but executable and working directory are nonempty. Every string token is at most 4096 UTF-8
/// bytes and contains no NUL. No caller-controlled diagnostic text or inner exception escapes rejection.
/// Artifact uses exactly op, relative_root, relative_path and offset; callers cannot select a chunk size.
/// </remarks>
internal static class EvidenceControlProtocol
{
    /// <summary>Maximum complete UTF-8 JSON request size, including whitespace and escaping.</summary>
    internal const int MaximumRequestBytes = 64 * 1024;
    /// <summary>Maximum argument count in one run request.</summary>
    internal const int MaximumArguments = 128;
    /// <summary>Maximum decoded UTF-8 byte length of an individual string token.</summary>
    internal const int MaximumTokenBytes = 4096;
    /// <summary>Fixed server artifact chunk limit; this is not a caller-supplied request field.</summary>
    internal const int MaximumArtifactChunkBytes = 128 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Parses detached data without executing, authorizing or issuing a capability.</summary>
    /// <param name="utf8Json">One UTF-8 JSON object of at most 64 KiB.</param>
    /// <returns>A concrete immutable request whose string and argument data outlive the input buffer.</returns>
    /// <exception cref="EvidenceAdmissionException">
    /// Fixed ASEVD420 rejection for malformed or excessive data.
    /// </exception>
    internal static EvidenceControlRequest Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length is 0 or > MaximumRequestBytes)
        {
            throw Rejected();
        }

        try
        {
            _ = StrictUtf8.GetCharCount(utf8Json.Span);
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            var fields = ReadFields(document.RootElement);
            var operation = Token(fields["op"]);
            switch (operation)
            {
                case "ready":
                    RequireFields(fields, "op");
                    return new EvidenceReadyControlRequest();
                case "stop":
                    RequireFields(fields, "op");
                    return new EvidenceStopControlRequest();
                case "wait":
                    RequireFields(fields, "op");
                    return new EvidenceWaitControlRequest();
                case "exit":
                    RequireFields(fields, "op");
                    return new EvidenceExitControlRequest();
                case "run":
                    RequireFields(fields, "op", "executable", "arguments", "working_directory");
                    return new EvidenceRunControlRequest(Token(fields["executable"]),
                        Arguments(fields["arguments"]), Token(fields["working_directory"]));
                case "artifacts":
                    RequireFields(fields, "op", "relative_root");
                    return new EvidenceArtifactsControlRequest(Name(fields["relative_root"]));
                case "artifact":
                    RequireFields(fields, "op", "relative_root", "relative_path", "offset");
                    var offset = fields["offset"];
                    if (offset.ValueKind != JsonValueKind.Number || !offset.TryGetInt64(out var start) || start < 0)
                    {
                        throw Rejected();
                    }
                    return new EvidenceArtifactControlRequest(Name(fields["relative_root"]),
                        RelativePath(fields["relative_path"]), start);
                case "application-start":
                    RequireFields(fields, "op", "application_id", "entry_digest");
                    return new EvidenceApplicationStartControlRequest(Name(fields["application_id"]),
                        Hex(fields["entry_digest"], 64));
                case "resource-wait":
                    RequireFields(fields, "op", "lease_id", "resource_id");
                    return new EvidenceResourceWaitControlRequest(Hex(fields["lease_id"], 32),
                        Name(fields["resource_id"]));
                default:
                    throw Rejected();
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException
                                     or KeyNotFoundException)
        {
            throw Rejected();
        }
    }

    private static Dictionary<string, JsonElement> ReadFields(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Rejected();
        }
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (fields.Count == 4 || !names.Add(property.Name))
            {
                throw Rejected();
            }
            fields.Add(property.Name, property.Value);
        }
        return fields;
    }

    private static void RequireFields(Dictionary<string, JsonElement> fields, params string[] expected)
    {
        if (fields.Count != expected.Length || expected.Any(name => !fields.ContainsKey(name)))
        {
            throw Rejected();
        }
    }

    private static string Token(JsonElement value, bool allowEmpty = false)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Rejected();
        }
        var text = value.GetString()!;
        if ((!allowEmpty && text.Length == 0) || text.Contains('\0')
            || StrictUtf8.GetByteCount(text) > MaximumTokenBytes)
        {
            throw Rejected();
        }
        return text;
    }

    private static string[] Arguments(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumArguments)
        {
            throw Rejected();
        }
        var result = new string[value.GetArrayLength()];
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            result[index++] = Token(item, allowEmpty: true);
        }
        return result;
    }

    private static string Name(JsonElement value)
    {
        var text = Token(value);
        if (text.Length > 96 || !char.IsAsciiLetterOrDigit(text[0])
            || text.Any(character => !char.IsAsciiLetterOrDigit(character)
                                     && character is not '.' and not '_' and not '-'))
        {
            throw Rejected();
        }
        return text;
    }

    private static string Hex(JsonElement value, int length)
    {
        var text = Token(value);
        if (text.Length != length || text.Any(character => character is not (>= '0' and <= '9')
                                                          and not (>= 'a' and <= 'f')))
        {
            throw Rejected();
        }
        return text;
    }

    private static string RelativePath(JsonElement value)
    {
        var text = Token(value);
        if (text.StartsWith('/') || text.Contains('\\')
            || text.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
        {
            throw Rejected();
        }
        return text;
    }

    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD420", "The control request is malformed or exceeds its fixed limits.");
}
