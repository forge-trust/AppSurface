using ForgeTrust.AppSurface.Config;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class AppSurfaceLocalSecretKeyMigrationTests
{
    [Fact]
    public void Run_ShouldPublishIndexBeforeDeletingSourceAndCommitEveryState()
    {
        var backend = new RecordingMigrationBackend("source-secret");
        backend.Values["legacy.key"] = backend.Values["source"];
        backend.Values.Remove("source");

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "legacy.key", "appsurface:MyApp:Development:Payments:ApiKey",
            AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Complete, result.State);
        Assert.Equal(
            new[] { "lease", "read-journal", "journal:Prepared", "read:legacy.key", "validate:Prepared", "read:appsurface:MyApp:Development:Payments:ApiKey",
                "write:appsurface:MyApp:Development:Payments:ApiKey", "journal:DestinationWritten",
                "validate:DestinationWritten", "read:appsurface:MyApp:Development:Payments:ApiKey", "read:legacy.key", "journal:DestinationVerified",
                "validate:DestinationVerified", "publish-index", "journal:SourceDeletePending", "validate:SourceDeletePending",
                "read:appsurface:MyApp:Development:Payments:ApiKey", "read:legacy.key", "delete:legacy.key", "read:legacy.key", "publish-index", "journal:Complete" },
            backend.Events);
        Assert.Equal("source-secret", backend.Values["appsurface:MyApp:Development:Payments:ApiKey"]);
        Assert.DoesNotContain("legacy.key", backend.Values.Keys);
    }

    [Fact]
    public void Run_ShouldRefuseDifferentExistingDestinationWithoutMutation()
    {
        var backend = new RecordingMigrationBackend("source-secret");
        backend.Values["destination"] = "other-secret";

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Prepared, result.State);
        Assert.Equal("local-secret-migration-destination-mismatch", result.Diagnostic?.Code);
        Assert.DoesNotContain(backend.Events, eventName => eventName.StartsWith("write:", StringComparison.Ordinal));
        Assert.DoesNotContain(backend.Events, eventName => eventName.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_ShouldKeepSourceWhenVerificationRereadChanges()
    {
        var backend = new RecordingMigrationBackend("source-secret")
        {
            SourceReread = "changed-secret"
        };

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.DestinationWritten, result.State);
        Assert.Equal("local-secret-migration-verification-failed", result.Diagnostic?.Code);
        Assert.Contains("source", backend.Values.Keys);
        Assert.DoesNotContain(backend.Events, eventName => eventName.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_ShouldResumeFromDurableDestinationWrittenJournalWithSameMigrationId()
    {
        var backend = new RecordingMigrationBackend("source-secret")
        {
            Journal = new AppSurfaceLocalSecretMigrationJournal(
                "stable-id", "MyApp", "Development", null, "source", "destination",
                AppSurfaceLocalSecretMigrationState.DestinationWritten)
        };
        backend.Values["destination"] = "source-secret";

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Equal("stable-id", result.MigrationId);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Complete, result.State);
        Assert.Contains("journal:Complete", backend.Events);
    }

    [Fact]
    public void Run_ShouldContinueWhenDestinationAlreadyContainsTheSameValue()
    {
        var backend = new RecordingMigrationBackend("source-secret");
        backend.Values["destination"] = "source-secret";

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Complete, result.State);
        Assert.Contains("journal:DestinationWritten", backend.Events);
        Assert.DoesNotContain("write:destination", backend.Events);
        Assert.DoesNotContain("source", backend.Values.Keys);
    }

    [Theory]
    [InlineData(AppSurfaceLocalSecretMigrationState.Prepared)]
    [InlineData(AppSurfaceLocalSecretMigrationState.DestinationWritten)]
    [InlineData(AppSurfaceLocalSecretMigrationState.DestinationVerified)]
    [InlineData(AppSurfaceLocalSecretMigrationState.SourceDeletePending)]
    public void Run_ShouldReturnSafeResumePointWhenJournalTransitionFails(AppSurfaceLocalSecretMigrationState state)
    {
        var backend = new RecordingMigrationBackend("source-secret") { FailCommitAt = state };
        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal(state switch
        {
            AppSurfaceLocalSecretMigrationState.Prepared => AppSurfaceLocalSecretMigrationState.Prepared,
            _ => state - 1
        }, result.State);
        Assert.Equal("local-secret-migration-io-failure", result.Diagnostic?.Code);
        Assert.Contains("source", backend.Values.Keys);
    }

    [Fact]
    public void Run_ShouldRetainPendingJournalWhenSourceDeletionCannotBeConfirmed()
    {
        var backend = new RecordingMigrationBackend("source-secret") { DeleteLeavesSource = true };

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.SourceDeletePending, result.State);
        Assert.Equal("local-secret-migration-source-delete-unconfirmed", result.Diagnostic?.Code);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.SourceDeletePending, backend.Journal?.State);
    }

    [Fact]
    public void Run_ShouldRejectSameExactIdentifierBeforeAcquiringLease()
    {
        var backend = new RecordingMigrationBackend("source-secret");

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "same", "same", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal("local-secret-migration-same-key", result.Diagnostic?.Code);
        Assert.Empty(backend.Events);
    }

    [Fact]
    public void Run_ShouldRefuseAnActiveJournalForADifferentRequestWithoutTouchingValues()
    {
        var backend = new RecordingMigrationBackend("source-secret")
        {
            Journal = new AppSurfaceLocalSecretMigrationJournal(
                "active-id", "MyApp", "Development", null, "other-source", "other-destination",
                AppSurfaceLocalSecretMigrationState.DestinationWritten)
        };

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Unrecoverable, result.State);
        Assert.Equal("local-secret-migration-journal-conflict", result.Diagnostic?.Code);
        Assert.DoesNotContain(backend.Events, eventName => eventName.StartsWith("read:", StringComparison.Ordinal));
        Assert.DoesNotContain(backend.Events, eventName => eventName.StartsWith("write:", StringComparison.Ordinal));
        Assert.DoesNotContain(backend.Events, eventName => eventName.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_ShouldKeepSourceWhenPendingDestinationDisappears()
    {
        var backend = new RecordingMigrationBackend("source-secret")
        {
            Journal = new AppSurfaceLocalSecretMigrationJournal(
                "pending-id", "MyApp", "Development", null, "source", "destination",
                AppSurfaceLocalSecretMigrationState.SourceDeletePending)
        };

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.SourceDeletePending, result.State);
        Assert.Equal("local-secret-migration-destination-missing", result.Diagnostic?.Code);
        Assert.Contains("source", backend.Values.Keys);
        Assert.DoesNotContain(backend.Events, eventName => eventName.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_ShouldRetainChangedSourceWhenResumingAfterPendingTransitionFailure()
    {
        var backend = new RecordingMigrationBackend("source-secret") { FailCommitAt = AppSurfaceLocalSecretMigrationState.SourceDeletePending };

        var first = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.Unavailable, first.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.DestinationVerified, first.State);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.DestinationVerified, backend.Journal?.State);

        backend.FailCommitAt = null;
        backend.Values["source"] = "writer-changed-source";
        backend.Values["destination"] = "writer-changed-destination";

        var resumed = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, resumed.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.SourceDeletePending, resumed.State);
        Assert.Equal("local-secret-migration-source-destination-changed", resumed.Diagnostic?.Code);
        Assert.Equal("writer-changed-source", backend.Values["source"]);
        Assert.Equal("writer-changed-destination", backend.Values["destination"]);
        Assert.DoesNotContain(backend.Events, eventName => eventName == "delete:source");
    }

    [Fact]
    public void Run_ShouldReturnStructuredFailureWhenMaintenanceLeaseAcquisitionFails()
    {
        var backend = new RecordingMigrationBackend("source-secret") { ThrowOnAcquire = true };

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Prepared, result.State);
        Assert.Equal("local-secret-migration-io-failure", result.Diagnostic?.Code);
    }

    [Fact]
    public void Run_ShouldReportMissingSourceBeforeWritingDestination()
    {
        var backend = new RecordingMigrationBackend("source-secret");
        backend.Values.Remove("source");

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Unrecoverable, result.State);
        Assert.Equal("local-secret-migration-source-missing", result.Diagnostic?.Code);
        Assert.DoesNotContain("destination", backend.Values.Keys);
    }

    [Fact]
    public void Run_ShouldReturnUnrecoverableWhenJournalAlreadyRequiresOperatorRecovery()
    {
        var backend = new RecordingMigrationBackend("source-secret")
        {
            Journal = new AppSurfaceLocalSecretMigrationJournal(
                "stuck-id", "MyApp", "Development", null, "source", "destination",
                AppSurfaceLocalSecretMigrationState.Unrecoverable)
        };

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Unrecoverable, result.State);
        Assert.Equal("local-secret-migration-unrecoverable", result.Diagnostic?.Code);
        Assert.DoesNotContain(backend.Events, eventName => eventName.StartsWith("read:", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_ShouldStartNewJournalAfterMismatchedCompletedJournal()
    {
        var backend = new RecordingMigrationBackend("source-secret")
        {
            Journal = new AppSurfaceLocalSecretMigrationJournal(
                "old-complete", "MyApp", "Development", null, "other-source", "other-destination",
                AppSurfaceLocalSecretMigrationState.Complete)
        };

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.NotEqual("old-complete", result.MigrationId);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Complete, result.State);
        Assert.DoesNotContain("old-complete", backend.Events);
    }

    [Fact]
    public void Run_ShouldReturnSafeFailureWhenJournalReadFails()
    {
        var backend = new RecordingMigrationBackend("source-secret") { ThrowOnReadJournal = true };

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Prepared, result.State);
        Assert.Equal("local-secret-migration-io-failure", result.Diagnostic?.Code);
        Assert.DoesNotContain(backend.Events, eventName => eventName.StartsWith("read:", StringComparison.Ordinal));
        Assert.DoesNotContain(backend.Events, eventName => eventName.StartsWith("write:", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_ShouldRetainSourceWhenInitialReadFails()
    {
        var backend = new RecordingMigrationBackend("source-secret") { FailSourceRead = true };

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Prepared, result.State);
        Assert.Equal("local-secret-migration-io-failure", result.Diagnostic?.Code);
        Assert.DoesNotContain(backend.Events, eventName => eventName.StartsWith("write:", StringComparison.Ordinal));
        Assert.DoesNotContain(backend.Events, eventName => eventName.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_ShouldRetainSourceWhenDestinationWriteFails()
    {
        var backend = new RecordingMigrationBackend("source-secret") { FailDestinationWrite = true };

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Prepared, result.State);
        Assert.Contains("source", backend.Values.Keys);
        Assert.DoesNotContain("destination", backend.Values.Keys);
    }

    [Fact]
    public void Run_ShouldRetainSourceWhenVerificationReadFails()
    {
        var backend = new RecordingMigrationBackend("source-secret") { FailDestinationReadNumber = 2 };

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.DestinationWritten, result.State);
        Assert.Equal("source-secret", backend.Values["destination"]);
        Assert.Contains("source", backend.Values.Keys);
        Assert.DoesNotContain(backend.Events, eventName => eventName.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_ShouldRetainSourceWhenPreDeleteIndexPublicationFails()
    {
        var backend = new RecordingMigrationBackend("source-secret") { FailPublishAt = 1 };

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.DestinationVerified, result.State);
        Assert.Contains("source", backend.Values.Keys);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.DestinationVerified, backend.Journal?.State);
    }

    [Theory]
    [InlineData(AppSurfaceLocalSecretMigrationState.DestinationWritten)]
    [InlineData(AppSurfaceLocalSecretMigrationState.DestinationVerified)]
    [InlineData(AppSurfaceLocalSecretMigrationState.SourceDeletePending)]
    public void Run_ShouldRejectDestinationCollisionOnEveryResumableStage(AppSurfaceLocalSecretMigrationState state)
    {
        var backend = new RecordingMigrationBackend("source-secret")
        {
            Journal = new AppSurfaceLocalSecretMigrationJournal(
                "stable-id", "MyApp", "Development", null, "source", "destination", state),
            CollisionState = state
        };
        backend.Values["destination"] = "source-secret";

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal(state, result.State);
        Assert.Equal("config-key-collision", result.Diagnostic?.Code);
        Assert.Contains("source", backend.Values.Keys);
        Assert.DoesNotContain(backend.Events, eventName => eventName == "delete:source");
    }

    [Fact]
    public void Run_ShouldRetainSourceWhenDeleteFails()
    {
        var backend = new RecordingMigrationBackend("source-secret") { FailDelete = true };

        var result = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.SourceDeletePending, result.State);
        Assert.Contains("source", backend.Values.Keys);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.SourceDeletePending, backend.Journal?.State);
    }

    [Fact]
    public void Run_ShouldReportWhenPostDeleteIndexPublicationFailsWithoutReDeleting()
    {
        var backend = new RecordingMigrationBackend("source-secret") { FailPublishAt = 2 };

        var first = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.Unavailable, first.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.SourceDeletePending, first.State);
        Assert.DoesNotContain("source", backend.Values.Keys);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.SourceDeletePending, backend.Journal?.State);

        var resumed = AppSurfaceLocalSecretMigrationCoordinator.Run(
            backend, "MyApp", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "test-backend");

        Assert.Equal(LocalSecretResultStatus.Found, resumed.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Complete, resumed.State);
        Assert.Null(resumed.Diagnostic);
        Assert.Equal(1, backend.Events.Count(eventName => eventName == "delete:source"));
    }

    private sealed class RecordingMigrationBackend(string sourceValue) : IAppSurfaceLocalSecretMigrationBackend
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal) { ["source"] = sourceValue };
        public List<string> Events { get; } = [];
        public string? SourceReread { get; init; }
        public AppSurfaceLocalSecretMigrationJournal? Journal { get; set; }
        public bool DeleteLeavesSource { get; init; }
        public bool ThrowOnReadJournal { get; init; }
        public AppSurfaceLocalSecretMigrationState? FailCommitAt { get; set; }
        public bool ThrowOnAcquire { get; init; }
        public bool FailSourceRead { get; init; }
        public bool FailDestinationWrite { get; init; }
        public int? FailDestinationReadNumber { get; init; }
        public bool FailDelete { get; init; }
        public int? FailPublishAt { get; init; }
        public AppSurfaceLocalSecretMigrationState? CollisionState { get; init; }
        private int _sourceReads;
        private int _destinationReads;
        private int _publishCount;

        public bool SupportsDurableMigration => true;

        public IDisposable AcquireMaintenanceLease(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Events.Add("lease");
            if (ThrowOnAcquire) throw new IOException("lease acquisition failed");
            return new Lease();
        }

        public AppSurfaceLocalSecretMigrationJournal? ReadJournal()
        {
            Events.Add("read-journal");
            if (ThrowOnReadJournal)
            {
                throw new IOException("journal read failed");
            }

            return Journal;
        }

        public void CommitJournal(AppSurfaceLocalSecretMigrationJournal journal)
        {
            Events.Add($"journal:{journal.State}");
            if (FailCommitAt == journal.State)
            {
                throw new IOException("journal commit failed");
            }
            Journal = journal;
        }

        public string? ReadExact(string storedKey)
        {
            Events.Add($"read:{storedKey}");
            if (storedKey == "source" && FailSourceRead) throw new IOException("source read failed");
            if (storedKey == "destination" && FailDestinationReadNumber is { } readNumber && ++_destinationReads == readNumber)
            {
                throw new IOException("destination read failed");
            }
            if (storedKey == "source" && ++_sourceReads > 1 && SourceReread is not null)
            {
                return SourceReread;
            }

            return Values.GetValueOrDefault(storedKey);
        }

        public void WriteExact(string storedKey, string value)
        {
            Events.Add($"write:{storedKey}");
            if (FailDestinationWrite) throw new IOException("destination write failed");
            Values[storedKey] = value;
        }

        public void DeleteExact(string storedKey)
        {
            Events.Add($"delete:{storedKey}");
            if (FailDelete) throw new IOException("source delete failed");
            if (!DeleteLeavesSource)
            {
                Values.Remove(storedKey);
            }
        }

        public void PublishIndex()
        {
            Events.Add("publish-index");
            if (++_publishCount == FailPublishAt) throw new IOException("index publication failed");
        }

        public void ValidateDestination()
        {
            Events.Add($"validate:{Journal?.State}");
            if (Journal?.State == CollisionState) throw new AppSurfaceLocalSecretMigrationCollisionException();
        }

        private sealed class Lease : IDisposable
        {
            public void Dispose() { }
        }
    }
}
