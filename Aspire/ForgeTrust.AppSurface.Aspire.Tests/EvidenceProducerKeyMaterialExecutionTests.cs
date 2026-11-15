using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceProducerKeyMaterialExecutionTests
{
    [Fact]
    public async Task SuccessfulMaterial_ShouldBeAvailableOnlyFromTheActualCompletedCallback()
    {
        var material = new Dictionary<string, string> { ["tool"] = "version" };
        await using var execution = EvidenceProducerKeyMaterialExecution.Start(
            new Callback((_, _) => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(material)),
            Context(), TimeSpan.FromMilliseconds(50), CancellationToken.None);

        var result = await execution.WaitAsync();

        Assert.True(result.Settled);
        Assert.NotSame(material, result.Material);
        Assert.Equal("version", result.Material!["tool"]);
        material["tool"] = "changed after callback";
        Assert.Equal("version", result.Material["tool"]);
        Assert.Equal("reuse.material_ready", result.Reason);
    }

    [Fact]
    public async Task NoncooperativeCancellation_ShouldRetainActualCallbackUntilDisposalJoinsIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var execution = EvidenceProducerKeyMaterialExecution.Start(new Callback((_, _) =>
        {
            entered.SetResult();
            return new ValueTask<IReadOnlyDictionary<string, string>>(release.Task);
        }), Context(), TimeSpan.FromMilliseconds(20), cancellation.Token);
        var wait = execution.WaitAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        var result = await wait.WaitAsync(TimeSpan.FromSeconds(10));
        var cleanup = execution.DisposeAsync().AsTask();
        Assert.True(result.CallerCancelled);
        Assert.False(result.Settled);
        Assert.Null(result.Material);
        Assert.False(cleanup.IsCompleted);
        release.SetResult(new Dictionary<string, string> { ["late"] = "discarded" });
        await cleanup;
        Assert.Equal("reuse.material_cancelled_active", result.Reason);
    }

    [Fact]
    public async Task Deadline_ShouldNotTreatSynchronousCallbackBodyAsTerminated()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = EvidenceProducerKeyMaterialExecution.Start(new Callback((_, _) =>
        {
            entered.SetResult();
            release.Wait();
            return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        }), Context(timeoutSeconds: 1), TimeSpan.FromMilliseconds(20), CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await execution.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(result.Settled);
            Assert.Null(result.Material);
            Assert.Equal("reuse.material_unsettled", result.Reason);
        }
        finally
        {
            release.Set();
            await execution.DisposeAsync();
        }
    }

    [Fact]
    public async Task CooperativeCancellation_ShouldAllowOnlySettledFallbackWithoutLateMaterial()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        await using var execution = EvidenceProducerKeyMaterialExecution.Start(new Callback(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new Dictionary<string, string>();
        }), Context(), TimeSpan.FromSeconds(1), cancellation.Token);
        var wait = execution.WaitAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        var result = await wait.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Settled);
        Assert.True(result.CallerCancelled);
        Assert.Null(result.Material);
    }

    [Fact]
    public async Task FatalCallbackFailure_ShouldPropagateUnchanged()
    {
        var fatal = new OutOfMemoryException("controlled key callback failure");
        var execution = EvidenceProducerKeyMaterialExecution.Start(new Callback((_, _) => throw fatal),
            Context(), TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Same(fatal, await Assert.ThrowsAsync<OutOfMemoryException>(() => execution.WaitAsync()));
        Assert.Same(fatal, await Assert.ThrowsAsync<OutOfMemoryException>(() => execution.DisposeAsync().AsTask()));
    }

    [Fact]
    public async Task NonfatalCallbackFailure_ShouldPermitFallbackAfterActualSettlement()
    {
        await using var execution = EvidenceProducerKeyMaterialExecution.Start(
            new Callback((_, _) => throw new InvalidOperationException("raw value must not enter diagnostics")),
            Context(), TimeSpan.FromMilliseconds(50), CancellationToken.None);

        var result = await execution.WaitAsync();

        Assert.True(result.Settled);
        Assert.Null(result.Material);
        Assert.Equal("reuse.material_failed", result.Reason);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public async Task InvalidDataCallbackFailure_ShouldUseSafeDiagnosticWithoutCopyingExceptionMessage()
    {
        const string privateCallbackMessage = "private-key=secret-value";
        await using var execution = EvidenceProducerKeyMaterialExecution.Start(
            new Callback((_, _) => throw new InvalidDataException(privateCallbackMessage)),
            Context(), TimeSpan.FromSeconds(1), CancellationToken.None);

        var result = await execution.WaitAsync();

        Assert.True(result.Settled);
        Assert.Null(result.Material);
        Assert.Equal("reuse.material_invalid", result.Reason);
        var diagnostic = Assert.IsType<string>(result.Diagnostic);
        Assert.Contains("within the declared limits", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(privateCallbackMessage, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidMaterialBounds_ShouldReportSafeNumericCountsWithoutRawKeysOrValues()
    {
        var tooMany = Enumerable.Range(0, EvidenceProducerIdentityBuilder.MaximumMaterialKeys + 1)
            .ToDictionary(static index => $"private-key-{index}", static index => $"private-value-{index}");
        var countResult = await PrepareMaterialAsync(tooMany);
        var countDiagnostic = Assert.IsType<string>(countResult.Diagnostic);
        Assert.Equal("reuse.material_invalid", countResult.Reason);
        Assert.Contains("65", countDiagnostic, StringComparison.Ordinal);
        Assert.Contains("64", countDiagnostic, StringComparison.Ordinal);
        Assert.Contains("Remove keys", countDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key-", countDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value-", countDiagnostic, StringComparison.Ordinal);

        const string keyMarker = "private-key-marker";
        var overlongKey = keyMarker + new string('k', EvidenceProducerIdentityBuilder.MaximumMaterialKeyLength + 1 - keyMarker.Length);
        var keyLengthResult = await PrepareMaterialAsync(new Dictionary<string, string> { [overlongKey] = "private-value-marker" });
        var keyLengthDiagnostic = Assert.IsType<string>(keyLengthResult.Diagnostic);
        Assert.Equal("reuse.material_invalid", keyLengthResult.Reason);
        Assert.Contains("129", keyLengthDiagnostic, StringComparison.Ordinal);
        Assert.Contains("128", keyLengthDiagnostic, StringComparison.Ordinal);
        Assert.Contains("Shorten key names", keyLengthDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(keyMarker, keyLengthDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value-marker", keyLengthDiagnostic, StringComparison.Ordinal);

        var overlongUtf8Value = new string('é', 2049);
        var valueLengthResult = await PrepareMaterialAsync(new Dictionary<string, string> { ["private-key-marker"] = overlongUtf8Value });
        var valueLengthDiagnostic = Assert.IsType<string>(valueLengthResult.Diagnostic);
        Assert.Equal("reuse.material_invalid", valueLengthResult.Reason);
        Assert.Contains("4098", valueLengthDiagnostic, StringComparison.Ordinal);
        Assert.Contains("4096", valueLengthDiagnostic, StringComparison.Ordinal);
        Assert.Contains("UTF-8 bytes", valueLengthDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key-marker", valueLengthDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(overlongUtf8Value, valueLengthDiagnostic, StringComparison.Ordinal);

        var overTotalBytes = Enumerable.Range(0, EvidenceProducerIdentityBuilder.MaximumMaterialKeys)
            .ToDictionary(static index => $"k{index:D2}", static index => new string('v', index == 63 ? 4094 : 4093));
        var totalResult = await PrepareMaterialAsync(overTotalBytes);
        var totalDiagnostic = Assert.IsType<string>(totalResult.Diagnostic);
        Assert.Equal("reuse.material_invalid", totalResult.Reason);
        Assert.Contains("262145", totalDiagnostic, StringComparison.Ordinal);
        Assert.Contains("262144", totalDiagnostic, StringComparison.Ordinal);
        Assert.Contains("Reduce key and value sizes", totalDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("k00", totalDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('v', 32), totalDiagnostic, StringComparison.Ordinal);
    }

    private static EvidenceProducerKeyMaterialContext Context(int timeoutSeconds = 30)
    {
        var producer = new EvidenceProducerDeclaration("tests", "custom", "1", [], [], [], timeoutSeconds);
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [], [producer], []);
        return new EvidenceProducerKeyMaterialContext(new EvidencePlan("1.0", "policy", "", "", profile, [], [], ""),
            producer, TimeProvider.System);
    }

    private static async Task<EvidenceProducerKeyMaterialPreparation> PrepareMaterialAsync(IReadOnlyDictionary<string, string> material)
    {
        await using var execution = EvidenceProducerKeyMaterialExecution.Start(
            new Callback((_, _) => ValueTask.FromResult(material)), Context(), TimeSpan.FromSeconds(1), CancellationToken.None);
        return await execution.WaitAsync();
    }

    private sealed class Callback(Func<EvidenceProducerKeyMaterialContext, CancellationToken,
        ValueTask<IReadOnlyDictionary<string, string>>> execute) : IEvidenceProducerKeyMaterial
    {
        public ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(
            EvidenceProducerKeyMaterialContext context, CancellationToken cancellationToken) => execute(context, cancellationToken);
    }
}
