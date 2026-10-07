"""Linux-only unprivileged tests of the actual Bash parser; no root/native acceptance."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shlex
import signal
import stat
import subprocess
import tempfile
import time
import unittest

FIXTURE = None
END = None
BLOCK = None
LEXICAL = None

def group_absent(pid):
    try:
        os.killpg(pid, 0)
        return False
    except ProcessLookupError:
        return True


def invoke(argv, **kwargs):
    # One original 45s interval, with its final 2s reserved for cleanup.
    work_end = END - 2
    if time.monotonic() >= work_end:
        raise TimeoutError("test-original-deadline")
    proc = None
    result = None
    first_error = None
    try:
        proc = subprocess.Popen(argv, start_new_session=True, stdin=subprocess.DEVNULL,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, **kwargs)
        remaining = work_end - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("test-original-deadline")
        result = proc.communicate(timeout=remaining)
        if time.monotonic() >= work_end:
            raise TimeoutError("test-late-completion")
        if any(len(data) > 1048576 for data in result):
            raise AssertionError("test-output-bound")
        if not group_absent(proc.pid):
            raise AssertionError("unsettled-test-group")
    except BaseException as caught:
        first_error = caught
    finally:
        if proc is not None:
            try:
                if proc.poll() is None or not group_absent(proc.pid):
                    try:
                        os.killpg(proc.pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
            except BaseException as caught:
                first_error = first_error or caught
            # Attempt reap/pump settlement independently even after a kill error.
            try:
                remaining = END - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("test-cleanup-deadline")
                result = proc.communicate(timeout=remaining)
            except BaseException as caught:
                first_error = first_error or caught
            for stream in (proc.stdout, proc.stderr):
                try:
                    stream.close()
                except BaseException as caught:
                    first_error = first_error or caught
            try:
                if proc.returncode is None or not group_absent(proc.pid):
                    raise AssertionError("test-cleanup-incomplete")
                if time.monotonic() >= END:
                    raise TimeoutError("test-cleanup-deadline")
            except BaseException as caught:
                first_error = first_error or caught
    if first_error is not None:
        raise first_error
    return proc.returncode, result

class ActualParserControls(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="issue779-batch-data-")
        self.addCleanup(self.tmp.cleanup)
        self.folder = Path(self.tmp.name)
        self.tree = self.folder / "tree"
        self.tree.mkdir(mode=0o700)
        (self.tree / "empty").mkdir(mode=0o700)
        (self.tree / "file").write_bytes(b"abc")
        os.chmod(self.tree / "file", 0o644)
        self.module = self.folder / "functions.sh"
        self.module.write_text(LEXICAL + "\n" + BLOCK)
        self.pre = self.snapshot()

    def snapshot(self):
        code, (data, error) = invoke(["/usr/bin/find", "-P", str(self.tree), "-printf",
            r"%P\0%y\0%D\0%i\0%s\0%m\0%n\0%U\0%G\0%T@\0%C@\0"])
        self.assertEqual(0, code)
        self.assertEqual(b"", error)
        return data

    def rows(self, data):
        values = data[:-1].split(b"\0")
        self.assertEqual(0, len(values) % 11)
        return [values[i:i+11] for i in range(0, len(values), 11)]

    def encode(self, rows):
        return b"".join(value+b"\0" for row in rows for value in row)

    def parse(self, pre=None, post=None, *, sealed=0, file_cap=2147483648):
        pre = self.pre if pre is None else pre
        pre_path = self.folder / "pre"
        post_path = self.folder / "post"
        pre_path.write_bytes(pre)
        if post is not None:
            post_path.write_bytes(post)
        script = self.folder / "parse.sh"
        code = """set -euo pipefail
export LC_ALL=C
source "$1"
fail() { printf 'REJECTED:%s\\n' "$1"; exit 1; }
# Deliberate metadata-only parser call: this override issues no deadline or root capability.
batch_check_time() { :; }
root=$2
MAX_INPUT_FILE_BYTES=$5; MAX_INPUT_TREE_BYTES=4294967296
node_modes=(); node_bytes=(); wanted=(); pre=(); seen=()
declare -A node_modes node_bytes wanted pre seen
wanted[.]=d; wanted[empty]=d; wanted[file]=f
node_modes[.]=0700; node_modes[empty]=0700; node_modes[file]=0644
node_bytes[file]=3; total=0
exec {fd}<"$3"
batch_parse_snapshot pre "$fd" source "$6"
exec {fd}<&-
((${#seen[@]}==${#wanted[@]})) || fail full-node-count
if [[ -n $4 ]]; then
 seen=(); exec {fd}<"$4"
 batch_parse_snapshot post "$fd" source "$6"
 exec {fd}<&-
 ((${#seen[@]}==${#wanted[@]})) || fail full-node-count
fi
printf 'PARSER_DATA_OK\\n'
"""
        code = code.replace("node_modes=(); node_bytes=(); wanted=(); pre=(); seen=()\n", "")
        script.write_text(code)
        status, result = invoke(["/usr/bin/bash", str(script), str(self.module), str(self.tree),
             str(pre_path), str(post_path) if post is not None else "", str(file_cap), str(sealed)])
        self.assertNotIn(b"secret-canary", b"".join(result))
        return status, result

    def test_real_complete_tree_with_empty_directory(self):
        self.assertEqual(0, self.parse()[0])

    def test_real_stable_before_after(self):
        self.assertEqual(0, self.parse(post=self.snapshot())[0])

    def test_missing_empty_directory(self):
        rows = [r for r in self.rows(self.pre) if r[0] != b"empty"]
        self.assertEqual(1, self.parse(self.encode(rows))[0])

    def test_extra_and_duplicate_nodes(self):
        rows = self.rows(self.pre)
        extra = list(rows[-1]); extra[0] = b"extra"
        for data in [self.encode(rows+[extra]), self.encode(rows+[rows[-1]])]:
            self.assertEqual(1, self.parse(data)[0])

    def test_real_symlink_and_fifo(self):
        declared=self.tree/"file"
        outside=self.folder/"sentinel"
        outside.write_bytes(b"abc")
        declared.unlink(); declared.symlink_to(outside)
        self.assertEqual(1, self.parse(self.snapshot())[0])
        declared.unlink(); os.mkfifo(declared)
        self.assertEqual(1, self.parse(self.snapshot())[0])
        self.assertEqual(b"abc",outside.read_bytes())

    def test_real_hardlink(self):
        outside = self.folder / "link"
        os.link(self.tree / "file", outside)
        self.assertEqual(1, self.parse(self.snapshot())[0])

    def test_real_wrong_mode(self):
        os.chmod(self.tree / "file", 0o600)
        self.assertEqual(1, self.parse(self.snapshot())[0])

    def test_real_byte_bound_neighbor(self):
        self.assertEqual(0, self.parse(file_cap=3)[0])
        self.assertEqual(1, self.parse(file_cap=2)[0])

    def test_real_mutation_between_snapshots(self):
        (self.tree / "file").write_bytes(b"abcd")
        self.assertEqual(1, self.parse(post=self.snapshot())[0])

    def test_every_metadata_identity_field_change(self):
        for i in range(2, 11):
            rows = self.rows(self.pre)
            target = next(r for r in rows if r[0] == b"file")
            target[i] = b"1.0" if i >= 9 else (b"999" if target[i] != b"999" else b"998")
            self.assertEqual(1, self.parse(post=self.encode(rows))[0])

    def test_incomplete_nul_records_and_canary_names(self):
        for data in [self.pre[:-1], self.pre+b"tail", self.pre+b"secret-canary\0f\0"]:
            self.assertEqual(1, self.parse(data)[0])

    def function_call(self, body, *args):
        script = self.folder / "functions-test.sh"
        script.write_text("set -euo pipefail\nsource \"$1\"\nfail() { printf 'REJECTED:%s\\n' \"$1\"; exit 1; }\nbounded() { \"$@\"; }\n"+body)
        return invoke(["/usr/bin/bash", str(script), str(self.module), *map(str,args)])

    def test_actual_exclusive_nofollow_creation(self):
        leaf=self.folder / "scratch"
        self.assertEqual(0,self.function_call('batch_create_scratch_leaf "$2"\n',leaf)[0])
        for kind in ("regular","symlink","fifo"):
            if leaf.exists() or leaf.is_symlink(): leaf.unlink()
            if kind=="regular": leaf.write_bytes(b"sentinel")
            elif kind=="symlink": leaf.symlink_to(self.tree/"file")
            else: os.mkfifo(leaf)
            before=leaf.lstat()
            self.assertEqual(1,self.function_call('batch_create_scratch_leaf "$2"\n',leaf)[0])
            after=leaf.lstat()
            self.assertEqual((before.st_dev,before.st_ino,before.st_mode),(after.st_dev,after.st_ino,after.st_mode))
            if kind=="regular": self.assertEqual(b"sentinel",leaf.read_bytes())
            self.assertEqual(b"abc",(self.tree/"file").read_bytes())

    def test_actual_named_root_identity_and_ancestor_rejection(self):
        self.assertEqual(0,self.function_call('before=$(stat -c \'%d:%i:%f:%u:%g\' -- "$2"); batch_root_pin "$2" "$before"\n',self.tree)[0])
        self.assertEqual(1,self.function_call('before=$(stat -c \'%d:%i:%f:%u:%g\' -- "$2"); mv "$2" "$2-old"; mkdir "$2"; batch_root_pin "$2" "$before"\n',self.tree)[0])
        self.tree.rmdir(); self.tree.with_name("tree-old").rename(self.tree)
        alias=self.folder/"alias"; alias.symlink_to(self.tree,target_is_directory=True)
        status,(out,error)=self.function_call('before=$(stat -c \'%d:%i:%f:%u:%g\' -- "$2"); batch_root_pin "$3" "$before"\n',self.tree/"empty",alias/"empty")
        self.assertEqual(1,status)
        self.assertEqual(b"REJECTED:deployment-symlink\n",out)
        self.assertEqual(b"",error)

    def test_actual_strict_checksum_success_and_failure(self):
        check = self.folder / "checks"
        digest = hashlib.sha256(b"abc").hexdigest()
        check.write_text(digest+"  "+str(self.tree/"file")+"\n")
        self.assertEqual(0, invoke(["/usr/bin/sha256sum", "--check", "--strict", "--status", "--", str(check)])[0])
        (self.tree / "file").write_bytes(b"abd")
        self.assertEqual(1, invoke(["/usr/bin/sha256sum", "--check", "--strict", "--status", "--", str(check)])[0])

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--sha256", required=True)
    args = parser.parse_args()
    raw = args.fixture.read_bytes()
    if hashlib.sha256(raw).hexdigest() != args.sha256:
        raise SystemExit("parser-fixture-pin")
    text = raw.decode()
    LEXICAL = text[text.index("lexical() {"):text.index("sha() {")]
    BLOCK = text[text.index("batch_check_time() {"):text.index("# OS aliases ONLY:")]
    END = time.monotonic()+45
    unittest.main(argv=[__file__, "-v"])
