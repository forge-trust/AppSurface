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

helper_receipt_adapter = None

_slot_os = os

NEGATIVE_CASE = 'N06'
N03_INDEPENDENT_REVIEW_CLEAR = True
N03_SOURCE_REBIND_PENDING = False
SOURCE = 'fc59bb1254294d624f6779f928020edfe988dd5e'
PARENT = '1794a99f9f9857e923fdb2cc8cc81fe0bd5c8c38'
SOURCE_MAP = 'bf7334e8a273dc5693bb9f2226c4b44c90c988fb2f2c8d56ddb0b840adc494c6'
CAPTURE_SHA = '6e46183742801e864802ab8a7f3e64cd320139a500b7b215b17befe96616d2c1'
CAPTURE_PROJECTION_SHA = '8457d006fa5061f7249afd6038107907557732e6a51de3d8ba151923007e3ea0'
SOURCE_COUNT = 2872
SOURCE_TREE = 'c9ed8adcc08d050dc70af6885d31171038a36f86'
PINS = {'build-helper.sh': 'a0188f106b75e1320aefac501c1057e66da973bda8c9e08a862bfebddd65390e', 'helper_receipt_adapter.py': '603e2cf0047a39783f96fc8b2978d33237a58e4849868d6e7a44b3be92c6c844', 'helper_receipt_validator.py': '106c6b5202f4795a351ef76b259177571d1a0691bf9f2e908ad145155104b630', 'Program.cs': '02a28424a2d57c56213fae8618d50df1019986cadf89246e2ce11411a23c967a', 'NativePeerBroker.csproj': '92da6c96d4c88fae754ece54af775a00c1d713c7e44b9a76bda7cc768773514e', 'packages.lock.json': 'a29c6aa8cfb81874ff8bb78dc369d7416f28c9b8cc47e99592bfc019b20c41eb', 'prepare-root-inputs-v2.sh': '987d629db03b6fa7991107bd2acb0c22f3ecdfd4a39fd718d0d8dcd5e7eb60f1', 'checkpoint-n01-n02-v5.sh': '593c1935ae61a07c6ed6ab043bbbcd3f831f851f7fed43f0ea1fb88bbae71842', 'prepare-os-audit-v2.py': 'b345b2be77b359af23ade0b334caf76d7fdeb14bd2d4a3f0837beb21232df5e0', 'source-review.json': 'b84977a7bdb1839a498d7ba50888342a6984efdcc6e7a231500e10f79ee5f96b', 'acquire-inbound.sh': 'd106469bd4d781a09fa29158a73f142bb0678f64790012fc2baf55ee65e13c43', 'retain-native.sh': 'a6427331feee6fb9c065fa4f1e5461d7b5e2a3751bdbeb320840a63ac506fc3e'}
ROOT_PREFIX = ['/usr/bin/sudo', '-n', '/usr/bin/env', '-i', 'PATH=/usr/bin:/usr/sbin',
               'LANG=C', 'LC_ALL=C', '/usr/bin/bash', '--noprofile', '--norc']
LOG_CAP = 40 * 1024 * 1024


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
INTEGRATED_SOURCE_BINDING = {'commit': 'fc59bb1254294d624f6779f928020edfe988dd5e', 'parent': '1794a99f9f9857e923fdb2cc8cc81fe0bd5c8c38', 'tree': 'c9ed8adcc08d050dc70af6885d31171038a36f86', 'count': 2872, 'capture': '6e46183742801e864802ab8a7f3e64cd320139a500b7b215b17befe96616d2c1', 'projection': '8457d006fa5061f7249afd6038107907557732e6a51de3d8ba151923007e3ea0', 'map': 'bf7334e8a273dc5693bb9f2226c4b44c90c988fb2f2c8d56ddb0b840adc494c6', 'raw_map': '02b833b330267cafc6e12e75ac82439c6ee80a7e839f7fad27d7097fac8da843'}
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
    validate_integrated_build(receipt, 'N06')
    pipe = receipt.get('linux_pipe_regression')
    require(type(pipe) is dict and set(pipe) == {'method', 'class_name', 'counters', 'trx_sha256', 'unprivileged_library_behavior_only', 'root_factory_exercised', 'native_acceptance', 'runtime_basis'} and pipe.get('runtime_basis') == 'selected SDK dotnet host', 'pipe-regression-shape')
    require(set(receipt['artifacts']) == {'source','tool','runtime'}, 'build-map-set')
    for name,fact in receipt['artifacts'].items():
        tsv,raw=maps[name]
        require(sha(tsv)==fact['tsv_sha256'] and sha(raw)==fact['nodes_sha256'], 'build-map-digest')
        nodes=decode(raw)
        require(set(nodes)=={'schema','root_name','files','directories'} and nodes['schema']=='issue779-build-node-inventory-v1' and nodes['root_name']==name, 'node-schema')
        require(len(nodes['files'])==fact['file_count'] and len(nodes['files'])+len(nodes['directories'])==fact['node_count']
                and sum(row['bytes'] for row in nodes['files'].values())==fact['total_bytes'], 'node-counts')
    require(receipt['artifacts']['source']['file_count']==SOURCE_COUNT, 'source-count')




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
                handles.append(path.open('xb', buffering=0))
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
 [[ $(/usr/bin/stat -c %h -- "$source") == 1 && $(/usr/bin/stat -c %s -- "$source") -le 131072 ]]
 diagnostic_stage=source-digest
 printf '%s  %s\n' "$hash" "$source" | /usr/bin/sha256sum --check --strict --status
 diagnostic_stage=copy
 /usr/bin/dd if="$source" of="$target/$name" iflag=nofollow,nonblock,count_bytes oflag=nofollow conv=fsync,excl count=131073 status=none
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
[[ ! -e /run/appsurface-evidence-fixture && ! -L /run/appsurface-evidence-fixture ]]
[[ $(/usr/bin/stat -c '%u:%g:%a' -- "$root/control") == 0:0:700 ]]
/usr/bin/jq -ncS --arg g "$g" '{schema:"issue779-native-dispatch-marker-v1",generation:$g,fixture_parent_absent:true}' >"$root/control/fixture-dispatch-marker.json"
printf '%s  %s\n' "$hash" "$root/reviewed/checkpoint-n01-n02-v5.sh" | /usr/bin/sha256sum --check --strict --status
capture_fixture "$root/control/fixture-stdout.log" "$root/control/fixture-stderr.log" /usr/bin/bash --noprofile --norc "$root/reviewed/checkpoint-n01-n02-v5.sh" "$@"
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

ARCHIVE_NAMES = {'logs/n02-higher.file-limit', 'logs/n03/worker.stderr', 'negative-setup.json', 'logs/n02.credentials.json', 'logs/n03/worker-live.json', 'logs/n02-higher.stderr', 'logs/n03/result.json', 'worker-live.json', 'logs/n03/broker.stderr', 'logs/n02.stderr', 'logs/n03/worker.stdout', 'fixture-stderr.log', 'negative-kernel-observation.json', 'n05-worker.stderr', 'logs/kill.log', 'final-files.sha256', 'fixture-result.json', 'logs/observer.stdout', 'negative-post-join.json', 'fixture-stdout.log', 'logs/n02-higher.credentials.json', 'logs/n03/broker-live.json.argv', 'logs/n03/worker-peer.trace', 'n02-startup-limit-diagnostic.pending', 'negative-root-terminal.txt', 'logs/n03/broker.events.jsonl', 'logs/n03/failure.json', 'raw-evidence-plan.json', 'logs/n02-higher.io.trace', 'logs/n02.stdout', 'n02-startup-limit-diagnostic.json', 'raw-evidence-manifest.json', 'n05-worker.stdout', 'negative-account-ids.tsv', 'logs/n03/broker-live.json', 'logs/n01.stderr', 'logs/n02.io.trace', 'logs/n02-higher.limits', 'logs/n02.facts.json', 'logs/n03/broker-live-after.json', 'negative-root-failure.json', 'logs/n02-higher.trace', 'raw-evidence-summary.json', 'request-policy.sha256', 'logs/n03/broker-live-after.json.argv', 'logs/n02-higher.facts.json', 'logs/n02.trace', 'logs/observer.stderr', 'logs/n03/worker-live.json.argv', 'logs/n02.file-limit', 'negative-descriptor.json', 'negative-worker-raw.json', 'negative-worker-projection.json', 'logs/n02-higher.stdout', 'n06-worker.stderr', 'logs/stop.log', 'retention-selection.json', 'logs/n01.stdout', 'n06-worker.stdout', 'logs/n02.limits'}



"""Closed fixture data checks only. Never authenticate native actors or grant acceptance."""
import base64
import hashlib
import io
import json
import re
import tarfile

# Replaced by the renderer from the exact frozen a358 C# enum declarations.
ENUMS = {'EvidenceControlOperation': ['Ready', 'Stop', 'Wait', 'Exit', 'Run', 'Artifacts', 'Artifact', 'ApplicationStart', 'ResourceWait'], 'EvidenceNativeObservationErrorKind': ['Unknown', 'Admission', 'Accounts', 'OutputPipe', 'ControlLine', 'Cancelled', 'Timeout', 'Io', 'AccessDenied', 'Unsupported', 'Disposed', 'Argument', 'InvalidData', 'InvalidOperation'], 'EvidenceNativeObservationPhase': ['Unknown', 'CallerCancellation', 'ProtectedInput', 'JobDeadline', 'BackendConnect', 'OwnerActivation', 'Plan', 'AccountCreate', 'WorkspaceCreate', 'ListenerBind', 'WorkerCreate', 'WorkerStart', 'ServerCreate', 'ServerLifetime', 'ServerRun', 'ServerCompletion', 'WorkerExit', 'WorkerStop', 'WorkerCompletion', 'BeginTeardown', 'Custody', 'FileVerification', 'CleanupBegin', 'ServerCancel', 'ListenerClose', 'ServerJoin', 'WorkerJoin', 'CleanupCustody', 'AccountsClose', 'WorkerClose', 'CustodyClose', 'WorkspaceClose', 'OwnerFinalCheck', 'ServerLifetimeClose', 'JobClose', 'OwnerClose', 'InputClose', 'BackendClose', 'FinalDeadline', 'ResultCheck'], 'LinuxAccountPreparationStage': ['Unknown', 'NamesAbsent', 'ReserveUtility', 'UtilityCreate', 'UtilityExecute', 'IdentityRead', 'OwnershipVerify', 'CleanupCheck', 'CleanupNameCheck', 'CleanupUtility', 'FinalNamesAbsent', 'FinalOwnershipCheck'], 'LinuxAccountUtilityStage': ['Unknown', 'OwnerCheck', 'Pipes', 'BackendConnect', 'Recipe', 'Start', 'CloseWrites', 'UnitRead', 'TerminalCheck', 'ObservationDelay', 'BeginTeardown', 'Stop', 'GroupRead', 'OutputJoin', 'PipeDispose', 'BackendDispose', 'PhysicalSettlement', 'FinalOwnerCheck'], 'LinuxControlFailureStage': ['Unknown', 'PeerCheck', 'WorkerExitTask', 'RequestLifetime', 'AcceptLoop', 'HandlerJoin', 'CapacityWait', 'Accept', 'AcceptJoin', 'ControlRegistration', 'HandlerDispatch', 'RequestRead', 'RequestClassify', 'CleanupRegistration', 'Stop', 'WaitJoin', 'ReplyGate', 'ReadyAuthorization', 'ReadyClaim', 'ReadyData', 'ResponseData', 'WaitClaim', 'ExitClaim', 'ResponseWrite', 'ConnectionRelease', 'PostWriteCheck', 'ReplyCommit', 'HandlerFailureCommit', 'ReplyGateRelease', 'ControlRelease', 'CleanupRegistrationClose', 'ExitCommit', 'AcceptCancel', 'ListenerClose', 'PendingAcceptJoin', 'HandlersJoin', 'DescendantsStop', 'ControlsJoin', 'FinalCancellation', 'CleanupBound', 'OwnerCheck', 'ProtocolIncomplete', 'WorkerTerminalTaskCompleted', 'ReplyGateClose', 'ListenerState', 'ListenerCancellation', 'ListenerWorkspace', 'ListenerParent', 'ListenerSocketMetadata', 'ListenerSocketName', 'ListenerEndpoint', 'ListenerWorkerSelection', 'ListenerOwnerIdentity', 'ListenerWorkerIdentity', 'ListenerDescriptor', 'ListenerNativeAccept', 'ListenerAcceptedPeer', 'ProcessState', 'ProcessSelection', 'ProcessExpectedSample', 'ProcessExpectedPid', 'ProcessExpectedStartTime', 'ProcessExpectedLiveState', 'ProcessExpectedUid', 'ProcessExpectedGid', 'ProcessExpectedCgroup', 'ProcessInitialContinuity', 'ProcessRepeatedContinuity', 'ProcessReadContinuity', 'ProcessRetainedProcRoot', 'ProcessRetainedProcess', 'ProcessRetainedStatus', 'ProcessRetainedStat', 'ProcessRetainedCgroup', 'ProcessNamedProcRoot', 'ProcessNamedProcess', 'ProcessNamedStatus', 'ProcessNamedStat', 'ProcessNamedCgroup', 'ProcessFirstStatRead', 'ProcessFirstStatParse', 'ProcessStatusRead', 'ProcessStatusParse', 'ProcessCgroupRead', 'ProcessCgroupParse', 'ProcessLastStatRead', 'ProcessLastStatParse', 'ProcessFileSystemInspect', 'ProcessFileSystemType', 'ProcessDirectoryStat', 'ProcessDirectoryInode', 'ProcessDirectoryType', 'ProcessRetainedProcessDeviceMajor', 'ProcessRetainedProcessDeviceMinor', 'ProcessRetainedProcessInode', 'ProcessRetainedProcessUid', 'ProcessRetainedProcessGid', 'ProcessRetainedProcessMode', 'ProcessRetainedProcessMetadata', 'ListenerAdmissionDrain', 'ListenerSocketClose', 'ListenerNamedSocketClose', 'ListenerParentClose', 'ListenerNativeAcceptOperationAborted', 'ListenerNativeAcceptInterrupted', 'ListenerNativeAcceptConnectionAborted', 'ListenerNativeAcceptSocketOther', 'ListenerAcceptedClose'], 'LinuxCustodyNodeKind': ['Generation', 'Control', 'Broker', 'Output', 'RawResults', 'Slot', 'Descriptor', 'Socket', 'Plan', 'Manifest', 'Summary'], 'LinuxCustodyOperation': ['Unknown', 'Platform', 'Settlement', 'AccountOwner', 'NodeSelection', 'Open', 'RetainedStat', 'AncestorPolicy', 'OriginalPolicy', 'BaselineComparison', 'NamedOpen', 'NamedStat', 'NamedComparison', 'InventoryRead', 'InventoryDecode', 'InventoryPolicy', 'InventoryComparison', 'HashRead', 'HashEof', 'HashFinalize', 'HashComparison', 'Chown', 'Chmod', 'TerminalPolicy', 'OriginalOwnerClose', 'FileRead', 'FileVerification', 'AccountRelease', 'RootRecheck', 'Cancellation', 'HolderState', 'OwnerIdentity'], 'LinuxRunAccountFailure': ['InvalidData', 'IdentityMismatch', 'NssFailed', 'UnsupportedPlatform', 'OperationFailed', 'CleanupFailed'], 'LinuxRunAccountOperation': ['CreateUser', 'CreateResultsGroup', 'DeleteUser', 'DeleteGroup'], 'LinuxSystemdStartError': ['Other', 'AccessDenied', 'InvalidArgs', 'NoReply', 'ServiceUnknown', 'UnknownMethod', 'UnitExists', 'LoadFailed', 'NoSuchUnit'], 'SupervisionCustodyFailure': ['None', 'Cancelled', 'SettlementValidationFailed', 'PreflightFailed', 'MutationFailed', 'LocalCloseFailed', 'FinalNativeRecheckFailed']}
CODES = frozenset(('ASEVD402','ASEVD404','ASEVD407','ASEVD409','ASEVD410','ASEVD420','ASEVD421'))
ROOT_TERMINAL = b'ASEVD410: The protected empty Observation execution or final cleanup could not be established. Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.\n'
WORKER_TERMINAL = b'ASEVD409: Fresh output allocation or activation failed. Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.\n'
NEGATIVE_CAPS = {
    'negative-setup.json':1024, 'negative-post-join.json':1024,
    'negative-worker-projection.json':1024, 'negative-kernel-observation.json':4097,
    'negative-worker-raw.json':4097, 'negative-root-failure.json':1024,
    'negative-root-terminal.txt':1024, 'negative-descriptor.json':65536,
    'negative-account-ids.tsv':128,
    'n05-worker.stdout':0, 'n05-worker.stderr':2048,
    'n06-worker.stdout':0, 'n06-worker.stderr':2048,
}

def require_data(value):
    if not value: raise ValueError('negative-fixture-data-rejected')

def unique_pairs(items):
    result={}; folded=set()
    for key,value in items:
        require_data(key.casefold() not in folded)
        folded.add(key.casefold()); result[key]=value
    return result

def decode_data(raw):
    return json.loads(raw.decode('utf-8'),object_pairs_hook=unique_pairs,
                      parse_constant=lambda _:require_data(False))

def exact_object(value,keys):
    require_data(type(value) is dict and set(value)==set(keys))

def enum_data(value,name,nullable=False):
    require_data(value is None and nullable or type(value) is str and value in ENUMS[name])

def code_data(value):
    require_data(value is None or type(value) is str and value in CODES)

def family(value):
    enum_data(value['error_kind'],'EvidenceNativeObservationErrorKind')
    code_data(value['diagnostic_code'])

def validate_failure(value):
    """Exact v4 serializer member sets and finite values, including null projections."""
    exact_object(value,('schema','phase','error_kind','diagnostic_code','account_failure','control_failure','custody_failure'))
    require_data(value['schema']=='evidence-native-observation-failure-v4')
    enum_data(value['phase'],'EvidenceNativeObservationPhase'); family(value)
    account=value['account_failure']
    if account is not None:
        exact_object(account,('preparation_stage','utility_stage','operation','error_kind','diagnostic_code','account_code','exec_main_code','exec_main_status','dbus_category'))
        enum_data(account['preparation_stage'],'LinuxAccountPreparationStage')
        enum_data(account['utility_stage'],'LinuxAccountUtilityStage')
        enum_data(account['operation'],'LinuxRunAccountOperation',True);family(account)
        enum_data(account['account_code'],'LinuxRunAccountFailure',True)
        enum_data(account['dbus_category'],'LinuxSystemdStartError',True)
        code,status=account['exec_main_code'],account['exec_main_status']
        require_data(code is None and status is None or type(code) is int and 1<=code<=6 and type(status) is int and 0<=status<=255)
    control=value['control_failure']
    if control is not None:
        require_data(value['phase'] in ('ServerRun','ServerCompletion'))
        exact_object(control,('stage','operation','error_kind','diagnostic_code'))
        enum_data(control['stage'],'LinuxControlFailureStage')
        enum_data(control['operation'],'EvidenceControlOperation',True);family(control)
    custody=value['custody_failure']
    if custody is not None:
        require_data(value['phase'] in ('Custody','CleanupCustody','FileVerification','AccountsClose'))
        exact_object(custody,('procedure','node_kind','operation','error_kind','diagnostic_code'))
        enum_data(custody['procedure'],'SupervisionCustodyFailure')
        enum_data(custody['node_kind'],'LinuxCustodyNodeKind',True)
        enum_data(custody['operation'],'LinuxCustodyOperation');family(custody)
    return value

def validate_fixture_frames(raw,case):
    """Seven exact LF frames, real producer raw bytes; returns detached data only."""
    require_data(case in ('N05','N06') and type(raw) is bytes and 0<len(raw)<=14336)
    require_data(raw.endswith(b'\n') and b'\r' not in raw)
    lines=raw.splitlines(keepends=True)
    require_data(len(lines)==7 and all(x.endswith(b'\n') for x in lines))
    require_data(all(0<len(x)<=n for x,n in zip(lines,(1024,1024,1024,4097,4097,1024,1024))))
    require_data(lines[6]==ROOT_TERMINAL)
    setup,after,projection,kernel,worker,failure=[decode_data(x) for x in lines[:6]]
    schema='issue779-n05-slot-inspection-v1' if case=='N05' else 'issue779-n06-symlink-inspection-v1'
    keys=('schema','phase','parent','slot','sentinel','sha256') if case=='N05' else ('schema','phase','parent','target','link','target_sha256','sentinel','sha256')
    exact_object(setup,keys);exact_object(after,keys)
    require_data(setup['schema']==after['schema']==schema and setup['phase']=='setup' and after['phase']=='post_join')
    require_data({k:v for k,v in setup.items() if k!='phase'}=={k:v for k,v in after.items() if k!='phase'})
    exact_object(projection,('schema','origin','allocation','terminal_diagnostic','stdout_bytes','stderr_bytes','native_authority'))
    require_data(projection['schema']=='issue779-'+case.lower()+'-joined-worker-output-v1'
                 and projection['origin']=='joined-worker-output' and projection['terminal_diagnostic']=='ASEVD409'
                 and type(projection['stdout_bytes']) is int and projection['stdout_bytes']==0
                 and type(projection['stderr_bytes']) is int and 0<projection['stderr_bytes']<=2048
                 and projection['native_authority'] is False)
    alloc=projection['allocation'];exact_object(alloc,('schema','phase','operation','stageOutcome','terminalCode','errorClass','nativeErrno'))
    require_data(alloc['schema']=='evidence-allocation-failure-v1' and alloc['phase']=='Allocation'
                 and alloc['operation']=='CreateSlot' and alloc['stageOutcome']=='Failed'
                 and alloc['terminalCode']=='StageFailed' and alloc['errorClass']=='Io'
                 and (alloc['nativeErrno'] is None or type(alloc['nativeErrno']) is int and 1<=alloc['nativeErrno']<=4095))
    exact_object(worker,('schema','stdout_base64','stderr_base64','stdout_bytes','stderr_bytes','native_authority'))
    require_data(worker['schema']=='issue779-'+case.lower()+'-joined-worker-raw-v1' and worker['native_authority'] is False
                 and worker['stdout_base64']=='' and type(worker['stdout_bytes']) is int and worker['stdout_bytes']==0
                 and type(worker['stderr_bytes']) is int and 0<worker['stderr_bytes']<=2048
                 and type(worker['stderr_base64']) is str)
    stdout=base64.b64decode(worker['stdout_base64'],validate=True)
    stderr=base64.b64decode(worker['stderr_base64'],validate=True)
    require_data(base64.b64encode(stdout).decode('ascii')==worker['stdout_base64']
                 and base64.b64encode(stderr).decode('ascii')==worker['stderr_base64']
                 and stdout==b'' and len(stderr)==worker['stderr_bytes']==projection['stderr_bytes'])
    worker_lines=stderr.splitlines(keepends=True)
    require_data(len(worker_lines)==2 and worker_lines[1]==WORKER_TERMINAL and decode_data(worker_lines[0])==alloc)
    validate_failure(failure)
    return {'lines':lines,'kernel':kernel,'stdout':stdout,'stderr':stderr,'failure':failure}

def inspect_retention(data,allowed):
    """Canonical bounded USTAR whitelist inspection only, not a native result verifier."""
    require_data(type(data) is bytes and 0<len(data)<=33619968)
    names=[];total=0;values={};entries=[]
    with tarfile.open(fileobj=io.BytesIO(data),mode='r:') as archive:
        for member in archive:
            require_data(member.name in allowed and member.name not in names and member.isfile()
                         and member.mode==0o600 and member.uid==member.gid==member.mtime==0
                         and not member.linkname and not member.pax_headers and not member.uname and not member.gname
                         and 0<=member.size<=NEGATIVE_CAPS.get(member.name,4096 if member.name in ('fixture-result.json','retention-selection.json','logs/n03/result.json','logs/n03/worker-live.json','logs/n03/broker-live.json','logs/n03/broker-live-after.json') else 8388608))
            names.append(member.name);total+=member.size;require_data(total<=33554432+4096 and len(names)<=len(allowed))
            stream=archive.extractfile(member);require_data(stream is not None)
            with stream: content=stream.read(member.size+1)
            require_data(len(content)==member.size);values[member.name]=content;entries.append((member.name,content))
    require_data(names==sorted(names) and 'retention-selection.json' in values)
    selection=decode_data(values['retention-selection.json'])
    exact_object(selection,('schema','selection_status','data_file_count','data_bytes','missing_fixed_file_count'))
    require_data(selection['schema']=='issue779-native-retention-selection-v1'
                 and selection['selection_status'] in ('parent-absent','parent-empty','one-namespace')
                 and type(selection['data_file_count']) is int and selection['data_file_count']==len(names)-1
                 and type(selection['data_bytes']) is int and selection['data_bytes']==total-len(values['retention-selection.json'])
                 and type(selection['missing_fixed_file_count']) is int and selection['missing_fixed_file_count']>=0)
    canonical=io.BytesIO()
    with tarfile.open(fileobj=canonical,mode='w:',format=tarfile.USTAR_FORMAT) as rebuilt:
        for name,content in entries:
            info=tarfile.TarInfo(name);info.size=len(content);info.mode=0o600;info.uid=info.gid=info.mtime=0
            rebuilt.addfile(info,io.BytesIO(content))
    require_data(canonical.getvalue()==data)
    return values


"""Strict detached negative-record consistency checks; no OS access or native authority."""
import json
import re

MAX_JSON_BYTES = 4096
MAX_STREAM_BYTES = 1024 * 1024
MAX_RECEIVED_LIMIT = 16 * 1024 * 1024
UINT32_MAX = (1 << 32) - 1
UINT64_MAX = (1 << 64) - 1
TOP = frozenset(('schema', 'generation', 'worker_unit', 'process', 'ready', 'terminal',
                 'cgroup', 'pumps', 'joins', 'observation_only', 'native_authority', 'native_acceptance'))


class KernelRecordRejected(ValueError):
    """Closed data rejection, deliberately retaining no supplied values or cause text."""


def _reject():
    raise KernelRecordRejected('negative-kernel-data-rejected')


def _require(value):
    if not value:
        _reject()


def _uint(value, low, high):
    _require(type(value) is int and low <= value <= high)


def _digest(value):
    _require(type(value) is str and re.fullmatch('[0-9a-f]{64}', value) is not None)


def _object(value, keys):
    _require(type(value) is dict and set(value) == set(keys))


def _pairs(pairs):
    result = {}
    folded = set()
    for key, value in pairs:
        canonical = key.casefold()
        _require(canonical not in folded)
        folded.add(canonical)
        result[key] = value
    return result


def _stream(value, expected_sha256, expected_bytes):
    _object(value, ('received_bytes', 'retained_bytes', 'discarded_bytes', 'eof', 'failure', 'sha256'))
    for key in ('received_bytes', 'retained_bytes'):
        _uint(value[key], 0, MAX_STREAM_BYTES)
        _require(value[key] == expected_bytes)
    _uint(value['discarded_bytes'], 0, 0)
    _require(value['eof'] is True and value['failure'] == 'None')
    _digest(value['sha256'])
    _require(value['sha256'] == expected_sha256)


def check_kernel_record(raw, *, expected_generation, expected_uid, expected_gid,
                        expected_pid, expected_starttime_ticks, expected_descriptor_sha256,
                        expected_stdout_sha256, expected_stdout_bytes,
                        expected_stderr_sha256, expected_stderr_bytes):
    """Return True for detached data consistency only; never issue admission or acceptance.

    ``raw`` is exactly a JSON object of at most 4096 UTF-8 bytes, optionally followed
    by one LF. Expected values are caller data, not authenticated identities. The
    original source-built holder and root-private stream capture must independently
    establish provenance. No path, command, timer, descriptor or mutable lease is
    consumed or created. Every rejection has one fixed message and no chained cause.
    Both stream hashes/counts must come from full captured bytes outside this parser.
    The negative-case terminal is fixed CLD_EXITED(1), status 1; zero and signals reject.
    """
    try:
        _require(type(raw) is bytes and 0 < len(raw) <= MAX_JSON_BYTES + 1)
        body = raw[:-1] if raw.endswith(b'\n') else raw
        _require(0 < len(body) <= MAX_JSON_BYTES and body.startswith(b'{') and body.endswith(b'}'))
        _require(type(expected_generation) is str
                 and re.fullmatch('[0-9a-f]{32}', expected_generation) is not None
                 and expected_generation != '0' * 32)
        _uint(expected_uid, 1, UINT32_MAX - 1)
        _uint(expected_gid, 1, UINT32_MAX - 1)
        _uint(expected_pid, 1, (1 << 31) - 1)
        _uint(expected_starttime_ticks, 1, UINT64_MAX)
        for value in (expected_descriptor_sha256, expected_stdout_sha256, expected_stderr_sha256):
            _digest(value)
        _uint(expected_stdout_bytes, 0, MAX_STREAM_BYTES)
        _uint(expected_stderr_bytes, 0, MAX_STREAM_BYTES)
        value = json.loads(body.decode('utf-8', errors='strict'), object_pairs_hook=_pairs,
                           parse_constant=lambda _: _reject())
        _object(value, TOP)
        _require(value['schema'] == 'issue779-negative-kernel-observation-v1')
        _require(value['generation'] == expected_generation)
        unit = 'appsurface-evidence-worker-' + expected_generation + '.service'
        group = '/system.slice/' + unit
        _require(value['worker_unit'] == unit)
        process = value['process']
        _object(process, ('pid', 'starttime_ticks', 'uid4', 'gid4', 'control_group'))
        _uint(process['pid'], 1, (1 << 31) - 1)
        _uint(process['starttime_ticks'], 1, UINT64_MAX)
        _require(process['pid'] == expected_pid and process['starttime_ticks'] == expected_starttime_ticks)
        for name, expected in (('uid4', expected_uid), ('gid4', expected_gid)):
            ids = process[name]
            _require(type(ids) is list and len(ids) == 4)
            for identity in ids:
                _uint(identity, 1, UINT32_MAX - 1)
                _require(identity == expected)
        _require(process['control_group'] == group)
        ready = value['ready']
        _object(ready, ('committed', 'descriptor_sha256'))
        _require(ready['committed'] is True)
        _digest(ready['descriptor_sha256'])
        _require(ready['descriptor_sha256'] == expected_descriptor_sha256)
        terminal = value['terminal']
        _object(terminal, ('exec_main_pid', 'exec_main_code', 'exec_main_status', 'active_state', 'sub_state'))
        _uint(terminal['exec_main_pid'], 1, (1 << 31) - 1)
        _uint(terminal['exec_main_code'], 1, 1)
        _uint(terminal['exec_main_status'], 1, 1)
        _require(terminal['exec_main_pid'] == expected_pid)
        _require((terminal['active_state'], terminal['sub_state']) in
                 (('active', 'exited'), ('inactive', 'dead'), ('failed', 'failed')))
        cg = value['cgroup']
        _object(cg, ('exists', 'populated', 'frozen', 'device_major', 'device_minor', 'inode'))
        _require(type(cg['exists']) is bool)
        if cg['exists']:
            _require(cg['populated'] is False and cg['frozen'] is False)
            _uint(cg['device_major'], 0, UINT32_MAX)
            _uint(cg['device_minor'], 0, UINT32_MAX)
            _uint(cg['inode'], 1, UINT64_MAX)
        else:
            _require(all(cg[k] is None for k in ('populated', 'frozen', 'device_major', 'device_minor', 'inode')))
        pumps = value['pumps']
        _object(pumps, ('stdout', 'stderr', 'received_bytes', 'received_byte_limit', 'failure', 'discarded_bytes'))
        _stream(pumps['stdout'], expected_stdout_sha256, expected_stdout_bytes)
        _stream(pumps['stderr'], expected_stderr_sha256, expected_stderr_bytes)
        _uint(pumps['received_bytes'], 0, MAX_RECEIVED_LIMIT)
        _uint(pumps['received_byte_limit'], 1, MAX_RECEIVED_LIMIT)
        _uint(pumps['discarded_bytes'], 0, 0)
        _require(pumps['received_bytes'] == expected_stdout_bytes + expected_stderr_bytes
                 and pumps['received_bytes'] <= pumps['received_byte_limit'] and pumps['failure'] == 'None')
        _object(value['joins'], ('startup', 'pending_stop', 'monitor', 'server', 'pumps'))
        _require(all(x is True for x in value['joins'].values()))
        _require(value['observation_only'] is True and value['native_authority'] is False
                 and value['native_acceptance'] is False)
        return True
    except (ValueError, TypeError, KeyError, OverflowError, RecursionError, UnicodeError):
        raise KernelRecordRejected('negative-kernel-data-rejected') from None


def inspect_archive(data):
    """Inspect bounded root-retained canonical bytes; raw data remains private."""
    raw = inspect_retention(data, ARCHIVE_NAMES)
    selected = ('fixture-result.json', 'retention-selection.json', 'logs/n03/result.json',
                'logs/n03/worker-live.json', 'logs/n03/broker-live.json', 'logs/n03/broker-live-after.json')
    require_data(all(len(raw[name]) <= 4096 for name in selected if name in raw))
    values = {name: decode_data(raw[name]) for name in selected if name in raw}
    if 'logs/n03/result.json' in raw:
        values['n03-result-sha256'] = sha(raw['logs/n03/result.json'])
    values['_retained_raw'] = raw
    return values


def validate_native_result(value):
    """Require the fixed negative result; it grants no positive custody or Trusted status."""
    fields = {'schema', 'generation', 'source_revision', 'base_revision', 'control',
              'root_launch_join_status', 'root_stdout_bytes', 'worker_terminal_from_original_monitor',
              'original_csharp_joins_required', 'account_disposition', 'native_acceptance',
              'observation_only', 'policy_sha256', 'other_controls'}
    require(type(value) is dict and set(value) == fields, 'negative-case-closed-fields')
    require(value['schema'] == 'issue779-negative-slot-fixture-v1'
            and value['source_revision'] == SOURCE and value['control'] == NEGATIVE_CASE
            and value['base_revision'] == PARENT
            and type(value['generation']) is str and re.fullmatch('[0-9a-f]{32}', value['generation'])
            and value['generation'] != '0' * 32
            and type(value['policy_sha256']) is str and re.fullmatch('[0-9a-f]{64}', value['policy_sha256'])
            and type(value['root_launch_join_status']) is int and value['root_launch_join_status'] == 1
            and type(value['root_stdout_bytes']) is int and value['root_stdout_bytes'] == 0
            and value['worker_terminal_from_original_monitor'] is True
            and value['original_csharp_joins_required'] is True
            and value['account_disposition'] == 'preserved-quarantined'
            and value['native_acceptance'] is False and value['observation_only'] is True
            and value['other_controls'] == 'not-qualified-by-this-fixture', 'negative-case-result')


SLOT_SENTINEL_BYTES = b'issue779-owned-slot-sentinel\n'

SLOT_SENTINEL_SHA256 = hashlib.sha256(SLOT_SENTINEL_BYTES).hexdigest()

SLOT_FRAME_MEMBERS = (
    'negative-setup.json',
    'negative-post-join.json',
    'negative-worker-projection.json',
    'negative-kernel-observation.json',
    'negative-worker-raw.json',
    'negative-root-failure.json',
    'negative-root-terminal.txt',
)

SLOT_DESCRIPTOR_KEYS = frozenset((
    'schema', 'run_id', 'worker_pid', 'broker_pid', 'worker_uid', 'worker_gid',
    'subject_uid', 'subject_gid', 'unit', 'cgroup', 'job_deadline_utc',
    'tool_root', 'subject_root', 'output_parent', 'output_slot', 'dotnet_path',
    'test_output_root', 'policy_file', 'mode', 'socket_path', 'descriptor_path',
    'entry_sha256', 'policy_sha256', 'base_revision', 'subject_revision',
    'workflow_identity', 'provider', 'platform', 'proof_digest',
    'output_parent_identity', 'observation_profile_ids', 'observation_producer_ids',
    'paths', 'admission_seconds', 'start_seconds', 'collection_seconds',
    'cleanup_seconds', 'stopping_seconds', 'diff_file', 'diff_sha256', 'solution',
))

SLOT_IDENTITY_KEYS = ('device_major', 'device_minor', 'inode', 'uid', 'gid', 'mode', 'links', 'length')

def _slot_identity(value):
    exact_object(value, SLOT_IDENTITY_KEYS)
    for key in ('device_major', 'device_minor'):
        require_data(type(value[key]) is int and 0 <= value[key] <= UINT32_MAX)
    require_data(type(value['inode']) is int and 0 < value['inode'] <= UINT64_MAX)
    require_data(type(value['uid']) is int and 0 <= value['uid'] < UINT32_MAX)
    require_data(type(value['gid']) is int and 0 <= value['gid'] < UINT32_MAX)
    require_data(type(value['mode']) is int and 0 <= value['mode'] <= UINT32_MAX)
    require_data(type(value['links']) is int and 0 < value['links'] <= UINT32_MAX)
    require_data(type(value['length']) is int and 0 <= value['length'] <= UINT64_MAX)

def _slot_descriptor(raw, generation, uid, gid, subject_uid, subject_gid):
    require_data(type(raw) is bytes and 0 < len(raw) <= 65536 and b'\n' not in raw and b'\r' not in raw)
    value = decode_data(raw)
    exact_object(value, SLOT_DESCRIPTOR_KEYS)
    require_data(value['schema'] == 'evidence-worker-linux-v1'
                 and value['run_id'] == 'csharp/' + generation)
    require_data(type(value['worker_pid']) is int and 1 <= value['worker_pid'] < (1 << 31)
                 and type(value['broker_pid']) is int and 1 <= value['broker_pid'] < (1 << 31)
                 and value['worker_pid'] != value['broker_pid'])
    for key in ('worker_uid', 'worker_gid', 'subject_uid', 'subject_gid'):
        require_data(type(value[key]) is int and 0 < value[key] < UINT32_MAX)
    require_data(value['worker_uid'] == uid and value['worker_gid'] == gid
                 and value['subject_uid'] == subject_uid and value['subject_gid'] == subject_gid)
    unit = 'appsurface-evidence-worker-' + generation + '.service'
    require_data(value['unit'] == unit and value['cgroup'] == '/system.slice/' + unit
                 and value['output_slot'] == 'evidence' and value['mode'] == 'observation'
                 and value['provider'] == 'github-actions' and value['platform'] == 'linux-x64'
                 and value['proof_digest'] == '')
    string_fields = SLOT_DESCRIPTOR_KEYS - {
        'worker_pid', 'broker_pid', 'worker_uid', 'worker_gid', 'subject_uid', 'subject_gid',
        'output_parent_identity', 'observation_profile_ids', 'observation_producer_ids', 'paths',
        'admission_seconds', 'start_seconds', 'collection_seconds', 'cleanup_seconds', 'stopping_seconds',
        'diff_file', 'diff_sha256', 'solution',
    }
    require_data(all(type(value[key]) is str for key in string_fields))
    require_data(all(value[key] is None or type(value[key]) is str for key in ('diff_file', 'diff_sha256', 'solution')))
    for key in ('observation_profile_ids', 'observation_producer_ids', 'paths'):
        require_data(type(value[key]) is list and all(type(item) is str for item in value[key]))
    for key in ('admission_seconds', 'start_seconds', 'collection_seconds', 'cleanup_seconds', 'stopping_seconds'):
        require_data(type(value[key]) is int and value[key] >= 0)
    for key in ('entry_sha256', 'policy_sha256'):
        require_data(re.fullmatch('[0-9a-f]{64}', value[key]) is not None)
    require_data(_slot_os.path.isabs(value['output_parent'])
                 and _slot_os.path.isabs(value['test_output_root'])
                 and _slot_os.path.normpath(_slot_os.path.join(value['output_parent'], '../raw-results'))
                 == _slot_os.path.normpath(value['test_output_root']))
    exact_object(value['output_parent_identity'], ('device_major', 'device_minor', 'inode', 'uid', 'gid'))
    parent = value['output_parent_identity']
    require_data(type(parent['device_major']) is int and 0 <= parent['device_major'] <= UINT32_MAX
                 and type(parent['device_minor']) is int and 0 <= parent['device_minor'] <= UINT32_MAX
                 and type(parent['inode']) is int and 0 < parent['inode'] <= UINT64_MAX
                 and type(parent['uid']) is int and 0 < parent['uid'] < UINT32_MAX
                 and type(parent['gid']) is int and 0 < parent['gid'] < UINT32_MAX
                 and parent['uid'] == uid and parent['gid'] == gid)
    return value

def _slot_account_ids(raw, uid, gid, subject_uid, subject_gid):
    """The actual root fixture emits one ordered five-column TSV record."""
    require_data(type(raw) is bytes and 0 < len(raw) <= 128
                 and raw.endswith(b'\n') and raw.count(b'\n') == 1 and b'\r' not in raw)
    fields = raw[:-1].split(b'\t')
    require_data(len(fields) == 5 and all(re.fullmatch(rb'[1-9][0-9]*', field) is not None for field in fields))
    numbers = tuple(int(field) for field in fields)
    require_data(numbers[:4] == (uid, gid, subject_uid, subject_gid)
                 and all(0 < number < UINT32_MAX for number in numbers)
                 and len({gid, subject_gid, numbers[4]}) == 3)
    return numbers[4]

def _slot_validate_metadata(setup, after, case, uid, gid, subject_uid, results_gid):
    for value in (setup, after):
        parent = value['parent']
        sentinel = value['sentinel']
        _slot_identity(parent)
        _slot_identity(sentinel)
        require_data(parent['uid'] == uid and parent['gid'] == gid and parent['mode'] == 0x41c0)
        require_data(parent['device_major'] == sentinel['device_major']
                     and parent['device_minor'] == sentinel['device_minor'])
        require_data(sentinel['mode'] == 0x8180 and sentinel['links'] == 1
                     and sentinel['length'] == len(SLOT_SENTINEL_BYTES))
        require_data(value['sha256'] == SLOT_SENTINEL_SHA256)
        if case == 'N05':
            slot = value['slot']
            _slot_identity(slot)
            require_data(slot['uid'] == uid and slot['gid'] == gid and slot['mode'] == 0x41c0
                         and slot['links'] >= 2 and slot['inode'] != parent['inode']
                         and slot['device_major'] == parent['device_major']
                         and slot['device_minor'] == parent['device_minor']
                         and sentinel['inode'] not in (parent['inode'], slot['inode'])
                         and sentinel['uid'] == uid and sentinel['gid'] == gid
                         and sentinel['device_major'] == slot['device_major']
                         and sentinel['device_minor'] == slot['device_minor'])
        else:
            target = value['target']
            link = value['link']
            _slot_identity(target)
            _slot_identity(link)
            require_data(target['uid'] == subject_uid and target['gid'] == results_gid
                         and target['mode'] == 0x41c8 and target['links'] >= 2
                         and target['inode'] != parent['inode']
                         and target['device_major'] == parent['device_major']
                         and target['device_minor'] == parent['device_minor'])
            require_data(link['uid'] == 0 and link['gid'] == 0 and link['mode'] == 0xa1ff
                         and link['links'] == 1 and link['length'] == len(b'../raw-results')
                         and link['inode'] not in (parent['inode'], target['inode'])
                         and link['device_major'] == parent['device_major']
                         and link['device_minor'] == parent['device_minor'])
            require_data(sentinel['inode'] not in (parent['inode'], target['inode'], link['inode'])
                         and sentinel['uid'] == 0 and sentinel['gid'] == 0
                         and sentinel['device_major'] == target['device_major']
                         and sentinel['device_minor'] == target['device_minor']
                         and value['target_sha256'] == SLOT_SENTINEL_SHA256)

def validate_slot_archive(values, case, expected_generation, expected_uid, expected_gid,
                          expected_subject_uid, expected_subject_gid):
    """Validate fixed retained N05/N06 members as detached data; never creates native credit.

    values is the raw-member mapping returned by inspect_retention. The exact seven
    frame members, descriptor, account ID map, and case-specific full stdout/stderr
    bytes are consumed by fixed archive names. This adapter has no caller override map,
    alias lookup, process, filesystem, Git, or network operation. Start time is
    read from the kernel record itself because the retained descriptor has no
    start-time field; this check is only consistency. A caller must independently
    authenticate the root-private archive and original-worker provenance.
    """
    try:
        require_data(type(values) is dict and type(case) is str and case in ('N05', 'N06'))
        require_data(type(expected_generation) is str
                     and re.fullmatch('[0-9a-f]{32}', expected_generation) is not None
                     and expected_generation != '0' * 32)
        for number in (expected_uid, expected_gid, expected_subject_uid, expected_subject_gid):
            require_data(type(number) is int and 0 < number < UINT32_MAX)
        require_data(expected_uid != expected_subject_uid and expected_gid != expected_subject_gid)
        stdout_name = case.lower() + '-worker.stdout'
        stderr_name = case.lower() + '-worker.stderr'
        opposite = 'n06' if case == 'N05' else 'n05'
        required = set(SLOT_FRAME_MEMBERS) | {
            'negative-descriptor.json', 'negative-account-ids.tsv', stdout_name, stderr_name,
        }
        require_data(required <= set(values))
        require_data(opposite + '-worker.stdout' not in values and opposite + '-worker.stderr' not in values)
        require_data(all(type(values[name]) is bytes for name in required))
        caps = {
            'negative-setup.json': 1024, 'negative-post-join.json': 1024,
            'negative-worker-projection.json': 1024, 'negative-kernel-observation.json': 4097,
            'negative-worker-raw.json': 4097, 'negative-root-failure.json': 1024,
        }
        for name, cap in caps.items():
            raw = values[name]
            require_data(0 < len(raw) <= cap and raw.endswith(b'\n')
                         and raw.count(b'\n') == 1 and b'\r' not in raw)
        require_data(values['negative-root-terminal.txt'] == ROOT_TERMINAL)
        frame_bytes = b''.join(values[name] for name in SLOT_FRAME_MEMBERS)
        frames = validate_fixture_frames(frame_bytes, case)
        setup = decode_data(values['negative-setup.json'])
        after = decode_data(values['negative-post-join.json'])
        descriptor = _slot_descriptor(values['negative-descriptor.json'], expected_generation,
                                      expected_uid, expected_gid, expected_subject_uid, expected_subject_gid)
        results_gid = _slot_account_ids(values['negative-account-ids.tsv'], expected_uid, expected_gid,
                                        expected_subject_uid, expected_subject_gid)
        _slot_validate_metadata(setup, after, case, expected_uid, expected_gid, expected_subject_uid, results_gid)
        parent = setup['parent']
        descriptor_parent = descriptor['output_parent_identity']
        require_data(all(parent[key] == descriptor_parent[key]
                         for key in ('device_major', 'device_minor', 'inode', 'uid', 'gid')))
        kernel = frames['kernel']
        process = kernel['process']
        stdout = values[stdout_name]
        stderr = values[stderr_name]
        require_data(stdout == frames['stdout'] and stderr == frames['stderr'])
        require_data(stdout == b'' and len(stderr) > 0)
        require_data(check_kernel_record(
            values['negative-kernel-observation.json'],
            expected_generation=expected_generation,
            expected_uid=expected_uid,
            expected_gid=expected_gid,
            expected_pid=descriptor['worker_pid'],
            expected_starttime_ticks=process['starttime_ticks'],
            expected_descriptor_sha256=hashlib.sha256(values['negative-descriptor.json']).hexdigest(),
            expected_stdout_sha256=hashlib.sha256(stdout).hexdigest(),
            expected_stdout_bytes=len(stdout),
            expected_stderr_sha256=hashlib.sha256(stderr).hexdigest(),
            expected_stderr_bytes=len(stderr),
        ) is True)
        return True
    except (ValueError, TypeError, KeyError, OverflowError, RecursionError, UnicodeError, OSError):
        raise ValueError('slot-archive-data-rejected') from None

def validate_negative_archive(values):
    """Bind the fixture result, original root stream, slot metadata and kernel record."""
    raw=values['_retained_raw']; result=values['fixture-result.json']
    validate_native_result(result)
    require(result['source_revision']==SOURCE and result['base_revision']==PARENT
            and result['control']==NEGATIVE_CASE and result['root_launch_join_status']==1
            and result['native_acceptance'] is False and result['observation_only'] is True, 'slot-fixture-result-binding')
    parts=tuple(SLOT_FRAME_MEMBERS)
    needed=set(parts)|{'negative-descriptor.json','negative-account-ids.tsv',
        NEGATIVE_CASE.lower()+'-worker.stdout',NEGATIVE_CASE.lower()+'-worker.stderr','logs/n01.stdout','logs/n01.stderr'}
    require(needed <= set(raw), 'slot-private-record-missing')
    frame=b''.join(raw[name] for name in parts)
    require(raw['logs/n01.stdout']==b'' and raw['logs/n01.stderr']==frame, 'slot-original-root-stream-binding')
    descriptor=decode_data(raw['negative-descriptor.json'])
    require(descriptor.get('policy_sha256')==result['policy_sha256'], 'slot-policy-binding')
    require(validate_slot_archive(raw, NEGATIVE_CASE, result['generation'], descriptor['worker_uid'],
        descriptor['worker_gid'], descriptor['subject_uid'], descriptor['subject_gid']) is True, 'slot-archive')



def validate_helper_handoff(value, recipe, private_diagnostics_raw):
    root = value.get('helper_root') if type(value) is dict else None
    require(type(root) is str and root.startswith('/')
            and len(root.encode('utf-8')) <= 4096
            and not re.search('[\\x00-\\x1f\\x7f]', root), 'helper-root-path')
    try:
        helper_receipt_adapter.validate_helper_receipt(value, 'N03', recipe, private_diagnostics_raw)
    except helper_receipt_adapter.HelperReceiptError:
        require(False, 'helper-receipt-adapter')


def validate_helper_private_diagnostics(value, raw):
    """Pin the private sidecar bytes without adding their content to public results."""
    require(type(raw) is bytes and 0 < len(raw) <= 1048576
            and sha(raw) == value['private_diagnostics_sha256'], 'helper-private-diagnostics-pin')


"""Closed data validation candidate. It issues no admission, lease or native proof."""

KEYS = {"asevd402_observed", "broker_exit", "broker_pid", "control", "descriptor_supplied", "kernel_peer_bound_to_owned_worker", "native_acceptance", "owned_process_groups_joined", "ready_request_observed", "request_bytes", "schema", "sent_bytes", "source_commit", "status", "stream_custody", "systemd_worker_authority", "worker_exit", "worker_pid", "worker_stdout_bytes"}

def require_n03_result(raw, source):
    def reject():
        raise ValueError("n03-closed-data-rejected")
    def unique(rows):
        result = {}
        for key, value in rows:
            if key in result: reject()
            result[key] = value
        return result
    def invalid_constant(value):
        reject()
    if not isinstance(raw, bytes) or not 0 < len(raw) <= 4096 or raw[-1:] != b"\n": reject()
    value = json.loads(raw, object_pairs_hook=unique, parse_constant=invalid_constant)
    if not isinstance(value, dict) or set(value) != KEYS: reject()
    expected = {"schema":"issue779-n03-peer-observation-v1", "control":"N03", "status":"observed", "source_commit":source, "worker_exit":1, "broker_exit":0, "worker_stdout_bytes":0, "asevd402_observed":True, "kernel_peer_bound_to_owned_worker":True, "request_bytes":0, "sent_bytes":0, "ready_request_observed":False, "descriptor_supplied":False, "owned_process_groups_joined":True, "native_acceptance":False, "systemd_worker_authority":False, "stream_custody":"private-files-after-original-process-reap"}
    if any(type(value[k]) is not type(v) or value[k] != v for k,v in expected.items()): reject()
    if any(type(value[k]) is not int or not 0 < value[k] <= 2147483647 for k in ("worker_pid", "broker_pid")): reject()
    if value["worker_pid"] == value["broker_pid"]: reject()
    # Caller must independently require fixture numeric0, exact three case receipt,
    # authenticated archive/custody, raw peer trace and live identity bindings.
    return value


def validate_n03_archive(values):
    require('logs/n03/result.json' in values, 'n03-result-missing')
    result = values['logs/n03/result.json']
    require_n03_result((json.dumps(result,separators=(',',':'))+'\n').encode(), SOURCE)
    require(values['fixture-result.json']['n03_result_sha256']==values['n03-result-sha256'], 'n03-result-binding')
    fields={'schema','pid','start_ticks','uid4','gid4','cgroup','exact_managed_argv','image_pinned','empty_groups','no_new_privs','effective_capabilities_zero'}
    for name,pid in [('worker-live.json',result['worker_pid']),('broker-live.json',result['broker_pid']),('broker-live-after.json',result['broker_pid'])]:
        row=values.get('logs/n03/'+name)
        require(type(row) is dict and set(row)==fields and row['schema']=='issue779-n03-owned-image-sample-v1' and type(row['pid']) is int and row['pid']==pid and type(row['start_ticks']) is str and re.fullmatch('[1-9][0-9]*',row['start_ticks']) and row['exact_managed_argv'] is True and row['image_pinned'] is True and row['empty_groups'] is True and type(row['no_new_privs']) is int and row['no_new_privs']==1 and row['effective_capabilities_zero'] is True, 'n03-live-binding')
        for field in ('uid4','gid4'):
            parts=row[field].split(':');require(len(parts)==4 and all(re.fullmatch('[1-9][0-9]*',x) for x in parts) and len(set(parts))==1,'n03-credentials')
        require(re.fullmatch(r'0::/system.slice/issue779-native-tool-[0-9a-f]{32}-[0-9]+\.service',row['cgroup']), 'n03-cgroup')
    require(values['logs/n03/broker-live.json']==values['logs/n03/broker-live-after.json'], 'n03-broker-continuity')
    worker=values['logs/n03/worker-live.json'];broker=values['logs/n03/broker-live.json']
    require(worker['uid4']!=broker['uid4'] and worker['gid4']==broker['gid4']=='65534:65534:65534:65534' and worker['cgroup']==broker['cgroup'], 'n03-selection-continuity')


def load_verified_helper_adapter(reviewed, deadline):
    """Evaluate only the exact bounded adapter bytes verified for this runner.

    Loading happens after the complete input-pin loop. Re-reading with the
    retained-file reader and checking its digest again prevents a later path
    substitution from supplying the executable module bytes.
    """
    adapter_path = reviewed / 'helper_receipt_adapter.py'
    raw = read(adapter_path, 131072, deadline)
    require(sha(raw) == PINS['helper_receipt_adapter.py'], 'helper-adapter-pin')
    module = types.ModuleType('issue779_verified_helper_receipt_adapter')
    module.__file__ = str(adapter_path)
    exec(compile(raw, str(adapter_path), 'exec'), module.__dict__)
    return module


def main():
    global helper_receipt_adapter
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--execute', action='store_true')
    parser.add_argument('--workspace', required=True)
    parser.add_argument('--build-root', required=True)
    parser.add_argument('--helper-build-root', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--reviewed-script-sha256', required=True)
    args = parser.parse_args()
    job_end = int(os.environ['JOB_DEADLINE_MONOTONIC_NS']) / 1_000_000_000
    start = time.monotonic()
    final = min(job_end, start + 900)
    require(N03_INDEPENDENT_REVIEW_CLEAR and not N03_SOURCE_REBIND_PENDING, 'n03-review-or-source-rebind-pending')
    require(args.execute and sys.platform == 'linux' and os.uname().machine == 'x86_64'
            and os.geteuid() > 0 and os.getegid() > 0 and final - start > 850, 'native-platform-or-job-budget')
    workspace, build, output = map(Path, (args.workspace, args.build_root, args.output))
    require(all(path.is_absolute() for path in (workspace, build, output)), 'absolute-paths')
    require(not output.exists() and not output.is_symlink(), 'output-reuse')
    output.mkdir(mode=0o700)
    helper_build = Path(args.helper_build_root)
    require(helper_build.is_absolute() and helper_build not in (workspace, build, output), 'helper-build-root')
    runner = Runner(output, final)
    result = {'schema': 'issue779-csharp-n05-n06-run-v1', 'case': NEGATIVE_CASE, 'source_commit': SOURCE, 'exit': 1,
              'commands': runner.records, 'native_dispatched': False, 'native_case_receipt_verified': False,
              'trusted_enabled': False, 'remaining_fourteen_controls': 'pending'}
    generation = uuid.uuid4().hex
    root = Path('/var/lib/appsurface-evidence-input-' + generation)
    retained = False
    failure = None
    try:
        require(re.fullmatch('[0-9a-f]{64}', args.reviewed_script_sha256)
                and sha(read(Path(__file__), 131072, final)) == args.reviewed_script_sha256, 'orchestrator-pin')
        reviewed = workspace / '.github/issue779-csharp-native'
        for name, pin in PINS.items():
            require(re.fullmatch('[0-9a-f]{64}', pin) and sha(read(reviewed / name, 131072, final)) == pin, 'reviewed-pin')
        helper_receipt_adapter = load_verified_helper_adapter(reviewed, final)
        capture_raw=read(workspace / '.github/issue779-csharp-build/capture-receipt.json', 2097152, final)
        capture=decode(capture_raw)
        require(sha(capture_raw)==CAPTURE_SHA and capture.get('schema')=='issue779-csharp-common-source-capture-v4'
                and capture.get('validation_commit')==SOURCE and capture.get('previous_validation_commit')==PARENT
                and capture.get('source_tree')==SOURCE_TREE and capture.get('source_count')==SOURCE_COUNT
                and capture.get('native_credit') is False and capture.get('draft_pr_created') is False
                and sha(json.dumps(capture['source'],sort_keys=True,separators=(',',':')).encode())==SOURCE_MAP, 'source-capture-binding')
        raw = read(build / 'receipts/build-receipt.json', 1048576, final)
        receipt = decode(raw)
        require(not os.path.lexists(build / 'receipts/late-publication-failure.json'), 'late-build-publication')
        maps = {name: (read(build / f'handoff/{name}.tsv', 1048576, final),
                       read(build / f'handoff/{name}-nodes.json', 4194304, final)) for name in ('source', 'tool', 'runtime')}
        validate_build(receipt, maps)
        result['build_receipt_sha256'] = sha(raw)
        result['generation'] = generation
        result['build_artifacts'] = receipt['artifacts']
        require(not os.path.lexists(helper_build / 'late-helper-publication-failure.json'), 'late-helper-publication')
        helper_raw = read(helper_build / 'helper-build-receipt.json', 1048576, final)
        helper = decode(helper_raw)
        private_diagnostics_raw=read(helper_build / 'helper-build-private-diagnostics.json', 1048576, final)
        validate_helper_handoff(helper, PINS['build-helper.sh'], private_diagnostics_raw)
        validate_helper_private_diagnostics(helper, private_diagnostics_raw)
        del private_diagnostics_raw
        helper_maps = (read(helper_build / 'helper.tsv', 1048576, final), read(helper_build / 'helper-nodes.json', 4194304, final))
        require(sha(helper_maps[0]) == helper['helper_tsv_sha256'] and sha(helper_maps[1]) == helper['helper_nodes_sha256'], 'helper-map-pins')
        helper_nodes = decode(helper_maps[1])
        require(set(helper_nodes) == {'schema','root_name','files','directories'} and helper_nodes['schema'] == 'issue779-build-node-inventory-v1' and helper_nodes['root_name'] == 'tool', 'helper-node-schema')
        require(len(helper_nodes['files']) == helper['helper_files'] and len(helper_nodes['directories']) == helper['helper_directories'] and sum(v['bytes'] for v in helper_nodes['files'].values()) == helper['helper_bytes'] and helper_nodes['files']['NativePeerBroker.dll']['sha256'] == helper['helper_entry_sha256'], 'helper-membership-receipt')
        result['helper_build_receipt_sha256'] = sha(helper_raw)
        helper_root = Path('/var/lib/appsurface-evidence-n03-helpers') / generation / 'bundle'
        runner.phase = 'root-bootstrap'
        bootstrap = Path('/var/lib/appsurface-evidence-bootstrap-' + generation)
        triples = [part for name, pin in PINS.items() for part in (str(reviewed / name), name, pin)]
        runner.run(ROOT_PREFIX + ['-c', BOOTSTRAP, '--', generation] + triples, time.monotonic() + 30)
        runner.phase = 'root-acquisition-and-transport'
        acquisition_end = min(final - 5, time.monotonic() + 120)
        acquisition_ms = root_deadline(acquisition_end)
        runner.run(ROOT_PREFIX + [str(bootstrap / 'acquire-inbound.sh'), '--execute', '--generation', generation,
            '--build-root', str(build), '--reviewed-root', str(bootstrap), '--deadline-monotonic-ms', acquisition_ms,
            '--reviewed-script-sha256', PINS['acquire-inbound.sh'], '--transport-sha256', PINS['prepare-root-inputs-v2.sh'],
            '--fixture-sha256', PINS['checkpoint-n01-n02-v5.sh'], '--audit-sha256', PINS['prepare-os-audit-v2.py'],
            '--source-review-sha256', PINS['source-review.json'], '--helper-build-root', str(helper_build), '--helper-build-receipt-sha256', sha(helper_raw)], acquisition_end)
        inbound = Path('/var/lib/appsurface-evidence-inbound-' + generation)
        transport = ['--execute', '--reviewed-script-sha256', PINS['prepare-root-inputs-v2.sh'], '--generation', generation,
                     '--deadline-monotonic-ms', acquisition_ms, '--source-commit', SOURCE]
        for name in ('source', 'tool', 'runtime'):
            fact = receipt['artifacts'][name]
            transport += ['--' + name + '-root', str(inbound / name), '--' + name + '-map', str(inbound / 'control' / (name + '.tsv')),
                          '--' + name + '-map-sha256', fact['tsv_sha256'], '--' + name + '-nodes', str(inbound / 'control' / (name + '-nodes.json')),
                          '--' + name + '-nodes-sha256', fact['nodes_sha256']]
        transport += ['--helper-root', str(inbound / 'helper'), '--helper-map', str(inbound / 'control/helper.tsv'), '--helper-map-sha256', helper['helper_tsv_sha256'], '--helper-nodes', str(inbound / 'control/helper-nodes.json'), '--helper-nodes-sha256', helper['helper_nodes_sha256'], '--helper-build-receipt', str(inbound / 'control/helper-build-receipt.json'), '--helper-build-receipt-sha256', sha(helper_raw), '--helper-entry-sha256', helper['helper_entry_sha256']]
        transport += ['--source-review', str(inbound / 'control/source-review.json'), '--source-review-sha256', PINS['source-review.json'],
                      '--build-receipt', str(inbound / 'control/build-receipt.json'), '--build-receipt-sha256', sha(raw),
                      '--fixture', str(inbound / 'scripts/checkpoint-n01-n02-v5.sh'), '--fixture-sha256', PINS['checkpoint-n01-n02-v5.sh'],
                      '--os-audit-script', str(inbound / 'scripts/prepare-os-audit-v2.py'), '--os-audit-script-sha256', PINS['prepare-os-audit-v2.py']]
        runner.run(ROOT_PREFIX + [str(bootstrap / 'prepare-root-inputs-v2.sh')] + transport, acquisition_end)
        runner.phase = 'same-host-unprivileged-os-audit'
        audit_dir = Path('/tmp/issue779-os-audit-' + generation)
        require(not os.path.lexists(audit_dir), 'audit-output-reuse')
        audit_dir.mkdir(mode=0o700)
        audit_end = min(final - 5, time.monotonic() + 60)
        try:
            runner.run(['/usr/bin/python3', '-B', str(root / 'reviewed/prepare-os-audit-v2.py'), '--runtime-root', str(root / 'runtime'),
                        '--tool-root', str(root / 'tool'), '--helper-root', str(helper_root), '--output', str(audit_dir / 'os-audit.json'),
                        '--deadline-monotonic', str(audit_end)], audit_end)
        except BaseException:
            try:
                result['os_audit_failure_context'] = retain_audit_failure_context(
                    audit_dir / 'os-audit.json.failure-context.json', output / 'os-audit-failure-context.json', audit_end)
            except BaseException:
                pass
            raise
        audit = read(audit_dir / 'os-audit.json', 1048576, audit_end)
        require(decode(audit)['schema'] == 'issue779-fixture-os-elf-pins-v2', 'audit-schema')
        audit_hash = sha(audit)
        runner.run(ROOT_PREFIX + ['-c', AUDIT_COPY, '--', generation, str(audit_dir / 'os-audit.json'), audit_hash], audit_end)
        result['os_audit_sha256'] = audit_hash
        runner.phase = 'actual-csharp-n01-n02'
        require(final - time.monotonic() > 665, 'fixture-plus-retention-budget')
        fixture = ['--execute', '--reviewed-script-sha256', PINS['checkpoint-n01-n02-v5.sh']]
        for name, flag in (('source', 'source'), ('tool', 'payload'), ('runtime', 'runtime')):
            fact = receipt['artifacts'][name]
            fixture += ['--' + flag + '-root', str(root / name), '--' + flag + '-manifest', str(root / 'control' / (name + '.tsv')),
                        '--' + flag + '-manifest-sha256', fact['tsv_sha256'], '--' + flag + '-nodes', str(root / 'control' / (name + '-nodes.json')),
                        '--' + flag + '-nodes-sha256', fact['nodes_sha256']]
        fixture += ['--n03-helper-root', str(helper_root), '--n03-helper-map', str(root / 'control/helper.tsv'), '--n03-helper-map-sha256', helper['helper_tsv_sha256'], '--n03-helper-nodes', str(root / 'control/helper-nodes.json'), '--n03-helper-nodes-sha256', helper['helper_nodes_sha256'], '--n03-helper-entry-sha256', helper['helper_entry_sha256'], '--n03-helper-generation', generation]
        fixture += ['--source-review', str(root / 'control/source-review.json'), '--source-review-sha256', PINS['source-review.json'],
                    '--build-receipt', str(root / 'control/build-receipt.json'), '--build-receipt-sha256', sha(raw),
                    '--os-audit', str(root / 'control/os-audit.json'), '--os-audit-sha256', audit_hash,
                    '--source-revision', SOURCE, '--base-revision', '1794a99f9f9857e923fdb2cc8cc81fe0bd5c8c38',
                    '--workflow-identity', 'github-actions-' + os.environ['GITHUB_RUN_ID'] + '-' + os.environ['GITHUB_RUN_ATTEMPT'],
                    '--entry', 'ForgeTrust.AppSurface.Cli.dll', '--entry-sha256', decode(maps['tool'][1])['files']['ForgeTrust.AppSurface.Cli.dll']['sha256'],
                    '--runtime-host', 'dotnet', '--n02-uid', str(os.geteuid()), '--n02-gid', str(os.getegid())]
        result['native_dispatched'] = True
        code, _ = runner.run(ROOT_PREFIX + ['-c', DISPATCH, '--', generation, PINS['checkpoint-n01-n02-v5.sh']] + fixture,
                             time.monotonic() + 630, allow_failure=True)
        result['fixture_exit'] = code
        require(code == 0, 'native-fixture-nonzero')
    except BaseException as caught:
        failure = caught
    finally:
        # Retention cannot upgrade a failed native operation, and cannot stop/clean its units.
        if result['native_dispatched'] and time.monotonic() + 35 < final:
            try:
                runner.phase = 'bounded-private-retention'
                retain_end = min(final - 5, time.monotonic() + 30)
                _, packet = runner.run(ROOT_PREFIX + [str(bootstrap / 'retain-native.sh'), '--execute', '--generation', generation,
                    '--deadline-monotonic-ms', root_deadline(retain_end), '--reviewed-script-sha256', PINS['retain-native.sh']], retain_end)
                result['retention_terminal'] = packet.decode('ascii').strip()
                _, before_sha = runner.run(ROOT_PREFIX + ['-c', ROOT_ARCHIVE_SHA, '--', generation], retain_end)
                _, archive = runner.run(ROOT_PREFIX + ['-c', READ_ROOT_ARCHIVE, '--', generation], retain_end)
                _, after_sha = runner.run(ROOT_PREFIX + ['-c', ROOT_ARCHIVE_SHA, '--', generation], retain_end)
                archive_hash = sha(archive)
                require(before_sha.decode('ascii').strip() == after_sha.decode('ascii').strip() == archive_hash, 'root-archive-digest')
                values = inspect_archive(archive)
                save(output / 'native-private.tar', archive, retain_end)
                result['private_archive_sha256'] = archive_hash
                result['private_archive_bytes'] = len(archive)
                result['retention_selection'] = values['retention-selection.json']
                if not failure:
                    require(result.get('fixture_exit') == 0 and 'fixture-result.json' in values, 'successful-native-receipt-missing')
                    validate_native_result(values['fixture-result.json'])
                    validate_negative_archive(values)
                    result['native_case_receipt_verified'] = True
                    result['native_cases_passed'] = [NEGATIVE_CASE]
                retained = True
            except BaseException as caught:
                failure = failure or caught
        elif result['native_dispatched']:
            failure = failure or Rejected('retention-budget-unavailable')
        result['retention_completed'] = retained
        result['failure'] = {'category': getattr(failure, 'category', 'native-operation'), 'error_class': type(failure).__name__} if failure else None
        result['elapsed_seconds'] = time.monotonic() - start
        result['exit'] = 0 if not failure and retained and result['native_case_receipt_verified'] else 1
        # Original five-second final reserve belongs to invalidation, not another phase.
        try:
            save(output / 'native-receipt.pending.json', result, final - 5)
            require(time.monotonic() < final - 5, 'late-pending-publication')
            os.rename(output / 'native-receipt.pending.json', output / 'native-receipt.json')
            require(time.monotonic() < final - 5, 'late-receipt-rename')
            print('NATIVE_TEST_TERMINAL:' + str(result['exit']), flush=True)
            require(time.monotonic() < final - 5, 'late-terminal-output')
        except BaseException:
            # A published zero is invalid if final output/clock validation fails.
            # The separately captured outer numeric terminal must also be zero.
            result['exit'] = 1
            if time.monotonic() < final:
                try:
                    save(output / 'native-late-failure.json', {'exit': 1, 'category': 'late-publication'}, final)
                except BaseException:
                    pass
    return result['exit']


if __name__ == '__main__':
    raise SystemExit(main())
