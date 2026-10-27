#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Daemon/Services/ConfigWatcherService.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/config-startup-reconciliation}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi
if [[ -e "$output_path/scope.json" || -e "$output_path/reports" ]]; then
  echo "The output path contains prior evidence. Select a fresh output path." >&2
  exit 1
fi
mkdir -p "$output_path"

# Select the inner predicate only. Roslyn uses UTF-16 offsets and columns.
read -r span_start span_end < <(
  python3 - "$source_file" "$output_path/scope.json" <<'PY'
import json
import pathlib
import sys

source_path, scope_path = map(pathlib.Path, sys.argv[1:])
source = source_path.read_text(encoding="utf-8")
begin = "// config-startup-reconciliation:begin"
end = "// config-startup-reconciliation:end"
if source.count(begin) != 1 or source.count(end) != 1:
    sys.exit("The predicate markers are missing or duplicated.")
left = "!hasKey"
right = "!string.Equals(entry.Value, value, StringComparison.Ordinal)"
predicate = left + " || " + right
if source.count(predicate) != 1:
    sys.exit("The exact inner predicate is missing or duplicated.")
start = source.index(predicate)
stop = start + len(predicate)
if not source.index(begin) + len(begin) < start < stop <= source.index(end):
    sys.exit("The predicate is outside its markers.")

def utf16(text):
    return len(text.encode("utf-16-le")) // 2

def position(offset):
    prefix = source[:offset]
    return {"line": prefix.count("\n") + 1,
            "column": utf16(prefix.rsplit("\n", 1)[-1]) + 1}

def location(offset, text):
    return {"start": position(offset), "end": position(offset + len(text))}

# The first report contains one logical mutation and two negation removals.
# The lookup now precedes the predicate, so each mutation can read its result.
expected = []
negation = "LogicalNotExpression to un-LogicalNotExpression mutation"
for original, replacement, mutator in (
    (predicate, predicate.replace("||", "&&"), "Logical mutation"),
    (left, left[1:], negation),
    (right, right[1:], negation),
):
    offset = start + predicate.index(original)
    expected.append({"original": original, "replacement": replacement,
                     "location": location(offset, original), "mutatorName": mutator})
scope = {"source_path": str(source_path), "source": source,
         "location": location(start, predicate), "expected": expected}
scope_path.write_text(json.dumps(scope, indent=2) + "\n", encoding="utf-8")
print(utf16(source[:start]), utf16(source[:stop]))
PY
)

# Keep reports even when Stryker fails. A failed initial run never passes this gate.
stryker_status=0
(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Daemon.csproj \
    --mutate "Services/ConfigWatcherService.cs{$span_start..$span_end}" \
    --output "$output_path" \
    --skip-version-check
) || stryker_status=$?

python3 - "$output_path/scope.json" "$output_path/reports/mutation-report.json" <<'PY'
import json
import pathlib
import re
import sys

scope_path, report_path = map(pathlib.Path, sys.argv[1:])
scope = json.loads(scope_path.read_text(encoding="utf-8"))
report = json.loads(report_path.read_text(encoding="utf-8"))
source_path = scope["source_path"]
source = pathlib.Path(source_path).read_text(encoding="utf-8")
if source != scope["source"]:
    sys.exit("The source changed during the mutation run.")
files = report["files"]
if source_path not in files or files[source_path].get("source") != source:
    sys.exit("The report source does not match the selected source file.")

def point(value):
    return value["line"], value["column"]

def within(location):
    return (point(scope["location"]["start"]) <= point(location["start"])
            < point(location["end"]) <= point(scope["location"]["end"]))

def identity(value):
    # Roslyn can change trivia around an operator. No target contains a string literal.
    return (value["mutatorName"], point(value["location"]["start"]), point(value["location"]["end"]),
            re.sub(r"\s+", "", value["replacement"]))

expected = scope["expected"]
targets = []
outside_compile_errors = []
unexpected = []
for path, entry in files.items():
    for mutant in entry["mutants"]:
        if path == source_path and within(mutant["location"]):
            targets.append(mutant)
        elif mutant["status"] == "CompileError":
            outside_compile_errors.append({"file": path, **mutant})
        elif mutant["status"] != "Ignored":
            unexpected.append({"file": path, **mutant})

# Preserve compiler artifacts outside the span. A target CompileError always fails.
receipt = report_path.parent / "outside-span-compile-errors.json"
receipt.write_text(json.dumps(outside_compile_errors, indent=2) + "\n", encoding="utf-8")
valid = (len(expected) == len(targets) == 3
         and sorted(map(identity, expected)) == sorted(map(identity, targets))
         and all(mutant["status"] == "Killed" for mutant in targets)
         and not unexpected)
summary = {"expected": len(expected), "targets": len(targets),
           "killed": sum(mutant["status"] == "Killed" for mutant in targets),
           "unexpected_active": len(unexpected),
           "outside_span_compile_errors": len(outside_compile_errors), "passed": valid}
(report_path.parent / "gate-summary.json").write_text(
    json.dumps(summary, indent=2) + "\n", encoding="utf-8")
print(json.dumps(summary))
if not valid:
    sys.exit("Expected exactly three killed mutants with the selected source, mutators, locations, and replacements.")
PY

if [[ "$stryker_status" -ne 0 ]]; then
  echo "Stryker failed with exit code $stryker_status. The gate cannot pass." >&2
  exit "$stryker_status"
fi
