using System.Reflection;
using System.Reflection.Emit;

namespace ForgeTrust.AppSurface.Core.Tests;

public sealed class AppSurfacePackageCompatibilityTests
{
    [Theory]
    [InlineData("ForgeTrust.AppSurface.Core")]
    [InlineData("ForgeTrust.AppSurface.Config")]
    [InlineData("ForgeTrust.AppSurface.Config.LocalSecrets")]
    [InlineData("ForgeTrust.AppSurface.Config.GoogleSecretManager")]
    [InlineData("ForgeTrust.AppSurface.Config.Testing")]
    public void PreviousContractFailsBeforeTypeActivation(string name)
    {
        var reference = new AssemblyName { Name = name, Version = new Version(0, 1, 0, 0) };
        var error = Assert.Throws<AppSurfacePackageCompatibilityException>(() =>
            AppSurfacePackageCompatibility.ValidateReference(reference));
        Assert.Equal("config-package-version-mismatch", error.Code);
        Assert.Contains("Retryable: false", error.Message);
        Assert.Contains("Docs:", error.Message);
        reference.Version = AppSurfacePackageCompatibility.ConfigurationContractVersion;
        AppSurfacePackageCompatibility.ValidateReference(reference);
    }

    [Fact]
    public void OtherAssembliesAndCurrentCompiledReferencesPass()
    {
        AppSurfacePackageCompatibility.ValidateReference(new AssemblyName());
        AppSurfacePackageCompatibility.ValidateReference(new AssemblyName { Name = "External", Version = new Version(1, 0) });
        AppSurfacePackageCompatibility.ValidateAssemblies([typeof(AppSurfacePackageCompatibility).Assembly, typeof(AppSurfacePackageCompatibilityTests).Assembly]);
        var dynamicAssembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DynamicFixture"), AssemblyBuilderAccess.Run);
        AppSurfacePackageCompatibility.ValidateAssemblies([dynamicAssembly]);
        AppSurfacePackageCompatibility.ValidateAssemblies([]);
        Assert.Throws<ArgumentNullException>(() => AppSurfacePackageCompatibility.ValidateAssemblies(null!));
        Assert.Throws<ArgumentNullException>(() => AppSurfacePackageCompatibility.ValidateAssemblies([null!]));
    }

    [Theory]
    [InlineData("0.1.0.0")]
    [InlineData("0.3.0.0")]
    [InlineData(null)]
    public void MetadataGuard_RejectsIncompatibleReferencesWithoutTypeDiscovery(string? version)
    {
        var assembly = new MetadataAssembly(new AssemblyName("ExternalProvider"),
            [new AssemblyName
            {
                Name = "forgetrust.appsurface.config",
                Version = version is null ? null : Version.Parse(version)
            }]);

        var error = Assert.Throws<AppSurfacePackageCompatibilityException>(() =>
            AppSurfacePackageCompatibility.ValidateAssemblies([assembly]));

        Assert.Equal("config-package-version-mismatch", error.Code);
        Assert.Equal(1, assembly.ReferenceReads);
        Assert.DoesNotContain("ExternalProvider", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataGuard_RejectsOwnIdentityBeforeReadingReferences()
    {
        var assembly = new MetadataAssembly(
            new AssemblyName("FORGETRUST.APPSURFACE.CORE") { Version = new Version(0, 1, 0, 0) }, []);

        Assert.Throws<AppSurfacePackageCompatibilityException>(() =>
            AppSurfacePackageCompatibility.ValidateAssemblies([assembly]));

        Assert.Equal(0, assembly.ReferenceReads);
    }

    [Fact]
    public void MetadataGuard_AcceptsCurrentContractWithDifferentNameCasing()
    {
        var assembly = new MetadataAssembly(new AssemblyName("ExternalProvider"),
            [new AssemblyName("forgetrust.appsurface.config")
            {
                Version = AppSurfacePackageCompatibility.ConfigurationContractVersion
            }]);

        AppSurfacePackageCompatibility.ValidateAssemblies([assembly]);

        Assert.Equal(1, assembly.ReferenceReads);
    }

    [Fact]
    public void MetadataGuard_DoesNotRelabelUnrelatedMetadataReadFailures()
    {
        var expected = new FileLoadException("unrelated-metadata-test");
        var assembly = new MetadataAssembly(new AssemblyName("ExternalProvider"), [], expected);

        var actual = Assert.Throws<FileLoadException>(() =>
            AppSurfacePackageCompatibility.ValidateAssemblies([assembly]));

        Assert.Same(expected, actual);
    }

    // Assembly's public metadata API is the guard boundary; discovering types here is always a test failure.
    private sealed class MetadataAssembly : Assembly
    {
        private readonly AssemblyName _name;
        private readonly AssemblyName[] _references;
        private readonly Exception? _readFailure;

        public MetadataAssembly(AssemblyName name, AssemblyName[] references, Exception? readFailure = null)
        {
            _name = name;
            _references = references;
            _readFailure = readFailure;
        }

        public int ReferenceReads { get; private set; }

        public override bool IsDynamic => false;

        public override AssemblyName GetName() => _name;

        public override AssemblyName[] GetReferencedAssemblies()
        {
            ReferenceReads++;
            if (_readFailure is not null)
            {
                throw _readFailure;
            }

            return _references;
        }

        public override Type[] GetTypes() => throw new InvalidOperationException("Type discovery must not run during preflight.");
    }
}
