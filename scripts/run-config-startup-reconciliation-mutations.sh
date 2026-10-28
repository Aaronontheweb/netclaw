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

# Roslyn uses UTF-16 offsets and columns. Select only the retained-event literal.
read -r span_start span_end < <(
  python3 - "$source_file" "$output_path/scope.json" <<'SCOPE'
import json
import pathlib
import sys

source_path, scope_path = map(pathlib.Path, sys.argv[1:])
source = source_path.read_text(encoding="utf-8")
begin = "// config-startup-reconciliation:begin"
end = "// config-startup-reconciliation:end"
if source.count(begin) != 1 or source.count(end) != 1:
    sys.exit("The retained-event markers are missing or duplicated.")
start = source.index(begin) + len(begin)
stop = source.index(end)
if " ".join(source[start:stop].split()) != "_pendingReload = true;":
    sys.exit("The retained-event assignment changed.")
start = source.index("true", start, stop)
stop = start + len("true")

def utf16(text):
    return len(text.encode("utf-16-le")) // 2

def position(offset):
    prefix = source[:offset]
    return {"line": prefix.count("\n") + 1,
            "column": utf16(prefix.rsplit("\n", 1)[-1]) + 1}

location = {"start": position(start), "end": position(stop)}
scope = {"source_path": str(source_path), "source": source, "location": location,
         "expected": {"mutatorName": "Boolean mutation", "replacement": "false", "location": location}}
scope_path.write_text(json.dumps(scope, indent=2) + "\n", encoding="utf-8")
print(utf16(source[:start]), utf16(source[:stop]))
SCOPE
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

def overlaps(location):
    return (point(location["start"]) < point(scope["location"]["end"])
            and point(location["end"]) > point(scope["location"]["start"]))

def within(location):
    return (point(scope["location"]["start"]) <= point(location["start"])
            < point(location["end"]) <= point(scope["location"]["end"]))

targets = []
outside_compile_errors = []
unexpected = []
for path, entry in files.items():
    for mutant in entry["mutants"]:
        if path == source_path and within(mutant["location"]):
            targets.append(mutant)
        elif path == source_path and overlaps(mutant["location"]) and mutant["status"] == "CompileError":
            unexpected.append({"file": path, **mutant})
        elif mutant["status"] == "CompileError":
            outside_compile_errors.append({"file": path, **mutant})
        elif mutant["status"] != "Ignored":
            unexpected.append({"file": path, **mutant})

# Preserve compiler artifacts outside the span. A target CompileError always fails.
receipt = report_path.parent / "outside-span-compile-errors.json"
receipt.write_text(json.dumps(outside_compile_errors, indent=2) + "\n", encoding="utf-8")
expected = scope["expected"]
valid = (len(targets) == 1 and not unexpected
         and all(targets[0].get(key) == value for key, value in expected.items())
         and targets[0]["status"] == "Killed")
summary = {"expected": 1, "targets": len(targets),
           "killed": sum(mutant["status"] == "Killed" for mutant in targets),
           "unexpected_active": len(unexpected),
           "outside_span_compile_errors": len(outside_compile_errors), "passed": valid}
(report_path.parent / "gate-summary.json").write_text(
    json.dumps(summary, indent=2) + "\n", encoding="utf-8")
print(json.dumps(summary))
if not valid:
    sys.exit("Expected one killed true-to-false Boolean mutant at the retained-event literal.")
PY

if [[ "$stryker_status" -ne 0 ]]; then
  echo "Stryker failed with exit code $stryker_status. The gate cannot pass." >&2
  exit "$stryker_status"
fi
