"""Fixed pure-data tests against the active standard collector's actual runtime image.

These commands run after the unchanged native procedure. They create no native
case, owner, lease, admission or acceptance proof and never read or edit counters.
"""
from __future__ import annotations

import os
from pathlib import Path
import stat
import tempfile
import time
import xml.etree.ElementTree as ET

import binding
import publish

PROJECT = 'Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/ForgeTrust.AppSurface.Evidence.Supervision.Tests.csproj'
ASSEMBLY = 'ForgeTrust.AppSurface.Evidence.Supervision'
NAMES = (ASSEMBLY + '.dll', ASSEMBLY + '.pdb')
PREFIX = 'ForgeTrust.AppSurface.Evidence.Supervision.Tests.'
CLASSES = {
    'N04': ('LinuxN04CheckpointOrderTests', 'LinuxNegativeKernelObservationTests'),
    'N05': ('LinuxSlotNegativeObservationTests',),
    'N07': ('LinuxFailedSettlementObservationTests', 'LinuxWorkerStopTimingTests', 'LinuxN07FailureSettlementTests'),
    'N08': ('LinuxJoinedWorkerOutputDiagnosticTests',),
    'N10': ('LinuxN10PendingStartCheckpointTests',),
    'N12': ('LinuxN12DescendantTests',),
    'N13': ('LinuxOwnerDeathPhaseFrameTests', 'SupervisionAcceptedWorkFramingTests'),
    'N14': ('LinuxOwnerDeathPhaseFrameTests',),
    'N16': ('LinuxN16ProgressDiagnosticTests', 'LinuxNegativeKernelObservationTests'),
}
LIMIT = 8 * 1024 * 1024
PREPARED_FIELDS = ('schema source selector workflow_identity source_capture_sha256 original_build_sha256 '
                   'sdk test_binary classes original_files commands native_acceptance coverage_credit').split()
RESULT_FIELDS = ('schema prepared_sha256 instrumented restored commands trx native_acceptance coverage_credit').split()


def need(value, reason):
    binding.require(value, 'measurement-data-' + reason)


def check(deadline):
    binding._remaining(deadline)


def read(path, maximum, deadline):
    # Definition-only import avoids a separate or weaker native file reader.
    import measurement
    return measurement.read(path, maximum, deadline)


def paths(args, original):
    """Derive executable and test paths from the verified current build, never artifact-supplied argv."""
    root = Path(original['source_root'])
    sdk = Path(original['commands'][0]['argv'][0])
    need(root.is_absolute() and sdk.is_absolute() and sdk.name == 'dotnet', 'paths')
    project = root / PROJECT
    binary = project.parent / ('bin/Release/net10.0/' + ASSEMBLY + '.Tests.dll')
    data = Path(args.evidence) / 'data-tests'
    return root, sdk, project, binary, data


def argv(args, original):
    root, sdk, project, binary, data = paths(args, original)
    selected = '-p:EvidencePrivateQualification=' + args.selector
    classes = [PREFIX + name for name in CLASSES[args.selector]]
    return (
        [str(sdk), 'restore', str(project), '--locked-mode', '--disable-parallel', '-warnaserror', selected],
        [str(sdk), 'build', str(project), '--no-restore', '-c', 'Release', '-warnaserror',
         '-p:UseSharedCompilation=false', selected],
        [str(sdk), 'vstest', str(binary), '--TestCaseFilter:' + '|'.join('FullyQualifiedName~' + name for name in classes),
         '--Logger:trx;LogFileName=data.trx', '--ResultsDirectory:' + str(data / 'trx')],
    )


def write_new(path, raw):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    with os.fdopen(fd, 'wb') as stream:
        stream.write(raw)
        stream.flush()
        os.fsync(stream.fileno())


def command(args, original, index, deadline):
    """Run a joined SDK phase with disposable private XDG data, using the original remaining bound.

    The directory is outside the source/evidence trees and is removed on success
    or failure. Its environment belongs only to this trusted SDK command, not to
    the original native procedure or the protected worker's environment.
    """
    root, sdk, _, _, data = paths(args, original)
    phase = ('data-restore', 'data-build', 'data-run')[index]
    reserve = 60 if index == 2 else 10
    end = min(deadline - reserve, time.monotonic() + 180)
    check(end)
    env = os.environ.copy()
    env.update(DOTNET_ROOT=str(sdk.parent), DOTNET_CLI_USE_MSBUILD_SERVER='0',
               MSBUILDDISABLENODEREUSE='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1')
    # NuGet's first-use migration must never fall back to a relative .local path
    # when the fixed outer harness HOME does not exist. This directory belongs
    # only to the trusted SDK process; it is never passed to a native worker.
    parent = args.evidence.parent.resolve(strict=True)
    need(parent != root and root not in parent.parents, 'sdk-data-parent')
    with tempfile.TemporaryDirectory(prefix='c1-sdk-data-', dir=parent) as directory:
        sdk_data = Path(directory).resolve(strict=True)
        need(sdk_data.is_absolute() and not binding.overlaps(root, sdk_data), 'sdk-data-root')
        env['XDG_DATA_HOME'] = str(sdk_data)
        row = publish.command(argv(args, original)[index], phase, data / (phase + '.log'), root, env, end)
    need(type(row['exit']) is int and row['exit'] == 0 and row['joined'] is True
         and row['timed_out'] is False, 'command')
    return row


def projection(raw):
    return {'sha256': binding.digest(raw), 'bytes': len(raw)}


def require_prepared(args, original, prepared):
    """Check fixed paths and original-image identity before any output replacement or dispatch."""
    binding.exact(prepared, PREPARED_FIELDS, 'measurement-data-prepared-fields')
    _, sdk, _, binary, _ = paths(args, original)
    need(prepared['schema'] == 'issue779-c1-data-prepared-v1'
         and prepared['source'] == args.source and prepared['selector'] == args.selector
         and prepared['workflow_identity'] == args.workflow_identity
         and prepared['source_capture_sha256'] == original['source_capture_sha256']
         and prepared['original_build_sha256'] == binding.digest(binding.canonical(original))
         and prepared['sdk'] == str(sdk) and prepared['test_binary'] == str(binary)
         and prepared['classes'] == [PREFIX + name for name in CLASSES[args.selector]]
         and prepared['native_acceptance'] is False and prepared['coverage_credit'] is False, 'prepared')
    binding.exact(prepared['original_files'], NAMES, 'measurement-data-original-fields')
    for name in NAMES:
        value = prepared['original_files'][name]
        binding.exact(value, ('sha256', 'bytes'), 'measurement-data-original-file-fields')
        need(value['sha256'] == original['tool']['files'][name]['sha256']
             and binding.integer(value['bytes'], 1, LIMIT), 'prepared-original')


def prepare(args, original, capture_raw, *, deadline):
    """Build fixed metadata tests before instrumentation; bind original runtime bytes and retain backups."""
    if args.selector not in CLASSES:
        return None
    check(deadline)
    binding.validate_build(original, capture_raw, args.source, args.selector)
    root, sdk, _, binary, data = paths(args, original)
    need(root.resolve(strict=True) == root and str(root) == original['source_root']
         and original['configuration'] == 'Release' and original['sdk_version'] == '10.0.401', 'source')
    os.mkdir(data, 0o700)
    commands = [command(args, original, index, deadline) for index in (0, 1)]
    os.mkdir(data / 'originals', 0o700)
    files = {}
    for name in NAMES:
        test_raw = read(binary.parent / name, LIMIT, deadline)
        tool_raw = read(Path(original['tool']['root']) / name, LIMIT, deadline)
        need(test_raw == tool_raw and binding.digest(test_raw) == original['tool']['files'][name]['sha256'],
             'original-runtime')
        write_new(data / 'originals' / name, test_raw)
        files[name] = projection(test_raw)
    result = {'schema': 'issue779-c1-data-prepared-v1', 'source': args.source, 'selector': args.selector,
              'workflow_identity': args.workflow_identity, 'source_capture_sha256': binding.digest(capture_raw),
              'original_build_sha256': binding.digest(binding.canonical(original)), 'sdk': str(sdk),
              'test_binary': str(binary), 'classes': [PREFIX + name for name in CLASSES[args.selector]],
              'original_files': files, 'commands': commands, 'native_acceptance': False, 'coverage_credit': False}
    write_new(data / 'prepared.json', binding.canonical(result))
    return result


def replace_owned(path, expected, raw, deadline, *, on_write=None, expected_pin=None):
    """Validate, mutate and read back one retained inode under retained no-follow parents."""
    check(deadline)
    path = binding.absolute(str(path))
    need(len(expected) <= LIMIT and len(raw) <= LIMIT, 'replace-size')
    fds = [os.open('/', os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)]
    parents = []
    signature = lambda value: (value.st_dev, value.st_ino, value.st_uid, value.st_gid, value.st_mode)

    def bytes_from(fd):
        body = bytearray()
        while True:
            check(deadline)
            part = os.pread(fd, min(65536, LIMIT + 1 - len(body)), len(body))
            if not part:
                return bytes(body)
            body.extend(part)
            need(len(body) <= LIMIT, 'replace-read-size')

    try:
        for name in path.parts[1:-1]:
            check(deadline)
            owner = fds[-1]
            before = os.stat(name, dir_fd=owner, follow_symlinks=False)
            need(stat.S_ISDIR(before.st_mode), 'replace-parent')
            child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=owner)
            fds.append(child)
            need(signature(os.fstat(child)) == signature(before), 'replace-parent-open')
            parents.append((owner, name, child, signature(before)))
        owner = fds[-1]
        before = os.stat(path.name, dir_fd=owner, follow_symlinks=False)
        need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and before.st_size <= LIMIT, 'replace-leaf')
        fd = os.open(path.name, os.O_RDWR | os.O_NONBLOCK | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=owner)
        fds.append(fd)
        current = os.fstat(fd)
        pin = signature(current)
        need(stat.S_ISREG(current.st_mode) and current.st_nlink == 1
             and binding._identity(current) == binding._identity(before), 'replace-inode')
        need(expected_pin is None or pin == expected_pin, 'replace-pin')
        need(bytes_from(fd) == expected and binding._identity(os.fstat(fd)) == binding._identity(before)
             == binding._identity(os.stat(path.name, dir_fd=owner, follow_symlinks=False)), 'replace-original')
        if on_write is not None:
            on_write(pin)
        os.ftruncate(fd, 0)
        view = memoryview(raw)
        while view:
            check(deadline)
            count = os.write(fd, view)
            need(count > 0, 'replace-write')
            view = view[count:]
        os.fsync(fd)
        need(bytes_from(fd) == raw and binding._identity(os.fstat(fd))
             == binding._identity(os.stat(path.name, dir_fd=owner, follow_symlinks=False)), 'replace-readback')
        for parent, name, child, pin in parents:
            need(signature(os.fstat(child)) == pin
                 == signature(os.stat(name, dir_fd=parent, follow_symlinks=False)), 'replace-parent-changed')
    finally:
        for fd in reversed(fds):
            os.close(fd)


def trx(raw, expected_classes):
    """Validate actual passing VSTest records; class data grants no native completion."""
    need(0 < len(raw) <= LIMIT and b'<!' not in raw and b'\x00' not in raw, 'trx-format')
    try:
        root = ET.fromstring(raw)
        ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
        counter = root.find('.//t:Counters', ns)
        rows = root.findall('.//t:UnitTestResult', ns)
        definitions = root.findall('.//t:TestDefinitions/t:UnitTest', ns)
        need(counter is not None and rows and definitions, 'trx-rows')
        by_id = {}
        for definition in definitions:
            key = definition.get('id')
            methods = definition.findall('t:TestMethod', ns)
            need(key and key not in by_id and len(methods) == 1, 'trx-definition')
            by_id[key] = methods[0].get('className')
        result_ids = [row.get('testId') for row in rows]
        need(len(set(result_ids)) == len(rows) and set(result_ids) == set(by_id)
             and set(by_id.values()) == set(expected_classes), 'trx-linkage')
        counts = {name: int(counter.get(name, '-1')) for name in ('total', 'executed', 'passed', 'failed', 'error', 'notExecuted')}
        need(counts['total'] == counts['executed'] == counts['passed'] == len(rows)
             and counts['failed'] == counts['error'] == counts['notExecuted'] == 0
             and all(row.get('outcome') == 'Passed' for row in rows), 'trx-passed')
        return counts
    except (ET.ParseError, ValueError, AttributeError):
        need(False, 'trx-format')


def run(args, original, prepared, *, deadline):
    """Run only fixed data classes through the active collector's exact instrumented runtime, then restore."""
    if args.selector not in CLASSES:
        return None
    _, _, _, binary, data = paths(args, original)
    require_prepared(args, original, prepared)
    need(binding.decode(read(data / 'prepared.json', LIMIT, deadline)) == prepared, 'prepared-changed')
    originals = {name: read(data / 'originals' / name, LIMIT, deadline) for name in NAMES}
    instrumented = {name: read(Path(original['tool']['root']) / name, LIMIT, deadline) for name in NAMES}
    measured_raw = read(Path(args.evidence) / 'build-measured.json', LIMIT, deadline)
    measured = binding.decode(measured_raw)
    need(measured['source'] == args.source and measured['selector'] == args.selector
         and measured['measurement']['original_build_sha256'] == prepared['original_build_sha256']
         and {name: binding.digest(raw) for name, raw in instrumented.items()}
             == measured['measurement']['instrumented_modules'], 'selected-instrumented-image')
    need(binding.digest(instrumented[NAMES[0]]) != prepared['original_files'][NAMES[0]]['sha256'], 'not-instrumented')
    replaced = {}
    first_error = None
    try:
        for name in NAMES:
            need(projection(originals[name]) == prepared['original_files'][name], 'backup')
            # Retain the actual inode obligation after validation and before the first truncation.
            replace_owned(binary.parent / name, originals[name], instrumented[name], deadline,
                          on_write=lambda pin, name=name: replaced.__setitem__(name, pin))
        row = command(args, original, 2, deadline)
    except BaseException as error:
        first_error = error
    finally:
        failed = False
        for name in reversed(replaced):
            try:
                current = read(binary.parent / name, LIMIT, deadline)
                replace_owned(binary.parent / name, current, originals[name], deadline,
                              expected_pin=replaced[name])
            except Exception:
                failed = True
    if first_error is not None:
        raise first_error
    need(not failed, 'restore')
    raw = read(data / 'trx/data.trx', LIMIT, deadline)
    counts = trx(raw, prepared['classes'])
    for name in NAMES:
        need(read(binary.parent / name, LIMIT, deadline) == originals[name]
             and read(Path(original['tool']['root']) / name, LIMIT, deadline) == instrumented[name], 'settled-images')
    need(read(Path(args.evidence) / 'build-measured.json', LIMIT, deadline) == measured_raw, 'measured-build-changed')
    result = {'schema': 'issue779-c1-data-result-v1', 'prepared_sha256': binding.digest(binding.canonical(prepared)),
              'instrumented': {name: projection(instrumented[name]) for name in NAMES},
              'restored': prepared['original_files'], 'commands': [row],
              'trx': {**projection(raw), 'counts': counts}, 'native_acceptance': False, 'coverage_credit': False}
    write_new(data / 'result.json', binding.canonical(result))
    return result


def validate(raws, args, original, prepared, *, deadline):
    """Pure-data aggregate check over re-read retained leaves; never executes a process or reads the OS."""
    check(deadline)
    need(args.selector in CLASSES, 'selector')
    get = lambda name: raws['data-tests/' + name]
    need(set(raws) == {'data-tests/' + name for name in
         ('prepared.json', 'result.json', 'data-restore.log', 'data-build.log', 'data-run.log',
          'trx/data.trx', 'originals/' + NAMES[0], 'originals/' + NAMES[1])}, 'leaves')
    require_prepared(args, original, prepared)
    _, sdk, _, binary, data = paths(args, original)
    need(get('prepared.json') == binding.canonical(prepared), 'prepared-bytes')
    result = binding.decode(get('result.json'))
    binding.exact(result, RESULT_FIELDS, 'measurement-data-result-fields')
    need(result['schema'] == 'issue779-c1-data-result-v1'
         and result['prepared_sha256'] == binding.digest(get('prepared.json'))
         and result['restored'] == prepared['original_files']
         and result['native_acceptance'] is False and result['coverage_credit'] is False, 'result')
    binding.exact(result['instrumented'], NAMES, 'measurement-data-instrumented-fields')
    for name in NAMES:
        need(prepared['original_files'][name] == projection(get('originals/' + name))
             and prepared['original_files'][name]['sha256'] == original['tool']['files'][name]['sha256'], 'original-file')
        binding.exact(result['instrumented'][name], ('sha256', 'bytes'), 'measurement-data-image-fields')
        need(type(result['instrumented'][name]['sha256']) is str and binding.HEX64.fullmatch(result['instrumented'][name]['sha256'])
             and binding.integer(result['instrumented'][name]['bytes'], 1, LIMIT), 'instrumented-file')
    need(result['instrumented'][NAMES[0]]['sha256'] != prepared['original_files'][NAMES[0]]['sha256'], 'instrumented-change')
    need(result['trx'] == {**projection(get('trx/data.trx')), 'counts': trx(get('trx/data.trx'), prepared['classes'])}, 'trx-binding')
    need(type(prepared['commands']) is list and len(prepared['commands']) == 2
         and type(result['commands']) is list and len(result['commands']) == 1, 'command-count')
    for index, row in enumerate([*prepared['commands'], *result['commands']]):
        phase = ('data-restore', 'data-build', 'data-run')[index]
        binding.exact(row, ('phase', 'argv', 'exit', 'timed_out', 'joined', 'log_path', 'log_sha256', 'log_bytes'),
                      'measurement-data-command-fields')
        log = get(phase + '.log')
        need(row['phase'] == phase and row['argv'] == argv(args, original)[index]
             and type(row['exit']) is int and row['exit'] == 0 and row['timed_out'] is False and row['joined'] is True
             and row['log_path'] == str(data / (phase + '.log')) and row['log_sha256'] == binding.digest(log)
             and type(row['log_bytes']) is int and row['log_bytes'] == len(log) <= LIMIT, 'command-binding')
    return result
