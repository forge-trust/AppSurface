"""Run N04's actual four-byte classifier on real files; no owner or native authority."""
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).parent / 'procedures/slot/checkpoint-n04.sh'


class N04StdlibMagicTests(unittest.TestCase):
    def setUp(self):
        source = SCRIPT.read_text()
        start = source.index(' python_magic=$(bounded od ')
        end = source.index('  python_resolved=$python_file', start)
        # Execute the exact production read, whitespace normalization and decision.
        self.classifier = source[start:end]

    def classify(self, path, *, read_exit=0):
        program = r'''set -euo pipefail
export LC_ALL=C
python_file=$1; read_exit=$2
bounded() {
 [[ $1 == od ]] || exit 91
 printf 'bounded:od\n' >&2
 if ((read_exit!=0)); then return "$read_exit"; fi
 /usr/bin/od "${@:2}"
}
''' + self.classifier + r'''
 printf 'ELF\n'
else
 printf 'OTHER\n'
fi
'''
        return subprocess.run(['/bin/bash', '--noprofile', '--norc', '-c', program,
                               'n04-magic', str(path), str(read_exit)],
                              capture_output=True, timeout=3)

    def test_real_binary_prefixes_match_exact_first_four_bytes(self):
        vectors = [b'', b'\x7f', b'\x7fE', b'\x7fEL', b'\x7fELF',
                   b'\x7fELF\0\xff\n', b'\0\x7fELF', b' \x7fELF',
                   b'\x7fE\0LF', b'\x7fE\nLF', b'\xffELF', b'data\x7fELF']
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'actual-file'
            for body in vectors:
                path.write_bytes(body)
                with self.subTest(body=body):
                    result = self.classify(path)
                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertEqual(b'ELF\n' if body[:4] == b'\x7fELF' else b'OTHER\n', result.stdout)
                    self.assertEqual(b'bounded:od\n', result.stderr)

    def test_missing_node_cannot_be_classified_as_non_elf(self):
        with tempfile.TemporaryDirectory() as directory:
            result = self.classify(Path(directory) / 'missing')
            self.assertNotEqual(0, result.returncode)
            self.assertEqual(b'', result.stdout)
            self.assertIn(b'bounded:od\n', result.stderr)

    @unittest.skipUnless(sys.platform.startswith('linux'), 'requires actual Linux od directory-read rejection')
    def test_linux_directory_read_error_cannot_be_classified_as_non_elf(self):
        with tempfile.TemporaryDirectory() as directory:
            result = self.classify(Path(directory))
            self.assertNotEqual(0, result.returncode)
            self.assertEqual(b'', result.stdout)
            self.assertIn(b'bounded:od\n', result.stderr)

    def test_bounded_read_error_or_deadline_stops_before_classification(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'elf'
            path.write_bytes(b'\x7fELF')
            for status in (1, 124, 137):
                with self.subTest(status=status):
                    result = self.classify(path, read_exit=status)
                    self.assertEqual(status, result.returncode)
                    self.assertEqual(b'', result.stdout)
                    self.assertEqual(b'bounded:od\n', result.stderr)


if __name__ == '__main__':
    unittest.main()
