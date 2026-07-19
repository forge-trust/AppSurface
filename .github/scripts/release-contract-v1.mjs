import { Buffer } from "node:buffer";

/**
 * Dormant v1 release-prose classifier for normalized pull-request evidence.
 *
 * This module is intentionally pure and read-only. It classifies only whether a
 * pull request has accepted release evidence; it makes no compatibility,
 * supply-chain, review, CI-health, or merge-safety judgment. Invalid JSON-shaped
 * evidence returns deterministic diagnostics instead of throwing.
 *
 * @typedef {object} ReleaseContractFileV1
 * @property {string} filename Repository-relative path returned by GitHub.
 * @property {string} status GitHub file status; automatic classification accepts only `modified`.
 * @property {number} additions Non-negative GitHub additions count.
 * @property {number} deletions Non-negative GitHub deletions count.
 * @property {number} changes Sum of additions and deletions.
 * @property {string} patch Complete GitHub unified-diff patch.
 *
 * @typedef {object} ReleaseContractInputV1
 * @property {string} title Pull-request title.
 * @property {string} authorLogin Exact pull-request author login.
 * @property {string[]} labels Exact, case-sensitive pull-request label names.
 * @property {number} changedFiles Authoritative pull-request `changed_files` value.
 * @property {ReleaseContractFileV1[]} files Complete, uniquely named file enumeration.
 */

/** Version checked by trusted-base workflow integration before evaluation. */
export const contractVersion = 1;

const MaximumPullRequestFiles = 3000;
const MaximumPatchBytesPerFile = 128 * 1024;
const MaximumAggregatePatchBytes = 1024 * 1024;
const MaximumPatchLineBytes = 8 * 1024;

const ConventionalTitlePattern = /^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([^)]+\))?!?: .+/;
const WorkflowPathPattern = /^\.github\/workflows\/.+\.ya?ml$/;
const UsesLinePattern = /^([ \t]*(?:-[ \t]+)?uses:[ \t]+)([A-Za-z0-9_.-]+(?:\/[A-Za-z0-9_.-]+)+)@([0-9a-f]{40})((?:[ \t]+#.*)?[ \t]*)$/;
const HunkHeaderPattern = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(?: (.*))?$/;
const StepMetadataPattern = /^- (?:name|id|if|continue-on-error|timeout-minutes):(?:\s|$)/;
const DocumentationPath = ".github/release-ops.md#release-contract-check";
const MaximumLocationLength = 256;

function boundedText(value, maximumLength = MaximumLocationLength) {
  return [...value].slice(0, maximumLength).join("");
}

function boundedLocation(location) {
  return {
    file: boundedText(location.file),
    ...(location.hunk ? { hunk: location.hunk } : {})
  };
}

function diagnostic(code, category, problem, cause, remediation, blocking = false, location = undefined) {
  return {
    code,
    category,
    problem,
    cause,
    remediation,
    docsUrl: DocumentationPath,
    blocking,
    ...(location ? { location: boundedLocation(location) } : {})
  };
}

function automaticDiagnostic(code, problem, cause, remediation, location = undefined) {
  return diagnostic(code, "automatic-eligibility", problem, cause, remediation, false, location);
}

function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function isNonNegativeSafeInteger(value) {
  return Number.isSafeInteger(value) && value >= 0;
}

function isWorkflowPath(value) {
  if (!WorkflowPathPattern.test(value) || value.includes("\0")) {
    return false;
  }

  return value.slice(".github/workflows/".length).split("/").every(segment => segment !== "" && segment !== "." && segment !== "..");
}

function firstArrayProblem(value, itemPredicate) {
  if (!Array.isArray(value)) {
    return "not-array";
  }

  for (let index = 0; index < value.length; index += 1) {
    if (!(index in value) || !itemPredicate(value[index])) {
      return "invalid-item";
    }
  }

  if (new Set(value).size !== value.length) {
    return "duplicate-item";
  }

  return null;
}

function parseHunkCount(value, omittedDefault) {
  if (value === undefined) {
    return omittedDefault;
  }

  const parsed = Number(value);
  return Number.isSafeInteger(parsed) ? parsed : null;
}

function parseUsesLine(line) {
  const match = UsesLinePattern.exec(line);
  if (!match) {
    return null;
  }

  const [, prefix, locator] = match;
  const components = locator.split("/");
  if (components.length < 2 || components.some(component => component === "." || component === "..")) {
    return null;
  }

  for (let index = 2; index < components.length - 1; index += 1) {
    if (components[index] === ".github" && components[index + 1] === "workflows") {
      return null;
    }
  }

  return `${prefix}${locator}@<PINNED-SHA>`;
}

function parsePatch(patch, filename) {
  if (patch.includes("\r")) {
    return {
      error: automaticDiagnostic(
        "patch-carriage-return",
        "Patch evidence uses unsupported line endings.",
        "The GitHub patch contains a carriage-return character, so its line structure is ambiguous.",
        "Ask a maintainer to classify the pull request manually.",
        { file: filename })
    };
  }

  let lines = patch.split("\n");
  if (lines.at(-1) === "") {
    lines = lines.slice(0, -1);
  }

  if (lines.length === 0 || lines.some(line => Buffer.byteLength(line, "utf8") > MaximumPatchLineBytes)) {
    return {
      error: automaticDiagnostic(
        "patch-line-limit",
        "Patch evidence is empty or exceeds the supported line length.",
        `Every patch line must be at most ${MaximumPatchLineBytes} UTF-8 bytes.`,
        "Ask a maintainer to classify the pull request manually.",
        { file: filename })
    };
  }

  let index = 0;
  if (lines[index]?.startsWith("--- ")) {
    if (lines[index] !== `--- a/${filename}` || lines[index + 1] !== `+++ b/${filename}`) {
      return {
        error: automaticDiagnostic(
          "patch-file-header-invalid",
          "Patch file headers are incomplete.",
          "A removed-file header was not followed by an added-file header.",
          "Ask a maintainer to classify the pull request manually.",
          { file: filename })
      };
    }

    index += 2;
  } else if (lines[index]?.startsWith("+++ ")) {
    return {
      error: automaticDiagnostic(
        "patch-file-header-invalid",
        "Patch file headers are incomplete.",
        "An added-file header appeared without a preceding removed-file header.",
        "Ask a maintainer to classify the pull request manually.",
        { file: filename })
    };
  }

  let additions = 0;
  let deletions = 0;
  let hunkIndex = 0;
  const hunks = [];

  while (index < lines.length) {
    const header = HunkHeaderPattern.exec(lines[index]);
    if (!header) {
      return {
        error: automaticDiagnostic(
          "patch-hunk-header-invalid",
          "Patch evidence contains unsupported metadata or a malformed hunk header.",
          `Expected a unified-diff hunk header at line ${index + 1}.`,
          "Ask a maintainer to classify the pull request manually.",
          { file: filename, hunk: hunkIndex + 1 })
      };
    }

    const oldStart = Number(header[1]);
    const oldCount = parseHunkCount(header[2], 1);
    const newStart = Number(header[3]);
    const newCount = parseHunkCount(header[4], 1);
    if (![oldStart, oldCount, newStart, newCount].every(Number.isSafeInteger)
        || oldStart < 0
        || oldCount < 0
        || newStart < 0
        || newCount < 0
        || (oldStart === 0) !== (oldCount === 0)
        || (newStart === 0) !== (newCount === 0)) {
      return {
        error: automaticDiagnostic(
          "patch-hunk-range-invalid",
          "Patch hunk ranges are invalid.",
          "A hunk range is negative or larger than JavaScript can represent safely.",
          "Ask a maintainer to classify the pull request manually.",
          { file: filename, hunk: hunkIndex + 1 })
      };
    }

    if (oldStart !== newStart || oldCount !== newCount) {
      return {
        error: automaticDiagnostic(
          "patch-position-changed",
          "The workflow reference did not remain in the same hunk position.",
          "The old and new hunk ranges differ.",
          "Review the workflow change and classify its release impact manually.",
          { file: filename, hunk: hunkIndex + 1 })
      };
    }

    index += 1;
    hunkIndex += 1;
    let consumedOld = 0;
    let consumedNew = 0;
    let hunkAdditions = 0;
    let hunkDeletions = 0;
    const oldLines = [];
    const newLines = [];

    while (index < lines.length && !lines[index].startsWith("@@ ")) {
      const line = lines[index];
      const prefix = line[0];
      const content = line.slice(1);

      if (prefix === " ") {
        consumedOld += 1;
        consumedNew += 1;
        oldLines.push({ content, changed: false });
        newLines.push({ content, changed: false });
      } else if (prefix === "-") {
        consumedOld += 1;
        hunkDeletions += 1;
        deletions += 1;
        oldLines.push({ content, changed: true });
      } else if (prefix === "+") {
        consumedNew += 1;
        hunkAdditions += 1;
        additions += 1;
        newLines.push({ content, changed: true });
      } else {
        return {
          error: automaticDiagnostic(
            prefix === "\\" ? "patch-newline-marker" : "patch-line-prefix-invalid",
            "Patch evidence contains an unsupported line.",
            prefix === "\\"
              ? "Terminal newline markers are outside the automatic classifier contract."
              : `Line ${index + 1} does not use a unified-diff prefix.`,
            "Ask a maintainer to classify the pull request manually.",
            { file: filename, hunk: hunkIndex })
        };
      }

      index += 1;
    }

    if (consumedOld !== oldCount || consumedNew !== newCount) {
      return {
        error: automaticDiagnostic(
          "patch-hunk-count-mismatch",
          "Patch hunk evidence is incomplete.",
          `The hunk header declares ${oldCount} old and ${newCount} new lines, but the patch contains ${consumedOld} old and ${consumedNew} new lines.`,
          "Ask a maintainer to classify the pull request manually.",
          { file: filename, hunk: hunkIndex })
      };
    }

    if (hunkAdditions === 0 || hunkDeletions === 0) {
      return {
        error: automaticDiagnostic(
          "patch-hunk-unpaired",
          "A changed hunk does not contain a paired replacement.",
          "Automatic exemption requires at least one removal and one addition in every hunk.",
          "Review the workflow change and classify its release impact manually.",
          { file: filename, hunk: hunkIndex })
      };
    }

    hunks.push({ oldLines, newLines, hunk: hunkIndex, section: header[5] ?? "" });
  }

  if (hunkIndex === 0) {
    return {
      error: automaticDiagnostic(
        "patch-hunk-missing",
        "Patch evidence contains no changed hunks.",
        "The pull-request files API did not return a usable unified diff.",
        "Ask a maintainer to classify the pull request manually.",
        { file: filename })
    };
  }

  return { additions, deletions, hunks };
}

function indentation(line) {
  return /^[ \t]*/.exec(line)[0].length;
}

function isScalarContent(lines, changedIndex) {
  let upperIndent = indentation(lines[changedIndex].content);
  let startIndex = changedIndex;
  while (upperIndent > 0) {
    const candidateIndex = precedingLowerIndentIndex(lines, startIndex, upperIndent);
    if (candidateIndex < 0) {
      return false;
    }

    const candidate = lines[candidateIndex].content;
    if (/:\s*[|>](?:[+-]?\d?|\d?[+-])?\s*(?:#.*)?$/.test(candidate)
        || /:\s*"(?:\\.|[^"\\])*$/.test(candidate)
        || /:\s*'(?:''|[^'])*$/.test(candidate)) {
      return true;
    }

    upperIndent = indentation(candidate);
    startIndex = candidateIndex;
  }

  return false;
}

function precedingLowerIndentIndex(lines, startIndex, upperIndent) {
  for (let index = startIndex - 1; index >= 0; index -= 1) {
    if (lines[index].content.trim().length > 0 && indentation(lines[index].content) < upperIndent) {
      return index;
    }
  }

  return -1;
}

function isEligibleUsesPosition(lines, changedIndex, section) {
  const changed = lines[changedIndex].content;
  const changedIndent = indentation(changed);
  const parentIndex = precedingLowerIndentIndex(lines, changedIndex, changedIndent);
  if (parentIndex < 0) {
    return false;
  }

  const parent = lines[parentIndex].content.trim();
  if (changed.trimStart().startsWith("- ")) {
    return parent === "steps:";
  }

  const parentIndent = indentation(lines[parentIndex].content);
  const grandparentIndex = precedingLowerIndentIndex(lines, parentIndex, parentIndent);
  if (grandparentIndex < 0) {
    return section === "jobs:" && StepMetadataPattern.test(parent);
  }

  const grandparent = lines[grandparentIndex].content.trim();
  return parent.startsWith("- ") && grandparent === "steps:";
}

function validateChangedLines(parsed, filename) {
  for (const hunk of parsed.hunks) {
    const normalizedSides = [];
    for (const [side, lines] of [["removal", hunk.oldLines], ["addition", hunk.newLines]]) {
      const normalized = [];
      for (let index = 0; index < lines.length; index += 1) {
        const line = lines[index];
        if (!line.changed) {
          normalized.push(line.content);
          continue;
        }

        const normalizedLine = parseUsesLine(line.content);
        if (normalizedLine === null || isScalarContent(lines, index) || !isEligibleUsesPosition(lines, index, hunk.section)) {
          return automaticDiagnostic(
            `uses-${side}-ineligible`,
            `A ${side === "removal" ? "removed" : "added"} line is not an eligible pinned external-action reference.`,
            "The line is quoted, unpinned, local, Docker-based, a reusable workflow, block-scalar content, or changes workflow semantics beyond the supported grammar.",
            "Review the workflow change and classify its release impact manually.",
            { file: filename, hunk: hunk.hunk });
        }

        normalized.push(normalizedLine);
      }

      normalizedSides.push(normalized);
    }

    const [normalizedOld, normalizedNew] = normalizedSides;
    if (normalizedOld.length !== normalizedNew.length
        || normalizedOld.some((line, lineIndex) => line !== normalizedNew[lineIndex])) {
      return automaticDiagnostic(
        "patch-normalized-content-changed",
        "The workflow hunk changes more than pinned action versions or comments.",
        "After normalizing eligible pins, the complete old and new hunk sequences differ.",
        "Review the workflow change and classify its release impact manually.",
        { file: filename, hunk: hunk.hunk });
    }
  }

  return null;
}

function evaluateAutomaticExemption(input, labels, files) {
  if (input.authorLogin !== "dependabot[bot]") {
    return {
      automaticExemption: false,
      automaticClassification: null,
      automaticDiagnostic: automaticDiagnostic(
        "identity-not-dependabot",
        "The pull request is not eligible for automatic release-note exemption.",
        "The author is not exactly dependabot[bot].",
        "Add release prose or ask a maintainer to apply no-unreleased-entry when appropriate.")
    };
  }

  const labelsProblem = firstArrayProblem(labels, label => typeof label === "string" && label.length > 0);
  if (labelsProblem !== null) {
    return {
      automaticExemption: false,
      automaticClassification: null,
      automaticDiagnostic: automaticDiagnostic(
        `labels-${labelsProblem}`,
        "Pull-request labels are malformed.",
        "Labels must be a unique array of non-empty strings.",
        "Retry the check or classify the pull request manually.")
    };
  }

  if (!labels.includes("dependencies")) {
    return {
      automaticExemption: false,
      automaticClassification: null,
      automaticDiagnostic: automaticDiagnostic(
        "label-dependencies-missing",
        "The Dependabot dependency label is missing.",
        "Automatic exemption requires the exact dependencies label.",
        "Restore the expected Dependabot labels or classify the pull request manually.")
    };
  }

  if (!labels.includes("github_actions")) {
    return {
      automaticExemption: false,
      automaticClassification: null,
      automaticDiagnostic: automaticDiagnostic(
        "label-github-actions-missing",
        "The GitHub Actions ecosystem label is missing.",
        "Automatic exemption requires the exact github_actions label.",
        "Restore the expected Dependabot labels or classify the pull request manually.")
    };
  }

  if (!Number.isSafeInteger(input.changedFiles) || input.changedFiles <= 0) {
    return {
      automaticExemption: false,
      automaticClassification: null,
      automaticDiagnostic: automaticDiagnostic(
        "changed-files-invalid",
        "The authoritative changed-file count is invalid.",
        "changedFiles must be a positive safe integer.",
        "Retry the check or classify the pull request manually.")
    };
  }

  if (input.changedFiles > MaximumPullRequestFiles) {
    return {
      automaticExemption: false,
      automaticClassification: null,
      automaticDiagnostic: automaticDiagnostic(
        "changed-files-over-limit",
        "The pull request is too large for complete file enumeration.",
        `GitHub returns at most ${MaximumPullRequestFiles} pull-request files.`,
        "Split the pull request or classify it manually.")
    };
  }

  if (!Array.isArray(files)) {
    return {
      automaticExemption: false,
      automaticClassification: null,
      automaticDiagnostic: automaticDiagnostic(
        "files-not-array",
        "Pull-request file evidence is malformed.",
        "files must be an array.",
        "Retry the check or classify the pull request manually.")
    };
  }

  if (files.length !== input.changedFiles) {
    return {
      automaticExemption: false,
      automaticClassification: null,
      automaticDiagnostic: automaticDiagnostic(
        "changed-files-count-mismatch",
        "Pull-request file enumeration is incomplete.",
        `The event reports ${input.changedFiles} changed files, but the API returned ${files.length}.`,
        "Retry the check or classify the pull request manually.")
    };
  }

  for (let index = 0; index < files.length; index += 1) {
    if (!(index in files) || !isRecord(files[index]) || typeof files[index].filename !== "string" || files[index].filename.length === 0) {
      return {
        automaticExemption: false,
        automaticClassification: null,
        automaticDiagnostic: automaticDiagnostic(
          "file-entry-invalid",
          "Pull-request file evidence contains an invalid entry.",
          "Every file must be an object with a non-empty filename.",
          "Retry the check or classify the pull request manually.")
      };
    }
  }

  const filenames = files.map(file => file.filename);
  if (new Set(filenames).size !== filenames.length) {
    return {
      automaticExemption: false,
      automaticClassification: null,
      automaticDiagnostic: automaticDiagnostic(
        "file-duplicate",
        "Pull-request file evidence contains duplicate filenames.",
        "Complete API enumeration must contain each file exactly once.",
        "Retry the check or classify the pull request manually.")
    };
  }

  const sortedFiles = [...files].sort((left, right) => left.filename < right.filename ? -1 : 1);
  for (const file of sortedFiles) {
    if (file.status !== "modified") {
      return {
        automaticExemption: false,
        automaticClassification: null,
        automaticDiagnostic: automaticDiagnostic(
          "file-status-ineligible",
          "A changed file is not a modification.",
          `Automatic exemption does not support the ${String(file.status)} file status.`,
          "Review the file set and classify the pull request manually.",
          { file: file.filename })
      };
    }

    if (!isWorkflowPath(file.filename)) {
      return {
        automaticExemption: false,
        automaticClassification: null,
        automaticDiagnostic: automaticDiagnostic(
          "file-path-ineligible",
          "A changed file is outside the supported workflow path.",
          "Automatic exemption only supports YAML descendants of .github/workflows/.",
          "Add release prose or ask a maintainer to classify the pull request.",
          { file: file.filename })
      };
    }

    if (![file.additions, file.deletions, file.changes].every(isNonNegativeSafeInteger)
        || file.changes !== file.additions + file.deletions) {
      return {
        automaticExemption: false,
        automaticClassification: null,
        automaticDiagnostic: automaticDiagnostic(
          "file-counts-invalid",
          "A workflow file has invalid change counts.",
          "additions, deletions, and changes must be non-negative safe integers and changes must equal additions plus deletions.",
          "Retry the check or classify the pull request manually.",
          { file: file.filename })
      };
    }

  }

  let aggregatePatchBytes = 0;
  for (const file of sortedFiles) {
    if (typeof file.patch !== "string" || file.patch.length === 0) {
      return {
        automaticExemption: false,
        automaticClassification: null,
        automaticDiagnostic: automaticDiagnostic(
          "patch-missing",
          "A workflow file has no complete patch evidence.",
          "GitHub omitted or returned an empty unified patch.",
          "Retry the check or classify the pull request manually.",
          { file: file.filename })
      };
    }

    const patchBytes = Buffer.byteLength(file.patch, "utf8");
    aggregatePatchBytes += patchBytes;
    if (patchBytes > MaximumPatchBytesPerFile || aggregatePatchBytes > MaximumAggregatePatchBytes) {
      return {
        automaticExemption: false,
        automaticClassification: null,
        automaticDiagnostic: automaticDiagnostic(
          patchBytes > MaximumPatchBytesPerFile ? "patch-file-size-limit" : "patch-aggregate-size-limit",
          "Patch evidence exceeds the automatic classifier size limit.",
          patchBytes > MaximumPatchBytesPerFile
            ? `One patch exceeds ${MaximumPatchBytesPerFile} UTF-8 bytes.`
            : `Aggregate patches exceed ${MaximumAggregatePatchBytes} UTF-8 bytes.`,
          "Split the pull request or classify it manually.",
          { file: file.filename })
      };
    }

  }

  const parsedFiles = [];
  for (const file of sortedFiles) {
    const parsed = parsePatch(file.patch, file.filename);
    if (parsed.error) {
      return {
        automaticExemption: false,
        automaticClassification: null,
        automaticDiagnostic: parsed.error
      };
    }

    parsedFiles.push({ file, parsed });
  }

  for (const { file, parsed } of parsedFiles) {
    if (parsed.additions !== file.additions || parsed.deletions !== file.deletions) {
      return {
        automaticExemption: false,
        automaticClassification: null,
        automaticDiagnostic: automaticDiagnostic(
          "patch-api-count-mismatch",
          "Patch evidence does not match GitHub's file counts.",
          `The patch contains ${parsed.additions} additions and ${parsed.deletions} deletions; the API reports ${file.additions} and ${file.deletions}.`,
          "Retry the check or classify the pull request manually.",
          { file: file.filename })
      };
    }
  }

  for (const { file, parsed } of parsedFiles) {
    const semanticDiagnostic = validateChangedLines(parsed, file.filename);
    if (semanticDiagnostic !== null) {
      return {
        automaticExemption: false,
        automaticClassification: null,
        automaticDiagnostic: semanticDiagnostic
      };
    }
  }

  return {
    automaticExemption: true,
    automaticClassification: "dependabot-github-actions-reference-only",
    automaticDiagnostic: null
  };
}

/**
 * Evaluates authored, maintainer-label, and syntactic Dependabot release evidence.
 * Evidence precedence is unreleased entry, exact maintainer label, then automatic
 * classification. Conventional-title validation remains an independent blocker.
 *
 * @param {ReleaseContractInputV1 | unknown} input Normalized JSON-shaped evidence.
 * @returns {object} Stable v1 result with evidence, classification, and structured diagnostics.
 */
export function evaluateReleaseContractV1(input) {
  const normalizedInput = isRecord(input) ? input : {};
  const title = typeof normalizedInput.title === "string" ? normalizedInput.title.trim() : "";
  const labels = normalizedInput.labels;
  const files = normalizedInput.files;
  const conventionalTitleValid = ConventionalTitlePattern.test(title);
  const labelsValid = firstArrayProblem(labels, label => typeof label === "string" && label.length > 0) === null;
  const fileEntriesValid = Array.isArray(files)
    && firstArrayProblem(files, file => isRecord(file) && typeof file.filename === "string" && file.filename.length > 0) === null
    && new Set(files.map(file => file.filename)).size === files.length;
  const hasUnreleasedEntry = fileEntriesValid && files.some(file =>
    file.filename === "releases/unreleased.md"
      && (file.status === "added" || file.status === "modified"));
  const hasMaintainerExemptionLabel = labelsValid && labels.includes("no-unreleased-entry");
  const automatic = evaluateAutomaticExemption(normalizedInput, labels, files);

  let releaseEvidenceKind = "none";
  if (hasUnreleasedEntry) {
    releaseEvidenceKind = "unreleased-entry";
  } else if (hasMaintainerExemptionLabel) {
    releaseEvidenceKind = "maintainer-label";
  } else if (automatic.automaticExemption) {
    releaseEvidenceKind = "automatic-dependabot";
  }

  const diagnostics = [];
  if (!conventionalTitleValid) {
    diagnostics.push(diagnostic(
      "conventional-title-invalid",
      "title",
      "The pull-request title does not follow Conventional Commits.",
      "The title is missing an accepted type, optional scope, colon, or description.",
      "Rename the pull request, for example: build(deps): update pinned GitHub Actions.",
      true));
  }

  if (automatic.automaticDiagnostic !== null) {
    diagnostics.push(automatic.automaticDiagnostic);
  }

  if (releaseEvidenceKind === "none") {
    diagnostics.push(diagnostic(
      "release-evidence-missing",
      "release-evidence",
      "The pull request has no accepted release evidence.",
      "It does not update releases/unreleased.md, carry no-unreleased-entry, or qualify for automatic release-note exemption.",
      "Add adopter-facing release prose, or ask a maintainer to apply no-unreleased-entry when the change is outside the public release story.",
      true));
  }

  return {
    contractVersion,
    conventionalTitleValid,
    hasUnreleasedEntry,
    hasMaintainerExemptionLabel,
    automaticExemption: automatic.automaticExemption,
    automaticClassification: automatic.automaticClassification,
    automaticDiagnostic: automatic.automaticDiagnostic,
    releaseEvidenceKind,
    diagnostics,
    evidence: {
      authorLogin: typeof normalizedInput.authorLogin === "string" ? normalizedInput.authorLogin : "",
      labels: labelsValid ? [...labels] : [],
      expectedChangedFiles: Number.isSafeInteger(normalizedInput.changedFiles) ? normalizedInput.changedFiles : null,
      returnedChangedFiles: Array.isArray(files) ? files.length : null
    }
  };
}

function escapeMarkdown(value, maximumLength = 200) {
  const text = String(value ?? "").slice(0, maximumLength);
  return text
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("|", "&#124;")
    .replaceAll("`", "&#96;")
    .replaceAll("\r", " ")
    .replaceAll("\n", " ");
}

function documentationUrl(context) {
  if (typeof context?.docsUrl === "string" && context.docsUrl.length > 0) {
    return context.docsUrl;
  }

  const owner = escapeMarkdown(context?.owner ?? "OWNER");
  const repo = escapeMarkdown(context?.repo ?? "REPOSITORY");
  const ref = escapeMarkdown(context?.baseSha ?? "main");
  return `https://github.com/${owner}/${repo}/blob/${ref}/.github/release-ops.md#release-contract-check`;
}

/**
 * Renders the bounded, Markdown-escaped decision summary used by CI and local diagnostics.
 * Patch content is never included.
 *
 * @param {ReturnType<typeof evaluateReleaseContractV1>} result Evaluated v1 result.
 * @param {{owner?: string, repo?: string, baseSha?: string, docsUrl?: string}} context Trusted link context.
 * @returns {string} Markdown beginning with Decision, Why, and What to do next.
 */
export function renderReleaseContractSummaryV1(result, context = {}) {
  const blocking = result.diagnostics.filter(item => item.blocking);
  const passed = blocking.length === 0;
  const docsUrl = documentationUrl(context);
  let why;
  let next;

  if (!passed) {
    why = blocking.map(item => item.problem).join(" ");
    next = blocking.map(item => item.remediation).join(" ");
  } else if (result.releaseEvidenceKind === "unreleased-entry") {
    why = "An authored releases/unreleased.md entry is present.";
    next = "Review the release prose and all ordinary checks before merging.";
  } else if (result.releaseEvidenceKind === "maintainer-label") {
    why = "A maintainer applied the no-unreleased-entry exemption label.";
    next = "Confirm the label remains appropriate and review all ordinary checks before merging.";
  } else {
    why = "The Dependabot change is syntactically limited to paired pinned external-action references.";
    next = "Review compatibility and all ordinary checks; add releases/unreleased.md if behavior is adopter-visible.";
  }

  const lines = [
    "## Decision",
    "",
    `**${passed ? "PASS" : "FAIL"}**`,
    "",
    "## Why",
    "",
    escapeMarkdown(why, 1000),
    "",
    "## What to do next",
    "",
    escapeMarkdown(next, 1000)
  ];

  if (result.releaseEvidenceKind === "automatic-dependabot") {
    lines.push(
      "",
      "> **Release-note exemption only; all ordinary checks and review remain required.**");
  }

  lines.push(
    "",
    `<details><summary>Evidence and diagnostics</summary>`,
    "",
    "| Field | Value |",
    "| --- | --- |",
    `| Contract version | ${escapeMarkdown(result.contractVersion)} |`,
    `| Conventional title valid | ${escapeMarkdown(result.conventionalTitleValid)} |`,
    `| Release evidence | ${escapeMarkdown(result.releaseEvidenceKind)} |`,
    `| Automatic exemption | ${escapeMarkdown(result.automaticExemption)} |`,
    `| Automatic classification | ${escapeMarkdown(result.automaticClassification ?? "none")} |`,
    `| Author | ${escapeMarkdown(result.evidence.authorLogin || "unknown")} |`,
    `| Labels | ${escapeMarkdown(result.evidence.labels.join(", ") || "none")} |`,
    `| Expected files | ${escapeMarkdown(result.evidence.expectedChangedFiles ?? "unknown")} |`,
    `| Returned files | ${escapeMarkdown(result.evidence.returnedChangedFiles ?? "unknown")} |`,
    "");

  if (result.diagnostics.length === 0) {
    lines.push("No diagnostics.");
  } else {
    lines.push(
      "| Code | Blocking | Problem | Cause | Remediation | Location |",
      "| --- | --- | --- | --- | --- | --- |");
    for (const item of result.diagnostics) {
      const location = item.location
        ? [item.location.file, item.location.hunk ? `hunk ${item.location.hunk}` : null]
          .filter(Boolean)
          .join(", ")
        : "none";
      lines.push(`| ${escapeMarkdown(item.code)} | ${escapeMarkdown(item.blocking)} | ${escapeMarkdown(item.problem)} | ${escapeMarkdown(item.cause)} | ${escapeMarkdown(item.remediation)} | ${escapeMarkdown(location)} |`);
    }
  }

  lines.push("", `See [release-contract maintainer guidance](${escapeMarkdown(docsUrl, 500)}).`, "", "</details>");
  return `${lines.join("\n")}\n`;
}
