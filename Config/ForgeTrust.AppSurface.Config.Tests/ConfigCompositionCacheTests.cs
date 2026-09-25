using System.Runtime.CompilerServices;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigCompositionCacheTests
{
    [Fact]
    public void CachedRootsKeepPlanIdentityWhileOverflowRootsRemainCollectible()
    {
        var compiler = CreateCompiler();
        var first = compiler.Compile("Production", "Service0", typeof(SecretOptions));
        for (var index = 1; index < ConfigCompositionPlanCompiler.MaximumCachedPlans; index++)
            _ = compiler.Compile("Production", $"Service{index}", typeof(SecretOptions));

        var overflow = CompileOverflow(compiler);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(overflow.IsAlive);
        Assert.Same(first, compiler.Compile("Production", "Service0", typeof(SecretOptions)));
        var again = compiler.Compile("Production", "Overflow", typeof(SecretOptions));
        Assert.Empty(again.Failures);
        Assert.Equal("Overflow:Access", Assert.Single(again.Slots).Path.Canonical);
        Assert.NotSame(again, compiler.Compile("Production", "Overflow", typeof(SecretOptions)));
        GC.KeepAlive(compiler);
    }

    [Fact]
    public void ConcurrentColdRootsCannotOverfillTheHostCache()
    {
        var compiler = CreateCompiler();
        var plans = new ConfigCompositionPlan[ConfigCompositionPlanCompiler.MaximumCachedPlans * 2];
        Parallel.For(0, plans.Length, index =>
            plans[index] = compiler.Compile("Production", $"Service{index}", typeof(SecretOptions)));

        var retained = plans.Count(plan => ReferenceEquals(plan,
            compiler.Compile(plan.Environment, plan.Key, plan.ValueType)));

        Assert.Equal(ConfigCompositionPlanCompiler.MaximumCachedPlans, retained);
        Assert.All(plans, plan => Assert.Empty(plan.Failures));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompileOverflow(ConfigCompositionPlanCompiler compiler) =>
        new(compiler.Compile("Production", "Overflow", typeof(SecretOptions)));

    private static ConfigCompositionPlanCompiler CreateCompiler() =>
        new(new ConfigCompositionJsonContract(new AppSurfaceConfigOptions()), new ConfigSecretProviderRegistry([]), [], []);

    private sealed class SecretOptions
    {
        public Secret<string> Access { get; set; } = new();
    }
}
