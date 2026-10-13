"""Run only the reviewed plan's fixed runtime or helper preparation stage.

Each child uses the copied ownership runner: bounded output, group settlement,
EOF and reap are required before a later command begins. Preparation failure
remains failure even after cleanup. This utility issues no product capability.
"""
import argparse
import json
import os
from pathlib import Path
import time
import preparation_process
import verify_installation as installation


def save(path, value):
    raw = (json.dumps(value, sort_keys=True, indent=2) + '\n').encode()
    installation.require(len(raw) <= 256 * 1024, 'preparation-receipt-size')
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    with os.fdopen(fd, 'wb') as stream:
        stream.write(raw)
        stream.flush()
        os.fsync(stream.fileno())


def successful(row):
    return (type(row) is dict and type(row.get('exit')) is int and row['exit'] == 0
            and all(row.get(k) is True for k in ('waited', 'leader_exit_observed',
                      'descendants_settled', 'pipe_eof', 'deadline_ok'))
            and all(row.get(k) is False for k in ('forced_cleanup', 'overflow',
                      'read_error', 'write_error', 'error'))
            and row.get('discarded_bytes') == 0 and row.get('failure') is None)


def execute(stage, plan_path, job_root):
    # Import after the workflow has verified the complete installed tool set.
    import packet_execution
    installation.private_root(job_root)
    plan = installation.decode(installation.read(plan_path))
    installation.require(plan_path == job_root / 'case-plan.json', 'preparation-plan-path')
    deadline_ns = int(os.environ['JOB_DEADLINE_MONOTONIC_NS'])
    packet_execution.validate_plan(plan, Path(__file__).parent / 'packet', job_root,
                                   Path(__file__).parent / 'execution-review.json',
                                   plan['review_sha256'], deadline_ns)
    rows = plan['stages'][stage]
    installation.require(type(rows) is list and len(rows) <= 2, 'preparation-stage-shape')
    receipt = {'schema': 'issue779-integrated16-preparation-stage-v1', 'stage': stage,
               'selector': plan['selector'], 'source_commit': installation.SOURCE,
               'commands': [], 'exit': 1, 'native_execution': False, 'native_credit': False}
    try:
        for index, row in enumerate(rows):
            packet_execution.validate_plan(plan, Path(__file__).parent / 'packet', job_root,
                                           Path(__file__).parent / 'execution-review.json',
                                           plan['review_sha256'], deadline_ns)
            job_end = deadline_ns / 1_000_000_000
            work_end = min(time.monotonic() + row['timeout_seconds'], job_end - 910)
            def check():
                if time.monotonic() >= work_end:
                    raise TimeoutError('preparation-original-deadline')
            env = os.environ.copy()
            env['RUNNER_TEMP'] = str(job_root)
            public, _ = preparation_process.run_command(
                row['operation'], row['argv'], cwd=Path(row['cwd']),
                log_path=job_root / f'preparation-{stage}-{index}.log', env=env,
                check=check, cleanup_remaining=lambda: max(0, min(5, job_end - time.monotonic() - 905)))
            receipt['commands'].append(public)
            installation.require(successful(public), 'preparation-child-unsuccessful')
        receipt['exit'] = 0
    finally:
        save(job_root / f'preparation-{stage}.json', receipt)
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', choices=('runtime', 'helpers'), required=True)
    parser.add_argument('--plan', type=Path, required=True)
    parser.add_argument('--job-root', type=Path, required=True)
    args = parser.parse_args()
    execute(args.stage, args.plan, args.job_root)


if __name__ == '__main__':
    main()
