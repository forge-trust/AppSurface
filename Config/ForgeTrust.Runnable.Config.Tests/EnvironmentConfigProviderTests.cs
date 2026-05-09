using FakeItEasy;
using ForgeTrust.Runnable.Core;

namespace ForgeTrust.Runnable.Config.Tests;

public class EnvironmentConfigProviderTests
{
    [Fact]
    public void GetValue_UsesEnvironmentSpecificVariableFirst()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_FEATURE_ENABLED", A<string?>._))
            .Returns("true");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = provider.GetValue<bool>("Production", "Feature.Enabled");

        Assert.True(value);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_FEATURE_ENABLED", A<string?>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("FEATURE_ENABLED", A<string?>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public void GetValue_FallsBackToKeyWhenEnvironmentSpecificVariableMissing()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("DEV_US_SECTION_VALUE", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("SECTION_VALUE", A<string?>._))
            .Returns("42");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = provider.GetValue<int>("Dev-Us", "Section.Value");

        Assert.Equal(42, value);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("DEV_US_SECTION_VALUE", A<string?>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("SECTION_VALUE", A<string?>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void GetValue_ParsesEnumValuesCaseInsensitive()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_DAY", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("DAY", A<string?>._))
            .Returns("monday");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = provider.GetValue<DayOfWeek>("Production", "Day");

        Assert.Equal(DayOfWeek.Monday, value);
    }

    [Fact]
    public void GetValue_HandlesNullableTypes()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        var provider = new EnvironmentConfigProvider(innerProvider);

        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_A", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("A", A<string?>._)).Returns("123");
        Assert.Equal(123, provider.GetValue<int?>("Production", "A"));

        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_B", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("B", A<string?>._)).Returns("");
        Assert.Null(provider.GetValue<int?>("Production", "B"));

        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_C", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("C", A<string?>._)).Returns(null);
        Assert.Null(provider.GetValue<int?>("Production", "C"));
    }

    [Fact]
    public void GetValue_ReturnsDefaultOnInvalidFormat()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("VALUE", A<string?>._))
            .Returns("not-a-number");

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Equal(0, provider.GetValue<int>("Production", "Value"));
    }

    [Fact]
    public void GetValue_ReturnsDefaultOnOverflow()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("VALUE", A<string?>._))
            .Returns(long.MaxValue.ToString());

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Equal(0, provider.GetValue<int>("Production", "Value"));
    }

    [Fact]
    public void Properties_AreProxiedToInnerProvider()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.Environment).Returns("Test");
        A.CallTo(() => innerProvider.IsDevelopment).Returns(true);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("ANY", A<string?>._))
            .Returns("value");

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Equal("Test", provider.Environment);
        Assert.True(provider.IsDevelopment);
        Assert.Equal("value", provider.GetEnvironmentVariable("ANY"));
    }

    [Fact]
    public void GetValue_ReturnsDefaultOnInvalidEnum()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("VALUE", A<string?>._))
            .Returns("InvalidEnumValue");

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Equal(default(System.UriKind), provider.GetValue<System.UriKind>("Production", "Value"));
    }

    [Fact]
    public void GetValue_BindsTopLevelListFromJsonValue()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP_ITEMS", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("ITEMS", A<string?>._)).Returns("""["a","b"]""");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = provider.GetValue<List<string>>("MyApp", "Items");

        Assert.NotNull(value);
        Assert.Equal(["a", "b"], value);
    }

    [Fact]
    public void GetValue_BindsTopLevelDictionaryFromJsonValue()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_SETTINGS", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("SETTINGS", A<string?>._))
            .Returns("""{"Retries":3,"Timeout":30}""");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = provider.GetValue<Dictionary<string, int>>("Production", "Settings");

        Assert.NotNull(value);
        Assert.Equal(3, value["Retries"]);
        Assert.Equal(30, value["Timeout"]);
    }

    [Fact]
    public void GetValue_BindsIndexedListFromDoubleUnderscoreVariables()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_MYAPP_ITEMS", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP_ITEMS", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__MYAPP__ITEMS", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__ITEMS", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__MYAPP__ITEMS__0", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__ITEMS__0", A<string?>._))
            .Returns("First");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__ITEMS__1", A<string?>._))
            .Returns("Second");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__ITEMS__2", A<string?>._))
            .Returns(null);

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = provider.GetValue<List<string>>("Production", "MyApp.Items");

        Assert.NotNull(value);
        Assert.Equal(["First", "Second"], value);
    }

    [Fact]
    public void GetValue_BindsEnvScopedIndexedListFromDoubleUnderscoreVariables()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_MYAPP_ITEMS", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP_ITEMS", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__MYAPP__ITEMS", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__ITEMS", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__MYAPP__ITEMS__0", A<string?>._))
            .Returns("First");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__MYAPP__ITEMS__1", A<string?>._))
            .Returns("Second");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__MYAPP__ITEMS__2", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__ITEMS__0", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__ITEMS__1", A<string?>._))
            .Returns(null);

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = provider.GetValue<List<string>>("Production", "MyApp.Items");

        Assert.NotNull(value);
        Assert.Equal(["First", "Second"], value);
    }

    [Fact]
    public void GetValue_ContinuesToNextCandidateWhenEarlierValueIsUnparseable()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_VALUE", A<string?>._))
            .Returns("not-a-number");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("VALUE", A<string?>._))
            .Returns("123");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = provider.GetValue<int>("Production", "Value");

        Assert.Equal(123, value);
    }

    [Fact]
    public void GetValue_DeduplicatesDirectCandidatesWhenKeyHasNoSeparators()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_ITEMS", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("ITEMS", A<string?>._))
            .Returns("not-json");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__ITEMS", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__ITEMS__0", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("ITEMS__0", A<string?>._))
            .Returns(null);

        var provider = new EnvironmentConfigProvider(innerProvider);
        var value = provider.GetValue<Dictionary<string, int>>("Production", "Items");

        Assert.Null(value);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("ITEMS", A<string?>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void GetValue_ParsesGuid()
    {
        var expected = Guid.NewGuid();
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_ID", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("ID", A<string?>._))
            .Returns(expected.ToString("D"));

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = provider.GetValue<Guid>("Production", "Id");

        Assert.Equal(expected, value);
    }

    [Fact]
    public void GetValue_ReturnsDefaultOnInvalidGuid()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_ID", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("ID", A<string?>._))
            .Returns("not-a-guid");

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Equal(Guid.Empty, provider.GetValue<Guid>("Production", "Id"));
    }

    [Fact]
    public void GetValue_ParsesDateTime()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_WHEN", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("WHEN", A<string?>._))
            .Returns("2026-02-13T12:34:56Z");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = provider.GetValue<DateTime>("Production", "When");

        Assert.Equal(new DateTime(2026, 2, 13, 12, 34, 56, DateTimeKind.Utc), value.ToUniversalTime());
    }

    [Fact]
    public void GetValue_ParsesDateTimeOffset()
    {
        var expected = DateTimeOffset.Parse("2026-02-13T12:34:56+00:00");
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_WHEN_OFFSET", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("WHEN_OFFSET", A<string?>._))
            .Returns("2026-02-13T12:34:56+00:00");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = provider.GetValue<DateTimeOffset>("Production", "When.Offset");

        Assert.Equal(expected, value);
    }

    [Fact]
    public void GetValue_ParsesTimeSpan()
    {
        var expected = TimeSpan.FromMinutes(90);
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_DURATION", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("DURATION", A<string?>._))
            .Returns("01:30:00");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = provider.GetValue<TimeSpan>("Production", "Duration");

        Assert.Equal(expected, value);
    }

    [Fact]
    public void GetValue_ParsesDecimalWithInvariantCulture()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_RATE", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("RATE", A<string?>._))
            .Returns("12.34");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = provider.GetValue<decimal>("Production", "Rate");

        Assert.Equal(12.34m, value);
    }

    [Fact]
    public void GetValue_HandlesNullableGuidEmptyString()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_OPTIONAL_ID", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("OPTIONAL_ID", A<string?>._))
            .Returns(string.Empty);

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Null(provider.GetValue<Guid?>("Production", "Optional.Id"));
    }

    [Fact]
    public void GetValue_BindsIndexedArrayFromDoubleUnderscoreVariables()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_MYAPP_VALUES", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP_VALUES", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__MYAPP__VALUES", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__VALUES", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__MYAPP__VALUES__0", A<string?>._))
            .Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__VALUES__0", A<string?>._))
            .Returns("A");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__VALUES__1", A<string?>._))
            .Returns("B");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__VALUES__2", A<string?>._))
            .Returns(null);

        var provider = new EnvironmentConfigProvider(innerProvider);

        var value = provider.GetValue<string[]>("Production", "MyApp.Values");

        Assert.NotNull(value);
        Assert.Equal(["A", "B"], value);
    }

    [Fact]
    public void GetValue_ReturnsDefaultOnUnsupportedJsonType()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_VALUE", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("VALUE", A<string?>._))
            .Returns("\"System.String\"");

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Null(provider.GetValue<Type>("Production", "Value"));
    }

    [Fact]
    public void GetValue_ReturnsDefaultOnInvalidDateTimeOffset()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_WHEN_OFFSET", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("WHEN_OFFSET", A<string?>._))
            .Returns("not-a-datetimeoffset");

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Equal(default(DateTimeOffset), provider.GetValue<DateTimeOffset>("Production", "When.Offset"));
    }

    [Fact]
    public void GetValue_ReturnsDefaultOnInvalidTimeSpan()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_DURATION", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("DURATION", A<string?>._))
            .Returns("not-a-timespan");

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Equal(default(TimeSpan), provider.GetValue<TimeSpan>("Production", "Duration"));
    }

    [Fact]
    public void GetValue_ReturnsDefaultWhenIndexedCollectionContainsInvalidElement()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION_MYAPP_VALUES", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP_VALUES", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__MYAPP__VALUES", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__VALUES", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__MYAPP__VALUES__0", A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__VALUES__0", A<string?>._)).Returns("1");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__VALUES__1", A<string?>._)).Returns("not-an-int");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__VALUES__2", A<string?>._)).Returns(null);

        var provider = new EnvironmentConfigProvider(innerProvider);

        Assert.Null(provider.GetValue<List<int>>("Production", "MyApp.Values"));
    }

    [Fact]
    public void TryPatch_PatchesNestedObjectFromDoubleUnderscoreVariables()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__DATABASE__PORT", A<string?>._))
            .Returns("6543");

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

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out AppSettings? value);

        Assert.True(patched);
        Assert.Same(current, value);
        Assert.NotNull(value);
        Assert.Equal("file", value.Mode);
        Assert.Equal("db.from.file", value.Database.Host);
        Assert.Equal(6543, value.Database.Port);
    }

    [Fact]
    public void TryPatch_CreatesNestedObjectWhenOnlyChildEnvironmentVariablesExist()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__MYAPP__SETTINGS__MODE", A<string?>._))
            .Returns("env");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__MYAPP__SETTINGS__DATABASE__HOST", A<string?>._))
            .Returns("db.from.env");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var patched = provider.TryPatch<AppSettings>("Production", "MyApp.Settings", null, out var value);

        Assert.True(patched);
        Assert.NotNull(value);
        Assert.Equal("env", value.Mode);
        Assert.NotNull(value.Database);
        Assert.Equal("db.from.env", value.Database.Host);
    }

    [Fact]
    public void TryPatch_PatchesIndexedCollectionMember()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__ENDPOINTS__0", A<string?>._))
            .Returns("https://one.example");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__ENDPOINTS__1", A<string?>._))
            .Returns("https://two.example");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new AppSettings();

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out AppSettings? value);

        Assert.True(patched);
        Assert.NotNull(value);
        Assert.Equal(["https://one.example", "https://two.example"], value.Endpoints);
    }

    [Fact]
    public void TryPatch_PatchesExistingGetterOnlyNestedObject()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__DATABASE__PORT", A<string?>._))
            .Returns("6543");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new GetterOnlyAppSettings
        {
            Mode = "file"
        };
        current.Database.Host = "db.from.file";
        current.Database.Port = 5432;

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out GetterOnlyAppSettings? value);

        Assert.True(patched);
        Assert.Same(current, value);
        Assert.NotNull(value);
        Assert.Equal("file", value.Mode);
        Assert.Equal("db.from.file", value.Database.Host);
        Assert.Equal(6543, value.Database.Port);
    }

    [Fact]
    public void TryPatch_PatchesExistingGetterOnlyCollection()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__ENDPOINTS__0", A<string?>._))
            .Returns("https://one.example");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__ENDPOINTS__1", A<string?>._))
            .Returns("https://two.example");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new GetterOnlyAppSettings();
        current.Endpoints.Add("https://file.example");

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out GetterOnlyAppSettings? value);

        Assert.True(patched);
        Assert.Same(current, value);
        Assert.NotNull(value);
        Assert.Equal(["https://one.example", "https://two.example"], value.Endpoints);
    }

    [Fact]
    public void TryPatch_PatchesExistingGetterOnlyCollectionFromEnvironmentScopedVariables()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__MYAPP__SETTINGS__ENDPOINTS__0", A<string?>._))
            .Returns("https://one.example");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__MYAPP__SETTINGS__ENDPOINTS__1", A<string?>._))
            .Returns("https://two.example");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new GetterOnlyAppSettings();
        current.Endpoints.Add("https://file.example");

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out GetterOnlyAppSettings? value);

        Assert.True(patched);
        Assert.NotNull(value);
        Assert.Equal(["https://one.example", "https://two.example"], value.Endpoints);
    }

    [Fact]
    public void TryPatch_DoesNotPatchScalarTopLevelValue()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        var provider = new EnvironmentConfigProvider(innerProvider);

        var patched = provider.TryPatch<string>("Production", "MyApp.Settings", null, out var value);

        Assert.False(patched);
        Assert.Null(value);
    }

    [Fact]
    public void TryPatch_DoesNotPatchNullableScalarTopLevelValue()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        var provider = new EnvironmentConfigProvider(innerProvider);

        var patched = provider.TryPatch<int?>("Production", "MyApp.Settings", null, out var value);

        Assert.False(patched);
        Assert.Null(value);
    }

    [Fact]
    public void TryPatch_DoesNotPatchTopLevelRuntimeScalarValue()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        var provider = new EnvironmentConfigProvider(innerProvider);

        var patched = provider.TryPatch<object>("Production", "MyApp.Settings", "file", out var value);

        Assert.False(patched);
        Assert.Null(value);
    }

    [Fact]
    public void TryPatch_DoesNotCreateTopLevelInterfaceValue()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        var provider = new EnvironmentConfigProvider(innerProvider);

        var patched = provider.TryPatch<IConfigPatchContract>("Production", "MyApp.Settings", null, out var value);

        Assert.False(patched);
        Assert.Null(value);
    }

    [Fact]
    public void TryPatch_SkipsIndexerProperties()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new IndexedOptions();

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out IndexedOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Equal("file", current[0]);
    }

    [Fact]
    public void TryPatch_DoesNotPatchGetterOnlyScalarProperty()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__MODE", A<string?>._))
            .Returns("environment");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new GetterOnlyScalarOptions();

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out GetterOnlyScalarOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Equal("file", current.Mode);
    }

    [Fact]
    public void TryPatch_DoesNotPatchPrivateSetterScalarProperty()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__MODE", A<string?>._))
            .Returns("environment");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new PrivateSetterOptions();

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out PrivateSetterOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Equal("file", current.Mode);
    }

    [Fact]
    public void TryPatch_DoesNotAttachNullGetterOnlyNestedObject()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__DATABASE__PORT", A<string?>._))
            .Returns("6543");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new NullGetterOnlyOptions();

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out NullGetterOnlyOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Null(current.Database);
    }

    [Fact]
    public void TryPatch_DoesNotUsePrivateSetterToAttachNestedObject()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__DATABASE__PORT", A<string?>._))
            .Returns("6543");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new PrivateSetterChildOptions();

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out PrivateSetterChildOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Null(current.Database);
    }

    [Fact]
    public void TryPatch_DoesNotPatchGetterOnlyReadOnlyCollection()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__ENDPOINTS__0", A<string?>._))
            .Returns("https://one.example");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new GetterOnlyReadOnlyCollectionOptions();

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out GetterOnlyReadOnlyCollectionOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Equal(["https://file.example"], current.Endpoints);
    }

    [Fact]
    public void TryPatch_PatchesRootMemberWhenKeyIsEmpty()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MODE", A<string?>._))
            .Returns("environment");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new AppSettings
        {
            Mode = "file"
        };

        var patched = provider.TryPatch("Production", string.Empty, current, out AppSettings? value);

        Assert.True(patched);
        Assert.Same(current, value);
        Assert.Equal("environment", value?.Mode);
    }

    [Fact]
    public void TryPatch_PatchesPublicWritableFields()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__MODE", A<string?>._))
            .Returns("environment");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__DATABASE__PORT", A<string?>._))
            .Returns("6543");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__READ_ONLY_MODE", A<string?>._))
            .Returns("environment-readonly");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new FieldBackedOptions();
        current.Database.Host = "db.from.file";
        current.Database.Port = 5432;

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out FieldBackedOptions? value);

        Assert.True(patched);
        Assert.Same(current, value);
        Assert.NotNull(value);
        Assert.Equal("environment", value.Mode);
        Assert.Equal("file-readonly", value.ReadOnlyMode);
        Assert.Equal("db.from.file", value.Database.Host);
        Assert.Equal(6543, value.Database.Port);
    }

    [Fact]
    public void TryPatch_CreatesNullFieldChildWhenChildEnvironmentVariablesExist()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__DATABASE__PORT", A<string?>._))
            .Returns("6543");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new NullableFieldBackedOptions();

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out NullableFieldBackedOptions? value);

        Assert.True(patched);
        Assert.Same(current, value);
        Assert.NotNull(value?.Database);
        Assert.Equal(6543, value.Database.Port);
    }

    [Fact]
    public void TryPatch_SkipsNullInterfaceChildProperty()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__CHILD__VALUE", A<string?>._))
            .Returns("environment");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new InterfaceChildOptions();

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out InterfaceChildOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Null(current.Child);
    }

    [Fact]
    public void TryPatch_SkipsChildPropertyWhenConstructorThrows()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__CHILD__VALUE", A<string?>._))
            .Returns("environment");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new ThrowingChildOptions();

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out ThrowingChildOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Null(current.Child);
    }

    [Fact]
    public void TryPatch_DoesNotRecurseThroughCycles()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__CHILD__NAME", A<string?>._))
            .Returns("environment");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new CyclicOptions();

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out CyclicOptions? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Equal("file", current.Name);
    }

    [Fact]
    public void TryPatch_DoesNotReplaceExistingValueWhenChildEnvironmentVariableIsInvalid()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__DATABASE__PORT", A<string?>._))
            .Returns("not-a-port");

        var provider = new EnvironmentConfigProvider(innerProvider);
        var current = new AppSettings
        {
            Database = new DatabaseOptions
            {
                Host = "db.from.file",
                Port = 5432
            }
        };

        var patched = provider.TryPatch("Production", "MyApp.Settings", current, out AppSettings? value);

        Assert.False(patched);
        Assert.Null(value);
        Assert.Equal(5432, current.Database.Port);
    }

    [Fact]
    public void Resolve_ReturnsInvalidDiagnosticWhenDirectValueCannotConvert()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PORT", A<string?>._))
            .Returns("not-a-port");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var resolution = ((IConfigDiagnosticProvider)provider)
            .Resolve("Production", "Port", typeof(int), ConfigAuditSourceRole.Override);

        Assert.Equal(ConfigAuditEntryState.Invalid, resolution.State);
        Assert.Contains(resolution.Diagnostics, diagnostic => diagnostic.Code == "config-environment-conversion-failed");
        Assert.DoesNotContain("not-a-port", resolution.Diagnostics.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReadsIndexedCollectionsWithSourceDiagnostics()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__ITEMS__0", A<string?>._))
            .Returns("first");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("PRODUCTION__ITEMS__1", A<string?>._))
            .Returns("second");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("FALLBACKITEMS__0", A<string?>._))
            .Returns("fallback");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var resolution = ((IConfigDiagnosticProvider)provider)
            .Resolve("Production", "Items", typeof(string[]), ConfigAuditSourceRole.Override);

        var values = Assert.IsType<string[]>(resolution.Value);
        Assert.Equal(["first", "second"], values);
        Assert.Equal(ConfigAuditEntryState.Resolved, resolution.State);
        Assert.All(resolution.Sources, source => Assert.Equal(ConfigAuditSourceKind.EnvironmentVariable, source.Kind));

        var fallbackResolution = ((IConfigDiagnosticProvider)provider)
            .Resolve("Production", "FallbackItems", typeof(List<string>), ConfigAuditSourceRole.Override);
        var fallbackValues = Assert.IsType<List<string>>(fallbackResolution.Value);
        Assert.Equal(["fallback"], fallbackValues);
    }

    [Fact]
    public void Resolve_ReturnsInvalidWhenIndexedCollectionElementCannotConvert()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("ITEMS__0", A<string?>._))
            .Returns("not-a-number");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var resolution = ((IConfigDiagnosticProvider)provider)
            .Resolve("Production", "Items", typeof(List<int>), ConfigAuditSourceRole.Override);

        Assert.Equal(ConfigAuditEntryState.Invalid, resolution.State);
        Assert.Contains(resolution.Diagnostics, diagnostic => diagnostic.ConfigPath == "Items.0");
    }

    [Fact]
    public void TracePatch_CoversDiagnosticPatchBranchesWithoutMutatingInputs()
    {
        var innerProvider = A.Fake<IEnvironmentProvider>();
        A.CallTo(() => innerProvider.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__ENDPOINTS__0", A<string?>._))
            .Returns("https://one.example");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__ENDPOINTS__1", A<string?>._))
            .Returns("https://two.example");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__DATABASE__PORT", A<string?>._))
            .Returns("6543");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__MODE", A<string?>._))
            .Returns("environment");
        A.CallTo(() => innerProvider.GetEnvironmentVariable("MYAPP__SETTINGS__CHILD__VALUE", A<string?>._))
            .Returns("environment-child");

        var provider = new EnvironmentConfigProvider(innerProvider);

        var simpleNull = ((IConfigDiagnosticPatcher)provider)
            .TracePatch("Production", "MyApp.Settings", null, typeof(string));
        Assert.False(simpleNull.Patched);

        var scalarRuntime = ((IConfigDiagnosticPatcher)provider)
            .TracePatch("Production", "MyApp.Settings", 42, typeof(object));
        Assert.False(scalarRuntime.Patched);

        var abstractType = ((IConfigDiagnosticPatcher)provider)
            .TracePatch("Production", "MyApp.Settings", null, typeof(AbstractPatchOptions));
        Assert.False(abstractType.Patched);

        var created = ((IConfigDiagnosticPatcher)provider)
            .TracePatch("Production", "MyApp.Settings", null, typeof(AppSettings));
        var createdValue = Assert.IsType<AppSettings>(created.Value);
        Assert.True(created.Patched);
        Assert.Equal(["https://one.example", "https://two.example"], createdValue.Endpoints);
        Assert.Equal(6543, createdValue.Database.Port);

        var getterOnly = ((IConfigDiagnosticPatcher)provider)
            .TracePatch("Production", "MyApp.Settings", null, typeof(GetterOnlyAppSettings));
        var getterOnlyValue = Assert.IsType<GetterOnlyAppSettings>(getterOnly.Value);
        Assert.True(getterOnly.Patched);
        Assert.Equal(["https://one.example", "https://two.example"], getterOnlyValue.Endpoints);

        var readOnlyCollection = ((IConfigDiagnosticPatcher)provider)
            .TracePatch("Production", "MyApp.Settings", null, typeof(GetterOnlyReadOnlyCollectionOptions));
        Assert.False(readOnlyCollection.Patched);

        var indexed = ((IConfigDiagnosticPatcher)provider)
            .TracePatch("Production", "MyApp.Settings", null, typeof(IndexedOptions));
        Assert.False(indexed.Patched);

        var fieldBacked = ((IConfigDiagnosticPatcher)provider)
            .TracePatch("Production", "MyApp.Settings", null, typeof(FieldBackedOptions));
        var fieldBackedValue = Assert.IsType<FieldBackedOptions>(fieldBacked.Value);
        Assert.True(fieldBacked.Patched);
        Assert.Equal("environment", fieldBackedValue.Mode);
        Assert.Equal("file-readonly", fieldBackedValue.ReadOnlyMode);
        Assert.Equal(6543, fieldBackedValue.Database.Port);

        var nullableField = ((IConfigDiagnosticPatcher)provider)
            .TracePatch("Production", "MyApp.Settings", null, typeof(NullableFieldBackedOptions));
        var nullableFieldValue = Assert.IsType<NullableFieldBackedOptions>(nullableField.Value);
        Assert.True(nullableField.Patched);
        Assert.NotNull(nullableFieldValue.Database);
        Assert.Equal(6543, nullableFieldValue.Database.Port);

        var interfaceChild = ((IConfigDiagnosticPatcher)provider)
            .TracePatch("Production", "MyApp.Settings", null, typeof(InterfaceChildOptions));
        Assert.False(interfaceChild.Patched);

        var throwingChild = ((IConfigDiagnosticPatcher)provider)
            .TracePatch("Production", "MyApp.Settings", null, typeof(ThrowingChildOptions));
        Assert.False(throwingChild.Patched);

        var createdCycle = ((IConfigDiagnosticPatcher)provider)
            .TracePatch("Production", "MyApp.Settings", null, typeof(CyclicOptions));
        Assert.False(createdCycle.Patched);

        var cyclic = new CyclicOptions();
        var cloneFailure = ((IConfigDiagnosticPatcher)provider)
            .TracePatch("Production", "MyApp.Settings", cyclic, typeof(CyclicOptions));
        Assert.False(cloneFailure.Patched);
        Assert.Contains(cloneFailure.Diagnostics, diagnostic => diagnostic.Code == "config-patch-clone-failed");
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
