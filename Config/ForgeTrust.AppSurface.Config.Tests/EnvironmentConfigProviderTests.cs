using FakeItEasy;
using ForgeTrust.AppSurface.Core;
using System.Runtime.CompilerServices;

namespace ForgeTrust.AppSurface.Config.Tests;

public class EnvironmentConfigProviderTests
{
    private static T? ResolveValue<T>(EnvironmentConfigProvider provider, string environment, string key)
    {
        var logicalKey = AppSurfaceConfigKey.Parse(key);
        var result = provider.Resolve<T>(new ConfigProviderRequest(environment, logicalKey));
        return result.Status switch
        {
            ConfigProviderValueStatus.Found => result.Value,
            ConfigProviderValueStatus.Missing => default,
            _ => throw new ConfigurationResolutionException(
                environment, logicalKey, provider.Name,
                result.Diagnostic ?? ConfigDiagnosticCatalog.Terminal("config-provider-failed"))
        };
    }

    private static readonly ConditionalWeakTable<IEnvironmentProvider, Dictionary<string, string>> SnapshotValues = new();
    [Fact]
    public void GetValue_UsesEnvironmentSpecificVariableFirst()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION__FEATURE__ENABLED", "true");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = ResolveValue<bool>(provider, "Production", "Feature:Enabled");

        Assert.True(value);
        A.CallTo(() => innerProvider.CaptureEnvironmentVariables()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void GetValue_FallsBackToKeyWhenEnvironmentSpecificVariableMissing()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "DEV_US__SECTION__VALUE", null);
        SetSnapshotValue(innerProvider, "SECTION__VALUE", "42");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = ResolveValue<int>(provider, "Dev-Us", "Section:Value");

        Assert.Equal(42, value);
        A.CallTo(() => innerProvider.CaptureEnvironmentVariables()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void GetValue_ParsesEnumValuesCaseInsensitive()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION_DAY", null);
        SetSnapshotValue(innerProvider, "DAY", "monday");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = ResolveValue<DayOfWeek>(provider, "Production", "Day");

        Assert.Equal(DayOfWeek.Monday, value);
    }

    [Fact]
    public void GetValue_HandlesNullableTypes()
    {
        var innerProvider = SnapshotFake();
        var provider = new EnvironmentConfigProvider(innerProvider);

        SetSnapshotValue(innerProvider, "PRODUCTION_A", null);
        SetSnapshotValue(innerProvider, "A", "123");
        Assert.Equal(123, ResolveValue<int?>(provider, "Production", "A"));

        SetSnapshotValue(innerProvider, "PRODUCTION_B", null);
        SetSnapshotValue(innerProvider, "B", "");
        Assert.Throws<ConfigurationResolutionException>(() => ResolveValue<int?>(provider, "Production", "B"));

        SetSnapshotValue(innerProvider, "PRODUCTION_C", null);
        SetSnapshotValue(innerProvider, "C", null);
        Assert.Null(ResolveValue<int?>(provider, "Production", "C"));
    }

    [Fact]
    public void GetValue_ThrowsTerminalOnInvalidFormat()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "VALUE", "not-a-number");

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Throws<ConfigurationResolutionException>(() => ResolveValue<int>(provider, "Production", "Value"));
    }

    [Fact]
    public void GetValue_ThrowsTerminalOnOverflow()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "VALUE", long.MaxValue.ToString());

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Throws<ConfigurationResolutionException>(() => ResolveValue<int>(provider, "Production", "Value"));
    }

    [Fact]
    public void Properties_AreProxiedToInnerProvider()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.Environment).Returns("Test");
        A.CallTo(() => innerProvider.IsDevelopment).Returns(true);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("ANY", A<string?>._)).Returns("value");
        SetSnapshotValue(innerProvider, "ANY", "value");

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Equal("Test", provider.Environment);
        Assert.True(provider.IsDevelopment);
        Assert.Equal("value", provider.GetEnvironmentVariable("ANY"));
    }

    [Fact]
    public void GetValue_ThrowsTerminalOnInvalidEnum()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "VALUE", "InvalidEnumValue");

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Throws<ConfigurationResolutionException>(() => ResolveValue<System.UriKind>(provider, "Production", "Value"));
    }

    [Fact]
    public void GetValue_BindsTopLevelListFromJsonValue()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "MYAPP__ITEMS", null);
        SetSnapshotValue(innerProvider, "ITEMS", """["a","b"]""");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = ResolveValue<List<string>>(provider, "MyApp", "Items");

        Assert.NotNull(value);
        Assert.Equal(["a", "b"], value);
    }

    [Fact]
    public void GetValue_BindsTopLevelDictionaryFromJsonValue()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION_SETTINGS", null);
        SetSnapshotValue(innerProvider, "SETTINGS", """{"Retries":3,"Timeout":30}""");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = ResolveValue<Dictionary<string, int>>(provider, "Production", "Settings");

        Assert.NotNull(value);
        Assert.Equal(3, value["Retries"]);
        Assert.Equal(30, value["Timeout"]);
    }

    [Fact]
    public void GetValue_BindsIndexedListFromDoubleUnderscoreVariables()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION_MYAPP__ITEMS", null);
        SetSnapshotValue(innerProvider, "MYAPP__ITEMS", null);
        SetSnapshotValue(innerProvider, "PRODUCTION__MYAPP__ITEMS", null);
        SetSnapshotValue(innerProvider, "MYAPP__ITEMS", null);
        SetSnapshotValue(innerProvider, "PRODUCTION__MYAPP__ITEMS__0", null);
        SetSnapshotValue(innerProvider, "MYAPP__ITEMS__0", "First");
        SetSnapshotValue(innerProvider, "MYAPP__ITEMS__1", "Second");
        SetSnapshotValue(innerProvider, "MYAPP__ITEMS__2", null);

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = ResolveValue<List<string>>(provider, "Production", "MyApp:Items");

        Assert.NotNull(value);
        Assert.Equal(["First", "Second"], value);
    }

    [Fact]
    public void GetValue_BindsEnvScopedIndexedListFromDoubleUnderscoreVariables()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION_MYAPP__ITEMS", null);
        SetSnapshotValue(innerProvider, "MYAPP__ITEMS", null);
        SetSnapshotValue(innerProvider, "PRODUCTION__MYAPP__ITEMS", null);
        SetSnapshotValue(innerProvider, "MYAPP__ITEMS", null);
        SetSnapshotValue(innerProvider, "PRODUCTION__MYAPP__ITEMS__0", "First");
        SetSnapshotValue(innerProvider, "PRODUCTION__MYAPP__ITEMS__1", "Second");
        SetSnapshotValue(innerProvider, "PRODUCTION__MYAPP__ITEMS__2", null);
        SetSnapshotValue(innerProvider, "MYAPP__ITEMS__0", null);
        SetSnapshotValue(innerProvider, "MYAPP__ITEMS__1", null);

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = ResolveValue<List<string>>(provider, "Production", "MyApp:Items");

        Assert.NotNull(value);
        Assert.Equal(["First", "Second"], value);
    }

    [Fact]
    public void GetValue_ThrowsTerminalInsteadOfRescuingAnUnparseableHigherLayer()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION__VALUE", "not-a-number");
        SetSnapshotValue(innerProvider, "VALUE", "123");

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Throws<ConfigurationResolutionException>(() => ResolveValue<int>(provider, "Production", "Value"));
    }

    [Fact]
    public void GetValue_DeduplicatesDirectCandidatesWhenKeyHasNoSeparators()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION_ITEMS", null);
        SetSnapshotValue(innerProvider, "ITEMS", "not-json");
        SetSnapshotValue(innerProvider, "PRODUCTION__ITEMS", null);
        SetSnapshotValue(innerProvider, "PRODUCTION__ITEMS__0", null);
        SetSnapshotValue(innerProvider, "ITEMS__0", null);

        var provider = new EnvironmentConfigProvider(innerProvider);
        Assert.Throws<ConfigurationResolutionException>(() => ResolveValue<Dictionary<string, int>>(provider, "Production", "Items"));
        A.CallTo(() => innerProvider.CaptureEnvironmentVariables()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void GetValue_ParsesGuid()
    {
        var expected = Guid.NewGuid();
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION_ID", null);
        SetSnapshotValue(innerProvider, "ID", expected.ToString("D"));

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = ResolveValue<Guid>(provider, "Production", "Id");

        Assert.Equal(expected, value);
    }

    [Fact]
    public void GetValue_ThrowsTerminalOnInvalidGuid()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION_ID", null);
        SetSnapshotValue(innerProvider, "ID", "not-a-guid");

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Throws<ConfigurationResolutionException>(() => ResolveValue<Guid>(provider, "Production", "Id"));
    }

    [Fact]
    public void GetValue_ParsesDateTime()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION_WHEN", null);
        SetSnapshotValue(innerProvider, "WHEN", "2026-02-13T12:34:56Z");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = ResolveValue<DateTime>(provider, "Production", "When");

        Assert.Equal(new DateTime(2026, 2, 13, 12, 34, 56, DateTimeKind.Utc), value.ToUniversalTime());
    }

    [Fact]
    public void GetValue_ParsesDateTimeOffset()
    {
        var expected = DateTimeOffset.Parse("2026-02-13T12:34:56+00:00");
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION__WHEN__OFFSET", null);
        SetSnapshotValue(innerProvider, "WHEN__OFFSET", "2026-02-13T12:34:56+00:00");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = ResolveValue<DateTimeOffset>(provider, "Production", "When:Offset");

        Assert.Equal(expected, value);
    }

    [Fact]
    public void GetValue_ParsesTimeSpan()
    {
        var expected = TimeSpan.FromMinutes(90);
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION_DURATION", null);
        SetSnapshotValue(innerProvider, "DURATION", "01:30:00");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = ResolveValue<TimeSpan>(provider, "Production", "Duration");

        Assert.Equal(expected, value);
    }

    [Fact]
    public void GetValue_ParsesDecimalWithInvariantCulture()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION_RATE", null);
        SetSnapshotValue(innerProvider, "RATE", "12.34");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = ResolveValue<decimal>(provider, "Production", "Rate");

        Assert.Equal(12.34m, value);
    }

    [Fact]
    public void GetValue_HandlesNullableGuidEmptyString()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION__OPTIONAL__ID", null);
        SetSnapshotValue(innerProvider, "OPTIONAL__ID", string.Empty);

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Throws<ConfigurationResolutionException>(() => ResolveValue<Guid?>(provider, "Production", "Optional:Id"));
    }

    [Fact]
    public void GetValue_BindsIndexedArrayFromDoubleUnderscoreVariables()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION_MYAPP__VALUES", null);
        SetSnapshotValue(innerProvider, "MYAPP__VALUES", null);
        SetSnapshotValue(innerProvider, "PRODUCTION__MYAPP__VALUES", null);
        SetSnapshotValue(innerProvider, "MYAPP__VALUES", null);
        SetSnapshotValue(innerProvider, "PRODUCTION__MYAPP__VALUES__0", null);
        SetSnapshotValue(innerProvider, "MYAPP__VALUES__0", "A");
        SetSnapshotValue(innerProvider, "MYAPP__VALUES__1", "B");
        SetSnapshotValue(innerProvider, "MYAPP__VALUES__2", null);

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = ResolveValue<string[]>(provider, "Production", "MyApp:Values");

        Assert.NotNull(value);
        Assert.Equal(["A", "B"], value);
    }

    [Fact]
    public void GetValue_ThrowsTerminalOnUnsupportedJsonType()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION_VALUE", null);
        SetSnapshotValue(innerProvider, "VALUE", "\"System.String\"");

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Throws<ConfigurationResolutionException>(() => ResolveValue<Type>(provider, "Production", "Value"));
    }

    [Fact]
    public void GetValue_ThrowsTerminalOnInvalidDateTimeOffset()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION__WHEN__OFFSET", null);
        SetSnapshotValue(innerProvider, "WHEN__OFFSET", "not-a-datetimeoffset");

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Throws<ConfigurationResolutionException>(() => ResolveValue<DateTimeOffset>(provider, "Production", "When:Offset"));
    }

    [Fact]
    public void GetValue_ThrowsTerminalOnInvalidTimeSpan()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION_DURATION", null);
        SetSnapshotValue(innerProvider, "DURATION", "not-a-timespan");

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Throws<ConfigurationResolutionException>(() => ResolveValue<TimeSpan>(provider, "Production", "Duration"));
    }

    [Fact]
    public void GetValue_ThrowsTerminalWhenIndexedCollectionContainsInvalidElement()
    {
        var innerProvider = SnapshotFake();
        SetSnapshotValue(innerProvider, "PRODUCTION_MYAPP__VALUES", null);
        SetSnapshotValue(innerProvider, "MYAPP__VALUES", null);
        SetSnapshotValue(innerProvider, "PRODUCTION__MYAPP__VALUES", null);
        SetSnapshotValue(innerProvider, "MYAPP__VALUES", null);
        SetSnapshotValue(innerProvider, "PRODUCTION__MYAPP__VALUES__0", null);
        SetSnapshotValue(innerProvider, "MYAPP__VALUES__0", "1");
        SetSnapshotValue(innerProvider, "MYAPP__VALUES__1", "not-an-int");
        SetSnapshotValue(innerProvider, "MYAPP__VALUES__2", null);

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Throws<ConfigurationResolutionException>(() => ResolveValue<List<int>>(provider, "Production", "MyApp:Values"));
    }

    [Fact]
    public void TryPatch_PatchesNestedObjectFromDoubleUnderscoreVariables()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__DATABASE__PORT", "6543");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new AppSettings
        {
            Mode = "file",
            Database = new DatabaseOptions
            {
                Host = "db.from.file",
                Port = 5432
            }
        };

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out AppSettings? value);

        Assert.True(patched);
        Assert.NotSame(current, value);
        Assert.NotNull(value);
        Assert.Equal("file", value.Mode);
        Assert.Equal("db.from.file", value.Database.Host);
        Assert.Equal(6543, value.Database.Port);
    }

    [Fact]
    public void TryPatch_CreatesNestedObjectWhenOnlyChildEnvironmentVariablesExist()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "PRODUCTION__MYAPP__SETTINGS__MODE", "env");
        SetSnapshotValue(innerProvider, "PRODUCTION__MYAPP__SETTINGS__DATABASE__HOST", "db.from.env");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var patched = PatchLegacy<AppSettings>(provider, "Production", "MyApp:Settings", null, out var value);

        Assert.True(patched);
        Assert.NotNull(value);
        Assert.Equal("env", value.Mode);
        Assert.NotNull(value.Database);
        Assert.Equal("db.from.env", value.Database.Host);
    }

    [Fact]
    public void TryPatch_PatchesIndexedCollectionMember()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__0", "https://one.example");
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__1", "https://two.example");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new AppSettings();

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out AppSettings? value);

        Assert.True(patched);
        Assert.NotNull(value);
        Assert.Equal(["https://one.example", "https://two.example"], value.Endpoints);
    }

    [Fact]
    public void TryPatch_PatchesExistingGetterOnlyNestedObject()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__DATABASE__PORT", "6543");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new GetterOnlyAppSettings
        {
            Mode = "file"
        };
        current.Database.Host = "db.from.file";
        current.Database.Port = 5432;

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out GetterOnlyAppSettings? value);

        Assert.True(patched);
        Assert.NotSame(current, value);
        Assert.NotNull(value);
        Assert.Equal("file", value.Mode);
        Assert.Equal("db.from.file", value.Database.Host);
        Assert.Equal(6543, value.Database.Port);
    }

    [Fact]
    public void TryPatch_PatchesExistingGetterOnlyCollection()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__0", "https://one.example");
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__1", "https://two.example");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new GetterOnlyAppSettings();
        current.Endpoints.Add("https://file.example");

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out GetterOnlyAppSettings? value);

        Assert.True(patched);
        Assert.NotSame(current, value);
        Assert.NotNull(value);
        Assert.Equal(["https://one.example", "https://two.example"], value.Endpoints);
    }

    [Fact]
    public void TryPatch_PatchesExistingGetterOnlyCollectionFromEnvironmentScopedVariables()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "PRODUCTION__MYAPP__SETTINGS__ENDPOINTS__0", "https://one.example");
        SetSnapshotValue(innerProvider, "PRODUCTION__MYAPP__SETTINGS__ENDPOINTS__1", "https://two.example");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new GetterOnlyAppSettings();
        current.Endpoints.Add("https://file.example");

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out GetterOnlyAppSettings? value);

        Assert.True(patched);
        Assert.NotNull(value);
        Assert.Equal(["https://one.example", "https://two.example"], value.Endpoints);
    }

    [Fact]
    public void TryPatch_DoesNotPatchScalarTopLevelValue()
    {
        var innerProvider = SnapshotFake();
        var provider = new EnvironmentConfigProvider(innerProvider);

        var patched = PatchLegacy<string>(provider, "Production", "MyApp:Settings", null, out var value);

        Assert.False(patched);
        Assert.Null(value);
    }

    [Fact]
    public void TryPatch_DoesNotPatchNullableScalarTopLevelValue()
    {
        var innerProvider = SnapshotFake();
        var provider = new EnvironmentConfigProvider(innerProvider);

        var patched = PatchLegacy<int?>(provider, "Production", "MyApp:Settings", null, out var value);

        Assert.False(patched);
        Assert.Null(value);
    }

    [Fact]
    public void TryPatch_DoesNotPatchTopLevelRuntimeScalarValue()
    {
        var innerProvider = SnapshotFake();
        var provider = new EnvironmentConfigProvider(innerProvider);

        var patched = PatchLegacy<object>(provider, "Production", "MyApp:Settings", "file", out var value);

        Assert.False(patched);
        Assert.Null(value);
    }

    [Fact]
    public void TryPatch_DoesNotCreateTopLevelInterfaceValue()
    {
        var innerProvider = SnapshotFake();
        var provider = new EnvironmentConfigProvider(innerProvider);

        var patched = PatchLegacy<IConfigPatchContract>(provider, "Production", "MyApp:Settings", null, out var value);

        Assert.False(patched);
        Assert.Null(value);
    }

    [Fact]
    public void TryPatch_SkipsIndexerProperties()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new IndexedOptions();

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out IndexedOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Equal("file", current[0]);
    }

    [Fact]
    public void TryPatch_DoesNotPatchGetterOnlyScalarProperty()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__MODE", "environment");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new GetterOnlyScalarOptions();

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out GetterOnlyScalarOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Equal("file", current.Mode);
    }

    [Fact]
    public void TryPatch_DoesNotPatchPrivateSetterScalarProperty()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__MODE", "environment");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new PrivateSetterOptions();

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out PrivateSetterOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Equal("file", current.Mode);
    }

    [Fact]
    public void TryPatch_DoesNotAttachNullGetterOnlyNestedObject()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__DATABASE__PORT", "6543");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new NullGetterOnlyOptions();

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out NullGetterOnlyOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Null(current.Database);
    }

    [Fact]
    public void TryPatch_DoesNotUsePrivateSetterToAttachNestedObject()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__DATABASE__PORT", "6543");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new PrivateSetterChildOptions();

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out PrivateSetterChildOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Null(current.Database);
    }

    [Fact]
    public void TryPatch_DoesNotPatchGetterOnlyReadOnlyCollection()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__0", "https://one.example");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new GetterOnlyReadOnlyCollectionOptions();

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out GetterOnlyReadOnlyCollectionOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Equal(["https://file.example"], current.Endpoints);
    }

    [Fact]
    public void TryPatch_RejectsEmptyLogicalKey()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MODE", "environment");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new AppSettings
        {
            Mode = "file"
        };

        Assert.Throws<FormatException>(() => PatchLegacy(provider, "Production", string.Empty, current, out AppSettings? _));
    }

    [Fact]
    public void TryPatch_PatchesPublicWritableFields()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__MODE", "environment");
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__DATABASE__PORT", "6543");
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__READONLYMODE", "environment-readonly");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new FieldBackedOptions();
        current.Database.Host = "db.from.file";
        current.Database.Port = 5432;

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out FieldBackedOptions? value);

        Assert.True(patched);
        Assert.NotSame(current, value);
        Assert.NotNull(value);
        Assert.Equal("environment", value.Mode);
        Assert.Equal("file-readonly", value.ReadOnlyMode);
        Assert.Equal("db.from.file", value.Database.Host);
        Assert.Equal(6543, value.Database.Port);
    }

    [Fact]
    public void TryPatch_CreatesNullFieldChildWhenChildEnvironmentVariablesExist()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__DATABASE__PORT", "6543");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new NullableFieldBackedOptions();

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out NullableFieldBackedOptions? value);

        Assert.True(patched);
        Assert.NotSame(current, value);
        Assert.NotNull(value?.Database);
        Assert.Equal(6543, value.Database.Port);
    }

    [Fact]
    public void TryPatch_SkipsNullInterfaceChildProperty()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__CHILD__VALUE", "environment");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new InterfaceChildOptions();

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out InterfaceChildOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Null(current.Child);
    }

    [Fact]
    public void TryPatch_SkipsChildPropertyWhenConstructorThrows()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__CHILD__VALUE", "environment");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new ThrowingChildOptions();

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out ThrowingChildOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Null(current.Child);
    }

    [Fact]
    public void TryPatch_DoesNotRecurseThroughCycles()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__CHILD__NAME", "environment");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new CyclicOptions();

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out CyclicOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Equal("file", current.Name);
    }

    [Fact]
    public void TryPatch_DoesNotReplaceExistingValueWhenChildEnvironmentVariableIsInvalid()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__DATABASE__PORT", "not-a-port");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new AppSettings
        {
            Database = new DatabaseOptions
            {
                Host = "db.from.file",
                Port = 5432
            }
        };

        var patched = PatchLegacy(provider, "Production", "MyApp:Settings", current, out AppSettings? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Equal(5432, current.Database.Port);
    }

    [Fact]
    public void Resolve_ReturnsInvalidDiagnosticWhenDirectValueCannotConvert()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "PORT", "not-a-port");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var resolution = ((IConfigDiagnosticProvider)provider)
            .Resolve(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Port")), typeof(int), ConfigAuditSourceRole.Override);

        Assert.Equal(ConfigAuditEntryState.Invalid, resolution.State);
        Assert.Contains(resolution.Diagnostics, diagnostic => diagnostic.Code == "config-environment-conversion-failed");
        Assert.DoesNotContain("not-a-port", resolution.Diagnostics.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReadsIndexedCollectionsWithSourceDiagnostics()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "PRODUCTION__ITEMS__0", "first");
        SetSnapshotValue(innerProvider, "PRODUCTION__ITEMS__1", "second");
        SetSnapshotValue(innerProvider, "FALLBACKITEMS__0", "fallback");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var resolution = ((IConfigDiagnosticProvider)provider)
            .Resolve(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Items")), typeof(string[]), ConfigAuditSourceRole.Override);

        var values = Assert.IsType<string[]>(resolution.Value);
        Assert.Equal(["first", "second"], values);
        Assert.Equal(ConfigAuditEntryState.Resolved, resolution.State);
        Assert.All(resolution.Sources, source => Assert.Equal(ConfigAuditSourceKind.EnvironmentVariable, source.Kind));

        var fallbackResolution = ((IConfigDiagnosticProvider)provider)
            .Resolve(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("FallbackItems")), typeof(List<string>), ConfigAuditSourceRole.Override);
        var fallbackValues = Assert.IsType<List<string>>(fallbackResolution.Value);
        Assert.Equal(["fallback"], fallbackValues);
    }

    [Fact]
    public void Resolve_ReturnsInvalidWhenIndexedCollectionElementCannotConvert()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "ITEMS__0", "not-a-number");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var resolution = ((IConfigDiagnosticProvider)provider)
            .Resolve(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Items")), typeof(List<int>), ConfigAuditSourceRole.Override);

        Assert.Equal(ConfigAuditEntryState.Invalid, resolution.State);
        Assert.Contains(resolution.Diagnostics, diagnostic => diagnostic.ConfigPath == "Items:0");
    }

    [Fact]
    public void TracePatch_DoesNotReportSourceForReadableButUnpatchableMembers()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__MODE", "environment");
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__DATABASE__PORT", "6543");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var patch = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), null, typeof(GetterOnlyScalarWithWritableChildOptions));

        var value = Assert.IsType<GetterOnlyScalarWithWritableChildOptions>(patch.Value);
        Assert.True(patch.Patched);
        Assert.Equal("file", value.Mode);
        Assert.Equal(6543, value.Database.Port);
        Assert.DoesNotContain(patch.Sources, source => source.ConfigPath == "MyApp:Settings:Mode");
        Assert.Contains(patch.Sources, source => source.ConfigPath == "MyApp:Settings:Database:Port");
    }

    [Fact]
    public void TracePatch_CoversDiagnosticPatchBranchesWithoutMutatingInputs()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__0", "https://one.example");
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__1", "https://two.example");
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__DATABASE__PORT", "6543");
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__MODE", "environment");
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__CHILD__VALUE", "environment-child");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var simpleNull = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), null, typeof(string));
        Assert.False(simpleNull.Patched);

        var scalarRuntime = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), 42, typeof(object));
        Assert.False(scalarRuntime.Patched);

        var abstractType = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), null, typeof(AbstractPatchOptions));
        Assert.False(abstractType.Patched);

        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__CHILD__VALUE", null);
        var created = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), null, typeof(AppSettings));
        var createdValue = Assert.IsType<AppSettings>(created.Value);
        Assert.True(created.Patched);
        Assert.Equal(["https://one.example", "https://two.example"], createdValue.Endpoints);
        Assert.Equal(6543, createdValue.Database.Port);

        var getterOnly = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), null, typeof(GetterOnlyAppSettings));
        var getterOnlyValue = Assert.IsType<GetterOnlyAppSettings>(getterOnly.Value);
        Assert.True(getterOnly.Patched);
        Assert.Equal(["https://one.example", "https://two.example"], getterOnlyValue.Endpoints);

        var readOnlyCollection = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), null, typeof(GetterOnlyReadOnlyCollectionOptions));
        Assert.False(readOnlyCollection.Patched);

        var indexed = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), null, typeof(IndexedOptions));
        Assert.False(indexed.Patched);

        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__0", null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__1", null);
        var fieldBacked = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), null, typeof(FieldBackedOptions));
        var fieldBackedValue = Assert.IsType<FieldBackedOptions>(fieldBacked.Value);
        Assert.True(fieldBacked.Patched);
        Assert.Equal("environment", fieldBackedValue.Mode);
        Assert.Equal("file-readonly", fieldBackedValue.ReadOnlyMode);
        Assert.Equal(6543, fieldBackedValue.Database.Port);

        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__MODE", null);
        var nullableField = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), null, typeof(NullableFieldBackedOptions));
        var nullableFieldValue = Assert.IsType<NullableFieldBackedOptions>(nullableField.Value);
        Assert.True(nullableField.Patched);
        Assert.NotNull(nullableFieldValue.Database);
        Assert.Equal(6543, nullableFieldValue.Database.Port);

        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__DATABASE__PORT", null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__CHILD__VALUE", "environment-child");
        var interfaceChild = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), null, typeof(InterfaceChildOptions));
        Assert.False(interfaceChild.Patched);

        var throwingChild = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), null, typeof(ThrowingChildOptions));
        Assert.False(throwingChild.Patched);

        var createdCycle = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), null, typeof(CyclicOptions));
        Assert.False(createdCycle.Patched);

        var cyclic = new CyclicOptions();
        var cloneFailure = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), cyclic, typeof(CyclicOptions));
        Assert.False(cloneFailure.Patched);
        Assert.Contains(cloneFailure.Diagnostics, diagnostic => diagnostic.Code == "config-patch-clone-failed");
        Assert.Empty(cloneFailure.Facts);
    }

    [Fact]
    public void TracePatch_RecordsPerElementPriorPresenceForCollectionReplacement()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__0", "https://one.example");
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__1", "https://two.example");
        var current = new AppSettings
        {
            Endpoints = ["https://file.example"]
        };

        var provider = new EnvironmentConfigProvider(innerProvider);

        var patch = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), current, typeof(AppSettings));

        Assert.True(patch.Patched);
        var first = Assert.Single(patch.Facts, fact => fact.ConfigPath == "MyApp:Settings:Endpoints:0");
        var second = Assert.Single(patch.Facts, fact => fact.ConfigPath == "MyApp:Settings:Endpoints:1");
        Assert.Equal(ConfigAuditPriorPresence.Present, first.PriorPresence);
        Assert.Equal(ConfigAuditPriorPresence.Missing, second.PriorPresence);
        Assert.Equal(ConfigPatchProvenanceAction.ReplacedCollection, first.Action);
    }

    [Fact]
    public void TracePatch_RecordsEnvironmentScopedCollectionReplacementFacts()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "PRODUCTION__MYAPP__SETTINGS__ENDPOINTS__0", "https://one.example");
        SetSnapshotValue(innerProvider, "PRODUCTION__MYAPP__SETTINGS__ENDPOINTS__1", "https://two.example");
        var current = new AppSettings
        {
            Endpoints = ["https://file.example"]
        };

        var provider = new EnvironmentConfigProvider(innerProvider);

        var patch = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), current, typeof(AppSettings));

        Assert.True(patch.Patched);
        Assert.Equal(
            ConfigAuditPriorPresence.Present,
            patch.Facts.Single(fact => fact.ConfigPath == "MyApp:Settings:Endpoints:0").PriorPresence);
        Assert.Equal(
            ConfigAuditPriorPresence.Missing,
            patch.Facts.Single(fact => fact.ConfigPath == "MyApp:Settings:Endpoints:1").PriorPresence);
    }

    [Fact]
    public void TracePatch_RecordsUnknownPriorPresenceWhenProviderEvidenceCollectionIsNull()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__0", "https://one.example");
        var current = new AppSettings
        {
            Endpoints = null!
        };

        var provider = new EnvironmentConfigProvider(innerProvider);

        var patch = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), current, typeof(AppSettings));

        Assert.True(patch.Patched);
        var fact = Assert.Single(patch.Facts, fact => fact.ConfigPath == "MyApp:Settings:Endpoints:0");
        Assert.Equal(ConfigAuditPriorPresence.Unknown, fact.PriorPresence);
    }

    [Fact]
    public void TracePatch_RecordsUnknownPriorPresenceForSetOnlyCollectionProperty()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__0", "https://one.example");
        var current = new SetOnlyEndpointSettings();

        var provider = new EnvironmentConfigProvider(innerProvider);

        var patch = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), current, typeof(SetOnlyEndpointSettings));

        Assert.True(patch.Patched);
        var value = Assert.IsType<SetOnlyEndpointSettings>(patch.Value);
        Assert.Equal(["https://one.example"], value.WrittenEndpoints);
        var fact = Assert.Single(patch.Facts, fact => fact.ConfigPath == "MyApp:Settings:Endpoints:0");
        Assert.Equal(ConfigAuditPriorPresence.Unknown, fact.PriorPresence);
    }

    [Fact]
    public void TracePatch_TreatsConstructorDefaultCollectionAsMissingWhenRootWasMissing()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__0", "https://one.example");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var patch = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), null, typeof(ConstructorDefaultCollectionOptions));

        Assert.True(patch.Patched);
        var fact = Assert.Single(patch.Facts, fact => fact.ConfigPath == "MyApp:Settings:Endpoints:0");
        Assert.Equal(ConfigAuditPriorPresence.Missing, fact.PriorPresence);
    }

    [Fact]
    public void TracePatch_RecordsPerElementPriorPresenceForGetterOnlyCollectionPatch()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__0", "https://one.example");
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__1", "https://two.example");
        var current = new GetterOnlyAppSettings();
        current.Endpoints.Add("https://file.example");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var patch = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), current, typeof(GetterOnlyAppSettings));

        Assert.True(patch.Patched);
        var patchedValue = Assert.IsType<GetterOnlyAppSettings>(patch.Value);
        Assert.Equal(["https://one.example", "https://two.example"], patchedValue.Endpoints);
        Assert.Equal(ConfigPatchProvenanceAction.PatchedExistingCollection, patch.Facts[0].Action);
        Assert.Equal(ConfigAuditPriorPresence.Present, patch.Facts.Single(fact => fact.ConfigPath == "MyApp:Settings:Endpoints:0").PriorPresence);
        Assert.Equal(ConfigAuditPriorPresence.Missing, patch.Facts.Single(fact => fact.ConfigPath == "MyApp:Settings:Endpoints:1").PriorPresence);
    }

    [Fact]
    public void TracePatch_RecordsArrayPriorPresenceForCollectionReplacement()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__0", "https://one.example");
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__1", "https://two.example");
        var current = new ArrayEndpointSettings
        {
            Endpoints = ["https://file.example"]
        };

        var provider = new EnvironmentConfigProvider(innerProvider);

        var patch = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), current, typeof(ArrayEndpointSettings));

        Assert.True(patch.Patched);
        Assert.Equal(ConfigAuditPriorPresence.Present, patch.Facts.Single(fact => fact.ConfigPath == "MyApp:Settings:Endpoints:0").PriorPresence);
        Assert.Equal(ConfigAuditPriorPresence.Missing, patch.Facts.Single(fact => fact.ConfigPath == "MyApp:Settings:Endpoints:1").PriorPresence);
    }

    [Fact]
    public void TracePatch_RecordsReadOnlyCollectionPriorPresenceForCollectionReplacement()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__0", "https://one.example");
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__1", "https://two.example");
        var current = new ReadOnlyEndpointSettings
        {
            Endpoints = new ReadOnlyEndpointList("https://file.example")
        };

        var provider = new EnvironmentConfigProvider(innerProvider);

        var patch = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), current, typeof(ReadOnlyEndpointSettings));

        Assert.True(patch.Patched);
        Assert.Equal(ConfigAuditPriorPresence.Present, patch.Facts.Single(fact => fact.ConfigPath == "MyApp:Settings:Endpoints:0").PriorPresence);
        Assert.Equal(ConfigAuditPriorPresence.Missing, patch.Facts.Single(fact => fact.ConfigPath == "MyApp:Settings:Endpoints:1").PriorPresence);
    }

    [Fact]
    public void TracePatch_SkipsGetterOnlyMultiDimensionalArrayCollection()
    {
        var innerProvider = SnapshotFake();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        SetSnapshotValue(innerProvider, "MYAPP__SETTINGS__ENDPOINTS__0", "https://one.example");
        var current = new MultiDimensionalArrayEndpointSettings();

        var provider = new EnvironmentConfigProvider(innerProvider);

        var patch = ((IConfigDiagnosticPatcher)provider)
            .TracePatch(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("MyApp:Settings")), current, typeof(MultiDimensionalArrayEndpointSettings));

        Assert.False(patch.Patched);
        Assert.Empty(patch.Facts);
    }

    private static bool PatchLegacy<T>(EnvironmentConfigProvider provider, string environment, string key, T? currentValue, out T? patchedValue)
    {
        var request = new ConfigProviderRequest(environment, AppSurfaceConfigKey.Parse(key));
        var result = ((IConfigValuePatcher)provider).Patch(request, currentValue);
        patchedValue = result.Value;
        return result.Status == ConfigPatchStatus.Applied;
    }

    private static IEnvironmentProvider SnapshotFake()
    {
        var fake = A.Fake<IEnvironmentProvider>();
        var values = SnapshotValues.GetOrCreateValue(fake);
        A.CallTo(() => fake.Environment).Returns("Production");
        A.CallTo(() => fake.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => fake.CaptureEnvironmentVariables()).ReturnsLazily(() =>
            new Dictionary<string, string>(values, StringComparer.Ordinal));
        return fake;
    }

    private static void SetSnapshotValue(IEnvironmentProvider provider, string name, string? value)
    {
        var values = SnapshotValues.GetOrCreateValue(provider);
        if (value is null)
        {
            values.Remove(name);
        }
        else
        {
            values[name] = value;
        }
    }

    private sealed class AppSettings
    {
        public string? Mode { get; set; }

        public DatabaseOptions Database { get; set; } = new();

        public List<string> Endpoints { get; set; } = [];
    }

    private sealed class GetterOnlyAppSettings
    {
        public string? Mode { get; set; }

        public DatabaseOptions Database { get; } = new();

        public List<string> Endpoints { get; } = [];
    }

    private sealed class ConstructorDefaultCollectionOptions
    {
        public List<string> Endpoints { get; set; } = ["https://constructor.example"];
    }

    private sealed class ArrayEndpointSettings
    {
        public string[] Endpoints { get; set; } = [];
    }

    private sealed class ReadOnlyEndpointSettings
    {
        public IReadOnlyList<string> Endpoints { get; set; } = [];
    }

    private sealed class SetOnlyEndpointSettings
    {
        public List<string>? WrittenEndpoints { get; private set; }

        public List<string> Endpoints
        {
            set => WrittenEndpoints = value;
        }
    }

    private sealed class MultiDimensionalArrayEndpointSettings
    {
        public string[,] Endpoints { get; } = new string[1, 1];
    }

    private sealed class ReadOnlyEndpointList : IReadOnlyList<string>
    {
        private readonly string[] _values;

        public ReadOnlyEndpointList(params string[] values)
        {
            _values = values;
        }

        public string this[int index] => _values[index];

        public int Count => _values.Length;

        public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_values).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private interface IConfigPatchContract
    {
        string? Value { get; set; }
    }

    private sealed class IndexedOptions
    {
        public string this[int index]
        {
            get => "file";
            set { }
        }
    }

    private sealed class GetterOnlyScalarOptions
    {
        public string Mode { get; } = "file";
    }

    private sealed class GetterOnlyScalarWithWritableChildOptions
    {
        public string Mode { get; } = "file";

        public DatabaseOptions Database { get; set; } = new();
    }

    private sealed class PrivateSetterOptions
    {
        public string Mode { get; private set; } = "file";
    }

    private sealed class NullGetterOnlyOptions
    {
        public DatabaseOptions? Database { get; }
    }

    private sealed class PrivateSetterChildOptions
    {
        public DatabaseOptions? Database { get; private set; }
    }

    private sealed class GetterOnlyReadOnlyCollectionOptions
    {
        public IList<string> Endpoints { get; } = Array.AsReadOnly(["https://file.example"]);
    }

    private sealed class FieldBackedOptions
    {
        public string? Mode = "file";

        public readonly string ReadOnlyMode = "file-readonly";

        public DatabaseOptions Database = new();
    }

    private sealed class NullableFieldBackedOptions
    {
        public NullableFieldBackedOptions()
        {
            Database = null;
        }

        public DatabaseOptions? Database;
    }

    private sealed class InterfaceChildOptions
    {
        public IConfigPatchContract? Child { get; set; }
    }

    private sealed class ThrowingChildOptions
    {
        public ThrowingChild? Child { get; set; }
    }

    private sealed class ThrowingChild
    {
        public ThrowingChild()
        {
            throw new InvalidOperationException("Constructor should be treated as unpatchable.");
        }

        public string? Value { get; set; }
    }

    private sealed class CyclicOptions
    {
        public CyclicOptions()
        {
            Child = this;
        }

        public string Name { get; set; } = "file";

        public CyclicOptions Child { get; set; }
    }

    private sealed class DatabaseOptions
    {
        public string? Host { get; set; }

        public int Port { get; set; }
    }

    private abstract class AbstractPatchOptions
    {
        public string? Value { get; set; }
    }
}
