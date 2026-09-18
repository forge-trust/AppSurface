using System.Text.Json;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class LocalSecretMigrationFailureMatrixTests
{
    // Complete happy-path protocol including all five journal commits, every read, both index publications and delete.
    private static readonly string[] Operations = ["journal-read", "Prepared", "source-read", "destination-read",
        "destination-write", "DestinationWritten", "destination-read", "source-read", "DestinationVerified",
        "index", "SourceDeletePending", "destination-read", "source-read", "source-delete", "source-read", "index", "Complete"];

    public static IEnumerable<object[]> Failures => Enumerable.Range(0, Operations.Length)
        .SelectMany(position => new[] { new object[] { position, false }, new object[] { position, true } });

    [Theory]
    [MemberData(nameof(Failures))]
    public void EveryBoundary_ShouldRollForwardAfterFailureBeforeOrAfterEffect(int position, bool after)
    {
        var backend = new Backend { FaultPosition = position, FaultAfter = after };
        var first = Run(backend);
        Assert.Equal(LocalSecretResultStatus.Unavailable, first.Status);
        Assert.Equal(Operations[position], backend.Events[position]);
        Assert.True(backend.Values.ContainsKey("source") || backend.Journal?.State >= AppSurfaceLocalSecretMigrationState.DestinationVerified);
        Assert.True(!backend.Values.TryGetValue("destination", out var destination) || destination == Backend.Marker);
        var id = backend.Journal?.MigrationId;
        backend.FaultPosition = null;
        backend.Events.Clear();

        var retry = Run(backend);

        Assert.Equal(LocalSecretResultStatus.Found, retry.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Complete, retry.State);
        if (id is not null) Assert.Equal(id, retry.MigrationId);
        Assert.False(backend.Values.ContainsKey("source"));
        Assert.Equal(Backend.Marker, backend.Values["destination"]);
        Assert.DoesNotContain(Backend.Marker, JsonSerializer.Serialize(backend.Journal));
        Assert.Equal(new[] { "destination" }, backend.Index);
        Assert.False(backend.Held);
    }

    [Theory]
    [InlineData(AppSurfaceLocalSecretMigrationState.DestinationVerified, true, false)]
    [InlineData(AppSurfaceLocalSecretMigrationState.DestinationVerified, false, true)]
    [InlineData(AppSurfaceLocalSecretMigrationState.DestinationVerified, true, true)]
    [InlineData(AppSurfaceLocalSecretMigrationState.SourceDeletePending, true, false)]
    [InlineData(AppSurfaceLocalSecretMigrationState.SourceDeletePending, false, true)]
    [InlineData(AppSurfaceLocalSecretMigrationState.SourceDeletePending, true, true)]
    public void ReacquiredLease_ShouldRetainSourceChangedByInterveningWriter(AppSurfaceLocalSecretMigrationState state, bool source, bool destination)
    {
        var backend = new Backend { FaultPosition = Array.IndexOf(Operations, state.ToString()), FaultAfter = true };
        Assert.Equal(LocalSecretResultStatus.Unavailable, Run(backend).Status);
        Assert.False(backend.Held);
        if (source) backend.Values["source"] = "new-source-marker";
        if (destination) backend.Values["destination"] = "new-destination-marker";
        backend.FaultPosition = null;
        backend.Events.Clear();
        var result = Run(backend);
        Assert.Equal("local-secret-migration-source-destination-changed", result.Diagnostic?.Code);
        Assert.Contains("source", backend.Values.Keys);
        Assert.DoesNotContain("source-delete", backend.Events);
    }

    [Fact]
    public void MissingCapability_ShouldStopBeforeLeaseJournalOrValueRead()
    {
        var backend = new Backend { Supported = false };
        var result = Run(backend);
        Assert.Equal("local-secret-migration-unsupported", result.Diagnostic?.Code);
        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Null(backend.Journal);
        Assert.Empty(backend.Events);
        Assert.Equal(0, backend.Leases);
    }

    [Theory]
    [InlineData("destination", 1, null)]
    [InlineData("destination", 1, "changed")]
    [InlineData("source", 2, null)]
    public void Verification_ShouldRetainRecordsWhenRereadCannotProveCopy(string key, int read, string? value)
    {
        var backend = new Backend
        {
            Journal = Journal(AppSurfaceLocalSecretMigrationState.DestinationWritten),
            ChangeKey = key,
            ChangeRead = read,
            ChangedValue = value
        };
        backend.Values["destination"] = Backend.Marker;
        var result = Run(backend);
        Assert.Equal("local-secret-migration-verification-failed", result.Diagnostic?.Code);
        Assert.DoesNotContain("source-delete", backend.Events);
    }

    [Fact]
    public void CompletedJournal_ShouldBeIdempotentWithoutReadingValues()
    {
        var backend = new Backend { Journal = Journal(AppSurfaceLocalSecretMigrationState.Complete) };
        Assert.Equal(LocalSecretResultStatus.Found, Run(backend).Status);
        Assert.Equal(new[] { "journal-read" }, backend.Events);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    public void InvalidJournalState_ShouldFailClosed(int state)
    {
        var backend = new Backend { Journal = Journal((AppSurfaceLocalSecretMigrationState)state) };
        Assert.Equal("local-secret-migration-unrecoverable", Run(backend).Diagnostic?.Code);
        Assert.Equal(new[] { "journal-read" }, backend.Events);
    }

    [Fact]
    public void CancellationWhileAcquiringLease_ShouldPropagateWithoutPrepared()
    {
        var backend = new Backend { CancelAcquire = true };
        Assert.Throws<OperationCanceledException>(() => Run(backend));
        Assert.Null(backend.Journal);
    }

    private static AppSurfaceLocalSecretKeyMigrationResult Run(Backend backend) => AppSurfaceLocalSecretMigrationCoordinator.Run(
        backend, "App", "Development", null, "source", "destination", AppSurfaceConfigKey.Parse("Payments:ApiKey"), "matrix");
    private static AppSurfaceLocalSecretMigrationJournal Journal(AppSurfaceLocalSecretMigrationState state) =>
        new("stable-operation", "App", "Development", null, "source", "destination", state);

    private sealed class Backend : IAppSurfaceLocalSecretMigrationBackend
    {
        internal const string Marker = "copy-marker-not-a-secret";
        internal Dictionary<string, string> Values { get; } = new() { ["source"] = Marker };
        internal List<string> Events { get; } = [];
        internal string[] Index { get; private set; } = ["source"];
        internal AppSurfaceLocalSecretMigrationJournal? Journal { get; set; }
        internal int? FaultPosition { get; set; }
        internal bool FaultAfter { get; init; }
        internal bool Supported { get; init; } = true;
        internal bool CancelAcquire { get; init; }
        internal bool Held { get; private set; }
        internal int Leases { get; private set; }
        internal string? ChangeKey { get; init; }
        internal int ChangeRead { get; init; }
        internal string? ChangedValue { get; init; }
        private int _changedReads;
        public bool SupportsDurableMigration => Supported;
        public IDisposable AcquireMaintenanceLease(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (CancelAcquire) throw new OperationCanceledException();
            Assert.False(Held);
            Held = true;
            Leases++;
            return new Release(() => Held = false);
        }
        public AppSurfaceLocalSecretMigrationJournal? ReadJournal() => At("journal-read", () => Journal);
        public void CommitJournal(AppSurfaceLocalSecretMigrationJournal journal) => At(journal.State.ToString(), () => Journal = journal);
        public string? ReadExact(string key) => At(key + "-read", () =>
        {
            if (key == ChangeKey && ++_changedReads == ChangeRead)
            {
                if (ChangedValue is null) Values.Remove(key); else Values[key] = ChangedValue;
            }
            return Values.GetValueOrDefault(key);
        });
        public void WriteExact(string key, string value) => At(key + "-write", () => Values[key] = value);
        public void DeleteExact(string key) => At(key + "-delete", () => Values.Remove(key));
        public void PublishIndex() => At("index", () => Index = Values.Keys.Order(StringComparer.Ordinal).ToArray());
        private T At<T>(string name, Func<T> operation)
        {
            Assert.True(Held);
            var fault = Events.Count == FaultPosition;
            Events.Add(name);
            if (fault && !FaultAfter) throw new IOException("injected before effect");
            var result = operation();
            if (fault) throw new IOException("injected after effect");
            return result;
        }
        private sealed class Release(Action action) : IDisposable { public void Dispose() => action(); }
    }
}
