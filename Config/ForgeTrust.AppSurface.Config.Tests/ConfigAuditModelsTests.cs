using System.Text.Json;

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
    public void ConfigAuditSourceLocation_RejectsInvalidConstructorArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigAuditSourceLocation(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigAuditSourceLocation(1, 0));
    }

    [Fact]
    public void ConfigAuditSourceRecord_DefaultsLocationToNull()
    {
        var source = new ConfigAuditSourceRecord
        {
            Kind = ConfigAuditSourceKind.File,
            Role = ConfigAuditSourceRole.Base
        };

        Assert.Null(source.Location);
    }

    [Fact]
    public void ConfigAuditSourceRecord_WithRolePreservesLocation()
    {
        var location = new ConfigAuditSourceLocation(12, 34);
        var source = new ConfigAuditSourceRecord
        {
            Kind = ConfigAuditSourceKind.File,
            ProviderName = "Files",
            FilePath = "/tmp/appsettings.json",
            ConfigPath = "Feature.Enabled",
            AppliedToPath = "Feature.Enabled",
            Location = location,
            Role = ConfigAuditSourceRole.Base
        };

        var patched = source.WithRole(ConfigAuditSourceRole.Patch);

        Assert.Same(location, patched.Location);
        Assert.Equal(ConfigAuditSourceRole.Patch, patched.Role);
    }

    [Fact]
    public void ConfigAuditSourceRecord_SerializesLocationShape()
    {
        var source = new ConfigAuditSourceRecord
        {
            Kind = ConfigAuditSourceKind.File,
            ConfigPath = "Feature.Enabled",
            AppliedToPath = "Feature.Enabled",
            Location = new ConfigAuditSourceLocation(2, 7),
            Role = ConfigAuditSourceRole.Base
        };

        var json = JsonSerializer.Serialize(source);

        Assert.Contains(@"""Location"":", json, StringComparison.Ordinal);
        Assert.Contains(@"""LineNumber"":2", json, StringComparison.Ordinal);
        Assert.Contains(@"""ByteColumnNumber"":7", json, StringComparison.Ordinal);
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
