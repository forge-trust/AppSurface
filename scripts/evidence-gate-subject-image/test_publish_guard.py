import hashlib
import json
import tempfile
import unittest
from copy import deepcopy
from pathlib import Path
from unittest.mock import patch

from publish_guard import (
    EXPECTED_IMAGE,
    EXPECTED_MAIN_RULESET_ID,
    EXPECTED_REF,
    EXPECTED_REPOSITORY,
    MAX_REGISTRY_METADATA_BYTES,
    validate_context,
    validate_environment,
    validate_image_identity,
    validate_main_head,
    validate_main_ruleset,
    verify_archive,
    verify_registry_manifest,
)


class PublishGuardTests(unittest.TestCase):
    def setUp(self):
        self.context = {
            "event_name": "push",
            "repository": EXPECTED_REPOSITORY,
            "ref": EXPECTED_REF,
            "image": EXPECTED_IMAGE,
            "sha": "a" * 40,
            "run_id": "1234",
            "run_attempt": "2",
        }

    def test_context_returns_run_unique_registry_tag(self):
        image, image_tag = validate_context(**self.context)

        self.assertEqual(EXPECTED_IMAGE, image)
        self.assertEqual(f"{EXPECTED_IMAGE}:main-{'a' * 40}-1234-2", image_tag)

    def test_context_rejects_untrusted_events_and_refs(self):
        for field, value in (
            ("event_name", "pull_request"),
            ("event_name", "pull_request_target"),
            ("event_name", "workflow_dispatch"),
            ("repository", "attacker/AppSurface"),
            ("ref", "refs/heads/codex/feature"),
            ("ref", "refs/tags/v1.0.0"),
        ):
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                validate_context(**{**self.context, field: value})

    def test_context_rejects_misconfigured_image_and_malformed_run_identity(self):
        for field, value in (
            ("image", "ghcr.io/attacker/subject"),
            ("sha", "a" * 39),
            ("run_id", "0"),
            ("run_attempt", "01"),
        ):
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                validate_context(**{**self.context, field: value})

    def test_main_head_guard_rejects_stale_or_malformed_source_revision(self):
        source_sha = "a" * 40
        validate_main_head(source_sha, source_sha)
        with self.assertRaisesRegex(ValueError, "no longer the current main"):
            validate_main_head(source_sha, "b" * 40)
        with self.assertRaisesRegex(ValueError, "full lowercase commit SHAs"):
            validate_main_head("not-a-sha", source_sha)

    def test_image_identity_requires_validated_linux_amd64_and_matching_id(self):
        image_id = "sha256:" + "b" * 64
        validate_image_identity(image_id=image_id, operating_system="linux", architecture="amd64")

        for values in (
            {"image_id": "sha256:bad", "operating_system": "linux", "architecture": "amd64"},
            {"image_id": image_id, "operating_system": "darwin", "architecture": "amd64"},
            {"image_id": image_id, "operating_system": "linux", "architecture": "arm64"},
            {
                "image_id": image_id,
                "operating_system": "linux",
                "architecture": "amd64",
                "expected_image_id": "sha256:" + "c" * 64,
            },
        ):
            with self.subTest(values=values), self.assertRaises(ValueError):
                validate_image_identity(**values)

    def test_archive_verification_checks_bytes_and_digest_format(self):
        contents = b"immutable validated image archive\n"
        digest = hashlib.sha256(contents).hexdigest()
        with tempfile.TemporaryDirectory() as directory:
            archive = Path(directory) / "image.tar"
            archive.write_bytes(contents)
            with patch.object(Path, "read_bytes", side_effect=AssertionError("archive must be streamed")):
                verify_archive(archive, digest)
            with self.assertRaisesRegex(ValueError, "does not match"):
                verify_archive(archive, "0" * 64)
            with self.assertRaisesRegex(ValueError, "64 lowercase"):
                verify_archive(archive, "not-a-digest")

    def test_environment_requires_exact_main_branch_and_independent_reviewer(self):
        environment = {
            "can_admins_bypass": False,
            "deployment_branch_policy": {
                "protected_branches": False,
                "custom_branch_policies": True,
            },
            "protection_rules": [
                {
                    "type": "required_reviewers",
                    "prevent_self_review": True,
                    "reviewers": [{"type": "User"}],
                }
            ],
        }
        branch_policies = {
            "total_count": 1,
            "branch_policies": [{"id": 361472, "node_id": "policy-node", "name": "main"}],
        }
        validate_environment(environment, branch_policies)

        invalid_configurations = (
            (
                {**environment, "can_admins_bypass": True},
                branch_policies,
            ),
            (
                {**environment, "deployment_branch_policy": None},
                branch_policies,
            ),
            (
                {
                    **environment,
                    "protection_rules": [
                        {"type": "required_reviewers", "prevent_self_review": False, "reviewers": [{"type": "User"}]}
                    ],
                },
                branch_policies,
            ),
            (
                environment,
                {
                    "total_count": 2,
                    "branch_policies": [
                        {"type": "branch", "name": "main"},
                        {"type": "branch", "name": "release"},
                    ],
                },
            ),
            (
                environment,
                {"total_count": 1, "branch_policies": [{"type": "branch", "name": "release"}]},
            ),
            (
                environment,
                {"total_count": 1, "branch_policies": [{"type": "tag", "name": "main"}]},
            ),
            (
                environment,
                {"total_count": 2, "branch_policies": [{"type": "branch", "name": "main"}]},
            ),
        )
        for invalid_environment, invalid_branches in invalid_configurations:
            with self.subTest(environment=invalid_environment, branches=invalid_branches), self.assertRaises(ValueError):
                validate_environment(invalid_environment, invalid_branches)

    def test_main_ruleset_requires_reviewed_latest_revision_without_bypass(self):
        ruleset = {
            "id": EXPECTED_MAIN_RULESET_ID,
            "enforcement": "active",
            "target": "branch",
            "conditions": {"ref_name": {"include": ["~DEFAULT_BRANCH"], "exclude": []}},
            "bypass_actors": [],
            "rules": [
                {"type": "non_fast_forward"},
                {
                    "type": "pull_request",
                    "parameters": {
                        "required_approving_review_count": 1,
                        "dismiss_stale_reviews_on_push": True,
                        "require_last_push_approval": False,
                    },
                },
            ],
        }
        validate_main_ruleset(ruleset, "main")
        explicit_main_ruleset = deepcopy(ruleset)
        explicit_main_ruleset["conditions"]["ref_name"]["include"] = ["refs/heads/main"]
        validate_main_ruleset(explicit_main_ruleset, "main")
        latest_push_ruleset = deepcopy(ruleset)
        latest_push_ruleset["rules"][1]["parameters"].update(
            dismiss_stale_reviews_on_push=False, require_last_push_approval=True
        )
        validate_main_ruleset(latest_push_ruleset, "main")
        hidden_bypass_ruleset = deepcopy(ruleset)
        del hidden_bypass_ruleset["bypass_actors"]
        with self.assertRaisesRegex(ValueError, "must expose bypass_actors"):
            validate_main_ruleset(hidden_bypass_ruleset, "main")

        invalid = []
        for field, value in (
            ("id", EXPECTED_MAIN_RULESET_ID + 1),
            ("enforcement", "evaluate"),
            ("target", "tag"),
            ("bypass_actors", None),
            ("bypass_actors", [{"actor_type": "OrganizationAdmin", "bypass_mode": "always"}]),
            ("rules", [ruleset["rules"][1]]),
        ):
            candidate = deepcopy(ruleset)
            candidate[field] = value
            invalid.append(candidate)
        for selectors in (
            {"include": ["refs/heads/release"], "exclude": []},
            {"include": ["~DEFAULT_BRANCH"], "exclude": ["refs/heads/main"]},
        ):
            candidate = deepcopy(ruleset)
            candidate["conditions"]["ref_name"] = selectors
            invalid.append(candidate)
        for parameters in (
            {"required_approving_review_count": 0, "dismiss_stale_reviews_on_push": True},
            {"required_approving_review_count": True, "dismiss_stale_reviews_on_push": True},
            {"required_approving_review_count": 1, "dismiss_stale_reviews_on_push": False,
             "require_last_push_approval": False},
        ):
            candidate = deepcopy(ruleset)
            candidate["rules"][1]["parameters"] = parameters
            invalid.append(candidate)
        for candidate in invalid:
            with self.subTest(candidate=candidate), self.assertRaises(ValueError):
                validate_main_ruleset(candidate, "main")
        with self.assertRaisesRegex(ValueError, "default branch"):
            validate_main_ruleset(ruleset, "release")
        with self.assertRaisesRegex(ValueError, "active reviewed ruleset"):
            validate_main_ruleset(None, "main")

    def test_registry_manifest_requires_descriptor_raw_and_image_identity_to_agree(self):
        image_id = "sha256:" + "c" * 64
        raw = json.dumps(
            {
                "schemaVersion": 2,
                "mediaType": "application/vnd.oci.image.manifest.v1+json",
                "config": {"digest": image_id},
                "layers": [],
            },
            separators=(",", ":"),
        ).encode()
        digest = "sha256:" + hashlib.sha256(raw).hexdigest()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            descriptor = root / "descriptor.json"
            manifest = root / "raw.json"
            descriptor.write_text(
                json.dumps(
                    {
                        "digest": digest,
                        "mediaType": "application/vnd.oci.image.manifest.v1+json",
                        "size": len(raw),
                    }
                ),
                encoding="utf-8",
            )
            manifest.write_bytes(raw)

            self.assertEqual(digest, verify_registry_manifest(descriptor, manifest, image_id))

            descriptor.write_text(
                json.dumps(
                    {
                        "digest": "sha256:" + "0" * 64,
                        "mediaType": "application/vnd.oci.image.manifest.v1+json",
                        "size": len(raw),
                    }
                ),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "do not match"):
                verify_registry_manifest(descriptor, manifest, image_id)

            descriptor.write_text("{}", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "no valid SHA-256"):
                verify_registry_manifest(descriptor, manifest, image_id)

            descriptor.write_text(
                json.dumps(
                    {
                        "digest": digest,
                        "mediaType": "application/vnd.oci.image.manifest.v1+json",
                        "size": len(raw),
                    }
                ),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "config digest"):
                verify_registry_manifest(descriptor, manifest, "sha256:" + "d" * 64)

            index = json.dumps(
                {
                    "schemaVersion": 2,
                    "mediaType": "application/vnd.oci.image.index.v1+json",
                    "manifests": [],
                },
                separators=(",", ":"),
            ).encode()
            manifest.write_bytes(index)
            descriptor.write_text(
                json.dumps(
                    {
                        "digest": "sha256:" + hashlib.sha256(index).hexdigest(),
                        "mediaType": "application/vnd.oci.image.index.v1+json",
                        "size": len(index),
                    }
                ),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "single-image manifest media type"):
                verify_registry_manifest(descriptor, manifest, image_id)

            manifest.write_bytes(b'{"schemaVersion":2,"schemaVersion":2}')
            with self.assertRaisesRegex(ValueError, "valid manifest JSON"):
                verify_registry_manifest(descriptor, manifest, image_id)

            descriptor.write_bytes(b"x" * (MAX_REGISTRY_METADATA_BYTES + 1))
            with self.assertRaisesRegex(ValueError, "fixed byte limit"):
                verify_registry_manifest(descriptor, manifest, image_id)
            descriptor.write_text("{}", encoding="utf-8")
            manifest.write_bytes(b"x" * (MAX_REGISTRY_METADATA_BYTES + 1))
            with self.assertRaisesRegex(ValueError, "fixed byte limit"):
                verify_registry_manifest(descriptor, manifest, image_id)


if __name__ == "__main__":
    unittest.main()
