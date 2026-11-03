using System.Collections;

namespace EvidenceHost.CompletedIsolation;

/// <summary>Exact environment contract shared by the private managed launcher and its baseline control.</summary>
/// <remarks>The Python replay worker constructs its separate four-variable environment.</remarks>
internal static class LauncherEnvironment
{
    /// <summary>Rejects missing, additional, differently cased or wrongly valued launcher entries.</summary>
    internal static void Require(IDictionary actual)
    {
        if (actual.Count != 5
            || !Equals(actual["PATH"], "/usr/bin:/bin")
            || !Equals(actual["HOME"], "/nonexistent")
            || !Equals(actual["LANG"], "C.UTF-8")
            || !Equals(actual["LC_ALL"], "C")
            || !Equals(actual["DOTNET_EnableDiagnostics"], "0"))
            throw new IsolationException("launcher-environment");
        foreach (DictionaryEntry entry in actual)
        {
            if (entry.Key is not ("PATH" or "HOME" or "LANG" or "LC_ALL" or "DOTNET_EnableDiagnostics"))
                throw new IsolationException("launcher-environment");
        }
    }
}
