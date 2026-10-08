#!/usr/bin/env python3
"""Evidence for the "Skill Guidance Position" eval cases.

`run-evals.sh` calls this module after each run of a position case. The module
reads the `--json` envelopes of the run and the headless session log, and
prints one JSON object. The assertions in `run-evals.sh` read that object with
`jq`, and the runner stores it as the recorded context cost.

The envelope gives the tool calls and their arguments. The session log gives
the text that each tool returned to the model, after the daemon bounded it.

Usage:
    skill_position_evals.py evidence <stdout-file> <session-log> [<session-log> ...]
    skill_position_evals.py summary <evidence-jsonl>
"""

import json
from pathlib import Path
import re
import sys

# The tools that return skill text to the model.
SKILL_TEXT_TOOLS = ("skill_load", "skill_read_resource", "tool_output_read")
TRUNCATION_MARK = re.compile(r"\[output truncated to (\d+) chars of (\d+)")
STEER_MARK = "continue with tool_output_read using CallId="
# A path below a skills folder. `skill_read_resource` prints the path of one
# resource, so an agent can find the folder without a guess.
PHYSICAL_SKILL_PATH = re.compile(r"\.netclaw/skills(?![\w-])|/skills/\.system|/skills/\.server-feeds|SKILL\.md")
RECORD_START = re.compile(r"^\[\d{4}-\d{2}-\d{2}T[^\]]*\] ")
TOOL_RESULT = re.compile(r"^TOOL_RESULT: (\S+) call_id=(\S+) result=(.*)$", re.DOTALL)


def read_envelopes(stdout_file):
    """Returns each JSON envelope in the file. A multi-turn case writes one for each turn."""
    text = Path(stdout_file).read_text(errors="replace")
    decoder = json.JSONDecoder()
    envelopes = []
    position = 0
    while position < len(text):
        while position < len(text) and text[position].isspace():
            position += 1
        if position >= len(text):
            break
        try:
            value, position = decoder.raw_decode(text, position)
        except json.JSONDecodeError:
            break
        if isinstance(value, dict):
            envelopes.append(value)
    return envelopes


def read_tool_results(session_logs):
    """Returns {call_id: (tool_name, result_text)} from the headless session logs."""
    results = {}
    for session_log in session_logs:
        path = Path(session_log)
        if not path.is_file():
            continue
        record = None
        records = []
        for line in path.read_text(errors="replace").splitlines(keepends=True):
            if RECORD_START.match(line):
                if record is not None:
                    records.append(record)
                record = RECORD_START.sub("", line, count=1)
            elif record is not None:
                record += line
        if record is not None:
            records.append(record)
        for body in records:
            match = TOOL_RESULT.match(body)
            if match:
                results[match.group(2)] = (match.group(1), match.group(3).rstrip("\n"))
    return results


def arguments_of(call):
    try:
        value = json.loads(call.get("argumentsJson") or "{}")
    except json.JSONDecodeError:
        return {}
    return value if isinstance(value, dict) else {}


def argument(arguments, name):
    """Reads one argument. The daemon tolerates case and punctuation in a key."""
    wanted = re.sub(r"[^a-z0-9]", "", name.lower())
    for key, value in arguments.items():
        if re.sub(r"[^a-z0-9]", "", key.lower()) == wanted:
            return value
    return None


def build_evidence(envelopes, tool_results):
    calls = []
    for turn, envelope in enumerate(envelopes, start=1):
        for call in envelope.get("toolCalls") or []:
            arguments = arguments_of(call)
            calls.append({
                "turn": turn,
                "tool": call.get("toolName", ""),
                "callId": call.get("callId", ""),
                "arguments": arguments,
            })

    skill_text_chars = 0
    chars_by_tool = {tool: 0 for tool in SKILL_TEXT_TOOLS}
    spilled = []
    steer_present = True
    missing_results = 0
    for call in calls:
        if call["tool"] not in SKILL_TEXT_TOOLS:
            continue
        logged = tool_results.get(call["callId"])
        if logged is None:
            missing_results += 1
            continue
        text = logged[1]
        skill_text_chars += len(text)
        chars_by_tool[call["tool"]] += len(text)
        mark = TRUNCATION_MARK.search(text)
        if mark and call["tool"] != "tool_output_read":
            spilled.append({
                "tool": call["tool"],
                "callId": call["callId"],
                "shownChars": int(mark.group(1)),
                "fullChars": int(mark.group(2)),
            })
            steer_present = steer_present and STEER_MARK in text

    skills_loaded = []
    resources_read = []
    output_reads = []
    physical_skill_reads = 0
    for call in calls:
        arguments = call["arguments"]
        if call["tool"] == "skill_load":
            skills_loaded.append(str(argument(arguments, "Name") or "").strip().lower())
        elif call["tool"] == "skill_read_resource":
            resources_read.append("{}:{}".format(
                str(argument(arguments, "SkillName") or "").strip().lower(),
                str(argument(arguments, "ResourcePath") or "").strip()))
        elif call["tool"] == "tool_output_read":
            output_reads.append({
                "callId": argument(arguments, "CallId"),
                "start": argument(arguments, "Start"),
                "limit": argument(arguments, "Limit"),
            })
        elif call["tool"] in ("file_read", "file_search", "file_list", "shell_execute"):
            # A read of a physical skill file goes around the logical skill tools.
            if PHYSICAL_SKILL_PATH.search(json.dumps(arguments)):
                physical_skill_reads += 1

    spilled_ids = {entry["callId"] for entry in spilled}
    return {
        "turns": len(envelopes),
        "toolCalls": len(calls),
        "toolSequence": [call["tool"] for call in calls],
        "toolsByTurn": [
            [call["tool"] for call in calls if call["turn"] == turn]
            for turn in range(1, len(envelopes) + 1)],
        "skillsLoaded": skills_loaded,
        "resourcesRead": resources_read,
        "skillTextChars": skill_text_chars,
        "skillTextCharsByTool": chars_by_tool,
        "missingResults": missing_results,
        "spilledResults": spilled,
        "spillSteerPresent": steer_present if spilled else None,
        "outputReads": output_reads,
        "outputReadCount": len(output_reads),
        "outputReadsOfSkillText": sum(1 for read in output_reads if read["callId"] in spilled_ids),
        "physicalSkillReads": physical_skill_reads,
        "shellCommands": [
            str(argument(call["arguments"], "Command") or "")
            for call in calls if call["tool"] == "shell_execute"],
        "response": "\n".join(str(envelope.get("response") or "") for envelope in envelopes),
        "lastResponse": str(envelopes[-1].get("response") or "") if envelopes else "",
    }


def summarize(records):
    """Groups evidence records by case. Each record has `case` and `passed`."""
    by_case = {}
    for record in records:
        by_case.setdefault(record["case"], []).append(record)
    rows = []
    for case, runs in by_case.items():
        count = len(runs)
        followed = [run for run in runs if run["outputReadsOfSkillText"] > 0]
        rows.append({
            "case": case,
            "runs": count,
            "passes": sum(1 for run in runs if run["passed"]),
            "avgToolCalls": round(sum(run["toolCalls"] for run in runs) / count, 1),
            "avgSkillTextChars": round(sum(run["skillTextChars"] for run in runs) / count),
            "runsWithSpill": sum(1 for run in runs if run["spilledResults"]),
            "runsWithFollowUp": len(followed),
            "followUpReads": sum(run["outputReadsOfSkillText"] for run in runs),
            "passesAfterFollowUp": sum(1 for run in followed if run["passed"]),
            "passesWithoutFollowUp": sum(
                1 for run in runs if run["passed"] and run["outputReadsOfSkillText"] == 0),
            "runsWithSteer": sum(1 for run in runs if run.get("spillSteerPresent")),
            "factsCorrect": sum(1 for run in runs if run.get("factsCorrect")),
            "runsWithPhysicalRead": sum(1 for run in runs if run.get("physicalSkillReads", 0) > 0),
        })
    return rows


def format_summary(rows):
    header = ("case", "pass", "facts correct", "tool calls", "skill chars", "spilled", "steer shown",
              "tool_output_read runs", "reads", "pass after read", "physical-read runs")
    lines = ["  " + " | ".join(header)]
    for row in rows:
        lines.append("  " + " | ".join(str(value) for value in (
            row["case"], f"{row['passes']}/{row['runs']}", f"{row['factsCorrect']}/{row['runs']}",
            row["avgToolCalls"], row["avgSkillTextChars"], f"{row['runsWithSpill']}/{row['runs']}",
            f"{row['runsWithSteer']}/{row['runs']}", f"{row['runsWithFollowUp']}/{row['runs']}",
            row["followUpReads"], f"{row['passesAfterFollowUp']}/{row['runsWithFollowUp']}",
            f"{row['runsWithPhysicalRead']}/{row['runs']}")))
    return "\n".join(lines)


def main(argv):
    if len(argv) >= 3 and argv[1] == "evidence":
        evidence = build_evidence(read_envelopes(argv[2]), read_tool_results(argv[3:]))
        json.dump(evidence, sys.stdout)
        print()
        return 0
    if len(argv) == 3 and argv[1] == "summary":
        records = [
            json.loads(line) for line in Path(argv[2]).read_text().splitlines() if line.strip()]
        print(format_summary(summarize(records)))
        return 0
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
