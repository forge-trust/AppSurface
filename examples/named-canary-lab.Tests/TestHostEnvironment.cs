using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace NamedCanaryLab.Tests;

internal sealed class TestHostEnvironment : IHostEnvironment
{
    public TestHostEnvironment()
        : this(Environments.Development)
    {
    }

    public TestHostEnvironment(string environmentName)
    {
        EnvironmentName = environmentName;
    }

    public string EnvironmentName { get; set; }

    public string ApplicationName { get; set; } = "NamedCanaryLab.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
