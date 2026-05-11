using System.Reflection;

namespace ForgeTrust.AppSurface.Config.Tests;

public class ConfigKeyRequiredAttributeTests
{
    [Fact]
    public void AttributeUsage_TargetsInheritedSingleClassUsage()
    {
        var usage = typeof(ConfigKeyRequiredAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Class, usage.ValidOn);
        Assert.False(usage.AllowMultiple);
        Assert.True(usage.Inherited);
    }
}
