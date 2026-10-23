"""Public audio through draft reuse, disabled drafts, and the fork's final wait limit."""
import json
import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path.cwd() / "scripts/verification"))
from hidden_desktop import HiddenDesktop
from isolated_app import FakeLlm, IsolatedApp, run_manifest, write_json
from live_smoke import dictate_once, session_checks
from recovery_smoke import X11Clipboard, private_x11_env

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--out", type=Path, default=Path(__file__).resolve().parent / "live-cleanup")
parser.add_argument("--cases", nargs="+", choices=["reused", "disabled", "deadline", "unlimited"],
                    default=["reused", "disabled", "deadline", "unlimited"])
args = parser.parse_args()
out = args.out.resolve()
if out.exists():
    raise RuntimeError("Choose a fresh evidence directory")
audio = Path("tests/fixtures/librispeech-1462-170145-0012.wav").resolve()
results = []
with HiddenDesktop(out / "desktop") as desktop:
    for name, live, delay, tail in [("reused", True, .25, 4), ("disabled", False, .25, .3),
                                   ("deadline", True, 6, 4), ("unlimited", False, 5.2, .3)]:
        if name not in args.cases:
            continue
        with FakeLlm(delay) as llm:
            settings, provider = llm.plugin_overrides()
            settings.update(selectedModelId="plugin:com.typewhisper.sherpa-onnx:parakeet-tdt-0.6b",
                            cleanupLevel=2, cleanupTimeoutSeconds=0 if name == "unlimited" else 1,
                            liveTranscriptionEnabled=True, liveCleanupEnabled=live, saveToHistoryEnabled=True)
            settings["pluginEnabledState"]["com.typewhisper.filler-words"] = False
            with IsolatedApp(out / name, configuration="Release", settings=settings,
                             plugin_data=["com.typewhisper.sherpa-onnx"],
                             plugin_settings={"com.typewhisper.openai-compatible": provider},
                             virtual_mic=True, display="none", extra_env=private_x11_env(desktop.app_env())) as app:
                app.load_model("sherpa-onnx", "parakeet-tdt-0.6b")
                session = dictate_once(app, llm, X11Clipboard(app.env), audio, tail=tail)
                checks = session_checks(session, live_cleanup=live)
                result = session["result"]
                performance = result["performance"]
                records = json.loads((app.instance / "data/TypeWhisper/Data/history.json").read_text())
                record = next(item for item in records if item["RawText"] == result["raw_text"])
                if name == "reused":
                    checks["matching_draft_reused"] = performance["live_cleanup_reused"]
                    checks["one_provider_request"] = session["llm_requests_total"] == 1
                elif name == "disabled":
                    checks["no_speculative_request"] = session["llm_requests_before_stop"] == 0
                    checks["one_final_request"] = session["llm_requests_total"] == 1
                elif name == "deadline":
                    checks["timeout_reported_in_history"] = record["CleanupOutcome"]["FallbackReason"] == 1
                    checks["one_second_final_budget_bounded"] = performance["processing_milliseconds"]["Cleanup"] < 1500
                else:
                    checks["zero_limit_waited_for_provider"] = performance["processing_milliseconds"]["Cleanup"] > 5000
                    checks["ai_cleanup_succeeded"] = record["CleanupOutcome"] == {
                        "RequestedLevel": 2, "AppliedLevel": 2, "FallbackReason": None}
                row = {"case": name, "checks": checks, "session": session,
                       "history_cleanup_outcome": record["CleanupOutcome"],
                       "backend": app.api("/v1/status").get("acceleration")}
                results.append(row)
                write_json(out / "results.json", results)
                assert all(checks.values()), row
write_json(out / "run.json", run_manifest({"kind": "live-cleanup-integration", "audio": str(audio),
                                           "provider": "local fake echo", "clipboard": "private X11"}))
print(json.dumps([{ "case": row["case"], "checks": row["checks"],
                   "performance": row["session"]["result"]["performance"]} for row in results], indent=2))
