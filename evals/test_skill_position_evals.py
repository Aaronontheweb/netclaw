"""Verify the window analysis, the evidence reader, and the position assertions."""

import json
from pathlib import Path
import subprocess
import tempfile
import unittest

import skill_position_evals as evidence
import skill_visible_windows as windows

EVALS = Path(__file__).resolve().parent
FACTS = EVALS / "fixtures" / "skill-position" / "facts.json"
ALWAYS_LOADED = [
    windows.REPO_ROOT / "src" / "Netclaw.Configuration" / "Resources" / "AGENTS.md",
    windows.REPO_ROOT / "src" / "Netclaw.Cli" / "Resources" / "identity" / "SOUL.template.md",
    windows.REPO_ROOT / "src" / "Netclaw.Cli" / "Resources" / "identity" / "TOOLING.template.md",
    EVALS / "fixtures" / "identity" / "AGENTS.md",
]
SPILL_STEER = ("\n\n[output truncated to 12000 chars of 56948; continue with tool_output_read "
               "using CallId='load1' and a bounded Start/Limit window instead of re-running]")


class WindowTests(unittest.TestCase):
    def test_budget_comes_from_the_session_tuning_source(self):
        self.assertGreater(windows.read_budget(), 0)

    def test_window_matches_the_daemon_split(self):
        self.assertEqual((5, 11), windows.window(16, 10))
        self.assertEqual((6, 12), windows.window(17, 11))
        self.assertEqual((8, 8), windows.window(8, 10))

    def test_zone_names_each_part(self):
        self.assertEqual("head", windows.zone(0, 5, 5, 11))
        self.assertEqual("cut", windows.zone(3, 7, 5, 11))
        self.assertEqual("hidden", windows.zone(5, 11, 5, 11))
        self.assertEqual("tail", windows.zone(11, 16, 5, 11))
        self.assertEqual("visible", windows.zone(0, 4, 8, 8))

    def test_body_extraction_removes_frontmatter(self):
        self.assertEqual("# Title\n", windows.extract_body("---\nname: x\n---\n\n# Title\n"))


class FactZoneTests(unittest.TestCase):
    """Each eval fact must be in the zone that its case states.

    A failure here means that a skill edit moved the fact. Update the row in
    facts.json and the comment of the case, because the case now measures
    another position.
    """

    def test_each_fact_is_in_its_stated_zone(self):
        budget = windows.read_budget()
        results = dict(windows.all_results())
        always = "".join(path.read_text() for path in ALWAYS_LOADED)
        for fact in json.loads(FACTS.read_text())["facts"]:
            with self.subTest(case=fact["case"], needle=fact["needle"][:40]):
                self.assertIn(fact["file"], results)
                zones = sorted({name for _, name in windows.locate(
                    results[fact["file"]], fact["needle"], budget)})
                self.assertEqual(sorted(fact["zones"]), zones)
                if fact["exclusive"]:
                    others = [label for label, text in results.items()
                              if label != fact["file"] and fact["needle"] in text]
                    self.assertEqual([], others)
                    self.assertNotIn(fact["needle"], always)


def envelope(calls, response="", session="signalr/abc"):
    return {"sessionId": session, "response": response, "toolCalls": [
        {"toolName": name, "callId": call_id, "argumentsJson": json.dumps(arguments)}
        for name, call_id, arguments in calls]}


def session_log(results):
    lines = ["[2026-10-08T16:30:03.2063589+00:00] Headless session started: signalr/abc\n"]
    for name, call_id, text in results:
        lines.append(f"[2026-10-08T16:30:04.0000000+00:00] TOOL_RESULT: {name} call_id={call_id} result={text}\n")
        lines.append("[2026-10-08T16:30:05.0000000+00:00] USAGE: in=1 out=1\n")
    return "".join(lines)


class EvidenceTests(unittest.TestCase):
    def build(self, envelopes, results):
        with tempfile.TemporaryDirectory() as directory:
            stdout = Path(directory) / "stdout.json"
            stdout.write_text("\n".join(json.dumps(item) for item in envelopes))
            log = Path(directory) / "session.log"
            log.write_text(session_log(results))
            return evidence.build_evidence(
                evidence.read_envelopes(stdout), evidence.read_tool_results([log]))

    def test_counts_returned_skill_text_and_the_follow_up(self):
        result = self.build(
            [envelope([
                ("skill_load", "load1", {"Name": "Netclaw-Operations", "_rationale": "x"}),
                ("tool_output_read", "read1", {"CallId": "load1", "Start": 6000, "Limit": 8000}),
                ("skill_read_resource", "res1", {"skill_name": "netclaw-operations",
                                                 "resourcePath": "references/webhooks.md"}),
                ("shell_execute", "sh1", {"Command": "git status"})])],
            [("skill_load", "load1", "x" * 100 + SPILL_STEER),
             ("tool_output_read", "read1", "line one\nline two"),
             ("skill_read_resource", "res1", "y" * 50),
             ("shell_execute", "sh1", "z" * 999)])
        self.assertEqual(4, result["toolCalls"])
        self.assertEqual(["netclaw-operations"], result["skillsLoaded"])
        self.assertEqual(["netclaw-operations:references/webhooks.md"], result["resourcesRead"])
        self.assertEqual(100 + len(SPILL_STEER) + len("line one\nline two") + 50, result["skillTextChars"])
        self.assertEqual(1, len(result["spilledResults"]))
        self.assertTrue(result["spillSteerPresent"])
        self.assertEqual(1, result["outputReadsOfSkillText"])
        self.assertEqual(["git status"], result["shellCommands"])

    def test_an_output_read_of_another_call_is_not_a_skill_follow_up(self):
        result = self.build(
            [envelope([
                ("skill_load", "load1", {"Name": "netclaw-memory"}),
                ("tool_output_read", "read1", {"CallId": "shell9"})])],
            [("skill_load", "load1", "short"), ("tool_output_read", "read1", "text")])
        self.assertEqual(1, result["outputReadCount"])
        self.assertEqual(0, result["outputReadsOfSkillText"])
        self.assertIsNone(result["spillSteerPresent"])

    def test_a_physical_skill_file_read_is_counted(self):
        result = self.build(
            [envelope([("file_read", "f1", {
                "Path": "/home/netclaw/.netclaw/skills/.system/files/netclaw-operations/SKILL.md"})])],
            [])
        self.assertEqual(1, result["physicalSkillReads"])

    def test_each_turn_keeps_its_own_tools(self):
        result = self.build(
            [envelope([("store_memory", "a", {})]), envelope([("find_memories", "b", {})])], [])
        self.assertEqual([["store_memory"], ["find_memories"]], result["toolsByTurn"])

    def test_summary_separates_passes_with_and_without_a_follow_up(self):
        rows = evidence.summarize([
            {"case": "c", "passed": True, "toolCalls": 2, "skillTextChars": 100,
             "spilledResults": [{}], "outputReadsOfSkillText": 2},
            {"case": "c", "passed": True, "toolCalls": 1, "skillTextChars": 50,
             "spilledResults": [{}], "outputReadsOfSkillText": 0},
            {"case": "c", "passed": False, "toolCalls": 1, "skillTextChars": 50,
             "spilledResults": [], "outputReadsOfSkillText": 0}])
        self.assertEqual(1, len(rows))
        self.assertEqual((2, 3, 2, 1, 2, 1, 1), (
            rows[0]["passes"], rows[0]["runs"], rows[0]["runsWithSpill"], rows[0]["runsWithFollowUp"],
            rows[0]["followUpReads"], rows[0]["passesAfterFollowUp"], rows[0]["passesWithoutFollowUp"]))


class AssertionTests(unittest.TestCase):
    """Run the bash assertions of run-evals.sh on fabricated runs."""

    def run_assertion(self, assertion, envelopes, results, setup=""):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory) / "home"
            (home / "logs").mkdir(parents=True)
            stdout = Path(directory) / "stdout.json"
            stdout.write_text("\n".join(json.dumps(item) for item in envelopes))
            (home / "logs" / "signalr-abc.log").write_text(session_log(results))
            script = (
                'source "$1"; set +e; EVAL_HOME="$2"; STDOUT_FILE="$3"; EVAL_ASSERTION_DETAILS=""; '
                + (setup + "; " if setup else "") +
                f'{assertion}; status=$?; printf "%s" "$EVAL_ASSERTION_DETAILS"; exit $status')
            completed = subprocess.run(
                ["bash", "-c", script, "bash", str(EVALS / "run-evals.sh"), str(home), str(stdout)],
                capture_output=True, text=True)
            return completed.returncode == 0, completed.stdout

    def test_a_run_with_no_tool_call_fails_each_case(self):
        nothing = [envelope([], "NORMAL /api/mcp/oauth/callback tool-approvals.json.invalid "
                                "file_search tool_output_read 12 days X-TextForge-Signature 0.24 "
                                "get_reminder_history netclaw reminder enable delete_webhook")]
        for case in ["head", "tail", "middle_oauth_redirect", "middle_approvals_quarantine",
                     "reference_middle", "memory_no_recall_block", "right_reference_operations",
                     "right_reference_memory", "memory_tool_choice", "middle_long_commit",
                     "middle_oauth_redirect_steer", "reference_middle_steer"]:
            with self.subTest(case=case):
                passed, details = self.run_assertion(f"assert_skill_position_{case}", nothing, [])
                self.assertFalse(passed)
                self.assertEqual("skill-not-loaded", details)

    def test_fact_case_passes_with_the_skill_loaded_and_the_fact(self):
        load = ("skill_load", "load1", {"Name": "netclaw-operations"})
        passed, _ = self.run_assertion(
            "assert_skill_position_middle_oauth_redirect",
            [envelope([load], "Use http://127.0.0.1:5199/api/mcp/oauth/callback")],
            [("skill_load", "load1", "text")])
        self.assertTrue(passed)
        passed, details = self.run_assertion(
            "assert_skill_position_middle_oauth_redirect",
            [envelope([load], "Use http://localhost/callback")], [("skill_load", "load1", "text")])
        self.assertFalse(passed)
        self.assertIn("fact-missing", details)

    def test_head_case_accepts_the_table_form_and_the_plain_form(self):
        load = ("skill_load", "load1", {"Name": "netclaw-operations"})
        for response in [
                "get_reminder_history; `netclaw reminder enable <id>`; delete_webhook",
                "get_reminder_history, netclaw reminder show|status|enable|delete <id>, delete_webhook"]:
            with self.subTest(response=response):
                passed, _ = self.run_assertion(
                    "assert_skill_position_head", [envelope([load], response)], [])
                self.assertTrue(passed)

    def test_a_physical_skill_file_read_fails_a_fact_case(self):
        for call in [
                ("file_read", "f1", {"Path": "/home/netclaw/.netclaw/skills/.system/netclaw-operations/SKILL.md"}),
                ("shell_execute", "s1", {
                    "Command": "grep -rn redirect /home/netclaw/.netclaw/skills/.system/netclaw-operations/"})]:
            with self.subTest(tool=call[0]):
                passed, details = self.run_assertion(
                    "assert_skill_position_tail",
                    [envelope([("skill_load", "load1", {"Name": "netclaw-operations"}), call],
                              "file_search and tool_output_read")], [])
                self.assertFalse(passed)
                self.assertEqual("physical-skill-read", details)

    def test_a_wrong_fact_is_reported_before_the_route(self):
        passed, details = self.run_assertion(
            "assert_skill_position_tail",
            [envelope([
                ("skill_load", "load1", {"Name": "netclaw-operations"}),
                ("file_read", "f1", {"Path": "/home/netclaw/.netclaw/skills/.system/netclaw-operations/SKILL.md"})],
                "file_search only")], [])
        self.assertFalse(passed)
        self.assertIn("fact-missing", details)

    def test_steer_case_reads_the_tool_calls_of_each_turn(self):
        passed, _ = self.run_assertion(
            "assert_skill_position_middle_oauth_redirect_steer",
            [envelope([("shell_execute", "s", {"Command": "pwd"})], "/work"),
             envelope([("skill_load", "l", {"Name": "netclaw-operations"}),
                       ("tool_output_read", "r", {"CallId": "l", "Start": 18000})],
                      "http://127.0.0.1:{Daemon.Port}/api/mcp/oauth/callback")], [])
        self.assertTrue(passed)

    def test_right_reference_case_rejects_an_unrelated_reference(self):
        load = ("skill_load", "load1", {"Name": "netclaw-operations"})
        right = ("skill_read_resource", "r1", {
            "SkillName": "netclaw-operations", "ResourcePath": "references/webhooks.md"})
        other = ("skill_read_resource", "r2", {
            "SkillName": "netclaw-operations", "ResourcePath": "references/tools.md"})
        passed, _ = self.run_assertion(
            "assert_skill_position_right_reference_operations",
            [envelope([load, right], "X-TextForge-Signature")], [])
        self.assertTrue(passed)
        passed, details = self.run_assertion(
            "assert_skill_position_right_reference_operations",
            [envelope([load, right, other], "X-TextForge-Signature")], [])
        self.assertFalse(passed)
        self.assertEqual("unrelated-reference-read", details)
        passed, details = self.run_assertion(
            "assert_skill_position_right_reference_operations",
            [envelope([load, other], "X-TextForge-Signature")], [])
        self.assertFalse(passed)
        self.assertIn("reference-not-read", details)

    def test_no_recall_block_case_reads_the_one_word_answer(self):
        load = ("skill_load", "load1", {"Name": "netclaw-memory"})
        passed, _ = self.run_assertion(
            "assert_skill_position_memory_no_recall_block",
            [envelope([load], "NORMAL. An absent block is the usual result.")], [])
        self.assertTrue(passed)
        passed, details = self.run_assertion(
            "assert_skill_position_memory_no_recall_block",
            [envelope([load], "BROKEN. It is not NORMAL.")], [])
        self.assertFalse(passed)
        self.assertEqual("said-broken", details)

    def test_tool_choice_case_needs_the_tool_of_each_turn(self):
        turns = [
            envelope([("skill_load", "l", {"Name": "netclaw-memory"}), ("store_memory", "s", {})]),
            envelope([("find_memories", "f", {})]),
            envelope([("update_memory", "u", {})])]
        passed, _ = self.run_assertion("assert_skill_position_memory_tool_choice", turns, [])
        self.assertTrue(passed)
        turns[2] = envelope([("update_memory", "u", {}), ("store_memory", "s2", {})])
        passed, details = self.run_assertion("assert_skill_position_memory_tool_choice", turns, [])
        self.assertFalse(passed)
        self.assertEqual("turn3-second-store_memory", details)
        turns[2] = envelope([("store_memory", "s2", {})])
        passed, details = self.run_assertion("assert_skill_position_memory_tool_choice", turns, [])
        self.assertFalse(passed)
        self.assertEqual("turn3-no-update_memory", details)

    def test_long_commit_case_rejects_the_message_inside_a_shell_command(self):
        with tempfile.TemporaryDirectory() as repo:
            def git(*arguments):
                subprocess.run(["git", "-C", repo, *arguments], check=True, capture_output=True)
            git("init", "-q", "-b", "main")
            git("config", "user.name", "Netclaw Eval")
            git("config", "user.email", "eval@netclaw.dev")
            Path(repo, "notes.txt").write_text("x\n")
            git("add", "notes.txt")
            git("commit", "-q", "-m", "Record the notes (position-eval-marker-7f3a).")
            setup = f'position_commit_message() {{ git -C "{repo}" log -1 --format=%B; }}'
            load = ("skill_load", "load1", {"Name": "netclaw-operations"})
            passed, _ = self.run_assertion(
                "assert_skill_position_middle_long_commit",
                [envelope([load, ("shell_execute", "s", {"Command": "git commit -F msg.txt"})])],
                [], setup)
            self.assertTrue(passed)
            passed, details = self.run_assertion(
                "assert_skill_position_middle_long_commit",
                [envelope([load, ("shell_execute", "s", {
                    "Command": "git commit -m 'Record the notes (position-eval-marker-7f3a).'"})])],
                [], setup)
            self.assertFalse(passed)
            self.assertEqual("long-text-inline-in-shell", details)

    def test_memory_reference_case_is_not_applicable_without_the_reference(self):
        passed, _ = self.run_assertion("applicable_skill_position_right_reference_memory", [], [])
        self.assertFalse(passed)

    def test_memory_reference_case_reads_only_the_recall_reference(self):
        load = ("skill_load", "load1", {"Name": "netclaw-memory"})
        recall = ("skill_read_resource", "r1", {
            "SkillName": "netclaw-memory", "ResourcePath": "references/recall-internals.md"})
        other = ("skill_read_resource", "r2", {
            "SkillName": "netclaw-memory", "ResourcePath": "references/diagnostics.md"})
        passed, _ = self.run_assertion(
            "assert_skill_position_right_reference_memory",
            [envelope([load, recall], "The floor is 0.24.")], [])
        self.assertTrue(passed)
        passed, details = self.run_assertion(
            "assert_skill_position_right_reference_memory",
            [envelope([load, recall, other], "The floor is 0.24.")], [])
        self.assertFalse(passed)
        self.assertEqual("unrelated-reference-read", details)


if __name__ == "__main__":
    unittest.main()
