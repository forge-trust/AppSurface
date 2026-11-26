using System.Collections.Generic;
using System.Reflection;
using Xunit.Sdk;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

internal static class LinuxArm64TestPlatform
{
    internal const string SkipReason = "Requires a Linux ARM64 host.";

    internal static bool IsSupported
        => OperatingSystem.IsLinux()
           && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
               == System.Runtime.InteropServices.Architecture.Arm64;
}

public sealed class LinuxArm64FactAttribute : FactAttribute
{
    public LinuxArm64FactAttribute()
    {
        if (!LinuxArm64TestPlatform.IsSupported) Skip = LinuxArm64TestPlatform.SkipReason;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class LinuxArm64InlineDataAttribute : DataAttribute
{
    private readonly object[] _data;

    public LinuxArm64InlineDataAttribute(params object[] data)
    {
        _data = data;
        if (!LinuxArm64TestPlatform.IsSupported) Skip = LinuxArm64TestPlatform.SkipReason;
    }

    public override IEnumerable<object[]> GetData(MethodInfo testMethod)
        => [_data];
}
