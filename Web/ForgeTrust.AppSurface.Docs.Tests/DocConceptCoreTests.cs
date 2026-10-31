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
        var routes = new[] { Route("guide/topic", "en") };
        var validPage = "\"canonicalRoutePath\":\"guide/topic\",\"locale\":\"en\",\"conceptId\":\"topic\",\"title\":\"Topic\",\"anchorIds\":[]";
        var cases = new (string Json, DocConceptValidationFailure Failure)[]
        {
            ("null", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[],\"unexpected\":true}}", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":7,\"identityCoverage\":\"complete\",\"pages\":[]}}", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"unknown\",\"pages\":[]}}", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[\"page\"]}}", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[{{\"canonicalRoutePath\":\"../topic\",\"locale\":\"en\",\"conceptId\":\"topic\",\"title\":\"Topic\",\"anchorIds\":[]}}]}}", DocConceptValidationFailure.CanonicalRouteInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[{{\"canonicalRoutePath\":\"guide/topic\",\"locale\":\"fr\",\"conceptId\":\"topic\",\"title\":\"Topic\",\"anchorIds\":[]}}]}}", DocConceptValidationFailure.CanonicalRouteInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[{{\"canonicalRoutePath\":\"guide/topic\",\"locale\":\"en\",\"conceptId\":\"Upper\",\"title\":\"Topic\",\"anchorIds\":[]}}]}}", DocConceptValidationFailure.InvalidConceptId),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[{{\"canonicalRoutePath\":\"guide/topic\",\"locale\":\"en\",\"conceptId\":\"topic\",\"title\":false,\"anchorIds\":[]}}]}}", DocConceptValidationFailure.IndexInvalid),
            ($"{{\"schema\":\"{SidecarSchema}\",\"identityCoverage\":\"complete\",\"pages\":[{{\"canonicalRoutePath\":\"guide/topic\",\"locale\":\"en\",\"conceptId\":\"topic\",\"title\":\"Topic\",\"anchorIds\":[3]}}]}}", DocConceptValidationFailure.IndexInvalid),
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
    }

    [Fact]
    public void BridgeSchemaRejectsInvalidMembershipShapesAndPins()
    {
        var cases = new (string Json, DocConceptValidationFailure Failure)[]
        {
            ("null", DocConceptValidationFailure.MapInvalid),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[],\"extra\":true}}", DocConceptValidationFailure.MapInvalid),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[{{\"conceptId\":\"Upper\",\"memberships\":[{{\"version\":\"1\",\"releaseManifestSha256\":\"{Pin('1')}\",\"locale\":\"en\",\"kind\":\"page\",\"canonicalRoutePath\":\"guide/topic\"}}]}}]}}", DocConceptValidationFailure.InvalidConceptId),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[{{\"conceptId\":\"topic\",\"memberships\":[]}}]}}", DocConceptValidationFailure.MapInvalid),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[{{\"conceptId\":\"topic\",\"memberships\":[{{\"version\":\"1\",\"releaseManifestSha256\":\"bad\",\"locale\":\"en\",\"kind\":\"page\",\"canonicalRoutePath\":\"guide/topic\"}}]}}]}}", DocConceptValidationFailure.MapInvalid),
            ($"{{\"schema\":\"{BridgeSchema}\",\"concepts\":[{{\"conceptId\":\"topic\",\"memberships\":[{{\"version\":\"1\",\"releaseManifestSha256\":\"{Pin('1')}\",\"locale\":\"en us\",\"kind\":\"page\",\"canonicalRoutePath\":\"guide/topic\"}}]}}]}}", DocConceptValidationFailure.MapInvalid),
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
