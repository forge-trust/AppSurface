using System.Net;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Readiness;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceAspireApplicationLifetimeTests
{
    // valuecard protects shared ownership, one cleanup execution, and the original cleanup failure across all joiners.
    // fails_when a nonfinal release cleans early, final release does not wait, or owner/final/repeated calls observe different failures.
    // why_new the lifetime spans independent adapters; seam two registrations plus a gated throwing application callback.
    [Fact]
    public async Task TwoRegistrations_KeepApplicationAliveUntilFinalRelease_AndJoinTheSameFailure()
    {
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new InvalidOperationException("application cleanup failed");
        var cleanupCount = 0;
        var lifetime = new EvidenceAspireApplicationLifetime(async () =>
        {
            Interlocked.Increment(ref cleanupCount);
            cleanupEntered.TrySetResult();
            await finishCleanup.Task;
            throw cleanupFailure;
        });

        var first = lifetime.Acquire();
        var final = lifetime.Acquire();

        await first.DisposeAsync();
        await first.DisposeAsync();
        lifetime.ThrowIfDisposed();
        Assert.Equal(0, cleanupCount);

        var finalRelease = final.DisposeAsync().AsTask();
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var repeatedRelease = final.DisposeAsync().AsTask();
        var directOwnerDispose = lifetime.DisposeAsync().AsTask();

        Assert.False(finalRelease.IsCompleted);
        Assert.False(repeatedRelease.IsCompleted);
        Assert.False(directOwnerDispose.IsCompleted);
        Assert.Equal(1, cleanupCount);
        Assert.Throws<ObjectDisposedException>(() => lifetime.Acquire());
        Assert.Throws<ObjectDisposedException>(() => lifetime.ThrowIfDisposed());

        // A registration that already released non-finally keeps its original completed result.
        Assert.True(first.DisposeAsync().IsCompletedSuccessfully);
        finishCleanup.TrySetResult();

        Assert.Same(cleanupFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => finalRelease));
        Assert.Same(cleanupFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => repeatedRelease));
        Assert.Same(cleanupFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => directOwnerDispose));
        Assert.Same(cleanupFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime.DisposeAsync().AsTask()));
        Assert.Equal(1, cleanupCount);
    }

    // valuecard protects direct owner shutdown from triggering a second cleanup when an outstanding registration later releases.
    // fails_when registration release starts another callback or fails to join the owner's original cleanup result.
    // why_new direct disposal closes admission independently of registration count; seam gated owner cleanup and one retained registration.
    [Fact]
    public async Task DirectOwnerDisposal_ClosesAdmissionAndRegistrationReleaseJoinsWithoutSecondCleanup()
    {
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new InvalidOperationException("owner cleanup failed");
        var cleanupCount = 0;
        var lifetime = new EvidenceAspireApplicationLifetime(async () =>
        {
            Interlocked.Increment(ref cleanupCount);
            cleanupEntered.TrySetResult();
            await finishCleanup.Task;
            throw cleanupFailure;
        });
        var registration = lifetime.Acquire();

        var ownerDispose = lifetime.DisposeAsync().AsTask();
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var registrationRelease = registration.DisposeAsync().AsTask();
        var repeatedOwnerDispose = lifetime.DisposeAsync().AsTask();

        Assert.False(ownerDispose.IsCompleted);
        Assert.False(registrationRelease.IsCompleted);
        Assert.False(repeatedOwnerDispose.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => lifetime.Acquire());
        Assert.Throws<ObjectDisposedException>(() => lifetime.ThrowIfDisposed());

        finishCleanup.TrySetResult();
        Assert.Same(cleanupFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => ownerDispose));
        Assert.Same(cleanupFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => registrationRelease));
        Assert.Same(cleanupFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => repeatedOwnerDispose));
        Assert.Same(cleanupFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => registration.DisposeAsync().AsTask()));
        Assert.Equal(1, cleanupCount);
    }

    // valuecard protects another adapter's in-flight HTTP work and client from a sibling adapter's disposal.
    // fails_when first disposal cancels the second wait, disposes its client, or runs application cleanup before the second settles.
    // why_new adapters have separate waits and clients over one owner; seam real readiness adapters and an HTTP handler that ignores cancellation.
    [Fact]
    public async Task DisposingOneAdapter_DoesNotCancelAnotherAdaptersIgnoringHttpWaitOrDisposeItsClient()
    {
        var requestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientDisposed = 0;
        var applicationCleanupCount = 0;
        var lifetime = new EvidenceAspireApplicationLifetime(() =>
        {
            Interlocked.Increment(ref applicationCleanupCount);
            return ValueTask.CompletedTask;
        });
        var firstRegistration = lifetime.Acquire();
        var secondRegistration = lifetime.Acquire();
        var firstAdapter = CreateReadiness(firstRegistration.DisposeAsync, new DelegateHandler(
            _ => throw new InvalidOperationException("The first adapter must not send an HTTP request."), () => { }));
        var secondAdapter = CreateReadiness(secondRegistration.DisposeAsync, new DelegateHandler(
            async _ =>
            {
                requestEntered.TrySetResult();
                // Deliberately ignore cancellation until the test releases the response.
                await finishRequest.Task;
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            () => Interlocked.Increment(ref clientDisposed)));

        var secondWait = secondAdapter.WaitUntilReadyAsync(default);
        await requestEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await ((IAsyncDisposable)firstAdapter).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(secondWait.IsCompleted);
        Assert.Equal(0, clientDisposed);
        Assert.Equal(0, applicationCleanupCount);

        var secondDispose = ((IAsyncDisposable)secondAdapter).DisposeAsync().AsTask();
        Assert.False(secondDispose.IsCompleted);
        Assert.False(secondWait.IsCompleted);
        Assert.Equal(0, clientDisposed);

        finishRequest.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondWait.WaitAsync(TimeSpan.FromSeconds(10)));
        await secondDispose.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, clientDisposed);
        Assert.Equal(1, applicationCleanupCount);
    }

    // valuecard protects cleanup reentry through both registration release and direct owner disposal from self-awaiting.
    // fails_when either callback reentry deadlocks or causes application cleanup to run more than once.
    // why_new cleanup callbacks can reach their owning lifetime; seam final registration release reenters both public disposal paths.
    [Fact]
    public async Task CleanupCallbackCanReenterRegistrationAndOwnerDisposalWithoutWaitingOnItself()
    {
        EvidenceAspireApplicationLifetime? lifetime = null;
        IAsyncDisposable? registration = null;
        var cleanupCount = 0;
        lifetime = new EvidenceAspireApplicationLifetime(async () =>
        {
            Interlocked.Increment(ref cleanupCount);
            await registration!.DisposeAsync();
            await lifetime!.DisposeAsync();
        });
        registration = lifetime.Acquire();

        await registration.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await registration.DisposeAsync();
        await lifetime.DisposeAsync();

        Assert.Equal(1, cleanupCount);
    }

    // valuecard protects parent cleanup from a nested cleanup callback reentering and awaiting the parent's in-progress result.
    // fails_when AsyncLocal cleanup ancestry is lost and the parent waits on its own completion.
    // why_new cleanup callbacks can dispose another lifetime; seam nested owner callbacks and a bounded completion assertion.
    [Fact]
    public async Task NestedCleanupCallbackCanReenterOwnerThroughParentCleanupFlow()
    {
        EvidenceAspireApplicationLifetime? outer = null;
        EvidenceAspireApplicationLifetime? inner = null;
        var outerCleanupCount = 0;
        var innerCleanupCount = 0;
        outer = new EvidenceAspireApplicationLifetime(async () =>
        {
            Interlocked.Increment(ref outerCleanupCount);
            await inner!.DisposeAsync();
        });
        inner = new EvidenceAspireApplicationLifetime(async () =>
        {
            Interlocked.Increment(ref innerCleanupCount);
            await outer.DisposeAsync();
        });

        await outer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, outerCleanupCount);
        Assert.Equal(1, innerCleanupCount);
    }

    // valuecard protects atomic closure of registration admission against concurrent owner disposal.
    // fails_when the race admits after closure, rejects a registration linearized before closure, hangs, or executes cleanup more than once.
    // why_new callers may acquire while owner shutdown begins; seam bounded simultaneous acquire/dispose starts over repeated fresh owners.
    [Fact]
    public async Task AcquireRacingDirectDisposal_EitherAcquiresBeforeClosureOrObservesClosure()
    {
        for (var iteration = 0; iteration < 24; iteration++)
        {
            var cleanupCount = 0;
            var lifetime = new EvidenceAspireApplicationLifetime(() =>
            {
                Interlocked.Increment(ref cleanupCount);
                return ValueTask.CompletedTask;
            });
            using var start = new Barrier(3);
            IAsyncDisposable? registration = null;

            var acquire = Task.Run(() =>
            {
                if (!start.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Acquire race did not start.");
                try
                {
                    registration = lifetime.Acquire();
                }
                catch (ObjectDisposedException)
                {
                    // Disposal won admission; this is the other valid linearized outcome.
                }
            });
            var dispose = Task.Run(async () =>
            {
                if (!start.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Dispose race did not start.");
                await lifetime.DisposeAsync();
            });

            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
            await Task.WhenAll(acquire, dispose).WaitAsync(TimeSpan.FromSeconds(10));
            if (registration is not null) await registration.DisposeAsync();

            Assert.Throws<ObjectDisposedException>(() => lifetime.Acquire());
            Assert.Equal(1, cleanupCount);
        }
    }

    // valuecard protects the constructor's required cleanup callback invariant.
    // fails_when a null callback is accepted and can fail later during disposal.
    // why_new cleanup is required for every lifetime; seam null delegate passed through the internal constructor.
    [Fact]
    public void ConstructorRejectsNullApplicationCleanup()
    {
        Assert.Throws<ArgumentNullException>(() => new EvidenceAspireApplicationLifetime(null!));
    }

    private static IEvidenceResourceReadiness CreateReadiness(Func<ValueTask> disposeRegistration, HttpMessageHandler handler) =>
        EvidenceAspireApplication.CreateHttpReadinessCore(
            "web",
            "/healthz",
            TimeSpan.FromSeconds(30),
            HttpReadinessSettings.Snapshot(new()),
            _ => Task.CompletedTask,
            _ => Task.FromResult<string?>("http://localhost/"),
            _ => new HttpClient(handler),
            disposeRegistration,
            TimeProvider.System);

    private sealed class DelegateHandler(Func<CancellationToken, Task<HttpResponseMessage>> send, Action dispose) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing) dispose();
            base.Dispose(disposing);
        }
    }
}
