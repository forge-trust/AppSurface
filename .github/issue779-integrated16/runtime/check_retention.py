"""Bound only the fixed retained case files; never print their contents."""
import argparse
import json
import os
from pathlib import Path
import stat
import verify_installation as installation

FILES = {
    'image-download.json': 65536,
    'image/receipts/build-receipt.json': 4 * 1024 * 1024,
    'case-plan.json': 256 * 1024,
    'preparation-runtime.json': 256 * 1024,
    'preparation-helpers.json': 256 * 1024,
    'preparation-runtime-0.log': 8 * 1024 * 1024,
    'preparation-helpers-0.log': 8 * 1024 * 1024,
    'preparation-helpers-1.log': 8 * 1024 * 1024,
    'native-runner-receipt.json': 256 * 1024,
    'native-runner.log': 8 * 1024 * 1024,
    'issue779-csharp-native-run/native-receipt.json': 4 * 1024 * 1024,
    'issue779-csharp-native-run/native-late-failure.json': 65536,
    'issue779-csharp-native-run/native-private.tar': 33_619_968,
    'issue779-csharp-native-run/native-command-failure.tar': 143_360,
}
# The new diagnostic shares the original total artifact budget.
TOTAL_LIMIT = sum(FILES.values()) - FILES['issue779-csharp-native-run/native-command-failure.tar']


def check(root):
    installation.private_root(root)
    total, seen = 0, []
    for relative, limit in FILES.items():
        path = root / relative
        if not os.path.lexists(path):
            continue
        for parent in path.relative_to(root).parents:
            fact = (root / parent).lstat()
            installation.require(stat.S_ISDIR(fact.st_mode) and not stat.S_ISLNK(fact.st_mode)
                                 and fact.st_uid == os.geteuid()
                                 and stat.S_IMODE(fact.st_mode) & 0o077 == 0,
                                 'retention-parent-private')
        fact = path.lstat()
        installation.require(fact.st_uid == os.geteuid() and stat.S_IMODE(fact.st_mode) == 0o600,
                             'retention-file-private')
        raw = installation.read(path, limit)
        total += len(raw)
        seen.append(relative)
    installation.require(total <= TOTAL_LIMIT, 'retention-total')
    return {'files': seen, 'bytes': total, 'native_credit': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--job-root', type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(check(args.job_root), sort_keys=True))


if __name__ == '__main__':
    main()
