"""Bound the separate 29-row application audit and report-discovery TRX; data grants no native authority."""
from pathlib import Path
import hashlib
import os
import stat
import xml.etree.ElementTree as ET

MAXIMUM_BYTES = 1024 * 1024
AUDIT_PREFIX = 'ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxApplicationBundleAuditNativeTests.'
DISCOVERY_PREFIX = 'ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxProducerReportDiscoveryNativeTests.'
RESULTS_PREFIX = 'ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxSelectedProducerResultsRootNativeTests.'
METHODS = (
    AUDIT_PREFIX + 'ActualRootTreeInventoryAcceptsSelectedBytesAndRetainsOnlyBorrowedNodes',
    AUDIT_PREFIX + 'ActualExtraFileIsRejectedAfterItsDescriptorJoinsTheCallersQuarantineSet',
    AUDIT_PREFIX + 'ChangedDeclaredBytesAreRejectedAndTheActualTreeRemainsUnmodifiedByAudit',
    AUDIT_PREFIX + 'PreCancelledAuditStopsBeforeOpeningChildrenAndLeavesRootAndSourceBytesIntact',
    AUDIT_PREFIX + 'InvalidNameAndSpecialFilesystemChildrenAreRejectedWithoutChangingTheirSentinel',
    AUDIT_PREFIX + 'ActualThirtySecondDirectoryLevelIsAcceptedAndThirtyThirdIsRejectedAfterRetention',
    DISCOVERY_PREFIX + 'ActualResultsDescriptorDiscoversOnlySortedCanonicalReportPaths',
    DISCOVERY_PREFIX + 'NonReportEntriesAndEmptyNestedDirectoriesReject(kind: "non-report")',
    DISCOVERY_PREFIX + 'NonReportEntriesAndEmptyNestedDirectoriesReject(kind: "empty-nested-directory")',
    DISCOVERY_PREFIX + 'SpecialOrUnsafeActualEntriesRejectWithoutChangingReportBytes(kind: "symlink")',
    DISCOVERY_PREFIX + 'SpecialOrUnsafeActualEntriesRejectWithoutChangingReportBytes(kind: "hardlink")',
    DISCOVERY_PREFIX + 'SpecialOrUnsafeActualEntriesRejectWithoutChangingReportBytes(kind: "fifo")',
    DISCOVERY_PREFIX + 'SpecialOrUnsafeActualEntriesRejectWithoutChangingReportBytes(kind: "wrong-mode")',
    DISCOVERY_PREFIX + 'SpecialOrUnsafeActualEntriesRejectWithoutChangingReportBytes(kind: "wrong-owner")',
    DISCOVERY_PREFIX + 'SixtyFourthReportIsAcceptedAndTheNextActualReportIsRejected',
    DISCOVERY_PREFIX + 'ThirtySecondPathComponentIsAcceptedAndThirtyThirdIsRejected',
    DISCOVERY_PREFIX + 'PreCancelledDiscoveryStopsBeforeNativeEnumeration',
    RESULTS_PREFIX + 'ActualSelectedDirectoryRechecksAndRepeatedDisposeClosesTheRetainedDescriptor',
    RESULTS_PREFIX + 'ASelectedRootWithAnotherKernelInodeRejectsBeforeAdoptingTheActualDirectory',
    RESULTS_PREFIX + 'SelectedDirectoryRequiresExactPrivateModeEvenWhenTheSampleMatchesTheKernel',
    RESULTS_PREFIX + 'SelectedDirectoryRequiresTheOriginalSubjectOwner',
    RESULTS_PREFIX + 'ResultsParentRequiresTheExactRootOwnedSearchableMode',
    RESULTS_PREFIX + 'WritableAncestorRejectsWithoutOpeningAReplacementOrChangingItsBytes',
    RESULTS_PREFIX + 'RenamedSelectedDirectoryCannotBeReplacedBehindItsRetainedDescriptor',
    RESULTS_PREFIX + 'RenamedResultsParentCannotBeReplacedBehindTheRetainedChain',
    RESULTS_PREFIX + 'ChangedSelectedOwnerAfterOpenRejectsTheNextKernelRecheck',
    RESULTS_PREFIX + 'SelectedSymlinkCannotAdoptAnOtherwiseValidOutsideDirectory',
    RESULTS_PREFIX + 'PreCancelledOpenAndRecheckKeepTheOriginalTokenAndNeverChangeTheTree',
    RESULTS_PREFIX + 'ExpiredOriginalDeadlineRejectsOpenAndRecheckWithoutRenewingTheAllowance',
)
ZERO_COUNTERS = ('failed', 'error', 'notExecuted', 'warning', 'aborted', 'inconclusive',
                 'timeout', 'notRunnable', 'disconnected', 'completed', 'inProgress',
                 'pending', 'passedButRunAborted')
NS = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'


def require(condition):
    if not condition:
        raise ValueError('application-bundle-measurement-invalid')


def parse_trx(raw):
    """Compare bounded raw XML and exact names/counters; no file or execution claim is issued."""
    require(type(raw) is bytes and 0 < len(raw) <= MAXIMUM_BYTES)
    # Only ordinary UTF-8 TRX is emitted by the fixed collector. Reject declarations before parsing.
    require(b'\x00' not in raw and b'<!DOCTYPE' not in raw.upper() and b'<!ENTITY' not in raw.upper())
    document = ET.fromstring(raw)
    require(document.tag == NS + 'TestRun')
    counters = document.findall('.//' + NS + 'Counters')
    require(len(counters) == 1)
    counter = counters[0].attrib
    require(set(counter) == set(ZERO_COUNTERS) | {'total', 'executed', 'passed'})
    require(all(counter.get(key) == '29' for key in ('total', 'executed', 'passed')))
    require(all(counter.get(key) == '0' for key in ZERO_COUNTERS))
    rows = document.findall('.//' + NS + 'UnitTestResult')
    require(len(rows) == 29 and all(row.get('outcome') == 'Passed' for row in rows))
    require({row.get('testName') for row in rows} == set(METHODS))
    return {'trx_sha256': hashlib.sha256(raw).hexdigest(), 'executed': 29, 'passed': 29,
            'native_acceptance': False}


def read_trx(path):
    """Read one stable bounded single-link regular leaf without following links or blocking on FIFOs."""
    fd = os.open(Path(path), os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
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
