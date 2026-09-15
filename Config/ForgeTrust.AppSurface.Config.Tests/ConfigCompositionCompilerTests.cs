using System.Text.Json;
using System.Text.Json.Serialization;
using FakeItEasy;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigCompositionCompilerTests
{
    [Fact]
    public void ContainsSecrets_UsesScalarGateAndFindsSecretDescendants()
    {
        var contract = Contract();

        Assert.False(contract.ContainsSecrets(typeof(string)));
        Assert.False(contract.ContainsSecrets(typeof(int?)));
        Assert.True(contract.ContainsSecrets(typeof(Secret<string>)));
        Assert.True(contract.ContainsSecrets(typeof(FlatOptions)));
        Assert.True(contract.ContainsSecrets(typeof(RepeatedOptions)));
    }

    [Fact]
    public void GetShape_HandlesRepeatedSiblingsAndUsesSerializedNames()
    {
        var shape = Contract().GetShape(typeof(RepeatedOptions));

        Assert.Equal(new[] { "First:ApiKey", "Second:ApiKey" },
            shape.Secrets.Select(s => string.Join(':', s.Members)).ToArray());
        Assert.Contains(shape.Members, m => string.Join(':', m.Members) == "First:Plain");
        Assert.Contains(shape.Members, m => string.Join(':', m.Members) == "Second:Plain");
        Assert.Empty(shape.Failures);
    }

    [Fact]
    public void GetShape_StopsSelfAndMutualRecursionWithoutDroppingTheFirstSecret()
    {
        var contract = Contract();

        var self = contract.GetShape(typeof(SelfRecursiveOptions));
        var mutual = contract.GetShape(typeof(MutualA));

        Assert.Contains(self.Secrets, s => string.Join(':', s.Members) == "Token");
        Assert.Contains(self.Failures, f => string.Join(':', f.Members) == "Child"
            && f.Code == "secret-destination-type-unsupported");
        Assert.Contains(mutual.Secrets, s => string.Join(':', s.Members) == "Child:Token");
        Assert.Contains(mutual.Failures, f => f.Code == "secret-destination-type-unsupported");
    }

    [Fact]
    public void GetShape_RejectsCollectionsDictionariesSecretObjectAndConfigStruct()
    {
        var shape = Contract().GetShape(typeof(UnsupportedContainers));

        Assert.Contains(shape.Failures, f => string.Join(':', f.Members) == "Items" && f.Code == "secret-destination-type-unsupported");
        Assert.Contains(shape.Failures, f => string.Join(':', f.Members) == "Lookup" && f.Code == "secret-destination-type-unsupported");
        Assert.Contains(shape.Failures, f => string.Join(':', f.Members) == "Opaque" && f.Code == "secret-destination-type-unsupported");
        Assert.Contains(shape.Failures, f => string.Join(':', f.Members) == "Struct" && f.Code == "secret-destination-type-unsupported");
    }

    [Fact]
    public void GetShape_HonorsJsonPropertyNameRecordsInitFieldsIgnoreAndExtensionData()
    {
        var shape = Contract().GetShape(typeof(AnnotatedOptions));
        var paths = shape.Secrets.Select(s => string.Join(':', s.Members)).ToArray();

        Assert.Contains("api_key", paths);
        Assert.Contains("Record:RecordToken", paths);
        Assert.Contains("IncludedField", paths);
        Assert.DoesNotContain("Ignored", paths);
        Assert.Contains(shape.Failures, f => string.Join(':', f.Members) == "Extension" && f.Code == "secret-destination-type-unsupported");
        Assert.Contains(shape.Failures, f => string.Join(':', f.Members) == "Converted" && f.Code == "secret-destination-type-unsupported");
    }

    [Fact]
    public void GetShape_EnforcesDepthNodeAndSlotLimits()
    {
        var depth = new ConfigCompositionJsonContract(new AppSurfaceConfigOptions { MaxCompositionGraphDepth = 1 });
        var nodes = new ConfigCompositionJsonContract(new AppSurfaceConfigOptions { MaxCompositionGraphNodes = 2 });
        var slots = new ConfigCompositionJsonContract(new AppSurfaceConfigOptions { MaxSecretDestinationsPerRoot = 1 });

        Assert.Contains(depth.GetShape(typeof(NestedOptions)).Failures, f => f.Code == "secret-graph-limit-exceeded");
        Assert.Contains(nodes.GetShape(typeof(RepeatedOptions)).Failures, f => f.Code == "secret-graph-limit-exceeded");
        Assert.Contains(slots.GetShape(typeof(RepeatedOptions)).Failures, f => f.Code == "secret-graph-limit-exceeded");
    }

    [Fact]
    public void Compile_FileDescriptorsLayerAtomicallyAndReportsMalformedHigherValues()
    {
        using var fixture = FileFixture.Create(
            "{\"Service\":{\"ApiKey\":{\"key\":\"opaque-lower\",\"version\":\"7\"}}}",
            "{\"Service\":{\"ApiKey\":null}}",
            "Production");
        var provider = fixture.Provider;
        var secret = new FakeSecretProvider("provider-a");
        var compiler = Compiler(provider, [secret]);

        var plan = compiler.Compile("Production", "Service", typeof(FlatOptions));

        Assert.Contains(plan.Failures, f => f.Path == "Service:ApiKey" && f.Code == "secret-descriptor-invalid");
        Assert.Empty(plan.Slots.Single().Providers);
        Assert.Equal(0, secret.ValidateCalls);
        Assert.Equal(0, secret.ResolveCalls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("7")]
    [InlineData("[]")]
    [InlineData("{not-json")]
    public void Compile_HigherScalarArrayOrMalformedDeclarationFailsBeforeProviderCalls(string higher)
    {
        using var fixture = FileFixture.Create(
            "{\"Service\":{\"ApiKey\":{\"key\":\"opaque-lower\"}}}",
            $"{{\"Service\":{{\"ApiKey\":{higher}}}}}",
            "Production");
        var secret = new FakeSecretProvider("provider-a");
        var plan = Compiler(fixture.Provider, [secret]).Compile("Production", "Service", typeof(FlatOptions));

        Assert.NotEmpty(plan.Failures);
        Assert.Equal(0, secret.ValidateCalls);
        Assert.Equal(0, secret.ResolveCalls);
    }

    [Fact]
    public void Compile_RejectsRootAncestorDescendantAndPlainMappedSiblingClaims()
    {
        var provider = new FakeRawProvider();
        var source = new FakeClaims(
            new(ConfigSecretConfiguredClaimKind.RootMapping, "Service", "provider-a", "root-key", null),
            new(ConfigSecretConfiguredClaimKind.ExactMapping, "Service:ApiKey", "provider-a", "child-key", null),
            new(ConfigSecretConfiguredClaimKind.ExactMapping, "Service:Plain", "provider-a", "plain-key", null));
        var secret = new FakeSecretProvider("provider-a");
        var compiler = Compiler([provider], [secret], [source]);

        var plan = compiler.Compile("Production", "Service", typeof(FlatWithPlainOptions));

        Assert.Contains(plan.Failures, f => f.Code == "secret-claim-overlap");
        Assert.DoesNotContain(plan.Failures, f => f.Code == "config-composition-provider-unsupported");
        Assert.Equal(1, source.InspectCalls);
        AssertNoIo(provider, secret);
    }

    [Fact]
    public void Compile_ValidatesReferencesWithoutResolutionAndRetainsCompatibleProviders()
    {
        using var fixture = FileFixture.Create(
            "{\"Service\":{\"ApiKey\":{\"key\":\"opaque-key\",\"provider\":\"provider-a\"}}}",
            null,
            "Production");
        var a = new FakeSecretProvider("provider-a");
        var b = new FakeSecretProvider("provider-b");
        var plan = Compiler(fixture.Provider, [b, a]).Compile("Production", "Service", typeof(FlatOptions));

        Assert.Empty(plan.Failures);
        var slot = Assert.Single(plan.Slots);
        Assert.Equal("opaque-key", slot.Reference!.Key);
        Assert.Equal("provider-a", slot.ProviderConstraint);
        Assert.Equal("provider-a", Assert.Single(slot.Providers).Id);
        Assert.Equal(1, a.ValidateCalls);
        Assert.Equal(0, a.ResolveCalls);
        Assert.Equal(0, b.ValidateCalls);
    }

    [Fact]
    public void Compile_AcceptsADeclaredRawCompositionBaseWithoutCallingIt()
    {
        using var fixture = FileFixture.Create(
            "{\"Service\":{\"ApiKey\":{\"key\":\"opaque-key\"}}}",
            null,
            "Production");
        var raw = new FakeRawProvider();
        var secret = new FakeSecretProvider("provider-a");
        var plan = Compiler([fixture.Provider, raw], [secret]).Compile("Production", "Service", typeof(FlatOptions));

        Assert.Empty(plan.Failures);
        Assert.Equal(0, raw.ResolveCalls);
        Assert.Equal(0, raw.GetValueCalls);
    }

    [Fact]
    public void Compile_RejectsUnsupportedBaseProviderBeforeAnyCompositionIo()
    {
        using var fixture = FileFixture.Create("{\"Service\":{\"ApiKey\":{\"key\":\"key\"}}}", null, "Production");
        var provider = A.Fake<IConfigProvider>();
        A.CallTo(() => provider.Name).Returns("typed-only");
        A.CallTo(() => provider.Priority).Returns(5);
        var secret = new FakeSecretProvider("provider-a");
        var plan = Compiler([fixture.Provider, provider], [secret])
            .Compile("Production", "Service", typeof(FlatOptions));

        Assert.Contains(plan.Failures, f => f.Code == "config-composition-provider-unsupported");
        Assert.DoesNotContain(Fake.GetCalls(provider), c => c.Method.Name == nameof(IConfigProvider.GetValue));
        Assert.Equal(0, secret.ValidateCalls);
        Assert.Equal(0, secret.ResolveCalls);
    }

    [Fact]
    public void Compile_DetectsEnvironmentAliasesForDistinctSecretDestinations()
    {
        using var fixture = FileFixture.Create(
            """{"Service":{"A_B":{"key":"opaque-key-sentinel"},"A-B":{"key":"other-key-sentinel"}}}""",
            null, "Production");
        var raw = new FakeRawProvider();
        var secret = new FakeSecretProvider("provider-a");
        var compiler = Compiler([fixture.Provider, raw], [secret]);

        var plan = compiler.Compile("Production", "Service", typeof(AliasOptions));

        Assert.Contains(plan.Failures, f => f.Code == "secret-environment-alias-collision");
        Assert.All(plan.Failures, f =>
        {
            Assert.Contains(f.Path, new[] { "Service:A_B", "Service:A-B" });
            Assert.Equal(f.Path == "Service:A_B" ? "Service:A-B" : "Service:A_B", f.RelatedPath);
            Assert.Contains(f.EnvironmentVariableName, new[] { "PRODUCTION_SERVICE_A_B", "SERVICE_A_B" });
        });
        Assert.Equal(0, secret.ValidateCalls);
        AssertNoIo(raw, secret);
        Assert.DoesNotContain("key-sentinel", JsonSerializer.Serialize(plan.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigLogicalPath_OverlapsOnlyCompleteCaseInsensitiveSegments()
    {
        var root = ConfigLogicalPath.Parse("Service:Api");

        Assert.True(root.Overlaps(ConfigLogicalPath.Parse("service.api")));
        Assert.True(root.Overlaps(ConfigLogicalPath.Parse("SERVICE:API:Child")));
        Assert.False(root.Overlaps(ConfigLogicalPath.Parse("Service:ApiKey")));
        Assert.Equal("Service:Api", root.Canonical);
    }

    [Theory]
    [InlineData(typeof(object))]
    [InlineData(typeof(PlainRecursiveOptions))]
    [InlineData(typeof(IgnoredOnlyOptions))]
    [InlineData(typeof(List<string>))]
    [InlineData(typeof(Dictionary<string, int>))]
    public void ContainsSecrets_DoesNotOptInOrdinaryOrIgnoredGraphs(Type type)
    {
        var contract = Contract();

        Assert.False(contract.ContainsSecrets(type));
        Assert.Empty(contract.GetShape(type).Secrets);
        Assert.Empty(contract.GetShape(type).Failures);
    }

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(bool))]
    [InlineData(typeof(byte))]
    [InlineData(typeof(char))]
    [InlineData(typeof(short))]
    [InlineData(typeof(int))]
    [InlineData(typeof(int?))]
    [InlineData(typeof(long))]
    [InlineData(typeof(float))]
    [InlineData(typeof(double))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(DateTimeOffset))]
    [InlineData(typeof(TimeSpan))]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(Uri))]
    [InlineData(typeof(DayOfWeek))]
    public void GetShape_AcceptsEachSupportedScalarWithoutDiscoveringItsMembers(Type scalar)
    {
        var type = typeof(ScalarOptions<>).MakeGenericType(scalar);
        var contract = Contract();

        Assert.True(contract.ContainsSecrets(type));
        var shape = contract.GetShape(type);

        Assert.Empty(shape.Failures);
        var slot = Assert.Single(shape.Secrets);
        Assert.Equal(new[] { "Token" }, slot.Members);
        Assert.Equal(scalar, slot.InnerType);
        Assert.Equal(typeof(Secret<>).MakeGenericType(scalar), slot.WrapperType);
        Assert.Empty(shape.Members);
    }

    [Theory]
    [InlineData(typeof(Secret<object>))]
    [InlineData(typeof(Secret<FlatOptions>))]
    [InlineData(typeof(Secret<string[]>))]
    [InlineData(typeof(Secret<Dictionary<string, string>>))]
    [InlineData(typeof(Secret<Secret<string>>))]
    [InlineData(typeof(Secret<string>[]))]
    [InlineData(typeof(List<FlatOptions>))]
    [InlineData(typeof(Dictionary<string, FlatOptions>))]
    [InlineData(typeof(ConfigStruct<SecretStruct>))]
    [InlineData(typeof(ConvertedObjectOptions))]
    [InlineData(typeof(Secret<ConvertedEnum>))]
    public void Compile_RejectsUnsupportedPayloadAndContainerShapesBeforeAnyProviderCalls(Type member)
    {
        var type = typeof(MemberOptions<>).MakeGenericType(member);
        var contract = Contract();
        var raw = new FakeRawProvider();
        var secret = new FakeSecretProvider("provider-a");
        var claims = new FakeClaims();
        var compiler = new ConfigCompositionPlanCompiler(contract, new([secret]), [raw], [claims]);

        Assert.True(contract.ContainsSecrets(type));
        var plan = compiler.Compile("Production", "Service", type);

        Assert.Contains(plan.Failures, f => f.Path == "Service:Item"
            && f.Code == "secret-destination-type-unsupported");
        Assert.Equal(0, claims.InspectCalls);
        Assert.Equal(0, secret.ValidateCalls);
        AssertNoIo(raw, secret);
    }

    [Theory]
    [InlineData(typeof(SecretStruct))]
    [InlineData(typeof(SecretStruct?))]
    [InlineData(typeof(Secret<string>))]
    [InlineData(typeof(ConfigStruct<SecretStruct>))]
    public void GetShape_RejectsStructAndSecretRoots(Type root)
    {
        var contract = Contract();

        Assert.True(contract.ContainsSecrets(root));
        Assert.Contains(contract.GetShape(root).Failures, f => f.Members.Count == 0
            && f.Code == "secret-destination-type-unsupported");
    }

    [Fact]
    public void GetShape_AllowsRecordsConstructorBoundGettersInitAndIncludedFields()
    {
        var contract = Contract();
        var shape = contract.GetShape(typeof(BindableOptions));

        Assert.True(contract.ContainsSecrets(typeof(BindableOptions)));
        Assert.Empty(shape.Failures);
        Assert.Equal(new[] { "IncludedField", "Inherited", "Init", "ReadOnly:Token", "Record:RecordToken", "private_set" },
            shape.Secrets.Select(s => string.Join(':', s.Members)).Order(StringComparer.Ordinal));
        Assert.Same(shape, contract.GetShape(typeof(BindableOptions)));
    }

    [Fact]
    public void GetShape_IgnoresAlwaysIgnoredMembersButKeepsConditionalIgnoreMembers()
    {
        var contract = Contract();
        var shape = contract.GetShape(typeof(ConditionalIgnoreOptions));

        Assert.True(contract.ContainsSecrets(typeof(ConditionalIgnoreOptions)));
        Assert.Empty(shape.Failures);
        Assert.Equal(new[] { "Never", "WhenDefault", "WhenNull" },
            shape.Secrets.Select(s => string.Join(':', s.Members)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void GetShape_RejectsUnbindableReadOnlySecretMembersAndSubgraphs()
    {
        var contract = Contract();
        var shape = contract.GetShape(typeof(ReadOnlyOptions));

        Assert.True(contract.ContainsSecrets(typeof(ReadOnlyOptions)));
        Assert.Empty(shape.Secrets);
        Assert.Equal(new[] { "Child", "Token" },
            shape.Failures.Select(f => string.Join(':', f.Members)).Order(StringComparer.Ordinal));
        Assert.All(shape.Failures, f => Assert.Equal("secret-destination-type-unsupported", f.Code));
    }

    [Fact]
    public void GetShape_RejectsCustomConvertersAndPolymorphismThatHideSecrets()
    {
        var contract = Contract();
        var shape = contract.GetShape(typeof(ConvertedMembersOptions));

        Assert.True(contract.ContainsSecrets(typeof(ConvertedObjectOptions)));
        Assert.True(contract.ContainsSecrets(typeof(ConvertedMembersOptions)));
        Assert.Equal(new[] { "Container", "Polymorphic", "Token" },
            shape.Failures.Select(f => string.Join(':', f.Members)).Order(StringComparer.Ordinal));
        Assert.All(shape.Failures, f => Assert.Equal("secret-destination-type-unsupported", f.Code));
    }

    [Theory]
    [InlineData(typeof(DottedNameOptions))]
    [InlineData(typeof(ColonNameOptions))]
    [InlineData(typeof(CaseCollisionOptions))]
    [InlineData(typeof(EmptyNameOptions))]
    public void Compile_RejectsAmbiguousSerializedMemberNamesWithoutThrowing(Type type)
    {
        var raw = new FakeRawProvider();
        var secret = new FakeSecretProvider("provider-a");
        var plan = Compiler([raw], [secret]).Compile("Production", "Service", type);

        Assert.Contains(plan.Failures, f => f.Code == "secret-path-collision");
        Assert.Equal(0, secret.ValidateCalls);
        AssertNoIo(raw, secret);
    }

    [Theory]
    [InlineData("depth", 1, false)]
    [InlineData("depth", 2, true)]
    [InlineData("depth", 3, true)]
    [InlineData("nodes", 2, false)]
    [InlineData("nodes", 3, true)]
    [InlineData("nodes", 4, true)]
    [InlineData("slots", 1, false)]
    [InlineData("slots", 2, true)]
    [InlineData("slots", 3, true)]
    public void Compile_GraphLimitsAcceptTheBoundaryAndRejectOnePastIt(string limit, int maximum, bool succeeds)
    {
        var options = new AppSurfaceConfigOptions();
        var type = typeof(TwoSecretOptions);
        switch (limit)
        {
            case "depth":
                options.MaxCompositionGraphDepth = maximum;
                type = typeof(NestedOptions);
                break;
            case "nodes": options.MaxCompositionGraphNodes = maximum; break;
            case "slots": options.MaxSecretDestinationsPerRoot = maximum; break;
        }
        var raw = new FakeRawProvider();
        var secret = new FakeSecretProvider("provider-a");
        var compiler = new ConfigCompositionPlanCompiler(Contract(options), new([secret]), [raw], []);

        var plan = compiler.Compile("Production", "Service", type);

        if (succeeds) Assert.Empty(plan.Failures);
        else Assert.Contains(plan.Failures, f => f.Code == "secret-graph-limit-exceeded");
        Assert.Equal(0, secret.ValidateCalls);
        AssertNoIo(raw, secret);
    }

    [Theory]
    [InlineData("{}", "secret-descriptor-incomplete")]
    [InlineData("{\"key\":\" \"}", "secret-descriptor-incomplete")]
    [InlineData("{\"key\":null}", "secret-descriptor-invalid")]
    [InlineData("{\"key\":17}", "secret-descriptor-invalid")]
    [InlineData("{\"key\":\"k\",\"version\":false}", "secret-descriptor-invalid")]
    [InlineData("{\"key\":\"k\",\"version\":\"\"}", "secret-descriptor-invalid")]
    [InlineData("{\"key\":\"k\",\"provider\":null}", "secret-descriptor-invalid")]
    [InlineData("{\"key\":\"k\",\"provider\":\" \"}", "secret-descriptor-invalid")]
    [InlineData("{\"key\":\"k\",\"enabled\":\"false\"}", "secret-descriptor-invalid")]
    [InlineData("{\"key\":\"k\",\"enabled\":null}", "secret-descriptor-invalid")]
    [InlineData("{\"key\":\"k\",\"unknown\":true}", "secret-descriptor-invalid")]
    [InlineData("{\"key\":\"k\",\"KEY\":\"other\"}", "config-composition-file-invalid")]
    public void Compile_ValidatesDescriptorSyntaxBeforeProviderValidation(string descriptor, string code)
    {
        using var fixture = FileFixture.Create(Wrap(descriptor), null, "Production");
        var raw = new FakeRawProvider();
        var secret = new FakeSecretProvider("provider-a");

        var plan = Compiler([fixture.Provider, raw], [secret]).Compile("Production", "Service", typeof(FlatOptions));

        Assert.Contains(plan.Failures, f => f.Code == code);
        Assert.Equal(0, secret.ValidateCalls);
        AssertNoIo(raw, secret);
    }

    [Fact]
    public void Compile_DescriptorReplacementResetsOmittedOptionalFieldsAtomically()
    {
        using var fixture = FileFixture.Create(
            Wrap("""{"key":"Lower.Key:Opaque","version":"V7","provider":"provider-a","enabled":false}"""),
            Wrap("""{"key":"Upper.Key:Opaque"}"""), "Production");
        var a = new FakeSecretProvider("provider-a");
        var b = new FakeSecretProvider("provider-b");
        var raw = new FakeRawProvider();

        var plan = Compiler([fixture.Provider, raw], [b, a]).Compile("Production", "Service", typeof(FlatOptions));

        Assert.Empty(plan.Failures);
        var slot = Assert.Single(plan.Slots);
        Assert.Equal("Upper.Key:Opaque", slot.Reference!.Key);
        Assert.Null(slot.Reference.Version);
        Assert.Null(slot.ProviderConstraint);
        Assert.True(slot.Enabled);
        Assert.Equal(new[] { "provider-a", "provider-b" }, slot.Providers.Select(p => p.Id));
        Assert.Equal(new[] { "Lower.Key:Opaque", "Upper.Key:Opaque" }, a.Validated.Select(r => r.Key));
        Assert.Equal("Upper.Key:Opaque", Assert.Single(b.Validated).Key);
        Assert.EndsWith("appsettings.Production.json", slot.Source!.FilePath, StringComparison.Ordinal);
        Assert.NotNull(slot.Source.Location);
        AssertNoIo(raw, a, b);
    }

    [Fact]
    public void Compile_OmittedHigherDestinationPreservesCompleteLowerDeclaration()
    {
        using var fixture = FileFixture.Create(
            Wrap("""{"key":"LowerKey","version":"V7","provider":"provider-a","enabled":false}"""),
            """{"Service":{"Plain":"changed"}}""", "Production");
        var secret = new FakeSecretProvider("provider-a");

        var plan = Compiler(fixture.Provider, [secret]).Compile("Production", "Service", typeof(FlatWithPlainOptions));

        Assert.Empty(plan.Failures);
        var slot = Assert.Single(plan.Slots);
        Assert.Equal("LowerKey", slot.Reference!.Key);
        Assert.Equal("V7", slot.Reference.Version);
        Assert.False(slot.Enabled);
        Assert.Equal("provider-a", slot.ProviderConstraint);
        Assert.EndsWith("appsettings.json", slot.Source!.FilePath, StringComparison.Ordinal);
        Assert.Single(secret.Validated);
        Assert.Equal(0, secret.ResolveCalls);
    }

    [Fact]
    public void Compile_HigherDescriptorCannotInheritARequiredKey()
    {
        using var fixture = FileFixture.Create(Wrap("""{"key":"lower","version":"1"}"""),
            Wrap("""{"version":"2","enabled":false}"""), "Production");
        var secret = new FakeSecretProvider("provider-a");

        var plan = Compiler(fixture.Provider, [secret]).Compile("Production", "Service", typeof(FlatOptions));

        Assert.Contains(plan.Failures, f => f.Path == "Service:ApiKey" && f.Code == "secret-descriptor-incomplete");
        Assert.Equal(0, secret.ValidateCalls);
        Assert.Equal(0, secret.ResolveCalls);
    }

    [Fact]
    public void Compile_InvalidLowerDescriptorCannotBeHiddenByValidHigherDescriptor()
    {
        using var fixture = FileFixture.Create(Wrap("""{"enabled":false}"""),
            Wrap("""{"key":"valid"}"""), "Production");
        var secret = new FakeSecretProvider("provider-a");

        var plan = Compiler(fixture.Provider, [secret]).Compile("Production", "Service", typeof(FlatOptions));

        Assert.Contains(plan.Failures, f => f.Code == "secret-descriptor-incomplete"
            && f.Source!.FilePath!.EndsWith("appsettings.json", StringComparison.Ordinal));
        Assert.Equal(0, secret.ValidateCalls);
        Assert.Equal(0, secret.ResolveCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Compile_ValidatesShadowedLowerProviderReferenceBeforeAnyResolution(bool providerConstrained)
    {
        var constraint = providerConstrained ? ",\"provider\":\"provider-a\"" : "";
        using var fixture = FileFixture.Create(
            Wrap("{\"key\":\"invalid-lower\",\"enabled\":false" + constraint + "}"),
            Wrap("{\"key\":\"valid-higher\"" + constraint + "}"), "Production");
        var raw = new FakeRawProvider();
        var secret = new FakeSecretProvider("provider-a")
        {
            Validation = reference => reference.Key == "invalid-lower"
                ? ConfigSecretReferenceValidation.Invalid() : ConfigSecretReferenceValidation.Supported()
        };

        var plan = Compiler([fixture.Provider, raw], [secret]).Compile("Production", "Service", typeof(FlatOptions));

        Assert.Contains(plan.Failures, f => f.Code == "secret-reference-invalid" && f.Path == "Service:ApiKey"
            && f.ProviderId == "provider-a" && f.Source!.FilePath!.EndsWith("appsettings.json", StringComparison.Ordinal));
        Assert.Equal(new[] { "invalid-lower", "valid-higher" }, secret.Validated.Select(r => r.Key));
        AssertNoIo(raw, secret);
    }

    [Fact]
    public void Compile_NoDescriptorMeansEnabledEmptyDestinationAndNoReferenceValidation()
    {
        using var fixture = FileFixture.Create("""{"Service":{"Plain":"ordinary"}}""", null, "Production");
        var raw = new FakeRawProvider();
        var secret = new FakeSecretProvider("provider-a");

        var plan = Compiler([fixture.Provider, raw], [secret]).Compile("Production", "Service", typeof(FlatWithPlainOptions));

        Assert.Empty(plan.Failures);
        var slot = Assert.Single(plan.Slots);
        Assert.True(slot.Enabled);
        Assert.Null(slot.Reference);
        Assert.Empty(slot.Providers);
        Assert.Equal(0, secret.ValidateCalls);
        AssertNoIo(raw, secret);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Staging")]
    public void Compile_WildcardDirectoryReadFailureAppliesToEveryEnvironment(string environment)
    {
        var snapshot = new ConfigFileProviderSnapshot(new(), new(), [],
            new Dictionary<string, Lazy<ConfigFileSourceLocationMap>>(), [],
            [new ConfigFileLoadFailure("config-file-directory-unreadable", "*", "configuration-directory", 0,
                ConfigFileLoadFailureClassification.Read)]);
        var file = new FileBasedConfigProvider(snapshot);
        var raw = new FakeRawProvider();
        var secret = new FakeSecretProvider("provider-a");

        var plan = Compiler([file, raw], [secret]).Compile(environment, "Service", typeof(FlatOptions));

        var failure = Assert.Single(plan.Failures);
        Assert.Equal("config-composition-file-invalid", failure.Code);
        Assert.Equal("Service", failure.Path);
        Assert.Equal("configuration-directory", failure.Source!.FilePath);
        Assert.Equal(0, secret.ValidateCalls);
        AssertNoIo(raw, secret);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Compile_ExplicitAndProviderlessDeclarationsValidateEvenWhenDisabled(bool constrained, bool enabled)
    {
        var descriptor = new Dictionary<string, object>
        {
            ["KEY"] = "Opaque.Key:MixedCase",
            ["VERSION"] = "Release-A",
            ["ENABLED"] = enabled,
        };
        if (constrained) descriptor["PROVIDER"] = "PROVIDER-A";
        using var fixture = FileFixture.Create(Wrap(JsonSerializer.Serialize(descriptor)), null, "Production");
        var a = new FakeSecretProvider("provider-a");
        var b = new FakeSecretProvider("provider-b") { Validation = _ => ConfigSecretReferenceValidation.Unclaimed() };
        var c = new FakeSecretProvider("provider-c");
        var raw = new FakeRawProvider();

        var plan = Compiler([fixture.Provider, raw], [c, b, a]).Compile("Production", "Service", typeof(FlatOptions));

        Assert.Empty(plan.Failures);
        var slot = Assert.Single(plan.Slots);
        Assert.Equal(enabled, slot.Enabled);
        Assert.Equal(constrained ? new[] { "provider-a" } : ["provider-a", "provider-c"], slot.Providers.Select(p => p.Id));
        var reference = Assert.Single(a.Validated);
        Assert.Equal("Opaque.Key:MixedCase", reference.Key);
        Assert.Equal("Release-A", reference.Version);
        Assert.Equal("Service:ApiKey", reference.LogicalPath);
        Assert.Equal("Production", reference.Environment);
        Assert.Equal(constrained ? 0 : 1, b.ValidateCalls);
        Assert.Equal(constrained ? 0 : 1, c.ValidateCalls);
        Assert.Same(a, slot.Providers[0].Provider);
        AssertNoIo(raw, a, b, c);
    }

    [Theory]
    [InlineData("invalid", "secret-reference-invalid")]
    [InlineData("throws", "secret-reference-invalid")]
    [InlineData("unclaimed", "secret-reference-unsupported")]
    [InlineData("unregistered", "secret-provider-not-registered")]
    public void Compile_ExplicitProviderValidationFailuresDoNotFallThrough(string outcome, string code)
    {
        using var fixture = FileFixture.Create(Wrap("""{"key":"key","provider":"provider-a","enabled":false}"""), null, "Production");
        var a = new FakeSecretProvider("provider-a") { Validation = _ => ValidationResult(outcome) };
        var b = new FakeSecretProvider("provider-b");
        var raw = new FakeRawProvider();
        IReadOnlyList<IConfigSecretProvider> registrations = outcome == "unregistered" ? [b] : [b, a];

        var plan = Compiler([fixture.Provider, raw], registrations).Compile("Production", "Service", typeof(FlatOptions));

        Assert.Contains(plan.Failures, f => f.Code == code && f.Path == "Service:ApiKey");
        Assert.Equal(0, b.ValidateCalls);
        AssertNoIo(raw, a, b);
        Assert.DoesNotContain("exception-sentinel", JsonSerializer.Serialize(plan.Failures), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("throws")]
    public void Compile_ProviderlessInvalidValidationIsTerminalDespiteACompatibleProvider(string outcome)
    {
        using var fixture = FileFixture.Create(Wrap("""{"key":"key"}"""), null, "Production");
        var a = new FakeSecretProvider("provider-a") { Validation = _ => ValidationResult(outcome) };
        var b = new FakeSecretProvider("provider-b");
        var raw = new FakeRawProvider();

        var plan = Compiler([fixture.Provider, raw], [b, a]).Compile("Production", "Service", typeof(FlatOptions));

        Assert.Contains(plan.Failures, f => f.Code == "secret-reference-invalid" && f.ProviderId == "provider-a");
        Assert.Single(a.Validated);
        Assert.Single(b.Validated);
        AssertNoIo(raw, a, b);
    }

    [Theory]
    [InlineData(false, "secret-provider-not-registered")]
    [InlineData(true, "secret-reference-unsupported")]
    public void Compile_ProviderlessReferenceRequiresACompatibleRegisteredProvider(bool registered, string code)
    {
        using var fixture = FileFixture.Create(Wrap("""{"key":"key","enabled":false}"""), null, "Production");
        var secret = new FakeSecretProvider("provider-a") { Validation = _ => ConfigSecretReferenceValidation.Unclaimed() };
        var raw = new FakeRawProvider();

        var plan = Compiler([fixture.Provider, raw], registered ? [secret] : []).Compile("Production", "Service", typeof(FlatOptions));

        Assert.Contains(plan.Failures, f => f.Code == code);
        AssertNoIo(raw, secret);
    }

    [Theory]
    [InlineData(ConfigSecretConfiguredClaimKind.ExactMapping, "App:Service:ApiKey")]
    [InlineData(ConfigSecretConfiguredClaimKind.ExactMapping, "App.Service.apikey")]
    [InlineData(ConfigSecretConfiguredClaimKind.RootMapping, "App:Service")]
    [InlineData(ConfigSecretConfiguredClaimKind.RootConvention, "APP.SERVICE")]
    [InlineData(ConfigSecretConfiguredClaimKind.ExactMapping, "App")]
    [InlineData(ConfigSecretConfiguredClaimKind.ExactMapping, "App:Service:ApiKey:Nested")]
    [InlineData(ConfigSecretConfiguredClaimKind.ExactMapping, "App:Service:Plain")]
    public void Compile_RejectsEachOverlappingClaimIndependentlyEvenForDisabledDeclarations(
        ConfigSecretConfiguredClaimKind kind, string path)
    {
        using var fixture = FileFixture.Create(
            """{"App":{"Service":{"ApiKey":{"key":"inline","enabled":false}}}}""", null, "Production");
        var secret = new FakeSecretProvider("provider-a");
        var raw = new FakeRawProvider();
        var claims = new FakeClaims(new ConfigSecretConfiguredClaim(kind, path, "provider-a", "mapped", null));

        var plan = Compiler([fixture.Provider, raw], [secret], [claims])
            .Compile("Production", "App:Service", typeof(FlatWithPlainOptions));

        Assert.Contains(plan.Failures, f => f.Code == "secret-claim-overlap");
        Assert.Equal(0, secret.ValidateCalls);
        AssertNoIo(raw, secret);
    }

    [Fact]
    public void Compile_UnrelatedClaimsDoNotOverlapACommonTextPrefix()
    {
        using var fixture = FileFixture.Create(Wrap("""{"key":"inline"}"""), null, "Production");
        var secret = new FakeSecretProvider("provider-a");
        var source = new FakeClaims(new ConfigSecretConfiguredClaim(ConfigSecretConfiguredClaimKind.ExactMapping, "ServiceOther:ApiKey", "provider-a", "other", null));

        var plan = Compiler(fixture.Provider, [secret], [source]).Compile("Production", "Service", typeof(FlatOptions));

        Assert.Empty(plan.Failures);
        Assert.Equal("inline", Assert.Single(secret.Validated).Key);
        Assert.Equal(0, secret.ResolveCalls);
    }

    [Fact]
    public void Compile_TwoMappedSiblingsRequireBothDestinationsToBecomeSecrets()
    {
        var raw = new FakeRawProvider();
        var secret = new FakeSecretProvider("provider-a");
        var claims = new FakeClaims(
            new(ConfigSecretConfiguredClaimKind.ExactMapping, "Service:ApiKey", "provider-a", "First.Opaque:KEY", "V1"),
            new(ConfigSecretConfiguredClaimKind.ExactMapping, "service.plain", "provider-a", "Second.Opaque:KEY", "V2"));
        var compiler = Compiler([raw], [secret], [claims]);

        var partial = compiler.Compile("Production", "Service", typeof(FlatWithPlainOptions));
        Assert.Contains(partial.Failures, f => f.Code == "secret-claim-overlap" && f.Path == "service:plain");
        Assert.Equal(0, secret.ValidateCalls);

        var complete = compiler.Compile("Production", "Service", typeof(FullyMigratedOptions));
        Assert.Empty(complete.Failures);
        Assert.Equal(new[] { "First.Opaque:KEY", "Second.Opaque:KEY" }, complete.Slots.Select(s => s.Reference!.Key));
        Assert.Equal(new[] { "V1", "V2" }, complete.Slots.Select(s => s.Reference!.Version));
        Assert.All(complete.Slots, s =>
        {
            Assert.Equal("provider-a", s.ProviderConstraint);
            Assert.Same(secret, Assert.Single(s.Providers).Provider);
            Assert.Equal(ConfigAuditSourceKind.Provider, s.Source!.Kind);
        });
        Assert.Equal(new[] { "Service:ApiKey", "Service:Plain" }, secret.Validated.Select(r => r.LogicalPath),
            StringComparer.OrdinalIgnoreCase);
        AssertNoIo(raw, secret);
    }

    [Fact]
    public void Compile_DuplicateMappedDestinationsCollideAcrossCaseAndSeparators()
    {
        var raw = new FakeRawProvider();
        var secret = new FakeSecretProvider("provider-a");
        var claims = new FakeClaims(
            new(ConfigSecretConfiguredClaimKind.ExactMapping, "Service:ApiKey", "provider-a", "first", null),
            new(ConfigSecretConfiguredClaimKind.ExactMapping, "service.apikey", "provider-a", "second", null));

        var plan = Compiler([raw], [secret], [claims]).Compile("Production", "Service", typeof(FlatOptions));

        Assert.Contains(plan.Failures, f => f.Code == "secret-claim-overlap");
        Assert.Equal(0, secret.ValidateCalls);
        AssertNoIo(raw, secret);
    }

    [Theory]
    [InlineData("Service.ApiKey")]
    [InlineData("service:APIKEY")]
    public void Compile_FlatAndNestedSpellingsCannotDeclareTheSameDestination(string flattenedPath)
    {
        using var fixture = FileFixture.Create(
            "{\"Service\":{\"ApiKey\":{\"key\":\"first\"}},\"" + flattenedPath + "\":{\"key\":\"second\"}}", null, "Production");
        var secret = new FakeSecretProvider("provider-a");

        var plan = Compiler(fixture.Provider, [secret]).Compile("Production", "Service", typeof(FlatOptions));

        Assert.Contains(plan.Failures, f => f.Code == "secret-path-collision");
        Assert.Equal(0, secret.ValidateCalls);
        Assert.Equal(0, secret.ResolveCalls);
    }

    [Theory]
    [InlineData("App.Service")]
    [InlineData("APP:SERVICE")]
    public void Compile_CanonicalizesRootAndFileAliasesWithoutNormalizingProviderKeys(string root)
    {
        using var fixture = FileFixture.Create(
            """{"app.service.apikey":{"key":"Opaque.Key:CaseSensitive","version":"Release.V:1"}}""", null, "Production");
        var secret = new FakeSecretProvider("provider-a");

        var plan = Compiler(fixture.Provider, [secret]).Compile("production", root, typeof(FlatOptions));

        Assert.Empty(plan.Failures);
        var reference = Assert.Single(secret.Validated);
        Assert.Equal(root.Replace('.', ':') + ":ApiKey", reference.LogicalPath);
        Assert.Equal("Opaque.Key:CaseSensitive", reference.Key);
        Assert.Equal("Release.V:1", reference.Version);
        Assert.Equal(0, secret.ResolveCalls);
    }

    [Theory]
    [InlineData("Service..Api")]
    [InlineData("Service::Api")]
    [InlineData(".Service")]
    [InlineData("Service:")]
    [InlineData("Service: :Api")]
    public void Compile_InvalidLogicalRootFailsBeforeProviderCalls(string root)
    {
        var raw = new FakeRawProvider();
        var secret = new FakeSecretProvider("provider-a");

        var plan = Compiler([raw], [secret]).Compile("Production", root, typeof(FlatOptions));

        Assert.Equal("secret-path-invalid", Assert.Single(plan.Failures).Code);
        Assert.Equal(0, secret.ValidateCalls);
        AssertNoIo(raw, secret);
    }

    [Theory]
    [InlineData("provider-a", "secret-provider-id-duplicate")]
    [InlineData("Provider-A", "secret-provider-id-invalid")]
    public void Compile_RejectsBadRegistrationsBeforeFileOrProviderCallbacks(string secondId, string code)
    {
        var location = A.Fake<IConfigFileLocationProvider>();
        var file = new FileBasedConfigProvider(location, A.Fake<ILogger<FileBasedConfigProvider>>());
        var raw = new FakeRawProvider();
        var a = new FakeSecretProvider("provider-a");
        var b = new FakeSecretProvider(secondId);
        var claims = new FakeClaims();

        var plan = Compiler([file, raw], [a, b], [claims]).Compile("Production", "Service", typeof(FlatOptions));

        Assert.Contains(plan.Failures, f => f.Code == code);
        A.CallTo(() => location.Directory).MustNotHaveHappened();
        Assert.Equal(0, claims.InspectCalls);
        Assert.Equal(0, a.ValidateCalls);
        Assert.Equal(0, b.ValidateCalls);
        AssertNoIo(raw, a, b);
    }

    [Theory]
    [InlineData(ConfigProviderClaim.Unclaimed, false, true)]
    [InlineData(ConfigProviderClaim.MayClaim, false, false)]
    [InlineData(ConfigProviderClaim.Unclaimed, true, false)]
    public void Compile_TypedOnlyBaseCanBeExcludedOnlyBySuccessfulLocalUnclaimedInspection(
        ConfigProviderClaim claim, bool throws, bool succeeds)
    {
        var typed = A.Fake<IConfigProvider>(o => o.Implements<IConfigProviderClaimInspector>());
        var inspector = (IConfigProviderClaimInspector)typed;
        if (throws) A.CallTo(() => inspector.InspectClaim("Production", "Service")).Throws(new InvalidOperationException("exception-sentinel"));
        else A.CallTo(() => inspector.InspectClaim("Production", "Service")).Returns(claim);
        var raw = new FakeRawProvider();

        var plan = Compiler([typed, raw], []).Compile("Production", "Service", typeof(FlatOptions));

        if (succeeds)
        {
            Assert.Empty(plan.Failures);
            Assert.Same(raw, Assert.Single(plan.Bases));
        }
        else Assert.Contains(plan.Failures, f => f.Code == "config-composition-provider-unsupported");
        A.CallTo(() => inspector.InspectClaim("Production", "Service")).MustHaveHappenedOnceExactly();
        Assert.DoesNotContain(Fake.GetCalls(typed), c => c.Method.Name == nameof(IConfigProvider.GetValue));
        AssertNoIo(raw);
    }

    [Theory]
    [InlineData(typeof(SelfRecursiveOptions))]
    [InlineData(typeof(MutualA))]
    public void Compile_RecursiveSecretGraphsFailBeforeClaimInspectionOrProviderCalls(Type type)
    {
        var raw = new FakeRawProvider();
        var secret = new FakeSecretProvider("provider-a");
        var claims = new FakeClaims();

        var plan = Compiler([raw], [secret], [claims]).Compile("Production", "Service", type);

        Assert.Contains(plan.Failures, f => f.Code == "secret-destination-type-unsupported");
        Assert.Equal(0, claims.InspectCalls);
        Assert.Equal(0, secret.ValidateCalls);
        AssertNoIo(raw, secret);
    }

    [Fact]
    public void Compile_ReportsFlatAndHierarchicalAliasesBetweenDifferentPathDepths()
    {
        var raw = new FakeRawProvider();
        var secret = new FakeSecretProvider("provider-a");

        var plan = Compiler([raw], [secret]).Compile("Production", "Service", typeof(HierarchicalAliasOptions));

        Assert.Equal(new[] { "PRODUCTION_SERVICE_A_B", "PRODUCTION__SERVICE__A__B", "SERVICE_A_B", "SERVICE__A__B" },
            plan.Failures.Select(f => f.EnvironmentVariableName).Distinct().Order(StringComparer.Ordinal));
        Assert.All(plan.Failures, f =>
        {
            Assert.Equal("secret-environment-alias-collision", f.Code);
            Assert.Contains(f.Path, new[] { "Service:A-B", "Service:A:B" });
            Assert.Equal(f.Path == "Service:A-B" ? "Service:A:B" : "Service:A-B", f.RelatedPath);
        });
        Assert.Equal(0, secret.ValidateCalls);
        AssertNoIo(raw, secret);
    }

    [Fact]
    public void Compile_DetectsWhenAScopedCandidateEqualsAnotherPathsUnscopedCandidate()
    {
        var raw = new FakeRawProvider();
        var secret = new FakeSecretProvider("provider-a");

        var plan = Compiler([raw], [secret]).Compile("Service", "Service", typeof(ScopedAliasOptions));

        Assert.Contains(plan.Failures, f => f.Code == "secret-environment-alias-collision"
            && f.EnvironmentVariableName == "SERVICE_SERVICE_X");
        Assert.Contains(plan.Failures, f => f.Code == "secret-environment-alias-collision"
            && f.EnvironmentVariableName == "SERVICE__SERVICE__X");
        Assert.All(plan.Failures, f =>
        {
            Assert.Contains(f.Path, new[] { "Service:X", "Service:Service:X" });
            Assert.Equal(f.Path == "Service:X" ? "Service:Service:X" : "Service:X", f.RelatedPath);
        });
        Assert.Equal(0, secret.ValidateCalls);
        AssertNoIo(raw, secret);
    }

    [Fact]
    public void Compile_CachesAValidatedPlanForTheSameInputsWithoutRevalidatingProviders()
    {
        using var fixture = FileFixture.Create(Wrap("""{"key":"key"}"""), null, "Production");
        var secret = new FakeSecretProvider("provider-a");
        var raw = new FakeRawProvider();
        var compiler = Compiler([fixture.Provider, raw], [secret]);

        var first = compiler.Compile("Production", "Service", typeof(FlatOptions));
        var second = compiler.Compile("Production", "Service", typeof(FlatOptions));

        Assert.Empty(first.Failures);
        Assert.Same(first, second);
        Assert.Single(secret.Validated);
        AssertNoIo(raw, secret);
    }

    [Fact]
    public void Compile_ValidatesEachReferenceInAMultipleDestinationRootBeforeAnyResolution()
    {
        using var fixture = FileFixture.Create(
            """{"Service":{"OtherKey":{"key":"second"},"ApiKey":{"key":"first"}}}""", null, "Production");
        var raw = new FakeRawProvider();
        var secret = new FakeSecretProvider("provider-a");

        var plan = Compiler([fixture.Provider, raw], [secret]).Compile("Production", "Service", typeof(TwoSecretOptions));

        Assert.Empty(plan.Failures);
        Assert.Equal(new[] { "first", "second" }, secret.Validated.Select(r => r.Key).Order(StringComparer.Ordinal));
        Assert.All(plan.Slots, s => Assert.Equal("provider-a", Assert.Single(s.Providers).Id));
        AssertNoIo(raw, secret);
    }

    private static ConfigSecretReferenceValidation ValidationResult(string outcome) => outcome switch
    {
        "invalid" => ConfigSecretReferenceValidation.Invalid(),
        "throws" => throw new InvalidOperationException("exception-sentinel"),
        _ => ConfigSecretReferenceValidation.Unclaimed()
    };

    private static string Wrap(string descriptor) => "{\"Service\":{\"ApiKey\":" + descriptor + "}}";

    private static void AssertNoIo(FakeRawProvider raw, params FakeSecretProvider[] secrets)
    {
        Assert.Equal(0, raw.ResolveCalls);
        Assert.Equal(0, raw.GetValueCalls);
        Assert.All(secrets, secret => Assert.Equal(0, secret.ResolveCalls));
    }

    private static ConfigCompositionJsonContract Contract(AppSurfaceConfigOptions? options = null) =>
        new(options ?? new AppSurfaceConfigOptions());

    private static ConfigCompositionPlanCompiler Compiler(
        FileBasedConfigProvider provider,
        IReadOnlyList<IConfigSecretProvider> secrets,
        IEnumerable<IConfigSecretDeclarationSource>? claims = null) =>
        Compiler([provider], secrets, claims);

    private static ConfigCompositionPlanCompiler Compiler(
        IReadOnlyList<IConfigProvider> bases,
        IReadOnlyList<IConfigSecretProvider> secrets,
        IEnumerable<IConfigSecretDeclarationSource>? claims = null) =>
        new(Contract(), new ConfigSecretProviderRegistry(secrets), bases, claims ?? []);

    private sealed class FakeSecretProvider(string id) : IConfigSecretProvider
    {
        public string Id { get; } = id;
        public int ValidateCalls { get; private set; }
        public int ResolveCalls { get; private set; }
        public List<ConfigSecretReference> Validated { get; } = [];
        public Func<ConfigSecretReference, ConfigSecretReferenceValidation> Validation { get; init; } =
            _ => ConfigSecretReferenceValidation.Supported();
        public ConfigSecretReferenceValidation ValidateReference(ConfigSecretReference reference)
        {
            ValidateCalls++;
            Validated.Add(reference);
            return Validation(reference);
        }
        public ConfigSecretProviderResolution Resolve(ConfigSecretReference reference, ConfigSecretResolutionContext context)
        {
            ResolveCalls++;
            return ConfigSecretProviderResolution.Missing(Id);
        }
    }

    private sealed class FakeRawProvider : IConfigProvider, IConfigCompositionValueProvider
    {
        public int Priority => 2;
        public string Name => "raw-base";
        public int ResolveCalls { get; private set; }
        public int GetValueCalls { get; private set; }
        public T? GetValue<T>(string environment, string key)
        {
            GetValueCalls++;
            return default;
        }
        public ConfigCompositionValueResolution ResolveRaw(string environment, string logicalKey)
        {
            ResolveCalls++;
            return ConfigCompositionValueResolution.Missing(Name, Priority);
        }
    }

    private sealed class FakeClaims(params ConfigSecretConfiguredClaim[] claims) : IConfigSecretDeclarationSource
    {
        public int InspectCalls { get; private set; }
        public IReadOnlyList<ConfigSecretConfiguredClaim> InspectClaims(string rootLogicalPath, IReadOnlyList<string> secretDestinationPaths)
        {
            InspectCalls++;
            return claims;
        }
    }

    private sealed class FileFixture : IDisposable
    {
        private readonly string _directory;
        public FileBasedConfigProvider Provider { get; }
        private FileFixture(string directory, FileBasedConfigProvider provider)
        {
            _directory = directory;
            Provider = provider;
        }

        public static FileFixture Create(string lower, string? higher, string environment)
        {
            var directory = Path.Combine(Path.GetTempPath(), "appsurface-composition-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "appsettings.json"), lower);
            if (higher is not null) File.WriteAllText(Path.Combine(directory, $"appsettings.{environment}.json"), higher);
            var location = A.Fake<IConfigFileLocationProvider>();
            A.CallTo(() => location.Directory).Returns(directory);
            return new(directory, new FileBasedConfigProvider(location, A.Fake<ILogger<FileBasedConfigProvider>>()));
        }
        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private sealed class FlatOptions { public Secret<string> ApiKey { get; init; } = new(); }
    private sealed class FlatWithPlainOptions { public Secret<string> ApiKey { get; init; } = new(); public string Plain { get; init; } = ""; }
    private sealed class RepeatedOptions
    {
        public FlatWithPlainOptions First { get; init; } = new();
        public FlatWithPlainOptions Second { get; init; } = new();
    }
    private sealed class SelfRecursiveOptions { public Secret<string> Token { get; init; } = new(); public SelfRecursiveOptions? Child { get; init; } }
    private sealed class MutualA { public MutualB? Child { get; init; } }
    private sealed class MutualB { public MutualA? Parent { get; init; } public Secret<string> Token { get; init; } = new(); }
    private sealed class UnsupportedContainers
    {
        public List<Secret<string>> Items { get; init; } = [];
        public Dictionary<string, Secret<string>> Lookup { get; init; } = [];
        public Secret<object> Opaque { get; init; } = new();
        public ConfigStruct<SecretStruct> Struct { get; init; } = new();
    }
    private struct SecretStruct
    {
        public SecretStruct() { }
        public Secret<string> Token { get; set; } = new();
    }
    private sealed class AnnotatedOptions
    {
        [JsonPropertyName("api_key")] public Secret<string> ApiKey { get; init; } = new();
        public RecordOptions Record { get; init; } = new(new());
        [JsonIgnore] public Secret<string> Ignored { get; init; } = new();
        [JsonInclude] private Secret<string> IncludedField = new();
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extension { get; init; }
        [JsonConverter(typeof(SecretStringConverter))] public Secret<string> Converted { get; init; } = new();
    }
    private sealed record RecordOptions([property: JsonPropertyName("RecordToken")] Secret<string> Token);
    private sealed class SecretStringConverter : JsonConverter<Secret<string>>
    {
        public override Secret<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new();
        public override void Write(Utf8JsonWriter writer, Secret<string> value, JsonSerializerOptions options) => writer.WriteNullValue();
    }
    private sealed class NestedOptions { public FlatOptions Child { get; init; } = new(); }
    private sealed class AliasOptions { [JsonPropertyName("A_B")] public Secret<string> A { get; init; } = new(); [JsonPropertyName("A-B")] public Secret<string> B { get; init; } = new(); }

    private sealed class ScalarOptions<T> where T : notnull
    {
        public Secret<T> Token { get; init; } = new();
    }

    private sealed class MemberOptions<T>
    {
        public T? Item { get; init; }
    }

    private sealed class PlainRecursiveOptions
    {
        public PlainRecursiveOptions? Child { get; init; }
        public string Label { get; init; } = "";
    }

    private sealed class IgnoredOnlyOptions
    {
        [JsonIgnore] public Secret<string> Always { get; init; } = new();
        [JsonInclude, JsonIgnore] public Secret<string> IgnoredField = new();
        public Secret<string> UnincludedField = new();
        public static Secret<string> Static { get; } = new();
        public Secret<string> this[int index] => new();
    }

    private class InheritedOptions
    {
        public Secret<string> Inherited { get; init; } = new();
    }

    private sealed class BindableOptions : InheritedOptions
    {
        public Secret<string> Init { get; init; } = new();
        [JsonInclude] public Secret<string> IncludedField = new();
        [JsonInclude, JsonPropertyName("private_set")] public Secret<string> PrivateSet { get; private set; } = new();
        public RecordOptions Record { get; init; } = new(new());
        public ConstructorOptions ReadOnly { get; init; } = new(new());
    }

    private sealed class ConstructorOptions(Secret<string> token)
    {
        public Secret<string> Token { get; } = token;
    }

    private sealed class ConditionalIgnoreOptions
    {
        [JsonIgnore] public Secret<string> Always { get; init; } = new();
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public Secret<string> Never { get; init; } = new();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Secret<string>? WhenNull { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public Secret<string>? WhenDefault { get; init; }
    }

    private sealed class ReadOnlyOptions
    {
        public Secret<string> Token { get; } = new();
        public FlatOptions Child { get; } = new();
    }

    [JsonConverter(typeof(OpaqueConverter<ConvertedObjectOptions>))]
    private sealed class ConvertedObjectOptions
    {
        public Secret<string> Token { get; init; } = new();
    }

    [JsonConverter(typeof(JsonStringEnumConverter<ConvertedEnum>))]
    private enum ConvertedEnum { First }

    private sealed class ConvertedMembersOptions
    {
        [JsonConverter(typeof(OpaqueConverter<FlatOptions>))] public FlatOptions Container { get; init; } = new();
        [JsonConverter(typeof(SecretStringConverter))] public Secret<string> Token { get; init; } = new();
        public PolymorphicOptions Polymorphic { get; init; } = new();
    }

    [JsonPolymorphic, JsonDerivedType(typeof(DerivedOptions), "derived")]
    private class PolymorphicOptions
    {
        public Secret<string> Token { get; init; } = new();
    }

    private sealed class DerivedOptions : PolymorphicOptions;

    private sealed class OpaqueConverter<T> : JsonConverter<T> where T : new()
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new();
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteNullValue();
    }

    private sealed class DottedNameOptions
    {
        [JsonPropertyName("A.B")] public Secret<string> Token { get; init; } = new();
    }

    private sealed class ColonNameOptions
    {
        [JsonPropertyName("A:B")] public Secret<string> Token { get; init; } = new();
    }

    private sealed class EmptyNameOptions
    {
        [JsonPropertyName("")] public Secret<string> Token { get; init; } = new();
    }

    private sealed class CaseCollisionOptions
    {
        [JsonPropertyName("Token")] public Secret<string> First { get; init; } = new();
        [JsonPropertyName("token")] public Secret<string> Second { get; init; } = new();
    }

    private sealed class TwoSecretOptions
    {
        public Secret<string> ApiKey { get; init; } = new();
        public Secret<string> OtherKey { get; init; } = new();
    }

    private sealed class FullyMigratedOptions
    {
        public Secret<string> ApiKey { get; init; } = new();
        public Secret<string> Plain { get; init; } = new();
    }

    private sealed class HierarchicalAliasOptions
    {
        [JsonPropertyName("A-B")] public Secret<string> Flat { get; init; } = new();
        public AliasLeafOptions A { get; init; } = new();
    }

    private sealed class AliasLeafOptions
    {
        public Secret<string> B { get; init; } = new();
    }

    private sealed class ScopedAliasOptions
    {
        public Secret<string> X { get; init; } = new();
        public ScopedLeafOptions Service { get; init; } = new();
    }

    private sealed class ScopedLeafOptions
    {
        public Secret<string> X { get; init; } = new();
    }
}
