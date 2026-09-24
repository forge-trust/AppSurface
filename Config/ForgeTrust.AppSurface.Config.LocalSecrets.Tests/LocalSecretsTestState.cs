using System.Runtime.CompilerServices;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

internal static class LocalSecretsTestState
{
    [ModuleInitializer]
    internal static void ConfigureIsolatedStateDirectory()
    {
        PlatformLocalSecretStatePaths.TestDirectory =
            Environment.GetEnvironmentVariable("APPSURFACE_LOCAL_SECRETS_TEST_STATE") ??
            Path.Combine(Path.GetTempPath(), $"appsurface-local-secrets-tests-{Guid.NewGuid():N}");
    }
}
