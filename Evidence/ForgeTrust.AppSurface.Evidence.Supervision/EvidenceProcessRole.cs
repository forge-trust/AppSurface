using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed executable roles selected before ordinary CLI composition.</summary>
internal enum EvidenceProcessRole
{
    Worker,
    Supervisor,
    /// <summary>Private N12 child; unavailable in every ordinary build and never an admitted worker.</summary>
    OutputHolder,
}

/// <summary>Parsed process-role data; selection does not authenticate or grant execution authority.</summary>
/// <param name="Role">The exact reserved role.</param>
/// <param name="Path">Control socket or protected request file; empty only for help.</param>
/// <param name="Help">Whether to print fixed role usage without executing.</param>
internal sealed record EvidenceProcessRoleSelection(EvidenceProcessRole Role, string Path, bool Help);

/// <summary>Recognizes and parses reserved roles without configuration, discovery, or I/O.</summary>
internal static class EvidenceProcessRoleParser
{
    /// <summary>Identifies reserved role attempts, including case aliases that must be rejected.</summary>
    /// <param name="arguments">Process argument tokens.</param>
    /// <returns>True for worker/supervise role attempts; false for ordinary CLI commands.</returns>
    internal static bool IsReserved(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Count >= 2
            && string.Equals(arguments[0], "evidence", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(arguments[1], "worker", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arguments[1], "supervise", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arguments[1], LinuxN12Descendant.Role, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Parses one exact role and its single bounded path option or fixed help request.</summary>
    /// <param name="arguments">Exact evidence/role tokens followed by the role option.</param>
    /// <returns>Immutable data for the early executable dispatcher.</returns>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD402 for an invalid role grammar.</exception>
    /// <remarks>Path ownership, peer identity and platform checks belong to the selected runtime entry.</remarks>
    internal static EvidenceProcessRoleSelection Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count < 3 || arguments[0] != "evidence") throw InvalidRole();
        if (arguments[1] == LinuxN12Descendant.Role)
        {
            if (!EvidenceNativeQualification.DescendantEnabled || arguments.Count != 4 || arguments[2] != "--parent")
                throw InvalidRole();
            try { _ = LinuxN12Descendant.ParsePid(arguments[3]); }
            catch (EvidenceAdmissionException) { throw InvalidRole(); }
            return new(EvidenceProcessRole.OutputHolder, arguments[3], false);
        }
        var role = arguments[1] switch
        {
            "worker" => EvidenceProcessRole.Worker,
            "supervise" => EvidenceProcessRole.Supervisor,
            _ => throw InvalidRole(),
        };
        if (arguments.Count == 3 && arguments[2] is "--help" or "-h")
            return new(role, string.Empty, true);

        var option = role == EvidenceProcessRole.Worker ? "--control" : "--request";
        string path;
        if (arguments.Count == 4 && arguments[2] == option) path = arguments[3];
        else if (arguments.Count == 3 && arguments[2] is { } inline
            && inline.StartsWith(option + "=", StringComparison.Ordinal)) path = inline[(option.Length + 1)..];
        else throw InvalidRole();

        var maximumBytes = role == EvidenceProcessRole.Worker ? 100 : 4096;
        if (string.IsNullOrWhiteSpace(path) || path[0] != '/' || path.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(path) > maximumBytes
            || path.Split('/').Skip(1).Any(static part => part.Length == 0 || part is "." or ".."))
            throw InvalidRole();
        return new(role, path, false);
    }

    /// <summary>Creates the closed grammar diagnostic without supplied values or an inner exception.</summary>
    private static EvidenceAdmissionException InvalidRole() =>
        new("ASEVD402", "The protected process role or its arguments are invalid.");
}
