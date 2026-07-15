using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Durable.Examples;
using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class AppSurfaceDurableModuleTests
{
    [Fact]
    public void PassiveRegistrationProof_registers_only_host_neutral_registries()
    {
        PassiveRegistrationProof.Run();
    }

    [Fact]
    public void RegisterDependentModules_adds_flow_and_workers()
    {
        var builder = new ModuleDependencyBuilder();

        new AppSurfaceDurableModule().RegisterDependentModules(builder);

        Assert.Contains(builder.Modules, module => module is AppSurfaceFlowModule);
        Assert.Contains(builder.Modules, module => module is AppSurfaceWorkersModule);
    }

}
