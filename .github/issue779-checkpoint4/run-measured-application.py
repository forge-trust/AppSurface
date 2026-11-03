"""Coverlet target: record actual instrumented bytes, then run the unchanged native C3 controls."""
from pathlib import Path
import hashlib
import json
import os
import runpy
import sys
from measurement_arguments import MODULES


def main():
    # Arguments before '--' belong only to this private measurement wrapper.
    split = sys.argv.index('--')
    if split != 5 or sys.argv[1] != '--binding' or sys.argv[3] != '--original':
        raise ValueError('measurement-wrapper-arguments')
    binding = Path(sys.argv[2])
    original = json.loads(Path(sys.argv[4]).read_bytes())
    args = sys.argv[split + 1:]
    tool = Path(args[args.index('--tool') + 1])
    sdk_tool = Path(args[args.index('--sdk-tool') + 1])
    source = args[args.index('--source') + 1]
    if os.geteuid() != 0 or source != original['source']:
        raise ValueError('measurement-source-owner')
    names = {module + suffix for module in MODULES for suffix in ('.dll', '.pdb')}
    if set(original['files']) != names:
        raise ValueError('measurement-module-set')
    rows = {}
    for name in original['files']:
        data = (tool / name).read_bytes()
        if not 0 < len(data) <= 32 * 1024 * 1024:
            raise ValueError('measurement-module-size')
        rows[name] = hashlib.sha256(data).hexdigest()
    if set(rows) != set(original['files']) or any(rows[name] == digest for name, digest in original['files'].items() if name.endswith('.dll')):
        raise ValueError('measurement-instrumentation-missing')
    sdk_rows = {}
    if set(original['sdk_files']) != set(original['files']):
        raise ValueError('measurement-sdk-module-set')
    for name in original['sdk_files']:
        data = (sdk_tool / name).read_bytes()
        if not 0 < len(data) <= 32 * 1024 * 1024:
            raise ValueError('measurement-sdk-module-size')
        sdk_rows[name] = hashlib.sha256(data).hexdigest()
    if any(sdk_rows[name] == digest for name, digest in original['sdk_files'].items() if name.endswith('.dll')):
        raise ValueError('measurement-sdk-instrumentation-missing')
    value = {'schema': 'issue779-native-instrumentation-v1', 'source': source,
             'collector': 'coverlet.console/10.0.1', 'original': original['files'], 'instrumented': rows,
             'sdk_original': original['sdk_files'], 'sdk_instrumented': sdk_rows}
    with binding.open('x') as target:
        json.dump(value, target, sort_keys=True)
    binding.chmod(0o600)
    sys.argv = ['run-native.py', *args]
    target = Path(__file__).resolve().parents[1] / 'issue779-checkpoint3/run-native.py'
    runpy.run_path(str(target), run_name='__main__')


if __name__ == '__main__':
    main()
