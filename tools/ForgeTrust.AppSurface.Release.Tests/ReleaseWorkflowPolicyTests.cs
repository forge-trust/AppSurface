using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Release.Tests;

using ForgeTrust.AppSurface.Release;

public sealed class ReleaseWorkflowPolicyTests
{
    [Fact]
    public void ReleasePreparationChangePolicyAcceptsTheCompletePreparationArtifactSet()
    {
        var result = ReleasePreparationChangePolicy.Validate(
            "1.2.3",
            [
                new("A", "releases/v1.2.3.md"),
                new("A", "releases/v1.2.3.md.yml"),
                new("A", "releases/v1.2.3.release.json"),
                new("A", "releases/v1.2.3.evidence.json"),
                new("M", "releases/current.md"),
                new("M", "CHANGELOG.md"),
                new("M", "releases/unreleased.md"),
                new("M", "releases/unreleased.md.yml"),
                new("D", "releases/unreleased.entries/2026-08-08-release-workflow.md")
            ],
            ["releases/unreleased.entries/2026-08-08-release-workflow.md"]);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void ReleasePreparationChangePolicyRejectsPackageReadmeChanges()
    {
        var result = ReleasePreparationChangePolicy.Validate(
            "1.2.3",
            [
                new("A", "releases/v1.2.3.md"),
                new("A", "releases/v1.2.3.md.yml"),
                new("A", "releases/v1.2.3.release.json"),
                new("A", "releases/v1.2.3.evidence.json"),
                new("M", "releases/current.md"),
                new("M", "CHANGELOG.md"),
                new("M", "releases/unreleased.md"),
                new("M", "releases/unreleased.md.yml"),
                new("M", "Web/ForgeTrust.AppSurface.Web/README.md")
            ]);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => string.Equals(
                error,
                "Unexpected release-preparation path: Web/ForgeTrust.AppSurface.Web/README.md.",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "A requested release version is required.")]
    [InlineData("1.2.3", "Required release-preparation path is missing: releases/v1.2.3.md.yml.", "releases/v1.2.3.md")]
    [InlineData("1.2.3", "Required release-preparation path is missing: releases/v1.2.3.md.", "releases/v1.2.3.md.yml", "releases/v1.2.3.release.json", "releases/v1.2.3.evidence.json", "releases/current.md")]
    public void ReleasePreparationChangePolicyRejectsEmptyOrPartialDiff(string version, string expectedError, params string[] paths)
    {
        var result = ReleasePreparationChangePolicy.Validate(
            version,
            paths.Select(path => new ReleasePreparationChange("A", path)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => string.Equals(error, expectedError, StringComparison.Ordinal));
    }

    [Fact]
    public void ReleasePreparationChangePolicyRejectsAnActuallyEmptyDiff()
    {
        var result = ReleasePreparationChangePolicy.Validate("1.2.3", []);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("diff is empty", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleasePreparationChangePolicyRejectsUnsupportedGitStatuses()
    {
        var result = ReleasePreparationChangePolicy.Validate(
            "1.2.3",
            [
                new("C", "README.md"),
                new("M", "CHANGELOG.md"),
                new("M", "CHANGELOG.md")
            ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Unsupported Git change status 'C'", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("path appears more than once: CHANGELOG.md", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleasePreparationChangePolicyRejectsSidecarUnexpectedChangesDeletesAndRenames()
    {
        var result = ReleasePreparationChangePolicy.Validate(
            "1.2.3",
            [
                new("A", "releases/v1.2.3.md"),
                new("A", "releases/v1.2.3.md.yml"),
                new("A", "releases/v1.2.3.release.json"),
                new("A", "releases/v1.2.3.evidence.json"),
                new("M", "releases/current.md"),
                new("M", "CHANGELOG.md"),
                new("M", "releases/unreleased.md"),
                new("M", "releases/unreleased.md.yml"),
                new("M", "releases/current.md.yml"),
                new("M", "README.md"),
                new("D", "releases/v1.1.0.md"),
                new("R100", "releases/v1.0.0.md", "releases/v1.2.3.md")
            ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("current.md.yml", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("Unexpected", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("may delete only", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("Renames", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleasePreparationChangePolicyRejectsUnexpectedUnreleasedEntryChanges()
    {
        var result = ReleasePreparationChangePolicy.Validate(
            "1.2.3",
            [
                new("A", "releases/v1.2.3.md"),
                new("A", "releases/v1.2.3.md.yml"),
                new("A", "releases/v1.2.3.release.json"),
                new("A", "releases/v1.2.3.evidence.json"),
                new("M", "releases/current.md"),
                new("M", "CHANGELOG.md"),
                new("M", "releases/unreleased.md"),
                new("M", "releases/unreleased.md.yml"),
                new("A", "releases/unreleased.entries/2026-08-08-feature.md"),
                new("D", "releases/unreleased.entries/not-an-entry.md")
            ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Unexpected release-preparation path", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("may delete only", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleasePreparationChangePolicyRequiresManifestProofForEveryArchivedEntry()
    {
        var requiredChanges = new[]
        {
            new ReleasePreparationChange("A", "releases/v1.2.3.md"),
            new ReleasePreparationChange("A", "releases/v1.2.3.md.yml"),
            new ReleasePreparationChange("A", "releases/v1.2.3.release.json"),
            new ReleasePreparationChange("A", "releases/v1.2.3.evidence.json"),
            new ReleasePreparationChange("M", "releases/current.md"),
            new ReleasePreparationChange("M", "CHANGELOG.md"),
            new ReleasePreparationChange("M", "releases/unreleased.md"),
            new ReleasePreparationChange("M", "releases/unreleased.md.yml"),
            new ReleasePreparationChange("D", "releases/unreleased.entries/2026-08-08-archived.md")
        };

        var unprovenDeletion = ReleasePreparationChangePolicy.Validate("1.2.3", requiredChanges);
        var missingDeletion = ReleasePreparationChangePolicy.Validate(
            "1.2.3",
            requiredChanges[..^1],
            ["releases/unreleased.entries/2026-08-08-archived.md"]);
        var duplicateDeletion = ReleasePreparationChangePolicy.Validate(
            "1.2.3",
            [.. requiredChanges, requiredChanges[^1]],
            ["releases/unreleased.entries/2026-08-08-archived.md"]);

        Assert.False(unprovenDeletion.IsValid);
        Assert.Contains(unprovenDeletion.Errors, error => error.Contains("may delete only", StringComparison.Ordinal));
        Assert.False(missingDeletion.IsValid);
        Assert.Contains(missingDeletion.Errors, error => error.Contains("does not delete", StringComparison.Ordinal));
        Assert.False(duplicateDeletion.IsValid);
        Assert.Contains(duplicateDeletion.Errors, error => error.Contains("appears more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleasePreparationChangePolicyRejectsInvalidDuplicateAndUnorderedManifestEntryPaths()
    {
        var result = ReleasePreparationChangePolicy.Validate(
            "1.2.3",
            [],
            [
                "releases/unreleased.entries/2026-08-08-zulu.md",
                "releases/unreleased.entries/2026-08-08-alpha.md",
                "releases/unreleased.entries/2026-08-08-alpha.md",
                "not-an-unreleased-entry.md"
            ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("invalid consumed unreleased entry path", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("same consumed unreleased entry path", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("must be ordinally sorted", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleasePreparationChangePolicyParsesRenameNameStatus()
    {
        var changes = ReleasePreparationChangePolicy.ParseNameStatus(
            "R100\treleases/v1.0.0.md\treleases/v1.2.3.md\nM\treleases/current.md\n");

        Assert.Collection(
            changes,
            rename =>
            {
                Assert.Equal("R100", rename.Status);
                Assert.Equal("releases/v1.0.0.md", rename.OriginalPath);
                Assert.Equal("releases/v1.2.3.md", rename.Path);
            },
            current => Assert.Equal("releases/current.md", current.Path));
    }

    [Fact]
    public async Task ReleasePreparationChangePolicyValidatesPullRequestDiffWhenRequestedByWorkflow()
    {
        var version = Environment.GetEnvironmentVariable("RELEASE_PREP_POLICY_VERSION");
        if (version is null)
        {
            return;
        }

        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var baseRef = Environment.GetEnvironmentVariable("RELEASE_PREP_POLICY_BASE_REF");
        Assert.False(string.IsNullOrWhiteSpace(baseRef), "RELEASE_PREP_POLICY_BASE_REF must be set by the release-prep workflow.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add("diff");
        process.StartInfo.ArgumentList.Add("--find-renames");
        process.StartInfo.ArgumentList.Add("--no-color");
        process.StartInfo.ArgumentList.Add("--name-status");
        process.StartInfo.ArgumentList.Add($"origin/{baseRef}...HEAD");
        Assert.True(process.Start());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync());
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, error);

        var manifestPath = TestPathUtils.PathUnder(repositoryRoot!, "releases", $"v{version}.release.json");
        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        Assert.True(ReleaseManifestV2Validator.TryDeserialize(manifestJson, out var manifest, out var manifestIssue), manifestIssue);
        Assert.Equal(version, manifest!.Version);

        var result = ReleasePreparationChangePolicy.Validate(
            version,
            ReleasePreparationChangePolicy.ParseNameStatus(output),
            manifest.ConsumedUnreleasedEntryPaths);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public async Task ReleasePrepReviewUsesReadOnlyPullRequestTriggerWithoutSecrets()
    {
        var workflow = await ReadRepositoryFileAsync(".github/workflows/release-prep.yml");
        var review = GetWorkflowJob(workflow, "release-prep-review");

        Assert.Contains("pull_request:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target", workflow, StringComparison.Ordinal);
        Assert.Contains("contents: read", review, StringComparison.Ordinal);
        Assert.Contains("pull-requests: read", review, StringComparison.Ordinal);
        Assert.Contains("ref: ${{ github.event.pull_request.head.sha }}", review, StringComparison.Ordinal);
        Assert.Contains("./eng/release verify-prep-diff", review, StringComparison.Ordinal);
        Assert.Contains("--base-ref \"${GITHUB_BASE_REF}\"", review, StringComparison.Ordinal);
        Assert.Contains("Publish release-preparation diff report", review, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.", review, StringComparison.Ordinal);
        Assert.DoesNotContain("create-github-app-token", review, StringComparison.Ordinal);
        Assert.DoesNotContain("release-bot-token.outputs.token", review, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseContractWorkflowValidatesAppendOnlyEntriesAndTemplateMarkers()
    {
        var workflow = await ReadRepositoryFileAsync(".github/workflows/release-contract.yml");

        Assert.Contains("NODE_OPTIONS: --use-system-ca", workflow, StringComparison.Ordinal);
        Assert.Contains("github.rest.repos.getContent", workflow, StringComparison.Ordinal);
        Assert.Contains("validateUnreleasedTemplate", workflow, StringComparison.Ordinal);
        Assert.Contains("validateAddedUnreleasedEntries", workflow, StringComparison.Ordinal);
        Assert.Contains("hasValidEntryDate", workflow, StringComparison.Ordinal);
        Assert.Contains("Feature pull requests must not edit releases/unreleased.md", workflow, StringComparison.Ordinal);
        Assert.Contains("appsurface:unreleased-entry directive", workflow, StringComparison.Ordinal);
        Assert.Contains("must not introduce a top-level # or ## heading", workflow, StringComparison.Ordinal);
        Assert.Contains("hasTopLevelAtxHeading", workflow, StringComparison.Ordinal);
        Assert.Contains("hasTopLevelSetextHeading", workflow, StringComparison.Ordinal);
        Assert.Contains("must contain exactly one", workflow, StringComparison.Ordinal);
        Assert.Contains("must not contain unsupported append-only entry markers", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseWorkflowsDeclareConcurrencyAndAvoidEval()
    {
        var prep = await ReadRepositoryFileAsync(".github/workflows/release-prep.yml");
        var prepare = GetWorkflowJob(prep, "prepare", "release-prep-review");
        var publish = await ReadRepositoryFileAsync(".github/workflows/release-publish.yml");
        var stablePublish = await ReadRepositoryFileAsync(".github/workflows/nuget-stable-publish.yml");

        Assert.Contains("concurrency:", prep, StringComparison.Ordinal);
        Assert.Contains("concurrency:", publish, StringComparison.Ordinal);
        Assert.Contains("concurrency:", stablePublish, StringComparison.Ordinal);
        Assert.Contains("github.ref == 'refs/heads/main'", prepare, StringComparison.Ordinal);
        Assert.Contains("environment:\n      name: release-prep\n      deployment: false", prepare, StringComparison.Ordinal);
        Assert.Contains("RELEASE_BOT_APP_CLIENT_ID", prepare, StringComparison.Ordinal);
        Assert.Contains("RELEASE_BOT_APP_PRIVATE_KEY", prepare, StringComparison.Ordinal);
        Assert.Contains("actions/create-github-app-token@bcd2ba49218906704ab6c1aa796996da409d3eb1 # v3.2.0", prepare, StringComparison.Ordinal);
        Assert.Contains("client-id: ${{ secrets.RELEASE_BOT_APP_CLIENT_ID }}", prepare, StringComparison.Ordinal);
        Assert.Contains("private-key: ${{ secrets.RELEASE_BOT_APP_PRIVATE_KEY }}", prepare, StringComparison.Ordinal);
        Assert.Contains("owner: forge-trust", prepare, StringComparison.Ordinal);
        Assert.Contains("repositories: AppSurface", prepare, StringComparison.Ordinal);
        Assert.Contains("permission-contents: write", prepare, StringComparison.Ordinal);
        Assert.Contains("permission-pull-requests: write", prepare, StringComparison.Ordinal);
        Assert.Contains("skip-token-revoke: false", prepare, StringComparison.Ordinal);
        Assert.Contains("RELEASE_BRANCH: release-bot/v${{ inputs.version }}", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("RELEASE_BRANCH: release/v${{ inputs.version }}", prepare, StringComparison.Ordinal);
        Assert.Contains("GH_TOKEN: ${{ steps.release-bot-token.outputs.token }}", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("RELEASE_BOT_TOKEN", prep, StringComparison.Ordinal);
        var trustedBaseStepIndex = prepare.IndexOf("- name: Validate trusted release base", StringComparison.Ordinal);
        var checkoutStepIndex = prepare.IndexOf("- name: Checkout base", StringComparison.Ordinal);
        var prepareFilesStepIndex = prepare.IndexOf("- name: Prepare release files", StringComparison.Ordinal);
        var commitPreparationStepIndex = prepare.IndexOf("- name: Commit release preparation", StringComparison.Ordinal);
        var credentialsStepIndex = prepare.IndexOf("- name: Validate release bot app credentials", StringComparison.Ordinal);
        var mintTokenStepIndex = prepare.IndexOf("- name: Mint release bot installation token", StringComparison.Ordinal);
        Assert.True(trustedBaseStepIndex >= 0, "Release Prep must validate the base ref before checkout.");
        Assert.True(checkoutStepIndex > trustedBaseStepIndex, "Release Prep must validate the base ref before checkout.");
        Assert.True(prepareFilesStepIndex > checkoutStepIndex, "Release Prep must prepare release files only after checkout.");
        Assert.True(commitPreparationStepIndex > prepareFilesStepIndex, "Release Prep must commit release files before reading App credentials.");
        Assert.True(credentialsStepIndex >= 0, "Release Prep must validate its App credentials before minting a token.");
        Assert.True(mintTokenStepIndex > credentialsStepIndex, "Release Prep must validate its App credentials before minting a token.");
        Assert.True(credentialsStepIndex > commitPreparationStepIndex, "Release Prep must not read App credentials before release preparation is committed.");
        const string trustedBasePattern = "^(main|release/[0-9]+\\.[0-9]+\\.[0-9]+)$";
        Assert.Contains(trustedBasePattern, prepare, StringComparison.Ordinal);
        Assert.Matches(trustedBasePattern, "main");
        Assert.Matches(trustedBasePattern, "release/0.1.0");
        Assert.DoesNotMatch(trustedBasePattern, "feature/release-prep");
        Assert.DoesNotMatch(trustedBasePattern, "release/0.1");
        Assert.DoesNotMatch(trustedBasePattern, "release/0.1.0-preview.1");
        Assert.DoesNotMatch(trustedBasePattern, "release/0.1.0/repair");
        Assert.DoesNotMatch(trustedBasePattern, "refs/heads/main");
        Assert.DoesNotMatch(trustedBasePattern, "v0.1.0");
        Assert.DoesNotMatch(trustedBasePattern, string.Empty);
        Assert.Contains("persist-credentials: false", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("token: ${{ steps.release-bot-token.outputs.token }}", prepare, StringComparison.Ordinal);
        var credentialsStep = prepare[credentialsStepIndex..mintTokenStepIndex];
        Assert.Contains("if [[ -z \"${RELEASE_BOT_APP_CLIENT_ID}\" ]]; then", credentialsStep, StringComparison.Ordinal);
        Assert.Contains("if [[ -z \"${RELEASE_BOT_APP_PRIVATE_KEY}\" ]]; then", credentialsStep, StringComparison.Ordinal);
        Assert.Equal(2, credentialsStep.Split("exit 1", StringSplitOptions.None).Length - 1);
        var createPullRequestStepIndex = prepare.IndexOf("- name: Create release pull request", mintTokenStepIndex, StringComparison.Ordinal);
        Assert.True(createPullRequestStepIndex > mintTokenStepIndex, "Release Prep must create its pull request only after minting the App token.");
        var mintTokenStep = prepare[mintTokenStepIndex..createPullRequestStepIndex];
        var commitPreparationStep = prepare[commitPreparationStepIndex..credentialsStepIndex];
        var createPullRequestStep = prepare[createPullRequestStepIndex..];
        Assert.Single(Regex.Matches(mintTokenStep, @"(?m)^\s+owner:"));
        Assert.Single(Regex.Matches(mintTokenStep, @"(?m)^\s+owner:\s*forge-trust\s*$"));
        Assert.Single(Regex.Matches(mintTokenStep, @"(?m)^\s+repositories:"));
        Assert.Single(Regex.Matches(mintTokenStep, @"(?m)^\s+repositories:\s*AppSurface\s*$"));
        Assert.Equal(2, Regex.Matches(mintTokenStep, @"(?m)^\s+permission-").Count);
        Assert.Contains("git -c core.hooksPath=/dev/null checkout -b", commitPreparationStep, StringComparison.Ordinal);
        Assert.Contains("checkout -b \"${RELEASE_BRANCH}\"", commitPreparationStep, StringComparison.Ordinal);
        Assert.Contains("git -c core.hooksPath=/dev/null commit", commitPreparationStep, StringComparison.Ordinal);
        Assert.Contains("git add", commitPreparationStep, StringComparison.Ordinal);
        Assert.Contains("git bundle create \"${RUNNER_TEMP}/release-prep.bundle\" \"${RELEASE_BRANCH}\"", commitPreparationStep, StringComparison.Ordinal);
        Assert.DoesNotContain("GH_TOKEN", commitPreparationStep, StringComparison.Ordinal);
        Assert.DoesNotContain("git add", createPullRequestStep, StringComparison.Ordinal);
        Assert.DoesNotContain("git commit", createPullRequestStep, StringComparison.Ordinal);
        Assert.DoesNotContain("git checkout", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("VERSION: ${{ inputs.version }}", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("gh pr edit \"${existing_pr}\" --title \"chore(release): prepare v${VERSION}\"", createPullRequestStep, StringComparison.Ordinal);
        Assert.Equal(2, createPullRequestStep.Split("--title \"chore(release): prepare v${VERSION}\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 git init --bare \"${push_repository}\"", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("fetch \"${RUNNER_TEMP}/release-prep.bundle\" \"refs/heads/${RELEASE_BRANCH}:refs/heads/${RELEASE_BRANCH}\"", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("remote add origin \"https://github.com/forge-trust/AppSurface.git\"", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("push --force-with-lease origin \"refs/heads/${RELEASE_BRANCH}:refs/heads/${RELEASE_BRANCH}\"", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("gh pr list --head \"${RELEASE_BRANCH}\" --base \"${BASE_REF}\"", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("--json number,headRefName,headRepositoryOwner,isCrossRepository,baseRefName", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains(".headRepositoryOwner.login == \"forge-trust\"", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains(".isCrossRepository == false", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains(".baseRefName == env.BASE_REF", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("--head \"${RELEASE_BRANCH}\"", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("github_token_header=\"AUTHORIZATION: basic $(printf 'x-access-token:%s' \"${GITHUB_TOKEN}\"", prepare, StringComparison.Ordinal);
        Assert.Contains("release_bot_header=\"AUTHORIZATION: basic $(printf 'x-access-token:%s' \"${GH_TOKEN}\"", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("git_with_release_bot_token() {", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("GIT_CONFIG_KEY_0=http.https://github.com/.extraheader", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("GIT_CONFIG_VALUE_0=\"${release_bot_header}\"", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("GIT_CONFIG_GLOBAL=/dev/null", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("GIT_CONFIG_NOSYSTEM=1", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("git -c core.hooksPath=/dev/null \"$@\"", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("git_with_release_bot_token -C \"${push_repository}\" fetch origin", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("git_with_release_bot_token -C \"${push_repository}\" ls-remote --exit-code --heads origin", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("else\n              remote_branch_status=$?\n            fi", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("[[ \"${remote_branch_status}\" -ne 2 ]]", createPullRequestStep, StringComparison.Ordinal);
        Assert.Contains("git_with_release_bot_token -C \"${push_repository}\" push --force-with-lease origin", createPullRequestStep, StringComparison.Ordinal);
        Assert.DoesNotContain("-c http.https://github.com/.extraheader=", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("|| true", createPullRequestStep, StringComparison.Ordinal);
        var hookableGitCommands = Regex.Matches(prepare, @"(?m)^\s+git .*(?:\bcheckout\b|\bcommit\b|\bpush\b).*$");
        Assert.NotEmpty(hookableGitCommands);
        Assert.All(hookableGitCommands, command => Assert.Contains("core.hooksPath=/dev/null", command.Value, StringComparison.Ordinal));
        Assert.DoesNotContain("eval ", prep, StringComparison.Ordinal);
        Assert.DoesNotContain("eval ", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("eval ", stablePublish, StringComparison.Ordinal);
        Assert.Contains("actions: read", publish, StringComparison.Ordinal);
        Assert.Contains("BASE_REF: ${{ inputs.base-ref }}", prep, StringComparison.Ordinal);
        Assert.Contains("expected_base=\"$(git rev-parse \"origin/${BASE_REF}\")\"", prep, StringComparison.Ordinal);
        Assert.Contains("--base \"${BASE_REF}\"", prep, StringComparison.Ordinal);
        Assert.DoesNotContain("expected_main=\"$(git rev-parse origin/main)\"", prep, StringComparison.Ordinal);
        Assert.DoesNotContain("merge-base --is-ancestor HEAD origin/main", prep, StringComparison.Ordinal);
        Assert.Contains("./eng/release verify-prep-diff", prep, StringComparison.Ordinal);
        Assert.Contains("--base-ref \"${GITHUB_BASE_REF}\"", prep, StringComparison.Ordinal);
        Assert.Contains("Validate release semantics", prep, StringComparison.Ordinal);
        Assert.Contains("dotnet test tools/ForgeTrust.AppSurface.Release.Tests/ForgeTrust.AppSurface.Release.Tests.csproj", prep, StringComparison.Ordinal);
        Assert.Contains("./eng/release check --version \"${version}\" --fail-on-warnings --allow-existing-targets", prep, StringComparison.Ordinal);
        Assert.Contains("RELEASE_PREP_REPORT", prep, StringComparison.Ordinal);
        Assert.Contains("GITHUB_STEP_SUMMARY", prep, StringComparison.Ordinal);
        Assert.DoesNotContain("Legacy release warning recomputation", prep, StringComparison.Ordinal);
        Assert.DoesNotContain("release_prep_changes", prep, StringComparison.Ordinal);
        Assert.DoesNotContain("git diff --no-renames --name-status", prep, StringComparison.Ordinal);
        Assert.Contains("git add -u -- releases/unreleased.entries", prep, StringComparison.Ordinal);
        Assert.Contains("releases/v${VERSION}.release.json", prep, StringComparison.Ordinal);
        Assert.Contains("git add", prep, StringComparison.Ordinal);
        Assert.DoesNotContain("git add CHANGELOG.md releases", prep, StringComparison.Ordinal);
        Assert.Contains("docs export", prep, StringComparison.Ordinal);
        Assert.Contains("docs verify-archive", prep, StringComparison.Ordinal);
        Assert.Contains("--docs-catalog", prep, StringComparison.Ordinal);
        Assert.Contains("--docs-trusted-release-root", prep, StringComparison.Ordinal);
        Assert.Contains("git worktree add --detach", prep, StringComparison.Ordinal);
        Assert.Contains("refs/tags/v${version}^{commit}", prep, StringComparison.Ordinal);
        Assert.DoesNotContain("Allowing superseded release sidecar", prep, StringComparison.Ordinal);
        Assert.DoesNotContain("find releases -maxdepth 1 -name 'v*.release.json'", prep, StringComparison.Ordinal);
        Assert.Contains("--github-output", publish, StringComparison.Ordinal);
        Assert.Contains("Validate tag-bound release evidence", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("docs-catalog:", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("docs-trusted-release-root:", publish, StringComparison.Ordinal);
        Assert.Contains("promote-recommended:", publish, StringComparison.Ordinal);
        Assert.Contains("type: choice", publish, StringComparison.Ordinal);
        Assert.Contains("promote-recommended must be true or false", publish, StringComparison.Ordinal);
        Assert.Contains("validate-release:", publish, StringComparison.Ordinal);
        Assert.Contains("publish-docs-archive:", publish, StringComparison.Ordinal);
        Assert.Contains("deploy-docs-pages:", publish, StringComparison.Ordinal);
        Assert.Contains("verify-public-docs:", publish, StringComparison.Ordinal);
        Assert.Contains("publish-github-release:", publish, StringComparison.Ordinal);
        Assert.Contains("--dry-run", publish, StringComparison.Ordinal);
        Assert.Contains("docs-publication", publish, StringComparison.Ordinal);
        Assert.Contains("dotnet build ForgeTrust.AppSurface.slnx -c Release", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet build AppSurface.slnx", publish, StringComparison.Ordinal);
        Assert.Contains("Normalize release base ref", publish, StringComparison.Ordinal);
        Assert.Contains("base_ref: ${{ steps.base.outputs.base_ref }}", publish, StringComparison.Ordinal);
        Assert.Contains("origin/*) base_ref=\"${base_ref#origin/}\"", publish, StringComparison.Ordinal);
        Assert.Contains("refs/heads/*) base_ref=\"${base_ref#refs/heads/}\"", publish, StringComparison.Ordinal);
        Assert.Contains("refs/remotes/origin/*) base_ref=\"${base_ref#refs/remotes/origin/}\"", publish, StringComparison.Ordinal);
        Assert.Contains("ref: ${{ needs.validate-release.outputs.tag_commit }}", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("ref: ${{ needs.validate-release.outputs.tag }}", publish, StringComparison.Ordinal);
        Assert.Contains("persist-credentials: false", publish, StringComparison.Ordinal);
        Assert.Contains("TAG_COMMIT: ${{ needs.validate-release.outputs.tag_commit }}", publish, StringComparison.Ordinal);
        Assert.Contains("actual_tag_commit=\"$(git rev-parse \"refs/tags/${TAG}^{commit}\")\"", publish, StringComparison.Ordinal);
        Assert.Contains("Expected ${TAG} to resolve to ${TAG_COMMIT}; got ${actual_tag_commit}.", publish, StringComparison.Ordinal);
        Assert.Contains("git show \"${TAG_COMMIT}:releases/v${VERSION}.md\"", publish, StringComparison.Ordinal);
        Assert.Contains("Resolve tag-bound sidecar for docs export", publish, StringComparison.Ordinal);
        Assert.Contains("./eng/release inspect", publish, StringComparison.Ordinal);
        Assert.Contains("--out \"${projection}\"", publish, StringComparison.Ordinal);
        Assert.Contains("inspect_proof=\"${RUNNER_TEMP}/appsurface-tagged-release-inspect.txt\"", publish, StringComparison.Ordinal);
        Assert.Contains("| tee \"${inspect_proof}\"", publish, StringComparison.Ordinal);
        Assert.Contains("cat \"${inspect_proof}\" >> \"${GITHUB_STEP_SUMMARY}\"", publish, StringComparison.Ordinal);
        Assert.Contains("cp \"${projection}\" \"releases/v${VERSION}.md.yml\"", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("git add ", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("git commit ", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("git push ", publish, StringComparison.Ordinal);
        Assert.Contains("Export current docs root and exact docs tree", publish, StringComparison.Ordinal);
        Assert.Contains("AppSurfaceDocs__Contributor__DefaultBranch: ${{ needs.validate-release.outputs.base_ref }}", publish, StringComparison.Ordinal);
        Assert.Contains("AppSurfaceDocs__Contributor__SourceRef: ${{ needs.validate-release.outputs.tag_commit }}", publish, StringComparison.Ordinal);
        Assert.Contains("AppSurfaceDocs__Contributor__SourceUrlTemplate: https://github.com/${{ github.repository }}/blob/{branch}/{path}", publish, StringComparison.Ordinal);
        Assert.Contains("AppSurfaceDocs__Contributor__SymbolSourceUrlTemplate: https://github.com/${{ github.repository }}/blob/{ref}/{path}#L{line}", publish, StringComparison.Ordinal);
        Assert.Contains("AppSurfaceDocs__Contributor__EditUrlTemplate: https://github.com/${{ github.repository }}/edit/{branch}/{path}", publish, StringComparison.Ordinal);
        Assert.Contains("AppSurfaceDocs__Contributor__LastUpdatedMode: Git", publish, StringComparison.Ordinal);
        Assert.Contains("AppSurfaceDocs__Identity__BrandingAssets__DirectoryPath: branding", publish, StringComparison.Ordinal);
        Assert.Contains("AppSurfaceDocs__Harvest__JavaScript__IncludeGlobs__0: Web/ForgeTrust.RazorWire/assets/contracts/razorwire-public-contracts.js", publish, StringComparison.Ordinal);
        Assert.Contains("--output \"${EXISTING_PAGES_ROOT}\"", publish, StringComparison.Ordinal);
        Assert.Contains("cp -R \"${EXISTING_PAGES_ROOT}/.\" \"${exact_tree}/\"", publish, StringComparison.Ordinal);
        Assert.Contains("Hydrate existing release docs archives", publish, StringComparison.Ordinal);
        Assert.Contains("mapfile -t release_rows", publish, StringComparison.Ordinal);
        Assert.Contains("gh api --paginate \"repos/${GITHUB_REPOSITORY}/releases\"", publish, StringComparison.Ordinal);
        Assert.Contains("select(.draft | not)", publish, StringComparison.Ordinal);
        Assert.Contains("[.tag_name, .prerelease] | @tsv", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("select(.isPrerelease == false)", publish, StringComparison.Ordinal);
        Assert.Contains("--existing-pages-root \"${EXISTING_PAGES_ROOT}\"", publish, StringComparison.Ordinal);
        Assert.Contains("curl -fsSL \"${root}/docs\"", publish, StringComparison.Ordinal);
        var resolveProjectionIndex = publish.IndexOf("Resolve tag-bound sidecar for docs export", StringComparison.Ordinal);
        var exportDocsIndex = publish.IndexOf("Export current docs root and exact docs tree", StringComparison.Ordinal);
        Assert.True(resolveProjectionIndex >= 0, "Release publish must resolve the tag-bound projection before docs export.");
        Assert.True(exportDocsIndex > resolveProjectionIndex, "Docs export must see the verified tagged projection, not committed prepared metadata.");
        var hydrateIndex = publish.IndexOf("Hydrate existing release docs archives", StringComparison.Ordinal);
        var planIndex = publish.IndexOf("Create docs publication plan", StringComparison.Ordinal);
        Assert.True(hydrateIndex >= 0, "Release publish must hydrate prior release archives.");
        Assert.True(planIndex > hydrateIndex, "Prior release archive hydration must happen before the publication plan is created.");
        Assert.Contains("expected_release_manifest_sha256=\"${EXPECTED_MANIFEST_SHA256}\"", publish, StringComparison.Ordinal);
        Assert.Contains("if [[ \"${expected_release_manifest_sha256}\" == \"generated\" ]]; then", publish, StringComparison.Ordinal);
        Assert.Contains("expected_release_manifest_sha256=\"$(sha256sum \"${exact_tree}/.appsurface-docs-release-manifest.json\" | awk '{print $1}')\"", publish, StringComparison.Ordinal);
        Assert.Contains("--expected-release-manifest-sha256 \"${expected_release_manifest_sha256}\"", publish, StringComparison.Ordinal);
        Assert.Contains("docs verify-archive", publish, StringComparison.Ordinal);
        Assert.Contains("gh release create \"${TAG}\" --verify-tag --draft", publish, StringComparison.Ordinal);
        Assert.Contains("gh release edit \"${TAG}\" --title \"${TITLE}\" --notes-file \"${notes_file}\"", publish, StringComparison.Ordinal);
        Assert.Contains("gh release upload \"${TAG}\" \"${ARCHIVE_PATH}\" \"${SHA256_PATH}\" --clobber", publish, StringComparison.Ordinal);
        Assert.Contains("${{ runner.temp }}/appsurface-tagged-release-inspect.txt", publish, StringComparison.Ordinal);
        Assert.Contains("actions/upload-pages-artifact", publish, StringComparison.Ordinal);
        Assert.Contains("include-hidden-files: true", publish, StringComparison.Ordinal);
        Assert.Contains("actions/deploy-pages", publish, StringComparison.Ordinal);
        Assert.Contains("curl -fsSL \"${root}/versions.json\"", publish, StringComparison.Ordinal);
        Assert.Contains("PROMOTE_RECOMMENDED: ${{ inputs.promote-recommended }}", publish, StringComparison.Ordinal);
        Assert.Contains("--arg promoteRecommended \"${PROMOTE_RECOMMENDED}\"", publish, StringComparison.Ordinal);
        Assert.Contains("$promoteRecommended != \"true\" or .recommendedVersion == $version", publish, StringComparison.Ordinal);
        Assert.Contains("gh release download \"${TAG}\" --repo \"${GITHUB_REPOSITORY}\" --pattern \"${ARCHIVE_ASSET_NAME}\"", publish, StringComparison.Ordinal);
        Assert.Contains("gh release edit ${TAG} --repo ${GITHUB_REPOSITORY} --draft=false", publish, StringComparison.Ordinal);
        Assert.Contains("gh release view ${TAG} --repo ${GITHUB_REPOSITORY} --json isDraft,url", publish, StringComparison.Ordinal);
        Assert.Contains("args=(release edit \"${TAG}\" --draft=false)", publish, StringComparison.Ordinal);
        Assert.Contains("gh release delete ${TAG} --repo ${GITHUB_REPOSITORY} --cleanup-tag=false", publish, StringComparison.Ordinal);
        Assert.Contains("tar -tzf \"${asset_dir}/${asset_name}\"", publish, StringComparison.Ordinal);
        Assert.Contains("unsafe absolute or parent-relative entry", publish, StringComparison.Ordinal);
        Assert.Contains("tar -tzvf \"${asset_dir}/${asset_name}\"", publish, StringComparison.Ordinal);
        Assert.Contains("non-regular tar entry", publish, StringComparison.Ordinal);
        Assert.Contains("tar -xzf \"${asset_dir}/${asset_name}\" -C \"${exact_tree}\"", publish, StringComparison.Ordinal);
        Assert.Contains("base-ref:", publish, StringComparison.Ordinal);
        Assert.Contains("BASE_REF: ${{ inputs.base-ref }}", publish, StringComparison.Ordinal);
        Assert.Contains("BASE_REF: ${{ steps.base.outputs.base_ref }}", publish, StringComparison.Ordinal);
        Assert.Contains("BASE_REF: ${{ needs.validate-release.outputs.base_ref }}", publish, StringComparison.Ordinal);
        Assert.Contains("--base-ref \"${BASE_REF}\"", publish, StringComparison.Ordinal);
        Assert.Contains("git fetch origin -- \"${BASE_REF}:refs/remotes/origin/${BASE_REF}\"", publish, StringComparison.Ordinal);
        Assert.Contains("evidence_path", await ReadRepositoryFileAsync("tools/ForgeTrust.AppSurface.Release/ReleasePublishing.cs"), StringComparison.Ordinal);
        Assert.Contains("pages: write", publish, StringComparison.Ordinal);
        Assert.Contains("Required to publish the verified Pages artifact.", publish, StringComparison.Ordinal);
        Assert.Contains("id-token: write", publish, StringComparison.Ordinal);
        Assert.Contains("Required by deploy-pages to mint the GitHub Pages deployment token.", publish, StringComparison.Ordinal);
        Assert.Contains("Required so release validation can verify the protected NuGet workflow run for the tag.", publish, StringComparison.Ordinal);
        Assert.Contains("Required to create or reuse the draft GitHub Release and upload docs archive assets.", publish, StringComparison.Ordinal);
        Assert.Contains("contents: write # Required because draft release assets are not downloadable with the workflow token under contents: read.", publish, StringComparison.Ordinal);
        Assert.Contains("Required to promote the verified draft GitHub Release to public.", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("attestations: write", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("supportState:\"Supported\"", publish, StringComparison.Ordinal);
        Assert.Contains("supportState:\"Maintained\"", publish, StringComparison.Ordinal);
        Assert.Contains("stable_version", publish, StringComparison.Ordinal);
        Assert.Contains("semver_key", publish, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleasePrepPushScriptHandlesNewExistingAndFailureBranchStates()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var workflow = await ReadRepositoryFileAsync(".github/workflows/release-prep.yml");
        var prepare = GetWorkflowJob(workflow, "prepare", "release-prep-review");
        var script = GetFinalWorkflowStepRun(prepare, "Create release pull request");
        var cases = new[]
        {
            new { Name = "new", ExistingPullRequest = "", ExpectedExitCode = 0, ExpectedPullRequestCommand = "gh:pr create --base main --head release-bot/v0.2.0 --title chore(release): prepare v0.2.0 --body-file", ExpectsBranchLookup = true, ExpectsPush = true, ExpectsPullRequestList = true },
            new { Name = "existing", ExistingPullRequest = "42", ExpectedExitCode = 0, ExpectedPullRequestCommand = "gh:pr edit 42 --title chore(release): prepare v0.2.0 --body-file", ExpectsBranchLookup = false, ExpectsPush = true, ExpectsPullRequestList = true },
            new { Name = "existing-fetch-failure", ExistingPullRequest = "", ExpectedExitCode = 128, ExpectedPullRequestCommand = string.Empty, ExpectsBranchLookup = true, ExpectsPush = false, ExpectsPullRequestList = false },
            new { Name = "lookup-failure", ExistingPullRequest = "", ExpectedExitCode = 128, ExpectedPullRequestCommand = string.Empty, ExpectsBranchLookup = true, ExpectsPush = false, ExpectsPullRequestList = false },
            new { Name = "push-failure", ExistingPullRequest = "", ExpectedExitCode = 128, ExpectedPullRequestCommand = string.Empty, ExpectsBranchLookup = false, ExpectsPush = true, ExpectsPullRequestList = false },
            new { Name = "list-failure", ExistingPullRequest = "", ExpectedExitCode = 128, ExpectedPullRequestCommand = string.Empty, ExpectsBranchLookup = false, ExpectsPush = true, ExpectsPullRequestList = true },
            new { Name = "edit-failure", ExistingPullRequest = "42", ExpectedExitCode = 128, ExpectedPullRequestCommand = "gh:pr edit 42 --title chore(release): prepare v0.2.0 --body-file", ExpectsBranchLookup = false, ExpectsPush = true, ExpectsPullRequestList = true },
            new { Name = "create-failure", ExistingPullRequest = "", ExpectedExitCode = 128, ExpectedPullRequestCommand = "gh:pr create --base main --head release-bot/v0.2.0 --title chore(release): prepare v0.2.0 --body-file", ExpectsBranchLookup = false, ExpectsPush = true, ExpectsPullRequestList = true }
        };

        foreach (var testCase in cases)
        {
            var result = await RunReleasePrepPushScriptAsync(script, testCase.Name, testCase.ExistingPullRequest);

            Assert.True(
                result.ExitCode == testCase.ExpectedExitCode,
                $"{testCase.Name} exited {result.ExitCode}. stderr:{Environment.NewLine}{result.StandardError}");
            Assert.Equal(testCase.ExpectsBranchLookup, result.Calls.Contains("git:ls-remote", StringComparison.Ordinal));
            Assert.Equal(testCase.ExpectsPush, result.Calls.Contains("git:push", StringComparison.Ordinal));
            Assert.Equal(testCase.ExpectsPullRequestList, result.Calls.Contains("gh:pr list", StringComparison.Ordinal));
            if (!string.IsNullOrEmpty(testCase.ExpectedPullRequestCommand))
            {
                Assert.Contains(testCase.ExpectedPullRequestCommand, result.Calls, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("gh:pr edit", result.Calls, StringComparison.Ordinal);
                Assert.DoesNotContain("gh:pr create", result.Calls, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task StableNuGetPublishWorkflowUsesStablePolicy()
    {
        var workflow = await ReadRepositoryFileAsync(".github/workflows/nuget-stable-publish.yml");

        Assert.Contains("push:", workflow, StringComparison.Ordinal);
        Assert.Contains("tags:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow_dispatch", workflow, StringComparison.Ordinal);
        Assert.Contains("tag_pattern='^v(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$'", workflow, StringComparison.Ordinal);
        Assert.Contains("STABLE_BASE_REF: main", workflow, StringComparison.Ordinal);
        Assert.Contains("origin/${STABLE_BASE_REF}", workflow, StringComparison.Ordinal);
        Assert.Contains("--branch \"${STABLE_BASE_REF}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("nuget-stable", workflow, StringComparison.Ordinal);
        Assert.Contains("nuget-stable-smoke", workflow, StringComparison.Ordinal);
        Assert.Contains("prevent_self_review == true", workflow, StringComparison.Ordinal);
        Assert.Contains("wait_timer == 25", workflow, StringComparison.Ordinal);
        Assert.Contains("id-token: write", workflow, StringComparison.Ordinal);
        Assert.Contains("actions: read", workflow, StringComparison.Ordinal);
        Assert.Contains("Required so gh run list can verify source CI for the tag commit.", workflow, StringComparison.Ordinal);
        Assert.Contains("Required for NuGet trusted publishing to request an OIDC token.", workflow, StringComparison.Ordinal);
        Assert.Contains("persist-credentials: false", workflow, StringComparison.Ordinal);
        Assert.Contains("prove-docs-archive:", workflow, StringComparison.Ordinal);
        var proveDocsArchiveIndex = workflow.IndexOf("prove-docs-archive:", StringComparison.Ordinal);
        var publishNugetIndex = workflow.IndexOf("publish-nuget:", StringComparison.Ordinal);
        Assert.True(proveDocsArchiveIndex >= 0, "Stable docs proof job must be declared.");
        Assert.True(publishNugetIndex > proveDocsArchiveIndex, "Stable docs proof must be declared before the irreversible publish-nuget job.");
        var proveDocsArchiveJob = workflow[proveDocsArchiveIndex..publishNugetIndex];
        Assert.Contains("fetch-depth: 0", proveDocsArchiveJob, StringComparison.Ordinal);
        Assert.Contains("Export and verify stable docs archive before NuGet publish", workflow, StringComparison.Ordinal);
        Assert.Contains("docs export", workflow, StringComparison.Ordinal);
        Assert.Contains("docs verify-archive", workflow, StringComparison.Ordinal);
        Assert.Contains("appsurface-stable-docs-proof", workflow, StringComparison.Ordinal);
        Assert.Contains("--docs-catalog \"${docs_release_root}/versions.json\"", workflow, StringComparison.Ordinal);
        Assert.Contains("--docs-trusted-release-root \"${docs_release_root}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("\"\"|/*|../*|*/../*|*/..|\".\"|\"..\"|.*|*/.*|*//*)", proveDocsArchiveJob, StringComparison.Ordinal);
        Assert.DoesNotContain("*\"/..\"", proveDocsArchiveJob, StringComparison.Ordinal);
        Assert.Contains("- prove-docs-archive", workflow, StringComparison.Ordinal);
        Assert.Contains("NuGet/login", workflow, StringComparison.Ordinal);
        Assert.Contains("publish-stable", workflow, StringComparison.Ordinal);
        Assert.Contains("appsurface-stable-packages", workflow, StringComparison.Ordinal);
        Assert.Contains("appsurface-stable-publish-log", workflow, StringComparison.Ordinal);
        Assert.Contains("appsurface-stable-smoke", workflow, StringComparison.Ordinal);
        Assert.Contains("package-manager-cache: false", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("cache: true", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget-prerelease", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MainDocsDeployHydratesReleaseArchivesBeforePagesUpload()
    {
        var workflow = await ReadRepositoryFileAsync(".github/workflows/build.yml");

        var hydrateIndex = workflow.IndexOf("Hydrate release-pinned docs archives", StringComparison.Ordinal);
        var uploadIndex = workflow.IndexOf("Upload Pages artifact", StringComparison.Ordinal);
        Assert.True(hydrateIndex >= 0, "Main docs deploy must hydrate published release docs archives.");
        Assert.True(uploadIndex > hydrateIndex, "Release archive hydration must happen before Pages artifact upload.");
        Assert.Contains("include-hidden-files: true", workflow, StringComparison.Ordinal);
        Assert.Contains("gh api --paginate \"repos/${GITHUB_REPOSITORY}/releases\"", workflow, StringComparison.Ordinal);
        Assert.Contains("select(.draft | not)", workflow, StringComparison.Ordinal);
        Assert.Contains("mapfile -t release_rows", workflow, StringComparison.Ordinal);
        Assert.Contains("[.tag_name, .prerelease] | @tsv", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("select(.isPrerelease == false)", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--limit 100", workflow, StringComparison.Ordinal);
        Assert.Contains("gh release download \"${release_tag}\" --pattern \"${asset_name}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("tar -tzf \"${asset_dir}/${asset_name}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("unsafe absolute or parent-relative entry", workflow, StringComparison.Ordinal);
        Assert.Contains("tar -tzvf \"${asset_dir}/${asset_name}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("non-regular tar entry", workflow, StringComparison.Ordinal);
        Assert.Contains("tar -xzf \"${asset_dir}/${asset_name}\" -C \"${exact_tree}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("releaseManifestSha256", workflow, StringComparison.Ordinal);
        Assert.Contains("\"${PAGES_ROOT}/versions.json\"", workflow, StringComparison.Ordinal);
        Assert.Contains("semver_key", workflow, StringComparison.Ordinal);
        Assert.Contains("stable_version", workflow, StringComparison.Ordinal);
        Assert.Contains("supportState:\"Maintained\"", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("supportState:\"Supported\"", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("sort | last", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseWrapperRestoresDependenciesForFreshCheckouts()
    {
        var wrapper = await ReadRepositoryFileAsync("eng/release");

        Assert.Contains("dotnet run --project", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-restore", wrapper, StringComparison.Ordinal);
    }

    private static async Task<string> ReadRepositoryFileAsync(string relativePath)
    {
        var root = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        return await File.ReadAllTextAsync(TestPathUtils.PathUnder(root, relativePath));
    }

    private static string GetWorkflowJob(string workflow, string jobName, string? nextJobName = null)
    {
        var jobStart = workflow.IndexOf($"\n  {jobName}:", StringComparison.Ordinal);
        Assert.True(jobStart >= 0, $"Workflow does not contain the '{jobName}' job.");

        var jobEnd = nextJobName is null
            ? workflow.Length
            : workflow.IndexOf($"\n  {nextJobName}:", jobStart + 1, StringComparison.Ordinal);
        Assert.True(jobEnd > jobStart, $"Workflow does not contain a valid end for the '{jobName}' job.");

        return workflow[jobStart..jobEnd];
    }

    private static string GetFinalWorkflowStepRun(string workflowJob, string stepName)
    {
        var stepStart = workflowJob.IndexOf($"      - name: {stepName}\n", StringComparison.Ordinal);
        Assert.True(stepStart >= 0, $"Workflow job does not contain the '{stepName}' step.");

        const string runMarker = "        run: |\n";
        var runStart = workflowJob.IndexOf(runMarker, stepStart, StringComparison.Ordinal);
        Assert.True(runStart > stepStart, $"Workflow step '{stepName}' does not contain a shell script.");

        var indentedScript = workflowJob[(runStart + runMarker.Length)..].TrimEnd();
        return string.Join(
            "\n",
            indentedScript.Split('\n').Select(line => line.StartsWith("          ", StringComparison.Ordinal) ? line[10..] : line));
    }

    private static async Task<(int ExitCode, string Calls, string StandardError)> RunReleasePrepPushScriptAsync(
        string script,
        string scenario,
        string existingPullRequest)
    {
        var temporaryDirectory = Path.Join(Path.GetTempPath(), $"appsurface-release-prep-push-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var callLogPath = Path.Join(temporaryDirectory, "calls.log");

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "bash",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.StartInfo.ArgumentList.Add("-s");
            process.StartInfo.Environment["BASE_REF"] = "main";
            process.StartInfo.Environment["GH_TOKEN"] = "test-release-bot-token";
            process.StartInfo.Environment["HARNESS_CALL_LOG"] = callLogPath;
            process.StartInfo.Environment["HARNESS_EXISTING_PULL_REQUEST"] = existingPullRequest;
            process.StartInfo.Environment["HARNESS_SCENARIO"] = scenario;
            process.StartInfo.Environment["RELEASE_BRANCH"] = "release-bot/v0.2.0";
            process.StartInfo.Environment["RUNNER_TEMP"] = temporaryDirectory;
            process.StartInfo.Environment["VERSION"] = "0.2.0";
            Assert.True(process.Start());

            await process.StandardInput.WriteAsync(
                """
                git() {
                  command_name=""
                  remote_fetch=false
                  for argument in "$@"; do
                    case "${argument}" in
                      init|fetch|remote|ls-remote|push) command_name="${argument}" ;;
                      origin) remote_fetch=true ;;
                    esac
                  done
                  printf 'git:%s %s\n' "${command_name}" "$*" >> "${HARNESS_CALL_LOG}"
                  case "${command_name}" in
                    fetch)
                      if [[ "${remote_fetch}" == true ]]; then
                        case "${HARNESS_SCENARIO}" in
                          new|existing-fetch-failure|lookup-failure) return 128 ;;
                        esac
                      fi
                      return 0
                      ;;
                    ls-remote)
                      case "${HARNESS_SCENARIO}" in
                        new) return 2 ;;
                        existing-fetch-failure) return 0 ;;
                        lookup-failure) return 128 ;;
                      esac
                      return 99
                      ;;
                    init|remote) return 0 ;;
                    push)
                      if [[ "${HARNESS_SCENARIO}" == push-failure ]]; then
                        return 128
                      fi
                      return 0
                      ;;
                  esac
                  return 99
                }
                gh() {
                  printf 'gh:%s\n' "$*" >> "${HARNESS_CALL_LOG}"
                  if [[ "$1" == pr && "$2" == list ]]; then
                    if [[ "${HARNESS_SCENARIO}" == list-failure ]]; then
                      return 128
                    fi
                    printf '%s' "${HARNESS_EXISTING_PULL_REQUEST}"
                  elif [[ "$1" == pr && "$2" == edit && "${HARNESS_SCENARIO}" == edit-failure ]]; then
                    return 128
                  elif [[ "$1" == pr && "$2" == create && "${HARNESS_SCENARIO}" == create-failure ]]; then
                    return 128
                  fi
                }

                """);
            await process.StandardInput.WriteAsync(script);
            process.StandardInput.Close();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync());
            var calls = File.Exists(callLogPath) ? await File.ReadAllTextAsync(callLogPath) : string.Empty;
            return (process.ExitCode, calls, await errorTask);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}
