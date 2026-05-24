namespace ForgeTrust.AppSurface.Config.Tests;

public class ConfigAuditModelsTests
{
    [Fact]
    public void ConfigAuditKnownEntry_RejectsInvalidConstructorArguments()
    {
        Assert.Throws<ArgumentException>(() => new ConfigAuditKnownEntry("", null, typeof(string)));
        Assert.Throws<ArgumentNullException>(() => new ConfigAuditKnownEntry("Valid.Key", null, null!));
    }

    [Fact]
    public void ConfigAuditKnownEntry_SnapshotsOptions()
    {
        var options = new ConfigAuditEntryOptions
        {
            TraverseCollectionElements = true,
            MaxCollectionDepth = 2,
            MaxCollectionElements = 3,
            MaxReportNodes = 4,
            DisplayDictionaryKeys = false
        };

        var entry = new ConfigAuditKnownEntry("Valid.Key", null, typeof(string), options);
        options.TraverseCollectionElements = false;

        var snapshot = entry.Options;
        snapshot.MaxCollectionDepth = 99;

        Assert.True(entry.Options.TraverseCollectionElements);
        Assert.Equal(2, entry.Options.MaxCollectionDepth);
        Assert.Equal(3, entry.Options.MaxCollectionElements);
        Assert.Equal(4, entry.Options.MaxReportNodes);
        Assert.False(entry.Options.DisplayDictionaryKeys);
    }

    [Fact]
    public void ConfigAuditEntryOptions_DefaultsPreserveOpaqueCollectionBehavior()
    {
        var options = new ConfigAuditEntryOptions();

        Assert.False(options.TraverseCollectionElements);
        Assert.Equal(4, options.MaxCollectionDepth);
        Assert.Equal(128, options.MaxCollectionElements);
        Assert.Equal(4096, options.MaxReportNodes);
        Assert.True(options.DisplayDictionaryKeys);
    }

    [Fact]
    public void PublicEnums_KeepStableOrdinals()
    {
        Assert.Equal(0, (int)ConfigAuditEntryState.Resolved);
        Assert.Equal(1, (int)ConfigAuditEntryState.PartiallyResolved);
        Assert.Equal(2, (int)ConfigAuditEntryState.Defaulted);
        Assert.Equal(3, (int)ConfigAuditEntryState.Missing);
        Assert.Equal(4, (int)ConfigAuditEntryState.Invalid);

        Assert.Equal(0, (int)ConfigAuditElementKind.ArrayItem);
        Assert.Equal(1, (int)ConfigAuditElementKind.ListItem);
        Assert.Equal(2, (int)ConfigAuditElementKind.DictionaryItem);

        Assert.Equal(0, (int)ConfigAuditSourceKind.Provider);
        Assert.Equal(1, (int)ConfigAuditSourceKind.File);
        Assert.Equal(2, (int)ConfigAuditSourceKind.EnvironmentVariable);
        Assert.Equal(3, (int)ConfigAuditSourceKind.Default);
        Assert.Equal(4, (int)ConfigAuditSourceKind.Missing);

        Assert.Equal(0, (int)ConfigAuditSourceRole.Base);
        Assert.Equal(1, (int)ConfigAuditSourceRole.Override);
        Assert.Equal(2, (int)ConfigAuditSourceRole.Patch);
        Assert.Equal(3, (int)ConfigAuditSourceRole.Fallback);

        Assert.Equal(0, (int)ConfigAuditSensitivity.Unknown);
        Assert.Equal(1, (int)ConfigAuditSensitivity.NonSensitive);
        Assert.Equal(2, (int)ConfigAuditSensitivity.Sensitive);

        Assert.Equal(0, (int)ConfigAuditDiagnosticSeverity.Info);
        Assert.Equal(1, (int)ConfigAuditDiagnosticSeverity.Warning);
        Assert.Equal(2, (int)ConfigAuditDiagnosticSeverity.Error);
    }
}
