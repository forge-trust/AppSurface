using ForgeTrust.AppSurface.Evidence.Coverage;

if (args.Length != 3 || args[0] is not ("hold" or "try") || args[2] is not ("shared" or "exclusive"))
{
    Console.Error.WriteLine("Usage: <hold|try> <existing-output-directory> <shared|exclusive>");
    return 64;
}

var exclusive = args[2] == "exclusive";
if (args[0] == "try")
{
    try
    {
        using var lease = CoverageArtifactOperationLease.Acquire(args[1], exclusive);
        Console.WriteLine("acquired");
        return 0;
    }
    catch (CoverageExecutionException)
    {
        Console.WriteLine("busy");
        return 2;
    }
}

using (CoverageArtifactOperationLease.Acquire(args[1], exclusive))
{
    Console.WriteLine("acquired");
    Console.Out.Flush();
    _ = await Console.In.ReadLineAsync();
}

return 0;
