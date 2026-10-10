using System.Reflection;
using FakeItEasy;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigTypeRegistrationTests
{
    [Fact]
    public void NullInputsFailWithTheirParameterBeforeAddingDescriptors()
    {
        Assert.Equal("services", Assert.Throws<ArgumentNullException>(
            () => AppSurfaceConfigServiceCollectionExtensions.AddAppSurfaceConfig<SelectedConfig>(null!)).ParamName);
        Assert.Equal("services", Assert.Throws<ArgumentNullException>(
            () => ConfigTypeRegistration.Register(null!, typeof(SelectedConfig))).ParamName);
        var services = new ServiceCollection();
        Assert.Equal("configType", Assert.Throws<ArgumentNullException>(
            () => ConfigTypeRegistration.Register(services, null!)).ParamName);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData(typeof(IConfig))]
    [InlineData(typeof(int))]
    [InlineData(typeof(string))]
    [InlineData(typeof(AbstractConfig))]
    [InlineData(typeof(GenericConfig<>))]
    public void UnsupportedTypeShapesDoNotMutateTheCollection(Type type)
    {
        IServiceCollection services = new ServiceCollection();
        var sentinel = ServiceDescriptor.Singleton(new object());
        services.Add(sentinel);
        var exception = Assert.Throws<ArgumentException>(() => ConfigTypeRegistration.Register(services, type));
        Assert.Equal("configType", exception.ParamName);
        Assert.Same(sentinel, Assert.Single(services));
    }

    [Fact]
    public void PublicConstructorShapeIsRequiredWithoutAnEnclosingVisibilityRule()
    {
        var services = new ServiceCollection();
        var type = new MetadataType(typeof(SelectedConfig)) { HideConstructors = true };
        Assert.Equal("configType", Assert.Throws<ArgumentException>(
            () => ConfigTypeRegistration.Register(services, type)).ParamName);
        Assert.Empty(services);

        Assert.Same(services, services.AddAppSurfaceConfig<SelectedConfig>());
        Assert.False(typeof(SelectedConfig).IsVisible);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(SelectedConfig));
    }

    [Fact]
    public void SelectedAssemblyCompatibilityPrecedesConstructorAndTraversalMetadata()
    {
        var services = new ServiceCollection();
        var type = new MetadataType(typeof(SelectedConfig)) { SelectedAssembly = new OldContractAssembly() };
        var exception = Assert.Throws<AppSurfacePackageCompatibilityException>(
            () => ConfigTypeRegistration.Register(services, type));
        Assert.Equal("config-package-version-mismatch", exception.Code);
        Assert.False(type.ConstructorsRead);
        Assert.False(type.TraversalRead);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("constructors")]
    [InlineData("traversal")]
    public void ExpectedMetadataFailurePrecedesHelperAdditions(string boundary)
    {
        var services = new ServiceCollection();
        var failure = new InvalidOperationException("metadata fixture");
        var type = new MetadataType(typeof(SelectedConfig))
        {
            ConstructorFailure = boundary == "constructors" ? failure : null,
            TraversalFailure = boundary == "traversal" ? failure : null
        };
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
            () => ConfigTypeRegistration.Register(services, type)));
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("explicit")]
    [InlineData("discovery")]
    [InlineData("explicit-discovery")]
    [InlineData("discovery-explicit")]
    public void RepeatedEntryPathsKeepExactDescriptorsAndAttributedDeclarationOnce(string order)
    {
        var services = new ServiceCollection();
        var context = new StartupContext([], new TestHostModule())
        {
            OverrideEntryPointAssembly = typeof(ConfigTypeRegistrationTests).Assembly
        };
        new AppSurfaceConfigModule().ConfigureServices(context, services);
        var before = SelectedConfig.Constructions;
        foreach (var entry in order.Split('-'))
        {
            if (entry == "explicit")
            {
                Assert.Same(services, services.AddAppSurfaceConfig<SelectedConfig>());
                services.AddAppSurfaceConfig<SelectedConfig>();
            }
            else
            {
                context.CustomRegistrations[0](services);
                context.CustomRegistrations[0](services);
            }
        }

        var snapshot = services.ToArray();
        services.AddAppSurfaceConfig<SelectedConfig>();
        if (order.Contains("discovery", StringComparison.Ordinal))
        {
            context.CustomRegistrations[0](services);
        }

        Assert.Equal(snapshot, services.ToArray());
        var descriptor = Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(SelectedConfig) && !descriptor.IsKeyedService);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.NotNull(descriptor.ImplementationFactory);
        Assert.Single(Attributed(services, typeof(SelectedConfig)));
        Assert.Equal(before, SelectedConfig.Constructions);
    }

    [Theory]
    [InlineData(ServiceLifetime.Scoped, 1)]
    [InlineData(ServiceLifetime.Transient, 1)]
    [InlineData(ServiceLifetime.Singleton, 2)]
    [InlineData(ServiceLifetime.Transient, 2)]
    [InlineData(ServiceLifetime.Singleton, 3)]
    public void CurrentConflictsAreSafeActionableAndLeaveTheCollectionUnchanged(ServiceLifetime lifetime, int count)
    {
        IServiceCollection services = new ServiceCollection();
        var invoked = 0;
        for (var i = 0; i < count; i++)
        {
            services.Add(new ServiceDescriptor(typeof(SelectedConfig), _ =>
            {
                invoked++;
                throw new InvalidOperationException("secret-value-sentinel");
            }, i == 0 ? lifetime : ServiceLifetime.Singleton));
        }

        var snapshot = services.ToArray();
        var exception = Assert.Throws<InvalidOperationException>(() => services.AddAppSurfaceConfig<SelectedConfig>());
        Assert.Equal(snapshot, services.ToArray());
        Assert.Equal(0, invoked);
        Assert.Contains("config-registration-conflict", exception.Message);
        Assert.Contains(typeof(SelectedConfig).FullName!, exception.Message);
        Assert.Contains($"Found {count}", exception.Message);
        Assert.Contains(lifetime.ToString(), exception.Message);
        Assert.Contains("Cause:", exception.Message);
        Assert.Contains("Fix: Keep exactly one unkeyed singleton", exception.Message);
        Assert.Contains(ConfigDiagnosticCatalog.Reference, exception.Message);
        Assert.DoesNotContain("secret-value-sentinel", exception.Message);
    }

    [Fact]
    public void LaterAppendsAreRejectedAndReplaceDoesNotRepairAnAlreadyDuplicatedCollection()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceConfig<SelectedConfig>();
        services.AddSingleton<SelectedConfig>();
        services.Replace(ServiceDescriptor.Singleton(new SelectedConfig()));
        var snapshot = services.ToArray();
        Assert.Throws<InvalidOperationException>(() => services.AddAppSurfaceConfig<SelectedConfig>());
        Assert.Equal(snapshot, services.ToArray());
        Assert.Equal(2, services.Count(descriptor => descriptor.ServiceType == typeof(SelectedConfig)));
        Assert.Single(Attributed(services, typeof(SelectedConfig)));
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("factory")]
    [InlineData("type")]
    public void KeyedWrappersCoexistWithoutAffectingUnkeyedOwnership(string kind)
    {
        var services = new ServiceCollection();
        var instance = new SelectedConfig();
        var invoked = 0;
        switch (kind)
        {
            case "instance":
                services.AddKeyedSingleton<SelectedConfig>("selected", instance);
                break;
            case "factory":
                services.AddKeyedSingleton<SelectedConfig>("selected", (_, _) =>
                {
                    invoked++;
                    return instance;
                });
                break;
            default:
                services.AddKeyedSingleton<SelectedConfig>("selected");
                break;
        }

        services.AddAppSurfaceConfig<SelectedConfig>();
        services.AddAppSurfaceConfig<SelectedConfig>();
        Assert.Equal(0, invoked);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(SelectedConfig) && descriptor.IsKeyedService);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(SelectedConfig) && !descriptor.IsKeyedService);
        services.AddSingleton(A.Fake<IConfigManager>());
        services.AddSingleton(A.Fake<IEnvironmentProvider>());
        using var provider = services.BuildServiceProvider();
        var unkeyed = provider.GetRequiredService<SelectedConfig>();
        var keyed = provider.GetRequiredKeyedService<SelectedConfig>("selected");
        Assert.NotSame(unkeyed, keyed);
        Assert.Equal(1, unkeyed.Initializations);
        Assert.Equal(0, keyed.Initializations);
        Assert.Equal(kind == "factory" ? 1 : 0, invoked);
    }

    [Fact]
    public void OpaqueAndKeyedRawMetadataAreNotProbedOrUsedAsUnkeyedDedupMarkers()
    {
        var services = new ServiceCollection();
        var invoked = 0;
        var raw = Raw(typeof(SelectedConfig));
        services.AddSingleton<ConfigAuditRawDeclaration>(_ =>
        {
            invoked++;
            return raw;
        });
        services.AddKeyedSingleton("raw", raw);
        services.AddSingleton(raw with { IsAttributeDeclaration = false, RawKey = "Explicit:Selected" });

        services.AddAppSurfaceConfig<SelectedConfig>();
        var snapshot = services.ToArray();
        services.AddAppSurfaceConfig<SelectedConfig>();
        Assert.Equal(snapshot, services.ToArray());
        Assert.Equal(0, invoked);
        Assert.Single(Attributed(services, typeof(SelectedConfig)));
        Assert.Equal(4, services.Count(descriptor => descriptor.ServiceType == typeof(ConfigAuditRawDeclaration)));
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ConfigDeclarationRegistry>();
        Assert.Single(registry.Entries);
        Assert.Equal(1, invoked);
        Assert.Equal(typeof(SelectedConfig), registry.GetForConfigType(typeof(SelectedConfig)).ConfigType);
    }

    [Fact]
    public void ExistingKnownAttributedInstanceIsPreservedAndManualPolicyAndSameKeyTypesSurvive()
    {
        var services = new ServiceCollection();
        var raw = Raw(typeof(SelectedConfig));
        services.AddSingleton(raw);
        services.AddConfigAuditKey<string>("Explicit:Selected", options =>
        {
            options.Sensitivity = ConfigAuditSensitivity.Sensitive;
            options.MaxCollectionElements = 2;
        });
        services.AddAppSurfaceConfig<SelectedConfig>();
        services.AddAppSurfaceConfig<SharedKeyConfig>();
        services.AddAppSurfaceConfig<SharedKeyConfig>();
        Assert.Same(raw, Assert.Single(Attributed(services, typeof(SelectedConfig))));
        Assert.Single(Attributed(services, typeof(SharedKeyConfig)));
        Assert.Equal(3, services.Count(descriptor => descriptor.ServiceType == typeof(ConfigAuditRawDeclaration)));
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ConfigDeclarationRegistry>();
        var entry = Assert.Single(registry.Entries);
        Assert.Equal(ConfigAuditSensitivity.Sensitive, entry.Options.Sensitivity);
        Assert.Equal(2, entry.Options.MaxCollectionElements);
        Assert.Same(entry, registry.GetForConfigType(typeof(SelectedConfig)));
        Assert.Same(entry, registry.GetForConfigType(typeof(SharedKeyConfig)));
    }

    [Fact]
    public void MetadataPreservesInheritedAndOverriddenTraversalAndValueTypeHierarchy()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceConfig<InheritedConfig>();
        services.AddAppSurfaceConfig<OverriddenConfig>();
        services.AddAppSurfaceConfig<StructConfig>();
        services.AddAppSurfaceConfig<SelectedConfig>();
        services.AddAppSurfaceConfig<ConventionalConfig>();

        var inherited = Assert.Single(Attributed(services, typeof(InheritedConfig)));
        Assert.Equal(typeof(List<string>), inherited.ValueType);
        Assert.True(inherited.Options.TraverseCollectionElements);
        Assert.Equal(3, inherited.Options.MaxCollectionElements);
        Assert.False(inherited.Options.DisplayDictionaryKeys);
        Assert.Equal(ConfigAuditSensitivity.Unknown, inherited.Options.Sensitivity);
        Assert.True(inherited.Options.AssignedOptions.HasFlag(ConfigAuditEntryOptionAssignments.CollectionTraversal));
        Assert.True(inherited.Options.AssignedOptions.HasFlag(ConfigAuditEntryOptionAssignments.DictionaryKeyCorrelationMode));
        var overridden = Assert.Single(Attributed(services, typeof(OverriddenConfig)));
        Assert.Equal(-1, overridden.Options.MaxCollectionDepth);
        Assert.Equal(-1, overridden.Options.MaxCollectionElements);
        Assert.Equal(0, overridden.Options.MaxReportNodes);
        Assert.Equal(typeof(int), Assert.Single(Attributed(services, typeof(StructConfig))).ValueType);
        Assert.Equal(typeof(object), Assert.Single(Attributed(services, typeof(SelectedConfig))).ValueType);
        var conventional = Assert.Single(Attributed(services, typeof(ConventionalConfig)));
        Assert.Equal(typeof(string), conventional.ValueType);
        Assert.False(conventional.Options.TraverseCollectionElements);
        using var provider = services.BuildServiceProvider();
        Assert.Equal("ConfigTypeRegistrationTests:ConventionalConfig", provider
            .GetRequiredService<ConfigDeclarationRegistry>().GetForConfigType(typeof(ConventionalConfig)).LogicalKey.Value);
    }

    [Fact]
    public void InfrastructureDescriptorsRemainStableWhileManualDeclarationsRemainAdditive()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceConfig<SelectedConfig>();
        var infrastructure = Infrastructure(services);
        services.AddAppSurfaceConfig<SelectedConfig>();
        services.AddAppSurfaceConfig<SharedKeyConfig>();
        services.AddConfigAuditKey<string>("Manual:First");
        services.AddConfigAuditKey<int>(AppSurfaceConfigKey.Parse("Manual:Second"));
        ConfigAuditServiceCollectionExtensions.EnsureDeclarationInfrastructure(services);
        Assert.Equal(infrastructure, Infrastructure(services));
        Assert.Single(services, descriptor => descriptor.ImplementationType == typeof(AppSurfaceConfigKeyOptionsValidator));
        Assert.Single(services, descriptor => descriptor.ImplementationType == typeof(ConfigDeclarationStartupValidator));
        Assert.Equal(4, services.Count(descriptor => descriptor.ServiceType == typeof(ConfigAuditRawDeclaration)));
    }

    [Fact]
    public void KeyedFrameworkValidatorsDoNotSuppressUnkeyedStartupBundles()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IValidateOptions<AppSurfaceConfigKeyOptions>, AppSurfaceConfigKeyOptionsValidator>("keys");
        services.AddKeyedSingleton<IValidateOptions<ConfigDeclarationStartupOptions>, ConfigDeclarationStartupValidator>("declarations");
        services.AddAppSurfaceConfig<SelectedConfig>();
        var snapshot = services.ToArray();
        services.AddAppSurfaceConfig<SelectedConfig>();
        Assert.Equal(snapshot, services.ToArray());
        Assert.Single(services, descriptor => !descriptor.IsKeyedService
            && descriptor.ImplementationType == typeof(AppSurfaceConfigKeyOptionsValidator));
        Assert.Single(services, descriptor => !descriptor.IsKeyedService
            && descriptor.ImplementationType == typeof(ConfigDeclarationStartupValidator));
        Assert.Equal(2, services.Count(descriptor => descriptor.IsKeyedService));
    }

    [Fact]
    public void CallerValidatorsParserAndRegistryRemainIndependentOfFrameworkInstallation()
    {
        IServiceCollection services = new ServiceCollection();
        var keyValidator = new CallerKeyValidator();
        var declarationValidator = new CallerDeclarationValidator();
        var parser = new CountingParser();
        var registry = new ConfigDeclarationRegistry([], [], parser);
        var parserDescriptor = ServiceDescriptor.Singleton<IConfigKeyInputParser>(parser);
        var registryDescriptor = ServiceDescriptor.Singleton(registry);
        services.AddSingleton<IValidateOptions<AppSurfaceConfigKeyOptions>>(keyValidator);
        services.AddSingleton<IValidateOptions<ConfigDeclarationStartupOptions>>(declarationValidator);
        services.Add(parserDescriptor);
        services.Add(registryDescriptor);

        services.AddAppSurfaceConfig<SelectedConfig>();
        var snapshot = services.ToArray();
        services.AddConfigAuditKey<string>("Manual:Value");
        Assert.Equal(snapshot, services.Where(descriptor => descriptor != services.Last()).ToArray());
        Assert.Same(parserDescriptor, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IConfigKeyInputParser)));
        Assert.Same(registryDescriptor, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ConfigDeclarationRegistry)));
        Assert.Equal(0, parser.Parses);
        using var provider = services.BuildServiceProvider();
        Assert.Same(parser, provider.GetRequiredService<IConfigKeyInputParser>());
        Assert.Same(registry, provider.GetRequiredService<ConfigDeclarationRegistry>());
        Assert.Equal(2, provider.GetServices<IValidateOptions<AppSurfaceConfigKeyOptions>>().Count());
        Assert.Equal(2, provider.GetServices<IValidateOptions<ConfigDeclarationStartupOptions>>().Count());
        _ = provider.GetRequiredService<IOptions<AppSurfaceConfigKeyOptions>>().Value;
        Assert.Equal(1, keyValidator.Calls);
    }

    [Fact]
    public async Task InvalidFinalKeyPolicyStillFailsBeforeHostedServiceAfterRepeatedSelection()
    {
        var reachedHostedService = false;
        using var host = Host.CreateDefaultBuilder().ConfigureServices(services =>
        {
            services.AddAppSurfaceConfig<SelectedConfig>();
            services.AddAppSurfaceConfig<SelectedConfig>();
            services.AddConfigAuditKey<string>("Manual:Value");
            services.PostConfigure<AppSurfaceConfigKeyOptions>(options => options.LegacyDotPathBehavior = (LegacyDotPathBehavior)99);
            services.AddSingleton<IHostedService>(new ProbeHostedService(() => reachedHostedService = true));
        }).Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.False(reachedHostedService);
    }

    [Fact]
    public void ClonedInfrastructureUsesEachProvidersFinalOptionsAndRegistry()
    {
        IServiceCollection original = new ServiceCollection();
        original.AddAppSurfaceConfig<ConventionalConfig>();
        IServiceCollection first = new ServiceCollection();
        IServiceCollection second = new ServiceCollection();
        foreach (var descriptor in original)
        {
            first.Add(descriptor);
            second.Add(descriptor);
        }

        first.PostConfigure<AppSurfaceConfigKeyOptions>(options => options.LegacyDotPathBehavior = LegacyDotPathBehavior.Strict);
        second.AddConfigAuditKey<string>("Additional.Value");
        using var firstProvider = first.BuildServiceProvider();
        using var secondProvider = second.BuildServiceProvider();
        var firstRegistry = firstProvider.GetRequiredService<ConfigDeclarationRegistry>();
        var secondRegistry = secondProvider.GetRequiredService<ConfigDeclarationRegistry>();
        Assert.NotSame(firstRegistry, secondRegistry);
        Assert.Single(firstRegistry.Entries);
        Assert.Equal(2, secondRegistry.Entries.Count);
        Assert.Contains(secondRegistry.Keys, key => key.Value == "Additional:Value");
        Assert.Equal(LegacyDotPathBehavior.Strict, firstProvider.GetRequiredService<IOptions<AppSurfaceConfigKeyOptions>>().Value.LegacyDotPathBehavior);
        Assert.Equal(LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic, secondProvider.GetRequiredService<IOptions<AppSurfaceConfigKeyOptions>>().Value.LegacyDotPathBehavior);
    }

    [Fact]
    public void KeyPolicyValidatorRetainsDefaultNameAndValidationBoundaries()
    {
        var validator = new AppSurfaceConfigKeyOptionsValidator();
        Assert.Throws<ArgumentNullException>(() => validator.Validate(Options.DefaultName, null!));
        Assert.True(validator.Validate("caller-name", new AppSurfaceConfigKeyOptions
        { LegacyDotPathBehavior = (LegacyDotPathBehavior)99 }).Skipped);
        Assert.True(validator.Validate(Options.DefaultName, new AppSurfaceConfigKeyOptions()).Succeeded);
        Assert.True(validator.Validate(Options.DefaultName, new AppSurfaceConfigKeyOptions
        { LegacyDotPathBehavior = (LegacyDotPathBehavior)99 }).Failed);
    }

    [Fact]
    public void InvalidLogicalGrammarIsDeferredToFinalRegistryWithoutRegistrationParsing()
    {
        var services = new ServiceCollection();
        var parser = new CountingParser();
        services.AddSingleton<IConfigKeyInputParser>(parser);
        var type = new MetadataType(typeof(SelectedConfig)) { KeyOverride = "Invalid::Key" };
        ConfigTypeRegistration.Register(services, type);
        Assert.Equal(0, parser.Parses);
        Assert.Single(Attributed(services, type));
        using var provider = services.BuildServiceProvider();
        Assert.Contains("config-key-invalid", Assert.Throws<FormatException>(
            () => provider.GetRequiredService<ConfigDeclarationRegistry>()).Message);
        Assert.Equal(1, parser.Parses);
    }

    [Fact]
    public void ExplicitCaseCollisionStillFailsAtTheFinalRegistry()
    {
        var services = new ServiceCollection();
        ConfigTypeRegistration.Register(services, new MetadataType(typeof(SelectedConfig)) { KeyOverride = "Case:Leaf" });
        ConfigTypeRegistration.Register(services, new MetadataType(typeof(SharedKeyConfig)) { KeyOverride = "case:leaf" });
        Assert.Equal(2, services.Count(descriptor => descriptor.ServiceType == typeof(ConfigAuditRawDeclaration)));
        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ConfigDeclarationRegistry>());
        Assert.Contains("config-key-collision", exception.Message);
    }

    [Theory]
    [InlineData(LegacyDotPathBehavior.Strict, false)]
    [InlineData(LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic, true)]
    public void ExplicitLegacyCollisionUsesLateHostPolicy(LegacyDotPathBehavior policy, bool collides)
    {
        var services = new ServiceCollection();
        ConfigTypeRegistration.Register(services, new MetadataType(typeof(SelectedConfig)) { KeyOverride = "Legacy.Leaf" });
        ConfigTypeRegistration.Register(services, new MetadataType(typeof(SharedKeyConfig)) { KeyOverride = "Legacy:Leaf" });
        services.PostConfigure<AppSurfaceConfigKeyOptions>(options => options.LegacyDotPathBehavior = policy);
        using var provider = services.BuildServiceProvider();
        if (collides)
        {
            var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ConfigDeclarationRegistry>());
            Assert.Contains("config-key-collision", exception.Message);
        }
        else
        {
            Assert.Equal(2, provider.GetRequiredService<ConfigDeclarationRegistry>().Entries.Count);
        }
    }

    [Fact]
    public void UnattributedTopLevelWrapperUsesItsNameAsARootKey()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceConfig<ExplicitConventionalRootConfig>();
        using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<ConfigDeclarationRegistry>().GetForConfigType(typeof(ExplicitConventionalRootConfig));
        Assert.Equal(nameof(ExplicitConventionalRootConfig), entry.LogicalKey.Value);
        Assert.Equal(typeof(string), entry.ValueType);
        Assert.Single(Attributed(services, typeof(ExplicitConventionalRootConfig)));
    }

    private static ConfigAuditRawDeclaration Raw(Type type) =>
        new(null, type, typeof(string), new ConfigAuditEntryOptions(), true);

    private static IEnumerable<ConfigAuditRawDeclaration> Attributed(IServiceCollection services, Type type) =>
        services.Where(descriptor => descriptor.ServiceType == typeof(ConfigAuditRawDeclaration) && !descriptor.IsKeyedService)
            .Select(descriptor => descriptor.ImplementationInstance).OfType<ConfigAuditRawDeclaration>()
            .Where(declaration => declaration.ConfigType == type && declaration.IsAttributeDeclaration);

    private static ServiceDescriptor[] Infrastructure(IServiceCollection services) =>
        services.Where(descriptor => descriptor.ServiceType != typeof(ConfigAuditRawDeclaration)
            && !typeof(IConfig).IsAssignableFrom(descriptor.ServiceType)).ToArray();

    [ConfigKey("Explicit:Selected", root: true)]
    private sealed class SelectedConfig : IConfig
    {
        // Used only as a registration tripwire; tests compare snapshots rather than an absolute shared count.
        internal static int Constructions;
        public int Initializations { get; private set; }
        public SelectedConfig() => Interlocked.Increment(ref Constructions);
        public void Init(IConfigManager configManager, IEnvironmentProvider environmentProvider, AppSurfaceConfigKey key) =>
            Initializations++;
    }

    [ConfigKey("Explicit:Selected", root: true)]
    private sealed class SharedKeyConfig : Config<string> { }
    private sealed class ConventionalConfig : Config<string> { }
    private abstract class AbstractConfig : Config<string> { }
    private sealed class GenericConfig<T> : Config<string> { }

    [ConfigAuditCollectionTraversal(MaxCollectionElements = 3, DisplayDictionaryKeys = false)]
    private abstract class TraversalBase : Config<List<string>> { }
    [ConfigKey("Explicit:Inherited", root: true)]
    private sealed class InheritedConfig : TraversalBase { }
    [ConfigKey("Explicit:Overridden", root: true)]
    [ConfigAuditCollectionTraversal(MaxCollectionDepth = -1, MaxCollectionElements = -1, MaxReportNodes = 0)]
    private sealed class OverriddenConfig : TraversalBase { }
    private abstract class IntermediateStruct<T> : ConfigStruct<T> where T : struct { }
    [ConfigKey("Explicit:Struct", root: true)]
    private sealed class StructConfig : IntermediateStruct<int> { }

    private sealed class MetadataType(Type type) : TypeDelegator(type)
    {
        public Assembly? SelectedAssembly { get; init; }
        public string? KeyOverride { get; init; }
        public bool HideConstructors { get; init; }
        public Exception? ConstructorFailure { get; init; }
        public Exception? TraversalFailure { get; init; }
        public bool ConstructorsRead { get; private set; }
        public bool TraversalRead { get; private set; }
        public override Assembly Assembly => SelectedAssembly ?? base.Assembly;
        public override ConstructorInfo[] GetConstructors(BindingFlags bindingAttr)
        {
            ConstructorsRead = true;
            if (ConstructorFailure is not null) { throw ConstructorFailure; }
            return HideConstructors ? [] : base.GetConstructors(bindingAttr);
        }
        public override object[] GetCustomAttributes(Type attributeType, bool inherit)
        {
            if (attributeType == typeof(ConfigKeyAttribute) && KeyOverride is not null)
            {
                return new Attribute[] { new ConfigKeyAttribute(KeyOverride, root: true) };
            }

            TraversalRead = true;
            if (TraversalFailure is not null) { throw TraversalFailure; }
            return base.GetCustomAttributes(attributeType, inherit);
        }
    }

    private sealed class OldContractAssembly : Assembly
    {
        public override bool IsDynamic => false;
        public override AssemblyName GetName() => new("OldSelectedWrapper");
        public override AssemblyName[] GetReferencedAssemblies() =>
            [new("ForgeTrust.AppSurface.Config") { Version = new Version(0, 1, 0, 0) }];
    }
    private sealed class CallerKeyValidator : IValidateOptions<AppSurfaceConfigKeyOptions>
    {
        public int Calls { get; private set; }
        public ValidateOptionsResult Validate(string? name, AppSurfaceConfigKeyOptions options)
        {
            Calls++;
            return ValidateOptionsResult.Success;
        }
    }
    private sealed class CallerDeclarationValidator : IValidateOptions<ConfigDeclarationStartupOptions>
    {
        public ValidateOptionsResult Validate(string? name, ConfigDeclarationStartupOptions options) => ValidateOptionsResult.Success;
    }
    private sealed class CountingParser : IConfigKeyInputParser
    {
        public int Parses { get; private set; }
        public AppSurfaceConfigKey Parse(string key)
        {
            Parses++;
            return AppSurfaceConfigKey.Parse(key);
        }
    }
    private sealed class ProbeHostedService(Action reached) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) { reached(); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public sealed class ExplicitConventionalRootConfig : Config<string> { }
