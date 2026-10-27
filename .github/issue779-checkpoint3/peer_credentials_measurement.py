"""Validate the separate four-row actual-peer credential TRX; measurements grant no authority."""
from pathlib import Path
import hashlib
import os
import stat
import xml.etree.ElementTree as ET

MAXIMUM_BYTES = 1024 * 1024
PREFIX = 'ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxPeerCredentialsNativeTests.'
MATCH = PREFIX + 'ActualSetprivPeerMatchesItsKernelPidUidAndGid'
MISMATCH = PREFIX + 'ActualSetprivPeerRejectsEachWrongExpectedCredential'
METHODS = (MATCH, *(MISMATCH + '(field: "' + field + '")' for field in ('gid', 'pid', 'uid')))
ZERO_COUNTERS = ('failed', 'error', 'notExecuted', 'warning', 'aborted', 'inconclusive',
                 'timeout', 'notRunnable', 'disconnected', 'completed', 'inProgress',
                 'pending', 'passedButRunAborted')
NS = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'


def require(condition):
    if not condition:
        raise ValueError('peer-credentials-measurement-invalid')


def parse_trx(raw):
    """Require the exact passing four-row set, without inferring owner or admission proof."""
    require(type(raw) is bytes and 0 < len(raw) <= MAXIMUM_BYTES)
    require(b'\x00' not in raw and b'<!DOCTYPE' not in raw.upper() and b'<!ENTITY' not in raw.upper())
    document = ET.fromstring(raw)
    require(document.tag == NS + 'TestRun')
    counters = document.findall('.//' + NS + 'Counters')
    require(len(counters) == 1)
    values = counters[0].attrib
    require(set(values) == set(ZERO_COUNTERS) | {'total', 'executed', 'passed'})
    require(all(values.get(name) == '4' for name in ('total', 'executed', 'passed')))
    require(all(values.get(name) == '0' for name in ZERO_COUNTERS))
    rows = document.findall('.//' + NS + 'UnitTestResult')
    require(len(rows) == 4 and all(row.get('outcome') == 'Passed' for row in rows))
    require({row.get('testName') for row in rows} == set(METHODS))
    return {'schema': 'issue779-peer-credentials-trx-v1', 'trx_sha256': hashlib.sha256(raw).hexdigest(),
            'executed': 4, 'passed': 4, 'native_acceptance': False}


def read_trx(path):
    """Read one stable bounded single-link regular TRX leaf without following links or blocking on FIFOs."""
    path = Path(path)
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        before = os.fstat(fd)
        require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
                and 0 < before.st_size <= MAXIMUM_BYTES)
        raw = bytearray()
        while len(raw) <= before.st_size:
            part = os.read(fd, min(65536, before.st_size + 1 - len(raw)))
            if not part:
                break
            raw.extend(part)
        signature = lambda value: (value.st_dev, value.st_ino, value.st_mode, value.st_nlink,
                                   value.st_size, value.st_mtime_ns, value.st_ctime_ns)
        require(len(raw) == before.st_size and signature(before) == signature(os.fstat(fd))
                == signature(os.stat(path, follow_symlinks=False)))
        return parse_trx(bytes(raw))
    finally:
        os.close(fd)
