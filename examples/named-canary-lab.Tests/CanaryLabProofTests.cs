using ForgeTrust.AppSurface.Web;

namespace NamedCanaryLab.Tests;

public sealed class CanaryLabProofTests
{
    private const string Marker = "marker-sentinel";

    [Fact]
    public async Task Evaluator_ReturnsPendingWhenNoMatchingProofExists()
    {
        var evaluator = CreateEvaluator(new CanaryLabProofStore());

        var result = await evaluator.EvaluateAsync(Context(), CancellationToken.None);

        Assert.Equal(AppSurfaceCanaryStatus.Pending, result.Status);
        Assert.Equal(0, result.MatchedCount);
        Assert.Equal("proof-not-observed", result.ReasonCode);
        Assert.Equal("No matching local proof has been observed yet.", result.Summary);
        Assert.Null(result.CorrelationId);
        Assert.Empty(result.Details);
    }

    [Fact]
    public async Task Evaluator_RejectsMissingMarkerOrFreshnessBoundary()
    {
        var evaluator = CreateEvaluator(new CanaryLabProofStore());

        var markerException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            evaluator.EvaluateAsync(
                new AppSurfaceCanaryEvaluationContext(
                    NamedCanaryLabApp.CanaryName,
                    marker: " ",
                    freshSince: DateTimeOffset.UtcNow),
                CancellationToken.None).AsTask());
        var freshnessException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            evaluator.EvaluateAsync(
                new AppSurfaceCanaryEvaluationContext(
                    NamedCanaryLabApp.CanaryName,
                    Marker,
                    freshSince: null),
                CancellationToken.None).AsTask());

        Assert.Equal("The named-canary lab evaluator requires marker and freshness registration.", markerException.Message);
        Assert.Equal(markerException.Message, freshnessException.Message);
    }

    [Fact]
    public async Task Evaluator_ReturnsPassForFreshMatchingProof()
    {
        var store = new CanaryLabProofStore();
        var now = DateTimeOffset.UtcNow;
        store.Record(new CanaryProofRecord(
            new CanaryProofIdentity("candidate-sentinel", "development"),
            CanaryLabMarkerFingerprint.Create(Marker),
            now,
            AppSurfaceCanaryStatus.Pass));

        var result = await CreateEvaluator(store).EvaluateAsync(Context(now.AddSeconds(-1)), CancellationToken.None);

        Assert.Equal(AppSurfaceCanaryStatus.Pass, result.Status);
        Assert.Equal("proof-observed", result.ReasonCode);
        Assert.Equal(now, result.ObservedAt);
    }

    [Fact]
    public async Task Evaluator_TreatsEvidenceAtTheFreshnessBoundaryAsCurrent()
    {
        var store = new CanaryLabProofStore();
        var observedAt = DateTimeOffset.UtcNow;
        store.Record(new CanaryProofRecord(
            new CanaryProofIdentity("candidate-sentinel", "development"),
            CanaryLabMarkerFingerprint.Create(Marker),
            observedAt,
            AppSurfaceCanaryStatus.Pass));

        var result = await CreateEvaluator(store).EvaluateAsync(Context(observedAt), CancellationToken.None);

        Assert.Equal(AppSurfaceCanaryStatus.Pass, result.Status);
        Assert.Equal(observedAt, result.ObservedAt);
    }

    [Fact]
    public async Task Evaluator_ReturnsStaleWhenProofPredatesFreshnessBoundary()
    {
        var store = new CanaryLabProofStore();
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        store.Record(new CanaryProofRecord(
            new CanaryProofIdentity("candidate-sentinel", "development"),
            CanaryLabMarkerFingerprint.Create(Marker),
            observedAt,
            AppSurfaceCanaryStatus.Pass));

        var result = await CreateEvaluator(store).EvaluateAsync(Context(observedAt.AddSeconds(1)), CancellationToken.None);

        Assert.Equal(AppSurfaceCanaryStatus.Stale, result.Status);
        Assert.Equal("proof-stale", result.ReasonCode);
        Assert.Equal("Matching local proof predates the requested freshness boundary.", result.Summary);
    }

    [Fact]
    public async Task Evaluator_ReturnsFailForIdentityMismatchAndFixedNegativeRecord()
    {
        var mismatchStore = new CanaryLabProofStore();
        mismatchStore.Record(new CanaryProofRecord(
            new CanaryProofIdentity("other-candidate", "development"),
            CanaryLabMarkerFingerprint.Create(Marker),
            DateTimeOffset.UtcNow,
            AppSurfaceCanaryStatus.Pass));

        var mismatch = await CreateEvaluator(mismatchStore).EvaluateAsync(Context(), CancellationToken.None);
        Assert.Equal(AppSurfaceCanaryStatus.Fail, mismatch.Status);
        Assert.Equal("candidate-mismatch", mismatch.ReasonCode);

        var environmentMismatchStore = new CanaryLabProofStore();
        environmentMismatchStore.Record(new CanaryProofRecord(
            new CanaryProofIdentity("candidate-sentinel", "other-environment"),
            CanaryLabMarkerFingerprint.Create(Marker),
            DateTimeOffset.UtcNow,
            AppSurfaceCanaryStatus.Pass));

        var environmentMismatch = await CreateEvaluator(environmentMismatchStore).EvaluateAsync(Context(), CancellationToken.None);
        Assert.Equal(AppSurfaceCanaryStatus.Fail, environmentMismatch.Status);
        Assert.Equal("candidate-mismatch", environmentMismatch.ReasonCode);

        var failureStore = new CanaryLabProofStore();
        failureStore.Record(new CanaryProofRecord(
            new CanaryProofIdentity("candidate-sentinel", "development"),
            CanaryLabMarkerFingerprint.Create(Marker),
            DateTimeOffset.UtcNow,
            AppSurfaceCanaryStatus.Fail));

        var failure = await CreateEvaluator(failureStore).EvaluateAsync(Context(), CancellationToken.None);
        Assert.Equal(AppSurfaceCanaryStatus.Fail, failure.Status);
        Assert.Equal("workflow-failed", failure.ReasonCode);
        Assert.DoesNotContain(Marker, failure.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public void Store_PreservesNewerEvidenceWhenDelayedStaleWriteArrives()
    {
        var store = new CanaryLabProofStore();
        var identity = new CanaryProofIdentity("candidate-sentinel", "development");
        var fingerprint = CanaryLabMarkerFingerprint.Create(Marker);
        var newest = new CanaryProofRecord(identity, fingerprint, DateTimeOffset.UtcNow, AppSurfaceCanaryStatus.Pass);
        var stale = newest with { ObservedAt = newest.ObservedAt.AddMinutes(-1) };

        Assert.Same(newest, store.Record(newest));
        Assert.Same(newest, store.Record(stale));
        Assert.True(store.TryRead(fingerprint, out var stored));
        Assert.Same(newest, stored);
    }

    [Fact]
    public void Store_ReplacesOlderEvidenceWhenNewerProofArrives()
    {
        var store = new CanaryLabProofStore();
        var identity = new CanaryProofIdentity("candidate-sentinel", "development");
        var fingerprint = CanaryLabMarkerFingerprint.Create(Marker);
        var older = new CanaryProofRecord(
            identity,
            fingerprint,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            AppSurfaceCanaryStatus.Fail);
        var newer = older with { ObservedAt = older.ObservedAt.AddMinutes(1), Status = AppSurfaceCanaryStatus.Pass };

        Assert.Same(older, store.Record(older));
        Assert.Same(newer, store.Record(newer));
        Assert.True(store.TryRead(fingerprint, out var stored));
        Assert.Same(newer, stored);
    }

    [Fact]
    public async Task Evaluator_RejectsAnUnsupportedStoredProofStatus()
    {
        var store = new CanaryLabProofStore();
        store.Record(new CanaryProofRecord(
            new CanaryProofIdentity("candidate-sentinel", "development"),
            CanaryLabMarkerFingerprint.Create(Marker),
            DateTimeOffset.UtcNow,
            (AppSurfaceCanaryStatus)999));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateEvaluator(store).EvaluateAsync(Context(), CancellationToken.None).AsTask());

        Assert.Equal("The named-canary lab store contains an unsupported proof status.", exception.Message);
    }

    [Fact]
    public void Evaluator_RejectsNullContextsAndCancelledRequests()
    {
        var evaluator = CreateEvaluator(new CanaryLabProofStore());
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.Throws<ArgumentNullException>(() => evaluator.EvaluateAsync(null!, CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => evaluator.EvaluateAsync(Context(), cancellationSource.Token));
    }

    [Fact]
    public async Task Store_PreservesTheNewestEvidenceWhenConcurrentWritersRace()
    {
        var store = new CanaryLabProofStore();
        var identity = new CanaryProofIdentity("candidate-sentinel", "development");
        var fingerprint = CanaryLabMarkerFingerprint.Create(Marker);
        var firstObservedAt = DateTimeOffset.UtcNow;
        var candidates = Enumerable.Range(0, 64)
            .Select(offset => new CanaryProofRecord(
                identity,
                fingerprint,
                firstObservedAt.AddSeconds(offset),
                AppSurfaceCanaryStatus.Pass))
            .ToArray();

        await Task.WhenAll(candidates.Select(candidate => Task.Run(() => store.Record(candidate))));

        Assert.True(store.TryRead(fingerprint, out var stored));
        Assert.Equal(candidates[^1].ObservedAt, stored.ObservedAt);
    }

    [Fact]
    public void Store_RejectsNullEvidenceAndNewMarkersPastItsBound()
    {
        var store = new CanaryLabProofStore();
        var identity = new CanaryProofIdentity("candidate-sentinel", "development");

        Assert.Throws<ArgumentNullException>(() => store.Record(null!));
        for (var index = 0; index < CanaryLabProofStore.MaximumRecordCount; index++)
        {
            var marker = $"marker-{index}";
            Assert.NotNull(store.Record(new CanaryProofRecord(
                identity,
                CanaryLabMarkerFingerprint.Create(marker),
                DateTimeOffset.UtcNow,
                AppSurfaceCanaryStatus.Pass)));
        }

        Assert.Null(store.Record(new CanaryProofRecord(
            identity,
            CanaryLabMarkerFingerprint.Create("marker-over-capacity"),
            DateTimeOffset.UtcNow,
            AppSurfaceCanaryStatus.Pass)));

        var updatedExisting = new CanaryProofRecord(
            identity,
            CanaryLabMarkerFingerprint.Create("marker-0"),
            DateTimeOffset.UtcNow.AddSeconds(1),
            AppSurfaceCanaryStatus.Fail);
        Assert.Same(updatedExisting, store.Record(updatedExisting));
        Assert.True(store.TryRead(updatedExisting.MarkerFingerprint, out var stored));
        Assert.Same(updatedExisting, stored);
    }

    private static CanaryLabEvaluator CreateEvaluator(CanaryLabProofStore store) =>
        new(
            store,
            CanaryLabSettings.Create(
                CanaryLabSettingsTests.CreateConfiguration(),
                new TestHostEnvironment()));

    private static AppSurfaceCanaryEvaluationContext Context(DateTimeOffset? freshSince = null) =>
        new(NamedCanaryLabApp.CanaryName, Marker, freshSince ?? DateTimeOffset.UtcNow.AddMinutes(-1));

}
