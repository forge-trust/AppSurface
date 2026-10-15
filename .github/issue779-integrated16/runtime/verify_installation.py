"""Verify the reviewed tooling bytes before restoring their captured modes.

Git preserves executable bits but not private 0600 modes. The workflow calls
this unprivileged preflight before loading any copied native procedure. The
review's independent SHA binds the complete installation map; it grants no
product admission or native acceptance.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat

LIMIT = 4 * 1024 * 1024
SOURCE = '5b4ed381badb91de47d793715d05b6ae7737e39e'
CAPTURE = '4ad23a2fdeccf9abbcd06724b9c97e3a4c1751ecb45e8321cc35bd79c036d540'
PACKET = 'b04c48f3469b03a73a38e4a7708c6847ed3b9c07f2120acb6dc49f77c36e3f0e'


def require(ok, category):
    if not ok:
        raise ValueError(category)


def unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'installation-duplicate-json')
        result[key] = value
    return result


def identity(fact):
    return (fact.st_dev, fact.st_ino, fact.st_mode, fact.st_uid, fact.st_gid,
            fact.st_nlink, fact.st_size, fact.st_mtime_ns, fact.st_ctime_ns)


def read(path, limit=LIMIT):
    before = path.lstat()
    require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
            and 0 <= before.st_size <= limit, 'installation-file-shape')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK)
    try:
        require(identity(before) == identity(os.fstat(fd)), 'installation-file-replaced')
        blocks, count = [], 0
        while True:
            block = os.read(fd, min(65536, limit + 1 - count))
            if not block:
                break
            count += len(block)
            require(count <= limit, 'installation-file-growth')
            blocks.append(block)
        require(count == before.st_size and identity(before) == identity(os.fstat(fd))
                == identity(path.lstat()), 'installation-file-changed')
        return b''.join(blocks)
    finally:
        os.close(fd)


def decode(raw):
    value = json.loads(raw, object_pairs_hook=unique)
    require(type(value) is dict, 'installation-json-object')
    return value


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def relative(name):
    require(type(name) is str and bool(name) and not name.startswith('/')
            and '\\' not in name and all(p not in ('', '.', '..') for p in name.split('/')),
            'installation-relative-path')
    return name


def private_root(root):
    require(root.is_absolute() and str(root) == str(root.resolve()), 'installation-root-path')
    fact = root.lstat()
    require(stat.S_ISDIR(fact.st_mode) and not stat.S_ISLNK(fact.st_mode)
            and fact.st_uid == os.geteuid() and stat.S_IMODE(fact.st_mode) & 0o077 == 0,
            'installation-root-owner')


def review_record(path, expected):
    require(type(expected) is str and re.fullmatch('[0-9a-f]{64}', expected),
            'installation-review-digest')
    raw = read(path)
    require(sha(raw) == expected, 'installation-review-pin')
    review = decode(raw)
    require(review.get('schema') == 'issue779-integrated16-native-execution-review-v1'
            and review.get('source_commit') == SOURCE and review.get('capture_sha256') == CAPTURE
            and review.get('packet_sha256') == PACKET
            and all(review.get(k) is True for k in ('source_review_clear',
                    'whole_harness_review_clear', 'native_execution_clear')),
            'installation-review-not-clear')
    return review


def verify(root, review_path, review_sha):
    private_root(root)
    review = review_record(review_path, review_sha)
    raw = read(root / 'installation-map.json')
    require(sha(raw) == review.get('installation_map_sha256'), 'installation-map-pin')
    manifest = decode(raw)
    require(manifest.get('schema') == 'issue779-integrated16-native-installation-v1'
            and manifest.get('source_commit') == SOURCE and manifest.get('capture_sha256') == CAPTURE
            and manifest.get('packet_sha256') == PACKET, 'installation-map-binding')
    rows = manifest.get('files')
    require(type(rows) is dict and rows and rows == review.get('files'), 'installation-review-files')
    selected = []
    for name, row in rows.items():
        relative(name)
        require(type(row) is dict and set(row) == {'sha256', 'mode', 'bytes'}
                and type(row['sha256']) is str and re.fullmatch('[0-9a-f]{64}', row['sha256'])
                and row['mode'] in ('0600', '0644', '0700', '0755')
                and type(row['bytes']) is int and 0 <= row['bytes'] <= LIMIT,
                'installation-row-shape')
        path = root / name
        for parent in path.relative_to(root).parents:
            fact = (root / parent).lstat()
            require(stat.S_ISDIR(fact.st_mode) and not stat.S_ISLNK(fact.st_mode)
                    and fact.st_uid == os.geteuid() and stat.S_IMODE(fact.st_mode) & 0o022 == 0,
                    'installation-parent-shape')
        data = read(path)
        require(len(data) == row['bytes'] and sha(data) == row['sha256'], 'installation-bytes')
        selected.append((path, row))
    actual = set()
    for folder, dirs, files in os.walk(root, followlinks=False):
        for name in dirs:
            require(not (Path(folder) / name).is_symlink(), 'installation-directory-link')
        actual.update((Path(folder) / name).relative_to(root).as_posix() for name in files)
    require(actual == set(rows) | {'installation-map.json', 'execution-review.json'},
            'installation-file-set')
    # All bytes are checked before the first mode mutation. Each retained FD
    # is checked again so a replaced path cannot receive the reviewed mode.
    for path, row in selected:
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK)
        try:
            fact = os.fstat(fd)
            require(identity(fact) == identity(path.lstat()) and fact.st_nlink == 1
                    and stat.S_ISREG(fact.st_mode), 'installation-mode-substitution')
            data = bytearray()
            while True:
                block = os.read(fd, 65536)
                if not block:
                    break
                data.extend(block)
                require(len(data) <= row['bytes'], 'installation-mode-growth')
            require(len(data) == row['bytes'] and sha(data) == row['sha256'],
                    'installation-mode-bytes')
            os.fchmod(fd, int(row['mode'], 8))
            require(identity(os.fstat(fd)) == identity(path.lstat())
                    and stat.S_IMODE(os.fstat(fd).st_mode) == int(row['mode'], 8),
                    'installation-mode-terminal')
        finally:
            os.close(fd)
    return {'files_verified': len(rows), 'native_execution': False, 'native_credit': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--review', type=Path, required=True)
    parser.add_argument('--review-sha256', required=True)
    args = parser.parse_args()
    print(json.dumps(verify(args.root, args.review, args.review_sha256), sort_keys=True))


if __name__ == '__main__':
    main()
