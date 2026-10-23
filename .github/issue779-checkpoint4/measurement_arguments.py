"""Closed arguments for nested pinned collectors; no shell, runtime policy or admission."""
from pathlib import Path
import subprocess
import sys

MODULES = ('ForgeTrust.AppSurface.Cli', 'ForgeTrust.AppSurface.Evidence.Contracts',
           'ForgeTrust.AppSurface.Evidence.Planner', 'ForgeTrust.AppSurface.Evidence.Coverage',
           'ForgeTrust.AppSurface.Evidence.Aspire', 'ForgeTrust.AppSurface.Evidence.Supervision',
           'ForgeTrust.AppSurface.Evidence.Cli')


def arguments(values):
    """Quote the double-quote/backslash grammar used by .NET ProcessStartInfo.Arguments."""
    if any(type(value) is not str or not value or len(value) > 65536
           or any(ord(char) < 32 for char in value) for value in values):
        raise ValueError('measurement-argument-shape')
    return subprocess.list2cmdline(values)


def validate(inputs):
    evidence, tool, sdk_tool, runtime, prepared, workspace, source, console = inputs
    if any(type(value) is not str or not value or any(char in value for char in '\n\r"\\')
           or any(ord(char) < 32 for char in value) for value in inputs):
        raise ValueError('measurement-input-shape')
    if len(source) != 40 or any(char not in '0123456789abcdef' for char in source):
        raise ValueError('measurement-source-shape')
    if any(not Path(value).is_absolute() for value in inputs if value != source):
        raise ValueError('measurement-absolute-path')


def failure_target(evidence, tool, sdk_tool, runtime, prepared, workspace, source, console):
    """One fixed negative SDK control, independent of the original matrix's allowance."""
    validate((evidence, tool, sdk_tool, runtime, prepared, workspace, source, console))
    return ['-B', '.github/issue779-checkpoint4/sdk_failure.py', 'run',
            '--evidence', evidence, '--tool', tool, '--sdk-tool', sdk_tool, '--runtime', runtime,
            '--prepared', prepared, '--workspace', workspace, '--source', source,
            '--base', '416b30919e6c4d03b634d9d3fe558884b02f0b91']


def build(evidence, tool, sdk_tool, runtime, prepared, workspace, source, console):
    """The original matrix; its standard SDK report also merges the completed negative partition."""
    validate((evidence, tool, sdk_tool, runtime, prepared, workspace, source, console))
    target = ['-B', '.github/issue779-checkpoint4/run-measured-application.py',
              '--binding', evidence + '/instrumentation.json',
              '--original', evidence + '/original-supervision.json', '--',
              '--tool', tool, '--sdk-tool', sdk_tool, '--runtime', runtime,
              '--subject', workspace + '/.github/issue779-checkpoint3/subject',
              '--prepared', prepared + '/payload-preparation.json',
              '--digests', evidence + '/registration-digests.json',
              '--output', evidence + '/native', '--source', source,
              '--base', '416b30919e6c4d03b634d9d3fe558884b02f0b91']
    return [console, sdk_tool + '/ForgeTrust.AppSurface.Cli.dll',
            '--target', '/usr/bin/python3', '--targetargs', arguments(target),
            '--include', *('[' + module + ']*' for module in MODULES), '--include-test-assembly',
            '--merge-with', evidence + '/native-sdk-failure.json',
            '--format', 'json', '--output', evidence + '/native-sdk']


if __name__ == '__main__':
    if len(sys.argv) == 10 and sys.argv[1] == '--failure-target':
        print(arguments(failure_target(*sys.argv[2:])))
        raise SystemExit(0)
    if len(sys.argv) != 9:
        raise ValueError('measurement-argument-count')
    print(arguments(build(*sys.argv[1:])))
