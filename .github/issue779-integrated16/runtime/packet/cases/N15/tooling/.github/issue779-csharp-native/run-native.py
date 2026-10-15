#!/usr/bin/env python3
"""Reviewed test orchestration only; the candidate C# image owns the live lifecycle."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import stat
import subprocess
import sys
import io
import tarfile
import time
import uuid
import types
from datetime import datetime, timedelta, timezone

NEGATIVE_CASE = 'N15'
N15_SOURCE_REVIEW_CLEAR = True
N15_FIXTURE_REVIEW_CLEAR = True
N15_ROOT_CLEANUP_REVIEW_CLEAR = True
SOURCE = '5b4ed381badb91de47d793715d05b6ae7737e39e'
PARENT = '0adef6032217b4181898a4d6d129e1248cb0eda3'
SOURCE_PARENT = '914046e1e998ca89a5f0f5be39a90bbfb88a431d'
SOURCE_MAP = '2c27553e3441cfc7e1c8b53cac0da15b6cff4a6c8c5c8d6dae01e0ba116351ea'
CAPTURE_SHA = '4ad23a2fdeccf9abbcd06724b9c97e3a4c1751ecb45e8321cc35bd79c036d540'
SOURCE_COUNT = 2872
SOURCE_TREE = '3bafd0fcd5321f1b29323f46df94b77eb48fa859'
PARSER_SHA = 'fa1fc74882dd5018d5e04128adf3ef30ae19570f4e1eee075eca2c9b57cfa877'
REVIEWED_FILES = {'acquire-inbound.sh': 'f33509ecffde6981a8cbe545cead1028753542a22405b86ce8d5752ef899c7ba', 'prepare-root-inputs-v2.sh': 'c623ba2ed0249fcbc5d647d802a68844b97ecb5edd0030829bbbef32a125f6b8', 'checkpoint-n15-pending-start-death.sh': 'e3ab3e07d0ef1c5887f171f5322b93b7cef3be5c3751c4a20974f8713ed61b24', 'check_n15_records.py': 'fa1fc74882dd5018d5e04128adf3ef30ae19570f4e1eee075eca2c9b57cfa877', 'observe_n15_pending_start.py': '0cb173f7bed9bcfc59410aa57cfa995d2ae308183dcc6d14494ec06f738c8922', 'n15-guard-reactivation-retry.sh': '9ab6bb3f3b96a284485ba7fb8eac9aa49a2b4e0f0bed4756ec9a17d166cad3c6', 'prepare-os-audit-v2.py': 'b345b2be77b359af23ade0b334caf76d7fdeb14bd2d4a3f0837beb21232df5e0', 'retain-native.sh': '6ba3f76b69e95eb1084e22f4745da1f28dd93bcf7b7a59ac27fe47b8597d461c', 'source-review.json': '446a5c08e2158a2ae17363925a96d1eecb86d518719746e0927626ee6b69dbf5', 'prepare-case-workspace.py': 'd970e2ab458a1cf6f8f3f5559ebaa15c81896aa513ce90975fb4b0d97dd99293', 'finalize_n15_observation.py': '086ec6d313ce7475724668045a81f0b0340866b27ddcfa84fd7438c1b3cd97cc'}
REVIEWED_FILES['finalize_n15_observation.py'] = '086ec6d313ce7475724668045a81f0b0340866b27ddcfa84fd7438c1b3cd97cc'
SOURCE_REVIEW_SHA = '446a5c08e2158a2ae17363925a96d1eecb86d518719746e0927626ee6b69dbf5'
ROOT_PREFIX = ['/usr/bin/sudo', '-n', '/usr/bin/env', '-i', 'PATH=/usr/bin:/usr/sbin',
               'LANG=C', 'LC_ALL=C', '/usr/bin/bash', '--noprofile', '--norc']
LOG_CAP = 40 * 1024 * 1024
ARCHIVE_CAP = 33619968
class Rejected(ValueError):
    def __init__(self, category):
        super().__init__('native-test-rejected')
        self.category = category


def require(value, category):
    if not value:
        raise Rejected(category)


def unique(rows):
    result = {}
    for key, value in rows:
        require(key not in result, 'duplicate-json')
        result[key] = value
    return result


def ident(s):
    return (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid, s.st_nlink,
            s.st_size, s.st_mtime_ns, s.st_ctime_ns)


def read(path, cap, deadline):
    require(time.monotonic() < deadline, 'read-deadline')
    before = path.lstat()
    require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and 0 <= before.st_size <= cap,
            'file-shape')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    chunks = []
    length = 0
    try:
        require(ident(before) == ident(os.fstat(fd)), 'opened-substitution')
        while True:
            require(time.monotonic() < deadline, 'read-deadline')
            data = os.read(fd, min(65536, cap + 1 - length))
            if not data:
                break
            chunks.append(data)
            length += len(data)
            require(length <= cap and length <= before.st_size, 'file-growth')
        require(length == before.st_size and ident(before) == ident(os.fstat(fd)) == ident(path.lstat()),
                'file-changed')
    finally:
        os.close(fd)
    require(time.monotonic() < deadline, 'read-deadline')
    return b''.join(chunks)


def save(path, value, deadline):
    data = value if isinstance(value, bytes) else (json.dumps(value, sort_keys=True, separators=(',', ':')) + '\n').encode()
    require(len(data) <= LOG_CAP and time.monotonic() < deadline, 'publication-bound')
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    try:
        pending = memoryview(data)
        while pending:
            require(time.monotonic() < deadline, 'publication-deadline')
            count = os.write(fd, pending)
            require(count > 0, 'publication-short-write')
            pending = pending[count:]
        os.fsync(fd)
    finally:
        os.close(fd)
    require(time.monotonic() < deadline, 'publication-deadline')


def validate_audit_failure_context(data):
    """Validate bounded diagnostic data only; it cannot change the failed audit result."""
    require(0 < len(data) <= 32768, 'diagnostic-bytes')
    value = decode(data)
    fields = {'schema', 'category', 'requester', 'soname', 'candidate_count',
              'runpath_count', 'cache_count', 'truncated', 'runpath_candidates',
              'cache_candidates', 'combined_candidates'}
    require(isinstance(value, dict) and set(value) == fields
            and value['schema'] == 'issue779-os-audit-dependency-failure-context-v1'
            and value['category'] == 'dependency-unresolved-or-ambiguous', 'diagnostic-schema')
    def path_data(path):
        require(isinstance(path, str) and path.startswith('/') and path != '/'
                and len(path.encode('utf-8')) <= 4096
                and all(ord(c) >= 32 and ord(c) != 127 for c in path)
                and all(part not in ('', '.', '..') for part in path[1:].split('/')), 'diagnostic-path')
    path_data(value['requester'])
    require(isinstance(value['soname'], str)
            and re.fullmatch(r'[A-Za-z0-9_.+-]{1,256}', value['soname']), 'diagnostic-soname')
    counts = []
    for count_name, rows_name in (('runpath_count', 'runpath_candidates'),
                                 ('cache_count', 'cache_candidates'),
                                 ('candidate_count', 'combined_candidates')):
        count, rows = value[count_name], value[rows_name]
        require(type(count) is int and 0 <= count <= 4096 and isinstance(rows, list)
                and len(rows) == min(count, 8), 'diagnostic-count')
        for path in rows:
            path_data(path)
        counts.append(count)
    require(type(value['truncated']) is bool and value['truncated'] == any(c > 8 for c in counts)
            and value['candidate_count'] != 1, 'diagnostic-truncation-or-not-rejected')
    return value


def retain_audit_failure_context(source, destination, deadline):
    """Copy only the optional fixed sidecar as private data; original audit failure remains fatal."""
    require(time.monotonic() < deadline, 'diagnostic-deadline')
    parent = source.parent.lstat()
    require(stat.S_ISDIR(parent.st_mode) and parent.st_uid == os.geteuid()
            and parent.st_gid == os.getegid() and stat.S_IMODE(parent.st_mode) == 0o700,
            'diagnostic-parent')
    before = source.lstat()
    require(stat.S_ISREG(before.st_mode) and before.st_uid == os.geteuid()
            and before.st_gid == os.getegid() and before.st_nlink == 1
            and stat.S_IMODE(before.st_mode) == 0o600 and 0 < before.st_size <= 32768,
            'diagnostic-file')
    data = read(source, 32768, deadline)
    validate_audit_failure_context(data)
    require(ident(before) == ident(source.lstat()) and ident(parent) == ident(source.parent.lstat()),
            'diagnostic-substitution')
    save(destination, data, deadline)
    after = destination.lstat()
    require(stat.S_ISREG(after.st_mode) and after.st_uid == os.geteuid()
            and after.st_gid == os.getegid() and after.st_nlink == 1
            and stat.S_IMODE(after.st_mode) == 0o600 and after.st_size == len(data),
            'diagnostic-copy')
    require(time.monotonic() < deadline, 'diagnostic-deadline')
    return {'file': destination.name, 'bytes': len(data), 'sha256': sha(data)}


def decode(data):
    return json.loads(data.decode('utf-8'), object_pairs_hook=unique)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def root_deadline(end):
    # /proc/uptime is CLOCK_BOOTTIME, which must not be assumed equal to Python's
    # CLOCK_MONOTONIC across a host suspend. Both are bounded by the original outer end.
    before = time.monotonic()
    with open('/proc/uptime', encoding='ascii') as source:
        stamp = source.read(128).split()[0]
    require(re.fullmatch(r'[0-9]+\.[0-9]+', stamp), 'root-clock-shape')
    require(time.monotonic() < end, 'root-clock-expiry')
    return str(int(float(stamp) * 1000) + max(0, int((end - time.monotonic()) * 1000) - 20))






# Byte-identical common prerequisite contract; no native acceptance authority.
INTEGRATED_BUILD_SOURCE = '"""Pure build prerequisites shared by the image builder and native case readers.\n\nThis module performs no I/O and issues no native credit. All fourteen images\nmust carry actual passing source-selected core and CLI test results. Commands\nare checked by their operation, project and selector, not an incidental count\nof unrelated Git/package queries. Runtime case acceptance remains separate.\n"""\n\nimport pathlib\nimport re\n\nSCHEMA = "issue779-integrated16-build-v1"\nSELECTORS = ("None", *(f"N{i:02d}" for i in range(4, 17)))\nPIPE_METHOD = "OwnedRawPipeReadWrappingUsesHandleModeAndJoinsBothEofs"\nFRAME_METHOD = "OwnerDeathVariantIsExclusiveAndFrameIsExactBoundedData"\nCLI_METHOD = "CompiledImageOwnsOnlyItsSelectedCheckpoint"\nCORE_PROJECT = "Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/ForgeTrust.AppSurface.Evidence.Supervision.Tests.csproj"\nCLI_PROJECT = "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"\nCLI_TEST_PROJECT = "Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj"\nCORE_CLASS = "ForgeTrust.AppSurface.Evidence.Supervision.Tests."\nCLI_CLASS = "ForgeTrust.AppSurface.Cli.Tests.EvidenceNativeQualificationTests"\nSOURCE_PHASES = ("source_before", "source_after_assets", "source_after_build", "source_final")\n\n\nclass BuildContractError(ValueError):\n    """Closed metadata rejection; never include caller values in its message."""\n\n\ndef require(ok, category):\n    if not ok:\n        raise BuildContractError(category)\n\n\ndef is_sha(value):\n    return type(value) is str and re.fullmatch("[0-9a-f]{64}", value) is not None\n\n\ndef expected_regressions(selector):\n    require(selector in SELECTORS, "build-selector")\n    if selector in ("N13", "N14"):\n        return (("owner_death_phase_frame_regression", CORE_CLASS + "LinuxOwnerDeathPhaseFrameTests", FRAME_METHOD),\n                ("owner_death_cli_qualification", CLI_CLASS, CLI_METHOD))\n    return (("linux_pipe_regression", CORE_CLASS + "LinuxOutputPipesTests", PIPE_METHOD),\n            ("compiled_cli_qualification", CLI_CLASS, CLI_METHOD))\n\n\ndef validate_command_records(commands, selector):\n    require(selector in SELECTORS, "build-selector")\n    require(type(commands) is list and 0 < len(commands) <= 128, "build-command-bound")\n    for ordinal, row in enumerate(commands):\n        require(type(row) is dict and type(row.get("ordinal")) is int and row["ordinal"] == ordinal,\n                "build-command-ordinal")\n        args = row.get("argv")\n        require(type(args) is list and args and all(type(arg) is str and arg for arg in args),\n                "build-command-arguments")\n        # MSBuild accepts /p:, -property:, case aliases and multi-property\n        # arguments. A second selector in any such form cannot be ignored.\n        # Response files are also forbidden: they can supply unseen properties.\n        canonical = "-p:EvidencePrivateQualification=" + selector\n        require(not any(arg.startswith("@") for arg in args)\n                and all("evidenceprivatequalification" not in arg.lower()\n                        or (selector != "None" and arg == canonical) for arg in args),\n                "build-selector-alias-or-response-file")\n        require(type(row.get("exit")) is int and row["exit"] == 0 and row.get("failure") is None\n                and row.get("waited") is True and row.get("group_absent") is True\n                and row.get("forced_cleanup") is False and row.get("timed_out") is False\n                and row.get("late_completion") is not True, "build-command-terminal")\n        for name, expected in (("pipe_eofs", [True, True]), ("read_errors", [False, False]),\n                               ("write_errors", [False, False]), ("discarded_bytes", [0, 0])):\n            values = row.get(name)\n            require(type(values) is list and len(values) == 2\n                    and all(type(actual) is type(wanted) and actual == wanted\n                            for actual, wanted in zip(values, expected)), "build-command-output")\n        logs = row.get("logs")\n        require(type(logs) is list and len(logs) == 2\n                and all(type(log) is dict and is_sha(log.get("sha256"))\n                        and type(log.get("bytes")) is int and 0 <= log["bytes"] <= 8 * 1024 * 1024\n                        for log in logs), "build-command-logs")\n    selected = [row for row in commands if pathlib.PurePosixPath(row["argv"][0]).name == "dotnet"\n                and len(row["argv"]) > 1 and row["argv"][1] in ("restore", "publish", "test", "build")]\n    require([row["argv"][1] for row in selected] == ["restore", "publish", "restore", "test", "restore", "test"],\n            "build-operation-sequence")\n    projects = (CLI_PROJECT, CLI_PROJECT, CORE_PROJECT, CORE_PROJECT, CLI_TEST_PROJECT, CLI_TEST_PROJECT)\n    for row, project in zip(selected, projects):\n        args = row["argv"]\n        require(len(args) > 2 and args[2].endswith("/" + project), "build-project")\n        flags = [arg for arg in args if arg.startswith("-p:EvidencePrivateQualification=")]\n        require(flags == ([] if selector == "None" else ["-p:EvidencePrivateQualification=" + selector]),\n                "build-command-selector")\n        require(("--locked-mode" in args) if args[1] == "restore" else ("--no-restore" in args),\n                "build-restore-mode")\n        if args[1] in ("test", "publish"):\n            require("-p:UseSharedCompilation=false" in args, "build-compiler-mode")\n    for row, (_, class_name, method) in zip((selected[3], selected[5]), expected_regressions(selector)):\n        args = row["argv"]\n        require(args.count("--filter") == 1 and args[args.index("--filter") + 1]\n                == "FullyQualifiedName=" + class_name + "." + method, "build-test-filter")\n    require(sum(arg.startswith("-p:EvidencePrivateQualification=") for row in commands for arg in row["argv"])\n            == (0 if selector == "None" else 6), "build-selector-cardinality")\n    return True\n\n\ndef validate_build_receipt(receipt, selector, source):\n    """Validate a measured receipt against the independently pinned source tuple.\n\n    ``source`` has commit, parent, tree, count, capture, projection, map and\n    raw_map fields. It comes from the immutable common capture, never from the\n    receipt being validated. File/archive checks remain with the I/O caller.\n    """\n    require(selector in SELECTORS and type(receipt) is dict and type(source) is dict, "build-receipt-shape")\n    require(set(source) == {"commit", "parent", "tree", "count", "capture", "projection", "map", "raw_map"},\n            "build-source-binding-shape")\n    require(receipt.get("schema") == SCHEMA and type(receipt.get("exit")) is int and receipt["exit"] == 0\n            and receipt.get("failure") is None and receipt.get("diagnostics") == [], "build-receipt-terminal")\n    require(receipt.get("private_qualification") == selector and receipt.get("selected_variant") == selector,\n            "build-receipt-selector")\n    for field, binding in (("source_commit", "commit"), ("source_revision", "commit"), ("source_parent", "parent"),\n                           ("source_count", "count"), ("capture_sha256", "capture"),\n                           ("source_capture_sha256", "capture"), ("capture_input_sha256", "capture"),\n                           ("capture_projection_sha256", "projection"), ("source_map_sha256", "map"),\n                           ("source_capture_map_sha256", "raw_map"), ("harness_parent", "commit"),\n                           ("harness_retry_parent", "commit"), ("harness_retry_parent_parent", "parent")):\n        require(type(receipt.get(field)) is type(source[binding]) and receipt[field] == source[binding],\n                "build-receipt-source")\n    require(receipt.get("build_prerequisite_only") is True and receipt.get("test_execution") is True\n            and all(receipt.get(field) is False for field in ("native_execution", "native_acceptance", "checkpoint_pass",\n                                                            "review_clear", "source_review_clear", "fixture_review_clear",\n                                                            "root_transport_review_clear"))\n            and receipt.get("os_audit") is None, "build-receipt-scope")\n    validate_command_records(receipt.get("commands"), selector)\n    for phase in SOURCE_PHASES:\n        fact = receipt.get(phase)\n        require(type(fact) is dict and fact.get("head") == source["commit"] and fact.get("tree") == source["tree"]\n                and type(fact.get("count")) is int and fact["count"] == source["count"]\n                and fact.get("source_map_sha256") == source["map"]\n                and all(fact.get(key) is True for key in ("index_tree_matches", "physical_git_sha1", "physical_sha256_modes")),\n                "build-source-recheck")\n    for key, class_name, method in expected_regressions(selector):\n        fact = receipt.get(key)\n        require(type(fact) is dict and fact.get("class_name") == class_name and fact.get("method") == method\n                and fact.get("native_acceptance") is False and fact.get("root_factory_exercised") is False\n                and fact.get("unprivileged_library_behavior_only") is True\n                and is_sha(fact.get("trx_sha256")), "build-regression-binding")\n        counters = fact.get("counters")\n        require(type(counters) is dict and all(counters.get(key) == value for key, value in\n                {"total": "1", "executed": "1", "passed": "1", "failed": "0", "error": "0",\n                 "notExecuted": "0", "timeout": "0", "aborted": "0"}.items()), "build-regression-outcome")\n    known = {"owner_death_phase_frame_regression", "owner_death_cli_qualification", "linux_pipe_regression",\n             "compiled_cli_qualification"}\n    expected = {row[0] for row in expected_regressions(selector)}\n    require(not (known - expected) & set(receipt), "build-regression-foreign-selection")\n    return True\n'
INTEGRATED_BUILD_SOURCE_SHA = '4e1e774f57d1b878a0985ac218a27490ae4de8da1f21f81cd696dbe6e9c399f8'
INTEGRATED_SOURCE_BINDING = {'commit': '5b4ed381badb91de47d793715d05b6ae7737e39e', 'parent': '914046e1e998ca89a5f0f5be39a90bbfb88a431d', 'tree': '3bafd0fcd5321f1b29323f46df94b77eb48fa859', 'count': 2872, 'capture': '4ad23a2fdeccf9abbcd06724b9c97e3a4c1751ecb45e8321cc35bd79c036d540', 'projection': '488b59a9bd50ef1f05f8eb4291ed3eee164b6d1536cdeec73fe9f6847194100c', 'map': '758a0f769ca67ce6f1fda2eea3569a373efafb192e2d2c2cce2224809b352a7d', 'raw_map': '2c27553e3441cfc7e1c8b53cac0da15b6cff4a6c8c5c8d6dae01e0ba116351ea'}
_integrated_build_scope = {}
if hashlib.sha256(INTEGRATED_BUILD_SOURCE.encode()).hexdigest() != INTEGRATED_BUILD_SOURCE_SHA:
    raise ValueError('integrated-build-contract-pin')
exec(compile(INTEGRATED_BUILD_SOURCE, '<pinned-pure-build-contract>', 'exec'), _integrated_build_scope)
def validate_integrated_build(receipt, selector):
    _integrated_build_scope['validate_build_receipt'](receipt, selector, INTEGRATED_SOURCE_BINDING)
    validate_integrated_artifact_metadata(receipt)
def validate_integrated_artifact_metadata(receipt):
    artifacts = receipt.get('artifacts')
    check = _integrated_build_scope['require']
    check(all(name not in receipt or receipt[name] is False for name in
              ('runtime_acceptance', 'trusted_enabled', 'native_credit', 'all16_final_integrated_native_verified')),
          'build-supplemental-acceptance-claim')
    check(type(artifacts) is dict and set(artifacts) == {'source', 'tool', 'runtime'}, 'build-artifact-roles')
    for role, fact in artifacts.items():
        check(type(fact) is dict and _integrated_build_scope['is_sha'](fact.get('tsv_sha256'))
              and _integrated_build_scope['is_sha'](fact.get('nodes_sha256')), 'build-artifact-digests')
        check(type(fact.get('file_count')) is int and 0 < fact['file_count'] <= 8192
              and type(fact.get('node_count')) is int and fact['file_count'] <= fact['node_count'] <= 8192
              and type(fact.get('total_bytes')) is int and 0 <= fact['total_bytes'] <= 1073741824,
              'build-artifact-counts')
    check(artifacts['source']['file_count'] == INTEGRATED_SOURCE_BINDING['count'], 'build-artifact-source-count')


def validate_build(receipt, maps):
    validate_integrated_build(receipt, 'N15')
    pipe = receipt.get('linux_pipe_regression')
    require(type(pipe) is dict and set(pipe) == {'method', 'class_name', 'counters', 'trx_sha256', 'unprivileged_library_behavior_only', 'root_factory_exercised', 'native_acceptance', 'runtime_basis'} and pipe.get('runtime_basis') == 'selected SDK dotnet host', 'pipe-regression-shape')
    require(set(receipt.get('artifacts',{}))=={'source','tool','runtime'}, 'build-map-set')
    for name,fact in receipt['artifacts'].items():
        tsv,raw=maps[name]
        require(sha(tsv)==fact.get('tsv_sha256') and sha(raw)==fact.get('nodes_sha256'), 'build-map-digest')
        nodes=decode(raw)
        require(set(nodes)=={'schema','root_name','files','directories'} and nodes['schema']=='issue779-build-node-inventory-v1' and nodes['root_name']==name, 'node-schema')
        require(len(nodes['files'])==fact.get('file_count') and len(nodes['files'])+len(nodes['directories'])==fact.get('node_count') and sum(x['bytes'] for x in nodes['files'].values())==fact.get('total_bytes'), 'node-counts')
    require(receipt['artifacts']['source']['file_count']==2872, 'source-count')

def absent(pid):
    try:
        os.killpg(pid, 0)
        return False
    except ProcessLookupError:
        return True
    except PermissionError:
        # Existence without signal permission is not absence and must not bypass cleanup.
        return False


UTILITY_SETTLED = r'''
set -euo pipefail
[[ $1 =~ ^issue779-native-tool-[0-9a-f]{32}-[0-9]{2}\.service$ ]]
unit=$1; group=/sys/fs/cgroup/system.slice/$unit
[[ ! -L /sys/fs/cgroup && ! -L /sys/fs/cgroup/system.slice && ! -L $group ]]
for checked in "$unit" "${unit%.service}-deadline.service" "${unit%.service}-deadline.timer"; do
# v255 show-properties returns zero for complete inactive/not-found facts;
# unlike status/help, no nonzero not-found code is a successful query here.
query_status=0
facts=$(/usr/bin/systemctl show --property=LoadState,ActiveState,SubState,Job,MainPID,ControlGroup "$checked") || query_status=$?
((query_status==0))
unset values; declare -A values=()
while IFS='=' read -r key value; do
 [[ $key == LoadState || $key == ActiveState || $key == SubState || $key == Job || $key == MainPID || $key == ControlGroup ]]
 [[ ! ${values[$key]+present} ]]; values[$key]=$value
done <<<"$facts"
[[ ( ${#values[@]} == 6 || ( $checked == *.timer && ${#values[@]} == 4 ) ) && ( ${values[LoadState]} == loaded || ${values[LoadState]} == not-found ) ]]
[[ ( ${values[ActiveState]} == inactive || ${values[ActiveState]} == failed ) && ( ${values[SubState]} == dead || ${values[SubState]} == failed ) ]]
[[ ( ${values[MainPID]-0} == 0 ) && ( -z ${values[Job]} || ${values[Job]} == 0 ) ]]
[[ -z ${values[ControlGroup]-} || ${values[ControlGroup]} == /system.slice/$checked ]]
group=/sys/fs/cgroup/system.slice/$checked
if [[ -e $group ]]; then
 [[ -d $group && $(/usr/bin/stat -f -c %t -- "$group") == 63677270 ]]
 /usr/bin/grep -qx 'populated 0' "$group/cgroup.events"
fi
done
printf 'ROOT_TOOL_CGROUP_SETTLED\n'
'''

GUARD_CREATE = r'''
set -euo pipefail; umask 077
[[ $1 =~ ^issue779-native-tool-([0-9a-f]{32})-([0-9]{2})\.service$ ]]
base=/run/issue779-native-tool-guards-${BASH_REMATCH[1]}; child=$base/${BASH_REMATCH[2]}
[[ ! -L $base ]]
if [[ ! -e $base ]]; then /usr/bin/mkdir -m0700 -- "$base"; fi
[[ $(/usr/bin/stat -c '%u:%g:%a' -- "$base") == 0:0:700 && ! -e $child && ! -L $child ]]
/usr/bin/mkdir -m0700 -- "$child"
printf 'armed\n' >"$child/armed"
[[ $(/usr/bin/stat -c '%u:%g:%a:%h' -- "$child/armed") == 0:0:600:1 ]]
[[ $2 =~ ^[0-9]+\.[0-9]+$ ]]
/usr/bin/systemd-run --quiet --unit="${1%.service}-deadline" --on-boot="$2" \
 --timer-property=AccuracySec=1us --timer-property=RandomizedDelaySec=0 \
 --property=Type=oneshot --property=TimeoutStartSec=1 --property=TimeoutStopSec=1 \
 --property=KillMode=control-group --property=RemainAfterExit=no \
 /usr/bin/systemctl kill --kill-whom=all --signal=KILL "$1"
timer=${1%.service}-deadline.timer; escaped=${timer//-/_2d}; escaped=${escaped//./_2e}
target=$(/usr/bin/busctl --system get-property org.freedesktop.systemd1 "/org/freedesktop/systemd1/unit/$escaped" org.freedesktop.systemd1.Timer NextElapseUSecMonotonic)
[[ $target =~ ^t\ ([1-9][0-9]*)$ ]]; actual=${BASH_REMATCH[1]}
whole=${2%%.*}; fraction=${2#*.}; [[ ${#fraction} == 6 ]]
expected=$((10#$whole*1000000+10#$fraction))
((actual>=expected-1 && actual<=expected+1))
'''

GUARD_CLOSE = r'''
set -euo pipefail
[[ $1 =~ ^issue779-native-tool-([0-9a-f]{32})-([0-9]{2})\.service$ ]]
base=/run/issue779-native-tool-guards-${BASH_REMATCH[1]}; child=$base/${BASH_REMATCH[2]}
[[ ! -L $base && ! -L $child && $(/usr/bin/stat -c '%u:%g:%a' -- "$base" "$child") == $'0:0:700\n0:0:700' ]]
[[ ! -L $child/armed ]]
if [[ -e $child/armed ]]; then
 [[ $(/usr/bin/stat -c '%u:%g:%a:%h' -- "$child/armed") == 0:0:600:1 ]]
 /usr/bin/rm -- "$child/armed"
fi
[[ ! -e $child/armed && ! -L $child/armed ]]
# Missing/GC units may make stop nonzero; subsequent exact state/job/cgroup checks decide settlement.
/usr/bin/systemctl stop --no-block "$1" "${1%.service}-deadline.timer" "${1%.service}-deadline.service" >/dev/null 2>&1 || :
'''

ABSOLUTE_ROOT_EXEC = r'''
set -euo pipefail
diagnostic_stage=guard
trap 'code=$?; if ((code!=0)); then printf "ROOT_TOOL_FAILURE:%s:%d\n" "$diagnostic_stage" "$code" >&2; fi' EXIT
[[ $1 =~ ^[1-9][0-9]{0,14}$ && $2 =~ ^issue779-native-tool-([0-9a-f]{32})-([0-9]{2})\.service$ ]] || exit 1
end=$1; unit=$2; guard=/run/issue779-native-tool-guards-${BASH_REMATCH[1]}/${BASH_REMATCH[2]}/armed; shift 2
now_ms() { local value other whole fraction; IFS=' ' read -r value other </proc/uptime; whole=${value%%.*}; fraction=${value#*.}000; printf '%s' "$((10#$whole*1000+10#${fraction:0:3}))"; }
check_guard() { [[ -f $guard && ! -L $guard && $(/usr/bin/stat -c '%u:%g:%a:%h' -- "$guard") == 0:0:600:1 ]]; }
check_guard; diagnostic_stage=clock; now=$(now_ms); ((now<end)); left=$((end-now)); printf -v seconds '%d.%03d' "$((left/1000))" "$((left%1000))"
check_guard; now=$(now_ms); ((now<end)); left=$((end-now)); printf -v seconds '%d.%03d' "$((left/1000))" "$((left%1000))"
diagnostic_stage=exec
printf 'ROOT_TOOL_PREEXEC\n' >&2
exec /usr/bin/timeout --signal=KILL "$seconds" /usr/bin/bash --noprofile --norc "$@"
'''


class Runner:
    def __init__(self, output, deadline):
        self.output = output
        self.deadline = deadline
        self.records = []
        self.total_bytes = 0
        self.phase = 'preflight'
        self.generation = uuid.uuid4().hex

    def root_metadata(self, source, unit, end, extra=()):
        left = end - time.monotonic()
        require(left > 0, 'root-metadata-deadline')
        value = subprocess.run(['/usr/bin/sudo', '-n', '/usr/bin/timeout', '--signal=KILL', str(left),
            '/usr/bin/env', '-i', 'PATH=/usr/bin:/usr/sbin', 'LANG=C', 'LC_ALL=C', '/usr/bin/bash',
            '--noprofile', '--norc', '-c', source, '--', unit, *extra], stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=left,
            env={'PATH': '/usr/bin:/usr/sbin', 'LANG': 'C', 'LC_ALL': 'C'})
        require(value.returncode == 0 and len(value.stdout) <= 4096 and time.monotonic() < end, 'root-metadata-terminal')
        return value.stdout

    def run(self, argv, deadline, allow_failure=False):
        deadline = min(deadline, self.deadline - 5)
        require(time.monotonic() < deadline, 'command-preflight-deadline')
        root_operation = argv[:len(ROOT_PREFIX)] == ROOT_PREFIX
        unit = 'issue779-native-tool-' + self.generation + '-' + f'{len(self.records):02d}' + '.service' if root_operation else None
        row = {'ordinal': len(self.records), 'phase': self.phase, 'argv': argv, 'pid': None, 'exit': None, 'root_utility_unit': unit,
               'waited': False, 'group_absent': False, 'forced_cleanup': False, 'timed_out': False, 'failure': None}
        self.records.append(row)
        logs = [self.output / (f'{row["ordinal"]:02d}-' + name + '.log') for name in ('stdout', 'stderr')]
        handles, process, error = [], None, None
        started = time.monotonic()
        cleanup_end = deadline
        work_end = deadline - 5
        require(started < work_end, 'phase-cleanup-reserve')
        try:
            for path in logs:
                # Diagnostic streams are private from creation, independent of caller umask.
                fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
                try:
                    handles.append(os.fdopen(fd, 'wb', buffering=0))
                except BaseException:
                    os.close(fd)
                    raise
            selected = argv
            if root_operation:
                row['root_start_guard_created'] = True
                self.root_metadata(GUARD_CREATE, unit, work_end, (format(work_end, '.6f'),))
                remaining = work_end - time.monotonic()
                require(remaining > 0, 'root-operation-deadline')
                # systemd owns every ordinary/setsid descendant in the utility cgroup.
                # An independent ROOT timeout also bounds the --wait/--pipe client.
                selected = ['/usr/bin/sudo', '-n', '/usr/bin/env', '-i', 'PATH=/usr/bin:/usr/sbin', 'LANG=C', 'LC_ALL=C',
                    '/usr/bin/timeout', '--signal=KILL', str(max(.001, cleanup_end - time.monotonic() - .5)),
                    '/usr/bin/systemd-run', '--quiet', '--wait', '--pipe', '--expand-environment=no', '--unit=' + unit,
                    '--property=Type=exec', '--property=User=0', '--property=Group=0',
                    '--property=KillMode=control-group', '--property=Restart=no', '--property=RemainAfterExit=no',
                    '--property=ConditionPathExists=/run/issue779-native-tool-guards-' + self.generation + '/' + f'{row["ordinal"]:02d}' + '/armed',
                    '--property=RuntimeMaxSec=' + str(remaining), '--property=TimeoutStopSec=3',
                    '--property=SendSIGKILL=yes', '--property=FinalKillSignal=9',
                    '/usr/bin/env', '-i', 'PATH=/usr/bin:/usr/sbin', 'LANG=C', 'LC_ALL=C',
                    '/usr/bin/bash', '--noprofile', '--norc', '-c', ABSOLUTE_ROOT_EXEC, '--', root_deadline(work_end), unit] + argv[len(ROOT_PREFIX):]
            row['dispatched_argv'] = selected
            process = subprocess.Popen(selected, stdin=subprocess.DEVNULL, stdout=handles[0], stderr=handles[1],
                                       start_new_session=True, env={'PATH': '/usr/bin:/usr/sbin', 'LANG': 'C', 'LC_ALL': 'C'}, umask=0o077)
            row['pid'] = process.pid
            while True:
                require(all(path.stat().st_size <= LOG_CAP for path in logs)
                        and self.total_bytes + sum(path.stat().st_size for path in logs) <= 80 * 1024 * 1024,
                        'command-log-bound')
                if time.monotonic() >= work_end:
                    row['timed_out'] = True
                    raise Rejected('command-deadline')
                if process.poll() is not None:
                    row['exit'] = process.wait()
                    row['waited'] = True
                    require(absent(process.pid), 'command-group-survived')
                    break
                time.sleep(min(.02, max(0, deadline - time.monotonic())))
        except BaseException as caught:
            error = caught
        finally:
            root_close_failed = False
            if root_operation and row.get('root_start_guard_created'):
                try:
                    self.root_metadata(GUARD_CLOSE, unit, cleanup_end)
                    row['root_start_guard_closed'] = True
                except BaseException as caught:
                    error = error or caught
                    root_close_failed = True
            if process is not None:
                try:
                    unsettled = process.poll() is None or not absent(process.pid)
                except BaseException as caught:
                    error = error or caught
                    unsettled = True
                root_ambiguous = root_operation and (error is not None or row['exit'] != 0 or not row['waited'] or unsettled or root_close_failed)
                if unsettled or root_ambiguous:
                    row['forced_cleanup'] = True
                    try:
                        if root_operation:
                            left = max(.001, cleanup_end - time.monotonic())
                            stop = subprocess.run(['/usr/bin/sudo', '-n', '/usr/bin/timeout', '--signal=KILL', str(left),
                                '/usr/bin/systemctl', 'stop', '--no-block', unit], stdin=subprocess.DEVNULL,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=left,
                                env={'PATH': '/usr/bin:/usr/sbin', 'LANG': 'C', 'LC_ALL': 'C'})
                            require(stop.returncode == 0, 'root-utility-stop-request')
                        else:
                            os.killpg(process.pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                    except BaseException as caught:
                        error = error or caught
                try:
                    process.wait(timeout=max(.001, cleanup_end - time.monotonic()))
                    row['exit'], row['waited'] = process.returncode, True
                    while not absent(process.pid) and time.monotonic() < cleanup_end:
                        time.sleep(.01)
                    row['group_absent'] = absent(process.pid)
                    require(row['group_absent'], 'command-group-unjoined')
                except BaseException as caught:
                    error = error or caught
            if root_operation and row.get('root_start_guard_created'):
                try:
                    left = max(.001, cleanup_end - time.monotonic())
                    inspect = subprocess.run(['/usr/bin/sudo', '-n', '/usr/bin/timeout', '--signal=KILL', str(left),
                        '/usr/bin/env', '-i', 'PATH=/usr/bin:/usr/sbin', 'LANG=C', 'LC_ALL=C',
                        '/usr/bin/bash', '--noprofile', '--norc', '-c', UTILITY_SETTLED, '--', unit],
                        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=left,
                        env={'PATH': '/usr/bin:/usr/sbin', 'LANG': 'C', 'LC_ALL': 'C'})
                    row['root_utility_cgroup_empty'] = inspect.returncode == 0 and inspect.stdout == b'ROOT_TOOL_CGROUP_SETTLED\n'
                    require(row['root_utility_cgroup_empty'], 'root-utility-cgroup-unjoined')
                except BaseException as caught:
                    error = error or caught
            for handle in handles:
                try:
                    handle.close()
                except OSError as caught:
                    error = error or caught
            row['logs'] = []
            for path in logs:
                try:
                    data = read(path, LOG_CAP, cleanup_end)
                    row['logs'].append({'name': path.name, 'bytes': len(data), 'sha256': sha(data)})
                    self.total_bytes += len(data)
                except BaseException as caught:
                    error = error or caught
            if time.monotonic() >= cleanup_end:
                error = error or Rejected('late-command-completion')
            row['elapsed_seconds'] = time.monotonic() - started
            row['failure'] = type(error).__name__ if error else None
            try:
                save(self.output / f'command-{row["ordinal"]:02d}.json', row, cleanup_end)
            except BaseException as caught:
                error = error or caught
            if time.monotonic() >= cleanup_end:
                error = error or Rejected('late-phase-record-publication')
        if error:
            raise error
        require(not row['forced_cleanup'] and row['group_absent'] and row['waited'], 'command-not-settled')
        require(allow_failure or row['exit'] == 0, 'command-nonzero')
        data = read(logs[0], LOG_CAP, cleanup_end)
        require(time.monotonic() < cleanup_end, 'late-stdout-return')
        return row['exit'], data


BOOTSTRAP = r'''
set -euo pipefail; umask 077
diagnostic_stage=base
trap 'code=$?; if ((code!=0)); then printf "ROOT_BOOTSTRAP_FAILURE:%s:%d\n" "$diagnostic_stage" "$code" >&2; fi' EXIT
[[ $1 =~ ^[0-9a-f]{32}$ ]] || exit 1
target=/var/lib/appsurface-evidence-bootstrap-$1; shift
for parent in / /var /var/lib; do
 [[ -d $parent && ! -L $parent && $(/usr/bin/stat -c '%u:%g' -- "$parent") == 0:0 ]]
 (( (8#$(/usr/bin/stat -c %a -- "$parent") & 0022)==0 ))
done
[[ ! -e $target && ! -L $target ]]
/usr/bin/mkdir -m0700 -- "$target"
while (($#)); do
 diagnostic_stage=source-shape
 source=$1; name=$2; hash=$3; shift 3
 [[ $name =~ ^[a-zA-Z0-9._-]+$ && $name != . && $name != .. && ${#name} -le 255 && $hash =~ ^[0-9a-f]{64}$ && -f $source && ! -L $source ]]
 cap=131072; [[ $name != capture-receipt.json ]] || cap=2097152
 [[ $(/usr/bin/stat -c %h -- "$source") == 1 && $(/usr/bin/stat -c %s -- "$source") -le $cap ]]
 diagnostic_stage=source-digest
 printf '%s  %s\n' "$hash" "$source" | /usr/bin/sha256sum --check --strict --status
 diagnostic_stage=copy
 /usr/bin/dd if="$source" of="$target/$name" iflag=nofollow,nonblock,count_bytes oflag=nofollow conv=fsync,excl count=$((cap+1)) status=none
 diagnostic_stage=seal
 /usr/bin/chown 0:0 -- "$target/$name"
 mode=0444; [[ $name != acquire-inbound.sh && $name != retain-native.sh ]] || mode=0500
 /usr/bin/chmod "$mode" -- "$target/$name"
 diagnostic_stage=destination-digest
 printf '%s  %s\n' "$hash" "$target/$name" | /usr/bin/sha256sum --check --strict --status
 diagnostic_stage=source-recheck
 printf '%s  %s\n' "$hash" "$source" | /usr/bin/sha256sum --check --strict --status
done
'''

AUDIT_COPY = r'''
set -euo pipefail; umask 077
[[ $1 =~ ^[0-9a-f]{32}$ && $3 =~ ^[0-9a-f]{64}$ ]]
target=/var/lib/appsurface-evidence-input-$1/control/os-audit.json
[[ -f $2 && ! -L $2 && $(/usr/bin/stat -c %h -- "$2") == 1 && $(/usr/bin/stat -c %s -- "$2") -le 1048576 ]]
printf '%s  %s\n' "$3" "$2" | /usr/bin/sha256sum --check --strict --status
/usr/bin/dd if="$2" of="$target" iflag=nofollow,nonblock,count_bytes oflag=nofollow conv=fsync,excl count=1048577 status=none
/usr/bin/chown 0:0 -- "$target"; /usr/bin/chmod 0600 -- "$target"
printf '%s  %s\n' "$3" "$target" | /usr/bin/sha256sum --check --strict --status
printf '%s  %s\n' "$3" "$2" | /usr/bin/sha256sum --check --strict --status
'''

DISPATCH = r'''
set -euo pipefail; umask 077
# This is the existing audited payload-file envelope, not the per-log budget.
ulimit -f 262144
fixture_stdout_pid=; fixture_stderr_pid=
fixture_stdout_fifo=; fixture_stderr_fifo=
fixture_stdout_fifo_owned=0; fixture_stderr_fifo_owned=0
finish_fixture_capture() {
 local status=$1 pump_status=0 pid
 trap - EXIT
 for pid in "$fixture_stdout_pid" "$fixture_stderr_pid"; do
  [[ -n $pid ]] || continue
  pump_status=0; wait "$pid" || pump_status=$?
  if ((status==0 && pump_status!=0)); then status=$pump_status; fi
 done
 if ((fixture_stdout_fifo_owned)); then
  rm -f -- "$fixture_stdout_fifo" || { ((status!=0)) || status=1; }
 fi
 if ((fixture_stderr_fifo_owned)); then
  rm -f -- "$fixture_stderr_fifo" || { ((status!=0)) || status=1; }
 fi
 # The prearmed root utility timer owns this entire join, including partial
 # setup. A missing/failed pump or any earlier fixture failure cannot pass.
 exit "$status"
}
capture_fixture() {
 local stdout=$1 stderr=$2 fixture_status=0
 shift 2
 fixture_stdout_fifo=$stdout.pipe; fixture_stderr_fifo=$stderr.pipe
 trap 'finish_fixture_capture "$?"' EXIT
 # Each FIFO is created exclusively in the fixed root-owned private control
 # directory. Retain every ordinary background child immediately for wait.
 mkfifo -m 600 -- "$fixture_stdout_fifo"; fixture_stdout_fifo_owned=1
 mkfifo -m 600 -- "$fixture_stderr_fifo"; fixture_stderr_fifo_owned=1
 (trap - EXIT; ulimit -f 8192; ulimit -c 0; set -C; exec cat <"$fixture_stdout_fifo" >"$stdout") &
 fixture_stdout_pid=$!
 (trap - EXIT; ulimit -f 8192; ulimit -c 0; set -C; exec cat <"$fixture_stderr_fifo" >"$stderr") &
 fixture_stderr_pid=$!
 "$@" >"$fixture_stdout_fifo" 2>"$fixture_stderr_fifo" || fixture_status=$?
 finish_fixture_capture "$fixture_status"
}
[[ $1 =~ ^[0-9a-f]{32}$ ]]; g=$1; hash=$2; shift 2
root=/var/lib/appsurface-evidence-input-$g
[[ ! -L /run/appsurface-evidence-fixture && ! -L /var/lib/appsurface-evidence-fixture ]]
[[ $(/usr/bin/stat -c '%u:%g:%a' -- /run/appsurface-evidence-fixture) == 0:0:755 ]]
[[ $(/usr/bin/stat -c '%u:%g:%a' -- /run/appsurface-evidence-fixture/$g) == 0:0:700 ]]
[[ $(/usr/bin/stat -c '%u:%g:%a' -- /var/lib/appsurface-evidence-fixture/$g) == 0:0:555 ]]
[[ $(/usr/bin/stat -c '%u:%g:%a' -- "$root/control") == 0:0:700 ]]
/usr/bin/jq -ncS --arg g "$g" '{schema:"issue779-native-dispatch-marker-v2",generation:$g,fixture_parent_prepared:true}' >"$root/control/fixture-dispatch-marker.json"
printf '%s  %s\n' "$hash" "$root/reviewed/checkpoint-n15-pending-start-death.sh" | /usr/bin/sha256sum --check --strict --status
capture_fixture "$root/control/fixture-stdout.log" "$root/control/fixture-stderr.log" /usr/bin/bash --noprofile --norc "$root/reviewed/checkpoint-n15-pending-start-death.sh" "$@"
/usr/bin/cat -- "$root/control/fixture-stdout.log"
'''


READ_ROOT_ARCHIVE = r'''
set -euo pipefail; umask 077
[[ $1 =~ ^[0-9a-f]{32}$ ]]
control=/var/lib/appsurface-evidence-input-$1/control; path=$control/native.tar
[[ ! -L /var && ! -L /var/lib && ! -L /var/lib/appsurface-evidence-input-$1 && ! -L $control && ! -L $path ]]
[[ $(/usr/bin/stat -c '%u:%g:%a' -- "$control") == 0:0:700 && -f $path ]]
[[ $(/usr/bin/stat -c '%u:%g:%a:%h' -- "$path") == 0:0:600:1 ]]
before=$(/usr/bin/stat -c '%d:%i:%s:%f:%h:%u:%g:%y:%z' -- "$path")
size=$(/usr/bin/stat -c %s -- "$path"); ((size>0 && size<=33619968))
/usr/bin/dd if="$path" iflag=nofollow,nonblock,count_bytes count=$((size+1)) status=none
[[ $(/usr/bin/stat -c '%d:%i:%s:%f:%h:%u:%g:%y:%z' -- "$path") == "$before" ]]
'''

ROOT_ARCHIVE_SHA = r'''
set -euo pipefail
[[ $1 =~ ^[0-9a-f]{32}$ ]]; path=/var/lib/appsurface-evidence-input-$1/control/native.tar
[[ -f $path && ! -L $path && $(/usr/bin/stat -c '%u:%g:%a:%h' -- "$path") == 0:0:600:1 ]]
/usr/bin/sha256sum -- "$path" | /usr/bin/cut -c1-64
'''


ARCHIVE_NAMES = {
 'fixture-stdout.log','fixture-stderr.log','retention-selection.json',
 'logs/n15.stdout','logs/n15.stderr','partial.json','reactivation-measurement.json','retry.stdout','retry.stderr',
 'owner-launch.stdout','worker-before.txt','worker-after.txt','owner-before.txt','retry-owner.txt','account-cleanup.json',
 'n15-final-record.json','n15-record-summary.json',
 'request.json','request-policy.sha256','logs/kill.log','logs/stop.log'}
CAPS={'fixture-stdout.log':8388608,'fixture-stderr.log':8388608,'retention-selection.json':4096,
 'logs/n15.stdout':65536,'logs/n15.stderr':114688,'partial.json':65536,'reactivation-measurement.json':4096,
 'retry.stdout':65536,'retry.stderr':114688,'owner-launch.stdout':65536,'worker-before.txt':16384,'worker-after.txt':16384,
 'owner-before.txt':16384,'retry-owner.txt':16384,'account-cleanup.json':65536,
 'n15-final-record.json':65536,'n15-record-summary.json':4096,
 'request.json':65536,'request-policy.sha256':4096,
 'logs/kill.log':8388608,'logs/stop.log':8388608}

def inspect_archive(data):
    require(type(data) is bytes and 0 < len(data) <= ARCHIVE_CAP, 'archive-size')
    values={}; total=0
    try:
        with tarfile.open(fileobj=io.BytesIO(data),mode='r:') as tar:
            members=tar.getmembers()
            require(0 < len(members) <= len(ARCHIVE_NAMES), 'archive-entry-count')
            names=[]; entries=[]
            for member in members:
                name=member.name
                require(name in ARCHIVE_NAMES and name not in values and member.isfile() and member.mode==0o600 and member.uid==member.gid==member.mtime==0 and not member.issym() and not member.islnk() and not member.linkname and not member.pax_headers and not member.uname and not member.gname and member.size<=CAPS[name], 'archive-member')
                names.append(name)
                stream=tar.extractfile(member); require(stream is not None, 'archive-read')
                raw=stream.read(CAPS[name]+1); require(len(raw)==member.size and len(raw)<=CAPS[name], 'archive-member-size')
                total+=len(raw); require(total<=32*1024*1024, 'archive-expanded-size')
                values[name]=raw; entries.append((name,raw))
            require(names==sorted(names), 'archive-member-order')
            canonical=io.BytesIO()
            with tarfile.open(fileobj=canonical,mode='w:',format=tarfile.USTAR_FORMAT) as rebuilt:
                for name,content in entries:
                    info=tarfile.TarInfo(name); info.size=len(content); info.mode=0o600; info.uid=info.gid=info.mtime=0
                    rebuilt.addfile(info,io.BytesIO(content))
            require(canonical.getvalue()==data, 'archive-noncanonical')
    except (tarfile.TarError, OSError, EOFError):
        raise Rejected('archive-invalid')
    require('retention-selection.json' in values, 'retention-selection-missing')
    selection=decode_data(values['retention-selection.json'])
    require(type(selection) is dict and set(selection)=={'schema','selection_status','data_file_count','data_bytes','missing_fixed_file_count'} and selection.get('schema')=='issue779-native-retention-selection-v1', 'retention-selection-shape')
    require(selection['selection_status'] in ('parent-absent','parent-empty','one-namespace') and type(selection['data_file_count']) is int and selection['data_file_count']==len(values)-1 and type(selection['data_bytes']) is int and selection['data_bytes']==sum(len(v) for k,v in values.items() if k!='retention-selection.json') and type(selection['missing_fixed_file_count']) is int and selection['missing_fixed_file_count']==len(ARCHIVE_NAMES)-1-selection['data_file_count'], 'retention-selection-values')
    return values

def decode_data(raw):
    try: return json.loads(raw.decode('utf-8'), object_pairs_hook=unique)
    except (UnicodeError, json.JSONDecodeError, ValueError): raise Rejected('retained-json')

def validate_cleanup_receipt(cleanup, partial, measurement, raw, generation):
    keys={'schema','generation','source_commit','source_capture_sha256','partial_sha256','measurement_sha256',
          'names','ids','workspace','joined_launcher_pid','launcher_absent_after_wait','worker_before_after_sha256',
          'retry_owner_terminal_sha256','retry_stdout_sha256','retry_stderr_sha256','owner_before_sha256',
          'worker_snapshot_unchanged','retry_owner_terminal','utilities',
          'numeric_and_name_lookup_absent','native_credit'}
    require(type(cleanup) is dict and set(cleanup)==keys and cleanup['schema']=='issue779-n15-root-account-cleanup-v1'
            and cleanup['generation']==generation and cleanup['source_commit']==SOURCE
            and cleanup['source_capture_sha256']==CAPTURE_SHA and cleanup['partial_sha256']==sha(raw['partial.json'])
            and cleanup['measurement_sha256']==sha(raw['reactivation-measurement.json']), 'n15-cleanup-binding')
    accounts=partial['account_snapshot']; require(cleanup['names']==accounts['names'] and cleanup['ids']==accounts['ids'], 'n15-cleanup-accounts')
    require(type(cleanup['workspace']) is dict and set(cleanup['workspace'])=={'device','inode','uid','gid','mode'}
            and type(cleanup['workspace']['device']) is int and cleanup['workspace']['device']>=0
            and type(cleanup['workspace']['inode']) is int and cleanup['workspace']['inode']>0
            and cleanup['workspace']['uid']==cleanup['workspace']['gid']==0 and cleanup['workspace']['mode']==0o700,
            'n15-cleanup-workspace')
    require(type(cleanup['joined_launcher_pid']) is int and cleanup['joined_launcher_pid']>1
            and cleanup['launcher_absent_after_wait'] is True
            and cleanup['worker_snapshot_unchanged'] is True and cleanup['retry_owner_terminal']=={
                'LoadState':'loaded','ActiveState':'failed','SubState':'failed','ExecMainCode':'1','ExecMainStatus':'1'}
            and cleanup['numeric_and_name_lookup_absent'] is True and cleanup['native_credit'] is False,
            'n15-cleanup-observations')
    digest_fields={'worker_before_after_sha256':'worker_before_after_snapshot_sha256',
                   'retry_owner_terminal_sha256':'retry_owner_terminal_snapshot_sha256',
                   'retry_stdout_sha256':'retry_stdout_sha256','retry_stderr_sha256':'retry_stderr_sha256'}
    for target,source in digest_fields.items():
        require(cleanup[target]==measurement[source] and sha(raw['worker-before.txt'])==measurement['worker_before_after_snapshot_sha256']
                and sha(raw['worker-after.txt'])==measurement['worker_before_after_snapshot_sha256']
                and sha(raw['retry-owner.txt'])==measurement['retry_owner_terminal_snapshot_sha256']
                and sha(raw['retry.stdout'])==measurement['retry_stdout_sha256']
                and sha(raw['retry.stderr'])==measurement['retry_stderr_sha256'], 'n15-cleanup-raw-binding')
    require(type(cleanup['owner_before_sha256']) is str and re.fullmatch('[0-9a-f]{64}',cleanup['owner_before_sha256'])
            and sha(raw['owner-before.txt'])==cleanup['owner_before_sha256'], 'n15-cleanup-owner-before')
    expected_names=[accounts['names']['worker'],accounts['names']['subject'],accounts['names']['worker'],
                    accounts['names']['subject'],accounts['names']['results']]
    require(type(cleanup['utilities']) is list and len(cleanup['utilities'])==5, 'n15-cleanup-utility-count')
    seen=[]
    for i,row in enumerate(cleanup['utilities']):
        require(type(row) is dict and row.get('name') in expected_names, 'n15-cleanup-utility-name')
        utility_key=(row.get('utility'),row['name'])
        require(utility_key not in seen, 'n15-cleanup-utility-duplicate')
        seen.append(utility_key)
        require(row.get('utility')==('userdel' if i<2 else 'groupdel'), 'n15-cleanup-utility-kind')
        if row.get('already_absent') is True:
            require(row['utility']=='groupdel' and row.get('exit') is None, 'n15-cleanup-absence-row')
        else:
            require(set(row)=={'utility','name','exit'} and type(row['exit']) is int and row['exit']==0, 'n15-cleanup-utility-exit')
    require(seen[:2]==[('userdel',expected_names[0]),('userdel',expected_names[1])], 'n15-cleanup-user-order')
    require(set(seen)=={('userdel',expected_names[0]),('userdel',expected_names[1]),
                        ('groupdel',expected_names[2]),('groupdel',expected_names[3]),
                        ('groupdel',expected_names[4])}, 'n15-cleanup-complete-coverage')
    return True

FIXTURE_POLICY = {
    'Id':'checkpoint-empty','Version':'1','ConservativeProfileId':'conservative',
    'Profiles':[{'Id':'empty','Scope':'Targeted','Resources':[],'Producers':[],'Obligations':[]},
        {'Id':'conservative','Scope':'Release','Resources':[{'Id':'build','Readiness':'completion','DeadlineSeconds':1,'Requires':[]}],'Producers':[],'Obligations':[]}],
    'Rules':[{'Id':'docs','Pattern':'docs/**','ProfileId':'empty','Precedence':0}]}


def validate_request_binding(request_raw, checksum_raw, generation, workflow, expected_deadline_utc):
    """Check retained request and both checksum rows as data, never as admission authority."""
    require(type(request_raw) is bytes and 0<len(request_raw)<=65536 and type(checksum_raw) is bytes and 0<len(checksum_raw)<=4096, 'request-policy-bounds')
    request=decode_data(request_raw)
    base='/var/lib/appsurface-evidence-fixture/'+generation
    expected={'schema':'evidence-supervisor-linux-v1','mode':'observation','tool_root':base+'/tool',
        'runtime_root':base+'/runtime','runtime_host':base+'/runtime/dotnet','entry_path':base+'/tool/ForgeTrust.AppSurface.Cli.dll',
        'policy_file':base+'/tool/fixture-policy.json','subject_root':base+'/subject',
        'base_revision':SOURCE_PARENT,'subject_revision':SOURCE,'workflow_identity':workflow,
        'paths':['docs/designs/issue-779-csharp-supervision-core.md'],'observation_profile_ids':['empty'],
        'observation_producer_ids':[],'admission_seconds':10,'start_seconds':30,'collection_seconds':10,
        'cleanup_seconds':60,'stopping_seconds':5}
    require(type(request) is dict and set(request)==set(expected)|{'job_deadline_utc'}, 'request-shape')
    require(all(type(request[k]) is type(v) and request[k]==v for k,v in expected.items()), 'request-binding')
    stamp=request['job_deadline_utc']
    require(type(stamp) is str and re.fullmatch(r'[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z',stamp) is not None, 'request-deadline-shape')
    try: datetime.strptime(stamp,'%Y-%m-%dT%H:%M:%SZ')
    except ValueError: raise Rejected('request-deadline-shape')
    require(stamp==expected_deadline_utc, 'request-original-deadline-binding')
    policy_raw=json.dumps(FIXTURE_POLICY,sort_keys=True,separators=(',',':')).encode()
    expected_checksums=(sha(request_raw)+'  /run/appsurface-evidence-fixture/'+generation+'/request.json\n'
        +sha(policy_raw)+'  '+base+'/tool/fixture-policy.json\n').encode()
    require(checksum_raw==expected_checksums, 'request-policy-binding')


def validate_n15_archive(values, parser_bytes, generation, build_sha, workflow, deadline, original_deadline_utc):
    needed=('fixture-stdout.log','fixture-stderr.log','retention-selection.json','logs/n15.stdout','logs/n15.stderr','owner-launch.stdout',
        'partial.json','reactivation-measurement.json','retry.stdout','retry.stderr','worker-before.txt','worker-after.txt',
        'owner-before.txt','retry-owner.txt','account-cleanup.json','n15-final-record.json','n15-record-summary.json',
        'request.json','request-policy.sha256')
    require(all(name in values for name in needed), 'n15-retained-required-files')
    require(values['logs/n15.stdout']==b'' and values['fixture-stderr.log']==b'', 'n15-worker-or-wrapper-stderr')
    wrapper=re.fullmatch(rb'N15_OWNER_LAUNCH_JOIN_MEASURED:launch=([0-9]{1,3}):observer=0:stdout=([0-9]{1,6}):phase_stderr=([1-9][0-9]{0,4})\nN15_FINAL_RECORD_MEASURED:NO_NATIVE_CREDIT\n',values['fixture-stdout.log'])
    require(wrapper is not None and int(wrapper.group(1))<=255 and int(wrapper.group(2))==len(values['owner-launch.stdout'])
            and int(wrapper.group(3))==len(values['logs/n15.stderr']), 'n15-wrapper-output')
    require(type(parser_bytes) is bytes and sha(parser_bytes)==PARSER_SHA, 'parser-pin')
    parser=types.ModuleType('n15_pinned_parser'); exec(compile(parser_bytes,'<pinned-N15-record-parser>','exec',dont_inherit=True),parser.__dict__)
    partial=decode_data(values['partial.json']); parser.validate_partial_observation(partial,generation)
    measurement=decode_data(values['reactivation-measurement.json'])
    measurement_keys={'schema','generation','owner_unit','worker_unit','retry_exit','retry_stdout_sha256','retry_stderr_sha256',
        'worker_before_after_snapshot_sha256','retry_owner_terminal_snapshot_sha256','raw_logs_retained','synthetic_completion_flags'}
    require(type(measurement) is dict and set(measurement)==measurement_keys and measurement['schema']=='issue779-n15-guard-reactivation-measurement-v1'
        and measurement['generation']==generation and measurement['owner_unit']==partial['owner']['unit']
        and measurement['worker_unit']==partial['worker']['unit'] and type(measurement['retry_exit']) is int
        and measurement['retry_exit']==1 and measurement['raw_logs_retained'] is True and measurement['synthetic_completion_flags'] is False,
        'n15-reactivation-measurement')
    result=decode_data(values['n15-final-record.json']); summary=parser.validate_result(result,generation)
    phase=parser.validate_phase(values['logs/n15.stderr'],generation)
    require(phase['phase_sha256']==result['phase_sha256'] and phase['job_path']==result['job_path']
            and phase['unit']==result['worker']['unit'], 'n15-phase-record-binding')
    completion={'schema':parser.COMPLETION_SCHEMA,'case_generation':generation,'source_commit':SOURCE,
        'source_capture_sha256':CAPTURE_SHA,'reactivation':result['reactivation']}
    parser.validate_completion_evidence(completion,generation)
    require(all(result[k]==partial[k] for k in parser.PARTIAL_KEYS & parser.FINAL_KEYS), 'n15-partial-final-binding')
    cleanup=decode_data(values['account-cleanup.json'])
    validate_cleanup_receipt(cleanup,partial,measurement,values,generation)
    retained_summary=decode_data(values['n15-record-summary.json'])
    require(retained_summary==summary and summary.get('generation')==generation and summary.get('phase_sha256')==phase['phase_sha256'], 'n15-record-summary-binding')
    validate_request_binding(values['request.json'],values['request-policy.sha256'],generation,workflow,original_deadline_utc)
    return result,summary,values.get('fixture-stdout.log',b'')


def acquisition_arguments(generation, build, bootstrap, deadline_ms):
    return ['--execute','--generation',generation,'--build-root',str(build),'--reviewed-root',str(bootstrap),
        '--deadline-monotonic-ms',deadline_ms,'--reviewed-script-sha256',REVIEWED_FILES['acquire-inbound.sh'],
        '--transport-sha256',REVIEWED_FILES['prepare-root-inputs-v2.sh'],
        '--fixture-sha256',REVIEWED_FILES['checkpoint-n15-pending-start-death.sh'],
        '--audit-sha256',REVIEWED_FILES['prepare-os-audit-v2.py'],'--source-review-sha256',SOURCE_REVIEW_SHA,
        '--source-capture-sha256',CAPTURE_SHA,'--n15-parser-sha256',PARSER_SHA,
        '--n15-observer-sha256',REVIEWED_FILES['observe_n15_pending_start.py'],
        '--n15-retry-sha256',REVIEWED_FILES['n15-guard-reactivation-retry.sh'],
        '--n15-finalizer-sha256',REVIEWED_FILES['finalize_n15_observation.py'],
        '--case-workspace-sha256',REVIEWED_FILES['prepare-case-workspace.py']]


def transport_arguments(generation, inbound, receipt, build_sha, deadline_ms):
    args=['--execute','--reviewed-script-sha256',REVIEWED_FILES['prepare-root-inputs-v2.sh'],
        '--generation',generation,'--deadline-monotonic-ms',deadline_ms,'--source-commit',SOURCE]
    for name in ('source','tool','runtime'):
        fact=receipt['artifacts'][name]
        args += ['--'+name+'-root',str(inbound/name),'--'+name+'-map',str(inbound/'control'/(name+'.tsv')),
            '--'+name+'-map-sha256',fact['tsv_sha256'],'--'+name+'-nodes',str(inbound/'control'/(name+'-nodes.json')),
            '--'+name+'-nodes-sha256',fact['nodes_sha256']]
    args += ['--source-review',str(inbound/'control/source-review.json'),'--source-review-sha256',SOURCE_REVIEW_SHA,
        '--build-receipt',str(inbound/'control/build-receipt.json'),'--build-receipt-sha256',build_sha,
        '--fixture',str(inbound/'scripts/checkpoint-n15-pending-start-death.sh'),'--fixture-sha256',REVIEWED_FILES['checkpoint-n15-pending-start-death.sh'],
        '--n15-finalizer-sha256',REVIEWED_FILES['finalize_n15_observation.py'],
        '--os-audit-script',str(inbound/'scripts/prepare-os-audit-v2.py'),'--os-audit-script-sha256',REVIEWED_FILES['prepare-os-audit-v2.py'],
        '--n15-parser-root',str(inbound/'scripts'),'--n15-parser-sha256',PARSER_SHA,
        '--n15-observer-sha256',REVIEWED_FILES['observe_n15_pending_start.py'],
        '--n15-retry-sha256',REVIEWED_FILES['n15-guard-reactivation-retry.sh'],
        '--case-workspace',str(inbound/'scripts/prepare-case-workspace.py'),
        '--case-workspace-sha256',REVIEWED_FILES['prepare-case-workspace.py'],
        '--source-capture',str(inbound/'control/source-capture.json'),'--source-capture-sha256',CAPTURE_SHA]
    return args


def workspace_arguments(generation, receipt, build_sha, workflow, utc_deadline, deadline_ms):
    input_root=Path('/var/lib/appsurface-evidence-input-'+generation)
    args=['--generation',generation,'--case','N15']
    for role in ('source','tool','runtime'):
        fact=receipt['artifacts'][role]
        args += ['--'+role+'-root',str(input_root/role),
            '--'+role+'-map',str(input_root/'control'/(role+'.tsv')),
            '--'+role+'-map-sha256',fact['tsv_sha256'],
            '--'+role+'-nodes',str(input_root/'control'/(role+'-nodes.json')),
            '--'+role+'-nodes-sha256',fact['nodes_sha256']]
    args += ['--build-receipt',str(input_root/'control/build-receipt.json'),
        '--build-receipt-sha256',build_sha,'--source-capture',str(input_root/'control/source-capture.json'),
        '--source-capture-sha256',CAPTURE_SHA,'--job-deadline-utc',utc_deadline,
        '--job-deadline-boottime-ms',deadline_ms,'--workflow-identity',workflow]
    return args


def fixture_arguments(root, generation, job_deadline_boot_ms):
    """Build the fixed source-owned owner launch/observer ABI under original deadlines."""
    require(re.fullmatch('[0-9a-f]{32}', generation) is not None, 'n15-generation')
    boot_now = time.clock_gettime_ns(time.CLOCK_BOOTTIME) // 1_000_000
    require(type(job_deadline_boot_ms) is int and job_deadline_boot_ms > boot_now,
            'n15-original-job-deadline-expired')
    job_deadline = job_deadline_boot_ms
    work_deadline = job_deadline - 60_000
    startup_deadline = min(work_deadline, boot_now + 30_000)
    # Legacy observer ABI name: this barrier holds StartAsync before the server
    # and its real Admission10 interval exist. Share the ORIGINAL Start30 bound.
    # No new clock sample, renewed timer, request change or extra allowance.
    admission_deadline = startup_deadline
    require(boot_now < admission_deadline <= work_deadline
            and boot_now < startup_deadline <= work_deadline < job_deadline,
            'n15-original-deadline-window')
    request = f'/run/appsurface-evidence-fixture/{generation}/request.json'
    result_dir = f'/run/appsurface-evidence-fixture/{generation}'
    phase = result_dir + '/logs/n15.stderr'
    partial = result_dir + '/partial.json'
    root_input = f'/var/lib/appsurface-evidence-fixture/{generation}'
    return ['--execute', '--case-generation', generation, '--request', request,
        '--runtime-host', root_input + '/runtime/dotnet', '--entry',
        root_input + '/tool/ForgeTrust.AppSurface.Cli.dll', '--phase-stderr', phase,
        '--partial-record', partial, '--result-directory', result_dir,
        '--startup-deadline-boot-ms', str(startup_deadline),
        '--admission-deadline-boot-ms', str(admission_deadline),
        '--job-deadline-boot-ms', str(job_deadline), '--work-deadline-boot-ms',
        str(work_deadline), '--root-input-root', root_input]


def collect_private_archive(runner, bootstrap, generation, output, deadline):
    """Retain bounded diagnostic bytes; callers preserve the original fixture failure."""
    deadline=min(deadline,time.monotonic()+30)
    require(time.monotonic()<deadline,'retention-deadline')
    runner.phase='bounded-private-retention'
    runner.run(ROOT_PREFIX+[str(bootstrap/'retain-native.sh'),'--execute','--generation',generation,
        '--deadline-monotonic-ms',root_deadline(deadline),'--reviewed-script-sha256',REVIEWED_FILES['retain-native.sh']],deadline)
    _,before=runner.run(ROOT_PREFIX+['-c',ROOT_ARCHIVE_SHA,'--',generation],deadline)
    _,archive=runner.run(ROOT_PREFIX+['-c',READ_ROOT_ARCHIVE,'--',generation],deadline)
    _,after=runner.run(ROOT_PREFIX+['-c',ROOT_ARCHIVE_SHA,'--',generation],deadline)
    digest=sha(archive)
    require(before==after==(digest+'\n').encode(),'root-archive-digest')
    values=inspect_archive(archive)
    save(output/'native-private.tar',archive,deadline)
    return values,{'private_archive_sha256':digest,'private_archive_bytes':len(archive),
        'retention_selection':decode_data(values['retention-selection.json'])}


def run_fixture_with_retention(runner, dispatch, dispatch_end, collect, result,
                               dispatch_index):
    """Attempt retention after any dispatch outcome without changing a failed outcome."""
    failure=None; values=None
    try:
        code,_=runner.run(dispatch,dispatch_end,allow_failure=True)
        result['fixture_exit']=code
        require(code==0,'native-fixture-wrapper-nonzero')
    except BaseException as caught: failure=caught
    # Capture the dispatch record before retention appends its own commands. The
    # archive's saved fixture stdout is the sole source for the dispatch marker.
    dispatch_row=None
    if type(dispatch_index) is int and 0 <= dispatch_index < len(runner.records):
        candidate=runner.records[dispatch_index]
        if (type(candidate) is dict and candidate.get('ordinal')==dispatch_index
                and candidate.get('argv')==dispatch):
            dispatch_row=candidate
    try:
        values,facts=collect()
        result.update(facts)
    except BaseException as caught:
        result['retention_failure']={'category':getattr(caught,'category','retention-operation'),
            'error_class':type(caught).__name__}
        failure=failure or caught
    fixture_stdout=values.get('fixture-stdout.log') if type(values) is dict else None
    result['native_dispatched']=fixture_dispatched(dispatch_row,fixture_stdout)
    if failure: raise failure
    return values


def fixture_dispatched(dispatch_row, fixture_stdout):
    """Recognize only the exact dispatch line in the retained fixture stdout bytes."""
    if (type(dispatch_row) is not dict
            or type(dispatch_row.get('ordinal')) is not int
            or dispatch_row.get('ordinal') < 0
            or type(dispatch_row.get('argv')) is not list
            or type(fixture_stdout) is not bytes
            or len(fixture_stdout) > CAPS['fixture-stdout.log']):
        return False
    return b'N15_CSHARP_OWNER_DISPATCHED\n' in fixture_stdout.splitlines(keepends=True)



def retain_failed_command_logs(runner, deadline):
    """Retain original failure logs and a failed retention command's private stderr.

    Only fixed recorder filenames and their recorded byte/digest identities are accepted.
    This diagnostic archive is separate from fixture evidence and confers no native credit.
    Unjoined commands, replacement files and an expired retention deadline are rejected.
    """
    failed = next((row for row in runner.records if row.get('failure') is not None
                   or row.get('exit') not in (None, 0)), None)
    if failed is None:
        return None
    require(time.monotonic() < deadline, 'failure-log-retention-deadline')
    require(failed.get('waited') is True and failed.get('group_absent') is True
            and (failed.get('root_utility_unit') is None
                 or failed.get('root_utility_cgroup_empty') is True), 'failure-log-command-unjoined')
    ordinal = failed.get('ordinal')
    require(type(ordinal) is int and 0 <= ordinal <= 99, 'failure-log-command-ordinal')
    expected = [f'{ordinal:02d}-stdout.log', f'{ordinal:02d}-stderr.log']
    rows = failed.get('logs')
    require(type(rows) is list and len(rows) == 2
            and [item.get('name') for item in rows] == expected, 'failure-log-recorder-shape')
    directory = runner.output.lstat()
    require(stat.S_ISDIR(directory.st_mode) and not stat.S_ISLNK(directory.st_mode)
            and directory.st_uid == os.geteuid() and stat.S_IMODE(directory.st_mode) == 0o700,
            'failure-log-output-private')
    # A retention failure can explain why the original fixture logs are absent.
    # Keep its stderr separately, after the original command's two fixed logs.
    # 4 KiB fits beside their existing 64 KiB prefixes in the same archive cap.
    selected = [(row, member, 65536) for row, member in zip(rows, ('stdout.log', 'stderr.log'))]
    retention = next((row for row in runner.records
                      if row.get('phase') == 'bounded-private-retention'
                      and type(row.get('ordinal')) is int and row['ordinal'] > ordinal
                      and (row.get('failure') is not None or row.get('exit') not in (None, 0))), None)
    retention_ordinal = None
    if retention is not None:
        require(retention.get('waited') is True and retention.get('group_absent') is True
                and (retention.get('root_utility_unit') is None
                     or retention.get('root_utility_cgroup_empty') is True),
                'failure-log-retention-command-unjoined')
        retention_ordinal = retention['ordinal']
        require(0 <= retention_ordinal <= 99, 'failure-log-retention-command-ordinal')
        retention_rows = retention.get('logs')
        require(type(retention_rows) is list and len(retention_rows) == 2
                and [row.get('name') for row in retention_rows]
                == [f'{retention_ordinal:02d}-stdout.log', f'{retention_ordinal:02d}-stderr.log'],
                'failure-log-retention-recorder-shape')
        selected.append((retention_rows[1], 'retention.stderr.log', 4096))
    retained_logs = []
    stream = io.BytesIO()
    with tarfile.open(fileobj=stream, mode='w', format=tarfile.USTAR_FORMAT) as archive:
        for row, member, cap in selected:
            path = runner.output / row['name']
            fact = path.lstat()
            require(stat.S_ISREG(fact.st_mode) and fact.st_nlink == 1
                    and fact.st_uid == os.geteuid() and stat.S_IMODE(fact.st_mode) == 0o600,
                    'failure-log-private-file')
            raw = read(path, LOG_CAP, deadline)
            require(ident(fact) == ident(path.lstat()) and type(row.get('bytes')) is int
                    and row['bytes'] == len(raw) and row.get('sha256') == sha(raw),
                    'failure-log-recorder-identity')
            prefix = raw[:cap]
            retained_logs.append({'name': member, 'original_bytes': len(raw), 'original_sha256': sha(raw),
                                  'retained_bytes': len(prefix), 'truncated': len(prefix) < len(raw)})
            entry = tarfile.TarInfo(member)
            entry.size, entry.mode, entry.uid, entry.gid, entry.mtime = len(prefix), 0o600, 0, 0, 0
            entry.uname = entry.gname = ''
            archive.addfile(entry, io.BytesIO(prefix))
    raw = stream.getvalue()
    require(len(raw) <= 143360 and time.monotonic() < deadline,
            'failure-log-archive-bound')
    save(runner.output / 'native-command-failure.tar', raw, deadline)
    return {'command_ordinal': ordinal, 'retention_command_ordinal': retention_ordinal,
            'bytes': len(raw), 'sha256': sha(raw), 'logs': retained_logs, 'native_credit': False}


def record_failed_command_logs(runner, result, deadline):
    """Contain retention errors; the original failed result remains failed and printable data stays closed."""
    try:
        value = retain_failed_command_logs(runner, deadline)
        result['command_failure_logs'] = {'status': 'retained', 'archive': value} if value else {'status': 'unavailable'}
    except BaseException:
        result['command_failure_logs'] = {'status': 'rejected'}


def publish_terminal(output, result, deadline):
    """Publication failure always returns failure; it cannot leave a zero exit without a receipt."""
    try:
        require(time.monotonic()<deadline,'terminal-deadline')
        save(output/'native-receipt.json',result,deadline)
        require(time.monotonic()<deadline,'terminal-deadline')
        print('NATIVE_TEST_TERMINAL:'+str(result['exit']),flush=True)
        require(time.monotonic()<deadline,'terminal-deadline')
        return result['exit']
    except BaseException:
        return 1

def parse_runner_arguments(argv):
    """Reject repeated option names before argparse can replace a reviewed value."""
    seen=set()
    for value in argv:
        if value.startswith('--'):
            name=value.split('=',1)[0]
            require(name not in seen,'duplicate-orchestrator-option')
            seen.add(name)
    ap=argparse.ArgumentParser(description=__doc__,allow_abbrev=False)
    ap.add_argument('--execute',action='store_true'); ap.add_argument('--workspace',required=True)
    ap.add_argument('--build-root',required=True); ap.add_argument('--output',required=True)
    ap.add_argument('--reviewed-script-sha256',required=True)
    return ap.parse_args(argv)


def create_os_audit_directory(generation):
    """Keep the fresh caller-owned audit leaf below root-owned sticky /tmp.

    The audit publisher rejects caller-owned intermediate ancestors. The CI
    artifact directory under /home/runner therefore cannot hold this input.
    Root copies only the separately verified audit bytes into its control root.
    """
    require(type(generation) is str and re.fullmatch('[0-9a-f]{32}', generation) is not None,
            'audit-generation')
    path = Path('/tmp/issue779-os-audit-' + generation)
    path.mkdir(mode=0o700)
    return path


def main():
    args=parse_runner_arguments(sys.argv[1:]); start=time.monotonic(); start_utc=datetime.now(timezone.utc)
    start_boot_ms=time.clock_gettime_ns(time.CLOCK_BOOTTIME)//1_000_000
    original_job_deadline_monotonic=start+240.0
    original_job_deadline_boot_ms=start_boot_ms+240_000
    original_work_deadline_monotonic=original_job_deadline_monotonic-60.0
    original_job_deadline_utc=(start_utc+timedelta(seconds=240)).strftime('%Y-%m-%dT%H:%M:%SZ')
    job_end=int(os.environ['JOB_DEADLINE_MONOTONIC_NS'])/1_000_000_000; final=min(job_end,start+900)
    require(N15_SOURCE_REVIEW_CLEAR and N15_FIXTURE_REVIEW_CLEAR and N15_ROOT_CLEANUP_REVIEW_CLEAR,'n15-independent-review-pending')
    require(args.execute and sys.platform=='linux' and os.uname().machine=='x86_64' and os.geteuid()>0 and os.getegid()>0 and final-start>850,'native-platform-or-job-budget')
    workspace,build,output=map(Path,(args.workspace,args.build_root,args.output))
    require(all(x.is_absolute() for x in (workspace,build,output)) and not output.exists() and not output.is_symlink(),'absolute-paths-or-output-reuse')
    output.mkdir(mode=0o700); runner=Runner(output,final); result={'schema':'issue779-csharp-n15-native-run-v1','source_commit':SOURCE,'exit':1,'commands':runner.records,'native_dispatched':False,'native_execution':False,'native_credit':False,'native_acceptance':False,'fixture_result_verified':False}
    generation=uuid.uuid4().hex; root=Path('/var/lib/appsurface-evidence-input-'+generation); failure=None; retained=False
    try:
        require(re.fullmatch('[0-9a-f]{64}',args.reviewed_script_sha256) and sha(read(Path(__file__),131072,final))==args.reviewed_script_sha256,'orchestrator-pin')
        reviewed=workspace/'.github/issue779-csharp-native'; cap_path=workspace/'.github/issue779-csharp-build/capture-receipt.json'
        capture_raw=read(cap_path,4*1024*1024,final); require(sha(capture_raw)==CAPTURE_SHA,'capture-pin'); capture=decode(capture_raw)
        require(capture.get('schema')=='issue779-csharp-common-source-capture-v4' and capture.get('validation_commit')==SOURCE and capture.get('previous_validation_commit')==SOURCE_PARENT and capture.get('source_count')==SOURCE_COUNT and capture.get('source_tree')==SOURCE_TREE and capture.get('clean') is True and capture.get('index_checksum_verified') is True and capture.get('index_tree_blob_modes_equal') is True and capture.get('no_alternates') is True and capture.get('native_credit') is False and capture.get('all16_final_integrated_native_verified') is False,'capture-identity')
        runner.phase='reviewed-inputs'
        for name,pin in REVIEWED_FILES.items():
            require(re.fullmatch('[0-9a-f]{64}',pin) is not None and sha(read(reviewed/name,131072,final))==pin,'reviewed-pin')
        sr_raw=read(reviewed/'source-review.json',65536,final); require(sha(sr_raw)==SOURCE_REVIEW_SHA,'source-review-pin'); sr=decode(sr_raw)
        expected_flags={'source_capture','fixture','root_cleanup','parser','root_transport','retention','native_handoff'}
        require(sr.get('schema')=='issue779-n15-native-source-review-v1' and sr.get('case')=='N15' and sr.get('source_commit')==SOURCE and sr.get('source_parent')==SOURCE_PARENT and sr.get('source_count')==2872 and sr.get('source_tree')=='3bafd0fcd5321f1b29323f46df94b77eb48fa859' and sr.get('source_capture_sha256')==CAPTURE_SHA and type(sr.get('review_flags')) is dict and set(sr['review_flags'])==expected_flags and all(value is True for value in sr['review_flags'].values()), 'n15-source-review-gates')
        raw=read(build/'receipts/build-receipt.json',1048576,final); receipt=decode(raw)
        maps={name:(read(build/f'handoff/{name}.tsv',1048576,final),read(build/f'handoff/{name}-nodes.json',4194304,final)) for name in ('source','tool','runtime')}
        validate_build(receipt,maps); result['build_receipt_sha256']=sha(raw); result['build_artifacts']=receipt['artifacts']; result['source_capture_sha256']=CAPTURE_SHA
        parser_bytes=read(reviewed/'check_n15_records.py',131072,final); require(sha(parser_bytes)==PARSER_SHA,'parser-pin')
        result['generation']=generation
        runner.phase='root-bootstrap'; bootstrap=Path('/var/lib/appsurface-evidence-bootstrap-'+generation)
        triples=[part for name,pin in REVIEWED_FILES.items() for part in (str(reviewed/name),name,pin)]
        triples += [str(cap_path),'capture-receipt.json',CAPTURE_SHA]
        runner.run(ROOT_PREFIX+['-c',BOOTSTRAP,'--',generation]+triples,time.monotonic()+30)
        runner.phase='root-acquisition-and-transport'; acquisition_end=min(final-5,time.monotonic()+120); acq_ms=root_deadline(acquisition_end)
        runner.run(ROOT_PREFIX+[str(bootstrap/'acquire-inbound.sh')]+acquisition_arguments(generation,build,bootstrap,acq_ms),acquisition_end)
        inbound=Path('/var/lib/appsurface-evidence-inbound-'+generation); build_sha=sha(raw)
        runner.run(ROOT_PREFIX+[str(bootstrap/'prepare-root-inputs-v2.sh')]+transport_arguments(generation,inbound,receipt,build_sha,acq_ms),acquisition_end)
        runner.phase='same-host-unprivileged-os-audit'
        audit_dir=create_os_audit_directory(generation)
        audit_end=min(final-5,time.monotonic()+60)
        try:
            runner.run(['/usr/bin/python3','-I','-S','-B',str(root/'reviewed/prepare-os-audit-v2.py'),
                '--runtime-root',str(root/'runtime'),'--tool-root',str(root/'tool'),
                '--helper-root',str(root/'empty-audit-helper'),'--output',str(audit_dir/'os-audit.json'),
                '--deadline-monotonic',str(audit_end)],audit_end)
        except BaseException:
            # This optional closed sidecar records data only and never upgrades the failed audit.
            try:
                result['os_audit_failure_context']=retain_audit_failure_context(
                    audit_dir/'os-audit-failure-context.json',output/'os-audit-failure-context.json',audit_end)
            except BaseException: pass
            raise
        audit_raw=read(audit_dir/'os-audit.json',1048576,audit_end); audit_sha=sha(audit_raw); result['os_audit_sha256']=audit_sha
        runner.run(ROOT_PREFIX+['-c',AUDIT_COPY,'--',generation,str(audit_dir/'os-audit.json'),audit_sha],audit_end)
        workflow='github-actions-'+os.environ['GITHUB_RUN_ID']+'-'+os.environ['GITHUB_RUN_ATTEMPT']
        runner.phase='sealed-n15-workspace-and-request-materialization'
        materialize_end=min(final-5,original_work_deadline_monotonic)
        materializer=root/'reviewed/prepare-case-workspace.py'
        runner.run(ROOT_PREFIX+['-c','exec "$@"','n15-workspace','/usr/bin/python3.12','-I','-S','-B',
            str(materializer)]+workspace_arguments(generation,receipt,sha(raw),workflow,
                original_job_deadline_utc,str(original_job_deadline_boot_ms)),materialize_end)
        runner.phase='actual-csharp-n15-owner-launch-observe-and-replay'
        fixture_end=min(final-5,original_job_deadline_monotonic)
        fixture=fixture_arguments(root,generation,original_job_deadline_boot_ms)
        root_dispatch=ROOT_PREFIX+['-c',DISPATCH,'--',generation,REVIEWED_FILES['checkpoint-n15-pending-start-death.sh']]+fixture
        def collect_n15_archive():
            return collect_private_archive(runner,bootstrap,generation,output,final-5)
        fixture_dispatch_index=len(runner.records)
        values=run_fixture_with_retention(runner,root_dispatch,fixture_end,
            collect_n15_archive,result,fixture_dispatch_index)
        result['owner_launch_join_replay_completed']=True
        runner.phase='n15-private-archive-validation'
        final_record,record_summary,_=validate_n15_archive(values,parser_bytes,generation,build_sha,workflow,final-5,original_job_deadline_utc)
        result['n15_final_record_sha256']=sha(values['n15-final-record.json'])
        result['n15_cleanup_receipt_sha256']=sha(values['account-cleanup.json'])
        result['n15_record_summary']=record_summary
        result['fixture_result_verified']=True
        retained=True
    except BaseException as caught: failure=caught
    finally:
        result['failure']={'category':getattr(failure,'category','native-operation'),'error_class':type(failure).__name__} if failure else None
        if failure is not None:
            record_failed_command_logs(runner,result,min(final-5,time.monotonic()+5))
        result['elapsed_seconds']=time.monotonic()-start; result['exit']=0 if not failure and retained and result['fixture_result_verified'] else 1
    return publish_terminal(output,result,final-5)

if __name__=='__main__': raise SystemExit(main())
