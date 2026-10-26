"""Retain and recheck ordinary package-smoke outputs, without runtime authority.

Only the fixed 22 command logs, starter files and planning outputs are copied.
The existing producer artifact's 128-file/32-MiB limits still apply independently.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import stat

LEAF_LIMIT = 1024 * 1024
TOTAL_LIMIT = 4 * LEAF_LIMIT
COMMANDS = (
    'tool-pack', 'tool-install', 'ordinary-help', 'worker-help', 'supervisor-help',
    'unsupported-owner', 'omitted-mode', 'evidence-starter', 'evidence-doctor',
    'evidence-explain', 'evidence-starter-repeat', 'evidence-observation-without-control',
    'evidence-legacy-observation-without-control', 'evidence-conflicting-mode',
    'sdk-pack-contracts', 'sdk-pack-planner', 'sdk-pack-coverage', 'sdk-pack-aspire',
    'sdk-restore', 'sdk-locked-restore', 'sdk-build', 'sdk-load',
)
REJECTIONS = frozenset((
    'unsupported-owner', 'omitted-mode', 'evidence-starter-repeat',
    'evidence-observation-without-control', 'evidence-legacy-observation-without-control',
    'evidence-conflicting-mode',
))
SDK_LOAD_MARKERS = (
    b'PACKAGED_EVIDENCE_MIGRATION legacy-omitted ASEVD401 callbacks=0',
    b'PACKAGED_EVIDENCE_MIGRATION legacy-false ASEVD401 callbacks=0',
    b'PACKAGED_EVIDENCE_MIGRATION legacy-true ASEVD402 callbacks=0',
)
SDK_LOAD_COMPLETION = b'Exact packaged SDK planning, schema and closed-entry controls passed.'
STARTER = ('evidence.policy.json', 'EvidenceHost.cs', 'README.md')
OUTPUTS = {name + '.log': name + '.log' for name in COMMANDS}
OUTPUTS.update({'starter/' + name: 'planning-consumer/.appsurface/evidence/' + name
                for name in STARTER})
OUTPUTS.update({'planning/' + name: 'planning-consumer/TestResults/evidence/' + name
                for name in ('evidence-plan.json', 'evidence-summary.json')})
OUTPUTS['receipt.json'] = 'receipt.json'


def require(condition):
    """Reject incomplete or substituted smoke evidence without echoing its contents."""
    if not condition:
        raise ValueError('Package smoke evidence rejected.')


def decode(raw):
    """Read a bounded JSON object; duplicate members are never reconciled silently."""
    def unique(pairs):
        value = {}
        for key, item in pairs:
            require(key not in value)
            value[key] = item
        return value
    result = json.loads(raw, object_pairs_hook=unique)
    require(type(result) is dict)
    return result


def read(root, relative):
    """Read one fixed relative leaf through non-following directory and file handles."""
    current = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        parts = Path(relative).parts
        require(parts and all(part not in ('', '.', '..', '/') for part in parts))
        for part in parts[:-1]:
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=current)
            os.close(current)
            current = child
        fd = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=current)
        try:
            before = os.fstat(fd)
            require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
                    and before.st_size <= LEAF_LIMIT)
            raw = bytearray()
            while True:
                block = os.read(fd, min(65536, LEAF_LIMIT + 1 - len(raw)))
                if not block:
                    break
                raw.extend(block)
                require(len(raw) <= LEAF_LIMIT)
            stamp = lambda node: (node.st_dev, node.st_ino, node.st_mode, node.st_nlink,
                                  node.st_size, node.st_mtime_ns, node.st_ctime_ns)
            require(stamp(before) == stamp(os.fstat(fd)) == stamp(os.stat(
                parts[-1], dir_fd=current, follow_symlinks=False)) and len(raw) == before.st_size)
            return bytes(raw)
        finally:
            os.close(fd)
    finally:
        os.close(current)


def validate(bodies, source):
    """Bind actual outputs to the successful source receipt and fixed command decisions.

    This validates retained installation/planning evidence only. It cannot establish
    kernel isolation, an admission capability or production Trusted acceptance.
    """
    require(type(source) is str and re.fullmatch('[0-9a-f]{40}', source) is not None)
    require(set(bodies) == set(OUTPUTS) and sum(map(len, bodies.values())) <= TOTAL_LIMIT)
    receipt = decode(bodies['receipt.json'])
    require(receipt.get('schema') == 'issue779-packaged-consumer-v1'
            and type(receipt.get('exit')) is int and receipt['exit'] == 0
            and receipt.get('source') == source
            and receipt.get('protected_execution') is False
            and receipt.get('trusted_acceptance') is False)
    commands = receipt.get('commands')
    require(type(commands) is list and len(commands) == len(COMMANDS))
    for command, name in zip(commands, COMMANDS):
        require(type(command) is dict and command.get('name') == name
                and type(command.get('exit')) is int
                and command['exit'] == int(name in REJECTIONS)
                and type(command.get('argv')) is list and command['argv']
                and all(type(arg) is str for arg in command['argv'])
                and command.get('log_sha256') == hashlib.sha256(bodies[name + '.log']).hexdigest())
    journey = receipt.get('planning_journey')
    require(type(journey) is dict and journey.get('entry') == 'installed-nuget-tool'
            and journey.get('producer_execution') is False
            and journey.get('trusted_acceptance') is False
            and journey.get('independent_human_usability') is False)
    require(journey.get('starter_sha256') == {
        name: hashlib.sha256(bodies['starter/' + name]).hexdigest() for name in STARTER})
    for name, key in (('evidence-plan.json', 'plan_sha256'), ('evidence-summary.json', 'summary_sha256')):
        require(journey.get(key) == hashlib.sha256(bodies['planning/' + name]).hexdigest())
    require(b'ASEVD402' in bodies['unsupported-owner.log']
            and b'ASEVD401' in bodies['omitted-mode.log']
            and b'ASEVD202' in bodies['evidence-starter-repeat.log']
            and b'ASEVD402' in bodies['evidence-observation-without-control.log']
            and bodies['evidence-legacy-observation-without-control.log']
            == bodies['evidence-observation-without-control.log']
            and b'ASEVD401' in bodies['evidence-conflicting-mode.log'])
    require(b'unverified trusted-envelope:' in bodies['evidence-doctor.log']
            and b'do not grant admission' in bodies['evidence-doctor.log']
            and b'issue779-consumer-acceptance.md' in bodies['evidence-doctor.log'])
    sdk_load_lines = bodies['sdk-load.log'].splitlines()
    marker_positions = []
    for marker in SDK_LOAD_MARKERS:
        require(sdk_load_lines.count(marker) == 1)
        marker_positions.append(sdk_load_lines.index(marker))
    require(marker_positions == sorted(marker_positions)
            and sdk_load_lines.count(SDK_LOAD_COMPLETION) == 1
            and sdk_load_lines[-1] == SDK_LOAD_COMPLETION)
    require(all(value in bodies['evidence-explain.log'] for value in (
        b'Evidence plan: targeted-coverage (targeted)', b'Required producers: coverage',
        b'Required resources: none', b'changed-behavior-covered')))
    summary = decode(bodies['planning/evidence-summary.json'])
    require(summary.get('kind') == 'plan' and summary.get('profile') == 'targeted-coverage'
            and summary.get('obligations') == ['changed-behavior-covered']
            and type(summary.get('next')) is str
            and 'not use this plan as gate evidence' in summary['next'])
    return receipt


def retain(work, output, source):
    """Copy fixed validated files into a fresh private directory, then recheck the copy.

    All inputs are read and validated before creating the output directory. A copy
    failure leaves an unsuccessful partial directory, never a successful receipt.
    """
    bodies = {target: read(work, original) for target, original in OUTPUTS.items()}
    validate(bodies, source)
    output = Path(output)
    output.mkdir(mode=0o700)
    for name, raw in bodies.items():
        destination = output / name
        if destination.parent != output:
            destination.parent.mkdir(mode=0o700, exist_ok=True)
        fd = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, 'wb') as stream:
            stream.write(raw)
    verify(output, source)


def verify(output, source):
    """Re-read the exact retained file set, private modes and actual receipt-bound bytes."""
    output = Path(output)
    before = output.lstat()
    require(stat.S_ISDIR(before.st_mode) and stat.S_IMODE(before.st_mode) == 0o700)
    def inventory():
        nodes = {}
        files = set()
        for path in output.rglob('*'):
            node = path.lstat()
            name = path.relative_to(output).as_posix()
            if stat.S_ISDIR(node.st_mode):
                require(name in ('starter', 'planning') and stat.S_IMODE(node.st_mode) == 0o700)
            else:
                require(stat.S_ISREG(node.st_mode) and node.st_nlink == 1
                        and stat.S_IMODE(node.st_mode) == 0o600)
                files.add(name)
            nodes[name] = (node.st_dev, node.st_ino, node.st_mode, node.st_nlink,
                           node.st_size, node.st_mtime_ns, node.st_ctime_ns)
        require(files == set(OUTPUTS))
        return nodes

    original_nodes = inventory()
    receipt = validate({name: read(output, name) for name in OUTPUTS}, source)
    require(inventory() == original_nodes)
    after = output.lstat()
    require((before.st_dev, before.st_ino, before.st_mode, before.st_mtime_ns, before.st_ctime_ns)
            == (after.st_dev, after.st_ino, after.st_mode, after.st_mtime_ns, after.st_ctime_ns))
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=('retain', 'verify'))
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--work', type=Path)
    parser.add_argument('--source', required=True)
    args = parser.parse_args()
    require(args.output.is_absolute() and (args.work is None or args.work.is_absolute()))
    require((args.work is not None) == (args.operation == 'retain'))
    if args.operation == 'retain':
        retain(args.work, args.output, args.source)
    else:
        verify(args.output, args.source)
    print('Retained package smoke outputs verified; no protected acceptance claim.')


if __name__ == '__main__':
    main()
