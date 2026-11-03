using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Closed detached failure data controls; they create no native owner, lease or acceptance.</summary>
public sealed class LinuxCancellationProjectionFailureTests
{
    private static readonly Guid Generation = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string Canary = "/private/canary-output ASEVD410 raw exception text";

    /// <summary>Each closed checkpoint survives encoding without caller text or completion authority.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void ClosedCheckpointsRemainBoundedFailureData(int value)
    {
        var stage = (LinuxCancellationProjectionStage)value;
        var bytes = LinuxCancellationProjectionFailure.EncodeDetached(Generation, stage,
            new InvalidOperationException(Canary, new IOException(Canary)));
        Assert.InRange(bytes.Length, 1, LinuxCancellationProjectionFailure.MaximumJsonBytes);
        Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        Assert.Equal(8, root.EnumerateObject().Count());
        Assert.Equal("issue779-cancellation-projection-failure-v1", root.GetProperty("schema").GetString());
        Assert.Equal(Generation.ToString("N"), root.GetProperty("generation").GetString());
        Assert.Equal(stage.ToString(), root.GetProperty("stage").GetString());
        Assert.Equal("InvalidOperation", root.GetProperty("error_kind").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("diagnostic_code").ValueKind);
        Assert.True(root.GetProperty("observation_only").GetBoolean());
        Assert.False(root.GetProperty("native_authority").GetBoolean());
        Assert.False(root.GetProperty("native_acceptance").GetBoolean());
    }

    /// <summary>Error family derives from the actual caught type, never a code-looking message or inner error.</summary>
    [Theory]
    [InlineData(0, "Admission", "ASEVD410")]
    [InlineData(1, "Cancelled", null)]
    [InlineData(2, "Io", null)]
    [InlineData(3, "InvalidOperation", null)]
    [InlineData(4, "Unknown", null)]
    public void ActualErrorTypeControlsTheClosedFamily(int value, string kind, string? code)
    {
        Exception error = value switch
        {
            0 => LinuxSystemdBackend.InvalidControl(),
            1 => new OperationCanceledException(Canary),
            2 => new IOException(Canary),
            3 => new InvalidOperationException(Canary, LinuxSystemdBackend.InvalidControl()),
            _ => new Exception(Canary),
        };
        var bytes = LinuxCancellationProjectionFailure.EncodeDetached(Generation,
            LinuxCancellationProjectionStage.SignalProvenance, error);
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(kind, json.RootElement.GetProperty("error_kind").GetString());
        Assert.Equal(code, json.RootElement.GetProperty("diagnostic_code").GetString());
        Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    /// <summary>Missing generation/error and unknown checkpoints reject with fixed text and no inner exception.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MalformedMetadataCannotEnterTheDiagnostic(int value)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            LinuxCancellationProjectionFailure.EncodeDetached(value == 0 ? Guid.Empty : Generation,
                value == 1 ? (LinuxCancellationProjectionStage)99 : LinuxCancellationProjectionStage.OriginalCustody,
                value == 2 ? null! : new Exception(Canary)));
        Assert.Equal("ASEVD410: Cancellation projection diagnostic data rejected.", error.Message);
        Assert.Null(error.InnerException);
    }
}
