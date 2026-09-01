using AuthAspireKeycloakLocalSeedStore;

namespace AuthAspireKeycloakLocalSeedStore.Tests;

public sealed class LocalSeedStoreTests
{
    [Fact]
    public void ReadSnapshot_WhenStateIsMissing_ReturnsEmptySnapshot()
    {
        using var directory = new TempDirectory();
        var path = System.IO.Path.Combine(directory.Path, "seed-store.json");
        var store = new LocalSeedStore(path);

        var snapshot = store.ReadSnapshot();

        Assert.Empty(snapshot.BrokerAliases);
        Assert.Empty(snapshot.IdentitySubjectMaps);
        Assert.Empty(snapshot.CandidateFixtures);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Upserts_AreIdempotentAndReplaceByNaturalKey()
    {
        using var directory = new TempDirectory();
        var store = CreateStore(directory);

        store.UpsertBrokerAlias("local", "https://issuer-one", "client-one");
        store.UpsertBrokerAlias("local", "https://issuer-two", "client-two");
        store.UpsertIdentitySubjectMap("founder", "subject-one");
        store.UpsertIdentitySubjectMap("founder", "subject-two");
        store.UpsertCandidateFixture("founder", "subject-one");
        store.UpsertCandidateFixture("founder", "subject-two");

        var snapshot = store.ReadSnapshot();

        var broker = Assert.Single(snapshot.BrokerAliases);
        Assert.Equal(new BrokerAliasRecord("local", "https://issuer-two", "client-two"), broker);
        var mapping = Assert.Single(snapshot.IdentitySubjectMaps);
        Assert.Equal(new IdentitySubjectMapRecord("founder", "subject-two"), mapping);
        var fixture = Assert.Single(snapshot.CandidateFixtures);
        Assert.Equal(new CandidateFixtureRecord("founder", "subject-two"), fixture);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"brokerAliases":[],"identitySubjectMaps":[]}""")]
    [InlineData("""{"brokerAliases":[],"identitySubjectMaps":[],"candidateFixtures":[],"extra":true}""")]
    [InlineData("""{"brokerAliases":{},"identitySubjectMaps":[],"candidateFixtures":[]}""")]
    [InlineData("""{"brokerAliases":["not-an-object"],"identitySubjectMaps":[],"candidateFixtures":[]}""")]
    [InlineData("""{"brokerAliases":[],"identitySubjectMaps":{},"candidateFixtures":[]}""")]
    [InlineData("""{"brokerAliases":[],"identitySubjectMaps":[],"candidateFixtures":{}}""")]
    [InlineData("""{"brokerAliases":[{"alias":"local","issuer":"https://issuer","clientId":"client","extra":true}],"identitySubjectMaps":[],"candidateFixtures":[]}""")]
    [InlineData("""{"brokerAliases":[{"alias":"local","issuer":"https://issuer","clientId":"client"},{"alias":"local","issuer":"https://other","clientId":"other"}],"identitySubjectMaps":[],"candidateFixtures":[]}""")]
    [InlineData("""{"brokerAliases":[],"identitySubjectMaps":[{"naturalKey":"founder","subject":"one"},{"naturalKey":"founder","subject":"two"}],"candidateFixtures":[]}""")]
    [InlineData("""{"brokerAliases":[],"identitySubjectMaps":[],"candidateFixtures":[{"naturalKey":"founder","subject":"one"},{"naturalKey":"founder","subject":"two"}]}""")]
    [InlineData("""{"brokerAliases":[{"alias":"","issuer":"https://issuer","clientId":"client"}],"identitySubjectMaps":[],"candidateFixtures":[]}""")]
    [InlineData("""{"brokerAliases":[],"identitySubjectMaps":[],"candidateFixtures":[{"naturalKey":"founder"}]}""")]
    public void ReadSnapshot_WhenStateIsMalformedOrDuplicate_ThrowsInvalidDataException(string json)
    {
        using var directory = new TempDirectory();
        var path = System.IO.Path.Combine(directory.Path, "seed-store.json");
        File.WriteAllText(path, json);
        var store = new LocalSeedStore(path);

        Assert.Throws<InvalidDataException>(() => store.ReadSnapshot());
    }

    [Fact]
    public void FailedUpdate_LeavesPreviousReadableSnapshotAndBytesUntouched()
    {
        using var directory = new TempDirectory();
        var path = System.IO.Path.Combine(directory.Path, "seed-store.json");
        var store = new LocalSeedStore(path);
        store.UpsertBrokerAlias("local", "https://issuer", "client");
        var before = File.ReadAllBytes(path);

        File.WriteAllText(
            path,
            """{"brokerAliases":[{"alias":"local","issuer":"https://issuer","clientId":"client"},{"alias":"local","issuer":"https://other","clientId":"other"}],"identitySubjectMaps":[],"candidateFixtures":[]}""");
        var malformedState = File.ReadAllBytes(path);

        var exception = Assert.Throws<InvalidDataException>(
            () => store.UpsertBrokerAlias("local", "https://replacement", "replacement"));

        Assert.Contains("duplicate or invalid broker aliases", exception.Message, StringComparison.Ordinal);
        Assert.Equal(malformedState, File.ReadAllBytes(path));
        Assert.Throws<InvalidDataException>(() => store.ReadSnapshot());

        File.WriteAllBytes(path, before);
        var readable = store.ReadSnapshot();
        Assert.Equal("https://issuer", Assert.Single(readable.BrokerAliases).Issuer);
    }

    [Fact]
    public async Task ConcurrentReadsDuringUpdates_AlwaysObserveReadableSnapshots()
    {
        using var directory = new TempDirectory();
        var store = CreateStore(directory);
        store.UpsertBrokerAlias("local", "https://issuer-0", "client-0");

        var writer = Task.Run(() =>
        {
            for (var index = 1; index <= 100; index++)
            {
                store.UpsertBrokerAlias("local", $"https://issuer-{index}", $"client-{index}");
            }
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < 100; index++)
            {
                var snapshot = store.ReadSnapshot();
                Assert.Single(snapshot.BrokerAliases);
                Assert.Empty(snapshot.IdentitySubjectMaps);
                Assert.Empty(snapshot.CandidateFixtures);
            }
        }));

        await Task.WhenAll(readers.Append(writer));

        Assert.Equal("https://issuer-100", Assert.Single(store.ReadSnapshot().BrokerAliases).Issuer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_RejectsMissingPath(string? path)
    {
        Assert.Throws<ArgumentException>(() => new LocalSeedStore(path!));
    }

    [Fact]
    public void Upserts_RejectBlankArguments()
    {
        using var directory = new TempDirectory();
        var store = CreateStore(directory);

        Assert.Throws<ArgumentException>(() => store.UpsertBrokerAlias("", "issuer", "client"));
        Assert.Throws<ArgumentException>(() => store.UpsertBrokerAlias("alias", "", "client"));
        Assert.Throws<ArgumentException>(() => store.UpsertBrokerAlias("alias", "issuer", ""));
        Assert.Throws<ArgumentException>(() => store.UpsertIdentitySubjectMap("", "subject"));
        Assert.Throws<ArgumentException>(() => store.UpsertIdentitySubjectMap("key", ""));
        Assert.Throws<ArgumentException>(() => store.UpsertCandidateFixture("", "subject"));
        Assert.Throws<ArgumentException>(() => store.UpsertCandidateFixture("key", ""));
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "seed-store.json")));
    }

    [Fact]
    public void SnapshotValidate_RejectsNullCollectionsAndInvalidRecordValues()
    {
        Assert.Throws<InvalidDataException>(
            () => new LocalSeedStoreSnapshot(null!, [], []).Validate());
        Assert.Throws<InvalidDataException>(
            () => new LocalSeedStoreSnapshot([new BrokerAliasRecord(" ", "issuer", "client")], [], []).Validate());
        Assert.Throws<InvalidDataException>(
            () => new LocalSeedStoreSnapshot([], [new IdentitySubjectMapRecord("key", "")], []).Validate());
        Assert.Throws<InvalidDataException>(
            () => new LocalSeedStoreSnapshot([], [], [new CandidateFixtureRecord("", "subject")]).Validate());
    }

    private static LocalSeedStore CreateStore(TempDirectory directory) =>
        new(System.IO.Path.Combine(directory.Path, "seed-store.json"));
}
