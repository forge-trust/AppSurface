using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Core;

var config = new PortConfig();

try
{
    ((IConfig)config).Init(new ExampleConfigManager(), new ExampleEnvironmentProvider(), AppSurfaceConfigKey.Parse("PortConfig"));
    Console.WriteLine("Configuration validation unexpectedly passed.");

    return 0;
}
catch (ConfigurationValidationException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine("Fix the configured value or relax the scalar rule on the config wrapper.");

    return 1;
}

[ConfigValueRange(1, 65535)]
internal sealed class PortConfig : ConfigStruct<int>
{
}

internal sealed class ExampleConfigManager : IConfigManager
{
    public T? GetValue<T>(string environment, string key) => GetValue<T>(environment, AppSurfaceConfigKey.Parse(key));

    public T? GetValue<T>(string environment, AppSurfaceConfigKey key)
    {
        if (key.Equals(AppSurfaceConfigKey.Parse("PortConfig")) && typeof(T) == typeof(int?))
        {
            return (T)(object)70000;
        }

        return default;
    }
}

internal sealed class ExampleEnvironmentProvider : IEnvironmentProvider
{
    public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => new Dictionary<string, string>(StringComparer.Ordinal);

    public string Environment => "Development";

    public bool IsDevelopment => true;

    public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
}
