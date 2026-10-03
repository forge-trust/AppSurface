"""Portable private account/template controls; no root, systemd, lease or qualification proof."""
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("private_qualification_launcher", ROOT / "scripts/evidencehost-linux-launcher.py")
launcher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(launcher)


class PrivateRootBindingsControls(unittest.TestCase):
    """Inspect actual emitted utility argv through a mocked NSS/utility boundary only."""
    def setUp(self):
        self.accounts, self.groups, self.commands = {}, {}, []
        self.users_owned, self.groups_owned = [], []
        self.fail_at = None
        self.wrong_group = self.wrong_user = self.missing_group = None
        self.addCleanup(patch.stopall)
        patch.object(launcher.pwd, "getpwnam", side_effect=self.user_name).start()
        patch.object(launcher.pwd, "getpwuid", side_effect=self.user_id).start()
        patch.object(launcher.grp, "getgrnam", side_effect=self.group_name).start()
        patch.object(launcher.grp, "getgrgid", side_effect=self.group_id).start()
        patch.object(launcher.subprocess, "run", side_effect=self.utility).start()

    def user_name(self, name):
        if name not in self.accounts:
            raise KeyError("private-NSS-canary")
        uid, gid = self.accounts[name]
        return SimpleNamespace(pw_name=name, pw_uid=uid, pw_gid=gid + (name == self.wrong_user))

    def user_id(self, uid):
        for name, pair in self.accounts.items():
            if pair[0] == uid:
                return self.user_name(name)
        raise KeyError("private-NSS-canary")

    def group_name(self, name):
        if name not in self.groups or name == self.missing_group:
            raise KeyError("private-NSS-canary")
        return SimpleNamespace(gr_name=name, gr_gid=self.groups[name] + (name == self.wrong_group))

    def group_id(self, gid):
        for name, actual in self.groups.items():
            if actual == gid:
                return self.group_name(name)
        raise KeyError("private-NSS-canary")

    def utility(self, argv, **kwargs):
        self.commands.append(tuple(argv))
        self.assertEqual(launcher.ENV, kwargs["env"])
        self.assertGreater(kwargs["timeout"], 0)
        self.assertNotIn("--non-unique", argv)
        self.assertNotIn("--force", argv)
        if len(self.commands) == self.fail_at:
            raise OSError(5, "private-utility-canary")
        name, code = argv[-1], 0
        if argv[0] == "/usr/sbin/groupadd":
            self.assertIn(name, self.groups_owned)
            gid = int(argv[argv.index("--gid") + 1])
            if name in self.groups or gid in self.groups.values():
                code = 4
            else:
                self.groups[name] = gid
        elif argv[0] == "/usr/sbin/useradd":
            self.assertIn(name, self.users_owned)
            uid, gid = int(argv[argv.index("--uid") + 1]), int(argv[argv.index("--gid") + 1])
            self.assertIn("--no-user-group", argv)
            self.assertIn(gid, self.groups.values())
            if name in self.accounts or uid in [pair[0] for pair in self.accounts.values()]:
                code = 4
            else:
                self.accounts[name] = (uid, gid)
        elif argv[0] == "/usr/sbin/userdel":
            if name not in self.accounts:
                code = 6
            else:
                self.accounts.pop(name)
                # Model USERGROUPS_ENAB=yes: same-name primary groups may disappear.
                self.groups.pop(name, None)
        elif argv[0] == "/usr/sbin/groupdel":
            if name not in self.groups:
                code = 6
            else:
                self.assertNotIn(self.groups[name], [pair[1] for pair in self.accounts.values()])
                del self.groups[name]
        else:
            self.fail("Unexpected host command")
        return launcher.subprocess.CompletedProcess(argv, code, b"", b"private-utility-canary")

    def create_run(self):
        launcher._create_run_accounts("evw012345abcdef", "evs012345abcdef", "evr012345abcdef",
                                      self.users_owned, self.groups_owned)

    def create_application(self):
        launcher._create_application_accounts("eva012345abcdef", "evh012345abcdef",
                                              self.users_owned, self.groups_owned)

    def cleanup(self):
        launcher._delete_run_accounts(self.users_owned, self.groups_owned, strict=True)

    def test_exact_actual_creation_argv_and_reverse_cleanup_reserve_all_eight_identities(self):
        self.create_run(); self.create_application()
        self.assertEqual({"evw012345abcdef": (65010, 65011), "evs012345abcdef": (65012, 65013),
                          "eva012345abcdef": (65014, 65015)}, self.accounts)
        self.assertEqual({"evw012345abcdefg": 65011, "evs012345abcdefg": 65013,
                          "evr012345abcdef": 65016, "eva012345abcdefg": 65015,
                          "evh012345abcdef": 65017}, self.groups)
        expected = []
        for name, gid in (("evw012345abcdefg", 65011), ("evs012345abcdefg", 65013), ("evr012345abcdef", 65016)):
            expected.append(("/usr/sbin/groupadd", "--system", "--gid", str(gid), name))
        for name, uid, gid in (("evw012345abcdef", 65010, 65011), ("evs012345abcdef", 65012, 65013)):
            expected.append(("/usr/sbin/useradd", "--system", "--uid", str(uid), "--gid", str(gid),
                             "--no-user-group", "--no-create-home", "--shell", "/usr/sbin/nologin", name))
        for name, gid in (("eva012345abcdefg", 65015), ("evh012345abcdef", 65017)):
            expected.append(("/usr/sbin/groupadd", "--system", "--gid", str(gid), name))
        expected.append(("/usr/sbin/useradd", "--system", "--uid", "65014", "--gid", "65015",
                         "--no-user-group", "--no-create-home", "--shell", "/usr/sbin/nologin", "eva012345abcdef"))
        self.assertEqual(expected, self.commands)
        owned = (list(self.users_owned), list(self.groups_owned))
        self.cleanup()
        self.assertEqual([("/usr/sbin/userdel", name) for name in reversed(owned[0])]
                         + [("/usr/sbin/groupdel", name) for name in reversed(owned[1])], self.commands[8:])
        self.assertEqual(({}, {}), (self.accounts, self.groups))
        self.assertEqual(owned, (self.users_owned, self.groups_owned))

    def test_reserved_worker_primary_gid_binds_emitted_unit_properties_without_other_policy_changes(self):
        # This connects real creator argv/NSS metadata to the actual property
        # helper, not a root process, authenticated peer or protected lease.
        self.create_run()
        worker_name = "evw012345abcdef"
        worker = launcher.pwd.getpwnam(worker_name)
        self.assertEqual((65010, 65011), (worker.pw_uid, worker.pw_gid))
        self.assertEqual(worker_name + "g", launcher.grp.getgrgid(worker.pw_gid).gr_name)
        self.assertNotIn(worker_name, self.groups)
        self.assertIn(worker_name + "g", self.groups_owned)
        before = list(self.commands)
        args = (worker_name, Path("/protected-tools"), Path("/subject-source"),
                Path("/private-results"), Path("/protected-output"), 900)
        metadata_default = launcher.worker_unit_properties(*args)
        properties = launcher.worker_unit_properties(*args, worker_gid=worker.pw_gid)
        self.assertEqual({**metadata_default, "Group": "65011"}, properties)
        self.assertEqual(worker_name, properties["User"])
        self.assertEqual(worker.pw_gid, int(properties["Group"]))
        self.assertEqual("--property=Group=65011", f"--property=Group={properties['Group']}")
        self.assertNotEqual(worker_name, properties["Group"])
        for wrong_gid in (65013, 0, True, "65011"):
            with self.subTest(wrong_gid=wrong_gid):
                with self.assertRaises(launcher.LauncherError) as failed:
                    launcher.worker_unit_properties(*args, worker_gid=wrong_gid)
                self.assertEqual("identity-separation-failed", str(failed.exception))
        self.assertEqual(before, self.commands)
        self.cleanup()
        self.assertEqual(({}, {}), (self.accounts, self.groups))

    def test_each_selected_uid_occupied_rejects_before_any_utility_or_owned_reservation(self):
        for uid in (65010, 65012, 65014):
            with self.subTest(uid=uid):
                self.accounts = {"external": (uid, 70000)}
                with self.assertRaises(launcher.LauncherError): self.create_run()
                self.assertEqual([], self.commands)
                self.assertEqual(([], []), (self.users_owned, self.groups_owned))
                self.assertEqual({"external": (uid, 70000)}, self.accounts)

    def test_each_selected_gid_occupied_rejects_before_any_utility_or_owned_reservation(self):
        for gid in (65011, 65013, 65015, 65016, 65017):
            with self.subTest(gid=gid):
                self.groups = {"external": gid}
                with self.assertRaises(launcher.LauncherError): self.create_run()
                self.assertEqual([], self.commands)
                self.assertEqual(([], []), (self.users_owned, self.groups_owned))
                self.assertEqual({"external": gid}, self.groups)

    def test_fresh_selected_names_must_also_be_unoccupied(self):
        for name in ("evw012345abcdef", "evs012345abcdef", "evr012345abcdef"):
            with self.subTest(name=name):
                self.groups = {name: 70000}
                with self.assertRaises(launcher.LauncherError): self.create_run()
                self.assertEqual([], self.commands)
                self.assertEqual(([], []), (self.users_owned, self.groups_owned))

    def test_new_application_occupancy_preserves_existing_run_accounts_and_never_owns_foreign_ids(self):
        self.create_run()
        before = (list(self.commands), list(self.users_owned), list(self.groups_owned))
        for kind, identifier in (("user", 65014), ("group", 65015), ("group", 65017)):
            with self.subTest(kind=kind, identifier=identifier):
                if kind == "user": self.accounts["external"] = (identifier, 70000)
                else: self.groups["external"] = identifier
                with self.assertRaises(launcher.LauncherError): self.create_application()
                self.assertEqual(before, (self.commands, self.users_owned, self.groups_owned))
                self.accounts.pop("external", None); self.groups.pop("external", None)

    def test_each_precreation_failure_retains_pending_name_and_missing_cleanup_fails_closed(self):
        for failure_at in range(1, 9):
            with self.subTest(failure_at=failure_at):
                self.accounts, self.groups, self.commands = {}, {}, []
                self.users_owned, self.groups_owned = [], []
                self.fail_at = failure_at
                with self.assertRaises(launcher.LauncherError) as failed:
                    self.create_run(); self.create_application()
                self.assertNotIn("private-utility-canary", str(failed.exception))
                command = self.commands[-1]
                pending = self.groups_owned if command[0] == "/usr/sbin/groupadd" else self.users_owned
                self.assertEqual(command[-1], pending[-1])
                self.assertNotIn(command[-1], self.groups if command[0] == "/usr/sbin/groupadd" else self.accounts)
                self.assertTrue(set(self.accounts).issubset(self.users_owned))
                self.assertTrue(set(self.groups).issubset(self.groups_owned))
                owned = (list(self.users_owned), list(self.groups_owned))
                self.fail_at = None
                with self.assertRaises(launcher.LauncherError): self.cleanup()
                self.assertEqual(owned, (self.users_owned, self.groups_owned))

    def assert_mutation_then_failure(self, application, user):
        for error_kind in ("launcher", "timeout"):
            with self.subTest(application=application, user=user, error_kind=error_kind):
                self.accounts, self.groups, self.commands = {}, {}, []
                self.users_owned, self.groups_owned = [], []
                if application: self.create_run()
                name = ("eva012345abcdef" if application else "evw012345abcdef") + ("" if user else "g")
                utility_path = "/usr/sbin/useradd" if user else "/usr/sbin/groupadd"
                failure = (launcher.LauncherError("systemd-operation-failed") if error_kind == "launcher"
                           else launcher.subprocess.TimeoutExpired(utility_path, 5))
                def utility(argv, **kwargs):
                    # The actual creator must have recorded ownership before
                    # entering the utility, not merely when observing NSS later.
                    if argv[0] == utility_path and argv[-1] == name:
                        self.assertIn(name, self.users_owned if user else self.groups_owned)
                    result = self.utility(argv, **kwargs)
                    if argv[0] == utility_path and argv[-1] == name:
                        actual = self.accounts if user else self.groups
                        self.assertIn(name, actual)
                        raise failure
                    return result
                with patch.object(launcher.subprocess, "run", side_effect=utility), \
                     patch.object(launcher.subprocess, "Popen") as process, \
                     patch.object(launcher._application, "RootApplicationLease") as application_lease:
                    with self.assertRaises(type(failure)) as failed:
                        if application: self.create_application()
                        else: self.create_run()
                    self.assertIs(failure, failed.exception)
                    process.assert_not_called()
                    application_lease.assert_not_called()
                self.assertEqual(set(self.accounts), set(self.users_owned))
                self.assertEqual(set(self.groups), set(self.groups_owned))
                owned = (list(self.users_owned), list(self.groups_owned))
                before_cleanup = len(self.commands)
                self.cleanup()
                expected = [("/usr/sbin/userdel", item) for item in reversed(owned[0])]
                expected += [("/usr/sbin/groupdel", item) for item in reversed(owned[1])]
                self.assertEqual(expected, self.commands[before_cleanup:])
                self.assertIn(("/usr/sbin/userdel" if user else "/usr/sbin/groupdel", name), expected)
                self.assertEqual(({}, {}), (self.accounts, self.groups))
                self.assertEqual(owned, (self.users_owned, self.groups_owned))

    def test_run_group_created_before_launcher_error_or_timeout_remains_owned(self):
        self.assert_mutation_then_failure(application=False, user=False)

    def test_run_user_created_before_launcher_error_or_timeout_remains_owned(self):
        self.assert_mutation_then_failure(application=False, user=True)

    def test_application_group_created_before_launcher_error_or_timeout_remains_owned(self):
        self.assert_mutation_then_failure(application=True, user=False)

    def test_application_user_created_before_launcher_error_or_timeout_remains_owned(self):
        self.assert_mutation_then_failure(application=True, user=True)

    def test_actual_group_mismatch_is_latched_only_after_reservation_is_recorded(self):
        self.wrong_group = "evw012345abcdefg"
        with self.assertRaises(launcher.LauncherError): self.create_run()
        self.assertEqual(["evw012345abcdefg"], self.groups_owned)
        self.assertEqual([], self.users_owned)
        self.wrong_group = None
        self.cleanup()
        self.assertEqual({}, self.groups)

    def test_actual_user_gid_mismatch_records_account_before_rejection_and_cleanup(self):
        self.wrong_user = "evw012345abcdef"
        with self.assertRaises(launcher.LauncherError): self.create_run()
        self.assertEqual(["evw012345abcdef"], self.users_owned)
        self.assertEqual(3, len(self.groups_owned))
        self.wrong_user = None
        self.cleanup()
        self.assertEqual(({}, {}), (self.accounts, self.groups))

    def test_missing_postcreation_lookup_keeps_owned_reservation_and_hides_lookup_canary(self):
        self.missing_group = "evw012345abcdefg"
        with self.assertRaises(launcher.LauncherError) as failed: self.create_run()
        self.assertEqual(["evw012345abcdefg"], self.groups_owned)
        self.assertNotIn("private-NSS-canary", str(failed.exception))
        self.assertEqual("identity-separation-failed", str(failed.exception))
        self.missing_group = None
        self.cleanup()

    def test_strict_cleanup_error_propagates_and_does_not_clear_reservation_ledger(self):
        self.create_run(); self.create_application()
        owned = (list(self.users_owned), list(self.groups_owned))
        self.fail_at = len(self.commands) + 1
        with self.assertRaises(launcher.LauncherError): self.cleanup()
        self.assertEqual(owned, (self.users_owned, self.groups_owned))
        self.assertEqual(3, len(self.accounts)); self.assertEqual(5, len(self.groups))
        self.fail_at = None
        self.cleanup()
        self.assertEqual(({}, {}), (self.accounts, self.groups))

    def test_generated_names_are_not_a_second_identity_selector_or_fallback(self):
        launcher._create_run_accounts("evwfedcba543210", "evsfedcba543210", "evrfedcba543210",
                                      self.users_owned, self.groups_owned)
        self.assertEqual((65010, 65011), self.accounts["evwfedcba543210"])
        self.assertEqual((65012, 65013), self.accounts["evsfedcba543210"])
        self.cleanup()

    def test_template_has_exact_buildtime_markers_one_immutable_registration_and_no_runtime_authority(self):
        text = Path(__file__).with_name("root-registration.py.in").read_text()
        for marker in ("canonicalEntryB64", "entryDigest", "catalogueDigest", "policyByteSHA"):
            self.assertEqual(1, text.count("{{" + marker + "}}"))
        self.assertEqual(1, text.count("    _CompiledRegistration("))
        self.assertIn("validate=True", text)
        for forbidden in ("os.environ", "getenv(", "open(", "json.loads", "setattr("):
            self.assertNotIn(forbidden, text)
        self.assertEqual([], self.commands)


if __name__ == "__main__":
    unittest.main()
