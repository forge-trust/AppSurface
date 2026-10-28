namespace EvidenceHost.CompletedIsolation;

/// <summary>The private owner's closed set of phases; none grants product admission.</summary>
internal enum OwnerPhase { Prepare, Replay, Close, Control }

/// <summary>Fixed namespace paths shared by the owner and its reviewed phase launcher.</summary>
internal static class OwnerPaths
{
    internal const string Validator = "/opt/appsurface/validator";
    internal const string Source = "/home/runner/work/AppSurface/AppSurface";
    internal const string Native = "/opt/appsurface/native-source";
    internal const string Stage = "/opt/appsurface/stage";
    internal const string Input = "/opt/appsurface/input";
    internal const string Handoff = "/opt/appsurface/handoff";
    internal const string Output = "/opt/appsurface/output";
    internal const string Report = "/opt/appsurface/report";
    internal const string Preparation = Handoff + "/preparation.json";
    internal const string ReplayResult = Handoff + "/replay.json";
    internal const string SourceCapture = Input + "/source-capture.json";
    internal const string ValidatorCapture = Input + "/validator-capture.json";
    internal const string Event = Input + "/event.json";
    internal const string Assembly = "/opt/appsurface/owner/EvidenceHost.CompletedIsolation.dll";
    internal const string Dotnet = "/usr/share/dotnet/dotnet";
}

/// <summary>Root-retained paths and the dedicated phase identity, created before any preparation.</summary>
internal sealed record OwnerLayout(string HostRoot, string RootFileSystem, uint Uid, uint Gid,
    string AssemblyPath, string EvidenceRoot)
{
    /// <summary>Maps one fixed namespace path into the owned filesystem without accepting traversal.</summary>
    internal string Host(string absoluteInside)
    {
        if (string.IsNullOrEmpty(absoluteInside) || !Path.IsPathFullyQualified(absoluteInside) || absoluteInside == "/"
            || absoluteInside.Split('/').Skip(1).Any(part => part is "" or "." or "..")
            || absoluteInside.Any(char.IsControl)) throw new InvalidDataException("isolation-path");
        var path = Path.GetFullPath(RootFileSystem + absoluteInside);
        if (!path.StartsWith(RootFileSystem + "/", StringComparison.Ordinal))
            throw new InvalidDataException("isolation-path");
        return path;
    }
}

/// <summary>Observed phase termination; an exit code alone does not prove descendant settlement.</summary>
internal sealed record PhaseResult(int ExitCode, bool Settled, bool TimedOut, bool OutputOverflow,
    string Unit, long ElapsedMilliseconds, string StandardOutputPath, string StandardErrorPath,
    string? FailureCategory = null, bool EscapeObserved = false);

/// <summary>One bounded stdin release frame. Token bytes must never be serialized to disk or diagnostics.</summary>
/// <remarks>Only prepare/close may receive a token/context. Replay receives neither. Control names are a closed enum-like list.</remarks>
internal sealed record PhaseRelease(string Phase, string? Token, string? Control,
    Dictionary<string, string>? Context);

/// <summary>Closed failure at the private owner boundary; never includes child output or credentials.</summary>
internal sealed class IsolationException(string category) : Exception(category);
