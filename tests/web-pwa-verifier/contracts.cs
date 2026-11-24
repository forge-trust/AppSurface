#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property ImplicitUsings=enable
#:property Nullable=enable
#:property PublishAot=false
#:project ../ForgeTrust.AppSurface.Testing/ForgeTrust.AppSurface.Testing.csproj
#:include ../../Cli/ForgeTrust.AppSurface.Cli/PwaVerificationV3Report.cs
#:include ../../scripts/web-pwa/PwaVerifier.cs
#:include ProtocolContracts.cs
#:include TestHost.cs
#:include ConfigContracts.cs
#:include PackageContracts.cs
#:include ProcessContracts.cs
#:include NativeConsole.cs

if (args.Length != 0 && args[0].StartsWith("--console-", StringComparison.Ordinal))
{
    return await NativeConsole.DispatchAsync(args);
}

var suites = new (string Name, Func<Task> Run)[]
{
    ("protocol", ProtocolContracts.RunAsync),
    ("config", ConfigContracts.RunAsync),
    ("package", PackageContracts.RunAsync),
    ("process", ProcessContracts.RunAsync)
};
if (args.Length > 1 || (args.Length == 1 && !suites.Any(suite => suite.Name == args[0])))
{
    Console.Error.WriteLine("Usage: dotnet run --file tests/web-pwa-verifier/contracts.cs -- [protocol|config|package|process]");
    return 2;
}
foreach (var suite in suites.Where(suite => args.Length == 0 || suite.Name == args[0]))
{
    Console.WriteLine($"Running PWA verifier {suite.Name} contracts.");
    try
    {
        await suite.Run();
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Failed PWA verifier {suite.Name} contracts.\n{error}");
        return 1;
    }
    Console.WriteLine($"Passed PWA verifier {suite.Name} contracts.");
}
return 0;
