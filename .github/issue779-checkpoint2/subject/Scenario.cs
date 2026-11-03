namespace NativePreparation.Subject;

/// <summary>Root-selected scenario value copied into the sealed subject fixture.</summary>
public static class Scenario
{
    /// <summary>One of normal, test-failure, cancel, quota, cleanup-failure, or ignored-cancel.</summary>
    public static string Kind => "normal";
}
