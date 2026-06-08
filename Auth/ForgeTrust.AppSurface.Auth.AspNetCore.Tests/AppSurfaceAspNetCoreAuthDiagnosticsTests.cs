using System.Reflection;
using ForgeTrust.AppSurface.Auth.AspNetCore;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.Tests;

public sealed class AppSurfaceAspNetCoreAuthDiagnosticsTests
{
    [Fact]
    public void MissingService_WhenTypeHasNoFullName_UsesTypeName()
    {
        var metadata = AppSurfaceAspNetCoreAuthDiagnostics.MissingService(
            new TypeWithoutFullName(),
            "missing_test_service");

        Assert.Equal("missing_test_service", metadata[AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode]);
        Assert.Equal("TestService", metadata[AppSurfaceAspNetCoreAuthMetadataKeys.MissingService]);
        Assert.False(metadata.ContainsKey(AppSurfaceAspNetCoreAuthMetadataKeys.PolicyName));
    }

    private sealed class TypeWithoutFullName : TypeDelegator
    {
        public TypeWithoutFullName()
            : base(typeof(object))
        {
        }

        public override string? FullName => null;

        public override string Name => "TestService";
    }
}
