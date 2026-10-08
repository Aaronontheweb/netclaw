#!/usr/bin/env python3
"""Show which part of each system skill file the model reads in one tool result.

The daemon bounds a tool result to the inline budget
(`SessionTuning.MaxInlineToolResultChars`). A longer result keeps the first
half and the last half of the budget. The model does not read the middle
unless it calls `tool_output_read`.

This script builds the same text that `skill_load` and `skill_read_resource`
return, and applies the same window. It reads the budget from the C# source,
so the number has one owner.

Usage:
    python3 evals/skill_visible_windows.py                 # the size table
    python3 evals/skill_visible_windows.py --sections      # each heading: head, hidden, tail
    python3 evals/skill_visible_windows.py --locate TEXT   # where TEXT is, in each file
    python3 evals/skill_visible_windows.py --json          # machine-readable
"""

import argparse
import json
from pathlib import Path
import re
import sys

REPO_ROOT = Path(__file__).resolve().parent.parent
SKILL_ROOT = REPO_ROOT / "feeds" / "skills" / ".system" / "files"
BUDGET_SOURCE = REPO_ROOT / "src" / "Netclaw.Configuration" / "SessionTuning.cs"
# The path that the eval container gives to a system skill. Its length moves
# the window of a `skill_read_resource` result by a few characters.
CONTAINER_SKILL_ROOT = "/home/netclaw/.netclaw/skills/.system"


def read_budget(source=BUDGET_SOURCE):
    match = re.search(r"MaxInlineToolResultChars\s*\{[^}]*\}\s*=\s*([0-9_]+)\s*;", source.read_text())
    if match is None:
        raise SystemExit(f"MaxInlineToolResultChars has no default in {source}")
    return int(match.group(1).replace("_", ""))


def extract_body(content):
    """Mirror of SkillScanner.ExtractBody."""
    closing = content.find("\n---", 3)
    if closing < 0:
        return content
    body_start = content.find("\n", closing + 4)
    return "" if body_start < 0 else content[body_start + 1:].lstrip()


def frontmatter_version(content):
    match = re.search(r'^\s+version:\s*"?([^"\n]+)"?\s*$', content, re.MULTILINE)
    return match.group(1) if match else None


def resource_paths(skill_dir):
    return sorted(
        str(path.relative_to(skill_dir)).replace("\\", "/")
        for path in skill_dir.rglob("*")
        if path.is_file() and path.name != "SKILL.md")


def skill_load_result(skill_dir):
    """Mirror of the text that SkillLoadTool returns for a file skill."""
    content = (skill_dir / "SKILL.md").read_text()
    body = extract_body(content)
    heading = re.search(r"^#\s+(.+)$", body, re.MULTILINE)
    display_name = heading.group(1).strip() if heading else skill_dir.name
    lines = [f"## {display_name}\n"]
    version = frontmatter_version(content)
    if version is not None:
        lines.append(f"Version: {version}\n")
    lines.append("\n")
    lines.append(body + "\n")
    resources = resource_paths(skill_dir)
    if resources:
        lines.append("\n### Available Resources\n")
        lines.append("Load via skill_read_resource(skillName, resourcePath):\n")
        lines.extend(f"- {path}\n" for path in resources)
    return "".join(lines)


def resource_result(skill_dir, relative):
    """Mirror of the text that SkillReadResourceTool returns."""
    path_line = f"path: {CONTAINER_SKILL_ROOT}/{skill_dir.name}/{relative}"
    return f"{path_line}\n{(skill_dir / relative).read_text()}"


def window(length, budget):
    """Returns (head_end, tail_start). The hidden part is [head_end, tail_start)."""
    if length <= budget:
        return length, length
    head = budget // 2 + budget % 2
    return head, length - budget // 2


def zone(offset, end, head_end, tail_start):
    """Names the zone of the span [offset, end)."""
    if head_end == tail_start:
        return "visible"
    if end <= head_end:
        return "head"
    if offset >= tail_start:
        return "tail"
    if offset >= head_end and end <= tail_start:
        return "hidden"
    return "cut"


def all_results():
    """Yields (label, text) for each system SKILL.md and reference file."""
    for skill_dir in sorted(path for path in SKILL_ROOT.iterdir() if path.is_dir()):
        yield f"{skill_dir.name}/SKILL.md", skill_load_result(skill_dir)
        for relative in resource_paths(skill_dir):
            yield f"{skill_dir.name}/{relative}", resource_result(skill_dir, relative)


def sections(text):
    """Yields (heading, start, end) for each markdown heading outside a code block."""
    starts = []
    in_code = False
    offset = 0
    for line in text.splitlines(keepends=True):
        if line.startswith("```"):
            in_code = not in_code
        elif not in_code and re.match(r"#{1,4} ", line):
            starts.append((line.strip(), offset))
        offset += len(line)
    for index, (heading, start) in enumerate(starts):
        end = starts[index + 1][1] if index + 1 < len(starts) else len(text)
        yield heading, start, end


def locate(text, needle, budget):
    """Returns the zone of each match of needle in a tool result."""
    head_end, tail_start = window(len(text), budget)
    zones = []
    start = text.find(needle)
    while start >= 0:
        zones.append((start, zone(start, start + len(needle), head_end, tail_start)))
        start = text.find(needle, start + 1)
    return zones


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--sections", action="store_true")
    parser.add_argument("--locate", action="append", default=[])
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)
    budget = read_budget()

    report = []
    for label, text in all_results():
        head_end, tail_start = window(len(text), budget)
        entry = {
            "file": label,
            "resultChars": len(text),
            "hiddenChars": tail_start - head_end,
            "headEnd": head_end,
            "tailStart": tail_start,
        }
        if args.sections:
            entry["sections"] = [
                {"heading": heading, "start": start, "zone": zone(start, end, head_end, tail_start)}
                for heading, start, end in sections(text)]
        if args.locate:
            entry["matches"] = {
                needle: [{"offset": offset, "zone": name} for offset, name in locate(text, needle, budget)]
                for needle in args.locate}
        report.append(entry)

    if args.json:
        json.dump({"budget": budget, "files": report}, sys.stdout, indent=2)
        print()
        return 0

    print(f"inline budget: {budget} chars (head {budget // 2 + budget % 2}, tail {budget // 2})")
    for entry in report:
        hidden = entry["hiddenChars"]
        share = f"{100 * hidden // entry['resultChars']}%" if hidden else "-"
        print(f"{entry['file']:55} result={entry['resultChars']:6} hidden={hidden:6} {share}")
        for section in entry.get("sections", []):
            print(f"    {section['zone']:7} {section['start']:6} {section['heading']}")
        for needle, matches in entry.get("matches", {}).items():
            if matches:
                found = ", ".join(f"{match['zone']}@{match['offset']}" for match in matches)
                print(f"    {needle!r}: {found}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
