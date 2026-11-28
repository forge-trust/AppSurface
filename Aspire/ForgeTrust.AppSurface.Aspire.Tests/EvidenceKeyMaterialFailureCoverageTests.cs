using System.Collections;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using Microsoft.Extensions.Time.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceKeyMaterialFailureCoverageTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidProducerTimeoutCannotInvokeTheCallback(int seconds)
    {
        var calls = 0;
        var callback = new Callback(() => { calls++; return new Dictionary<string, string>(); });

        Assert.Throws<ArgumentOutOfRangeException>(() => EvidenceProducerKeyMaterialExecution.Start(callback,
            Context(seconds), TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(60001)]
    public void InvalidCleanupGraceCannotInvokeTheCallback(int milliseconds)
    {
        var calls = 0;
        var callback = new Callback(() => { calls++; return new Dictionary<string, string>(); });

        Assert.Throws<ArgumentOutOfRangeException>(() => EvidenceProducerKeyMaterialExecution.Start(callback,
            Context(), TimeSpan.FromMilliseconds(milliseconds), CancellationToken.None));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void CancellationAndNullArgumentsAreRejectedBeforeStartingCallbackOwnership()
    {
        var calls = 0;
        var callback = new Callback(() => { calls++; return new Dictionary<string, string>(); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<ArgumentNullException>(() => EvidenceProducerKeyMaterialExecution.Start(null!, Context(), TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => EvidenceProducerKeyMaterialExecution.Start(callback, null!, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => EvidenceProducerKeyMaterialExecution.Start(callback, Context(), TimeSpan.FromSeconds(1), cancellation.Token));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("deadline")]
    public async Task MaterialCompletedBeforeTheWaitIsStillDiscardedAfterCancellation(string source)
    {
        var clock = new FakeTimeProvider();
        using var caller = new CancellationTokenSource();
        await using var execution = EvidenceProducerKeyMaterialExecution.Start(
            new Callback(() => new Dictionary<string, string> { ["mode"] = "must-be-discarded" }),
            Context(1, clock), TimeSpan.FromSeconds(1), caller.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!execution.IsSettled)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
        if (source == "caller") caller.Cancel();
        else clock.Advance(TimeSpan.FromSeconds(1));

        var result = await execution.WaitAsync();

        Assert.True(result.Settled);
        Assert.Null(result.Material);
        Assert.Equal(source == "caller", result.CallerCancelled);
        Assert.Equal(source == "caller" ? "reuse.material_cancelled" : "reuse.material_timed_out", result.Reason);
    }

    [Fact]
    public async Task MaterialThatSettlesDuringCancellationCleanupIsJoinedAndDiscarded()
    {
        using var caller = new CancellationTokenSource();
        var callback = new PendingCallback();
        var execution = EvidenceProducerKeyMaterialExecution.Start(callback, Context(), TimeSpan.FromSeconds(5), caller.Token);
        try
        {
            await callback.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var waiting = execution.WaitAsync();
            Assert.False(waiting.IsCompleted);
            caller.Cancel();
            callback.Release.TrySetResult(new Dictionary<string, string> { ["mode"] = "late-private-material" });

            var result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(result.Settled);
            Assert.True(result.CallerCancelled);
            Assert.True(execution.IsSettled);
            Assert.Null(result.Material);
            Assert.Equal("reuse.material_cancelled", result.Reason);
        }
        finally
        {
            callback.Release.TrySetResult(new Dictionary<string, string>());
            await execution.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DeadlineCancelledCallbackThatSettlesWithinCleanupGraceIsDiscardedAsTimedOut()
    {
        var clock = new FakeTimeProvider();
        var callback = new DeadlinePendingCallback();
        var execution = EvidenceProducerKeyMaterialExecution.Start(callback, Context(1, clock),
            TimeSpan.FromSeconds(10), CancellationToken.None);
        try
        {
            await callback.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var waiting = execution.WaitAsync();
            Assert.False(waiting.IsCompleted);

            clock.Advance(TimeSpan.FromSeconds(1));
            await callback.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(execution.IsSettled);
            callback.Release.TrySetResult(new Dictionary<string, string> { ["mode"] = "late-deadline-material" });

            var result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(result.Settled);
            Assert.False(result.CallerCancelled);
            Assert.Null(result.Material);
            Assert.Equal("reuse.material_timed_out", result.Reason);
            Assert.True(execution.IsSettled);
        }
        finally
        {
            callback.Release.TrySetResult(new Dictionary<string, string>());
            await execution.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task CallbackFaultAfterCancellationIsJoinedAndClassifiedAsSettledCancellation()
    {
        using var caller = new CancellationTokenSource();
        var callback = new FaultAfterCancellationCallback();
        var execution = EvidenceProducerKeyMaterialExecution.Start(callback, Context(), TimeSpan.FromSeconds(5), caller.Token);
        try
        {
            await callback.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var waiting = execution.WaitAsync();
            Assert.False(waiting.IsCompleted);
            caller.Cancel();
            await callback.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            callback.Release.TrySetException(new IOException("controlled late callback failure"));

            var result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(result.Settled);
            Assert.True(execution.IsSettled);
            Assert.True(result.CallerCancelled);
            Assert.Null(result.Material);
            Assert.Equal("reuse.material_cancelled", result.Reason);
        }
        finally
        {
            callback.Release.TrySetResult(new Dictionary<string, string>());
            await execution.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task NullMaterialMapIsSettledButRejectedWithSafeDiagnostic()
    {
        await using var execution = EvidenceProducerKeyMaterialExecution.Start(
            new Callback(() => null!), Context(), TimeSpan.FromSeconds(1), CancellationToken.None);

        var result = await execution.WaitAsync();

        Assert.True(result.Settled);
        Assert.Null(result.Material);
        Assert.Equal("reuse.material_invalid", result.Reason);
        Assert.Contains("returned no material map", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonfatalCallbackFailureBecomesFreshFallbackWithoutLeakingExceptionText()
    {
        await using var execution = EvidenceProducerKeyMaterialExecution.Start(
            new Callback(() => throw new IOException("private callback detail")),
            Context(), TimeSpan.FromSeconds(1), CancellationToken.None);

        var result = await execution.WaitAsync();

        Assert.True(result.Settled);
        Assert.Null(result.Material);
        Assert.Equal("reuse.material_failed", result.Reason);
        Assert.Null(result.Diagnostic);
    }

    [Theory]
    [InlineData("empty-key", "length 0")]
    [InlineData("null-value", "null value")]
    [InlineData("duplicate-key", "unique")]
    [InlineData("underreported-count", "65 observed")]
    [InlineData("overreported-count", "65 keys")]
    [InlineData("oversized-key", "129 UTF-16 code units")]
    [InlineData("total-bytes", "totals")]
    public async Task UntrustedMapEnumerationCannotEvadeTheBoundedSnapshot(string defect, string expectedDiagnostic)
    {
        var entries = defect switch
        {
            "empty-key" => new[] { new KeyValuePair<string, string>(string.Empty, "private-value-marker") },
            "null-value" => [new("private-key-marker", null!)],
            "duplicate-key" => [new("private-key-marker", "first"), new("private-key-marker", "second")],
            "overreported-count" => [new("private-key-marker", "private-value-marker")],
            "oversized-key" => [new(new string('k', 129), "private-value-marker")],
            "total-bytes" => Enumerable.Range(0, 64)
                .Select(index => new KeyValuePair<string, string>($"key-{index:D2}", new string('v', 4096))).ToArray(),
            _ => Enumerable.Range(0, 65).Select(index => new KeyValuePair<string, string>("private-key-" + index, "private-value-marker")).ToArray()
        };
        await using var execution = EvidenceProducerKeyMaterialExecution.Start(
            new Callback(() => new UntrustedMaterialMap(entries, defect == "overreported-count" ? 65 : 0)),
            Context(), TimeSpan.FromSeconds(1), CancellationToken.None);

        var result = await execution.WaitAsync();

        Assert.True(result.Settled);
        Assert.Null(result.Material);
        Assert.Equal("reuse.material_invalid", result.Reason);
        Assert.Contains(expectedDiagnostic, result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key-", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value-marker", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ascii")]
    [InlineData("two-byte")]
    [InlineData("three-byte")]
    [InlineData("surrogate-pair")]
    [InlineData("unpaired-surrogate")]
    [InlineData("terminal-surrogate")]
    public async Task Utf8ValueBoundaryAcceptsExactBytesAndRejectsTheNextByte(string shape)
    {
        var value = shape switch
        {
            "ascii" => new string('a', 4096),
            "two-byte" => new string('é', 2048),
            "three-byte" => new string('\u0800', 1365) + "a",
            "surrogate-pair" => string.Concat(Enumerable.Repeat("\ud83d\ude00", 1024)),
            "unpaired-surrogate" => new string('\ud800', 1365) + "a",
            _ => "a" + new string('\ud800', 1365)
        };
        await using var exact = EvidenceProducerKeyMaterialExecution.Start(
            new Callback(() => new Dictionary<string, string> { ["mode"] = value }), Context(), TimeSpan.FromSeconds(1), CancellationToken.None);
        var accepted = await exact.WaitAsync();
        var malformed = shape is "unpaired-surrogate" or "terminal-surrogate";
        if (malformed)
        {
            Assert.Equal("reuse.material_invalid", accepted.Reason);
            Assert.Null(accepted.Material);
            Assert.Contains("malformed UTF-16", accepted.Diagnostic, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("reuse.material_ready", accepted.Reason);
            Assert.Equal(value, accepted.Material!["mode"]);
        }

        await using var over = EvidenceProducerKeyMaterialExecution.Start(
            new Callback(() => new Dictionary<string, string> { ["mode"] = value + "a" }), Context(), TimeSpan.FromSeconds(1), CancellationToken.None);
        var rejected = await over.WaitAsync();
        Assert.Equal("reuse.material_invalid", rejected.Reason);
        Assert.Null(rejected.Material);
        if (malformed) Assert.Contains("malformed UTF-16", rejected.Diagnostic, StringComparison.Ordinal);
        else
        {
            Assert.Contains("4097", rejected.Diagnostic, StringComparison.Ordinal);
            Assert.Contains("4096", rejected.Diagnostic, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("stack")]
    [InlineData("access")]
    [InlineData("domain")]
    public async Task FatalCallbackFailuresRemainObservableAfterActualCallbackJoining(string shape)
    {
        // Constructing a synthetic exception exercises propagation without exhausting resources.
        Exception failure = shape switch
        {
            "memory" => new OutOfMemoryException(),
            "stack" => new StackOverflowException(),
            "access" => new AccessViolationException(),
            _ => new AppDomainUnloadedException()
        };
        var execution = EvidenceProducerKeyMaterialExecution.Start(new Callback(() => throw failure),
            Context(), TimeSpan.FromSeconds(1), CancellationToken.None);
        try { Assert.Same(failure, await Record.ExceptionAsync(() => execution.WaitAsync())); }
        finally { Assert.Same(failure, await Record.ExceptionAsync(() => execution.DisposeAsync().AsTask())); }
        Assert.True(execution.IsSettled);
    }

    private static EvidenceProducerKeyMaterialContext Context(int timeout = 30, TimeProvider? clock = null)
    {
        var declaration = new EvidenceProducerDeclaration("material", "custom", "1", [], [], [], timeout);
        return new(new EvidencePlan("1.0", "policy", "", "",
            new EvidenceProfile("local", EvidenceProfileScope.Targeted, [], [declaration], []), [], [], ""), declaration,
            clock ?? TimeProvider.System);
    }

    private sealed class Callback(Func<IReadOnlyDictionary<string, string>> callback) : IEvidenceProducerKeyMaterial
    {
        public ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(EvidenceProducerKeyMaterialContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult(callback());
    }

    private sealed class PendingCallback : IEvidenceProducerKeyMaterial
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<IReadOnlyDictionary<string, string>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(EvidenceProducerKeyMaterialContext context,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return new(Release.Task);
        }
    }

    private sealed class DeadlinePendingCallback : IEvidenceProducerKeyMaterial
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<IReadOnlyDictionary<string, string>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(
            EvidenceProducerKeyMaterialContext context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            using var cancellationRegistration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            return await Release.Task.ConfigureAwait(false);
        }
    }

    private sealed class FaultAfterCancellationCallback : IEvidenceProducerKeyMaterial
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<IReadOnlyDictionary<string, string>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(
            EvidenceProducerKeyMaterialContext context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            return await Release.Task.ConfigureAwait(false);
        }
    }

    private sealed class UntrustedMaterialMap(IReadOnlyList<KeyValuePair<string, string>> entries, int reportedCount)
        : IReadOnlyDictionary<string, string>
    {
        public int Count => reportedCount;
        public IEnumerable<string> Keys => entries.Select(pair => pair.Key);
        public IEnumerable<string> Values => entries.Select(pair => pair.Value);
        public string this[string key] => entries.First(pair => pair.Key == key).Value;
        public bool ContainsKey(string key) => entries.Any(pair => pair.Key == key);
        public bool TryGetValue(string key, out string value)
        {
            foreach (var pair in entries)
            {
                if (pair.Key != key) continue;
                value = pair.Value;
                return true;
            }
            value = null!;
            return false;
        }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => entries.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
