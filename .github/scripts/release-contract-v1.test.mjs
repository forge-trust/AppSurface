import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { test } from "node:test";

import {
  contractVersion,
  evaluateReleaseContractV1,
  renderReleaseContractSummaryV1
} from "./release-contract-v1.mjs";

const oldPin = "1111111111111111111111111111111111111111";
const newPin = "2222222222222222222222222222222222222222";
const MaximumPullRequestFiles = 3000;
const MaximumPatchBytesPerFile = 128 * 1024;
const MaximumAggregatePatchBytes = 1024 * 1024;
const MaximumPatchLineBytes = 8 * 1024;
const pullRequest622Patch = [
  "@@ -300,7 +300,7 @@ jobs:",
  "           gh release upload \"${TAG}\" \"${ARCHIVE_PATH}\" \"${SHA256_PATH}\" --clobber",
  " ",
  "       - name: Upload docs publication artifacts",
  `-        uses: actions/upload-artifact@${oldPin} # v4.6.2`,
  `+        uses: actions/upload-artifact@${newPin} # v7.0.1`,
  "         with:",
  "           name: docs-publication-${{ needs.validate-release.outputs.version }}",
  "           path: |"
].join("\n");

function patch(oldReference = `actions/checkout@${oldPin}`, newReference = `actions/checkout@${newPin}`, options = {}) {
  const indent = options.indent ?? "      - ";
  const suffixOld = options.suffixOld ?? "";
  const suffixNew = options.suffixNew ?? "";
  const context = options.context ?? ["jobs:", "  build:", "    steps:"];
  const oldLines = [...context, `${indent}uses: ${oldReference}${suffixOld}`];
  const newLines = [...context, `${indent}uses: ${newReference}${suffixNew}`];
  const body = [
    ...context.map(line => ` ${line}`),
    `-${indent}uses: ${oldReference}${suffixOld}`,
    `+${indent}uses: ${newReference}${suffixNew}`
  ];
  return `@@ -1,${oldLines.length} +1,${newLines.length} @@\n${body.join("\n")}`;
}

function file(overrides = {}) {
  return {
    filename: ".github/workflows/build.yml",
    status: "modified",
    additions: 1,
    deletions: 1,
    changes: 2,
    patch: patch(),
    ...overrides
  };
}

function input(overrides = {}) {
  const files = overrides.files ?? [file()];
  return {
    title: "build(deps): bump actions/checkout",
    authorLogin: "dependabot[bot]",
    labels: ["dependencies", "github_actions"],
    changedFiles: files.length,
    files,
    ...overrides
  };
}

function codeFor(overrides) {
  return evaluateReleaseContractV1(input(overrides)).automaticDiagnostic?.code ?? null;
}

function summaryDigest(result, context) {
  return createHash("sha256").update(renderReleaseContractSummaryV1(result, context)).digest("hex");
}

test("exports only the documented v1 contract version", () => {
  assert.equal(contractVersion, 1);
});

test("accepts the documented 3,000-file boundary and an exact per-patch byte limit", () => {
  const files = Array.from({ length: MaximumPullRequestFiles }, (_, index) => file({
    filename: `.github/workflows/generated/${String(index).padStart(4, "0")}.yml`
  }));
  assert.equal(evaluateReleaseContractV1(input({ files, changedFiles: files.length })).automaticExemption, true);

  const hunkCount = 20;
  const makeSizedPatch = fillers => Array.from({ length: hunkCount }, (_, index) => [
    `@@ -${index * 4 + 1},4 +${index * 4 + 1},4 @@`,
    " jobs:",
    "   generated:",
    "     steps:",
    `-      - uses: owner/action@${oldPin} # ${"x".repeat(fillers[index * 2])}`,
    `+      - uses: owner/action@${newPin} # ${"x".repeat(fillers[index * 2 + 1])}`
  ].join("\n")).join("\n");
  const fillers = Array(hunkCount * 2).fill(0);
  const remaining = MaximumPatchBytesPerFile - Buffer.byteLength(makeSizedPatch(fillers));
  for (let index = 0; index < remaining; index += 1) {
    fillers[index % fillers.length] += 1;
  }
  const maximumPatch = makeSizedPatch(fillers);
  assert.equal(Buffer.byteLength(maximumPatch), MaximumPatchBytesPerFile);
  assert.equal(codeFor({
    files: [file({ additions: hunkCount, deletions: hunkCount, changes: hunkCount * 2, patch: maximumPatch })]
  }), null);

  const aggregateMaximum = Array.from({ length: MaximumAggregatePatchBytes / MaximumPatchBytesPerFile }, (_, index) => file({
    filename: `.github/workflows/aggregate-${index}.yml`,
    additions: hunkCount,
    deletions: hunkCount,
    changes: hunkCount * 2,
    patch: maximumPatch
  }));
  assert.equal(evaluateReleaseContractV1(input({ files: aggregateMaximum })).automaticExemption, true);
});

test("classifies the PR 622 shape and preserves independent title validation", () => {
  const result = evaluateReleaseContractV1(input({ files: [file({ patch: pullRequest622Patch })] }));
  assert.deepEqual(
    {
      title: result.conventionalTitleValid,
      note: result.hasUnreleasedEntry,
      label: result.hasMaintainerExemptionLabel,
      automatic: result.automaticExemption,
      classification: result.automaticClassification,
      diagnostic: result.automaticDiagnostic,
      evidence: result.releaseEvidenceKind,
      diagnostics: result.diagnostics
    },
    {
      title: true,
      note: false,
      label: false,
      automatic: true,
      classification: "dependabot-github-actions-reference-only",
      diagnostic: null,
      evidence: "automatic-dependabot",
      diagnostics: []
    });

  const invalid = evaluateReleaseContractV1(input({ title: "Bump checkout" }));
  assert.equal(invalid.automaticExemption, true);
  assert.equal(invalid.releaseEvidenceKind, "automatic-dependabot");
  assert.deepEqual(invalid.diagnostics.map(item => [item.code, item.blocking]), [["conventional-title-invalid", true]]);
});

test("supports grouped files, multiple hunks, YAML extensions, list syntax, and comment-only pin changes", () => {
  const firstPatch = [
    "--- a/.github/workflows/a.yml",
    "+++ b/.github/workflows/a.yml",
    `@@ -1,2 +1,2 @@ task`,
    "  steps:",
    `-    - uses: owner/action/sub@${oldPin} # v1`,
    `+    - uses: owner/action/sub@${newPin} # v2`,
    "@@ -20,4 +20,4 @@",
    " jobs:",
    "   other:",
    "     steps:",
    `-      - uses: owner/other@${oldPin} # old release`,
    `+      - uses: owner/other@${newPin} # new release`
  ].join("\n");
  const files = [
    file({ filename: ".github/workflows/nested/b.yaml" }),
    file({ filename: ".github/workflows/a.yml", additions: 2, deletions: 2, changes: 4, patch: firstPatch })
  ];
  const result = evaluateReleaseContractV1(input({ files, changedFiles: 2 }));
  assert.equal(result.automaticExemption, true);
});

test("authored entry and exact maintainer label take precedence over automatic classification", () => {
  const releaseFile = file({
    filename: "releases/unreleased.md",
    status: "modified",
    patch: "not relevant because earlier evidence is independently detected"
  });
  const authored = evaluateReleaseContractV1(input({ authorLogin: "human", files: [releaseFile], changedFiles: 1 }));
  assert.equal(authored.releaseEvidenceKind, "unreleased-entry");
  assert.equal(authored.hasUnreleasedEntry, true);
  assert.equal(authored.automaticDiagnostic.code, "identity-not-dependabot");
  assert.equal(authored.diagnostics.some(item => item.blocking), false);

  for (const status of ["removed", "renamed", undefined, null, 1]) {
    const releaseWithoutAuthoredEntry = file({ filename: "releases/unreleased.md", status });
    const rejected = evaluateReleaseContractV1(input({ authorLogin: "human", files: [releaseWithoutAuthoredEntry], changedFiles: 1 }));
    assert.equal(rejected.hasUnreleasedEntry, false, String(status));
    assert.equal(rejected.releaseEvidenceKind, "none", String(status));
    assert.equal(rejected.diagnostics.some(item => item.blocking), true, String(status));
  }

  const added = evaluateReleaseContractV1(input({ authorLogin: "human", files: [file({ filename: "releases/unreleased.md", status: "added" })], changedFiles: 1 }));
  assert.equal(added.hasUnreleasedEntry, true);
  assert.equal(added.releaseEvidenceKind, "unreleased-entry");

  const labeled = evaluateReleaseContractV1(input({
    authorLogin: "human",
    labels: ["dependencies", "github_actions", "no-unreleased-entry"]
  }));
  assert.equal(labeled.releaseEvidenceKind, "maintainer-label");
  assert.equal(labeled.hasMaintainerExemptionLabel, true);
  assert.equal(labeled.diagnostics.some(item => item.blocking), false);

  const wrongCase = evaluateReleaseContractV1(input({
    authorLogin: "human",
    labels: ["dependencies", "github_actions", "No-Unreleased-Entry"]
  }));
  assert.equal(wrongCase.releaseEvidenceKind, "none");
  assert.deepEqual(wrongCase.diagnostics.map(item => item.code), ["identity-not-dependabot", "release-evidence-missing"]);
});

test("validation precedence is identity, labels, aggregate schema, sorted files, patch, then changed lines", () => {
  assert.equal(codeFor({ authorLogin: "dependabot", labels: null, changedFiles: 0, files: null }), "identity-not-dependabot");
  assert.equal(codeFor({ labels: null, changedFiles: 0, files: null }), "labels-not-array");
  assert.equal(codeFor({ labels: ["dependencies", 2], changedFiles: 0, files: null }), "labels-invalid-item");
  assert.equal(codeFor({ labels: ["dependencies", "dependencies"], changedFiles: 0, files: null }), "labels-duplicate-item");
  assert.equal(codeFor({ labels: ["github_actions"], changedFiles: 0, files: null }), "label-dependencies-missing");
  assert.equal(codeFor({ labels: ["dependencies"], changedFiles: 0, files: null }), "label-github-actions-missing");
  assert.equal(codeFor({ changedFiles: 0, files: null }), "changed-files-invalid");
  assert.equal(codeFor({ changedFiles: MaximumPullRequestFiles + 1, files: [] }), "changed-files-over-limit");
  assert.equal(codeFor({ changedFiles: 1, files: null }), "files-not-array");
  assert.equal(codeFor({ changedFiles: 2, files: [file()] }), "changed-files-count-mismatch");
  assert.equal(codeFor({ changedFiles: 1, files: [null] }), "file-entry-invalid");
  assert.equal(codeFor({ changedFiles: 1, files: [{ filename: "" }] }), "file-entry-invalid");
  assert.equal(codeFor({ changedFiles: 2, files: [file(), file()] }), "file-duplicate");

  const lateBad = file({ filename: ".github/workflows/z.yml", status: "added", patch: "bad" });
  const earlyBad = file({ filename: ".github/workflows/a.yml", status: "removed", patch: "bad" });
  assert.equal(codeFor({ changedFiles: 2, files: [lateBad, earlyBad] }), "file-status-ineligible");
  assert.equal(evaluateReleaseContractV1(input({ changedFiles: 2, files: [lateBad, earlyBad] })).automaticDiagnostic.location.file, earlyBad.filename);
});

test("rejects ineligible status, paths, counts, missing patches, and exact size boundaries", () => {
  assert.equal(codeFor({ files: [file({ status: "renamed" })] }), "file-status-ineligible");
  assert.equal(codeFor({ files: [file({ filename: ".github/workflows.yml" })] }), "file-path-ineligible");
  assert.equal(codeFor({ files: [file({ filename: ".github/workflows/build.json" })] }), "file-path-ineligible");
  assert.equal(codeFor({ files: [file({ additions: -1 })] }), "file-counts-invalid");
  assert.equal(codeFor({ files: [file({ additions: 1.5 })] }), "file-counts-invalid");
  assert.equal(codeFor({ files: [file({ changes: 99 })] }), "file-counts-invalid");
  assert.equal(codeFor({ files: [file({ patch: null })] }), "patch-missing");
  assert.equal(codeFor({ files: [file({ patch: "" })] }), "patch-missing");

  const tooLarge = `@@ -1 +1 @@\n-${"a".repeat(MaximumPatchBytesPerFile)}\n+${"a".repeat(MaximumPatchBytesPerFile)}`;
  assert.equal(codeFor({ files: [file({ patch: tooLarge })] }), "patch-file-size-limit");

  const largePatch = Array.from({ length: 700 }, (_, index) => [
    `@@ -${index + 1} +${index + 1} @@`,
    `-uses: owner/action@${oldPin} # ${"a".repeat(20)}`,
    `+uses: owner/action@${newPin} # ${"a".repeat(20)}`
  ].join("\n")).join("\n");
  assert.ok(Buffer.byteLength(largePatch) < MaximumPatchBytesPerFile);
  const files = Array.from({ length: 10 }, (_, index) => file({
    filename: `.github/workflows/${index}.yml`,
    additions: 700,
    deletions: 700,
    changes: 1400,
    patch: largePatch
  }));
  assert.equal(codeFor({ files, changedFiles: files.length }), "patch-aggregate-size-limit");
});

test("rejects malformed unified diffs with stable structural leaf codes", () => {
  const cases = [
    ["patch-carriage-return", `${patch()}\r`],
    ["patch-line-limit", `@@ -1 +1 @@\n-${"x".repeat(MaximumPatchLineBytes + 1)}\n+x`],
    ["patch-file-header-invalid", "--- a/x\n@@ -1 +1 @@\n-a\n+b"],
    ["patch-file-header-invalid", "+++ b/x\n@@ -1 +1 @@\n-a\n+b"],
    ["patch-hunk-header-invalid", "diff --git a/x b/x\n@@ -1 +1 @@\n-a\n+b"],
    ["patch-hunk-header-invalid", "@@@ -1 +1 @@@\n-a\n+b"],
    ["patch-hunk-range-invalid", "@@ -9007199254740992 +9007199254740992 @@\n-a\n+b"],
    ["patch-hunk-range-invalid", "@@ -1,9007199254740992 +1,9007199254740992 @@\n-a\n+b"],
    ["patch-hunk-range-invalid", "@@ -0,1 +0,1 @@\n-a\n+b"],
    ["patch-position-changed", "@@ -1 +2 @@\n-a\n+b"],
    ["patch-hunk-count-mismatch", "@@ -1,2 +1,2 @@\n-a\n+b"],
    ["patch-newline-marker", "@@ -1,2 +1,2 @@\n-a\n+b\n\\ No newline at end of file"],
    ["patch-line-prefix-invalid", "@@ -1 +1 @@\n?a\n+b"],
    ["patch-hunk-unpaired", "@@ -1 +1 @@\n a"],
    ["patch-hunk-missing", "--- a/.github/workflows/build.yml\n+++ b/.github/workflows/build.yml"]
  ];
  for (const [expected, malformed] of cases) {
    assert.equal(codeFor({ files: [file({ patch: malformed })] }), expected, expected);
  }
});

test("rejects every unsupported uses shape and semantic substitution", () => {
  const eligible = [
    [`owner/action@${oldPin}`, `owner/action@${newPin}`],
    [`owner/action/sub@${oldPin}`, `owner/action/sub@${newPin}`]
  ];
  for (const [oldReference, newReference] of eligible) {
    assert.equal(codeFor({ files: [file({ patch: patch(oldReference, newReference) })] }), null);
  }
  assert.equal(codeFor({ files: [file({ patch: patch(`owner/./action@${oldPin}`, `owner/action@${newPin}`) })] }), "uses-removal-ineligible");

  const rejected = [
    ["tag", "owner/action@v4"],
    ["uppercase SHA", `owner/action@${"A".repeat(40)}`],
    ["short SHA", `owner/action@${oldPin.slice(1)}`],
    ["non-comment suffix", `owner/action@${oldPin}#suffix`],
    ["quoted", `\"owner/action@${oldPin}\"`],
    ["local", `./action@${oldPin}`],
    ["Docker", `docker://alpine@${oldPin}`],
    ["reusable workflow", `owner/repo/.github/workflows/build.yml@${oldPin}`]
  ];
  for (const [name, reference] of rejected) {
    assert.equal(codeFor({ files: [file({ patch: patch(reference, `owner/action@${newPin}`) })] }), "uses-removal-ineligible", name);
    assert.equal(codeFor({ files: [file({ patch: patch(`owner/action@${oldPin}`, reference) })] }), "uses-addition-ineligible", name);
  }

  assert.equal(codeFor({ files: [file({ patch: patch(`owner/action@${oldPin}`, `other/action@${newPin}`) })] }), "patch-normalized-content-changed");
  const moved = `@@ -1,4 +1,4 @@\n jobs:\n   build:\n     steps:\n-      - uses: owner/action@${oldPin}\n+        - uses: owner/action@${newPin}`;
  assert.equal(codeFor({ files: [file({ patch: moved })] }), "patch-normalized-content-changed");
  assert.equal(codeFor({ files: [file({ patch: patch(`owner/action@${oldPin}`, `owner/action@${newPin}`, { suffixNew: " # changed" }) })] }), null);

  const scalar = `@@ -1,2 +1,2 @@\n run: |\n-  uses: owner/action@${oldPin}\n+  uses: owner/action@${newPin}`;
  assert.equal(codeFor({ files: [file({ patch: scalar })] }), "uses-removal-ineligible");
  const scalarWithBlank = `@@ -1,3 +1,3 @@\n run: >-\n \n-  uses: owner/action@${oldPin}\n+  uses: owner/action@${newPin}`;
  assert.equal(codeFor({ files: [file({ patch: scalarWithBlank })] }), "uses-removal-ineligible");
  const nestedScalar = `@@ -1,3 +1,3 @@\n run: |\n   steps:\n-    - uses: owner/action@${oldPin}\n+    - uses: owner/action@${newPin}`;
  assert.equal(codeFor({ files: [file({ patch: nestedScalar })] }), "uses-removal-ineligible");
  for (const quote of ["\"", "'"]) {
    const quotedScalar = `@@ -1,3 +1,3 @@ jobs:\n run: ${quote}text\n   steps:\n-    - uses: owner/action@${oldPin}\n+    - uses: owner/action@${newPin}`;
    assert.equal(codeFor({ files: [file({ patch: quotedScalar })] }), "uses-removal-ineligible", quote);
  }

  for (const mapping of ["with", "env", "permissions"]) {
    const lookalike = `@@ -1,4 +1,4 @@\n jobs:\n   build:\n     ${mapping}:\n-      uses: owner/action@${oldPin}\n+      uses: owner/action@${newPin}`;
    assert.equal(codeFor({ files: [file({ patch: lookalike })] }), "uses-removal-ineligible", mapping);
  }

  const step = `@@ -1,4 +1,4 @@\n jobs:\n   build:\n     steps:\n-      - uses: owner/action@${oldPin}\n+      - uses: owner/action@${newPin}`;
  assert.equal(codeFor({ files: [file({ patch: step })] }), null);

  const namedStep = `@@ -1,5 +1,5 @@\n jobs:\n   build:\n     steps:\n       - name: Build\n-        uses: owner/action@${oldPin}\n+        uses: owner/action@${newPin}`;
  assert.equal(codeFor({ files: [file({ patch: namedStep })] }), null);

  const noParent = `@@ -1 +1 @@\n-uses: owner/action@${oldPin}\n+uses: owner/action@${newPin}`;
  assert.equal(codeFor({ files: [file({ patch: noParent })] }), "uses-removal-ineligible");
  const noGrandparent = `@@ -1,2 +1,2 @@\n build:\n-  uses: owner/action@${oldPin}\n+  uses: owner/action@${newPin}`;
  assert.equal(codeFor({ files: [file({ patch: noGrandparent })] }), "uses-removal-ineligible");
  const jobLevel = `@@ -1,3 +1,3 @@\n jobs:\n   build:\n-    uses: owner/action@${oldPin}\n+    uses: owner/action@${newPin}`;
  assert.equal(codeFor({ files: [file({ patch: jobLevel })] }), "uses-removal-ineligible");
});

test("rejects movement, reordering, unequal pairs, mixed changes, and API count mismatch", () => {
  const reordered = [
    "@@ -1,5 +1,5 @@",
    " jobs:",
    "   build:",
    "     steps:",
    `-      - uses: one/action@${oldPin}`,
    `-      - uses: two/action@${oldPin}`,
    `+      - uses: two/action@${newPin}`,
    `+      - uses: one/action@${newPin}`
  ].join("\n");
  assert.equal(codeFor({ files: [file({ additions: 2, deletions: 2, changes: 4, patch: reordered })] }), "patch-normalized-content-changed");
  assert.equal(codeFor({ files: [file({ patch: patch(), additions: 2, changes: 3 })] }), "patch-api-count-mismatch");
  assert.equal(codeFor({ files: [file({ patch: "@@ -1,4 +1,4 @@\n jobs:\n   build:\n     steps:\n-      - uses: x/action@1111111111111111111111111111111111111111\n+      - permissions: write" })] }), "uses-addition-ineligible");
});

test("is total for JSON-shaped null, missing, sparse, duplicate, and wrong-typed values", () => {
  for (const value of [null, undefined, true, 12, "input", [], {}]) {
    const result = evaluateReleaseContractV1(value);
    assert.equal(result.conventionalTitleValid, false);
    assert.equal(result.releaseEvidenceKind, "none");
    assert.ok(result.diagnostics.some(item => item.blocking));
  }

  const sparseLabels = [];
  sparseLabels.length = 1;
  assert.equal(evaluateReleaseContractV1(input({ labels: sparseLabels })).automaticDiagnostic.code, "labels-invalid-item");
  const sparseFiles = [];
  sparseFiles.length = 1;
  assert.equal(evaluateReleaseContractV1(input({ files: sparseFiles, changedFiles: 1 })).automaticDiagnostic.code, "file-entry-invalid");
  assert.equal(codeFor({ changedFiles: Number.MAX_SAFE_INTEGER + 1 }), "changed-files-invalid");
});

test("title grammar matches the live conventional-title policy", () => {
  for (const title of ["feat: x", "fix(ui): x", "chore!: x", "ci(scope)!: x", "revert: x"]) {
    assert.equal(evaluateReleaseContractV1(input({ title })).conventionalTitleValid, true, title);
  }
  for (const title of ["", "feature: x", "feat x", " feat: x ", "feat(): x", "feat:"]) {
    assert.equal(evaluateReleaseContractV1(input({ title })).conventionalTitleValid, title === " feat: x ", title);
  }
});

test("renders exact golden summaries for automatic, authored, label, title failure, and rejection", () => {
  const context = { owner: "forge-trust", repo: "AppSurface", baseSha: "abc123" };
  const automatic = renderReleaseContractSummaryV1(evaluateReleaseContractV1(input()), context);
  assert.match(automatic, /^## Decision\n\n\*\*PASS\*\*\n\n## Why\n/);
  assert.match(automatic, /Release-note exemption only; all ordinary checks and review remain required\./);
  assert.match(automatic, /blob\/abc123\/\.github\/release-ops\.md#release-contract-check/);
  assert.doesNotMatch(automatic, /1111111111111111111111111111111111111111/);

  const authored = evaluateReleaseContractV1(input({ authorLogin: "human", files: [file({ filename: "releases/unreleased.md" })], changedFiles: 1 }));
  assert.match(renderReleaseContractSummaryV1(authored, context), /An authored releases\/unreleased\.md entry is present\./);
  const labeled = evaluateReleaseContractV1(input({ authorLogin: "human", labels: ["no-unreleased-entry", "dependencies", "github_actions"] }));
  assert.match(renderReleaseContractSummaryV1(labeled, context), /A maintainer applied the no-unreleased-entry exemption label\./);
  assert.match(renderReleaseContractSummaryV1(evaluateReleaseContractV1(input({ title: "bad" })), context), /## Decision\n\n\*\*FAIL\*\*/);
  assert.match(renderReleaseContractSummaryV1(evaluateReleaseContractV1(input({ authorLogin: "human" })), context), /identity-not-dependabot/);

  assert.deepEqual([
    summaryDigest(evaluateReleaseContractV1(input()), context),
    summaryDigest(authored, context),
    summaryDigest(labeled, context),
    summaryDigest(evaluateReleaseContractV1(input({ title: "bad" })), context),
    summaryDigest(evaluateReleaseContractV1(input({ authorLogin: "human" })), context)
  ], [
    "b97350a5e5b1baae072db45ebface5cb72740e37abaa53510c64d790c3eeb1f6",
    "448fca479a020008da8e8659219ccf90bc54877e9f2ca6b79acb2c25a6abd718",
    "52a0e538c4a817ae23794d398b97cbf5d402a36626d43e49f76553ca6a4bd35a",
    "0191d39af2f715bd52f45431c9b7e00d3726a00e426206773598571224ffbae2",
    "b750cc348c5c73240a443e438f2f8302f095a10908a00a3b037f8c7bf94e5bf5"
  ]);
});

test("summary bounds and escapes all raw evidence and supports explicit docs URLs", () => {
  const hostile = `<ScRiPt>|\`x\`\n${"z".repeat(500)}`;
  const result = evaluateReleaseContractV1(input({
    authorLogin: hostile,
    labels: ["dependencies", "github_actions", hostile]
  }));
  const summary = renderReleaseContractSummaryV1(result, { docsUrl: "https://example.test/docs?a=1&b=2" });
  assert.equal(summary.includes("<ScRiPt>"), false);
  assert.match(summary, /&lt;ScRiPt&gt;&#124;&#96;x&#96;/);
  assert.match(summary, /https:\/\/example\.test\/docs\?a=1&amp;b=2/);
  assert.ok(summary.length < 10_000);

  const emptyResult = evaluateReleaseContractV1(null);
  const defaults = renderReleaseContractSummaryV1(emptyResult);
  assert.match(defaults, /github\.com\/OWNER\/REPOSITORY\/blob\/main/);
  assert.match(defaults, /\| Author \| unknown \|/);
  assert.match(defaults, /\| Labels \| none \|/);
  assert.match(defaults, /\| Expected files \| unknown \|/);
  assert.match(defaults, /\| Returned files \| unknown \|/);

  const located = renderReleaseContractSummaryV1(
    evaluateReleaseContractV1(input({ files: [file({ patch: `${patch()}\n` })] })),
    { owner: "o", repo: "r", baseSha: "s" });
  assert.match(located, /No diagnostics\./);

  const malformed = renderReleaseContractSummaryV1(
    evaluateReleaseContractV1(input({ files: [file({ patch: "@@ -1 +1 @@\n-x\n+y" })] })),
    { owner: "o", repo: "r", baseSha: "s" });
  assert.match(malformed, /\.github\/workflows\/build\.yml, hunk 1/);

  const missingPatch = renderReleaseContractSummaryV1(
    evaluateReleaseContractV1(input({ files: [file({ patch: null })] })),
    { owner: "o", repo: "r", baseSha: "s" });
  assert.match(missingPatch, /\.github\/workflows\/build\.yml \|/);

  const nullContract = { ...emptyResult, contractVersion: null };
  assert.match(renderReleaseContractSummaryV1(nullContract), /\| Contract version \|  \|/);
});

test("diagnostic CLI supports stdin, wrapper files, renderer parity, and stable exit statuses", () => {
  const cli = fileURLToPath(new URL("./release-contract-diagnose.mjs", import.meta.url));
  const validInput = input();
  const stdinRun = spawnSync(process.execPath, [cli], { input: JSON.stringify(validInput), encoding: "utf8" });
  assert.equal(stdinRun.status, 0);
  assert.equal(stdinRun.stdout, renderReleaseContractSummaryV1(evaluateReleaseContractV1(validInput)));
  assert.equal(stdinRun.stderr, "");

  const directory = mkdtempSync(join(tmpdir(), "release-contract-v1-"));
  const inputPath = join(directory, "input.json");
  const context = { owner: "forge-trust", repo: "AppSurface", baseSha: "base" };
  writeFileSync(inputPath, JSON.stringify({ input: validInput, context }));
  const fileRun = spawnSync(process.execPath, [cli, inputPath], { encoding: "utf8" });
  assert.equal(fileRun.status, 0);
  assert.equal(fileRun.stdout, renderReleaseContractSummaryV1(evaluateReleaseContractV1(validInput), context));

  const blockingRun = spawnSync(process.execPath, [cli], { input: "{}", encoding: "utf8" });
  assert.equal(blockingRun.status, 1);
  assert.match(blockingRun.stdout, /\*\*FAIL\*\*/);

  const invalidRun = spawnSync(process.execPath, [cli], { input: "not-json", encoding: "utf8" });
  assert.equal(invalidRun.status, 2);
  assert.match(invalidRun.stderr, /^release-contract-diagnose:/);
  rmSync(directory, { recursive: true });
});
