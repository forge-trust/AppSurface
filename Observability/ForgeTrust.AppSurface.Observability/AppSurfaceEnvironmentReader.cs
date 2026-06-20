namespace ForgeTrust.AppSurface.Observability;

internal interface IAppSurfaceEnvironmentReader
{
    string? GetEnvironmentVariable(string variable);
}

internal sealed class AppSurfaceEnvironmentReader : IAppSurfaceEnvironmentReader
{
    internal static readonly AppSurfaceEnvironmentReader Instance = new();

    public string? GetEnvironmentVariable(string variable) => Environment.GetEnvironmentVariable(variable);
}
