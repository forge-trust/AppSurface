namespace ForgeTrust.AppSurface.Observability.Tests;

internal sealed class TestEnvironmentReader(params (string Key, string Value)[] values) : IAppSurfaceEnvironmentReader
{
    private readonly Dictionary<string, string> _values = values.ToDictionary(static value => value.Key, static value => value.Value);

    public string? GetEnvironmentVariable(string variable)
    {
        return _values.GetValueOrDefault(variable);
    }
}
