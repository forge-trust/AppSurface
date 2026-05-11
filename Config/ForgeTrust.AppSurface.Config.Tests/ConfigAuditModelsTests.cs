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
    public void PublicEnums_KeepStableOrdinals()
    {
        Assert.Equal(0, (int)ConfigAuditEntryState.Resolved);
        Assert.Equal(1, (int)ConfigAuditEntryState.PartiallyResolved);
        Assert.Equal(2, (int)ConfigAuditEntryState.Defaulted);
        Assert.Equal(3, (int)ConfigAuditEntryState.Missing);
        Assert.Equal(4, (int)ConfigAuditEntryState.Invalid);

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
