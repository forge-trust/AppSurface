"""Verify unchanged baseline bytes/modes and the committed probe inputs."""
import hashlib
import json
import os
import stat
import subprocess
import sys
from pathlib import Path

root = Path(sys.argv[1]).resolve(strict=True)
probe = root / 'tests/evidencehost-consumer/ExistingPermissionsCoverageProbe'
binding = json.loads((probe / 'inputs.json').read_text())
assert binding['baseline'] == '171f53911c7c1fec08bf0676b321539567f97c5d'
assert len(binding['baseline_files']) == 2814
env = dict(os.environ, GIT_OPTIONAL_LOCKS='0')
subprocess.run(['git', 'diff', '--exit-code', '--quiet'], cwd=root, env=env, check=True, timeout=10)
subprocess.run(['git', 'diff', '--cached', '--exit-code', '--quiet'], cwd=root, env=env, check=True, timeout=10)
assert not subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=normal'], cwd=root, env=env, timeout=10)
for relative, expected in {**binding['baseline_files'], **binding['probe_files']}.items():
    parts = Path(relative).parts
    assert parts and not Path(relative).is_absolute() and all(p not in ('.', '..') for p in parts)
    path = root / relative
    for parent in path.parents:
        if parent == root:
            break
        assert not parent.is_symlink()
    observed = path.lstat()
    assert stat.S_ISREG(observed.st_mode) and observed.st_nlink == 1
    assert f'{stat.S_IMODE(observed.st_mode):04o}' == expected['mode']
    assert observed.st_size <= 32 * 1024 * 1024
    assert hashlib.sha256(path.read_bytes()).hexdigest() == expected['sha256']
print(json.dumps(dict(baseline_files_verified=2814, probe_files_verified=len(binding['probe_files']),
                      source_commit=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, env=env, timeout=10, text=True).strip(),
                      production_modified=False)))
