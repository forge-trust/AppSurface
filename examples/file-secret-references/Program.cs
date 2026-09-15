using FileSecretReferencesExample;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Console;

try
{
    await ConsoleApp<FileSecretReferencesModule>.RunAsync(args);
    return 0;
}
catch (ConfigurationCompositionException exception) when (FileSecretReferencesModule.IsExpectedFailure(args, exception))
{
    Console.WriteLine($"EXPECTED FAILURE: {exception.Failures[0].Code} at {exception.Failures[0].Path}");
    return 0;
}
