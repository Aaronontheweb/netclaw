#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Daemon/Services/ConfigWatcherActor.cs"
output_path="${1:-$repo_root/artifacts/stryker/config-startup-reconciliation}"
[[ "$output_path" == /* ]] || output_path="$repo_root/$output_path"
if [[ -e "$output_path/scope.json" || -e "$output_path/reports" ]]; then
  echo "Select a fresh mutation output path." >&2
  exit 1
fi
mkdir -p "$output_path"

# Select the exact hash comparison. Roslyn uses UTF-16 offsets and columns.
read -r first last < <(python3 - "$source_file" "$output_path/scope.json" <<'SCOPE'
import hashlib, json, pathlib, sys
path, output = map(pathlib.Path, sys.argv[1:])
source = path.read_text()
begin, end = "// config-startup-reconciliation:begin", "// config-startup-reconciliation:end"
expression = "loadedHash != currentHash"
if any(source.count(marker) != 1 for marker in (begin, end, expression)):
    sys.exit("The startup hash boundary is missing or duplicated.")
start, stop = source.index(expression), source.index(expression) + len(expression)
if not source.index(begin) < start < stop < source.index(end):
    sys.exit("The hash comparison is outside its markers.")
def utf16(value): return len(value.encode("utf-16-le")) // 2
def point(offset):
    prefix = source[:offset]
    return {"line": prefix.count("\n") + 1, "column": utf16(prefix.rsplit("\n", 1)[-1]) + 1}
scope = {"path": str(path), "sha256": hashlib.sha256(source.encode()).hexdigest(),
         "location": {"start": point(start), "end": point(stop)}}
output.write_text(json.dumps(scope, indent=2) + "\n")
print(utf16(source[:start]), utf16(source[:stop]))
SCOPE
)

status=0
(cd "$repo_root/src/Netclaw.Actors.MutationTests" && dotnet stryker \
  --config-file stryker-config.json --project Netclaw.Daemon.csproj \
  --mutate "Services/ConfigWatcherActor.cs{$first..$last}" \
  --output "$output_path" --skip-version-check) || status=$?

python3 - "$output_path/scope.json" "$output_path/reports/mutation-report.json" <<'VERIFY'
import hashlib, json, pathlib, sys
scope_path, report_path = map(pathlib.Path, sys.argv[1:])
scope, report = json.loads(scope_path.read_text()), json.loads(report_path.read_text())
source = pathlib.Path(scope["path"]).read_text()
if hashlib.sha256(source.encode()).hexdigest() != scope["sha256"]:
    sys.exit("The source changed during the mutation run.")
if report["files"].get(scope["path"], {}).get("source") != source:
    sys.exit("The report source differs from the selected source.")
def point(value): return value["line"], value["column"]
def overlaps(location):
    return (point(location["start"]) < point(scope["location"]["end"])
            and point(location["end"]) > point(scope["location"]["start"]))
targets, compiler_artifacts, unexpected = [], [], []
for path, data in report["files"].items():
    for mutant in data["mutants"]:
        if path == scope["path"] and mutant["location"] == scope["location"]:
            targets.append(mutant)
        elif mutant["status"] == "CompileError" and not (path == scope["path"] and overlaps(mutant["location"])):
            compiler_artifacts.append({"file": path, **mutant})
        elif mutant["status"] != "Ignored":
            unexpected.append({"file": path, **mutant})
(report_path.parent / "outside-span-compile-errors.json").write_text(json.dumps(compiler_artifacts, indent=2) + "\n")
valid = (len(targets) == 1 and not unexpected and targets[0]["status"] == "Killed"
         and targets[0]["mutatorName"] == "Equality mutation"
         and "".join(targets[0]["replacement"].split()) == "loadedHash==currentHash")
summary = {"targets": len(targets), "killed": sum(m["status"] == "Killed" for m in targets),
           "unexpected": len(unexpected), "compiler_artifacts": len(compiler_artifacts), "passed": valid}
(report_path.parent / "gate-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
print(json.dumps(summary))
if not valid: sys.exit("Expected one killed != to == Equality mutant at the startup hash comparison.")
VERIFY
exit "$status"
