"""Pure build prerequisites shared by the image builder and native case readers.

This module performs no I/O and issues no native credit. All fourteen images
must carry actual passing source-selected core and CLI test results. Commands
are checked by their operation, project and selector, not an incidental count
of unrelated Git/package queries. Runtime case acceptance remains separate.
"""

import pathlib
import re

SCHEMA = "issue779-integrated16-build-v1"
SELECTORS = ("None", *(f"N{i:02d}" for i in range(4, 17)))
PIPE_METHOD = "OwnedRawPipeReadWrappingUsesHandleModeAndJoinsBothEofs"
FRAME_METHOD = "OwnerDeathVariantIsExclusiveAndFrameIsExactBoundedData"
CLI_METHOD = "CompiledImageOwnsOnlyItsSelectedCheckpoint"
CORE_PROJECT = "Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/ForgeTrust.AppSurface.Evidence.Supervision.Tests.csproj"
CLI_PROJECT = "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"
CLI_TEST_PROJECT = "Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj"
CORE_CLASS = "ForgeTrust.AppSurface.Evidence.Supervision.Tests."
CLI_CLASS = "ForgeTrust.AppSurface.Cli.Tests.EvidenceNativeQualificationTests"
SOURCE_PHASES = ("source_before", "source_after_assets", "source_after_build", "source_final")


class BuildContractError(ValueError):
    """Closed metadata rejection; never include caller values in its message."""


def require(ok, category):
    if not ok:
        raise BuildContractError(category)


def is_sha(value):
    return type(value) is str and re.fullmatch("[0-9a-f]{64}", value) is not None


def expected_regressions(selector):
    require(selector in SELECTORS, "build-selector")
    if selector in ("N13", "N14"):
        return (("owner_death_phase_frame_regression", CORE_CLASS + "LinuxOwnerDeathPhaseFrameTests", FRAME_METHOD),
                ("owner_death_cli_qualification", CLI_CLASS, CLI_METHOD))
    return (("linux_pipe_regression", CORE_CLASS + "LinuxOutputPipesTests", PIPE_METHOD),
            ("compiled_cli_qualification", CLI_CLASS, CLI_METHOD))


def validate_command_records(commands, selector):
    require(selector in SELECTORS, "build-selector")
    require(type(commands) is list and 0 < len(commands) <= 128, "build-command-bound")
    for ordinal, row in enumerate(commands):
        require(type(row) is dict and type(row.get("ordinal")) is int and row["ordinal"] == ordinal,
                "build-command-ordinal")
        args = row.get("argv")
        require(type(args) is list and args and all(type(arg) is str and arg for arg in args),
                "build-command-arguments")
        # MSBuild accepts /p:, -property:, case aliases and multi-property
        # arguments. A second selector in any such form cannot be ignored.
        # Response files are also forbidden: they can supply unseen properties.
        canonical = "-p:EvidencePrivateQualification=" + selector
        require(not any(arg.startswith("@") for arg in args)
                and all("evidenceprivatequalification" not in arg.lower()
                        or (selector != "None" and arg == canonical) for arg in args),
                "build-selector-alias-or-response-file")
        require(type(row.get("exit")) is int and row["exit"] == 0 and row.get("failure") is None
                and row.get("waited") is True and row.get("group_absent") is True
                and row.get("forced_cleanup") is False and row.get("timed_out") is False
                and row.get("late_completion") is not True, "build-command-terminal")
        for name, expected in (("pipe_eofs", [True, True]), ("read_errors", [False, False]),
                               ("write_errors", [False, False]), ("discarded_bytes", [0, 0])):
            values = row.get(name)
            require(type(values) is list and len(values) == 2
                    and all(type(actual) is type(wanted) and actual == wanted
                            for actual, wanted in zip(values, expected)), "build-command-output")
        logs = row.get("logs")
        require(type(logs) is list and len(logs) == 2
                and all(type(log) is dict and is_sha(log.get("sha256"))
                        and type(log.get("bytes")) is int and 0 <= log["bytes"] <= 8 * 1024 * 1024
                        for log in logs), "build-command-logs")
    selected = [row for row in commands if pathlib.PurePosixPath(row["argv"][0]).name == "dotnet"
                and len(row["argv"]) > 1 and row["argv"][1] in ("restore", "publish", "test", "build")]
    require([row["argv"][1] for row in selected] == ["restore", "publish", "restore", "test", "restore", "test"],
            "build-operation-sequence")
    projects = (CLI_PROJECT, CLI_PROJECT, CORE_PROJECT, CORE_PROJECT, CLI_TEST_PROJECT, CLI_TEST_PROJECT)
    for row, project in zip(selected, projects):
        args = row["argv"]
        require(len(args) > 2 and args[2].endswith("/" + project), "build-project")
        flags = [arg for arg in args if arg.startswith("-p:EvidencePrivateQualification=")]
        require(flags == ([] if selector == "None" else ["-p:EvidencePrivateQualification=" + selector]),
                "build-command-selector")
        require(("--locked-mode" in args) if args[1] == "restore" else ("--no-restore" in args),
                "build-restore-mode")
        if args[1] in ("test", "publish"):
            require("-p:UseSharedCompilation=false" in args, "build-compiler-mode")
    for row, (_, class_name, method) in zip((selected[3], selected[5]), expected_regressions(selector)):
        args = row["argv"]
        require(args.count("--filter") == 1 and args[args.index("--filter") + 1]
                == "FullyQualifiedName=" + class_name + "." + method, "build-test-filter")
    require(sum(arg.startswith("-p:EvidencePrivateQualification=") for row in commands for arg in row["argv"])
            == (0 if selector == "None" else 6), "build-selector-cardinality")
    return True


def validate_build_receipt(receipt, selector, source):
    """Validate a measured receipt against the independently pinned source tuple.

    ``source`` has commit, parent, tree, count, capture, projection, map and
    raw_map fields. It comes from the immutable common capture, never from the
    receipt being validated. File/archive checks remain with the I/O caller.
    """
    require(selector in SELECTORS and type(receipt) is dict and type(source) is dict, "build-receipt-shape")
    require(set(source) == {"commit", "parent", "tree", "count", "capture", "projection", "map", "raw_map"},
            "build-source-binding-shape")
    require(receipt.get("schema") == SCHEMA and type(receipt.get("exit")) is int and receipt["exit"] == 0
            and receipt.get("failure") is None and receipt.get("diagnostics") == [], "build-receipt-terminal")
    require(receipt.get("private_qualification") == selector and receipt.get("selected_variant") == selector,
            "build-receipt-selector")
    for field, binding in (("source_commit", "commit"), ("source_revision", "commit"), ("source_parent", "parent"),
                           ("source_count", "count"), ("capture_sha256", "capture"),
                           ("source_capture_sha256", "capture"), ("capture_input_sha256", "capture"),
                           ("capture_projection_sha256", "projection"), ("source_map_sha256", "map"),
                           ("source_capture_map_sha256", "raw_map"), ("harness_parent", "commit"),
                           ("harness_retry_parent", "commit"), ("harness_retry_parent_parent", "parent")):
        require(type(receipt.get(field)) is type(source[binding]) and receipt[field] == source[binding],
                "build-receipt-source")
    require(receipt.get("build_prerequisite_only") is True and receipt.get("test_execution") is True
            and all(receipt.get(field) is False for field in ("native_execution", "native_acceptance", "checkpoint_pass",
                                                            "review_clear", "source_review_clear", "fixture_review_clear",
                                                            "root_transport_review_clear"))
            and receipt.get("os_audit") is None, "build-receipt-scope")
    validate_command_records(receipt.get("commands"), selector)
    for phase in SOURCE_PHASES:
        fact = receipt.get(phase)
        require(type(fact) is dict and fact.get("head") == source["commit"] and fact.get("tree") == source["tree"]
                and type(fact.get("count")) is int and fact["count"] == source["count"]
                and fact.get("source_map_sha256") == source["map"]
                and all(fact.get(key) is True for key in ("index_tree_matches", "physical_git_sha1", "physical_sha256_modes")),
                "build-source-recheck")
    for key, class_name, method in expected_regressions(selector):
        fact = receipt.get(key)
        require(type(fact) is dict and fact.get("class_name") == class_name and fact.get("method") == method
                and fact.get("native_acceptance") is False and fact.get("root_factory_exercised") is False
                and fact.get("unprivileged_library_behavior_only") is True
                and is_sha(fact.get("trx_sha256")), "build-regression-binding")
        counters = fact.get("counters")
        require(type(counters) is dict and all(counters.get(key) == value for key, value in
                {"total": "1", "executed": "1", "passed": "1", "failed": "0", "error": "0",
                 "notExecuted": "0", "timeout": "0", "aborted": "0"}.items()), "build-regression-outcome")
    known = {"owner_death_phase_frame_regression", "owner_death_cli_qualification", "linux_pipe_regression",
             "compiled_cli_qualification"}
    expected = {row[0] for row in expected_regressions(selector)}
    require(not (known - expected) & set(receipt), "build-regression-foreign-selection")
    return True
