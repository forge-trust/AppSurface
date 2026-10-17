#!/usr/bin/env python3
"""Inventory finished Aspire/DCP bundles and emit the private C3 Planner source."""

import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat


CASES = ("normal", "readiness", "factory-stall", "stuck-descendant")
ROLE = {
    "AppHost": "AppHost",
    "AppHostRuntimeConfiguration": "AppHostRuntimeConfiguration",
    "Resource": "Resource",
    "ResourceRuntimeConfiguration": "ResourceRuntimeConfiguration",
    "Dcp": "Dcp",
    "DcpExtension": "DcpExtension",
    "Dependency": "Dependency",
    "DeclaredInput": "DeclaredInput",
    "DependencyManifest": "DependencyManifest",
}


def require(condition, code):
    if not condition:
        raise RuntimeError(code)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("ascii")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def policy_for(case):
    profile = "native-http-" + case
    assertion = "appsurface/coverage/behavioral-patch@1"
    return {
        "Id": "issue779-c3-" + case,
        "Version": "1",
        "ConservativeProfileId": "conservative",
        "Profiles": [
            {
                "Id": profile,
                "Scope": "Targeted",
                "Resources": [{"Id": "native-http", "Readiness": "aspire_health", "DeadlineSeconds": 90, "Requires": []}],
                "Producers": [{
                    "Id": "coverage",
                    "Kind": "coverage",
                    "Version": "1.0.0",
                    "RequiredResources": ["native-http"],
                    "AssertionIds": [assertion],
                    "ArtifactSlots": [{
                        "LogicalName": "coverage-report",
                        "RelativeRoot": "merged",
                        "MediaType": "application/xml",
                        "Required": True,
                        "MaximumBytes": 20_971_520,
                    }],
                    "TimeoutSeconds": 180,
                    "CoverageGate": {
                        "MinLinePercent": 95,
                        "MinBranchPercent": 85,
                        "MinPatchLinePercent": None,
                        "MinPatchBranchPercent": None,
                        "PatchLineMode": "measurable",
                        "TolerancePercent": 0.5,
                    },
                }],
                "Obligations": [{
                    "Id": "application-readiness",
                    "RiskClass": "native-application",
                    "Rationale": "The selected native Aspire application must authenticate real resource readiness before coverage can run.",
                    "RequiredProducerIds": ["coverage"],
                    "RequiredAssertionId": assertion,
                }],
            },
            {
                "Id": "conservative",
                "Scope": "Release",
                "Resources": [{"Id": "build", "Readiness": "completion", "DeadlineSeconds": 1, "Requires": []}],
                "Producers": [],
                "Obligations": [],
            },
        ],
        "Rules": [{"Id": "source", "Pattern": "calculation/**", "ProfileId": profile}],
    }


def safe_relative(path):
    value = path.as_posix()
    return bool(value) and len(value) <= 256 and all(
        part and part not in (".", "..") and re.fullmatch(r"[A-Za-z0-9_.-]+", part)
        for part in value.split("/")
    )


def role_for(relative, executable):
    if relative == "inputs/declared-input.txt":
        return "DeclaredInput"
    if relative == "AspireChild.dll":
        return "AppHost"
    if relative == "AspireChild.runtimeconfig.json":
        return "AppHostRuntimeConfiguration"
    if relative == "resource/NativeHttpResource.dll":
        return "Resource"
    if relative == "resource/NativeHttpResource.runtimeconfig.json":
        return "ResourceRuntimeConfiguration"
    if relative == "dcp/dcp":
        return "Dcp"
    if relative.startswith("dcp/ext/") and executable:
        return "DcpExtension"
    if relative.endswith(".deps.json"):
        return "DependencyManifest"
    return "Dependency"


def inventory_bundle(bundle):
    expected = {
        "AspireChild.dll": "AppHost",
        "AspireChild.runtimeconfig.json": "AppHostRuntimeConfiguration",
        "resource/NativeHttpResource.dll": "Resource",
        "resource/NativeHttpResource.runtimeconfig.json": "ResourceRuntimeConfiguration",
        "dcp/dcp": "Dcp",
        "inputs/declared-input.txt": "DeclaredInput",
    }
    found = {}
    rows = []
    total = 0
    for path in sorted(bundle.rglob("*"), key=lambda item: item.as_posix()):
        value = path.lstat()
        if stat.S_ISDIR(value.st_mode):
            continue
        require(stat.S_ISREG(value.st_mode) and value.st_nlink == 1, "payload-nonregular-file")
        relative = path.relative_to(bundle).as_posix()
        require(safe_relative(path.relative_to(bundle)), "payload-path")
        body = path.read_bytes()
        require(0 < len(body) <= 128 * 1024 * 1024, "payload-file-size")
        executable = relative == "dcp/dcp" or (relative.startswith("dcp/ext/") and bool(value.st_mode & 0o111))
        role = role_for(relative, executable)
        mode = 0o555 if role in ("Dcp", "DcpExtension") else 0o444
        path.chmod(mode)
        rows.append({
            "relative_path": relative,
            "role": role,
            "length_bytes": len(body),
            "sha256": sha(body),
            "mode": mode,
        })
        total += len(body)
        if relative in expected:
            require(expected[relative] == role, "payload-required-role")
            found[relative] = True
    require(set(found) == set(expected), "payload-required-files")
    require(6 <= len(rows) <= 256 and total <= 512 * 1024 * 1024, "payload-bundle-bound")
    for directory in (bundle, *(p for p in bundle.rglob("*") if p.is_dir())):
        directory.chmod(0o555)
    dcp = (bundle / "dcp/dcp").read_bytes()
    require(dcp.startswith(b"\x7fELF"), "payload-dcp-not-elf")
    require((bundle / "inputs/declared-input.txt").read_bytes() == b"declared-native-input\n", "payload-declared-input")
    require(sum(row["role"] == "AppHost" for row in rows) == 1
        and sum(row["role"] == "AppHostRuntimeConfiguration" for row in rows) == 1
        and sum(row["role"] == "Resource" for row in rows) == 1
        and sum(row["role"] == "ResourceRuntimeConfiguration" for row in rows) == 1
        and sum(row["role"] == "Dcp" for row in rows) == 1
        and sum(row["role"] == "DeclaredInput" for row in rows) == 1, "payload-role-count")
    return rows


def csharp_string(value):
    return json.dumps(value, ensure_ascii=True)


def definition_source(case, rows, policy):
    slug = case
    identifier = "issue779-c3-" + slug
    bundle_id = "c3-" + sha(canonical(rows))[:32]
    policy_b64 = base64.b64encode(canonical(policy)).decode("ascii")
    lines = [
        "        new EvidenceClosedApplicationDefinition(",
        f"            {csharp_string(identifier)}, \"1\", {csharp_string(bundle_id)}, \"13.4.4\",",
        f"            EvidenceCanonicalJson.Deserialize<EvidencePolicy>(Convert.FromBase64String({csharp_string(policy_b64)})),",
        f"            {csharp_string('native-http-' + slug)},",
        "            Array.AsReadOnly(new EvidenceClosedResourceRegistration[]",
        "            { new(new EvidenceResourceDeclaration(\"native-http\", \"aspire_health\", 90, Array.Empty<string>()), \"native-http-uds\", \"1.0.0\", \"native-http\") }),",
        "            Array.AsReadOnly(new EvidenceClosedProducerRegistration[]",
        "            { new(new EvidenceProducerDeclaration(\"coverage\", \"coverage\", \"1.0.0\", Array.AsReadOnly(new[] { \"native-http\" }),",
        "                Array.AsReadOnly(new[] { \"appsurface/coverage/behavioral-patch@1\" }),",
        "                Array.AsReadOnly(new[] { new EvidenceArtifactSlot(\"coverage-report\", \"merged\", \"application/xml\", true, 20_971_520) }),",
        "                180, new EvidenceCoverageGateRequirements(95m, 85m, null, null, \"measurable\", 0.5m)), \"coverage\", \"1.0.0\") }),",
        "            Array.AsReadOnly(new EvidenceClosedBundleFile[]",
        "            {",
    ]
    for row in rows:
        lines.append(
            f"                new({csharp_string(row['relative_path'])}, EvidenceClosedBundleRole.{ROLE[row['role']]}, "
            f"{row['length_bytes']}L, {csharp_string(row['sha256'])}, 0x{row['mode']:03x}),"
        )
    lines.extend([
        "            }),",
        "            new EvidenceClosedApplicationCapabilities(Array.AsReadOnly(new[] { \"inputs/declared-input.txt\" }),",
        "                268_435_456, 1_073_741_824, 128, 1_048_576, 120, 5)),",
    ])
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--normal", type=Path, required=True)
    parser.add_argument("--readiness", type=Path, required=True)
    parser.add_argument("--factory-stall", type=Path, required=True)
    parser.add_argument("--stuck-descendant", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--registration-source", type=Path, required=True)
    args = parser.parse_args()
    require(not args.output_root.exists(), "preparation-output-exists")
    args.output_root.mkdir(mode=0o700, parents=True)
    app_root = args.output_root / "applications"
    policy_root = args.output_root / "policies"
    app_root.mkdir(mode=0o700)
    policy_root.mkdir(mode=0o700)
    publish_roots = {
        "normal": args.normal,
        "readiness": args.readiness,
        "factory-stall": args.factory_stall,
        "stuck-descendant": args.stuck_descendant,
    }
    definitions = []
    inventory = {}
    for case in CASES:
        source = publish_roots[case].resolve(strict=True)
        bundle = app_root / case
        require(source.is_dir() and not bundle.exists(), "payload-publish-directory")
        for path in (source, *source.rglob("*")):
            metadata = path.lstat()
            require(not stat.S_ISLNK(metadata.st_mode), "payload-source-symlink")
        shutil.copytree(source, bundle, symlinks=True)
        (bundle / "inputs").mkdir(mode=0o755)
        (bundle / "inputs/declared-input.txt").write_bytes(b"declared-native-input\n")
        (bundle / "inputs/declared-input.txt").chmod(0o444)
        rows = inventory_bundle(bundle)
        policy = policy_for(case)
        policy_path = policy_root / f"{case}.json"
        policy_bytes = canonical(policy)
        policy_path.write_bytes(policy_bytes)
        policy_path.chmod(0o444)
        inventory[case] = {
            "application_id": "issue779-c3-" + case,
            "application_path": str(bundle),
            "policy_path": str(policy_path),
            "policy_sha256": sha(policy_bytes),
            "bundle_files": rows,
        }
        definitions.append(definition_source(case, rows, policy))

    source = [
        "#if EVIDENCE_PRIVATE_C3",
        "using ForgeTrust.AppSurface.Evidence.Contracts;",
        "namespace ForgeTrust.AppSurface.Evidence.Planner;",
        "internal static class EvidencePrivateC3Registration",
        "{",
        "    internal static IReadOnlyList<EvidenceClosedApplicationDefinition> Definitions { get; } = Array.AsReadOnly(new EvidenceClosedApplicationDefinition[]",
        "    {",
        "\n".join(definitions),
        "    });",
        "    internal static EvidenceClosedApplicationDefinition Definition { get; } = Definitions.Single(static item => item.Id == \"issue779-c3-normal\");",
        "}",
        "internal static partial class EvidenceClosedApplicationCatalogue",
        "{",
        "    static partial void RegisterCompiledEntries(List<EvidenceClosedApplicationDefinition> entries) => entries.AddRange(EvidencePrivateC3Registration.Definitions);",
        "}",
        "#endif",
        "",
    ]
    generated = "\n".join(source).encode("utf-8")
    args.registration_source.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    args.registration_source.write_bytes(generated)
    args.registration_source.chmod(0o600)
    receipt = {
        "schema": "issue779-c3-preparation-input-v1",
        "registration_source_path": str(args.registration_source),
        "registration_source_sha256": sha(generated),
        "variants": inventory,
    }
    receipt_path = args.output_root / "payload-preparation.json"
    receipt_path.write_bytes(canonical(receipt))
    receipt_path.chmod(0o600)
    print(json.dumps({"registration_source_sha256": receipt["registration_source_sha256"],
        "variants": len(inventory), "payload_preparation": str(receipt_path)}))


if __name__ == "__main__":
    main()
