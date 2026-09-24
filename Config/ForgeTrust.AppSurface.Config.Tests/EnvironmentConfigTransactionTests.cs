using System.Collections;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public class EnvironmentConfigTransactionTests
{
    public static IEnumerable<object[]> AliasCombinations()
    {
        foreach (var translated in new[] { false, true })
            for (var mask = 0; mask < (translated ? 16 : 32); mask++) yield return [translated, mask];
    }

    [Theory]
    [MemberData(nameof(AliasCombinations))]
    public void EveryAliasStateUsesIdenticalDirectAndAuditRules(bool translated, int mask)
    {
        var names = translated
            ? new[] { "PRODUCTION__A__B", "PRODUCTION_A_B", "A__B", "A_B" }
            : new[] { "PRODUCTION__A__B", "PRODUCTION_A:B", "PRODUCTION__A:B", "A__B", "A:B" };
        var scopedCount = translated ? 2 : 3;
        var selected = names.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
        var environment = new EnvironmentFixture(selected.Select(name => (name, "same")));
        var provider = new EnvironmentConfigProvider(environment);
        var key = AppSurfaceConfigKey.Parse("A:B").WithInput(translated ? ConfigKeyInputOrigin.TranslatedDot : ConfigKeyInputOrigin.StrictString,
            translated ? "A.B" : "A:B");
        var request = new ConfigProviderRequest("Production", key);
        var direct = provider.Resolve<string>(request);
        var audit = ((IConfigDiagnosticProvider)provider).Resolve(request, typeof(string), ConfigAuditSourceRole.Override);
        var scoped = names.Take(scopedCount).Where(selected.Contains).ToArray();
        var unscoped = names.Skip(scopedCount).Where(selected.Contains).ToArray();
        var collision = scoped.Length > 1 || unscoped.Length > 1;
        Assert.Equal(collision ? ConfigProviderValueStatus.Terminal : selected.Length == 0 ? ConfigProviderValueStatus.Missing : ConfigProviderValueStatus.Found, direct.Status);
        Assert.Equal(collision ? ConfigAuditEntryState.Invalid : selected.Length == 0 ? ConfigAuditEntryState.Missing : ConfigAuditEntryState.Resolved, audit.State);
        Assert.Equal(1, environment.Captures);
        if (collision)
        {
            Assert.Equal("config-key-collision", direct.Diagnostic!.Code);
            Assert.Null(direct.Value);
            Assert.Empty(direct.Notices);
        }
        else if (selected.Length != 0)
        {
            var winner = scoped.FirstOrDefault() ?? unscoped.Single();
            Assert.Equal("same", direct.Value);
            Assert.Equal(winner != names[0] && winner != names[scopedCount], direct.Notices.Count > 0);
            Assert.Contains(audit.Sources, source => source.EnvironmentVariableName == winner && source.Role == ConfigAuditSourceRole.Override);
        }
    }

    [Theory]
    [InlineData("a__b", "config-key-environment-name-case")]
    [InlineData("production__a__b", "config-key-environment-name-case")]
    [InlineData("A__B|production__a__b", "config-key-collision")]
    [InlineData("a__b|PRODUCTION__A__B", "config-key-collision")]
    [InlineData("A__B|a__b", "config-key-collision")]
    public void CasingIsStrictInBothLayersAndAudit(string names, string code)
    {
        var environment = new EnvironmentFixture(names.Split('|').Select(name => (name, "value")));
        var provider = new EnvironmentConfigProvider(environment);
        var request = Request("A:B");
        Assert.Equal(code, provider.Resolve<string>(request).Diagnostic!.Code);
        Assert.Contains(((IConfigDiagnosticProvider)provider).Resolve(request, typeof(string), ConfigAuditSourceRole.Override).Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void OrderedScopesRetainBothExactSourcesAndWinningRoles()
    {
        var provider = new EnvironmentConfigProvider(new EnvironmentFixture([("A__B", "lower"), ("PRODUCTION__A__B", "higher")]));
        var resolution = ((IConfigDiagnosticProvider)provider).Resolve(Request("A:B"), typeof(string), ConfigAuditSourceRole.Override);
        Assert.Equal("higher", resolution.Value);
        Assert.Collection(resolution.Sources,
            source => { Assert.Equal("A__B", source.EnvironmentVariableName); Assert.Equal(ConfigAuditSourceRole.Base, source.Role); },
            source => { Assert.Equal("PRODUCTION__A__B", source.EnvironmentVariableName); Assert.Equal(ConfigAuditSourceRole.Override, source.Role); });
    }

    [Fact]
    public void PermanentBoundaryCounterexamplesCannotClaimTheSameFullName()
    {
        var provider = new EnvironmentConfigProvider(new EnvironmentFixture([("PRODUCTION__PAYMENTS__APIKEY", "marker")]));
        foreach (var key in new[] { "A_:B", "A:_B", "Production:Payments:ApiKey" })
            Assert.Equal("config-key-unrepresentable", provider.Resolve<string>(Request(key)).Diagnostic!.Code);
        Assert.Equal("marker", provider.Resolve<string>(Request("Payments:ApiKey")).Value);
        var mapped = new EnvironmentConfigProvider(new EnvironmentFixture([("PRODUCTION__ESCAPED", "mapped")]),
            Options.Create(new AppSurfaceEnvironmentConfigOptions().MapKey("Production:Payments:ApiKey", "ESCAPED")));
        Assert.Equal("mapped", mapped.Resolve<string>(Request("Production:Payments:ApiKey")).Value);
    }

    [Fact]
    public void GeneratedFullNamesAreInjectiveAcrossScopesAndEnvironmentPrefixes()
    {
        foreach (var environment in new[] { "Production", "Dev-Us", "Test.East" })
        {
            var claims = new EnvironmentNativeClaims([], new Dictionary<AppSurfaceConfigKey, string>(), 4096);
            var nativeOwners = new Dictionary<string, AppSurfaceConfigKey>(StringComparer.OrdinalIgnoreCase);
            var prefixes = new[] { "A", "a", "A_B", "A_", "_A", "A__B", EnvironmentConfigCodec.EncodeEnvironment(environment) };
            foreach (var first in prefixes)
                foreach (var second in prefixes)
                    foreach (var key in new[] { AppSurfaceConfigKey.FromSegments(first, second), AppSurfaceConfigKey.FromSegments(first) })
                    {
                        var request = new ConfigProviderRequest(environment, key);
                        if (claims.Claim(request) is not null) continue;
                        foreach (var name in EnvironmentConfigCodec.Candidates(request, new Dictionary<AppSurfaceConfigKey, string>()).Select(candidate => candidate.Name))
                        {
                            if (nativeOwners.TryGetValue(name, out var owner)) Assert.Equal(owner, key);
                            else nativeOwners.Add(name, key);
                        }
                    }
            Assert.NotEmpty(nativeOwners);
        }
    }

    [Fact]
    public async Task ConcurrentFirstClaimsPublishOneWholeIdentityAndRejectTheOther()
    {
        var claims = new EnvironmentNativeClaims([], new Dictionary<AppSurfaceConfigKey, string>(), 4096);
        var requests = new[] { Request("A_B"), new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("A:B").WithInput(ConfigKeyInputOrigin.TranslatedDot, "A.B")) };
        using var start = new ManualResetEventSlim();
        var calls = requests.Select(request => Task.Run(() => { start.Wait(); return claims.Claim(request); })).ToArray();
        start.Set();
        var results = await Task.WhenAll(calls);
        Assert.Single(results, result => result is null);
        Assert.Single(results, result => result == "config-key-unrepresentable");
        for (var index = 0; index < requests.Length; index++) Assert.Equal(results[index], claims.Claim(requests[index]));
        // A failed multi-name request must not reserve its unique canonical name.
        if (results[1] is not null) Assert.Null(claims.Claim(Request("A:B")));
    }

    [Fact]
    public void ClaimCapacityAndHostIsolationDoNotPoisonExistingKeys()
    {
        var claims = new EnvironmentNativeClaims([], new Dictionary<AppSurfaceConfigKey, string>(), 1);
        Assert.Null(claims.Claim(Request("A")));
        Assert.Equal("config-environment-claim-limit", claims.Claim(Request("B")));
        Assert.Null(claims.Claim(Request("a")));
        Assert.Null(new EnvironmentNativeClaims([], new Dictionary<AppSurfaceConfigKey, string>(), 1).Claim(Request("B")));
        var provider = new EnvironmentConfigProvider(new EnvironmentFixture([]), Options.Create(new AppSurfaceEnvironmentConfigOptions { MaxAdHocClaims = 1 }));
        Assert.Equal(ConfigProviderValueStatus.Missing, provider.Resolve<string>(Request("A")).Status);
        Assert.Equal("config-environment-claim-limit", provider.Resolve<string>(Request("B")).Diagnostic!.Code);
        Assert.Equal(ConfigProviderValueStatus.Missing, provider.Resolve<string>(Request("a")).Status);
    }

    [Theory]
    [InlineData("APP__PORT", "invalid")]
    [InlineData("APP__UNKNOWN", "valid-text")]
    [InlineData("APP__CHILD__PORT", "invalid")]
    [InlineData("APP__ITEMS__1", "invalid")]
    [InlineData("APP__ITEMS__01", "2")]
    [InlineData("APP__ITEMS__-1", "2")]
    [InlineData("APP__ITEMS__0__PORT", "2")]
    public void InvalidPresentChildrenDiscardEveryMutationAndAuditFact(string name, string value)
    {
        var provider = new EnvironmentConfigProvider(new EnvironmentFixture([("APP__NAME", "new"), (name, value)]));
        var original = new Aggregate { Name = "old", Port = 80 };
        original.Child.Port = 42;
        var request = Request("App");
        var result = ((IConfigValuePatcher)provider).Patch(request, original);
        var audit = ((IConfigDiagnosticPatcher)provider).TracePatch(request, original, typeof(Aggregate));
        Assert.Equal(ConfigPatchStatus.Terminal, result.Status);
        Assert.Null(result.Value);
        Assert.False(audit.Patched);
        Assert.Null(audit.Value);
        Assert.Empty(audit.Sources);
        Assert.Empty(audit.Facts);
        Assert.Empty(request.Scope.Notices);
        Assert.Equal("old", original.Name);
        Assert.Equal(80, original.Port);
        Assert.Equal(42, original.Child.Port);
    }

    [Fact]
    public void GetterOnlyAggregateCopiesInheritedOriginalValuesAndAllMutableReferences()
    {
        var provider = new EnvironmentConfigProvider(new EnvironmentFixture([("APP__CHILD__PORT", "9000")]));
        var original = new Aggregate { Name = "original", Port = 80 };
        original.Child.Name = "retained";
        original.Child.Port = 42;
        original.Items.Add(5);
        original.Children.Add(new Child { Name = "list-value", Port = 3 });
        original.FieldChild.Name = "field-value";
        original.FieldArray = ["field-array"];
        original.Dictionary["item"] = new Child { Name = "dictionary-value" };
        var result = ((IConfigValuePatcher)provider).Patch(Request("App"), original);
        Assert.Equal(ConfigPatchStatus.Applied, result.Status);
        var copy = result.Value!;
        Assert.NotSame(original, copy);
        Assert.NotSame(original.Child, copy.Child);
        Assert.Equal("retained", copy.Child.Name);
        Assert.Equal(9000, copy.Child.Port);
        Assert.Equal(42, original.Child.Port);
        Assert.Equal("original", copy.Name);
        Assert.Equal(80, copy.Port);
        Assert.NotSame(original.Items, copy.Items);
        Assert.Equal(original.Items, copy.Items);
        Assert.NotSame(original.Children, copy.Children);
        Assert.NotSame(original.Children[0], copy.Children[0]);
        Assert.Equal("list-value", copy.Children[0].Name);
        Assert.NotSame(original.FieldChild, copy.FieldChild);
        Assert.Equal("field-value", copy.FieldChild.Name);
        Assert.NotSame(original.FieldArray, copy.FieldArray);
        Assert.Equal(original.FieldArray, copy.FieldArray);
        Assert.NotSame(original.Dictionary, copy.Dictionary);
        Assert.NotSame(original.Dictionary["item"], copy.Dictionary["item"]);
    }

    [Fact]
    public void SnapshotIsSharedByDirectIndexedPatchAndAuditThenRefreshedByNextScope()
    {
        var environment = new EnvironmentFixture([("APP__CHILD__PORT", "10"), ("NUMBERS__7", "7")]);
        var provider = new EnvironmentConfigProvider(environment);
        var request = Request("App");
        Assert.Equal(ConfigProviderValueStatus.Missing, provider.Resolve<Aggregate>(request).Status);
        environment.Values["APP__CHILD__PORT"] = "20";
        Assert.Equal(10, ((IConfigValuePatcher)provider).Patch(request, new Aggregate()).Value!.Child.Port);
        Assert.Equal(10, ((Aggregate)((IConfigDiagnosticPatcher)provider).TracePatch(request, new Aggregate(), typeof(Aggregate)).Value!).Child.Port);
        var numbers = new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Numbers"), request.Scope);
        Assert.Equal(new[] { 7 }, provider.Resolve<int[]>(numbers).Value);
        Assert.Equal(1, environment.Captures);
        Assert.Equal(20, ((IConfigValuePatcher)provider).Patch(Request("App"), new Aggregate()).Value!.Child.Port);
        Assert.Equal(2, environment.Captures);
    }

    [Fact]
    public void CaptureFailureAndLimitsAreTerminalAcrossAllEnginesAndCached()
    {
        foreach (var oversized in new[] { false, true })
        {
            var environment = new EnvironmentFixture([("APP__NAME", "value")]) { ThrowCapture = !oversized };
            var provider = new EnvironmentConfigProvider(environment, resourceOptions: Options.Create(new ConfigResourceOptions { MaxEnvironmentBytes = 1 }));
            var request = Request("App");
            var expected = oversized ? "config-environment-snapshot-limit" : "config-environment-snapshot-failed";
            Assert.Equal(expected, provider.Resolve<Aggregate>(request).Diagnostic!.Code);
            Assert.Equal(expected, ((IConfigValuePatcher)provider).Patch(request, new Aggregate()).Diagnostic!.Code);
            Assert.Contains(((IConfigDiagnosticPatcher)provider).TracePatch(request, null, typeof(Aggregate)).Diagnostics, diagnostic => diagnostic.Code == expected);
            Assert.Contains(((IConfigDiagnosticProvider)provider).Resolve(request, typeof(Aggregate), ConfigAuditSourceRole.Base).Diagnostics, diagnostic => diagnostic.Code == expected);
            Assert.Equal(1, environment.Captures);
        }
    }

    [Fact]
    public void CancellationIsObservedBeforeCaptureAndAfterCapture()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var environment = new EnvironmentFixture([]);
        var provider = new EnvironmentConfigProvider(environment);
        var request = new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("App"), new(canceled.Token));
        Assert.Throws<OperationCanceledException>(() => provider.Resolve<string>(request));
        Assert.Throws<OperationCanceledException>(() => ((IConfigValuePatcher)provider).Patch(request, new Aggregate()));
        Assert.Equal(0, environment.Captures);
        using var during = new CancellationTokenSource();
        environment.AfterCapture = during.Cancel;
        Assert.Throws<OperationCanceledException>(() => provider.Resolve<string>(new("Production", AppSurfaceConfigKey.Parse("App"), new(during.Token))));
    }

    [Fact]
    public void DepthAndCyclesFailWithoutPublishingConstructorDefaults()
    {
        var environment = new EnvironmentFixture([("APP__CHILD__PORT", "10")]);
        var provider = new EnvironmentConfigProvider(environment, resourceOptions: Options.Create(new ConfigResourceOptions { MaxBindingDepth = 1 }));
        Assert.Equal(ConfigPatchStatus.Terminal, ((IConfigValuePatcher)provider).Patch<Aggregate>(Request("App"), null).Status);
        Assert.Equal(ConfigPatchStatus.Terminal, ((IConfigValuePatcher)provider).Patch(Request("App"), new Aggregate()).Status);
        var cyclic = new Cycle();
        Assert.Equal(ConfigPatchStatus.Terminal, ((IConfigValuePatcher)new EnvironmentConfigProvider(environment)).Patch(Request("App"), cyclic).Status);
    }

    [Fact]
    public void DiscoveryUsesOnlyForwardDeclarationsAndMappings()
    {
        var environment = new EnvironmentFixture([("MAPPED", "known"), ("UNRELATED_SECRET", "never-report")]);
        var provider = new EnvironmentConfigProvider(environment, Options.Create(new AppSurfaceEnvironmentConfigOptions().MapKey("Known", "MAPPED")));
        var entry = Assert.Single(provider.EnumerateKeys("Production"));
        Assert.Equal(AppSurfaceConfigKey.Parse("Known"), entry.Key);
        Assert.Equal("known", entry.RawValue);
        Assert.DoesNotContain(entry.Sources, source => source.EnvironmentVariableName == "UNRELATED_SECRET");
        Assert.Equal(1, environment.Captures);
    }

    [Fact]
    public void KnownReverseClaimsValidateCompleteCanonicalAndExplicitNames()
    {
        var a = AppSurfaceConfigKey.Parse("A");
        var mapped = AppSurfaceConfigKey.Parse("Mapped");
        var keys = new[] { a, mapped, AppSurfaceConfigKey.Parse("é"), AppSurfaceConfigKey.Parse("Production:A") };
        var mappings = new Dictionary<AppSurfaceConfigKey, string> { [mapped] = "Reserved" };
        var claims = new EnvironmentNativeClaims(keys, mappings, 4096);
        claims.Validate("Production");
        Assert.Equal("config-key-unrepresentable", claims.Claim(Request("Reserved")));
        Assert.Null(claims.Claim(Request("Other")));
        Assert.Null(claims.Claim(Request("A")));
        Assert.Null(claims.Claim(Request("Mapped")));
        var collision = new EnvironmentNativeClaims([a, mapped], new Dictionary<AppSurfaceConfigKey, string> { [mapped] = "a" }, 4096);
        Assert.Throws<OptionsValidationException>(() => collision.Validate("Production"));
        var explicitCollision = new EnvironmentNativeClaims([], new Dictionary<AppSurfaceConfigKey, string> { [a] = "X", [mapped] = "x" }, 4096);
        Assert.Throws<OptionsValidationException>(() => explicitCollision.Validate("Production"));
        // This conflicts only after composing one scoped and one unscoped full name.
        var scopedCollision = new EnvironmentNativeClaims([AppSurfaceConfigKey.Parse("Production:Reserved"), mapped], mappings, 4096);
        Assert.Throws<OptionsValidationException>(() => scopedCollision.Validate("Production"));
        foreach (var canonicalOrder in new[] { new[] { a, AppSurfaceConfigKey.Parse("Production:A") }, new[] { AppSurfaceConfigKey.Parse("Production:A"), a } })
        {
            var ownCanonical = new EnvironmentNativeClaims(canonicalOrder, new Dictionary<AppSurfaceConfigKey, string> { [a] = "A" }, 4096);
            Assert.Throws<OptionsValidationException>(() => ownCanonical.Validate("Production"));
        }
    }

    [Fact]
    public void SuccessfulAliasPatchesPublishChildNoticeOnlyAfterWholeTransaction()
    {
        foreach (var audit in new[] { false, true })
            foreach (var badSibling in new[] { false, true })
            {
                var environment = new EnvironmentFixture([("PRODUCTION_A_B__PORT", "10")]);
                if (badSibling) environment.Values["A__B__UNKNOWN"] = "bad";
                var provider = new EnvironmentConfigProvider(environment);
                var key = AppSurfaceConfigKey.Parse("A:B").WithInput(ConfigKeyInputOrigin.TranslatedDot, "A.B");
                var request = new ConfigProviderRequest("Production", key);
                if (audit)
                    Assert.Equal(!badSibling, ((IConfigDiagnosticPatcher)provider).TracePatch(request, null, typeof(Child)).Patched);
                else
                    Assert.Equal(badSibling ? ConfigPatchStatus.Terminal : ConfigPatchStatus.Applied, ((IConfigValuePatcher)provider).Patch<Child>(request, null).Status);
                if (badSibling) Assert.Empty(request.Scope.Notices);
                else
                {
                    var notice = Assert.Single(request.Scope.Notices);
                    Assert.Equal(AppSurfaceConfigKey.Parse("A:B:Port"), notice.Key);
                    Assert.Equal("PRODUCTION_A_B__PORT", notice.Notice.SafeSourceIdentifier);
                    Assert.Equal("config-key-legacy-provider-alias", notice.Notice.Code);
                }
            }
    }

    [Fact]
    public void IndexedLayersRejectCaseCollisionsAndPreserveWholeCollectionOverride()
    {
        var environment = new EnvironmentFixture([("NUMBERS__0", "1"), ("PRODUCTION__NUMBERS__0", "2"), ("PRODUCTION__NUMBERS__5", "5")]);
        var provider = new EnvironmentConfigProvider(environment);
        var request = Request("Numbers");
        Assert.Equal(new[] { 2, 5 }, provider.Resolve<int[]>(request).Value);
        var audit = ((IConfigDiagnosticProvider)provider).Resolve(request, typeof(int[]), ConfigAuditSourceRole.Override);
        Assert.Equal(3, audit.Sources.Count);
        Assert.Contains(audit.Sources, source => source.EnvironmentVariableName == "NUMBERS__0" && source.Role == ConfigAuditSourceRole.Base);
        environment.Values["numbers__0"] = "1";
        Assert.Equal("config-key-collision", provider.Resolve<int[]>(Request("Numbers")).Diagnostic!.Code);
        var ambiguous = new EnvironmentConfigProvider(new EnvironmentFixture([("APP__PORT", "5")]));
        Assert.Equal("config-key-collision", ((IConfigValuePatcher)ambiguous).Patch(Request("App"), new CaseAmbiguous()).Diagnostic!.Code);
    }

    [Fact]
    public void ThrowingPublicSetterCannotPublishOrMutateTheOriginal()
    {
        var provider = new EnvironmentConfigProvider(new EnvironmentFixture([("APP__PORT", "10")]));
        var original = new ThrowingSetter();
        Assert.Equal(ConfigPatchStatus.Terminal, ((IConfigValuePatcher)provider).Patch(Request("App"), original).Status);
        Assert.Equal(1, original.Port);
    }

    [Fact]
    public void SharedConstructorAggregateIsRejectedBeforeItsPublicSetterCanMutateSource()
    {
        var provider = new EnvironmentConfigProvider(new EnvironmentFixture([("APP__CHILD__PORT", "10")]));
        var original = new SharedConstructorChild();
        var initial = original.Child.Port;
        Assert.Equal(ConfigPatchStatus.Terminal, ((IConfigValuePatcher)provider).Patch(Request("App"), original).Status);
        Assert.Equal(initial, original.Child.Port);
    }

    private sealed class ThrowingSetter { public int Port { get => 1; set => throw new InvalidOperationException("SENTINEL_SECRET"); } }
    private sealed class SharedConstructorChild { private static readonly Child Shared = new(); public Child Child => Shared; }
    private sealed class CaseAmbiguous { public int Port { get; set; } public int port { get; set; } }

    [Fact]
    public void AlternateEnvironmentValidationAndCapacityAreBounded()
    {
        var key = AppSurfaceConfigKey.Parse("Mapped");
        var keys = new[] { key, AppSurfaceConfigKey.Parse("Other:Reserved") };
        var claims = new EnvironmentNativeClaims(keys, new Dictionary<AppSurfaceConfigKey, string> { [key] = "Reserved" }, 4096);
        claims.Validate("Production");
        Assert.Equal("config-key-unrepresentable", claims.Claim(new("Other", key)));
        var bounded = new EnvironmentNativeClaims([], new Dictionary<AppSurfaceConfigKey, string>(), 1);
        Assert.Null(bounded.Claim(Request("A")));
        Assert.Equal("config-environment-claim-limit", bounded.Claim(new("Other", AppSurfaceConfigKey.Parse("A"))));
    }

    [Fact]
    public void AuditInventorySharesItsCallersSnapshot()
    {
        var environment = new EnvironmentFixture([("MAPPED", "first")]);
        var provider = new EnvironmentConfigProvider(environment, Options.Create(new AppSurfaceEnvironmentConfigOptions().MapKey("Known", "MAPPED")));
        var request = Request("Known");
        Assert.Equal("first", provider.Resolve<string>(request).Value);
        environment.Values["MAPPED"] = "second";
        Assert.Equal("first", Assert.Single(provider.EnumerateKeys("Production", request.Scope)).RawValue);
        Assert.Equal(1, environment.Captures);
    }

    [Fact]
    public void BoundChildNamesParticipateInAdHocReverseClaims()
    {
        var environment = new EnvironmentFixture([("PRODUCTION_A_B__PORT", "10")]);
        var provider = new EnvironmentConfigProvider(environment);
        Assert.Equal("10", provider.Resolve<string>(Request("Production_A_B:Port")).Value);
        var request = new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("A:B").WithInput(ConfigKeyInputOrigin.TranslatedDot, "A.B"));
        Assert.Equal("config-key-unrepresentable", ((IConfigValuePatcher)provider).Patch<Child>(request, null).Diagnostic!.Code);
        Assert.Empty(request.Scope.Notices);
        var unicode = new EnvironmentConfigProvider(new EnvironmentFixture([("APP__É", "value")]));
        Assert.Equal("config-key-unrepresentable", ((IConfigValuePatcher)unicode).Patch<UnicodeChild>(Request("App"), null).Diagnostic!.Code);
    }

    private sealed class UnicodeChild { public string? É { get; set; } }

    [Fact]
    public void NoDescendantsAvoidCloningCyclesOrInvokingPublicGetters()
    {
        var environment = new EnvironmentFixture([("APPARCHIVE__NAME", "unrelated")]);
        var provider = new EnvironmentConfigProvider(environment);
        var cycle = new Cycle();
        var getter = new ObservedThrowingGetter();
        var request = Request("App");
        Assert.Equal(ConfigPatchStatus.NotApplied, ((IConfigValuePatcher)provider).Patch(request, cycle).Status);
        Assert.Equal(ConfigPatchStatus.NotApplied, ((IConfigValuePatcher)provider).Patch(request, getter).Status);
        Assert.False(((IConfigDiagnosticPatcher)provider).TracePatch(request, cycle, typeof(Cycle)).Patched);
        Assert.Empty(((IConfigDiagnosticPatcher)provider).TracePatch(request, getter, typeof(ObservedThrowingGetter)).Diagnostics);
        Assert.Equal(0, getter.ReadCount);
        Assert.Equal(1, environment.Captures);
        var failing = new EnvironmentConfigProvider(new EnvironmentFixture([]) { ThrowCapture = true });
        Assert.Equal("config-environment-snapshot-failed", ((IConfigValuePatcher)failing).Patch(Request("App"), getter).Diagnostic!.Code);
        Assert.Equal(0, getter.ReadCount);
    }

    private sealed class ObservedThrowingGetter
    {
        public int ReadCount;
        public string Name { get { ReadCount++; throw new InvalidOperationException("SENTINEL_SECRET"); } }
    }

    [Fact]
    public void RichAliasesUseBoundedScopeAndDoNotDuplicateInventoryDiagnostics()
    {
        var first = AppSurfaceConfigKey.Parse("Feature:One").WithInput(ConfigKeyInputOrigin.StrictString, "Feature:One");
        var second = AppSurfaceConfigKey.Parse("Feature:Two").WithInput(ConfigKeyInputOrigin.StrictString, "Feature:Two");
        var registry = new ConfigDeclarationRegistry([new(first, null, typeof(string)), new(second, null, typeof(string))], [],
            new ConfigKeyInputParser(Options.Create(new AppSurfaceConfigKeyOptions())));
        var provider = new EnvironmentConfigProvider(new EnvironmentFixture([("FEATURE:ONE", "one"), ("FEATURE:TWO", "two")]),
            resourceOptions: Options.Create(new ConfigResourceOptions { MaxNoticeIdentities = 1 }), declarationRegistry: registry);
        using var scope = new ConfigResolutionScope();
        var request = new ConfigProviderRequest("Production", first, scope);
        var diagnosticProvider = (IConfigDiagnosticProvider)provider;
        var firstResult = diagnosticProvider.Resolve(request, typeof(string), ConfigAuditSourceRole.Override);
        Assert.Equal("one", firstResult.Value);
        Assert.Empty(firstResult.Diagnostics);
        Assert.Single(scope.Notices);
        diagnosticProvider.Resolve(request, typeof(string), ConfigAuditSourceRole.Override);
        Assert.Single(scope.Notices);
        Assert.Null(scope.IncompleteAuditDiagnostic);
        var secondResult = diagnosticProvider.Resolve(new("Production", second, scope), typeof(string), ConfigAuditSourceRole.Override);
        Assert.Equal("two", secondResult.Value);
        Assert.Empty(secondResult.Diagnostics);
        Assert.Single(scope.Notices);
        Assert.Equal("config-audit-notice-limit", scope.IncompleteAuditDiagnostic!.Code);
        var inventory = provider.EnumerateKeys("Production", scope);
        Assert.All(inventory, item => Assert.Empty(item.Diagnostics));
        Assert.Single(scope.Notices);
    }

    private static ConfigProviderRequest Request(string key) => new("Production", AppSurfaceConfigKey.Parse(key));

    private class Parent { public Child Child { get; } = new(); }
    private sealed class Aggregate : Parent
    {
        public string? Name { get; set; }
        public int Port { get; set; }
        public List<int> Items { get; } = [];
        public List<Child> Children { get; } = [];
        public Child FieldChild = new();
        public string[] FieldArray = [];
        public Dictionary<string, Child> Dictionary { get; set; } = [];
    }
    private sealed class Child { public string? Name { get; set; } public int Port { get; set; } }
    private sealed class Cycle
    {
        public Cycle Child { get; set; }

        public Cycle()
        {
            Child = this;
        }
    }
}

internal sealed class EnvironmentFixture(IEnumerable<(string Name, string Value)> values) : IEnvironmentProvider
{
    internal Dictionary<string, string> Values { get; } = values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
    private int _captures;
    internal int Captures => _captures;
    internal bool ThrowCapture { get; init; }
    internal Action? AfterCapture { get; set; }
    public string Environment => "Production";
    public bool IsDevelopment => false;
    public string? GetEnvironmentVariable(string name, string? defaultValue = null) => throw new InvalidOperationException("Point reads must not occur.");
    public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables()
    {
        Interlocked.Increment(ref _captures);
        if (ThrowCapture) throw new InvalidOperationException("SENTINEL_SECRET");
        var snapshot = new Dictionary<string, string>(Values, StringComparer.Ordinal);
        AfterCapture?.Invoke();
        return snapshot;
    }
}
