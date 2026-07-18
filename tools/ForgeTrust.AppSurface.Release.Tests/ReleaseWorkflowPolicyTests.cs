namespace ForgeTrust.AppSurface.Release.Tests;

public sealed class ReleaseWorkflowPolicyTests
{
    [Fact]
    public async Task ReleaseContractUsesTrustedBaseReadOnlyClassifier()
    {
        var workflow = await ReadRepositoryFileAsync(".github/workflows/release-contract.yml");
        var buildWorkflow = await ReadRepositoryFileAsync(".github/workflows/build.yml");

        var trigger = SliceBetween(workflow, "on:\n", "\npermissions:");
        Assert.Contains("  pull_request:\n", trigger, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target", trigger, StringComparison.Ordinal);

        var permissions = SliceBetween(workflow, "permissions:\n", "\njobs:");
        Assert.Equal("  contents: read\n  pull-requests: read\n", permissions);
        Assert.DoesNotContain("write", permissions, StringComparison.Ordinal);

        var enforceJob = workflow[workflow.IndexOf("  enforce:\n", StringComparison.Ordinal)..];
        Assert.Contains("    timeout-minutes: 5\n", enforceJob, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.", enforceJob, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target", enforceJob, StringComparison.Ordinal);
        Assert.DoesNotContain("permissions:", enforceJob, StringComparison.Ordinal);
        Assert.DoesNotContain("issues: write", enforceJob, StringComparison.Ordinal);
        Assert.DoesNotContain("pull-requests: write", enforceJob, StringComparison.Ordinal);

        var checkout = SliceBetween(
            enforceJob,
            "      - name: Checkout trusted base release policy\n",
            "\n      - name: Evaluate trusted release contract");
        Assert.Contains("          ref: ${{ github.event.pull_request.base.sha }}\n", checkout, StringComparison.Ordinal);
        Assert.Contains("          path: .release-contract-policy\n", checkout, StringComparison.Ordinal);
        Assert.Contains("          persist-credentials: false\n", checkout, StringComparison.Ordinal);
        Assert.Contains("          sparse-checkout: |\n            .github/scripts\n", checkout, StringComparison.Ordinal);
        Assert.Contains("          sparse-checkout-cone-mode: false\n", checkout, StringComparison.Ordinal);

        var evaluate = SliceBetween(
            enforceJob,
            "      - name: Evaluate trusted release contract\n",
            "\n      - name: Summarize trusted-policy checkout failure");
        Assert.Contains("const { pathToFileURL } = await import(\"node:url\");", evaluate, StringComparison.Ordinal);
        Assert.Contains(".release-contract-policy/.github/scripts/release-contract-v1.mjs", evaluate, StringComparison.Ordinal);
        Assert.Contains("policy.contractVersion !== 1", evaluate, StringComparison.Ordinal);
        Assert.Contains("github.rest.pulls.listFiles", evaluate, StringComparison.Ordinal);
        Assert.Contains("policy.evaluateReleaseContractV1(input)", evaluate, StringComparison.Ordinal);
        Assert.Contains("policy.renderReleaseContractSummaryV1(result", evaluate, StringComparison.Ordinal);
        Assert.Contains("infrastructure-api-failure", evaluate, StringComparison.Ordinal);
        Assert.Contains("infrastructure-import-failure", evaluate, StringComparison.Ordinal);
        Assert.Contains("infrastructure-runtime-failure", evaluate, StringComparison.Ordinal);
        Assert.DoesNotContain("github.rest.issues", evaluate, StringComparison.Ordinal);
        Assert.DoesNotContain("addLabels", evaluate, StringComparison.Ordinal);
        Assert.DoesNotContain("createComment", evaluate, StringComparison.Ordinal);
        var infrastructureSummaryGolden = SliceBetween(
            evaluate,
            "              const summary = [\n",
            "              await core.summary.addRaw(summary).write();");
        Assert.Equal(
            "01F01D38A82BB2CCA9680DF73B1178BD6C891BCBAFEBC9ECF41AE51B073722E0",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(infrastructureSummaryGolden))));

        var fallback = enforceJob[enforceJob.IndexOf("      - name: Summarize trusted-policy checkout failure\n", StringComparison.Ordinal)..];
        Assert.Contains("if: ${{ failure() && steps.policy-checkout.outcome == 'failure' }}", fallback, StringComparison.Ordinal);
        Assert.Contains("infrastructure-checkout-failure", fallback, StringComparison.Ordinal);
        Assert.Contains("## Decision", fallback, StringComparison.Ordinal);
        Assert.Contains("## Why", fallback, StringComparison.Ordinal);
        Assert.Contains("## What to do next", fallback, StringComparison.Ordinal);
        var checkoutSummaryGolden = SliceBetween(
            fallback,
            "            await core.summary.addRaw([\n",
            "            ].join(\"\\n\")).write();");
        Assert.Equal(
            "5E0291D42478E031BE74686B31BCD7D9441FAA3E7F2DF94BB7EF0EAA2FD321F4",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(checkoutSummaryGolden))));

        var buildNodeSlice = SliceBetween(
            buildWorkflow,
            "      - name: Setup Node.js\n",
            "\n      - name: Verify generated package chooser");
        Assert.Contains("          node-version: 24\n", buildNodeSlice, StringComparison.Ordinal);
        Assert.Contains("      - name: Test dormant release-contract classifier\n", buildNodeSlice, StringComparison.Ordinal);
        Assert.Contains("node --experimental-test-coverage --test-coverage-lines=100 --test-coverage-functions=100 --test-coverage-branches=100 --test .github/scripts/release-contract-v1.test.mjs", buildNodeSlice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleasePrepReviewUsesReadOnlyPullRequestTriggerWithoutSecrets()
    {
        var workflow = await ReadRepositoryFileAsync(".github/workflows/release-prep.yml");

        Assert.Contains("pull_request:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target", workflow, StringComparison.Ordinal);
        Assert.Contains("release-prep-review:", workflow, StringComparison.Ordinal);
        Assert.Contains("contents: read", workflow, StringComparison.Ordinal);
        Assert.Contains("pull-requests: read", workflow, StringComparison.Ordinal);
        Assert.Contains("--fail-on-warnings", workflow, StringComparison.Ordinal);
        Assert.Contains("--allow-existing-targets", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseWorkflowsDeclareConcurrencyAndAvoidEval()
    {
        var prep = await ReadRepositoryFileAsync(".github/workflows/release-prep.yml");
        var publish = await ReadRepositoryFileAsync(".github/workflows/release-publish.yml");
        var stablePublish = await ReadRepositoryFileAsync(".github/workflows/nuget-stable-publish.yml");

        Assert.Contains("concurrency:", prep, StringComparison.Ordinal);
        Assert.Contains("concurrency:", publish, StringComparison.Ordinal);
        Assert.Contains("concurrency:", stablePublish, StringComparison.Ordinal);
        Assert.Contains("RELEASE_BOT_TOKEN", prep, StringComparison.Ordinal);
        Assert.DoesNotContain("eval ", prep, StringComparison.Ordinal);
        Assert.DoesNotContain("eval ", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("eval ", stablePublish, StringComparison.Ordinal);
        Assert.Contains("actions: read", publish, StringComparison.Ordinal);
        Assert.Contains("BASE_REF: ${{ inputs.base-ref }}", prep, StringComparison.Ordinal);
        Assert.Contains("expected_base=\"$(git rev-parse \"origin/${BASE_REF}\")\"", prep, StringComparison.Ordinal);
        Assert.Contains("--base \"${BASE_REF}\"", prep, StringComparison.Ordinal);
        Assert.DoesNotContain("expected_main=\"$(git rev-parse origin/main)\"", prep, StringComparison.Ordinal);
        Assert.DoesNotContain("merge-base --is-ancestor HEAD origin/main", prep, StringComparison.Ordinal);
        Assert.Contains("No versioned release artifacts changed; validating release tooling instead.", prep, StringComparison.Ordinal);
        Assert.Contains("dotnet test tools/ForgeTrust.AppSurface.Release.Tests/ForgeTrust.AppSurface.Release.Tests.csproj", prep, StringComparison.Ordinal);
        Assert.Contains("git diff --no-renames --name-status", prep, StringComparison.Ordinal);
        Assert.Contains("release_artifact_deletions", prep, StringComparison.Ordinal);
        Assert.Contains("git diff --no-renames --name-only --diff-filter=AM", prep, StringComparison.Ordinal);
        Assert.Contains("releases/v*.release.json", prep, StringComparison.Ordinal);
        Assert.Contains("releases/v*.evidence.json", prep, StringComparison.Ordinal);
        Assert.Contains("Expected exactly one added or modified release manifest", prep, StringComparison.Ordinal);
        Assert.Contains("Expected exactly one added or modified release evidence bundle", prep, StringComparison.Ordinal);
        Assert.Contains("Unexpected versioned release artifact deletion", prep, StringComparison.Ordinal);
        Assert.Contains("releases/v0.1.0-rc.4.release.json", prep, StringComparison.Ordinal);
        Assert.Contains("Release preparation pull requests must change exactly the four generated artifacts", prep, StringComparison.Ordinal);
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
        Assert.Contains("ref: ${{ needs.validate-release.outputs.tag_commit }}", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("ref: ${{ needs.validate-release.outputs.tag }}", publish, StringComparison.Ordinal);
        Assert.Contains("TAG_COMMIT: ${{ needs.validate-release.outputs.tag_commit }}", publish, StringComparison.Ordinal);
        Assert.Contains("actual_tag_commit=\"$(git rev-parse \"refs/tags/${TAG}^{commit}\")\"", publish, StringComparison.Ordinal);
        Assert.Contains("Expected ${TAG} to resolve to ${TAG_COMMIT}; got ${actual_tag_commit}.", publish, StringComparison.Ordinal);
        Assert.Contains("git show \"${TAG_COMMIT}:releases/v${VERSION}.md\"", publish, StringComparison.Ordinal);
        Assert.Contains("Export current docs root and exact docs tree", publish, StringComparison.Ordinal);
        Assert.Contains("AppSurfaceDocs__Contributor__DefaultBranch: ${{ inputs.base-ref }}", publish, StringComparison.Ordinal);
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
        Assert.Contains("--base-ref \"${BASE_REF}\"", publish, StringComparison.Ordinal);
        Assert.Contains("git fetch origin \"${BASE_REF}:refs/remotes/origin/${BASE_REF}\"", publish, StringComparison.Ordinal);
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

    private static string SliceBetween(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Expected bounded slice start: {start}");
        startIndex += start.Length;
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex >= startIndex, $"Expected bounded slice end: {end}");
        return source[startIndex..endIndex];
    }
}
