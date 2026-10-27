using ForgeTrust.AppSurface.Evidence.Coverage;

if (args.Length is not (3 or 4) || args[0] is not ("hold" or "try") || args[2] is not ("shared" or "exclusive"))
{
    Console.Error.WriteLine("Usage: <hold|try> <existing-output-directory> <shared|exclusive> [private-candidate-lock-domain]");
    return 64;
}

var exclusive = args[2] == "exclusive";
var candidateDomain = args.Length == 4 ? args[3] : null;
if (args[0] == "try")
{
    try
    {
        using var lease = CoverageArtifactOperationLease.Acquire(args[1], exclusive, windowsLockRoot: candidateDomain);
        Console.WriteLine("acquired");
        return 0;
    }
    catch (CoverageExecutionException)
    {
        Console.WriteLine("busy");
        return 2;
    }
}

using (CoverageArtifactOperationLease.Acquire(args[1], exclusive, windowsLockRoot: candidateDomain))
{
    Console.WriteLine("acquired");
    Console.Out.Flush();
    _ = await Console.In.ReadLineAsync();
}

return 0;
