using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public class EnvironmentConfigMappingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExactMappedChildAgreesAcrossDirectPatchAuditAndManager(bool scoped, bool conventionPresent)
    {
        var native = scoped ? "PRODUCTION__CUSTOM_NAME" : "CUSTOM_NAME";
        var environment = new EnvironmentFixture([(native, "mapped")]);
        if (conventionPresent) environment.Values["APP__NAME"] = "conventional";
        var provider = Provider(environment, "App:Name", "CUSTOM_NAME");
        var request = Request("App");
        var direct = provider.Resolve<string>(new("Production", AppSurfaceConfigKey.Parse("App:Name"), request.Scope));
        Assert.Equal("mapped", direct.Value);
        var original = new Settings { Name = "original" };
        var patch = ((IConfigValuePatcher)provider).Patch(request, original);
        Assert.Equal(ConfigPatchStatus.Applied, patch.Status);
        Assert.Equal(direct.Value, patch.Value!.Name);
        Assert.Equal("original", original.Name);
        var audit = ((IConfigDiagnosticPatcher)provider).TracePatch(request, original, typeof(Settings));
        Assert.True(audit.Patched);
        Assert.Equal(direct.Value, Assert.IsType<Settings>(audit.Value).Name);
        var source = Assert.Single(audit.Sources);
        Assert.Equal(native, source.EnvironmentVariableName);
        Assert.Equal("App:Name", source.ConfigPath);
        Assert.Equal(ConfigAuditSourceRole.Patch, source.Role);
        Assert.Empty(request.Scope.Notices);
        Assert.Equal(1, environment.Captures);
        var manager = new DefaultConfigManager(provider, [], NullLogger<DefaultConfigManager>.Instance);
        Assert.Equal("mapped", manager.GetValue<Settings>("Production", AppSurfaceConfigKey.Parse("App"))!.Name);
    }

    [Fact]
    public void MappedOnlyNestedChildIsDiscoveredAndRetainsGetterOnlyParentValues()
    {
        var environment = new EnvironmentFixture([("EXACT_PORT", "90"), ("APP__CHILD__PORT", "invalid-conventional")]);
        var provider = Provider(environment, "App:Child:Port", "EXACT_PORT");
        var original = new Settings();
        original.Child.Name = "retained";
        original.Child.Port = 10;
        var patch = ((IConfigValuePatcher)provider).Patch(Request("App"), original);
        Assert.Equal(ConfigPatchStatus.Applied, patch.Status);
        Assert.NotSame(original.Child, patch.Value!.Child);
        Assert.Equal(90, patch.Value.Child.Port);
        Assert.Equal("retained", patch.Value.Child.Name);
        Assert.Equal(10, original.Child.Port);
    }

    [Fact]
    public void MappedAggregateAndIndependentlyMappedChildUseOnlyTheirFinalNativePrefixes()
    {
        var environment = new EnvironmentFixture([("EXACT_CHILD__NAME", "name"), ("EXACT_PORT", "90"),
            ("EXACT_CHILD__PORT", "invalid-replaced"), ("APP__CHILD__NAME", "ignored")]);
        var provider = new EnvironmentConfigProvider(environment, Options.Create(new AppSurfaceEnvironmentConfigOptions()
            .MapKey("App:Child", "EXACT_CHILD").MapKey("App:Child:Port", "EXACT_PORT")));
        var patch = ((IConfigValuePatcher)provider).Patch<Settings>(Request("App"), null);
        Assert.Equal(ConfigPatchStatus.Applied, patch.Status);
        Assert.Equal("name", patch.Value!.Child.Name);
        Assert.Equal(90, patch.Value.Child.Port);
        var child = ((IConfigValuePatcher)provider).Patch<Child>(Request("App:Child"), null);
        Assert.Equal(ConfigPatchStatus.Applied, child.Status);
        Assert.Equal("name", child.Value!.Name);
        Assert.Equal(90, child.Value.Port);
    }

    [Fact]
    public void MappingAllowsOtherwiseUnrepresentableLiteralMember()
    {
        var provider = Provider(new EnvironmentFixture([("EXACT_UNICODE", "mapped")]), "App:É", "EXACT_UNICODE");
        Assert.Equal("mapped", provider.Resolve<string>(Request("App:É")).Value);
        var patch = ((IConfigValuePatcher)provider).Patch<UnicodeSettings>(Request("App"), null);
        Assert.Equal(ConfigPatchStatus.Applied, patch.Status);
        Assert.Equal("mapped", patch.Value!.É);
    }

    [Fact]
    public void MappedCollectionsShareDirectValuesAndBothLayerProvenance()
    {
        var environment = new EnvironmentFixture([("EXACT_ITEMS__1", "1"), ("PRODUCTION__EXACT_ITEMS__7", "7"), ("APP__ITEMS__0", "invalid")]);
        var provider = Provider(environment, "App:Items", "EXACT_ITEMS");
        var request = Request("App");
        Assert.Equal(new[] { 7 }, provider.Resolve<int[]>(new("Production", AppSurfaceConfigKey.Parse("App:Items"), request.Scope)).Value);
        var original = new Settings();
        original.Items.Add(42);
        var patch = ((IConfigValuePatcher)provider).Patch(request, original);
        Assert.Equal(ConfigPatchStatus.Applied, patch.Status);
        Assert.Equal(new[] { 7 }, patch.Value!.Items);
        Assert.Equal(new[] { 42 }, original.Items);
        var audit = ((IConfigDiagnosticPatcher)provider).TracePatch(request, original, typeof(Settings));
        Assert.Collection(audit.Sources,
            source => { Assert.Equal("EXACT_ITEMS__1", source.EnvironmentVariableName); Assert.Equal(ConfigAuditSourceRole.Base, source.Role); },
            source => { Assert.Equal("PRODUCTION__EXACT_ITEMS__7", source.EnvironmentVariableName); Assert.Equal(ConfigAuditSourceRole.Patch, source.Role); });
        Assert.Equal(1, environment.Captures);
    }

    [Theory]
    [InlineData("exact_port", "9", "config-key-environment-name-case")]
    [InlineData("EXACT_PORT", "invalid", "config-patch-failed")]
    public void InvalidMappedChildDiscardsValidSiblingAndAuditEvidence(string native, string value, string code)
    {
        var provider = Provider(new EnvironmentFixture([("APP__NAME", "new"), (native, value)]), "App:Child:Port", "EXACT_PORT");
        var original = new Settings { Name = "original" };
        var request = Request("App");
        var patch = ((IConfigValuePatcher)provider).Patch(request, original);
        Assert.Equal(ConfigPatchStatus.Terminal, patch.Status);
        Assert.Equal(code, patch.Diagnostic!.Code);
        Assert.Null(patch.Value);
        var audit = ((IConfigDiagnosticPatcher)provider).TracePatch(request, original, typeof(Settings));
        Assert.False(audit.Patched);
        Assert.Null(audit.Value);
        Assert.Empty(audit.Sources);
        Assert.Empty(audit.Facts);
        Assert.Empty(request.Scope.Notices);
        Assert.Equal("original", original.Name);
    }

    [Fact]
    public void UnrelatedOrAbsentMappingsDoNotInvokeGettersAndReplacedConventionsCannotRescue()
    {
        var environment = new EnvironmentFixture([("APP__NAME", "ignored"), ("UNRELATED", "value")]);
        var provider = new EnvironmentConfigProvider(environment, Options.Create(new AppSurfaceEnvironmentConfigOptions()
            .MapKey("App:Name", "MISSING").MapKey("AppArchive:Name", "UNRELATED")));
        Assert.Equal(ConfigPatchStatus.NotApplied, ((IConfigValuePatcher)provider).Patch<Settings>(Request("App"), null).Status);
        var replaced = new ThrowingGetter();
        Assert.Equal(ConfigPatchStatus.NotApplied, ((IConfigValuePatcher)provider).Patch(Request("App"), replaced).Status);
        Assert.Equal(0, replaced.Reads);
        var nested = Provider(new EnvironmentFixture([("APP__NAME__NESTED", "ignored")]), "App:Name:Nested", "MISSING");
        Assert.Equal(ConfigPatchStatus.NotApplied, ((IConfigValuePatcher)nested).Patch(Request("App"), replaced).Status);
        Assert.Equal(0, replaced.Reads);
        var empty = Provider(new EnvironmentFixture([]), "App:Name", "MISSING");
        var original = new ThrowingGetter();
        Assert.Equal(ConfigPatchStatus.NotApplied, ((IConfigValuePatcher)empty).Patch(Request("App"), original).Status);
        Assert.Equal(0, original.Reads);
    }

    [Fact]
    public void PresentUnknownMappedDescendantIsTerminal()
    {
        var provider = Provider(new EnvironmentFixture([("MAPPED_UNKNOWN", "value")]), "App:Unknown:Name", "MAPPED_UNKNOWN");
        Assert.Equal(ConfigPatchStatus.Terminal, ((IConfigValuePatcher)provider).Patch<Settings>(Request("App"), null).Status);
    }

    [Fact]
    public void PresentChildForUnconstructibleAggregateIsTerminalWithNestedPath()
    {
        var provider = new EnvironmentConfigProvider(new EnvironmentFixture([("APP__CHILD__VALUE", "supplied")]));
        var result = ((IConfigValuePatcher)provider).Patch(Request("App"), new UnconstructibleRoot());

        Assert.Equal(ConfigPatchStatus.Terminal, result.Status);
        Assert.Equal("config-patch-failed", result.Diagnostic!.Code);
        Assert.Contains("App:Child", result.Diagnostic.Cause);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PresentDescendantOfScalarCannotBeDiscardedByValidSibling(bool mapped, bool scalarPresent)
    {
        var environment = new EnvironmentFixture([("APP__NAME", "new"), (mapped ? "EXACT_UNKNOWN" : "APP__CHILD__PORT__UNKNOWN", "invalid")]);
        if (scalarPresent) environment.Values["APP__CHILD__PORT"] = "90";
        var provider = mapped ? Provider(environment, "App:Child:Port:Unknown", "EXACT_UNKNOWN") : new EnvironmentConfigProvider(environment);
        var original = new Settings { Name = "original" };
        var request = Request("App");
        var patch = ((IConfigValuePatcher)provider).Patch(request, original);
        Assert.Equal(ConfigPatchStatus.Terminal, patch.Status);
        Assert.Null(patch.Value);
        var audit = ((IConfigDiagnosticPatcher)provider).TracePatch(request, original, typeof(Settings));
        Assert.False(audit.Patched);
        Assert.Empty(audit.Sources);
        Assert.Empty(audit.Facts);
        Assert.Equal("original", original.Name);
        Assert.Equal(0, original.Child.Port);
    }

    [Fact]
    public void SuppliedNativeNamesCannotBypassRepresentabilityOrPoisonLegitimateClaims()
    {
        var claims = new EnvironmentNativeClaims([], new Dictionary<AppSurfaceConfigKey, string>(), 4096);
        foreach (var key in new[] { "Production:Payments", "A_:B", "A:_B", "É" })
            Assert.Equal("config-key-unrepresentable", claims.Claim(Request(key), ["PRODUCTION__PAYMENTS", "PAYMENTS"]));
        Assert.Null(claims.Claim(Request("Payments")));
        var mapped = AppSurfaceConfigKey.Parse("Production:Payments");
        var escaped = new EnvironmentNativeClaims([mapped], new Dictionary<AppSurfaceConfigKey, string> { [mapped] = "EXACT_PAYMENTS" }, 4096);
        Assert.Null(escaped.Claim(Request(mapped.Value), ["EXACT_PAYMENTS", "PRODUCTION__EXACT_PAYMENTS"]));
        Assert.Null(escaped.Claim(Request("Payments")));
    }

    [Fact]
    public void EnvironmentClaimIdentityIsNormalizedToEncodedNativePrefix()
    {
        var claims = new EnvironmentNativeClaims([], new Dictionary<AppSurfaceConfigKey, string>(), 4096);

        Assert.Null(claims.Claim(Request("Payments"), ["SHARED_NATIVE"]));
        Assert.Equal("config-key-unrepresentable", claims.Claim(new("production", AppSurfaceConfigKey.Parse("Other")), ["SHARED_NATIVE"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnvironmentPrefixChildCannotClaimScopedShorterIdentity(bool shorterFirst)
    {
        var provider = new EnvironmentConfigProvider(new EnvironmentFixture([("PRODUCTION__PAYMENTS", "90")]));
        if (shorterFirst) Assert.Equal(90, provider.Resolve<int>(Request("Payments")).Value);
        var request = Request("Production");
        var patch = ((IConfigValuePatcher)provider).Patch<ProductionSettings>(request, null);
        Assert.Equal(ConfigPatchStatus.Terminal, patch.Status);
        Assert.Equal("config-key-unrepresentable", patch.Diagnostic!.Code);
        var audit = ((IConfigDiagnosticPatcher)provider).TracePatch(request, null, typeof(ProductionSettings));
        Assert.False(audit.Patched);
        Assert.Empty(audit.Sources);
        Assert.Equal(90, provider.Resolve<int>(Request("Payments")).Value);
    }

    [Fact]
    public void ExactMappedEnvironmentPrefixChildCanPatchWithoutClaimingShorterIdentity()
    {
        var provider = Provider(new EnvironmentFixture([("EXACT_PAYMENTS", "90"), ("PRODUCTION__PAYMENTS", "80")]),
            "Production:Payments", "EXACT_PAYMENTS");
        var patch = ((IConfigValuePatcher)provider).Patch<ProductionSettings>(Request("Production"), null);
        Assert.Equal(ConfigPatchStatus.Applied, patch.Status);
        Assert.Equal(90, patch.Value!.Payments);
        Assert.Equal(90, provider.Resolve<int>(Request("Production:Payments")).Value);
        Assert.Equal(80, provider.Resolve<int>(Request("Payments")).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetterOnlyStructCannotPublishBoxedChildMutationOrRescueValidSibling(bool validSibling)
    {
        var environment = new EnvironmentFixture([("APP__STATE__PORT", "90")]);
        if (validSibling) environment.Values["APP__NAME"] = "new";
        var provider = new EnvironmentConfigProvider(environment);
        var original = new GetterOnlyStruct { Name = "original" };
        var request = Request("App");
        var patch = ((IConfigValuePatcher)provider).Patch(request, original);
        Assert.Equal(ConfigPatchStatus.Terminal, patch.Status);
        Assert.Equal("config-patch-failed", patch.Diagnostic!.Code);
        Assert.Null(patch.Value);
        var audit = ((IConfigDiagnosticPatcher)provider).TracePatch(request, original, typeof(GetterOnlyStruct));
        Assert.False(audit.Patched);
        Assert.Null(audit.Value);
        Assert.Empty(audit.Sources);
        Assert.Empty(audit.Facts);
        Assert.Equal(80, original.State.Port);
        Assert.Equal("original", original.Name);
        var manager = new DefaultConfigManager(provider, [], NullLogger<DefaultConfigManager>.Instance);
        Assert.Throws<ConfigurationResolutionException>(() => manager.GetValue<GetterOnlyStruct>("Production", AppSurfaceConfigKey.Parse("App")));
    }

    [Fact]
    public void WritableStructPublishesChildWritesWhileAbsentReadonlyStructRemainsUnchanged()
    {
        var provider = new EnvironmentConfigProvider(new EnvironmentFixture([("APP__STATE__PORT", "90")]));
        var original = new WritableStruct { State = new() { Port = 80 } };
        var patch = ((IConfigValuePatcher)provider).Patch(Request("App"), original);
        Assert.Equal(ConfigPatchStatus.Applied, patch.Status);
        Assert.Equal(90, patch.Value!.State.Port);
        Assert.Equal(80, original.State.Port);
        var siblingOnly = new EnvironmentConfigProvider(new EnvironmentFixture([("APP__NAME", "new")]));
        var siblingPatch = ((IConfigValuePatcher)siblingOnly).Patch(Request("App"), new GetterOnlyStruct());
        Assert.Equal(ConfigPatchStatus.Applied, siblingPatch.Status);
        Assert.Equal(80, siblingPatch.Value!.State.Port);
    }

    private static ConfigProviderRequest Request(string key) => new("Production", AppSurfaceConfigKey.Parse(key));
    private static EnvironmentConfigProvider Provider(EnvironmentFixture environment, string key, string suffix) =>
        new(environment, Options.Create(new AppSurfaceEnvironmentConfigOptions().MapKey(key, suffix)));

    private sealed class Settings
    {
        public string? Name { get; set; }
        public Child Child { get; } = new();
        public List<int> Items { get; } = [];
    }
    private sealed class Child { public string? Name { get; set; } public int Port { get; set; } }
    private sealed class UnicodeSettings { public string? É { get; set; } }
    private sealed class ProductionSettings { public int Payments { get; set; } }
    private sealed class UnconstructibleRoot { public UnconstructibleChild? Child { get; set; } }
    private sealed class UnconstructibleChild(string required) { public string Required { get; } = required; public string? Value { get; set; } }
    private sealed class ThrowingGetter { public int Reads; public string Name { get { Reads++; throw new InvalidOperationException("SENTINEL_SECRET"); } } }
    private sealed class GetterOnlyStruct { public string? Name { get; set; } public State State { get; } = new() { Port = 80 }; }
    private sealed class WritableStruct { public State State { get; set; } }
    public struct State { public int Port { get; set; } }
}
