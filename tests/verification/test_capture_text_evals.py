import importlib.util
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).parents[2] / "scripts/verification"))
spec = importlib.util.spec_from_file_location("capture", Path(__file__).parents[2] / "scripts/verification/capture_text_evals.py")
capture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture)

CASE = {"id": "facts-01", "input": "do not delete the backup", "language": "en", "profile": "email"}


class ResultRowTests(unittest.TestCase):
    def test_actual_light_level_with_requested_medium_is_a_fallback_error(self):
        response = {"text": "Do not delete the backup", "cleanup_level": "Light", "llm_calls": [],
                    "cleanup_outcome": {"requested_level": "Medium", "applied_level": "Light", "fallback_reason": "TimedOut"}}
        row = capture.result_row(CASE, response, None, 3.0, "fake")
        self.assertEqual("error", row["status"])
        self.assertIn("TimedOut", row["error"])
        self.assertEqual(response["cleanup_outcome"], row["metadata"]["cleanup_outcome"])

    def test_provider_canary_waits_for_requested_ai_even_when_actual_level_is_light(self):
        class App:
            calls = 0

            def api(self, _path, _body):
                self.calls += 1
                if self.calls == 1:
                    return {"cleanup_level": "Light", "cleanup_outcome": {"requested_level": "Medium", "fallback_reason": "ProviderUnavailable"}}
                return {"cleanup_level": "Medium", "cleanup_outcome": {"requested_level": "Medium", "fallback_reason": None},
                        "llm_calls": [{"stage": "Cleanup", "succeeded": True}]}

        app = App()
        capture.wait_for_cleanup_provider(app, timeout=2)
        self.assertEqual(2, app.calls)

    def test_failed_request_is_an_error_row_never_a_completed_one(self):
        row = capture.result_row(CASE, None, "HTTP 502: provider failed", 12.0, "fake")
        self.assertEqual("error", row["status"])
        self.assertIsNone(row["text"])
        self.assertEqual("do not delete the backup", row["raw_text"])

    def test_llm_cleanup_level_without_an_llm_call_is_a_silent_fallback_error(self):
        # LlmCleanupService returns Light-cleaned text when the provider is unavailable.
        response = {"text": "Do not delete the backup", "cleanup_level": "Medium", "steps": [], "llm_calls": []}
        row = capture.result_row(CASE, response, None, 3.0, "fake")
        self.assertEqual("error", row["status"])
        self.assertIsNone(row["text"])
        self.assertIn("fell back", row["error"])

    def test_an_attempted_but_failed_cleanup_call_is_still_a_fallback(self):
        # llm_calls[].succeeded is true only for a nonempty completed response.
        response = {"text": "Do not delete the backup", "cleanup_level": "Medium", "steps": [],
                    "llm_calls": [{"stage": "Cleanup", "provider": "p", "model": "m", "succeeded": False}]}
        row = capture.result_row(CASE, response, None, 3.0, "fake")
        self.assertEqual("error", row["status"])

    def test_light_cleanup_needs_no_llm_call(self):
        response = {"text": "Do not delete the backup.", "cleanup_level": "Light", "steps": [], "llm_calls": []}
        self.assertEqual("completed", capture.result_row(CASE, response, None, 3.0, "fake")["status"])

    def test_cleanup_provider_comes_from_the_cleanup_call_and_profile_is_not_claimed(self):
        response = {"text": "Do not delete the backup.", "cleanup_level": "Medium", "steps": [],
                    "llm_calls": [{"stage": "Memory", "provider": "x", "model": "y"},
                                  {"stage": "Cleanup", "provider": "openai-compatible", "model": "fixture"}]}
        row = capture.result_row(CASE, response, None, 30.0, "fake")
        self.assertEqual("completed", row["status"])
        self.assertEqual("openai-compatible", row["metadata"]["cleanup_provider"])
        self.assertEqual("fixture", row["metadata"]["cleanup_model"])
        self.assertFalse(row["metadata"]["profile_applied"])


if __name__ == "__main__":
    unittest.main()
