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

sdk = Path("/opt/homebrew/share/google-cloud-sdk")
sys.path[:0] = [str(sdk / "lib"), str(sdk / "lib" / "third_party")]
from apitools.base.py import encoding  # noqa: E402
from googlecloudsdk.api_lib.run import execution  # noqa: E402
from googlecloudsdk.core.resource import resource_printer  # noqa: E402
from googlecloudsdk.command_lib.run.printers.job_printer import ExecutionPrinter  # noqa: E402
from googlecloudsdk.generated_clients.apis.run.v1 import run_v1_messages as messages  # noqa: E402


def differences(left, right, path=""):
    if isinstance(left, dict) and isinstance(right, dict):
        for key in set(left) | set(right):
            yield from differences(left.get(key), right.get(key), path + "/" + key)
    elif isinstance(left, list) and isinstance(right, list):
        for index, (a, b) in enumerate(zip(left, right)):
            yield from differences(a, b, path + "/" + str(index))
    elif left != right:
        yield path, type(left).__name__, type(right).__name__


for argument in sys.argv[1:]:
    source = Path(argument).read_text()
    original = json.loads(source)
    message = encoding.JsonToMessage(messages.Execution, source)
    wrapped = execution.Execution(message, messages)
    output = io.StringIO()
    resource_printer.Print(wrapped, "json", out=output)
    rendered = json.loads(output.getvalue())
    diff = list(differences(rendered, original))
    allowed = {"/metadata/generation", "/status/observedGeneration", "/spec/template/spec/timeoutSeconds"}
    if any(item[0] not in allowed for item in diff):
        raise SystemExit(f"unexpected SDK representation difference: {diff}")
    print(json.dumps({"input": argument, "inputSha256": hashlib.sha256(source.encode()).hexdigest(), "outputSha256": hashlib.sha256(output.getvalue().encode()).hexdigest(), "semanticEquality": True, "representationDifferences": diff, "noNetwork": True}))


def printer_status(values):
    values = dict(values, completionTime=None, logUri=None)
    record = SimpleNamespace(status=SimpleNamespace(**values), ready_condition={"status": None}, creation_timestamp=None)
    return str(ExecutionPrinter.TransformStatus(record))


omitted = printer_status({"runningCount": None, "succeededCount": None, "failedCount": None, "cancelledCount": None, "retriedCount": None})
explicit_zero = printer_status({"runningCount": 0, "succeededCount": 0, "failedCount": 0, "cancelledCount": 0, "retriedCount": 0})
print(json.dumps({"counterDefaultReplay": {"omitted": omitted, "explicitZero": explicit_zero, "equal": omitted == explicit_zero, "scope": ["runningCount", "succeededCount", "failedCount", "cancelledCount"], "retriedCount": "not rendered by ExecutionPrinter", "noNetwork": True}}))
