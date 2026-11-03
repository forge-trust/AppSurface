"""Replay v1 Execution JSON through the installed gcloud SDK formatter.

This script is offline: it imports local SDK message/formatter code and never
contacts Cloud Run. It reports semantic equality separately from JSON byte/type
equality because the SDK normalizes generation and timeout representations.
"""
import hashlib
import io
import json
import sys
from types import SimpleNamespace
from pathlib import Path


def differences(left, right, path=""):
    if isinstance(left, dict) and isinstance(right, dict):
        for key in left.keys() | right.keys():
            child_path = path + "/" + key
            if key not in left:
                yield child_path, "missing", type(right[key]).__name__
            elif key not in right:
                yield child_path, type(left[key]).__name__, "missing"
            else:
                yield from differences(left[key], right[key], child_path)
    elif isinstance(left, list) and isinstance(right, list):
        shared_length = min(len(left), len(right))
        for index, (a, b) in enumerate(zip(left[:shared_length], right[:shared_length], strict=True)):
            yield from differences(a, b, path + "/" + str(index))
        for index in range(shared_length, len(left)):
            yield path + "/" + str(index), type(left[index]).__name__, "missing"
        for index in range(shared_length, len(right)):
            yield path + "/" + str(index), "missing", type(right[index]).__name__
    elif type(left) is not type(right) or left != right:
        yield path, type(left).__name__, type(right).__name__


def load_sdk():
    sdk = Path("/opt/homebrew/share/google-cloud-sdk")
    sys.path[:0] = [str(sdk / "lib"), str(sdk / "lib" / "third_party")]
    from apitools.base.py import encoding
    from googlecloudsdk.api_lib.run import execution
    from googlecloudsdk.core.resource import resource_printer
    from googlecloudsdk.command_lib.run.printers.job_printer import ExecutionPrinter
    from googlecloudsdk.generated_clients.apis.run.v1 import run_v1_messages as messages

    return encoding, execution, resource_printer, ExecutionPrinter, messages


def allowed_representation_differences(rendered, original, diff):
    """Allow only exact-value scalar conversions in the documented fixture mapping.

    Offline replay makes no provider request. The installed formatter converts
    generation and observedGeneration from fixture strings to integers, and
    timeoutSeconds from an integer to a string. The manifest records candidate
    SDK evidence only; these narrow conversions do not certify provider output.
    """
    expected_types = {
        "/metadata/generation": (int, str),
        "/status/observedGeneration": (int, str),
        "/spec/template/spec/timeoutSeconds": (str, int),
    }
    for path, _, _ in diff:
        expected = expected_types.get(path)
        if expected is None:
            return False

        rendered_value, original_value = rendered, original
        for key in path.strip("/").split("/"):
            if (
                not isinstance(rendered_value, dict)
                or not isinstance(original_value, dict)
                or key not in rendered_value
                or key not in original_value
            ):
                return False
            rendered_value = rendered_value[key]
            original_value = original_value[key]

        if type(rendered_value) is not expected[0] or type(original_value) is not expected[1]:
            return False

        if expected == (int, str):
            if not integer_string_matches(original_value, rendered_value):
                return False
        elif not integer_string_matches(rendered_value, original_value):
            return False

    return True


def integer_string_matches(string_value, integer_value):
    digits = string_value[1:] if string_value.startswith("-") else string_value
    if not digits or not digits.isascii() or not digits.isdigit():
        return False
    try:
        return int(string_value) == integer_value
    except ValueError:
        return False


def validated_differences(rendered, original):
    diff = list(differences(rendered, original))
    if not allowed_representation_differences(rendered, original, diff):
        raise ValueError(f"unexpected SDK representation difference: {diff}")
    return diff


def replay(arguments, encoding, execution, resource_printer, messages):
    for argument in arguments:
        source = Path(argument).read_text()
        original = json.loads(source)
        message = encoding.JsonToMessage(messages.Execution, source)
        wrapped = execution.Execution(message, messages)
        output = io.StringIO()
        resource_printer.Print(wrapped, "json", out=output)
        rendered = json.loads(output.getvalue())
        try:
            diff = validated_differences(rendered, original)
        except ValueError as error:
            raise SystemExit(str(error)) from None
        print(json.dumps({"input": argument, "inputSha256": hashlib.sha256(source.encode()).hexdigest(), "outputSha256": hashlib.sha256(output.getvalue().encode()).hexdigest(), "semanticEquality": True, "representationDifferences": diff, "noNetwork": True}))


def printer_status(values, execution_printer):
    values = dict(values, completionTime=None, logUri=None)
    record = SimpleNamespace(status=SimpleNamespace(**values), ready_condition={"status": None}, creation_timestamp=None)
    return str(execution_printer.TransformStatus(record))


def main(arguments):
    encoding, execution, resource_printer, execution_printer, messages = load_sdk()
    replay(arguments, encoding, execution, resource_printer, messages)

    omitted = printer_status({"runningCount": None, "succeededCount": None, "failedCount": None, "cancelledCount": None, "retriedCount": None}, execution_printer)
    explicit_zero = printer_status({"runningCount": 0, "succeededCount": 0, "failedCount": 0, "cancelledCount": 0, "retriedCount": 0}, execution_printer)
    print(json.dumps({"counterDefaultReplay": {"omitted": omitted, "explicitZero": explicit_zero, "equal": omitted == explicit_zero, "scope": ["runningCount", "succeededCount", "failedCount", "cancelledCount"], "retriedCount": "not rendered by ExecutionPrinter", "noNetwork": True}}))


if __name__ == "__main__":
    main(sys.argv[1:])
