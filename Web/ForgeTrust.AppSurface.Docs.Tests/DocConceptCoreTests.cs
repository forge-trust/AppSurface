using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DocConceptCoreTests
{
    private const int Budget = 1024 * 1024;
    private const string SidecarSchema = "appsurface-docs-concepts-v1";
    private const string BridgeSchema = "appsurface-docs-concept-map-v1";

    [Fact]
    public void SidecarRequiresExactCanonicalCoverageAndValidIdentityGrammar()
    {
        var routes = new[] { Route("guide/one", "en"), Route("guide/two", "en") };
        var complete = Sidecar(
            new Page("guide/one", "en", "guide.one", "One", ["one"]),
            new Page("guide/two", "en", "guide.two", "Two", ["two"]));

        Assert.True(DocConceptIdentityIndex.TryLoadSidecar(complete, routes, Budget, out var parsed, out var issue));
        Assert.Null(issue);
        Assert.Equal(DocConceptIdentityState.ValidComplete, parsed!.IdentityState);
        Assert.Equal(2, parsed.Pages.Count);

        var missingWinner = Sidecar(new Page("guide/one", "en", "guide.one", "One", ["one"]));
        Assert.False(DocConceptIdentityIndex.TryLoadSidecar(missingWinner, routes, Budget, out _, out issue));
        Assert.Equal(DocConceptValidationFailure.IndexInvalid, issue!.Failure);

        var invalidId = Sidecar(new Page("guide/one", "en", "Upper.Case", "One", ["one"]),
            new Page("guide/two", "en", "guide.two", "Two", ["two"]));
        Assert.False(DocConceptIdentityIndex.TryLoadSidecar(invalidId, routes, Budget, out _, out issue));
        Assert.Equal(DocConceptValidationFailure.InvalidConceptId, issue!.Failure);

        Assert.True(DocConceptSchema.TryNormalizeConceptId("  guide.one-2_3  ", out var normalized));
        Assert.Equal("guide.one-2_3", normalized);
        Assert.False(DocConceptSchema.TryNormalizeConceptId(".guide", out _));
        Assert.False(DocConceptSchema.TryNormalizeConceptId("guide.", out _));
        Assert.False(DocConceptSchema.TryNormalizeConceptId(new string('a', 129), out _));
        Assert.False(DocConceptSchema.TryNormalizeConceptId("guïde", out _));
    }

    [Fact]
    public void PartialSidecarPreservesUnspecifiedIdentityAndRejectsDuplicateJsonFields()
    {
        var routes = new[] { Route("guide/one", "en"), Route("guide/two", "en") };
        var partial = Sidecar(
            new Page("guide/one", "en", "one", "One", ["one"]),
            new Page("guide/two", "en", null, "Two", ["two"]));
        Assert.True(DocConceptIdentityIndex.TryLoadSidecar(partial, routes, Budget, out var parsed, out var issue));
        Assert.Null(issue);
        Assert.Equal(DocConceptIdentityState.ValidPartial, parsed!.IdentityState);
        Assert.Null(parsed.Pages.Single(page => page.RoutePath == "guide/two").ConceptId);

        var malformed = new[]
        {
            Encoding.UTF8.GetBytes("{"),
            Encoding.UTF8.GetBytes("{\"schema\":\"appsurface-docs-concepts-v1\",\"schema\":\"appsurface-docs-concepts-v1\",\"identityCoverage\":\"complete\",\"pages\":[]}"),
            Encoding.UTF8.GetBytes($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[]}}")
        };
        foreach (var bytes in malformed)
        {
            Assert.False(DocConceptIdentityIndex.TryLoadSidecar(bytes, routes, Budget, out _, out issue));
            Assert.NotNull(issue);
            Assert.Equal(DocConceptValidationFailure.IndexInvalid, issue.Failure);
        }

        var duplicateConcepts = Sidecar(
            new Page("guide/one", "en", "same", "One", []),
            new Page("guide/two", "en", "same", "Two", []));
        Assert.False(DocConceptIdentityIndex.TryLoadSidecar(duplicateConcepts, routes, Budget, out _, out issue));
        Assert.Equal(DocConceptValidationFailure.IdentityConflict, issue!.Failure);
    }

    [Fact]
    public void CompleteIndexesDistinguishMovesAndKnownAbsenceFromReusedRoutes()
    {
        var oldPin = Pin('a');
        var movedPin = Pin('b');
        var reusedPin = Pin('c');
        var source = Release("1.0", oldPin,
            [new Page("guide/install", "en", "install", "Install", ["install"])]);
        var moved = Release("2.0", movedPin,
            [new Page("guides/setup", "en", "install", "Set up", ["setup"])]);
        var reused = Release("3.0", reusedPin,
            [new Page("guide/install", "en", "replacement", "Different guide", ["different"])]);
        var index = Build([source, moved, reused]).Index;
        var sourceIdentity = new DocConceptPageIdentity("1.0", oldPin, "guide/install", "en", true);

        var movedResult = index.Resolve(sourceIdentity, "2.0", movedPin);
        Assert.Equal(DocConceptResolutionReason.MovedPage, movedResult.Reason);
        Assert.Equal("guides/setup", movedResult.TargetPage!.RoutePath);
        Assert.Equal("guides/setup.html", movedResult.TargetPage.ArtifactPath);

        var reusedResult = index.Resolve(sourceIdentity, "3.0", reusedPin);
        Assert.Equal(DocConceptResolutionReason.KnownAbsent, reusedResult.Reason);
        Assert.Null(reusedResult.TargetPage);
    }

    [Fact]
    public void PartialIndexesAllowOnlyUncontradictedCanonicalRouteContinuity()
    {
        var sourcePin = Pin('d');
        var samePin = Pin('e');
        var conflictPin = Pin('f');
        var source = Release("1", sourcePin,
            [new Page("guide/topic", "en", "topic", "Topic", ["topic"]), new Page("other", "en", null, "Other", [])],
            complete: false);
        var same = Release("2", samePin,
            [new Page("guide/topic", "en", null, "Topic", ["topic"]), new Page("other", "en", null, "Other", [])],
            complete: false);
        var conflict = Release("3", conflictPin,
            [new Page("guide/topic", "en", "different", "Reused", ["reused"]), new Page("other", "en", null, "Other", [])],
            complete: false);
        var index = Build([source, same, conflict]).Index;
        var sourceIdentity = new DocConceptPageIdentity("1", sourcePin, "guide/topic", "en", true);

        var continuity = index.Resolve(sourceIdentity, "2", samePin);
        Assert.Equal(DocConceptResolutionReason.SamePage, continuity.Reason);
        Assert.Equal("guide/topic", continuity.TargetPage!.RoutePath);

        var contradiction = index.Resolve(sourceIdentity, "3", conflictPin);
        Assert.Equal(DocConceptResolutionReason.Unknown, contradiction.Reason);
        Assert.Null(contradiction.TargetPage);
    }

    [Fact]
    public void PinnedLegacyBridgeEstablishesLocaleAndConflictsQuarantineOrderIndependently()
    {
        var sourcePin = Pin('1');
        var targetPin = Pin('2');
        var source = Legacy("1", sourcePin, "guide/old");
        var target = Legacy("2", targetPin, "guide/new");
        Assert.Null(source.ConceptIndexBytes);
        Assert.Null(target.ConceptIndexBytes);
        var linkedMap = Bridge(
            new Membership("1", sourcePin, "en", "page", "guide/old"),
            new Membership("2", targetPin, "en", "page", "guide/new"));
        var good = DocConceptIdentityIndex.BuildCombined([source, target], [linkedMap], DocConceptValidationMode.Quarantine, Budget).Index;
        var sourceIdentity = good.EnumeratePublicPages().Single(row => row.Identity.Version == "1").Identity;
        var result = good.Resolve(sourceIdentity, "2", targetPin);
        Assert.Equal("en", sourceIdentity.Locale);
        Assert.Equal(DocConceptResolutionReason.MovedPage, result.Reason);

        var absentMap = Bridge(
            new Membership("1", sourcePin, "en", "page", "guide/old"),
            new Membership("2", targetPin, "en", "absent"));
        var forward = DocConceptIdentityIndex.BuildCombined([source, target], [linkedMap, absentMap], DocConceptValidationMode.Quarantine, Budget).Index;
        var reverse = DocConceptIdentityIndex.BuildCombined([source, target], [absentMap, linkedMap], DocConceptValidationMode.Quarantine, Budget).Index;
        var forwardIdentity = forward.EnumeratePublicPages().Single(row => row.Identity.Version == "1").Identity;
        var reverseIdentity = reverse.EnumeratePublicPages().Single(row => row.Identity.Version == "1").Identity;

        Assert.Equal(DocConceptResolutionReason.Ambiguous, forward.Resolve(forwardIdentity, "2", targetPin).Reason);
        Assert.Equal(DocConceptResolutionReason.Ambiguous, reverse.Resolve(reverseIdentity, "2", targetPin).Reason);
    }

    [Fact]
    public void InvalidBridgeBlocksLegacyRouteCompatibilityAndIncomingIsDestinationBound()
    {
        var sourcePin = Pin('3');
        var targetPin = Pin('4');
        var source = Release("1", sourcePin,
            [new Page("guide/topic", "en", null, "Topic", ["topic"])], complete: false);
        var target = Release("2", targetPin,
            [new Page("guide/topic", "en", null, "Topic", ["topic"])], complete: false);
        var clean = Build([source, target]).Index;
        var sourceIdentity = new DocConceptPageIdentity("1", sourcePin, "guide/topic", "en", true);
        Assert.Equal(DocConceptResolutionReason.SamePage, clean.Resolve(sourceIdentity, "2", targetPin).Reason);

        var badMap = new DocConceptMapReference("concept-map.json", new string('0', 64), Encoding.UTF8.GetBytes("{}"));
        var poisoned = DocConceptIdentityIndex.BuildCombined([source, target], [badMap], DocConceptValidationMode.Quarantine, Budget).Index;
        Assert.Equal(DocConceptResolutionReason.Unknown, poisoned.Resolve(sourceIdentity, "2", targetPin).Reason);

        var movedTarget = Release("2", targetPin,
            [new Page("guides/topic", "en", "topic", "Topic", ["topic"]), new Page("other", "en", "other", "Other", ["other"])]);
        var movedSource = Release("1", sourcePin,
            [new Page("guide/topic", "en", "topic", "Topic", ["topic"])]);
        var moved = Build([movedSource, movedTarget]).Index;
        var sourcePage = moved.EnumeratePublicPages().Single(row => row.Identity.Version == "1").Identity;
        var destination = moved.EnumeratePublicPages().Single(row => row.Identity.Version == "2" && row.Identity.RoutePath == "guides/topic").Identity;
        var incoming = moved.GetIncoming(destination, moved.EnumeratePublicPages().Select(row => row.Identity), "instance-a").ToArray();
        Assert.Single(incoming);
        Assert.Equal(sourcePage, incoming[0].SourceIdentity);
        Assert.Equal(DocConceptResolutionReason.MovedPage, incoming[0].Reason);
        Assert.Equal(DocConceptIdentityIndex.CreateSourceKey("instance-a", sourcePage), incoming[0].SourceKey);
        var unrelated = moved.EnumeratePublicPages().Single(row => row.Identity.Version == "2" && row.Identity.RoutePath == "other").Identity;
        Assert.Empty(moved.GetIncoming(unrelated, moved.EnumeratePublicPages().Select(row => row.Identity), "instance-a"));
    }

    [Fact]
    public void RecoveryUsesAuthoredFallbackAndDeepestVerifiedNonRootAncestor()
    {
        var sourcePin = Pin('5');
        var targetPin = Pin('6');
        var source = Release("1", sourcePin,
            [new Page("section/sub/topic", "en", "topic", "Topic", ["topic"])]);
        var target = Release("2", targetPin,
            [new Page("section", "en", null, "Section", [], Landing: true),
             new Page("section/sub", "en", null, "Subsection", [], Landing: true),
             new Page("section/sub/deeper", "en", null, "Ordinary page", [])], complete: false);
        var index = Build([source, target]).Index;
        var identity = new DocConceptPageIdentity("1", sourcePin, "section/sub/topic", "en", true);
        var resolution = index.Resolve(identity, "2", targetPin);
        Assert.Equal(DocConceptResolutionReason.Unknown, resolution.Reason);
        Assert.Equal("section/sub", resolution.FallbackSection!.RoutePath);

        var absentBridge = Bridge(
            new Membership("1", sourcePin, "en", "page", "section/sub/topic"),
            new Membership("2", targetPin, "en", "absent", FallbackSectionRoute: "section"));
        var authored = DocConceptIdentityIndex.BuildCombined([source, target], [absentBridge], DocConceptValidationMode.Quarantine, Budget).Index;
        var authoredSource = authored.EnumeratePublicPages().Single(row => row.Identity.Version == "1").Identity;
        var authoredResult = authored.Resolve(authoredSource, "2", targetPin);
        Assert.Equal(DocConceptResolutionReason.KnownAbsent, authoredResult.Reason);
        Assert.Equal("section", authoredResult.FallbackSection!.RoutePath);
    }

    [Fact]
    public void SourceKeysAndByteBudgetUseUtf8AndRejectInvalidLimits()
    {
        var identity = new DocConceptPageIdentity("1", Pin('7'), "topic", "en", true);
        var same = identity with { ReleaseManifestSha256 = identity.ReleaseManifestSha256.ToUpperInvariant() };
        var anotherInstance = DocConceptIdentityIndex.CreateSourceKey("instance-b", identity);

        Assert.Equal(DocConceptIdentityIndex.CreateSourceKey("instance-a", identity), DocConceptIdentityIndex.CreateSourceKey("instance-a", same));
        Assert.NotEqual(anotherInstance, DocConceptIdentityIndex.CreateSourceKey("instance-a", identity));
        var sourceKey = DocConceptIdentityIndex.CreateSourceKey("instance-a", identity);
        Assert.Equal(64, sourceKey.Length);
        Assert.All(sourceKey, character => Assert.True(char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'));
        Assert.True(DocConceptArtifactBudget.TryMeasure("é", 2, out var bytes));
        Assert.Equal(2, bytes);
        Assert.False(DocConceptArtifactBudget.TryMeasure("é", 1, out _));
        Assert.False(DocConceptArtifactBudget.TryMeasure("x", 0, out _));
        Assert.True(DocConceptArtifactBudget.TryMeasure(new byte[] { 0xC3, 0xA9 }, 2, out bytes));
        Assert.False(DocConceptArtifactBudget.TryMeasure(new byte[] { 0xC3, 0xA9 }, 1, out _));
        Assert.Equal(4 * 1024 * 1024, DocConceptArtifactBudget.DefaultMaximumBytes);
        Assert.False(DocConceptArtifactBudget.IsValidLimit(32 * 1024 * 1024 + 1));
    }

    [Fact]
    public void StrictPublicationRejectsConflictingMapsWhileStartupQuarantinesTheWholeConcept()
    {
        var sourcePin = Pin('8');
        var targetPin = Pin('9');
        var source = Legacy("1", sourcePin, "old/topic");
        var target = LegacyWithRoutes("2", targetPin, "new/topic", "other/topic");
        var sourceMember = new Membership("1", sourcePin, "en", "page", "old/topic");
        var firstTargetMember = new Membership("2", targetPin, "en", "page", "new/topic");
        var conflictingTargetMember = new Membership("2", targetPin, "en", "page", "other/topic");
        var firstMap = Bridge(sourceMember, firstTargetMember);
        var conflictingMap = Bridge(sourceMember, conflictingTargetMember);

        var strict = DocConceptIdentityIndex.BuildCombined(
            [source, target], [firstMap, conflictingMap], DocConceptValidationMode.Strict, Budget);
        var startup = DocConceptIdentityIndex.BuildCombined(
            [source, target], [conflictingMap, firstMap], DocConceptValidationMode.Quarantine, Budget);

        Assert.False(strict.IsValid);
        Assert.Contains(strict.Issues, issue => issue.Failure == DocConceptValidationFailure.IdentityConflict);
        Assert.True(startup.Index.TryGetPageIdentity("1", sourcePin, "old/topic", out var sourceIdentity));
        Assert.Equal(DocConceptResolutionReason.Ambiguous, startup.Index.Resolve(sourceIdentity!, "2", targetPin).Reason);
        Assert.Equal(DocConceptResolutionReason.Ambiguous,
            DocConceptIdentityIndex.BuildCombined([source, target], [firstMap, conflictingMap], DocConceptValidationMode.Quarantine, Budget)
                .Index.Resolve(sourceIdentity!, "2", targetPin).Reason);
    }

    [Fact]
    public void DuplicateBridgeReferencesAndIdenticalFactsDeduplicateWithoutChangingOutcome()
    {
        var sourcePin = Pin('a');
        var targetPin = Pin('b');
        var source = Legacy("1", sourcePin, "old/topic");
        var target = Legacy("2", targetPin, "new/topic");
        var map = Bridge(
            new Membership("1", sourcePin, "en", "page", "old/topic"),
            new Membership("2", targetPin, "en", "page", "new/topic"));

        var result = DocConceptIdentityIndex.BuildCombined(
            [source, target], [map, map, Bridge(
                new Membership("1", sourcePin, "en", "page", "old/topic"),
                new Membership("2", targetPin, "en", "page", "new/topic"))],
            DocConceptValidationMode.Strict, Budget);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
        Assert.True(result.Index.TryGetPageIdentity("1", sourcePin, "old/topic", out var sourceIdentity));
        Assert.Equal(DocConceptResolutionReason.MovedPage, result.Index.Resolve(sourceIdentity!, "2", targetPin).Reason);
    }

    [Fact]
    public void ASharedLegacyPageQuarantinesEveryConnectedConceptRegardlessOfMapOrder()
    {
        var firstPin = Pin('b');
        var legacyPin = Pin('c');
        var secondPin = Pin('d');
        var first = Release("1", firstPin,
            [new Page("guide/first", "en", "first", "First", ["first"])]);
        var legacy = Legacy("2", legacyPin, "guide/shared");
        var second = Release("3", secondPin,
            [new Page("guide/second", "en", "second", "Second", ["second"])]);
        var firstMapMemberships = new[]
        {
            new Membership("1", firstPin, "en", "page", "guide/first") { ConceptId = "first" },
            new Membership("2", legacyPin, "en", "page", "guide/shared") { ConceptId = "first" }
        };
        var secondMapMemberships = new[]
        {
            new Membership("3", secondPin, "en", "page", "guide/second") { ConceptId = "second" },
            new Membership("2", legacyPin, "en", "page", "guide/shared") { ConceptId = "second" }
        };
        var combined = firstMapMemberships.Concat(secondMapMemberships).ToArray();
        var forward = Build([first, legacy, second], [Bridge(combined)]).Index;
        var reverse = Build([first, legacy, second], [Bridge(combined.Reverse().ToArray())]).Index;

        Assert.True(forward.TryGetPageIdentity("1", firstPin, "guide/first", out var firstIdentity));
        Assert.True(forward.TryGetPageIdentity("3", secondPin, "guide/second", out var secondIdentity));
        Assert.True(reverse.TryGetPageIdentity("1", firstPin, "guide/first", out var reverseFirstIdentity));
        Assert.True(reverse.TryGetPageIdentity("3", secondPin, "guide/second", out var reverseSecondIdentity));
        Assert.Equal(DocConceptResolutionReason.Ambiguous, forward.Resolve(firstIdentity!, "2", legacyPin).Reason);
        Assert.Equal(DocConceptResolutionReason.Ambiguous, forward.Resolve(secondIdentity!, "2", legacyPin).Reason);
        Assert.Equal(DocConceptResolutionReason.Ambiguous, reverse.Resolve(reverseFirstIdentity!, "2", legacyPin).Reason);
        Assert.Equal(DocConceptResolutionReason.Ambiguous, reverse.Resolve(reverseSecondIdentity!, "2", legacyPin).Reason);
    }

    [Fact]
    public void RouteCompatibilityAndRecoveryPreserveLogicalCaseAndCanonicalWinnerSpelling()
    {
        var sourcePin = Pin('c');
        var targetPin = Pin('d');
        var source = Release("1", sourcePin,
            [new Page("Guide/Topic", "en", null, "Topic", ["start"])], complete: false);
        var target = Release("2", targetPin,
            [new Page("guide/topic", "en", null, "Topic", ["start"])], complete: false);
        var index = Build([source, target]).Index;

        Assert.True(index.TryGetPageIdentity("2", targetPin, "GUIDE/TOPIC", out var targetIdentity));
        Assert.Equal("guide/topic", targetIdentity!.RoutePath);
        Assert.True(index.TryGetPageIdentity("1", sourcePin, "guide/topic", out var sourceIdentity));
        Assert.Equal("Guide/Topic", sourceIdentity!.RoutePath);
        var resolution = index.Resolve(sourceIdentity, "2", targetPin);
        Assert.Equal(DocConceptResolutionReason.SamePage, resolution.Reason);
        Assert.Equal("guide/topic", resolution.TargetPage!.RoutePath);
    }

    [Fact]
    public void LocaleAbsenceAndUnknownLegacyLocaleNeverBorrowAnotherLanguage()
    {
        var sourcePin = Pin('e');
        var targetPin = Pin('f');
        var source = Release("1", sourcePin,
            [new Page("guide/topic", "en", "topic", "Topic", ["topic"])]);
        var frenchOnly = Release("2", targetPin,
            [new Page("guide/topic", "fr", "topic", "Sujet", ["sujet"])]);
        var sourceIdentity = new DocConceptPageIdentity("1", sourcePin, "guide/topic", "en", true);
        var localeOutcome = Build([source, frenchOnly]).Index.Resolve(sourceIdentity, "2", targetPin);
        Assert.Equal(DocConceptResolutionReason.NoSelectedLocaleDocs, localeOutcome.Reason);
        Assert.Null(localeOutcome.FallbackSection);
        Assert.False(localeOutcome.SearchAvailable);
        Assert.Null(localeOutcome.HomeRoutePath);

        var unknownSource = Legacy("3", Pin('1'), "guide/topic");
        var knownTarget = Release("4", Pin('2'), [new Page("guide/topic", "en", "topic", "Topic", ["topic"])]);
        var unknownIndex = Build([unknownSource, knownTarget]).Index;
        Assert.True(unknownIndex.TryGetPageIdentity("3", Pin('1'), "guide/topic", out var unknownIdentity));
        var unknownOutcome = unknownIndex.Resolve(unknownIdentity!, "4", Pin('2'));
        Assert.Equal(DocConceptResolutionReason.Unknown, unknownOutcome.Reason);
        Assert.Null(unknownOutcome.FallbackSection);
        Assert.False(unknownOutcome.SearchAvailable);
        Assert.Null(unknownOutcome.HomeRoutePath);
    }

    [Fact]
    public void SidecarRejectsMalformedJsonShapesUtf8LocalesAndAnchorsAsWholeArtifacts()
    {
        var routes = new[] { Route("guide/topic", "en") };
        var cases = new (byte[] Bytes, DocConceptValidationFailure Failure)[]
        {
            (Encoding.UTF8.GetBytes("{\"schema\":\"appsurface-docs-concepts-v1\",\"identityCoverage\":\"complete\",\"pages\":[{\"canonicalRoutePath\":\"guide/topic\",\"locale\":\"en\",\"conceptId\":\"topic\",\"title\":\"Topic\",\"anchorIds\":[\"same\",\"same\"]}]}"), DocConceptValidationFailure.IndexInvalid),
            (Encoding.UTF8.GetBytes("{\"schema\":\"appsurface-docs-concepts-v1\",\"identityCoverage\":\"complete\",\"pages\":[{\"canonicalRoutePath\":\"guide/topic\",\"locale\":\"en \",\"conceptId\":\"topic\",\"title\":\"Topic\",\"anchorIds\":[]}]}"), DocConceptValidationFailure.CanonicalRouteInvalid),
            (new byte[] { 0xFF, 0xFE }, DocConceptValidationFailure.IndexInvalid),
            (Encoding.UTF8.GetBytes("[]"), DocConceptValidationFailure.IndexInvalid)
        };

        foreach (var (bytes, expectedFailure) in cases)
        {
            Assert.False(DocConceptIdentityIndex.TryLoadSidecar(bytes, routes, Budget, out var sidecar, out var issue));
            Assert.Null(sidecar);
            Assert.Equal(expectedFailure, issue!.Failure);
        }

        Assert.False(DocConceptIdentityIndex.TryLoadSidecar(Sidecar(new Page("guide/topic", "en", "topic", "Topic", [])), routes, 1, out _, out var tooLarge));
        Assert.Equal(DocConceptValidationFailure.ArtifactTooLarge, tooLarge!.Failure);

        var duplicateMembership = Bridge(
            new Membership("1", Pin('1'), "en", "page", "guide/topic"),
            new Membership("1", Pin('1'), "en", "page", "guide/topic"));
        Assert.False(DocConceptSchema.TryLoadMap(duplicateMembership.Bytes.Span, Budget, out _, out var duplicateIssue));
        Assert.Equal(DocConceptValidationFailure.IdentityConflict, duplicateIssue!.Failure);

        var nullLocaleMap = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = BridgeSchema,
            concepts = new[]
            {
                new { conceptId = "topic", memberships = new[] { new { version = "1", releaseManifestSha256 = Pin('1'), locale = (string?)null, kind = "absent" } } }
            }
        });
        Assert.True(DocConceptSchema.TryLoadMap(nullLocaleMap, Budget, out var defaultLocaleMemberships, out var localeIssue));
        Assert.Null(localeIssue);
        Assert.Null(Assert.Single(defaultLocaleMemberships).Locale);
    }

    [Fact]
    public void SourceKeysBindEveryIdentityFieldAndRejectUntrustedValues()
    {
        var identity = new DocConceptPageIdentity("1", Pin('3'), "guide/topic", "en", true);
        var key = DocConceptIdentityIndex.CreateSourceKey("instance-a", identity);
        var changed = new[]
        {
            identity with { Version = "2" },
            identity with { ReleaseManifestSha256 = Pin('4') },
            identity with { RoutePath = "guide/other" },
            identity with { Locale = "fr" },
            identity with { LocaleIsKnown = false }
        };
        Assert.All(changed, candidate => Assert.NotEqual(key, DocConceptIdentityIndex.CreateSourceKey("instance-a", candidate)));
        Assert.Throws<ArgumentException>(() => DocConceptIdentityIndex.CreateSourceKey(" ", identity));
        Assert.Throws<ArgumentException>(() => DocConceptIdentityIndex.CreateSourceKey("instance-a", identity with { RoutePath = "../topic" }));
    }

    [Fact]
    public void ExplicitAbsenceWinsOverContinuityAndHiddenOrUnavailableTargetsRevealNoMetadata()
    {
        var sourcePin = Pin('5');
        var targetPin = Pin('6');
        var source = Legacy("1", sourcePin, "guide/topic");
        var target = Legacy("2", targetPin, "guide/topic");
        var map = Bridge(
            new Membership("1", sourcePin, "en", "page", "guide/topic"),
            new Membership("2", targetPin, "en", "absent"));
        var index = Build([source, target], [map]).Index;
        Assert.True(index.TryGetPageIdentity("1", sourcePin, "guide/topic", out var identity));
        Assert.Equal(DocConceptResolutionReason.KnownAbsent, index.Resolve(identity!, "2", targetPin).Reason);

        var hidden = new DocConceptReleaseInput("3", Pin('7'), false, true, true, true, "secret/home",
            [Route("secret/page", "en")], null);
        var hiddenResult = Build([source, hidden]).Index.Resolve(identity!, "3", Pin('7'));
        Assert.Equal(DocConceptResolutionReason.TargetUnavailable, hiddenResult.Reason);
        Assert.Null(hiddenResult.TargetRelease);
        Assert.Null(hiddenResult.TargetPage);

        var unavailable = new DocConceptReleaseInput("4", Pin('8'), true, false, true, true, "unreadable/home",
            [Route("unreadable/page", "en")], null);
        var unavailableResult = Build([source, unavailable]).Index.Resolve(identity!, "4", Pin('8'));
        Assert.Equal(DocConceptResolutionReason.TargetUnavailable, unavailableResult.Reason);
        Assert.Null(unavailableResult.TargetRelease);
        Assert.Null(unavailableResult.TargetPage);
    }

    [Fact]
    public void FallbackUsesAuthoredMixedCaseRouteThenStrictAncestorAndExcludesRootOrOrdinaryPages()
    {
        var sourcePin = Pin('9');
        var targetPin = Pin('a');
        var source = Release("1", sourcePin,
            [new Page("Guide/Sub/topic", "en", "topic", "Topic", ["topic"])]);
        var targetInput = Release("2", targetPin,
            [new Page("GUIDE", "en", null, "Guide", [], Landing: true),
             new Page("guide/sub", "en", null, "Ordinary ancestor", []),
             new Page("guide/sub/deeper", "en", null, "Ordinary", []),
             new Page("", "en", null, "Root", [], Landing: true),
             new Page("elsewhere", "en", null, "Other", [])], complete: false);
        var map = Bridge(
            new Membership("1", sourcePin, "en", "page", "Guide/Sub/topic"),
            new Membership("2", targetPin, "en", "absent", FallbackSectionRoute: "guide"));
        var authored = Build([source, targetInput], [map]).Index;
        Assert.True(authored.TryGetPageIdentity("1", sourcePin, "Guide/Sub/topic", out var identity));
        var authoredResult = authored.Resolve(identity!, "2", targetPin);
        Assert.Equal("GUIDE", authoredResult.FallbackSection!.RoutePath);

        var noAuthoredMap = Build([source, targetInput]).Index;
        var ancestorResult = noAuthoredMap.Resolve(identity!, "2", targetPin);
        Assert.Equal("GUIDE", ancestorResult.FallbackSection!.RoutePath);

        var rootOnly = Release("3", Pin('b'), [new Page("", "en", null, "Root", [], Landing: true)], complete: false);
        var rootOnlyResult = Build([source, rootOnly]).Index.Resolve(identity!, "3", Pin('b'));
        Assert.Null(rootOnlyResult.FallbackSection);
    }

    [Fact]
    public void RecoveryReturnsVerifiedTargetSearchAndCanonicalHomeRouteOnlyWhenPresent()
    {
        var sourcePin = Pin('c');
        var targetPin = Pin('d');
        var source = Release("1", sourcePin,
            [new Page("guide/topic", "en", "topic", "Topic", ["topic"])]);
        var home = new Page("guide/home", "en", null, "Home", []);
        var other = new Page("other", "en", null, "Other", []);
        var targetPages = new[] { home, other };
        var target = new DocConceptReleaseInput("2", targetPin, true, true, true, true, "GUIDE/HOME",
            targetPages.Select(page => Route(page.Route, page.Locale)).ToArray(),
            Sidecar(targetPages.Select(page => new SidecarPage(page.Route, page.Locale, null, page.Title, page.Anchors)).ToArray(), complete: false));
        var index = Build([source, target]).Index;
        var sourceIdentity = new DocConceptPageIdentity("1", sourcePin, "guide/topic", "en", true);
        var resolution = index.Resolve(sourceIdentity, "2", targetPin);

        Assert.Equal(DocConceptResolutionReason.Unknown, resolution.Reason);
        Assert.True(resolution.SearchAvailable);
        Assert.Equal("guide/home", resolution.HomeRoutePath);
        Assert.Null(resolution.FallbackSection);
    }

    [Fact]
    public void SidecarSchemaRejectsInvalidTypesCoverageAndCanonicalBindings()
    {
        Assert.True(DocConceptSchema.IsSafeRoutePath(string.Empty));
        Assert.True(DocConceptSchema.IsSafeRoutePath("guide/topic"));
        foreach (var unsafeRoute in new string?[]
        {
            null, "/root/topic", "guide\\topic", "guide?query", "guide#anchor", "guide//topic", "guide/",
            "./guide", "guide/.hidden", "guide\n/topic"
        })
        {
            Assert.False(DocConceptSchema.IsSafeRoutePath(unsafeRoute));
        }

        var routes = new[] { Route("guide/topic", "en") };
        var validPage = "\"canonicalRoutePath\":\"guide/topic\",\"locale\":\"en\",\"conceptId\":\"topic\",\"title\":\"Topic\",\"anchorIds\":[]";
        var cases = new (string Json, DocConceptValidationFailure Failure)[]
        {
            ("null", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[],\"unexpected\":true}}", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":7,\"identityCoverage\":\"complete\",\"pages\":[]}}", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"unknown\",\"pages\":[]}}", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":null}}", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[\"page\"]}}", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[{{\"canonicalRoutePath\":\"../topic\",\"locale\":\"en\",\"conceptId\":\"topic\",\"title\":\"Topic\",\"anchorIds\":[]}}]}}", DocConceptValidationFailure.CanonicalRouteInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[{{\"canonicalRoutePath\":\"guide/topic\",\"locale\":\"fr\",\"conceptId\":\"topic\",\"title\":\"Topic\",\"anchorIds\":[]}}]}}", DocConceptValidationFailure.CanonicalRouteInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[{{\"canonicalRoutePath\":\"guide/topic\",\"locale\":\"en\",\"conceptId\":\"Upper\",\"title\":\"Topic\",\"anchorIds\":[]}}]}}", DocConceptValidationFailure.InvalidConceptId),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[{{\"canonicalRoutePath\":\"guide/topic\",\"locale\":\"en\",\"conceptId\":\"topic\",\"title\":false,\"anchorIds\":[]}}]}}", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[{{\"canonicalRoutePath\":\"guide/topic\",\"locale\":\"en\",\"conceptId\":\"topic\",\"anchorIds\":[]}}]}}", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[{{\"canonicalRoutePath\":\"guide/topic\",\"locale\":\"en\",\"conceptId\":\"topic\",\"title\":\"Topic\",\"anchorIds\":[3]}}]}}", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[{{\"canonicalRoutePath\":\"guide/topic\",\"locale\":\"en\",\"conceptId\":\"topic\",\"title\":\"Topic\",\"anchorIds\":[\"bad anchor\"]}}]}}", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[{{\"canonicalRoutePath\":\"guide/topic\",\"locale\":\"en\",\"conceptId\":\"topic\",\"title\":\"Topic\",\"anchorIds\":[\"bad#anchor\"]}}]}}", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[{{\"canonicalRoutePath\":\"guide/topic\",\"locale\":\"en\",\"conceptId\":\"topic\",\"title\":\"Topic\",\"anchorIds\":[],\"sectionLanding\":\"true\"}}]}}", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[{{{validPage}}},{{{validPage}}}]}}", DocConceptValidationFailure.IdentityConflict),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"partial\",\"pages\":[{{{validPage}}}]}}", DocConceptValidationFailure.IndexInvalid)
        };

        foreach (var (json, expectedFailure) in cases)
        {
            Assert.False(DocConceptIdentityIndex.TryLoadSidecar(Encoding.UTF8.GetBytes(json), routes, Budget, out var sidecar, out var issue));
            Assert.Null(sidecar);
            Assert.Equal(expectedFailure, issue!.Failure);
        }

        var duplicateRouteFacts = new[] { Route("guide/topic", "en"), Route("GUIDE/TOPIC", "en") };
        Assert.False(DocConceptIdentityIndex.TryLoadSidecar(Sidecar(), duplicateRouteFacts, Budget, out _, out var duplicateIssue));
        Assert.Equal(DocConceptValidationFailure.CanonicalRouteInvalid, duplicateIssue!.Failure);

        var missingArtifact = new[] { new DocConceptCanonicalRouteFact("guide/topic", null, "en", true) };
        Assert.False(DocConceptIdentityIndex.TryLoadSidecar(Sidecar(), missingArtifact, Budget, out _, out var artifactIssue));
        Assert.Equal(DocConceptValidationFailure.CanonicalRouteInvalid, artifactIssue!.Failure);

        var invalidRouteFacts = new[]
        {
            new DocConceptCanonicalRouteFact("guide/topic", "../topic.html", "en", true),
            new DocConceptCanonicalRouteFact("guide/topic", "guide/topic.txt", "en", true),
            new DocConceptCanonicalRouteFact("guide/topic", "guide/topic.html", "en ", true)
        };
        foreach (var routeFact in invalidRouteFacts)
        {
            Assert.False(DocConceptIdentityIndex.TryLoadSidecar(
                Sidecar(new Page("guide/topic", "en", "topic", "Topic", [])), [routeFact], Budget, out _, out var routeIssue));
            Assert.Equal(DocConceptValidationFailure.CanonicalRouteInvalid, routeIssue!.Failure);
        }
    }

    [Fact]
    public void BridgeSchemaRejectsInvalidMembershipShapesAndPins()
    {
        var cases = new (string Json, DocConceptValidationFailure Failure)[]
        {
            ("null", DocConceptValidationFailure.MapInvalid),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[],\"extra\":true}}", DocConceptValidationFailure.MapInvalid),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":null}}", DocConceptValidationFailure.MapInvalid),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[{{\"conceptId\":\"Upper\",\"memberships\":[{{\"version\":\"1\",\"releaseManifestSha256\":\"{Pin('1')}\",\"locale\":\"en\",\"kind\":\"page\",\"canonicalRoutePath\":\"guide/topic\"}}]}}]}}", DocConceptValidationFailure.InvalidConceptId),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[{{\"conceptId\":\"topic\",\"memberships\":[]}}]}}", DocConceptValidationFailure.MapInvalid),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[{{\"conceptId\":\"topic\",\"memberships\":[{{\"version\":\"1\",\"releaseManifestSha256\":\"bad\",\"locale\":\"en\",\"kind\":\"page\",\"canonicalRoutePath\":\"guide/topic\"}}]}}]}}", DocConceptValidationFailure.MapInvalid),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[{{\"conceptId\":\"topic\",\"memberships\":[{{\"version\":\"1\",\"releaseManifestSha256\":\"{Pin('1')}\",\"locale\":\"en us\",\"kind\":\"page\",\"canonicalRoutePath\":\"guide/topic\"}}]}}]}}", DocConceptValidationFailure.MapInvalid),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[{{\"conceptId\":\"topic\",\"memberships\":[{{\"version\":\"1\",\"releaseManifestSha256\":\"{Pin('1')}\",\"locale\":\"en/us\",\"kind\":\"page\",\"canonicalRoutePath\":\"guide/topic\"}}]}}]}}", DocConceptValidationFailure.MapInvalid),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[{{\"conceptId\":\"topic\",\"memberships\":[{{\"version\":\"1\",\"releaseManifestSha256\":\"{Pin('1')}\",\"locale\":\"en\",\"kind\":\"unknown\",\"canonicalRoutePath\":\"guide/topic\"}}]}}]}}", DocConceptValidationFailure.MapInvalid),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[{{\"conceptId\":\"topic\",\"memberships\":[{{\"version\":\"1\",\"releaseManifestSha256\":\"{Pin('1')}\",\"locale\":\"en\",\"kind\":\"page\"}}]}}]}}", DocConceptValidationFailure.CanonicalRouteInvalid),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[{{\"conceptId\":\"topic\",\"memberships\":[{{\"version\":\"1\",\"releaseManifestSha256\":\"{Pin('1')}\",\"locale\":\"en\",\"kind\":\"absent\",\"canonicalRoutePath\":\"guide/topic\"}}]}}]}}", DocConceptValidationFailure.MapInvalid),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[{{\"conceptId\":\"topic\",\"memberships\":[{{\"version\":\"1\",\"releaseManifestSha256\":\"{Pin('1')}\",\"locale\":\"en\",\"kind\":\"absent\",\"fallbackSectionRoute\":\"../guide\"}}]}}]}}", DocConceptValidationFailure.CanonicalRouteInvalid),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[{{\"conceptId\":\"topic\",\"memberships\":[{{\"version\":\"1\",\"releaseManifestSha256\":\"{Pin('1')}\",\"locale\":\"en\",\"kind\":\"absent\",\"sectionLanding\":\"true\"}}]}}]}}", DocConceptValidationFailure.MapInvalid)
        };

        foreach (var (json, expectedFailure) in cases)
        {
            Assert.False(DocConceptSchema.TryLoadMap(Encoding.UTF8.GetBytes(json), Budget, out var memberships, out var issue));
            Assert.Empty(memberships);
            Assert.Equal(expectedFailure, issue!.Failure);
        }

        var duplicateConceptJson = $"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[{{\"conceptId\":\"topic\",\"memberships\":[{{\"version\":\"1\",\"releaseManifestSha256\":\"{Pin('1')}\",\"locale\":\"en\",\"kind\":\"absent\"}}]}},{{\"conceptId\":\"topic\",\"memberships\":[{{\"version\":\"2\",\"releaseManifestSha256\":\"{Pin('2')}\",\"locale\":\"en\",\"kind\":\"absent\"}}]}}]}}";
        Assert.False(DocConceptSchema.TryLoadMap(Encoding.UTF8.GetBytes(duplicateConceptJson), Budget, out _, out var duplicateConceptIssue));
        Assert.Equal(DocConceptValidationFailure.IdentityConflict, duplicateConceptIssue!.Failure);
        Assert.False(DocConceptSchema.TryLoadMap(Encoding.UTF8.GetBytes("{"), Budget, out _, out var malformedIssue));
        Assert.Equal(DocConceptValidationFailure.MapInvalid, malformedIssue!.Failure);
        Assert.False(DocConceptSchema.TryLoadMap(Encoding.UTF8.GetBytes($"{{\"schema\":\"{BridgeSchema}\",\"schema\":\"{BridgeSchema}\",\"concepts\":[]}}"), Budget, out _, out var duplicatePropertyIssue));
        Assert.Equal(DocConceptValidationFailure.MapInvalid, duplicatePropertyIssue!.Failure);
    }

    [Fact]
    public void InvalidCatalogRouteFactsAndPinsAreReportedWithoutHidingValidRelease()
    {
        var valid = Release("1", Pin('2'), [new Page("guide/topic", "en", "topic", "Topic", [])]);
        var invalidPin = new DocConceptReleaseInput("hidden", new string('z', 64), false, false, false, false, null, [], null);
        var invalidRoute = new DocConceptReleaseInput("2", Pin('3'), true, true, true, true, null,
            [new DocConceptCanonicalRouteFact("guide/topic", "../outside.html", "en", true)], null);
        var duplicateVersion = new DocConceptReleaseInput("1", Pin('4'), true, true, true, false, null, [], null);

        var startup = DocConceptIdentityIndex.BuildCombined(
            [valid, invalidPin, invalidRoute, duplicateVersion], [], DocConceptValidationMode.Quarantine, Budget);

        Assert.Contains(startup.Issues, issue => issue.Failure == DocConceptValidationFailure.ReleasePinInvalid);
        Assert.Contains(startup.Issues, issue => issue.Failure == DocConceptValidationFailure.CanonicalRouteInvalid);
        Assert.Contains(startup.Issues, issue => issue.Failure == DocConceptValidationFailure.IdentityConflict);
        var available = startup.Index.PublicAvailableReleases;
        Assert.Equal(new[] { "1", "2" }, available.Select(release => release.Version));
        Assert.DoesNotContain(available, release => release.Version == "hidden");
        Assert.Empty(available.Single(release => release.Version == "2").Pages);
        Assert.True(startup.Index.TryGetPageIdentity("1", Pin('2'), "guide/topic", out _));
    }

    [Fact]
    public void PinnedBridgeSupportsKnownNonlocalizedCanonicalPages()
    {
        var sourcePin = Pin('a');
        var targetPin = Pin('b');
        var source = Legacy("1", sourcePin, "legacy/topic");
        var target = Legacy("2", targetPin, "current/topic");
        var map = Bridge(
            new Membership("1", sourcePin, null, "page", "legacy/topic"),
            new Membership("2", targetPin, null, "page", "current/topic"));

        var index = Build([source, target], [map]).Index;

        Assert.True(index.TryGetPageIdentity("1", sourcePin, "legacy/topic", out var identity));
        Assert.Null(identity!.Locale);
        Assert.True(identity.LocaleIsKnown);
        var resolution = index.Resolve(identity, "2", targetPin);
        Assert.Equal(DocConceptResolutionReason.MovedPage, resolution.Reason);
        Assert.Null(resolution.TargetPage!.Locale);
        Assert.True(resolution.TargetPage.LocaleIsKnown);
    }

    [Fact]
    public void IncomingSourcesAreDestinationBoundAndExcludeAuthoredFallbackOutcomes()
    {
        var firstPin = Pin('1');
        var secondPin = Pin('2');
        var targetPin = Pin('3');
        var retiredPin = Pin('4');
        var first = Legacy("1", firstPin, "old/one");
        var second = Legacy("2", secondPin, "old/two");
        var target = LegacyWithRoutes("3", targetPin, "new/topic", "new/section");
        var retired = Legacy("4", retiredPin, "old/retired");
        var map = Bridge(
            new Membership("1", firstPin, "en", "page", "old/one"),
            new Membership("2", secondPin, "en", "page", "old/two"),
            new Membership("3", targetPin, "en", "page", "new/topic"),
            new Membership("4", retiredPin, "en", "page", "old/retired") { ConceptId = "retired" },
            new Membership("3", targetPin, "en", "absent", FallbackSectionRoute: "new/topic") { ConceptId = "retired" });
        var index = Build([first, second, target, retired], [map]).Index;
        var identities = index.EnumeratePublicPages().Select(row => row.Identity).ToArray();
        var destination = identities.Single(identity => identity.Version == "3" && identity.RoutePath == "new/topic");

        var incoming = index.GetIncoming(destination, identities, "instance-a").ToArray();

        Assert.Equal(2, incoming.Length);
        Assert.Equal(new[] { "1", "2" }, incoming.Select(source => source.SourceIdentity.Version).OrderBy(version => version, StringComparer.Ordinal));
        Assert.All(incoming, source => Assert.Equal(DocConceptResolutionReason.MovedPage, source.Reason));
        Assert.DoesNotContain(incoming, source => source.SourceIdentity.Version == "4");
        var fallbackOnlyIdentity = identities.Single(identity => identity.Version == "4");
        var fallbackOnly = index.Resolve(fallbackOnlyIdentity, "3", targetPin);
        Assert.Equal(DocConceptResolutionReason.KnownAbsent, fallbackOnly.Reason);
        Assert.Null(fallbackOnly.TargetPage);
        Assert.Equal("new/topic", fallbackOnly.FallbackSection!.RoutePath);
    }

    [Fact]
    public void CombinedLoaderRejectsInvalidReleasePinsAndQuarantinesUnsafeOrUnservableRouteFacts()
    {
        var validPin = Pin('5');
        var valid = Release("valid", validPin, [new Page("guide/topic", "en", "topic", "Topic", [])]);
        var missingVersion = new DocConceptReleaseInput(" ", Pin('6'), true, true, true, true, null, [], null);
        var invalidPin = new DocConceptReleaseInput("bad-pin", new string('g', 64), true, true, true, true, null, [], null);
        var duplicateSamePin = Release("valid", validPin, [new Page("other", "en", "other", "Other", [])]);
        var duplicateVersionPin = Release("valid", Pin('7'), [new Page("other", "en", "other", "Other", [])]);
        var unsafeRoute = new DocConceptReleaseInput("unsafe", Pin('8'), true, true, true, false, null,
            [new DocConceptCanonicalRouteFact("../outside", "outside.html", "en", true)], null);
        var badLocale = new DocConceptReleaseInput("bad-locale", Pin('9'), true, true, true, false, null,
            [new DocConceptCanonicalRouteFact("guide/locale", "guide/locale.html", "en ", true)], null);

        var strict = DocConceptIdentityIndex.BuildCombined(
            [valid, missingVersion, invalidPin, duplicateSamePin, duplicateVersionPin, unsafeRoute, badLocale],
            [], DocConceptValidationMode.Strict, Budget);
        var startup = DocConceptIdentityIndex.BuildCombined(
            [valid, missingVersion, invalidPin, duplicateSamePin, duplicateVersionPin, unsafeRoute, badLocale],
            [], DocConceptValidationMode.Quarantine, Budget);

        Assert.False(strict.IsValid);
        Assert.Contains(strict.Issues, issue => issue.Failure == DocConceptValidationFailure.ReleasePinInvalid);
        Assert.Contains(strict.Issues, issue => issue.Failure == DocConceptValidationFailure.IdentityConflict);
        Assert.Contains(strict.Issues, issue => issue.Failure == DocConceptValidationFailure.CanonicalRouteInvalid);
        Assert.True(startup.Index.TryGetPageIdentity("valid", validPin, "guide/topic", out _));
        Assert.DoesNotContain(startup.Index.PublicAvailableReleases, release => release.Version == "bad-pin");
        Assert.Empty(startup.Index.PublicAvailableReleases.Single(release => release.Version == "unsafe").Pages);

        var unservableInput = new DocConceptReleaseInput("unservable", Pin('a'), true, true, true, true, null,
            [new DocConceptCanonicalRouteFact("guide/unservable", "guide/unservable.html", "en", true, IsServeable: false)],
            Sidecar(new Page("guide/unservable", "en", "unservable", "Unservable", [])));
        var unservable = DocConceptIdentityIndex.BuildCombined([unservableInput], [], DocConceptValidationMode.Quarantine, Budget);
        Assert.Contains(unservable.Issues, issue => issue.Failure == DocConceptValidationFailure.CanonicalRouteInvalid);
    }

    [Fact]
    public void ConflictingRepeatedBridgeReferenceBytesPoisonLegacyCompatibility()
    {
        var sourcePin = Pin('b');
        var targetPin = Pin('c');
        var source = Legacy("1", sourcePin, "guide/topic");
        var target = Legacy("2", targetPin, "guide/topic");
        var validMap = Bridge(
            new Membership("1", sourcePin, "en", "page", "guide/topic"),
            new Membership("2", targetPin, "en", "page", "guide/topic"));
        var conflictingBytes = Encoding.UTF8.GetBytes("{}");
        var conflictingReference = new DocConceptMapReference(validMap.Path, validMap.Sha256, conflictingBytes);

        var result = DocConceptIdentityIndex.BuildCombined(
            [source, target], [validMap, conflictingReference], DocConceptValidationMode.Quarantine, Budget);
        Assert.Contains(result.Issues, issue => issue.Failure == DocConceptValidationFailure.MapInvalid);
        Assert.True(result.Index.TryGetPageIdentity("1", sourcePin, "guide/topic", out var sourceIdentity));
        Assert.Equal(DocConceptResolutionReason.Unknown, result.Index.Resolve(sourceIdentity!, "2", targetPin).Reason);
    }

    [Fact]
    public void BridgeMembershipsRequireExactAvailableReleasesAndCanonicalFallbackPages()
    {
        var sourcePin = Pin('d');
        var targetPin = Pin('e');
        var hiddenPin = Pin('f');
        var unavailablePin = Pin('a');
        var source = Legacy("1", sourcePin, "old/topic");
        var target = Legacy("2", targetPin, "new/topic");
        var hidden = new DocConceptReleaseInput("hidden", hiddenPin, false, true, true, false, null,
            [Route("hidden/topic", null, localeKnown: false)], null);
        var unavailable = new DocConceptReleaseInput("unavailable", unavailablePin, true, false, true, false, null,
            [Route("unavailable/topic", null, localeKnown: false)], null);
        var map = Bridge(
            new Membership("1", sourcePin, "en", "page", "old/topic"),
            new Membership("2", targetPin, "en", "absent", FallbackSectionRoute: "missing/section"),
            new Membership("hidden", hiddenPin, "en", "page", "hidden/topic") { ConceptId = "hidden-concept" },
            new Membership("unavailable", unavailablePin, "en", "page", "unavailable/topic") { ConceptId = "unavailable-concept" },
            new Membership("not-in-catalog", Pin('b'), "en", "absent") { ConceptId = "stale-concept" });

        var result = DocConceptIdentityIndex.BuildCombined(
            [source, target, hidden, unavailable], [map], DocConceptValidationMode.Quarantine, Budget);

        Assert.Contains(result.Issues, issue => issue.Failure == DocConceptValidationFailure.ReleasePinInvalid);
        Assert.Contains(result.Issues, issue => issue.Failure == DocConceptValidationFailure.CanonicalRouteInvalid);
        Assert.True(result.Index.TryGetPageIdentity("1", sourcePin, "old/topic", out var sourceIdentity));
        Assert.Equal(DocConceptResolutionReason.Unknown, result.Index.Resolve(sourceIdentity!, "2", targetPin).Reason);
        Assert.Null(result.Index.GetPage(new DocConceptPageIdentity("hidden", hiddenPin, "hidden/topic", "en", false)));
        Assert.Null(result.Index.GetPage(new DocConceptPageIdentity("unavailable", unavailablePin, "unavailable/topic", "en", false)));

        var wrongLocalePage = Release("5", Pin('5'), [new Page("guide/topic", "en", null, "Topic", [])], complete: false);
        var wrongLocaleMap = Bridge(new Membership("5", Pin('5'), "fr", "page", "guide/topic"));
        var wrongLocale = DocConceptIdentityIndex.BuildCombined(
            [wrongLocalePage], [wrongLocaleMap], DocConceptValidationMode.Quarantine, Budget);
        Assert.Contains(wrongLocale.Issues, issue => issue.Failure == DocConceptValidationFailure.CanonicalRouteInvalid);

        var missingPageMap = Bridge(new Membership("1", sourcePin, "en", "page", "not-a-canonical-page"));
        var missingPage = DocConceptIdentityIndex.BuildCombined(
            [source], [missingPageMap], DocConceptValidationMode.Quarantine, Budget);
        Assert.Contains(missingPage.Issues, issue => issue.Failure == DocConceptValidationFailure.CanonicalRouteInvalid);

        var fallbackTarget = Release("6", Pin('6'), [new Page("fr/section", "fr", null, "Section", [], Landing: true)], complete: false);
        var wrongLocaleFallbackMap = Bridge(
            new Membership("1", sourcePin, "en", "page", "old/topic"),
            new Membership("6", Pin('6'), "en", "absent", FallbackSectionRoute: "fr/section"));
        var wrongLocaleFallback = DocConceptIdentityIndex.BuildCombined(
            [source, fallbackTarget], [wrongLocaleFallbackMap], DocConceptValidationMode.Quarantine, Budget);
        Assert.Contains(wrongLocaleFallback.Issues, issue => issue.Failure == DocConceptValidationFailure.CanonicalRouteInvalid);

        var legacyLandingMap = Bridge(new Membership("legacy-landing", Pin('7'), "en", "page", "guide/landing", SectionLanding: true));
        var legacyLandingRelease = Legacy("legacy-landing", Pin('7'), "guide/landing");
        var legacyLanding = DocConceptIdentityIndex.BuildCombined(
            [legacyLandingRelease], [legacyLandingMap], DocConceptValidationMode.Quarantine, Budget);
        Assert.True(legacyLanding.Index.TryGetPageIdentity("legacy-landing", Pin('7'), "guide/landing", out var legacyLandingIdentity));
        Assert.True(legacyLanding.Index.GetPage(legacyLandingIdentity!)!.Landing);
    }

    [Fact]
    public void InvalidMapReferencesAndLimitsDisableLegacyContinuityInsteadOfGuessing()
    {
        var sourcePin = Pin('b');
        var targetPin = Pin('c');
        var source = Release("1", sourcePin, [new Page("guide/topic", "en", null, "Topic", [])], complete: false);
        var target = Release("2", targetPin, [new Page("guide/topic", "en", null, "Topic", [])], complete: false);
        var identity = new DocConceptPageIdentity("1", sourcePin, "guide/topic", "en", true);
        Assert.Equal(DocConceptResolutionReason.SamePage, Build([source, target]).Index.Resolve(identity, "2", targetPin).Reason);

        var unsupportedBytes = Encoding.UTF8.GetBytes("{\"schema\":\"unsupported\",\"concepts\":[]}");
        var unsupported = new DocConceptMapReference("unsupported.json",
            Convert.ToHexString(SHA256.HashData(unsupportedBytes)).ToLowerInvariant(), unsupportedBytes);
        var invalidReferences = new DocConceptMapReference[]
        {
            null!,
            new("", Pin('1'), Encoding.UTF8.GetBytes("{}")),
            new("/absolute/map.json", Pin('1'), Encoding.UTF8.GetBytes("{}")),
            new("../escape.json", Pin('1'), Encoding.UTF8.GetBytes("{}")),
            new("folder\\map.json", Pin('1'), Encoding.UTF8.GetBytes("{}")),
            new("folder?query", Pin('1'), Encoding.UTF8.GetBytes("{}")),
            new("folder#anchor", Pin('1'), Encoding.UTF8.GetBytes("{}")),
            new("folder\nmap", Pin('1'), Encoding.UTF8.GetBytes("{}")),
            new("folder//map.json", Pin('1'), Encoding.UTF8.GetBytes("{}")),
            new("./map.json", Pin('1'), Encoding.UTF8.GetBytes("{}")),
            new("folder/../map.json", Pin('1'), Encoding.UTF8.GetBytes("{}")),
            new("invalid-pin.json", "not-a-digest", Encoding.UTF8.GetBytes("{}")),
            new("digest-mismatch.json", new string('0', 64), Encoding.UTF8.GetBytes("{}")),
            unsupported
        };

        var result = DocConceptIdentityIndex.BuildCombined(
            [source, target], invalidReferences, DocConceptValidationMode.Quarantine, Budget);
        Assert.Contains(result.Issues, issue => issue.Failure == DocConceptValidationFailure.MapInvalid);
        Assert.Equal(DocConceptResolutionReason.Unknown, result.Index.Resolve(identity, "2", targetPin).Reason);
        Assert.Throws<ArgumentNullException>(() => DocConceptIdentityIndex.BuildCombined(null!, [], DocConceptValidationMode.Quarantine, Budget));
        Assert.Throws<ArgumentNullException>(() => DocConceptIdentityIndex.BuildCombined([source], null!, DocConceptValidationMode.Quarantine, Budget));

        var invalidLimit = DocConceptIdentityIndex.BuildCombined([source], [], DocConceptValidationMode.Strict, 0);
        Assert.False(invalidLimit.IsValid);
        Assert.Contains(invalidLimit.Issues, issue => issue.Failure == DocConceptValidationFailure.ArtifactBudgetInvalid);
        Assert.False(DocConceptIdentityIndex.TryLoadSidecar(Sidecar(new Page("guide/topic", "en", "topic", "Topic", [])),
            [Route("guide/topic", "en")], 0, out _, out var sidecarLimitIssue));
        Assert.Equal(DocConceptValidationFailure.ArtifactBudgetInvalid, sidecarLimitIssue!.Failure);
        Assert.False(DocConceptSchema.TryLoadMap(Encoding.UTF8.GetBytes("{}"), 0, out _, out var mapLimitIssue));
        Assert.Equal(DocConceptValidationFailure.ArtifactBudgetInvalid, mapLimitIssue!.Failure);
    }

    [Fact]
    public void CanonicalInventoryRejectsDuplicateNullAndIncompleteFactsAndBindsHomeToPages()
    {
        var pin = Pin('6');
        var badInventories = new (IReadOnlyList<DocConceptCanonicalRouteFact> Routes, DocConceptValidationFailure Failure)[]
        {
            ([null!], DocConceptValidationFailure.CanonicalRouteInvalid),
            ([new DocConceptCanonicalRouteFact("guide/topic", "guide/topic.html", "en", true), Route("GUIDE/TOPIC", "en")], DocConceptValidationFailure.IdentityConflict),
            ([new DocConceptCanonicalRouteFact("guide/topic", "", "en", true)], DocConceptValidationFailure.CanonicalRouteInvalid),
            ([new DocConceptCanonicalRouteFact("guide/topic", "guide/topic.html", "", true)], DocConceptValidationFailure.CanonicalRouteInvalid)
        };

        foreach (var (routes, failure) in badInventories)
        {
            var input = new DocConceptReleaseInput(Guid.NewGuid().ToString("N"), pin, true, true, true, false, null, routes, null);
            var result = DocConceptIdentityIndex.BuildCombined([input], [], DocConceptValidationMode.Quarantine, Budget);
            Assert.Contains(result.Issues, issue => issue.Failure == failure);
        }

        var unservable = new DocConceptReleaseInput("unservable-only", Pin('7'), true, true, true, false, "guide/topic",
            [new DocConceptCanonicalRouteFact("guide/topic", null, "en", true, IsServeable: false)], null);
        var invalidHome = new DocConceptReleaseInput("invalid-home", Pin('8'), true, true, true, false, "../outside",
            [Route("guide/topic", "en")], null);
        var index = DocConceptIdentityIndex.BuildCombined([unservable, invalidHome], [], DocConceptValidationMode.Quarantine, Budget).Index;
        Assert.Null(index.GetPage(new DocConceptPageIdentity("unservable-only", Pin('7'), "guide/topic", "en", true)));
        Assert.False(index.TryGetPageIdentity("unservable-only", Pin('7'), "guide/topic", out _));
        Assert.Null(index.PublicAvailableReleases.Single(release => release.Version == "invalid-home").HomeRoutePath);
    }

    [Fact]
    public void ResolverReportsRenamesAndRejectsAnUnverifiedSourcePage()
    {
        var sourcePin = Pin('6');
        var targetPin = Pin('7');
        var source = Release("1", sourcePin, [new Page("guide/topic", "en", "topic", "Old title", [])]);
        var target = Release("2", targetPin, [new Page("guide/topic", "en", "topic", "New title", [])]);
        var index = Build([source, target]).Index;
        var valid = new DocConceptPageIdentity("1", sourcePin, "guide/topic", "en", true);
        Assert.Equal(DocConceptResolutionReason.RenamedPage, index.Resolve(valid, "2", targetPin).Reason);
        var unverified = valid with { RoutePath = "missing/page" };
        var result = index.Resolve(unverified, "2", targetPin);
        Assert.Equal(DocConceptResolutionReason.Unknown, result.Reason);
        Assert.Null(result.SourcePage);
        Assert.Null(result.TargetPage);
    }

    [Fact]
    public void ConflictingPageAbsenceAndPublishedLandingFactsQuarantineMemberships()
    {
        var sourcePin = Pin('c');
        var targetPin = Pin('d');
        var source = Legacy("1", sourcePin, "old/topic");
        var target = LegacyWithRoutes("2", targetPin, "new/topic", "new/section");
        var pageMap = Bridge(
            new Membership("1", sourcePin, "en", "page", "old/topic"),
            new Membership("2", targetPin, "en", "page", "new/topic"));
        var absenceMap = Bridge(new Membership("2", targetPin, "en", "absent"));
        var absenceConflict = DocConceptIdentityIndex.BuildCombined(
            [source, target], [pageMap, absenceMap], DocConceptValidationMode.Quarantine, Budget);
        Assert.Contains(absenceConflict.Issues, issue => issue.Failure == DocConceptValidationFailure.IdentityConflict);
        Assert.True(absenceConflict.Index.TryGetPageIdentity("1", sourcePin, "old/topic", out var legacyIdentity));
        Assert.Equal(DocConceptResolutionReason.Ambiguous, absenceConflict.Index.Resolve(legacyIdentity!, "2", targetPin).Reason);

        var indexedSource = Release("10", Pin('e'), [new Page("guide/topic", "en", "topic", "Topic", [], Landing: false)]);
        var indexedTarget = Release("11", Pin('f'), [new Page("guides/topic", "en", "topic", "Topic", [])]);
        var landingConflict = Bridge(new Membership("10", Pin('e'), "en", "page", "guide/topic", SectionLanding: true));
        var conflicted = DocConceptIdentityIndex.BuildCombined(
            [indexedSource, indexedTarget], [landingConflict], DocConceptValidationMode.Quarantine, Budget);
        Assert.Contains(conflicted.Issues, issue => issue.Failure == DocConceptValidationFailure.IdentityConflict);
        var indexedIdentity = conflicted.Index.EnumeratePublicPages().Single(row => row.Identity.Version == "10").Identity;
        Assert.Equal(DocConceptResolutionReason.Ambiguous, conflicted.Index.Resolve(indexedIdentity, "11", Pin('f')).Reason);
    }

    [Fact]
    public void PageLookupsAndIncomingIteratorRejectStaleOrMissingIdentities()
    {
        var sourcePin = Pin('1');
        var targetPin = Pin('2');
        var hiddenPin = Pin('3');
        var unavailablePin = Pin('4');
        var source = Release("1", sourcePin, [new Page("guide/topic", "en", "topic", "Topic", [])]);
        var target = Release("2", targetPin, [new Page("guides/topic", "en", "topic", "Topic", [])]);
        var hidden = new DocConceptReleaseInput("hidden", hiddenPin, false, true, true, false, null,
            [Route("guide/topic", "en")], null);
        var unavailable = new DocConceptReleaseInput("unavailable", unavailablePin, true, false, true, false, null,
            [Route("guide/topic", "en")], null);
        var index = Build([source, target, hidden, unavailable]).Index;
        var candidates = index.EnumeratePublicPages().Select(row => row.Identity).ToArray();
        Assert.True(index.TryGetPageIdentity("1", sourcePin, "GUIDE/TOPIC", out var sourceIdentity));
        var destination = candidates.Single(identity => identity.Version == "2");

        Assert.NotNull(index.GetPage(sourceIdentity!));
        Assert.Null(index.GetPage(sourceIdentity! with { ReleaseManifestSha256 = Pin('5') }));
        Assert.False(index.TryGetPageIdentity("missing", sourcePin, "guide/topic", out _));
        Assert.False(index.TryGetPageIdentity("1", Pin('5'), "guide/topic", out _));
        Assert.False(index.TryGetPageIdentity("1", sourcePin, "missing", out _));
        Assert.False(index.TryGetPageIdentity("hidden", hiddenPin, "guide/topic", out _));
        Assert.False(index.TryGetPageIdentity("unavailable", unavailablePin, "guide/topic", out _));

        Assert.Empty(index.GetIncoming(destination with { RoutePath = "missing" }, candidates, "instance-a"));
        var stale = sourceIdentity! with { ReleaseManifestSha256 = Pin('6') };
        var candidatesWithStaleAndNull = candidates.Append(stale).Append(null!).ToArray();
        var incoming = index.GetIncoming(destination, candidatesWithStaleAndNull, "instance-a").ToArray();
        Assert.Single(incoming);
        Assert.Equal(sourceIdentity, incoming[0].SourceIdentity);
        Assert.Throws<ArgumentNullException>(() => index.GetIncoming(null!, candidates, "instance-a").ToArray());
        Assert.Throws<ArgumentNullException>(() => index.GetIncoming(destination, null!, "instance-a").ToArray());
        Assert.Throws<ArgumentException>(() => index.GetIncoming(destination, candidates, " ").ToArray());
    }

    [Fact]
    public void OutgoingEnumerationResolvesEachExactTargetInCallerOrder()
    {
        var sourcePin = Pin('7');
        var targetPin = Pin('8');
        var source = Release("1", sourcePin, [new Page("guide/topic", "en", "topic", "Topic", [])]);
        var target = Release("2", targetPin, [new Page("guides/topic", "en", "topic", "Topic", [])]);
        var index = Build([source, target]).Index;
        var identity = new DocConceptPageIdentity("1", sourcePin, "guide/topic", "en", true);

        var results = index.EnumerateOutgoing(identity,
        [
            ("missing", targetPin),
            ("2", targetPin)
        ]).ToArray();

        Assert.Equal(2, results.Length);
        Assert.Equal(DocConceptResolutionReason.TargetUnavailable, results[0].Reason);
        Assert.Equal(DocConceptResolutionReason.MovedPage, results[1].Reason);
        Assert.Throws<ArgumentNullException>(() => index.EnumerateOutgoing(null!, []).ToArray());
        Assert.Throws<ArgumentNullException>(() => index.EnumerateOutgoing(identity, null!).ToArray());
    }

    private static DocConceptIndexBuildResult Build(IReadOnlyList<DocConceptReleaseInput> releases) =>
        DocConceptIdentityIndex.BuildCombined(releases, [], DocConceptValidationMode.Quarantine, Budget);

    private static DocConceptReleaseInput Release(string version, string pin, IReadOnlyList<Page> pages, bool complete = true)
    {
        var sidecarPages = pages.Select(page => new SidecarPage(page.Route, page.Locale, page.ConceptId, page.Title, page.Anchors, page.Landing)).ToArray();
        var coverage = complete && pages.All(page => page.ConceptId is not null);
        var routes = pages.Select(page => Route(page.Route, page.Locale)).ToArray();
        return new DocConceptReleaseInput(version, pin, true, true, true, true, null, routes,
            Sidecar(sidecarPages, coverage));
    }

    private static DocConceptReleaseInput Legacy(string version, string pin, string route) =>
        new(version, pin, true, true, true, false, null, [Route(route, null, localeKnown: false)], null);

    private static DocConceptReleaseInput LegacyWithRoutes(string version, string pin, params string[] routes) =>
        new(version, pin, true, true, true, true, routes.FirstOrDefault(), routes.Select(route => Route(route, null, localeKnown: false)).ToArray(), null);

    private static DocConceptCanonicalRouteFact Route(string route, string? locale, bool localeKnown = true) =>
        new(route, route.Length == 0 ? "index.html" : route + ".html", locale, localeKnown);

    private static DocConceptIndexBuildResult Build(IReadOnlyList<DocConceptReleaseInput> releases, IReadOnlyList<DocConceptMapReference> maps) =>
        DocConceptIdentityIndex.BuildCombined(releases, maps, DocConceptValidationMode.Quarantine, Budget);

    private static byte[] Sidecar(params Page[] pages) => Sidecar(pages.Select(page => new SidecarPage(page.Route, page.Locale, page.ConceptId, page.Title, page.Anchors, page.Landing)).ToArray(), pages.All(page => page.ConceptId is not null));

    private static byte[] Sidecar(IEnumerable<SidecarPage> pages, bool complete)
    {
        var pageValues = pages.Select(page => new
        {
            canonicalRoutePath = page.Route,
            locale = page.Locale,
            conceptId = page.ConceptId,
            title = page.Title,
            anchorIds = page.Anchors,
            sectionLanding = page.Landing
        });
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = SidecarSchema,
            identityCoverage = complete ? "complete" : "partial",
            pages = pageValues
        });
    }

    private static DocConceptMapReference Bridge(params Membership[] memberships)
    {
        var concepts = memberships.GroupBy(membership => membership.ConceptId, StringComparer.Ordinal).Select(group => new
        {
            conceptId = group.Key,
            memberships = group.Select(membership =>
            {
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["version"] = membership.Version,
                    ["releaseManifestSha256"] = membership.Pin,
                    ["locale"] = membership.Locale,
                    ["kind"] = membership.Kind
                };
                if (membership.Route is not null) fields["canonicalRoutePath"] = membership.Route;
                if (membership.FallbackSectionRoute is not null) fields["fallbackSectionRoute"] = membership.FallbackSectionRoute;
                if (membership.SectionLanding is { } landing) fields["sectionLanding"] = landing;
                return fields;
            })
        });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = BridgeSchema, concepts });
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new DocConceptMapReference("concept-map.json", digest, bytes);
    }

    private static string Pin(char digit) => new(digit, 64);

    private sealed record Page(string Route, string? Locale, string? ConceptId, string Title, IReadOnlyList<string> Anchors, bool Landing = false);
    private sealed record SidecarPage(string Route, string? Locale, string? ConceptId, string Title, IReadOnlyList<string> Anchors, bool Landing = false);
    private sealed record Membership(string Version, string Pin, string? Locale, string Kind, string? Route = null, string? FallbackSectionRoute = null, bool? SectionLanding = null)
    {
        public string ConceptId { get; init; } = "topic";
    }
}
