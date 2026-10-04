"""Portable owned-file procedures only: no download, root installation or admission."""
import gzip
import contextlib
import hashlib
import io
import os
from pathlib import Path
import stat
import tarfile
import tempfile
import time
import unittest
from unittest import mock

import trusted_sdk_distribution as distribution


def fixture(extra=(), *, format=tarfile.USTAR_FORMAT, suffix=b''):
    members = [('.', None, tarfile.DIRTYPE, 0o755),
               ('./host/', None, tarfile.DIRTYPE, 0o755),
               ('./host/fxr/', None, tarfile.DIRTYPE, 0o755),
               ('./shared/', None, tarfile.DIRTYPE, 0o755),
               ('./sdk/', None, tarfile.DIRTYPE, 0o755),
               ('./dotnet', b'ELF-metadata-fixture\x00', tarfile.REGTYPE, 0o755),
               ('./sdk/empty', b'', tarfile.REGTYPE, 0o644),
               ('./sdk/binary', b'private-canary\x00\xff', tarfile.REGTYPE, 0o644)]
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode='w', format=format) as archive:
        for name, data, kind, mode in members + list(extra):
            member = tarfile.TarInfo(name)
            member.type, member.mode = kind, mode
            if kind in (tarfile.SYMTYPE, tarfile.LNKTYPE):
                member.linkname = 'private-canary'
            if format == tarfile.PAX_FORMAT:
                member.pax_headers = {'comment': 'private-canary'}
            member.size = len(data) if data is not None else 0
            archive.addfile(member, io.BytesIO(data) if data is not None else None)
    return gzip.compress(buffer.getvalue() + suffix, mtime=0)


def control_fixture(payload, *, kind=tarfile.GNUTYPE_LONGNAME, nested=False, orphan=False, bad_padding=False):
    """Small raw control-record fixture, not publisher material or a usable SDK."""
    base = gzip.decompress(fixture())
    with tarfile.open(fileobj=io.BytesIO(base), mode='r:') as archive:
        last = archive.getmembers()[-1]
        end = last.offset_data + (last.size + 511) // 512 * 512
    control = tarfile.TarInfo('././@LongLink')
    control.type, control.size, control.mode = kind, len(payload), 0o644
    padding = bytearray((-len(payload)) % 512)
    if bad_padding and padding:
        padding[0] = 1
    data = base[:end] + control.tobuf(format=tarfile.GNU_FORMAT) + payload + padding
    if nested:
        data += control.tobuf(format=tarfile.GNU_FORMAT) + payload + bytes((-len(payload)) % 512)
    if not orphan:
        following = tarfile.TarInfo('placeholder')
        following.mode, following.size = 0o644, 1
        data += following.tobuf(format=tarfile.GNU_FORMAT) + b'x' + bytes(511)
    return gzip.compress(data + bytes(10240), mtime=0)


class DistributionProcedureControls(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.owner = os.geteuid()

    def archive(self, data):
        path = self.root / ('archive-' + str(len(list(self.root.iterdir()))))
        fd = os.open(path, os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        self.addCleanup(os.close, fd)
        os.fchmod(fd, 0o600)
        self.assertEqual(os.write(fd, data), len(data))
        return fd, path, hashlib.sha512(data).hexdigest()

    def audit(self, fd, digest, deadline=None):
        return distribution.audit_archive(fd, digest, time.monotonic() + 10 if deadline is None else deadline,
                                          owner_uid=self.owner)

    def rejected_without_destination_mutation(self, operation):
        with mock.patch.object(distribution.os, 'mkdir') as mkdir, \
                mock.patch.object(distribution.os, 'fchmod') as chmod, \
                mock.patch.object(distribution, '_download') as download:
            with self.assertRaises(distribution.DistributionFailure) as caught:
                operation()
            self.assertEqual(str(caught.exception), 'trusted-sdk-distribution-rejected')
            self.assertNotIn('private-canary', str(caught.exception))
            mkdir.assert_not_called()
            chmod.assert_not_called()
            download.assert_not_called()

    def test_complete_owned_fixture_preserves_all_bytes_modes_and_archive_identity(self):
        data = fixture()
        fd, path, digest = self.archive(data)
        before = distribution.identity(os.fstat(fd))
        audited = self.audit(fd, digest)
        destination = self.root / 'output'
        destination.mkdir(mode=0o700)
        os.chmod(destination, 0o700)
        target = os.open(destination, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            tree = distribution.extract_audited(fd, target, audited, time.monotonic() + 10, owner_uid=self.owner)
        finally:
            os.close(target)
        self.assertEqual(len(tree), 64)
        self.assertEqual((destination / 'sdk/binary').read_bytes(), b'private-canary\x00\xff')
        self.assertEqual((destination / 'sdk/empty').read_bytes(), b'')
        self.assertEqual(stat.S_IMODE((destination / 'dotnet').stat().st_mode), 0o755)
        self.assertEqual(stat.S_IMODE((destination / 'sdk/binary').stat().st_mode), 0o644)
        self.assertEqual(distribution.identity(os.fstat(fd)), before)
        self.assertEqual(path.read_bytes(), data)
        self.assertEqual(set(x.relative_to(destination).as_posix() for x in destination.rglob('*')),
                         set(r.path for r in audited.members if r.path))

    def test_digest_rejection_precedes_tar_parsing_and_every_destination_operation(self):
        fd, _, _ = self.archive(fixture())
        with mock.patch.object(distribution, '_tar') as parser:
            self.rejected_without_destination_mutation(lambda: self.audit(fd, '0' * 128))
            parser.assert_not_called()

    def test_paths_types_extensions_and_hidden_trailing_payload_reject_before_mutation(self):
        invalid = [fixture([(p, b'x', tarfile.REGTYPE, 0o644)])
                   for p in ('/absolute', '../escape', './sdk/../escape', 'sdk//x', 'sdk/./x',
                             'C:drive', 'sdk\\x', 'p/' * 32 + 'x')]
        invalid += [fixture([('sdk/private-canary', None, kind, 0o644)])
                    for kind in (tarfile.SYMTYPE, tarfile.LNKTYPE, tarfile.FIFOTYPE,
                                 tarfile.CHRTYPE, tarfile.BLKTYPE)]
        invalid += [fixture(format=tarfile.PAX_FORMAT), fixture(suffix=b'private-canary')]
        for data in invalid:
            with self.subTest(sha512=hashlib.sha512(data).hexdigest()):
                fd, _, digest = self.archive(data)
                self.rejected_without_destination_mutation(lambda: self.audit(fd, digest))

    def test_duplicate_file_directory_collision_special_modes_and_bounds_reject(self):
        cases = [fixture([('./sdk/binary', b'x', tarfile.REGTYPE, 0o644)]),
                 fixture([('./host', b'x', tarfile.REGTYPE, 0o644)]),
                 fixture([('./sdk/private-canary', b'x', tarfile.REGTYPE, 0o4644)]),
                 fixture([('./dotnet/child', b'x', tarfile.REGTYPE, 0o644)])]
        for data in cases:
            fd, _, digest = self.archive(data)
            self.rejected_without_destination_mutation(lambda: self.audit(fd, digest))
        for bound, value in (('MAX_NODES', 2), ('MAX_MEMBER_BYTES', 1), ('MAX_TOTAL_BYTES', 1),
                             ('MAX_DEPTH', 1), ('MAX_ARCHIVE_BYTES', 1)):
            fd, _, digest = self.archive(fixture())
            with self.subTest(bound=bound), mock.patch.object(distribution, bound, value):
                self.rejected_without_destination_mutation(lambda: self.audit(fd, digest))

    def test_archive_owner_mode_hardlink_nofollow_and_expired_deadline_guards(self):
        fd, path, digest = self.archive(fixture())
        self.rejected_without_destination_mutation(lambda: distribution.audit_archive(
            fd, digest, time.monotonic() + 10, owner_uid=self.owner + 1))
        os.chmod(path, 0o644)
        self.rejected_without_destination_mutation(lambda: self.audit(fd, digest))
        os.chmod(path, 0o600)
        os.link(path, self.root / 'hardlink')
        self.rejected_without_destination_mutation(lambda: self.audit(fd, digest))
        os.unlink(self.root / 'hardlink')
        link = self.root / 'symlink'
        link.symlink_to(path)
        with self.assertRaises(OSError):
            opened = os.open(link, os.O_RDONLY | os.O_NOFOLLOW)
            os.close(opened)
        self.rejected_without_destination_mutation(lambda: self.audit(fd, digest, time.monotonic() - 1))

    def test_retained_archive_change_and_nonempty_destination_reject_before_write(self):
        fd, _, digest = self.archive(fixture())
        audited = self.audit(fd, digest)
        destination = self.root / 'output'
        destination.mkdir(mode=0o700)
        os.chmod(destination, 0o700)
        target = os.open(destination, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            (destination / 'existing').write_bytes(b'private-canary')
            self.rejected_without_destination_mutation(lambda: distribution.extract_audited(
                fd, target, audited, time.monotonic() + 10, owner_uid=self.owner))
            (destination / 'existing').unlink()
            os.lseek(fd, 0, os.SEEK_SET)
            os.write(fd, b'x')
            self.rejected_without_destination_mutation(lambda: distribution.extract_audited(
                fd, target, audited, time.monotonic() + 10, owner_uid=self.owner))
            self.assertEqual(list(destination.iterdir()), [])
        finally:
            os.close(target)

    def test_actual_read_error_is_closed_and_cannot_echo_exception_canary(self):
        fd, _, digest = self.archive(fixture())
        with mock.patch.object(distribution.os, 'read', side_effect=OSError('private-canary')):
            self.rejected_without_destination_mutation(lambda: self.audit(fd, digest))

    def test_named_identity_substitution_and_short_write_fail_without_complete_result(self):
        fd, _, digest = self.archive(fixture())
        audited = self.audit(fd, digest)
        destination = self.root / 'output'
        destination.mkdir(mode=0o700)
        target = os.open(destination, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            with mock.patch.object(distribution.os, 'write', return_value=0):
                with self.assertRaises(distribution.DistributionFailure):
                    distribution.extract_audited(fd, target, audited, time.monotonic() + 10,
                                                 owner_uid=self.owner)
            # Partial failure is quarantined, never silently resumed in place.
            with self.assertRaises(distribution.DistributionFailure):
                distribution.extract_audited(fd, target, audited, time.monotonic() + 10,
                                             owner_uid=self.owner)
            selected_path = self.root / 'selected'
            selected_path.write_bytes(b'owned-metadata-fixture')
            parent = os.open(self.root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                selected = os.open('selected', os.O_RDONLY | os.O_NOFOLLOW, dir_fd=parent)
                try:
                    os.rename(selected_path, self.root / 'old-selected')
                    selected_path.write_bytes(b'private-canary')
                    with self.assertRaises(distribution.DistributionFailure):
                        distribution.named(parent, 'selected', selected)
                finally:
                    os.close(selected)
            finally:
                os.close(parent)
        finally:
            os.close(target)


    def test_gnu_longname_file_and_directory_keep_exact_resolved_tree(self):
        long_directory = 'sdk/' + 'd' * 120
        long_file = long_directory + '/child'
        data = fixture([(long_directory, None, tarfile.DIRTYPE, 0o755),
                        (long_file, b'private-canary\x00\xff', tarfile.REGTYPE, 0o644)],
                       format=tarfile.GNU_FORMAT)
        fd, _, digest = self.archive(data)
        audit = self.audit(fd, digest)
        self.assertEqual(audit.gnu_longname_headers, 2)
        self.assertIn(long_file, {r.path for r in audit.members})
        destination = self.root / 'gnu-output'
        destination.mkdir(mode=0o700)
        os.chmod(destination, 0o700)
        target = os.open(destination, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            tree = distribution.extract_audited(fd, target, audit, time.monotonic() + 10,
                                                 owner_uid=self.owner)
        finally:
            os.close(target)
        self.assertEqual(len(tree), 64)
        self.assertEqual((destination / long_file).read_bytes(), b'private-canary\x00\xff')
        self.assertEqual(stat.S_IMODE((destination / long_directory).stat().st_mode), 0o755)

    def test_gnu_longname_invalid_payload_nesting_orphan_and_other_controls_fail_before_mutation(self):
        invalid = [control_fixture(name) for name in
                   (b'../private-canary\x00', b'/private-canary\x00', b'sdk/binary\x00',
                    b'sdk//private-canary\x00', b'sdk/a\x00b\x00', b'private-canary',
                    b'x' * 4096 + b'\x00')]
        valid_payload = b'sdk/long-control-fixture\x00'
        invalid += [control_fixture(valid_payload, nested=True),
                    control_fixture(valid_payload, orphan=True),
                    control_fixture(valid_payload, bad_padding=True)]
        invalid += [control_fixture(valid_payload, kind=k) for k in
                    (tarfile.GNUTYPE_LONGLINK, tarfile.XHDTYPE, tarfile.XGLTYPE,
                     tarfile.GNUTYPE_SPARSE, b'Z')]
        for data in invalid:
            with self.subTest(sha512=hashlib.sha512(data).hexdigest()):
                fd, _, digest = self.archive(data)
                self.rejected_without_destination_mutation(lambda: self.audit(fd, digest))

    def test_download_partial_read1_progress_checks_deadline_before_next_read_or_destination(self):
        fd, _, _ = self.archive(b'fixture-initial-bytes')
        os.ftruncate(fd, 0)
        os.lseek(fd, 0, os.SEEK_SET)
        response = mock.MagicMock()
        response.__enter__.return_value = response
        response.status = 200
        response.geturl.return_value = distribution.ARCHIVE_URL
        response.read.side_effect = AssertionError('fill-read must never be used')
        opener = mock.MagicMock()
        opener.open.return_value = response
        with mock.patch.object(distribution.time, 'monotonic', return_value=0) as clock, \
                mock.patch.object(distribution.urllib.request, 'build_opener', return_value=opener), \
                mock.patch.object(distribution.os, 'mkdir') as mkdir, \
                mock.patch.object(distribution, '_tar') as parser:
            def partial(_):
                clock.return_value = 6
                return b'x'
            response.read1.side_effect = partial
            with self.assertRaises(distribution.DistributionFailure):
                distribution._download(fd, 10)
            self.assertEqual(os.fstat(fd).st_size, 1)
            self.assertEqual(response.read1.call_count, 1)
            response.read.assert_not_called()
            mkdir.assert_not_called()
            parser.assert_not_called()
        # This helper control does not claim a process-owner/DNS/signal guarantee.

    def test_cli_unknown_selection_is_closed_exit65_without_install_or_canary_echo(self):
        stdout, stderr = io.StringIO(), io.StringIO()
        with mock.patch.object(distribution, 'install') as install, \
                contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            code = distribution.main(['--install', '--url', 'private-canary'])
        self.assertEqual(code, 65)
        self.assertEqual(stdout.getvalue(), '')
        self.assertNotIn('private-canary', stderr.getvalue())
        self.assertLessEqual(len(stderr.getvalue().encode()), 4096)
        self.assertIn('distribution-rejected', stderr.getvalue())
        install.assert_not_called()


if __name__ == '__main__':
    unittest.main()
