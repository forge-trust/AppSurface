"""Defined real-FD data controls; fixture proc/cgroup bytes confer no kernel authority."""
import copy
import errno
import importlib.util
import json
import os
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    "private_collector_sample_data",
    Path(__file__).resolve().parents[2] / "scripts/evidencehost_private_collector_sample.py")
module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(module)


class Clock:
    def __init__(self):
        self.now = 10.0

    def __call__(self):
        return self.now


class CollectorSampleDataControls(unittest.TestCase):
    def fixture(self, unit="evidencehost-0123456789ab-s-0.service"):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name).resolve()
        uid, gid = os.geteuid(), os.getegid()
        # Only newly created owned fixture directories are normalized. These
        # owner overrides are ordinary data, never a simulated root capability.
        os.chown(root, -1, gid)
        root.chmod(0o700)
        proc, cgroup, sdk = root / "proc", root / "cgroup", root / "sdk"
        group = cgroup / "system.slice" / unit
        for directory in (proc, cgroup, sdk, group.parent, group):
            directory.mkdir(mode=0o700)
            os.chown(directory, -1, gid)
            directory.chmod(0o700)
        host = sdk / "dotnet"
        host.write_bytes(b"fixture filename only; never executed")
        host.chmod(0o600)
        f = {"root": root, "proc": proc, "cgroup": cgroup, "group": group,
             "sdk": sdk, "host": host, "unit": unit,
             "uid": uid or 1, "gid": gid or 1, "root_uid": uid, "root_gid": gid,
             "clock": Clock()}
        for name, data in {"cgroup.procs": b"301\n", "pids.current": b"1\n",
                           "pids.max": b"128\n", "pids.events": b"max 0\n",
                           "memory.current": b"4096\n", "memory.max": b"max\n",
                           "memory.events": b"low 0\nhigh 2\nmax 3\noom 0\noom_kill 0\n"}.items():
            self.write(group / name, data, uid, gid)
        self.process(f, 301)
        return f

    def write(self, path, data, uid, gid):
        path.write_bytes(data)
        os.chown(path, uid, gid)
        path.chmod(0o600)
        return path

    def process(self, f, pid, role="datacollector"):
        directory = f["proc"] / str(pid)
        directory.mkdir(mode=0o700)
        os.chown(directory, f["uid"], f["gid"])
        directory.chmod(0o700)
        start = str(pid + 1000).encode()
        files = {"stat": str(pid).encode() + b" (private-comm-canary) S " + b"0 "*18 + start + b"\n",
                 "status": ("Uid: " + " ".join([str(f["uid"])]*4) + "\nGid: "
                            + " ".join([str(f["gid"])]*4) + "\nGroups: " + str(f["gid"]) + "\n").encode(),
                 "cgroup": ("0::/system.slice/" + f["unit"] + "\n").encode()}
        command = [str(f["host"]), "exec", str(f["sdk"]) + "/sdk/10.0.401/" + role + ".dll",
                   "private-command-canary"]
        files["cmdline"] = b"\0".join(part.encode() for part in command) + b"\0"
        for name, data in files.items():
            self.write(directory / name, data, f["uid"], f["gid"])
        (directory / "exe").symlink_to(f["host"])
        return directory

    def sampler(self, f, deadline=100.0, **overrides):
        arguments = {"expected_root_uid": f["root_uid"], "expected_root_gid": f["root_gid"],
                     "proc_root": str(f["proc"]), "cgroup_root": str(f["cgroup"])}
        arguments.update(overrides)
        return module.CollectorStartupSampler(f["unit"], "/system.slice/" + f["unit"],
            f["uid"], f["gid"], str(f["host"]), str(f["sdk"]), deadline, **arguments)

    def snapshot(self, f, sampler=None):
        sampler = sampler or self.sampler(f)
        with patch.object(module.time, "monotonic", f["clock"]):
            self.assertIsNone(sampler.observe())
            raw = sampler.finish()
        self.assertIsNotNone(raw)
        self.assertTrue(module.validate_snapshot(raw))
        return raw, json.loads(raw)

    def assert_observation_fds_closed(self, operation):
        opened = []
        actual_open = os.open
        def open_file(*args, **kwargs):
            fd = actual_open(*args, **kwargs)
            opened.append(fd)
            return fd
        with patch.object(module.os, "open", side_effect=open_file):
            operation()
        self.assertTrue(opened)
        for fd in set(opened):
            with self.assertRaises(OSError) as caught:
                os.fstat(fd)
            self.assertEqual(errno.EBADF, caught.exception.errno)

    def test_valid_real_fd_snapshot_numeric_null_events_and_no_private_bytes(self):
        f = self.fixture()
        sampler = self.sampler(f)
        with patch.object(module.time, "monotonic", f["clock"]):
            self.assert_observation_fds_closed(sampler.observe)
            raw = sampler.finish()
        self.assertTrue(module.validate_snapshot(raw))
        data = json.loads(raw)
        self.assertEqual((True, False, False, False, 1),
            (data["collector_observed"], data["capture_incomplete"], data["network_observed"],
             data["authority"], data["sample_count"]))
        sample = data["samples"][0]
        self.assertEqual((0, 0), (sample["elapsed_ms"], sample["unavailable_pids"]))
        self.assertEqual({"pid": 301, "start_time": 1301, "uid": [f["uid"]]*4,
                          "gid": [f["gid"]]*4, "groups": [f["gid"]], "role": "datacollector"},
                         sample["processes"][0])
        self.assertEqual({"pids_current": 1, "pids_max": {"value": 128, "unbounded": False},
                          "pids_events": {"max": 0}, "memory_current": 4096,
                          "memory_max": {"value": None, "unbounded": True},
                          "memory_events": {"low": 0, "high": 2, "max": 3, "oom": 0,
                                            "oom_kill": 0, "oom_group_kill": None}}, sample["counters"])
        for canary in (b"private-comm-canary", b"private-command-canary", str(f["root"]).encode()):
            self.assertNotIn(canary, raw)

    def test_counter_missing_malformed_duplicate_and_unknown_values_are_null(self):
        for name, bad in (("pids.current", None), ("memory.current", b"-1\n"),
                          ("pids.max", b"true\n"), ("memory.max", b"9223372036854775808\n"),
                          ("pids.events", b"max 0\nmax 1\n"),
                          ("memory.events", b"unknown-private-canary 1\n")):
            with self.subTest(counter=name):
                f = self.fixture()
                _, good = self.snapshot(f)
                self.assertIsNotNone(good["samples"][0]["counters"][name.replace(".", "_")])
                path = f["group"] / name
                if bad is None: path.unlink()
                else: path.write_bytes(bad)
                raw, data = self.snapshot(f)
                self.assertIsNone(data["samples"][0]["counters"][name.replace(".", "_")])
                self.assertNotIn(b"unknown-private-canary", raw)

    def test_foreign_uid_gid_cgroup_and_duplicate_status_never_trigger_collector(self):
        for field in ("uid", "gid", "cgroup", "duplicate-status"):
            with self.subTest(field=field):
                f = self.fixture()
                self.snapshot(f)
                directory = f["proc"] / "301"
                if field == "cgroup":
                    (directory / "cgroup").write_bytes(b"0::/system.slice/unselected.service\n")
                else:
                    path = directory / "status"
                    raw = path.read_bytes()
                    if field == "duplicate-status": raw += b"Uid: 1 1 1 1\n"
                    else:
                        key = b"Uid" if field == "uid" else b"Gid"
                        foreign = str(f[field] + 1).encode()
                        raw = b"\n".join(key + b": " + b" ".join([foreign]*4) if line.startswith(key+b":") else line
                                         for line in raw.splitlines()) + b"\n"
                    path.write_bytes(raw)
                sampler = self.sampler(f)
                with patch.object(module.time, "monotonic", f["clock"]):
                    self.assert_observation_fds_closed(sampler.observe)
                    self.assertIsNone(sampler.finish())
                self.process(f, 302)
                (f["group"] / "cgroup.procs").write_bytes(b"301\n302\n")
                _, data = self.snapshot(f)
                self.assertEqual(1, data["samples"][0]["unavailable_pids"])
                self.assertEqual([302], [p["pid"] for p in data["samples"][0]["processes"]])

    def test_role_requires_exact_selected_executable_and_sdk_filename(self):
        for mismatch in ("executable", "filename", "argv-zero"):
            with self.subTest(mismatch=mismatch):
                f = self.fixture()
                self.snapshot(f)
                directory = f["proc"] / "301"
                if mismatch == "executable":
                    (directory / "exe").unlink()
                    (directory / "exe").symlink_to(f["sdk"] / "foreign-dotnet")
                else:
                    command = (directory / "cmdline").read_bytes()
                    if mismatch == "filename": command = command.replace(b"datacollector.dll", b"datacollector-private-canary.dll")
                    else: command = command.replace(str(f["host"]).encode(), b"foreign-host", 1)
                    (directory / "cmdline").write_bytes(command)
                sampler = self.sampler(f)
                with patch.object(module.time, "monotonic", f["clock"]):
                    sampler.observe()
                    self.assertIsNone(sampler.finish())
                self.process(f, 302)
                (f["group"] / "cgroup.procs").write_bytes(b"301\n302\n")
                raw, data = self.snapshot(f)
                expected = "dotnet" if mismatch == "filename" else "unknown"
                self.assertEqual(expected, data["samples"][0]["processes"][0]["role"])
                self.assertNotIn(b"private-canary", raw)

    def test_start_time_and_named_metadata_replacement_during_read_reject_process(self):
        actual_read = os.read
        for shape in ("start-time", "replacement"):
            with self.subTest(shape=shape):
                f = self.fixture()
                self.snapshot(f)
                directory = f["proc"] / "301"
                selected = directory / ("cmdline" if shape == "start-time" else "status")
                inode = selected.stat().st_ino
                changed = []
                def read(fd, amount):
                    block = actual_read(fd, amount)
                    if block and os.fstat(fd).st_ino == inode and not changed:
                        changed.append(True)
                        if shape == "start-time":
                            path = directory / "stat"
                            path.write_bytes(path.read_bytes().replace(b"1301\n", b"1302\n"))
                        else:
                            replacement = self.write(directory / "replacement", selected.read_bytes(), f["uid"], f["gid"])
                            replacement.replace(selected)
                    return block
                sampler = self.sampler(f)
                with patch.object(module.time, "monotonic", f["clock"]), patch.object(module.os, "read", side_effect=read):
                    self.assert_observation_fds_closed(sampler.observe)
                    self.assertIsNone(sampler.finish())
                self.assertEqual([True], changed)

    def test_process_link_fifo_and_byte_bounds_reject_without_blocking_or_raw_echo(self):
        for shape in ("symlink", "hardlink", "fifo", "byte-bound", "writable-mode"):
            with self.subTest(shape=shape):
                f = self.fixture()
                self.snapshot(f)
                path = f["proc"] / "301" / "status"
                original = path.read_bytes()
                if shape == "byte-bound": path.write_bytes(b"x"*8193)
                elif shape == "writable-mode": path.chmod(0o622)
                else:
                    path.unlink()
                    if shape == "fifo": os.mkfifo(path, 0o600)
                    else:
                        outside = self.write(f["root"] / "outside-status", original, f["uid"], f["gid"])
                        if shape == "symlink": path.symlink_to(outside)
                        else: os.link(outside, path)
                sampler = self.sampler(f)
                with patch.object(module.time, "monotonic", f["clock"]):
                    self.assert_observation_fds_closed(sampler.observe)
                    self.assertIsNone(sampler.finish())

    def test_counter_link_fifo_and_exact_byte_bound_remain_unavailable(self):
        for shape in ("symlink", "hardlink", "fifo", "oversize"):
            with self.subTest(shape=shape):
                f = self.fixture()
                _, good = self.snapshot(f)
                self.assertEqual(1, good["samples"][0]["counters"]["pids_current"])
                path = f["group"] / "pids.current"
                if shape == "oversize": path.write_bytes(b"1"*4097)
                else:
                    path.unlink()
                    if shape == "fifo": os.mkfifo(path, 0o600)
                    else:
                        outside = self.write(f["root"] / "outside-counter", b"1\n", f["root_uid"], f["root_gid"])
                        if shape == "symlink": path.symlink_to(outside)
                        else: os.link(outside, path)
                _, data = self.snapshot(f)
                self.assertIsNone(data["samples"][0]["counters"]["pids_current"])

    def test_pid_maximum_adjacent_overflow_duplicate_and_zero_have_no_extra_process_read(self):
        f = self.fixture()
        for pid in range(302, 317): self.process(f, pid)
        good = b"".join(str(pid).encode()+b"\n" for pid in range(301, 317))
        (f["group"] / "cgroup.procs").write_bytes(good)
        _, data = self.snapshot(f)
        self.assertEqual(16, len(data["samples"][0]["processes"]))
        for bad in (good+b"317\n", good+b"301\n", b"0\n", b"2147483648\n"):
            with self.subTest(pids=bad):
                (f["group"] / "cgroup.procs").write_bytes(bad)
                sampler = self.sampler(f)
                with patch.object(module.time, "monotonic", f["clock"]), patch.object(module.os, "readlink", wraps=os.readlink) as links:
                    sampler.observe()
                    self.assertIsNone(sampler.finish())
                self.assertEqual(0, links.call_count)

    def test_sample_limit_and_interval_leave_exactly_sixteen_bounded_samples(self):
        f = self.fixture()
        sampler = self.sampler(f)
        with patch.object(module.time, "monotonic", f["clock"]):
            for _ in range(16):
                sampler.observe()
                sampler.observe()  # Not due: must not append a second sample.
                f["clock"].now += module.MIN_INTERVAL
            with patch.object(module.os, "open", side_effect=AssertionError("unexpected read")):
                sampler.observe()
            raw = sampler.finish()
        self.assertTrue(module.validate_snapshot(raw))
        self.assertEqual(16, json.loads(raw)["sample_count"])

    def test_active_window_five_seconds_stops_reads_and_retains_earlier_sample(self):
        f = self.fixture()
        sampler = self.sampler(f)
        with patch.object(module.time, "monotonic", f["clock"]):
            sampler.observe()
            f["clock"].now += 5.0
            with patch.object(module.os, "open", side_effect=AssertionError("unexpected read")):
                sampler.observe()
            raw = sampler.finish()
        data = json.loads(raw)
        self.assertEqual(1, data["sample_count"])
        self.assertFalse(data["capture_incomplete"])

    def test_observation_tenth_second_and_job_deadline_bound_actual_fd_reads(self):
        actual_read = os.read
        for allowance in (0.1, 0.05):
            with self.subTest(allowance=allowance):
                f = self.fixture()
                sampler = self.sampler(f, deadline=100 if allowance == 0.1 else f["clock"].now + allowance)
                read_calls = []
                def read(fd, amount):
                    result = actual_read(fd, amount)
                    read_calls.append(True)
                    f["clock"].now += allowance + 0.001
                    return result
                with patch.object(module.time, "monotonic", f["clock"]), patch.object(module.os, "read", side_effect=read):
                    self.assert_observation_fds_closed(sampler.observe)
                    self.assertIsNone(sampler.finish())
                self.assertEqual(1, len(read_calls))

    def test_finish_after_job_deadline_or_without_collector_never_returns_snapshot(self):
        f = self.fixture()
        sampler = self.sampler(f, deadline=11)
        with patch.object(module.time, "monotonic", f["clock"]):
            sampler.observe()
            f["clock"].now = 11
            self.assertIsNone(sampler.finish())
        f = self.fixture()
        (f["group"] / "cgroup.procs").write_bytes(b"")
        with patch.object(module.time, "monotonic", f["clock"]):
            sampler = self.sampler(f)
            sampler.observe()
            self.assertIsNone(sampler.finish())

    def test_late_failed_observer_preserves_prior_capture_and_finish_consumes_once(self):
        f = self.fixture()
        sampler = self.sampler(f)
        with patch.object(module.time, "monotonic", f["clock"]):
            sampler.observe()
            (f["group"] / "cgroup.procs").write_bytes(b"invalid-private-canary\n")
            f["clock"].now += module.MIN_INTERVAL
            sampler.observe()
            raw = sampler.finish()
            self.assertIsNone(sampler.finish())
            with patch.object(module.os, "open", side_effect=AssertionError("unexpected read")):
                self.assertIsNone(sampler.observe())
        self.assertTrue(module.validate_snapshot(raw))
        data = json.loads(raw)
        self.assertEqual(1, data["sample_count"])
        self.assertTrue(data["capture_incomplete"])
        self.assertNotIn(b"invalid-private-canary", raw)

    def test_unit_grammar_valid_boundaries_and_invalid_selections_fail_without_io(self):
        for suffix in (0, 127):
            self.snapshot(self.fixture(f"evidencehost-0123456789ab-s-{suffix}.service"))
        f = self.fixture()
        for unit in ("evidencehost-0123456789ab-s-128.service", "evidencehost-0123456789ab-s-01.service",
                     "evidencehost-0123456789AB-s-0.service", "unselected.service", None):
            with self.subTest(unit=unit), patch.object(module.os, "open", side_effect=AssertionError("unexpected read")):
                sampler = module.CollectorStartupSampler(unit, "/system.slice/" + str(unit),
                    f["uid"], f["gid"], str(f["host"]), str(f["sdk"]), 100)
                sampler.observe()
                self.assertIsNone(sampler.finish())

    def test_owner_mismatch_and_default_root_selection_do_not_grant_fixture_origin(self):
        f = self.fixture()
        self.snapshot(f)
        for override in ({"expected_root_uid": f["root_uid"]+1}, {"expected_root_gid": f["root_gid"]+1}):
            sampler = self.sampler(f, **override)
            with patch.object(module.time, "monotonic", f["clock"]):
                with patch.object(module.os, "open", wraps=os.open) as opened:
                    sampler.observe()
                    self.assertIsNone(sampler.finish())
                self.assertEqual(0, opened.call_count)
        # Default root identity is never overridden in this call. On a root
        # test process these are actually root-owned fixture bytes, still data.
        sampler = module.CollectorStartupSampler(f["unit"], "/system.slice/"+f["unit"],
            f["uid"], f["gid"], str(f["host"]), str(f["sdk"]), 100,
            proc_root=str(f["proc"]), cgroup_root=str(f["cgroup"]))
        with patch.object(module.time, "monotonic", f["clock"]):
            sampler.observe()
            raw = sampler.finish()
        if f["root_uid"] == f["root_gid"] == 0:
            self.assertTrue(module.validate_snapshot(raw))
        else:
            self.assertIsNone(raw)

    def test_directory_child_churn_preserves_pinned_identity_and_readable_snapshot(self):
        actual_read = os.read
        for shape in ('proc-root', 'proc-pid', 'system-slice', 'selected-group'):
            with self.subTest(shape=shape):
                f = self.fixture()
                sampler = self.sampler(f)
                watched = (f['proc']/'301'/'cmdline') if shape == 'proc-pid' else (f['group']/'pids.current')
                watched_inode = watched.stat().st_ino
                parent = {'proc-root': f['proc'], 'proc-pid': f['proc']/'301',
                          'system-slice': f['group'].parent, 'selected-group': f['group']}[shape]
                changed = False

                def churn(fd, count):
                    nonlocal changed
                    data = actual_read(fd, count)
                    if data and not changed and os.fstat(fd).st_ino == watched_inode:
                        changed = True
                        (parent/'unrelated-owned-child').mkdir(mode=0o700)
                    return data

                with patch.object(module.time, 'monotonic', f['clock']), patch.object(module.os, 'read', side_effect=churn):
                    self.assert_observation_fds_closed(sampler.observe)
                    raw = sampler.finish()
                self.assertTrue(changed)
                self.assertTrue(module.validate_snapshot(raw))
                data = json.loads(raw)
                self.assertFalse(data['capture_incomplete'])
                self.assertEqual(1, data['sample_count'])

    def test_size_bound_discards_only_last_sample_and_preserves_prior_incomplete_data(self):
        f = self.fixture()
        pids = [301] + list(range(2147483633, 2147483648))
        for pid in pids[1:]:
            self.process(f, pid)
        (f['group']/'cgroup.procs').write_bytes(b''.join(str(pid).encode()+b'\n' for pid in pids))
        for pid in pids:
            directory = f['proc']/str(pid)
            status = ('Uid: ' + ' '.join([str(f['uid'])]*4) + '\nGid: '
                      + ' '.join([str(f['gid'])]*4) + '\nGroups: '
                      + ' '.join(str(value) for value in range(4294967280, 4294967296)) + '\n').encode()
            (directory/'status').write_bytes(status)
            (directory/'stat').write_bytes(str(pid).encode() + b' (fixture) S ' + b'0 '*18
                                          + str(module.MAX_NUMBER).encode() + b'\n')
        sampler = self.sampler(f)
        with patch.object(module.time, 'monotonic', f['clock']):
            for _ in range(module.MAX_SAMPLES):
                sampler.observe()
                f['clock'].now += 0.331
            raw = sampler.finish()
        self.assertIsNotNone(raw)
        self.assertLessEqual(len(raw), module.MAX_OUTPUT)
        self.assertTrue(module.validate_snapshot(raw))
        data = json.loads(raw)
        self.assertTrue(data['capture_incomplete'])
        self.assertGreater(data['sample_count'], 0)
        self.assertLess(data['sample_count'], module.MAX_SAMPLES)
        self.assertTrue(all(len(sample['processes']) == 16 for sample in data['samples']))

    def test_closed_canonical_schema_types_duplicates_and_canary_fields_reject(self):
        f = self.fixture()
        raw, original = self.snapshot(f)
        self.assertTrue(module.validate_snapshot(raw))
        mutations = []
        for key, value in (("authority", True), ("network_observed", True), ("collector_observed", False),
                           ("capture_incomplete", None), ("sample_count", True), ("schema", "private-canary"),
                           ("unit", "unknown"), ("extra-private-canary", 0)):
            changed = copy.deepcopy(original); changed[key] = value; mutations.append(changed)
        changed = copy.deepcopy(original); changed["samples"][0]["processes"][0]["role"] = "unknown-private-canary"; mutations.append(changed)
        changed = copy.deepcopy(original); changed["samples"][0]["counters"]["private-canary"] = 1; mutations.append(changed)
        changed = copy.deepcopy(original); changed["samples"][0]["counters"]["pids_current"] = True; mutations.append(changed)
        changed = copy.deepcopy(original); changed["samples"][0]["processes"][0]["uid"][0] = f["uid"]+1; mutations.append(changed)
        for changed in mutations:
            candidate = (json.dumps(changed, sort_keys=True, separators=(",", ":"))+"\n").encode()
            self.assertFalse(module.validate_snapshot(candidate))
        duplicate = raw.replace(b'"authority":false', b'"authority":false,"authority":false', 1)
        self.assertNotEqual(raw, duplicate)
        for candidate in (raw[:-1], duplicate,
                          b"private-canary", b"x"*65537, raw.decode(), None):
            self.assertFalse(module.validate_snapshot(candidate))


if __name__ == "__main__":
    unittest.main()
