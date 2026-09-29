#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Actors/Tools/SkillManageTool.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/skill-manage-guard}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

# Resolve each guard decision separately. Source drift must fail before Stryker starts.
spans="$(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    @markers = (
      "PathUtility.ContainsSymlinkSegment(_paths.SkillsDirectory, path)",
      "_protectedPaths.IsDenied(path)",
      "paths.Add(targetPath + AtomicTempSuffix);"
    );
    for $marker (@markers) {
      $start = index($_, $marker);
      die "A skill_manage guard marker is missing or duplicated.\n"
        if $start < 0 || index($_, $marker, $start + 1) >= 0;
      $line = 1 + (substr($_, 0, $start) =~ tr/\n/\n/);
      print "$start ", $start + length($marker), " $line\n";
    }
  ' "$source_file"
)"

mutate_args=()
expected_lines=()
while read -r span_start span_end line; do
  mutate_args+=(--mutate "Tools/SkillManageTool.cs{$span_start..$span_end}")
  expected_lines+=("$line")
done <<< "$spans"

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    "${mutate_args[@]}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
for line in "${expected_lines[@]}"; do
  jq -e --arg source "$source_file" --argjson line "$line" '
    [.files[$source].mutants[] | select(.status != "Ignored" and .status != "CompileError")
      | select(.location.start.line == $line)] as $mutants
    | ($mutants | length) == 1 and all($mutants[]; .status == "Killed")
  ' "$report" > /dev/null || {
    echo "Expected one killed skill_manage guard mutant at line $line." >&2
    exit 1
  }
done

# Stryker can report unrelated compile errors before it applies the span filter.
# Each target above still requires one killed mutant.
jq -e '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length == 3' \
  "$report" > /dev/null || {
  echo "Expected exactly three skill_manage guard mutants." >&2
  exit 1
}
