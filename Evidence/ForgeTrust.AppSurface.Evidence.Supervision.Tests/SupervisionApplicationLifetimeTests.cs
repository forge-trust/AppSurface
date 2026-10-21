using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Exercises coordinator task ownership with real barriers and no native process authority.</summary>
public sealed class SupervisionApplicationLifetimeTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task StopBeforeStartClosesAdmissionAndSharesOneSuccessfulCleanupTask()
    {
        var ledger = new SupervisionWorkRegistry();
        var lifetime = new SupervisionApplicationLifetime(ledger);
        var stopCalls = 0;

        var stop = lifetime.StopAndJoinAsync(_ => { stopCalls++; return Task.CompletedTask; }, default);
        await stop.WaitAsync(Guard);

        Assert.Same(stop, lifetime.StopAndJoinAsync(_ => throw new InvalidOperationException(), default));
        Assert.Throws<EvidenceAdmissionException>(() =>
        {
            _ = lifetime.StartAsync(_ => Task.FromResult("unexpected"), default);
        });
        Assert.Equal(1, stopCalls);
        Assert.True(lifetime.Joined);
        Assert.False(lifetime.Failed);
        Assert.False(ledger.IsWorkloadAdmissionClosed);
    }

    [Fact]
    public async Task StartAttemptIsConsumedBeforeNullCallbackValidation()
    {
        var ledger = new SupervisionWorkRegistry();
        var lifetime = new SupervisionApplicationLifetime(ledger);

        var first = lifetime.StartAsync<string>(null!, default);
        await Assert.ThrowsAsync<EvidenceAdmissionException>(() => first.WaitAsync(Guard));
        Assert.Throws<EvidenceAdmissionException>(() =>
        {
            _ = lifetime.StartAsync(_ => Task.FromResult("second"), default);
        });

        await lifetime.StopAndJoinAsync(_ => Task.CompletedTask, default).WaitAsync(Guard);
        Assert.True(lifetime.Failed);
        Assert.True(lifetime.Joined);
        Assert.Equal(SupervisionWorkFailure.None, ledger.FirstFailure);
    }

    [Fact]
    public async Task StartFailureStaysVisibleButDoesNotFaultSuccessfulCleanup()
    {
        var ledger = new SupervisionWorkRegistry();
        var lifetime = new SupervisionApplicationLifetime(ledger);
        var start = lifetime.StartAsync<string>(_ => Task.FromException<string>(new IOException("private-start-canary")), default);
        var startError = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => start.WaitAsync(Guard));
        Assert.DoesNotContain("private-start-canary", startError.ToString());

        var stop = lifetime.StopAndJoinAsync(_ => Task.CompletedTask, default);
        await stop.WaitAsync(Guard);

        Assert.True(lifetime.Failed);
        Assert.True(lifetime.Joined);
        Assert.Equal(SupervisionWorkFailure.None, ledger.FirstFailure);
        Assert.Equal(0, ledger.ActiveWorkloads);
    }

    [Fact]
    public async Task StartRegistrationRejectedByClosedSharedRegistryIsStickyAndNeverDispatches()
    {
        var ledger = new SupervisionWorkRegistry();
        Assert.True(await ledger.CloseAndJoinWorkloadsAsync().WaitAsync(Guard));
        var lifetime = new SupervisionApplicationLifetime(ledger);
        var startCalls = 0;
        var stopCalls = 0;

        var start = lifetime.StartAsync(_ =>
        {
            startCalls++;
            return Task.FromResult("unexpected");
        }, default);
        await Assert.ThrowsAsync<EvidenceAdmissionException>(() => start.WaitAsync(Guard));

        Assert.Equal(0, startCalls);
        Assert.True(lifetime.Failed);
        Assert.Equal(0, ledger.ActiveWorkloads);
        Assert.Equal(SupervisionWorkFailure.None, ledger.FirstFailure);

        await lifetime.StopAndJoinAsync(_ =>
        {
            stopCalls++;
            return Task.CompletedTask;
        }, default).WaitAsync(Guard);

        Assert.Equal(1, stopCalls);
        Assert.True(lifetime.Joined);
        Assert.True(lifetime.Failed);
        Assert.Equal(0, ledger.ActiveWorkloads);
        Assert.Equal(SupervisionWorkFailure.None, ledger.FirstFailure);
    }

    [Fact]
    public async Task PendingLateStartIsJoinedBeforeTheSecondStopCall()
    {
        var lifetime = new SupervisionApplicationLifetime(new SupervisionWorkRegistry());
        var entered = Signal();
        var releaseStart = Signal();
        var secondStopEntered = Signal();
        var releaseSecondStop = Signal();
        var createdLateResource = false;
        var stopCalls = 0;
        var start = lifetime.StartAsync(async _ =>
        {
            entered.TrySetResult();
            await releaseStart.Task;
            createdLateResource = true;
            return "late-resource";
        }, default);
        await entered.Task.WaitAsync(Guard);

        var stop = lifetime.StopAndJoinAsync(async _ =>
        {
            if (Interlocked.Increment(ref stopCalls) == 2)
            {
                secondStopEntered.TrySetResult();
                await releaseSecondStop.Task;
            }
        }, default);
        try
        {
            Assert.Equal(1, stopCalls);
            Assert.False(start.IsCompleted);
            Assert.False(stop.IsCompleted);
            Assert.False(lifetime.Joined);

            releaseStart.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Guard));
            await secondStopEntered.Task.WaitAsync(Guard);
            Assert.True(createdLateResource);
            Assert.Equal(2, stopCalls);
            Assert.False(stop.IsCompleted);
            Assert.False(lifetime.Joined);

            releaseSecondStop.TrySetResult();
            await stop.WaitAsync(Guard);
            Assert.True(lifetime.Joined);
            Assert.True(lifetime.Failed);
        }
        finally
        {
            releaseStart.TrySetResult();
            releaseSecondStop.TrySetResult();
            await IgnoreCleanupFailure(start);
            await IgnoreCleanupFailure(stop);
        }
    }

    [Fact]
    public async Task StopDoesNotDetachStartThatIgnoresCancellation()
    {
        var lifetime = new SupervisionApplicationLifetime(new SupervisionWorkRegistry());
        var entered = Signal();
        var release = Signal();
        var start = lifetime.StartAsync(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return 7;
        }, default);
        await entered.Task.WaitAsync(Guard);

        var stop = lifetime.StopAndJoinAsync(_ => Task.CompletedTask, default);
        try
        {
            Assert.False(start.IsCompleted);
            Assert.False(stop.IsCompleted);
            Assert.False(lifetime.Joined);

            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Guard));
            await stop.WaitAsync(Guard);

            Assert.True(lifetime.Joined);
            Assert.True(lifetime.Failed);
        }
        finally
        {
            release.TrySetResult();
            await IgnoreCleanupFailure(start);
            await IgnoreCleanupFailure(stop);
        }
    }

    [Fact]
    public async Task ReadinessCancellationClosesAdmissionButSuccessfulCleanupStillCompletes()
    {
        var lifetime = new SupervisionApplicationLifetime(new SupervisionWorkRegistry());
        await lifetime.StartAsync(_ => Task.FromResult("started"), default).WaitAsync(Guard);
        using var readinessCancellation = new CancellationTokenSource();
        var waitEntered = Signal();
        var releaseWait = Signal();
        var readiness = lifetime.WaitResourceAsync("database", async _ =>
        {
            waitEntered.TrySetResult();
            await releaseWait.Task;
            return true;
        }, readinessCancellation.Token);
        await waitEntered.Task.WaitAsync(Guard);

        readinessCancellation.Cancel();
        Assert.True(lifetime.Failed);
        Assert.Throws<EvidenceAdmissionException>(() =>
        {
            _ = lifetime.WaitResourceAsync("cache", _ => Task.FromResult(true), default);
        });

        var stop = lifetime.StopAndJoinAsync(_ => Task.CompletedTask, default);
        try
        {
            Assert.False(readiness.IsCompleted);
            Assert.False(stop.IsCompleted);
            Assert.False(lifetime.Joined);
            releaseWait.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readiness.WaitAsync(Guard));
            await stop.WaitAsync(Guard);

            Assert.True(lifetime.Joined);
            Assert.True(lifetime.Failed);
        }
        finally
        {
            releaseWait.TrySetResult();
            await IgnoreCleanupFailure(readiness);
            await IgnoreCleanupFailure(stop);
        }
    }

    [Fact]
    public async Task ReadinessFailureStaysVisibleWithoutPoisoningTheSharedLedgerOrCleanup()
    {
        var ledger = new SupervisionWorkRegistry();
        var lifetime = new SupervisionApplicationLifetime(ledger);
        await lifetime.StartAsync(_ => Task.FromResult("started"), default).WaitAsync(Guard);
        var readiness = lifetime.WaitResourceAsync<string>("database",
            _ => Task.FromException<string>(new IOException("private-readiness-canary")), default);

        var readinessError = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => readiness.WaitAsync(Guard));
        Assert.DoesNotContain("private-readiness-canary", readinessError.ToString());
        Assert.True(lifetime.Failed);
        Assert.Equal(SupervisionWorkFailure.None, ledger.FirstFailure);

        await lifetime.StopAndJoinAsync(_ => Task.CompletedTask, default).WaitAsync(Guard);
        Assert.True(lifetime.Joined);
        Assert.True(lifetime.Failed);
        Assert.Equal(SupervisionWorkFailure.None, ledger.FirstFailure);
    }

    [Fact]
    public async Task CallerCancellationClosesAdmissionWithoutReplacingTheCleanupToken()
    {
        var lifetime = new SupervisionApplicationLifetime(new SupervisionWorkRegistry());
        using var startCancellation = new CancellationTokenSource();
        using var cleanupCancellation = new CancellationTokenSource();
        await lifetime.StartAsync(_ => Task.FromResult(3), startCancellation.Token).WaitAsync(Guard);

        startCancellation.Cancel();
        Assert.True(lifetime.Failed);
        Assert.Throws<EvidenceAdmissionException>(() =>
        {
            _ = lifetime.WaitResourceAsync("late", _ => Task.FromResult(4), default);
        });

        var receivedCleanupToken = default(CancellationToken);
        var stop = lifetime.StopAndJoinAsync(token =>
        {
            receivedCleanupToken = token;
            return Task.CompletedTask;
        }, cleanupCancellation.Token);
        await stop.WaitAsync(Guard);

        Assert.Equal(cleanupCancellation.Token, receivedCleanupToken);
        Assert.True(lifetime.Failed);
        Assert.True(lifetime.Joined);
    }

    [Fact]
    public async Task ReadinessIsSuccessfulStartOnlyAndUniqueByOrdinalResourceId()
    {
        var ledger = new SupervisionWorkRegistry();
        var lifetime = new SupervisionApplicationLifetime(ledger);
        Assert.Throws<EvidenceAdmissionException>(() =>
        {
            _ = lifetime.WaitResourceAsync("resource", _ => Task.FromResult(1), default);
        });

        await lifetime.StartAsync(_ => Task.FromResult("started"), default).WaitAsync(Guard);
        var invalidResourceCallbacks = 0;
        foreach (var resourceId in new[] { string.Empty, " ", "\t\n" })
        {
            Assert.Throws<EvidenceAdmissionException>(() =>
            {
                _ = lifetime.WaitResourceAsync(resourceId, _ =>
                {
                    invalidResourceCallbacks++;
                    return Task.FromResult(true);
                }, default);
            });
        }
        Assert.Equal(0, invalidResourceCallbacks);

        var lateDisposerCalls = 0;
        Assert.Throws<EvidenceAdmissionException>(() => lifetime.RegisterDisposer(_ =>
        {
            lateDisposerCalls++;
            return Task.CompletedTask;
        }));

        Assert.Equal(2, await lifetime.WaitResourceAsync("resource", _ => Task.FromResult(2), default).WaitAsync(Guard));
        Assert.Equal(3, await lifetime.WaitResourceAsync("Resource", _ => Task.FromResult(3), default).WaitAsync(Guard));
        Assert.Throws<EvidenceAdmissionException>(() =>
        {
            _ = lifetime.WaitResourceAsync("resource", _ => Task.FromResult(4), default);
        });

        await lifetime.StopAndJoinAsync(_ => Task.CompletedTask, default).WaitAsync(Guard);
        Assert.Equal(0, lateDisposerCalls);
        Assert.Equal(SupervisionWorkFailure.None, ledger.FirstFailure);
    }

    [Fact]
    public async Task ReadinessRegistrationRejectedByClosedSharedRegistryIsStickyAndNeverDispatches()
    {
        var ledger = new SupervisionWorkRegistry();
        var lifetime = new SupervisionApplicationLifetime(ledger);
        await lifetime.StartAsync(_ => Task.FromResult("started"), default).WaitAsync(Guard);
        Assert.True(await ledger.CloseAndJoinWorkloadsAsync().WaitAsync(Guard));
        var readinessCalls = 0;
        var stopCalls = 0;

        var readiness = lifetime.WaitResourceAsync("database", _ =>
        {
            readinessCalls++;
            return Task.FromResult(true);
        }, default);
        await Assert.ThrowsAsync<EvidenceAdmissionException>(() => readiness.WaitAsync(Guard));

        Assert.Equal(0, readinessCalls);
        Assert.True(lifetime.Failed);
        Assert.Equal(0, ledger.ActiveWorkloads);
        Assert.Equal(SupervisionWorkFailure.None, ledger.FirstFailure);

        await lifetime.StopAndJoinAsync(_ =>
        {
            stopCalls++;
            return Task.CompletedTask;
        }, default).WaitAsync(Guard);

        Assert.Equal(1, stopCalls);
        Assert.True(lifetime.Joined);
        Assert.True(lifetime.Failed);
        Assert.Equal(0, ledger.ActiveWorkloads);
        Assert.Equal(SupervisionWorkFailure.None, ledger.FirstFailure);
    }

    [Fact]
    public async Task StopJoinsOnlyCoordinatorWorkAndNeverWaitsForControlHandlers()
    {
        var ledger = new SupervisionWorkRegistry();
        var lifetime = new SupervisionApplicationLifetime(ledger);
        var unrelatedWork = ledger.BeginWorkload();
        using var control = ledger.BeginControl();
        await lifetime.StartAsync(_ => Task.FromResult("started"), default).WaitAsync(Guard);
        await lifetime.WaitResourceAsync("resource", _ => Task.FromResult(true), default).WaitAsync(Guard);

        await lifetime.StopAndJoinAsync(_ => Task.CompletedTask, default).WaitAsync(Guard);

        Assert.True(lifetime.Joined);
        Assert.Equal(1, ledger.ActiveWorkloads);
        Assert.Equal(1, ledger.ActiveControls);
        Assert.False(ledger.IsWorkloadAdmissionClosed);
        Assert.False(ledger.IsControlAdmissionClosed);
        Assert.Equal(SupervisionWorkFailure.None, ledger.FirstFailure);
        Assert.True(unrelatedWork.Complete());
    }

    [Fact]
    public async Task ConcurrentStopReplayReturnsTheSameTaskAndCallsStopOnce()
    {
        var lifetime = new SupervisionApplicationLifetime(new SupervisionWorkRegistry());
        await lifetime.StartAsync(_ => Task.FromResult("started"), default).WaitAsync(Guard);
        var stopEntered = Signal();
        var releaseStop = Signal();
        var stopCalls = 0;

        var stop = lifetime.StopAndJoinAsync(async _ =>
        {
            Interlocked.Increment(ref stopCalls);
            stopEntered.TrySetResult();
            await releaseStop.Task;
        }, default);
        try
        {
            await stopEntered.Task.WaitAsync(Guard);
            var replay = lifetime.StopAndJoinAsync(_ => throw new InvalidOperationException("replay"), default);
            Assert.Same(stop, replay);
            Assert.Equal(1, stopCalls);
            Assert.False(stop.IsCompleted);

            releaseStop.TrySetResult();
            await stop.WaitAsync(Guard);
            Assert.True(lifetime.Joined);
            Assert.False(lifetime.Failed);
        }
        finally
        {
            releaseStop.TrySetResult();
            await IgnoreCleanupFailure(stop);
        }
    }

    [Fact]
    public async Task StopFailureIsSanitizedStickyAndReplayedAsTheSameTask()
    {
        var lifetime = new SupervisionApplicationLifetime(new SupervisionWorkRegistry());
        var calls = 0;
        var stop = lifetime.StopAndJoinAsync(_ =>
        {
            calls++;
            return Task.FromException(new IOException("private-stop-canary"));
        }, default);

        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => stop.WaitAsync(Guard));
        Assert.Same(stop, lifetime.StopAndJoinAsync(_ => Task.CompletedTask, default));
        Assert.DoesNotContain("private-stop-canary", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(1, calls);
        Assert.True(lifetime.Failed);
        Assert.True(lifetime.Joined);
    }

    [Fact]
    public async Task CleanupTokenCancellationFaultsSharedStopOnlyAfterItsCallbackReturns()
    {
        var lifetime = new SupervisionApplicationLifetime(new SupervisionWorkRegistry());
        using var cleanupCancellation = new CancellationTokenSource();
        var stopEntered = Signal();
        var releaseStop = Signal();
        var receivedToken = default(CancellationToken);
        var stop = lifetime.StopAndJoinAsync(async token =>
        {
            receivedToken = token;
            stopEntered.TrySetResult();
            await releaseStop.Task;
        }, cleanupCancellation.Token);
        try
        {
            await stopEntered.Task.WaitAsync(Guard);
            cleanupCancellation.Cancel();
            Assert.True(lifetime.Failed);
            Assert.False(stop.IsCompleted);
            Assert.False(lifetime.Joined);

            releaseStop.TrySetResult();
            await Assert.ThrowsAsync<EvidenceAdmissionException>(() => stop.WaitAsync(Guard));
            Assert.Equal(cleanupCancellation.Token, receivedToken);
            Assert.True(lifetime.Joined);
        }
        finally
        {
            releaseStop.TrySetResult();
            await IgnoreCleanupFailure(stop);
        }
    }

    [Fact]
    public async Task RegisteredDisposerRunsAfterPreDisposerJoinsAndMayUseItsOwnGuard()
    {
        var lifetime = new SupervisionApplicationLifetime(new SupervisionWorkRegistry());
        Task<string>? start = null;
        Task<bool>? readiness = null;
        var disposerRan = false;
        var joinedBeforeDisposer = false;
        var guardPassed = false;
        var selfJoinRejected = false;
        lifetime.RegisterDisposer(disposeToken =>
        {
            disposerRan = true;
            joinedBeforeDisposer = start!.IsCompleted && readiness!.IsCompleted;
            try
            {
                lifetime.RequireJoinedCleanupPhase();
                guardPassed = true;
            }
            catch (EvidenceAdmissionException) { }
            try
            {
                _ = lifetime.StopAndJoinAsync(__ => Task.CompletedTask, default);
            }
            catch (EvidenceAdmissionException) { selfJoinRejected = true; }
            return Task.CompletedTask;
        });

        Assert.Throws<EvidenceAdmissionException>(lifetime.RequireJoinedCleanupPhase);
        start = lifetime.StartAsync(_ => Task.FromResult("started"), default);
        await start!.WaitAsync(Guard);
        var readinessEntered = Signal();
        var releaseReadiness = Signal();
        readiness = lifetime.WaitResourceAsync("resource", async _ =>
        {
            readinessEntered.TrySetResult();
            await releaseReadiness.Task;
            return true;
        }, default);
        await readinessEntered.Task.WaitAsync(Guard);

        var stopEntered = Signal();
        var stop = lifetime.StopAndJoinAsync(_ =>
        {
            stopEntered.TrySetResult();
            return Task.CompletedTask;
        }, default);
        try
        {
            await stopEntered.Task.WaitAsync(Guard);
            Assert.False(disposerRan);
            Assert.False(stop.IsCompleted);

            releaseReadiness.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readiness!.WaitAsync(Guard));
            await stop.WaitAsync(Guard);

            Assert.True(disposerRan);
            Assert.True(joinedBeforeDisposer);
            Assert.True(guardPassed);
            Assert.True(selfJoinRejected);
            Assert.True(lifetime.Joined);
            Assert.True(lifetime.Failed);
        }
        finally
        {
            releaseReadiness.TrySetResult();
            await IgnoreCleanupFailure(readiness!);
            await IgnoreCleanupFailure(stop);
        }
    }

    [Fact]
    public async Task JoinedCleanupPhaseIsRejectedInsideStartAndReadinessProcedures()
    {
        var lifetime = new SupervisionApplicationLifetime(new SupervisionWorkRegistry());
        var startGuardRejected = false;
        var readinessGuardRejected = false;

        await lifetime.StartAsync(_ =>
        {
            try { lifetime.RequireJoinedCleanupPhase(); }
            catch (EvidenceAdmissionException) { startGuardRejected = true; }
            return Task.FromResult("started");
        }, default).WaitAsync(Guard);

        await lifetime.WaitResourceAsync("database", _ =>
        {
            try { lifetime.RequireJoinedCleanupPhase(); }
            catch (EvidenceAdmissionException) { readinessGuardRejected = true; }
            return Task.FromResult(true);
        }, default).WaitAsync(Guard);

        Assert.True(startGuardRejected);
        Assert.True(readinessGuardRejected);
        await lifetime.StopAndJoinAsync(_ => Task.CompletedTask, default).WaitAsync(Guard);
        Assert.True(lifetime.Joined);
        Assert.False(lifetime.Failed);
    }

    [Fact]
    public async Task DisposerFailureFaultsCleanupEvenWhenOriginalStartFailed()
    {
        var lifetime = new SupervisionApplicationLifetime(new SupervisionWorkRegistry());
        lifetime.RegisterDisposer(_ => Task.FromException(new IOException("private-disposer-canary")));
        var start = lifetime.StartAsync<string>(_ => Task.FromException<string>(new IOException("private-start-canary")), default);
        var startError = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => start.WaitAsync(Guard));
        Assert.DoesNotContain("private-start-canary", startError.ToString());

        var stop = lifetime.StopAndJoinAsync(_ => Task.CompletedTask, default);
        var stopError = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => stop.WaitAsync(Guard));
        Assert.DoesNotContain("private-disposer-canary", stopError.ToString());
        Assert.Null(stopError.InnerException);
        Assert.True(lifetime.Failed);
        Assert.True(lifetime.Joined);
    }

    [Fact]
    public async Task ThrowingStopCancellationCallbackCannotDetachStartOrSkipCleanup()
    {
        var lifetime = new SupervisionApplicationLifetime(new SupervisionWorkRegistry());
        using var cleanup = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        var callbackRan = false;
        var stopCalls = 0;
        var disposerRan = false;
        lifetime.RegisterDisposer(token =>
        {
            Assert.Equal(cleanup.Token, token);
            disposerRan = true;
            return Task.CompletedTask;
        });
        var start = lifetime.StartAsync(async token =>
        {
            using var registration = token.Register(() =>
            {
                callbackRan = true;
                throw new IOException("private-stop-signal-canary");
            });
            entered.TrySetResult();
            await release.Task;
            return "late-start";
        }, default);
        await entered.Task.WaitAsync(Guard);
        var stop = lifetime.StopAndJoinAsync(token =>
        {
            Assert.Equal(cleanup.Token, token);
            stopCalls++;
            return Task.CompletedTask;
        }, cleanup.Token);
        try
        {
            Assert.True(callbackRan);
            Assert.Equal(1, stopCalls);
            Assert.False(stop.IsCompleted);
            Assert.False(disposerRan);
            Assert.False(lifetime.Joined);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Guard));
            var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => stop.WaitAsync(Guard));
            Assert.Equal("ASEVD410", error.Code);
            Assert.DoesNotContain("private-stop-signal-canary", error.ToString());
            Assert.Null(error.InnerException);
            Assert.Equal(2, stopCalls);
            Assert.True(disposerRan);
            Assert.True(lifetime.Failed);
            Assert.True(lifetime.Joined);
        }
        finally
        {
            release.TrySetResult();
            await IgnoreCleanupFailure(start);
            await IgnoreCleanupFailure(stop);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedDisposersStayOwnedAfterAnEarlierDisposerFails(bool firstFails)
    {
        var lifetime = new SupervisionApplicationLifetime(new SupervisionWorkRegistry());
        using var cleanup = new CancellationTokenSource();
        var order = new List<int>();
        var secondEntered = Signal();
        var releaseSecond = Signal();
        lifetime.RegisterDisposer(token =>
        {
            Assert.Equal(cleanup.Token, token);
            order.Add(1);
            return firstFails ? Task.FromException(new IOException("private-disposer-canary")) : Task.CompletedTask;
        });
        lifetime.RegisterDisposer(async token =>
        {
            Assert.Equal(cleanup.Token, token);
            lifetime.RequireJoinedCleanupPhase();
            order.Add(2);
            secondEntered.TrySetResult();
            await releaseSecond.Task;
            order.Add(3);
        });
        await lifetime.StartAsync(_ => Task.FromResult("started"), default).WaitAsync(Guard);
        var stop = lifetime.StopAndJoinAsync(_ => Task.CompletedTask, cleanup.Token);
        try
        {
            await secondEntered.Task.WaitAsync(Guard);
            Assert.Equal(new[] { 1, 2 }, order);
            Assert.False(stop.IsCompleted);
            Assert.False(lifetime.Joined);
            Assert.Same(stop, lifetime.StopAndJoinAsync(_ => throw new InvalidOperationException("replayed-stop"), default));
            releaseSecond.TrySetResult();
            if (firstFails)
            {
                var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => stop.WaitAsync(Guard));
                Assert.Equal("ASEVD410", error.Code);
                Assert.DoesNotContain("private-disposer-canary", error.ToString());
                Assert.Null(error.InnerException);
            }
            else await stop.WaitAsync(Guard);
            Assert.Equal(new[] { 1, 2, 3 }, order);
            Assert.True(lifetime.Joined);
            Assert.Equal(firstFails, lifetime.Failed);
            Assert.Same(stop, lifetime.StopAndJoinAsync(_ => throw new InvalidOperationException("replayed-stop"), default));
        }
        finally
        {
            releaseSecond.TrySetResult();
            await IgnoreCleanupFailure(stop);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingStopCallbackOrTaskStillRunsDisposersButCannotCompleteCleanupSuccessfully(bool missingCallback)
    {
        var ledger = new SupervisionWorkRegistry();
        var lifetime = new SupervisionApplicationLifetime(ledger);
        var disposed = false;
        lifetime.RegisterDisposer(_ => { disposed = true; return Task.CompletedTask; });
        await lifetime.StartAsync(_ => Task.FromResult("started"), default).WaitAsync(Guard);
        var stop = lifetime.StopAndJoinAsync(missingCallback ? null! : _ => null!, default);
        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => stop.WaitAsync(Guard));
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
        Assert.True(disposed);
        Assert.Equal(!missingCallback, lifetime.Joined);
        Assert.True(lifetime.Failed);
        Assert.Same(stop, lifetime.StopAndJoinAsync(_ => Task.CompletedTask, default));
        Assert.Equal(0, ledger.ActiveWorkloads);
        Assert.Equal(SupervisionWorkFailure.None, ledger.FirstFailure);
    }

    [Fact]
    public async Task MissingDisposerTaskCannotSkipTheNextDisposerOrReleaseItsJoin()
    {
        var ledger = new SupervisionWorkRegistry();
        var lifetime = new SupervisionApplicationLifetime(ledger);
        var entered = Signal();
        var release = Signal();
        var order = new List<int>();
        lifetime.RegisterDisposer(_ => { order.Add(1); return null!; });
        lifetime.RegisterDisposer(async _ =>
        {
            order.Add(2);
            entered.TrySetResult();
            await release.Task;
            order.Add(3);
        });
        await lifetime.StartAsync(_ => Task.FromResult(true), default).WaitAsync(Guard);
        var stop = lifetime.StopAndJoinAsync(_ => Task.CompletedTask, default);
        try
        {
            await entered.Task.WaitAsync(Guard);
            Assert.Equal(new[] { 1, 2 }, order);
            Assert.False(stop.IsCompleted);
            Assert.False(lifetime.Joined);
        }
        finally
        {
            release.TrySetResult();
            await Assert.ThrowsAsync<EvidenceAdmissionException>(() => stop.WaitAsync(Guard));
        }
        Assert.Equal(new[] { 1, 2, 3 }, order);
        Assert.True(lifetime.Joined);
        Assert.True(lifetime.Failed);
        Assert.Equal(0, ledger.ActiveWorkloads);
        Assert.Equal(SupervisionWorkFailure.None, ledger.FirstFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyCancelledOperationTokenNeverDispatchesAndDoesNotPoisonCleanup(bool readiness)
    {
        var ledger = new SupervisionWorkRegistry();
        var lifetime = new SupervisionApplicationLifetime(ledger);
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        if (readiness)
            await lifetime.StartAsync(_ => Task.FromResult(true), default).WaitAsync(Guard);
        var operation = readiness
            ? lifetime.WaitResourceAsync("resource", _ => { calls++; return Task.FromResult(true); }, cancellation.Token)
            : lifetime.StartAsync(_ => { calls++; return Task.FromResult(true); }, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(Guard));
        Assert.Equal(0, calls);
        await lifetime.StopAndJoinAsync(_ => Task.CompletedTask, default).WaitAsync(Guard);
        Assert.True(lifetime.Failed);
        Assert.True(lifetime.Joined);
        Assert.Equal(0, ledger.ActiveWorkloads);
        Assert.Equal(SupervisionWorkFailure.None, ledger.FirstFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOperationTaskReleasesItsRegistrationButKeepsTheLifetimeFailed(bool readiness)
    {
        var ledger = new SupervisionWorkRegistry();
        var lifetime = new SupervisionApplicationLifetime(ledger);
        if (readiness)
            await lifetime.StartAsync(_ => Task.FromResult(true), default).WaitAsync(Guard);
        var calls = 0;
        Task<bool> MissingTask(CancellationToken _)
        {
            calls++;
            return null!;
        }
        var operation = readiness
            ? lifetime.WaitResourceAsync("resource", MissingTask, default)
            : lifetime.StartAsync(MissingTask, default);
        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => operation.WaitAsync(Guard));
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
        Assert.Equal(1, calls);
        Assert.Equal(0, ledger.ActiveWorkloads);
        Assert.Equal(SupervisionWorkFailure.None, ledger.FirstFailure);
        await lifetime.StopAndJoinAsync(_ => Task.CompletedTask, default).WaitAsync(Guard);
        Assert.True(lifetime.Joined);
        Assert.True(lifetime.Failed);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task IgnoreCleanupFailure(Task task)
    {
        try { await task.WaitAsync(Guard); }
        catch (Exception) { }
    }
}
