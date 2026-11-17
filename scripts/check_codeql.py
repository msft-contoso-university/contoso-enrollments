import json
from pathlib import Path
import sys

EXPECTED = {"csharp", "actions", "python"}


def validate_report(report, name):
    if not isinstance(report, dict) or report.get("version") != "2.1.0":
        raise ValueError(f"Invalid SARIF version: {name}")
    runs = report.get("runs")
    if not isinstance(runs, list) or not runs:
        raise ValueError(f"Missing SARIF runs: {name}")
    for run in runs:
        if not isinstance(run, dict) or run.get("tool", {}).get("driver", {}).get("name") != "CodeQL":
            raise ValueError(f"Unexpected SARIF driver: {name}")
        results = run.get("results", [])
        if not isinstance(results, list) or results:
            raise ValueError(f"CodeQL findings or malformed results: {name}")
        invocations = run.get("invocations", [])
        if not isinstance(invocations, list) or any(
            not isinstance(invocation, dict) or invocation.get("executionSuccessful") is False
            for invocation in invocations
        ):
            raise ValueError(f"Unsuccessful CodeQL invocation: {name}")


def validate_files(directory):
    files = list(Path(directory).glob("*.sarif"))
    if {path.stem for path in files} != EXPECTED or len(files) != len(EXPECTED):
        raise ValueError("Missing or unexpected CodeQL language evidence")
    for path in files:
        validate_report(json.loads(path.read_text(encoding="utf-8")), path.name)


if __name__ == "__main__":
    try:
        validate_files(sys.argv[1])
    except (ValueError, OSError, IndexError) as error:
        raise SystemExit(str(error)) from error
    print("CodeQL SARIF evidence is complete and has no findings")
