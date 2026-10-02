#!/usr/bin/env python3
"""Fail-closed guards for the protected-main subject image publisher."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path


EXPECTED_REPOSITORY = "forge-trust/AppSurface"
EXPECTED_REF = "refs/heads/main"
EXPECTED_IMAGE = "ghcr.io/forge-trust/appsurface-subject-native-validation"
EXPECTED_MAIN_RULESET_ID = 7295365
SHA256_RE = re.compile(r"^sha256:[0-9a-f]{64}$")
HEX_SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
GIT_SHA_RE = re.compile(r"^[0-9a-f]{40}$")
POSITIVE_DECIMAL_RE = re.compile(r"^[1-9][0-9]*$")
IMAGE_ID_RE = re.compile(r"^sha256:[0-9a-f]{64}$")
MAX_REGISTRY_METADATA_BYTES = 1024 * 1024


def _unique_json_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate registry manifest key")
        result[key] = value
    return result


def validate_context(
    *, event_name: str, repository: str, ref: str, image: str,
    sha: str, run_id: str, run_attempt: str,
) -> tuple[str, str]:
    if event_name != "push":
        raise ValueError("publication is allowed only for pushes to the protected main branch")
    if repository != EXPECTED_REPOSITORY:
        raise ValueError(f"publication is restricted to {EXPECTED_REPOSITORY}")
    if ref != EXPECTED_REF:
        raise ValueError(f"publication is restricted to {EXPECTED_REF}")
    if image != EXPECTED_IMAGE:
        raise ValueError(f"EVIDENCE_GATE_SUBJECT_IMAGE must equal {EXPECTED_IMAGE}")
    if not GIT_SHA_RE.fullmatch(sha):
        raise ValueError("GITHUB_SHA must be a full lowercase 40-character commit SHA")
    if not POSITIVE_DECIMAL_RE.fullmatch(run_id):
        raise ValueError("GITHUB_RUN_ID must be a positive decimal integer")
    if not POSITIVE_DECIMAL_RE.fullmatch(run_attempt):
        raise ValueError("GITHUB_RUN_ATTEMPT must be a positive decimal integer")

    image_tag = f"{image}:main-{sha}-{run_id}-{run_attempt}"
    return image, image_tag


def validate_main_head(source_sha: str, current_main_sha: str) -> None:
    if not GIT_SHA_RE.fullmatch(source_sha) or not GIT_SHA_RE.fullmatch(current_main_sha):
        raise ValueError("source and current main revisions must be full lowercase commit SHAs")
    if source_sha != current_main_sha:
        raise ValueError("validated source commit is no longer the current main revision")


def validate_image_identity(
    *, image_id: str, operating_system: str, architecture: str,
    expected_image_id: str | None = None,
) -> None:
    if not IMAGE_ID_RE.fullmatch(image_id):
        raise ValueError("Docker image ID must be sha256 followed by 64 lowercase hex characters")
    if operating_system != "linux" or architecture != "amd64":
        raise ValueError("validated subject image must be linux/amd64")
    if expected_image_id is not None and image_id != expected_image_id:
        raise ValueError("loaded image ID does not match the ID captured after native validation")


def verify_archive(archive: Path, expected_sha256: str) -> None:
    if not HEX_SHA256_RE.fullmatch(expected_sha256):
        raise ValueError("archive SHA-256 must be 64 lowercase hexadecimal characters")
    digest = hashlib.sha256()
    with archive.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    actual = digest.hexdigest()
    if actual != expected_sha256:
        raise ValueError("downloaded image archive does not match the validated archive SHA-256")


def validate_environment(environment: dict, branch_policies: dict) -> None:
    if environment.get("can_admins_bypass") is not False:
        raise ValueError("publisher environment must prevent administrator bypass")
    deployment_policy = environment.get("deployment_branch_policy")
    if not isinstance(deployment_policy, dict) or deployment_policy.get("protected_branches") is not False or deployment_policy.get("custom_branch_policies") is not True:
        raise ValueError("publisher environment must use explicit custom branch policies")

    rules = environment.get("protection_rules")
    has_independent_reviewer = isinstance(rules, list) and any(
        isinstance(rule, dict)
        and rule.get("type") == "required_reviewers"
        and rule.get("prevent_self_review") is True
        and isinstance(rule.get("reviewers"), list)
        and len(rule["reviewers"]) > 0
        for rule in rules
    )
    if not has_independent_reviewer:
        raise ValueError("publisher environment must require a reviewer and prevent self-review")

    policies = branch_policies.get("branch_policies")
    total_count = branch_policies.get("total_count")
    if total_count != 1 or not isinstance(policies, list) or len(policies) != 1:
        raise ValueError("publisher environment must allow exactly the main branch")
    policy = policies[0]
    # GitHub's list response omits `type` for the default branch policy in its
    # documented example. An explicit tag rule is still unacceptable.
    if (
        not isinstance(policy, dict)
        or policy.get("type", "branch") != "branch"
        or policy.get("name") != "main"
    ):
        raise ValueError("publisher environment must allow exactly the main branch")


def validate_main_ruleset(ruleset: dict, default_branch: str) -> None:
    """Require the current default branch to enforce review of the final revision without bypass."""
    if default_branch != "main":
        raise ValueError("publisher repository default branch must remain main")
    if (
        not isinstance(ruleset, dict)
        or ruleset.get("id") != EXPECTED_MAIN_RULESET_ID
        or ruleset.get("enforcement") != "active"
        or ruleset.get("target") != "branch"
    ):
        raise ValueError("publisher main ruleset must be the active reviewed ruleset")

    conditions = ruleset.get("conditions")
    ref_name = conditions.get("ref_name") if isinstance(conditions, dict) else None
    if (
        not isinstance(ref_name, dict)
        or not isinstance(ref_name.get("include"), list)
        or not any(
            selector in ref_name["include"]
            for selector in ("~DEFAULT_BRANCH", "refs/heads/main")
        )
        or ref_name.get("exclude") != []
    ):
        raise ValueError("publisher main ruleset must include main without exclusions")
    if "bypass_actors" not in ruleset:
        raise ValueError("publisher ruleset inspection credential must expose bypass_actors")
    if ruleset["bypass_actors"] != []:
        raise ValueError("publisher main ruleset must not allow review bypass actors")

    rules = ruleset.get("rules")
    if not isinstance(rules, list) or not any(
        isinstance(rule, dict) and rule.get("type") == "non_fast_forward"
        for rule in rules
    ):
        raise ValueError("publisher main ruleset must prevent force pushes")
    has_current_review = any(
        isinstance(rule, dict)
        and rule.get("type") == "pull_request"
        and isinstance(rule.get("parameters"), dict)
        and type(rule["parameters"].get("required_approving_review_count")) is int
        and rule["parameters"]["required_approving_review_count"] >= 1
        and (
            rule["parameters"].get("dismiss_stale_reviews_on_push") is True
            or rule["parameters"].get("require_last_push_approval") is True
        )
        for rule in rules
    )
    if not has_current_review:
        raise ValueError("publisher main ruleset must review the latest pull-request revision")


def verify_registry_manifest(
    descriptor_path: Path,
    raw_manifest_path: Path,
    expected_image_id: str,
) -> str:
    try:
        with descriptor_path.open("rb") as descriptor_file:
            descriptor_bytes = descriptor_file.read(MAX_REGISTRY_METADATA_BYTES + 1)
        with raw_manifest_path.open("rb") as manifest_file:
            raw_manifest = manifest_file.read(MAX_REGISTRY_METADATA_BYTES + 1)
    except OSError as error:
        raise ValueError("registry inspection did not return valid manifest JSON") from error
    if len(descriptor_bytes) > MAX_REGISTRY_METADATA_BYTES or len(raw_manifest) > MAX_REGISTRY_METADATA_BYTES:
        raise ValueError("registry manifest metadata exceeds its fixed byte limit")
    try:
        descriptor = json.loads(descriptor_bytes, object_pairs_hook=_unique_json_object)
        manifest = json.loads(raw_manifest, object_pairs_hook=_unique_json_object)
    except (UnicodeError, ValueError) as error:
        raise ValueError("registry inspection did not return valid manifest JSON") from error

    if not isinstance(descriptor, dict):
        raise ValueError("registry manifest descriptor must be a JSON object")
    digest = descriptor.get("digest")
    if not isinstance(digest, str) or not SHA256_RE.fullmatch(digest):
        raise ValueError("registry manifest descriptor has no valid SHA-256 digest")
    if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 2:
        raise ValueError("registry returned an unsupported or malformed image manifest")
    supported_media_types = {
        "application/vnd.docker.distribution.manifest.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
    }
    media_type = manifest.get("mediaType")
    if media_type not in supported_media_types or descriptor.get("mediaType") != media_type:
        raise ValueError("registry did not return a matching single-image manifest media type")
    if "manifests" in manifest:
        raise ValueError("registry returned an image index instead of the validated single-platform image")
    config = manifest.get("config")
    layers = manifest.get("layers")
    if not isinstance(config, dict) or not isinstance(layers, list):
        raise ValueError("registry image manifest is missing its config or layers")
    if not IMAGE_ID_RE.fullmatch(expected_image_id) or config.get("digest") != expected_image_id:
        raise ValueError("registry image config digest does not match the validated local image ID")
    if descriptor.get("size") != len(raw_manifest):
        raise ValueError("registry manifest size does not match the inspected raw manifest bytes")

    actual = "sha256:" + hashlib.sha256(raw_manifest).hexdigest()
    if actual != digest:
        raise ValueError("registry manifest bytes do not match the descriptor digest")
    return digest


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)

    context = commands.add_parser("context", help="validate the protected publisher context")
    for name in ("event-name", "repository", "ref", "image", "sha", "run-id", "run-attempt"):
        context.add_argument(f"--{name}", required=True)

    identity = commands.add_parser("image-identity", help="validate a Docker image identity")
    identity.add_argument("--image-id", required=True)
    identity.add_argument("--os", required=True)
    identity.add_argument("--architecture", required=True)
    identity.add_argument("--expected-image-id")

    main_head = commands.add_parser("main-head", help="require the source commit to remain main HEAD")
    main_head.add_argument("--source-sha", required=True)
    main_head.add_argument("--current-main-sha", required=True)

    archive = commands.add_parser("verify-archive", help="verify a transferred image archive")
    archive.add_argument("--archive", required=True, type=Path)
    archive.add_argument("--expected-sha256", required=True)

    environment = commands.add_parser("environment", help="require a protected publisher environment")
    environment.add_argument("--environment-json", required=True, type=Path)
    environment.add_argument("--branch-policies-json", required=True, type=Path)

    ruleset = commands.add_parser("main-ruleset", help="require reviewed, non-bypassable main")
    ruleset.add_argument("--ruleset-json", required=True, type=Path)
    ruleset.add_argument("--default-branch", required=True)

    manifest = commands.add_parser("manifest-digest", help="verify registry manifest bytes")
    manifest.add_argument("--descriptor", required=True, type=Path)
    manifest.add_argument("--raw-manifest", required=True, type=Path)
    manifest.add_argument("--expected-image-id", required=True)
    return parser


def main() -> int:
    arguments = build_parser().parse_args()
    try:
        if arguments.command == "context":
            image, image_tag = validate_context(
                event_name=arguments.event_name,
                repository=arguments.repository,
                ref=arguments.ref,
                image=arguments.image,
                sha=arguments.sha,
                run_id=arguments.run_id,
                run_attempt=arguments.run_attempt,
            )
            print(f"image_name={image}")
            print(f"image_tag={image_tag}")
        elif arguments.command == "image-identity":
            validate_image_identity(
                image_id=arguments.image_id,
                operating_system=arguments.os,
                architecture=arguments.architecture,
                expected_image_id=arguments.expected_image_id,
            )
        elif arguments.command == "main-head":
            validate_main_head(arguments.source_sha, arguments.current_main_sha)
        elif arguments.command == "verify-archive":
            verify_archive(arguments.archive, arguments.expected_sha256)
        elif arguments.command == "environment":
            environment = json.loads(arguments.environment_json.read_text(encoding="utf-8"))
            branch_policies = json.loads(arguments.branch_policies_json.read_text(encoding="utf-8"))
            if not isinstance(environment, dict) or not isinstance(branch_policies, dict):
                raise ValueError("publisher environment inspection must return JSON objects")
            validate_environment(environment, branch_policies)
        elif arguments.command == "main-ruleset":
            ruleset = json.loads(arguments.ruleset_json.read_text(encoding="utf-8"))
            validate_main_ruleset(ruleset, arguments.default_branch)
        else:
            print(
                verify_registry_manifest(
                    arguments.descriptor,
                    arguments.raw_manifest,
                    arguments.expected_image_id,
                )
            )
    except (OSError, ValueError) as error:
        print(f"publisher guard failed: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
