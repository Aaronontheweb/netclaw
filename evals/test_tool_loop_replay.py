#!/usr/bin/env python3
"""Tests for the sanitized beta.3 tool-loop replay harness."""

from __future__ import annotations

import copy
import json
import unittest
from pathlib import Path

from tool_loop_replay import (
    BASELINE_SOURCE_SHA256,
    BASELINE_SOURCE_PATH,
    FixtureError,
    extract_sanitized_case,
    load_fixture,
    replay_fixture,
    verify_baseline_source,
)


FIXTURE = Path(__file__).parent / "fixtures" / "tool-loop" / "corpus.json"


class ToolLoopReplayTests(unittest.TestCase):
    def setUp(self) -> None:
        self.fixture = load_fixture(FIXTURE)

    def test_corpus_replays_all_cases(self) -> None:
        metrics = replay_fixture(self.fixture)
        self.assertTrue(metrics["summary"]["all_passed"])
        self.assertEqual(12, metrics["summary"]["case_count"])
        self.assertEqual(12, metrics["summary"]["passed_count"])

    def test_historical_invalid_loop_case_records_45_events(self) -> None:
        case = next(item for item in self.fixture["cases"] if item["id"] == "incident_a")
        result = replay_fixture({**self.fixture, "cases": [case]})["cases"][0]
        self.assertEqual(4, result["completed_event_count"])
        self.assertEqual("correct", result["decisions"][-1])
        self.assertEqual(45, case["historical_request_count"])
        self.assertEqual(43, case["historical_exact_suffix_count"])
        self.assertEqual(19, result["first_intervention_request_index"])
        self.assertTrue(all(event["calls"][0]["argument_shape"] == "argument_class_repeat" for event in case["events"][2:4]))
        self.assertTrue(all(event["calls"][0]["result_shape"] == "result_class_repeat" for event in case["events"][2:4]))

    def test_exact_loop_uses_correction_then_stop(self) -> None:
        case = next(item for item in self.fixture["cases"] if item["id"] == "control_a")
        result = replay_fixture({**self.fixture, "cases": [case]})["cases"][0]
        self.assertEqual(["execute", "execute", "correct", "stop"], result["decisions"])

    def test_changed_result_and_argument_controls_execute(self) -> None:
        metrics = replay_fixture(self.fixture)
        by_id = {case["id"]: case for case in metrics["cases"]}
        self.assertEqual("execute", by_id["control_b"]["decisions"][-1])
        self.assertEqual("execute", by_id["control_c"]["decisions"][-1])
        self.assertEqual("execute", by_id["control_d"]["decisions"][-1])

    def test_period_parallel_eviction_and_lifecycle_vectors(self) -> None:
        by_id = {case["id"]: case for case in replay_fixture(self.fixture)["cases"]}
        self.assertEqual("correct", by_id["control_e"]["decisions"][-1])
        self.assertEqual("correct", by_id["control_f"]["decisions"][-1])
        self.assertEqual("execute", by_id["control_g"]["decisions"][-1])
        self.assertEqual("correct", by_id["control_h"]["decisions"][-1])
        self.assertEqual("execute", by_id["control_i"]["decisions"][-1])
        self.assertEqual("correct", by_id["control_j"]["decisions"][-1])
        self.assertEqual("execute", by_id["control_k"]["decisions"][-1])

    def test_schema_rejects_unknown_members(self) -> None:
        for location in ("root", "event"):
            malformed = copy.deepcopy(self.fixture)
            if location == "root":
                malformed["unexpected"] = True
            else:
                malformed["cases"][0]["events"][0]["unexpected"] = True
            with self.subTest(location=location), self.assertRaises(FixtureError):
                replay_fixture(malformed)

    def test_extractor_rejects_raw_fields_and_values(self) -> None:
        observation = {
            "kind": "candidate",
            "calls": [{"tool": "tool_a", "argument_shape": "argument_class_query"}],
            "expected_decision": "execute",
        }
        extracted = extract_sanitized_case("control_safe", "group_safe", "train", "productive", [observation])
        self.assertEqual("control_safe", extracted["id"])
        for field in ("prompt", "arguments", "path", "result", "call_id"):
            with self.subTest(field=field):
                raw = dict(observation)
                raw[field] = "private-value"
                with self.assertRaises(FixtureError):
                    extract_sanitized_case("control_unsafe", "group_safe", "train", "productive", [raw])
        raw_value = dict(observation)
        for value in ("private_user", "host_alias", "session_token", "secret_value", "channel_marker"):
            with self.subTest(value=value):
                raw_value["calls"] = [{"tool": "tool_a", "argument_shape": "argument_class_" + value}]
                with self.assertRaises(FixtureError):
                    extract_sanitized_case("control_unsafe", "group_safe", "train", "productive", [raw_value])

    def test_fixture_contains_no_infrastructure_or_private_markers(self) -> None:
        text = FIXTURE.read_text(encoding="utf-8")
        for marker in ("private-host", "private-user", "/home/", "https://", "@", "D1234567890"):
            with self.subTest(marker=marker):
                self.assertNotIn(marker, text)

    def test_split_rejects_session_group_leakage(self) -> None:
        malformed = copy.deepcopy(self.fixture)
        malformed["cases"][1]["session_group"] = malformed["cases"][0]["session_group"]
        malformed["cases"][1]["split"] = "holdout"
        with self.assertRaises(FixtureError):
            replay_fixture(malformed)

    def test_split_allows_multiple_cases_from_one_session_group(self) -> None:
        accepted = copy.deepcopy(self.fixture)
        accepted["cases"][1]["session_group"] = accepted["cases"][0]["session_group"]
        accepted["cases"][1]["split"] = "train"
        self.assertTrue(replay_fixture(accepted)["summary"]["all_passed"])

    def test_metrics_are_deterministic_and_machine_readable(self) -> None:
        first = json.dumps(replay_fixture(self.fixture), sort_keys=True, separators=(",", ":"))
        second = json.dumps(replay_fixture(load_fixture(FIXTURE)), sort_keys=True, separators=(",", ":"))
        self.assertEqual(first, second)
        decoded = json.loads(first)
        self.assertEqual("0.27.0-beta.3", decoded["baseline"]["release"])

    def test_locked_source_fingerprint_matches_detector_source(self) -> None:
        verify_baseline_source(FIXTURE.parents[3])
        self.assertEqual(BASELINE_SOURCE_PATH, self.fixture["baseline"]["source_path"])
        self.assertEqual(BASELINE_SOURCE_SHA256, self.fixture["baseline"]["source_sha256"])


if __name__ == "__main__":
    unittest.main()
