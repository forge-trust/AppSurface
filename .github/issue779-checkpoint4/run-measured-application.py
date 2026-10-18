"""Coverlet target: record actual instrumented bytes, then run the unchanged native C3 controls."""
from pathlib import Path
import hashlib
import json
import os
import runpy
import sys


def main():
    # Arguments before '--' belong only to this private measurement wrapper.
    split = sys.argv.index('--')
    if split != 5 or sys.argv[1] != '--binding' or sys.argv[3] != '--original':
        raise ValueError('measurement-wrapper-arguments')
    binding = Path(sys.argv[2])
    original = json.loads(Path(sys.argv[4]).read_bytes())
    args = sys.argv[split + 1:]
    tool = Path(args[args.index('--tool') + 1])
    source = args[args.index('--source') + 1]
    if os.geteuid() != 0 or source != original['source']:
        raise ValueError('measurement-source-owner')
    rows = {}
    for name in ('ForgeTrust.AppSurface.Evidence.Supervision.dll', 'ForgeTrust.AppSurface.Evidence.Supervision.pdb'):
        data = (tool / name).read_bytes()
        if not 0 < len(data) <= 32 * 1024 * 1024:
            raise ValueError('measurement-module-size')
        rows[name] = hashlib.sha256(data).hexdigest()
    if rows['ForgeTrust.AppSurface.Evidence.Supervision.dll'] == original['files']['ForgeTrust.AppSurface.Evidence.Supervision.dll']:
        raise ValueError('measurement-instrumentation-missing')
    value = {'schema': 'issue779-native-instrumentation-v1', 'source': source,
             'collector': 'coverlet.console/10.0.1', 'original': original['files'], 'instrumented': rows}
    with binding.open('x') as target:
        json.dump(value, target, sort_keys=True)
    binding.chmod(0o600)
    sys.argv = ['run-native.py', *args]
    target = Path(__file__).resolve().parents[1] / 'issue779-checkpoint3/run-native.py'
    runpy.run_path(str(target), run_name='__main__')


if __name__ == '__main__':
    main()
