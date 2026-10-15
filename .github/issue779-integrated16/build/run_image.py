#!/usr/bin/env python3
"""Execute one reviewed image of the common-source native checkpoint.

This transport entry uses the existing builder and its original per-image
ownership, output and cleanup bounds. It does not accept runtime proof from a
build receipt. The parallel workflow chooses a single closed selector; all
fourteen jobs use the same immutable product source and six-operation contract.
"""
from __future__ import annotations

import argparse
import dataclasses
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import sys
import time

BUILD_SHA = '63e5cbaa856e0e0fe99edaa66b9461cf3a0341fdd3ba4f2bbb1b2eb55d56e7f8'
CONTRACT_SHA = '4e1e774f57d1b878a0985ac218a27490ae4de8da1f21f81cd696dbe6e9c399f8'
RECIPE_SHA = '0927cc47a35be8ed7fd2f3b7c03350ef912f89c37cf38d8aeec6936ad6b18ca4'
CAPTURE_SHA = '4ad23a2fdeccf9abbcd06724b9c97e3a4c1751ecb45e8321cc35bd79c036d540'
SELECTORS = ('None', *(f'N{i:02d}' for i in range(4, 17)))
SOURCE_COMMIT = '5b4ed381badb91de47d793715d05b6ae7737e39e'


def need(ok, category):
    if not ok:
        raise ValueError(category)


def unique(pairs):
    value = {}
    for key, item in pairs:
        need(key not in value, 'duplicate-json')
        value[key] = item
    return value


def identity(fact):
    return (fact.st_dev, fact.st_ino, fact.st_mode, fact.st_uid, fact.st_gid,
            fact.st_nlink, fact.st_size, fact.st_mtime_ns, fact.st_ctime_ns)


def pinned_read(path, expected, limit=4 * 1024 * 1024):
    before = path.lstat()
    need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
         and 0 < before.st_size <= limit, 'input-shape')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        need(identity(before) == identity(os.fstat(fd)), 'input-open-substitution')
        raw = bytearray()
        while True:
            block = os.read(fd, min(65536, limit + 1 - len(raw)))
            if not block:
                break
            raw.extend(block)
            need(len(raw) <= before.st_size, 'input-growth')
        need(len(raw) == before.st_size and identity(before) == identity(os.fstat(fd))
             == identity(path.lstat()), 'input-final-substitution')
        need(hashlib.sha256(raw).hexdigest() == expected, 'input-pin')
        return bytes(raw), stat.S_IMODE(before.st_mode)
    finally:
        os.close(fd)


def validate_review(review, pins):
    need(type(review) is dict and review.get('schema') == 'issue779-integrated16-execution-review-v1',
         'review-schema')
    need(review.get('source_commit') == SOURCE_COMMIT and review.get('capture_sha256') == CAPTURE_SHA,
         'review-source')
    need(review.get('source_review_clear') is True and review.get('whole_harness_review_clear') is True,
         'review-pending')
    need(review.get('build_stage_execution_clear') is True
         and review.get('cleared_builder_sha256') == BUILD_SHA
         and review.get('cleared_recipe_sha256') == RECIPE_SHA, 'pinned-build-stage-review-pending')
    need(type(review.get('files')) is dict and bool(pins) and all(review['files'].get(name) == digest
         for name, digest in pins.items()), 'review-input-bindings')
    need(all(review.get(name) is False for name in
         ('native_execution', 'native_credit', 'trusted_enabled', 'all16_final_integrated_native_verified')),
         'review-no-runtime-authority')


def validate_preflight(receipt, builder_sha):
    need(type(receipt) is dict and receipt.get('status') == 'passed'
         and receipt.get('schema') == 'issue779-integrated16-linux-real-child-preflight-v1',
         'real-child-preflight-terminal')
    need(all(type(receipt.get(name)) is int and receipt[name] == count for name, count in
             (('tests_run', 3), ('failures', 0), ('errors', 0), ('skipped', 0),
              ('group_launches_with_matching_pgid', 4), ('group_launches_with_new_session', 4))),
         'real-child-preflight-counts')
    need(receipt.get('input_sha256', {}).get('integrated16_build.py') == builder_sha,
         'real-child-preflight-builder')
    need(receipt.get('group_launches_with_matching_pgid') == 4 and receipt.get('group_launches_with_new_session') == 4, 'real-child-preflight-groups')
    need(type(receipt.get('actual_group_leader_launches')) is list and len(receipt['actual_group_leader_launches']) == 4
         and all(row.get('new_session_requested') is True and row.get('pgid_matches_leader') is True
                 for row in receipt['actual_group_leader_launches']), 'real-child-preflight-ownership')
    need(type(receipt.get('elapsed_seconds')) in (int, float)
         and 0 <= receipt['elapsed_seconds'] <= 45 and receipt.get('log', {}).get('overflow') is False
         and type(receipt.get('log', {}).get('bytes')) is int
         and 0 <= receipt['log']['bytes'] <= 1048576
         and type(receipt.get('log', {}).get('sha256')) is str
         and re.fullmatch('[0-9a-f]{64}', receipt['log']['sha256']) is not None,
         'real-child-preflight-bounds')


def load_builder(directory):
    pins = {'integrated16_build.py': BUILD_SHA,
            'integrated16_build_contract.py': CONTRACT_SHA, 'build-fdd.sh': RECIPE_SHA}
    for name, digest in pins.items():
        pinned_read(directory / name, digest)
    sys.path.insert(0, str(directory))
    # The module is trusted tooling selected by the reviewed transport pins.
    spec = importlib.util.spec_from_file_location('integrated16_build', directory / 'integrated16_build.py')
    need(spec is not None and spec.loader is not None, 'builder-loader')
    builder = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = builder
    spec.loader.exec_module(builder)
    for name, digest in pins.items():
        pinned_read(directory / name, digest)
    return builder, pins


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--selector', choices=SELECTORS, required=True)
    parser.add_argument('--builder-dir', type=Path, required=True)
    parser.add_argument('--harness-root', type=Path, required=True)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--capture', type=Path, required=True)
    parser.add_argument('--output-root', type=Path, required=True)
    parser.add_argument('--review', type=Path)
    parser.add_argument('--review-sha256')
    parser.add_argument('--preflight', type=Path)
    parser.add_argument('--preflight-sha256')
    parser.add_argument('--execute', action='store_true')
    args = parser.parse_args(argv)
    builder, pins = load_builder(args.builder_dir.resolve(strict=True))
    capture, capture_mode = pinned_read(args.capture, CAPTURE_SHA)
    source = dataclasses.replace(builder.DEFAULT_CONFIG.source,
                                 source_root=args.source_root.resolve(strict=True),
                                 capture_path=args.capture.resolve(strict=True),
                                 capture_mode=capture_mode)
    config = dataclasses.replace(builder.DEFAULT_CONFIG, source=source,
                                 recipe_path=args.builder_dir.resolve(strict=True) / 'build-fdd.sh')
    # Full closed selector/capture/source/recipe preparation runs before reducing
    # the execution plan to the workflow's one selected image.
    plan = builder._prepare(config, args.harness_root.resolve(strict=True), args.output_root)
    selected = tuple(dataclasses.replace(image, output_dir=args.output_root.parent / 'image')
                     for image in plan.images if image.selector == args.selector)
    need(len(selected) == 1, 'single-image-selection')
    need(not os.path.lexists(selected[0].output_dir), 'single-image-output-fresh')
    plan = dataclasses.replace(plan, images=selected,
                               config=dataclasses.replace(config, total_deadline_seconds=1200))
    if not args.execute:
        print(json.dumps(builder._public_plan(plan), sort_keys=True))
        return 0
    need(sys.platform == 'linux' and os.geteuid() != 0, 'linux-unprivileged-builder')
    need(all(value is not None for value in (args.review, args.review_sha256,
                                           args.preflight, args.preflight_sha256)), 'execution-inputs')
    review_raw, _ = pinned_read(args.review, args.review_sha256)
    review = json.loads(review_raw, object_pairs_hook=unique)
    pins['run_image.py'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    validate_review(review, pins)
    preflight_raw, _ = pinned_read(args.preflight, args.preflight_sha256)
    validate_preflight(json.loads(preflight_raw, object_pairs_hook=unique), BUILD_SHA)
    preflight = json.loads(preflight_raw, object_pairs_hook=unique)
    pinned_read(args.preflight.parent / 'real-child-preflight.log', preflight['log']['sha256'], 1048576)
    job_end = int(os.environ['JOB_DEADLINE_MONOTONIC_NS']) / 1_000_000_000
    need(job_end - time.monotonic() > builder.PER_IMAGE_DEADLINE_SECONDS,
         'original-job-build-budget')
    result = builder._execute(plan)
    print(json.dumps(result, sort_keys=True))
    return 0 if result['status'] == 'build_prerequisites_complete' else 1


if __name__ == '__main__':
    raise SystemExit(main())
