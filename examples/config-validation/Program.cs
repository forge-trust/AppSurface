using ForgeTrust.Runnable.Config;
using ForgeTrust.Runnable.Core;

var config = new PortConfig();

try
{
    ((IConfig)config).Init(new ExampleConfigManager(), new ExampleEnvironmentProvider(), "PortConfig");
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
    public int Priority => 0;

    public string Name => nameof(ExampleConfigManager);

    public T? GetValue<T>(string environment, string key)
    {
        if (key == "PortConfig" && typeof(T) == typeof(int?))
        {
            return (T)(object)70000;
        }

        return default;
    }
}

internal sealed class ExampleEnvironmentProvider : IEnvironmentProvider
{
    public string Environment => "Development";

    public bool IsDevelopment => true;

    public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
}
