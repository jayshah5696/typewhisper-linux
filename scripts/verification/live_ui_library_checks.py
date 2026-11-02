#!/usr/bin/env python3
"""Live isolated UI checks for Library screens: Profiles, History, Dictionary, Plugins, Shortcuts.

Runs in a hidden headless Mutter compositor with private D-Bus and AT-SPI,
using isolated throwaway test fixtures. Never touches user data.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import isolated_app  # noqa: E402
from hidden_desktop import HiddenDesktop, find_node, all_names  # noqa: E402


def type_text_steps(text: str) -> list[dict]:
    steps = []
    for c in text:
        if c == "\n":
            steps.append({"keys": [0xff0d], "wait": 0.05})
        else:
            steps.append({"keys": [ord(c)], "wait": 0.05})
    return steps


def run_checks(out_dir: Path) -> int:
    out_dir.mkdir(parents=True, exist_ok=True)
    report: dict[str, object] = {}

    # Seed data folders under out_dir/app/instance/data/TypeWhisper/Data
    app_dir = out_dir / "app"
    data_dir = app_dir / "instance" / "data" / "TypeWhisper" / "Data"
    data_dir.mkdir(parents=True, exist_ok=True)

    # 1. Seed History record
    history_record = {
        "Id": "rec-seed-1",
        "Timestamp": "2026-11-01T15:30:00Z",
        "FinalText": "Restored client notes from isolated history",
        "RawText": "restored client notes from isolated history",
        "Status": 0,
        "AppName": "Notes",
        "DurationSeconds": 4.5,
        "Language": "en"
    }
    (data_dir / "history.json").write_text(json.dumps([history_record], indent=2), encoding="utf-8")

    # 2. Seed Dictionary entry
    dict_entry = {
        "Id": "dict-seed-1",
        "Original": "Kubernetes",
        "Replacement": None,
        "EntryType": 0,
        "Priority": 2,
        "IsStarred": True,
        "IsEnabled": True
    }
    (data_dir / "dictionary.json").write_text(json.dumps([dict_entry], indent=2), encoding="utf-8")

    # 3. Seed empty profiles file
    (data_dir / "profiles.json").write_text("[]", encoding="utf-8")

    settings_override = {
        "pluginEnabledState": {
            "com.typewhisper.filler-words": False
        }
    }

    with HiddenDesktop(out_dir) as desktop:
        with isolated_app.IsolatedApp(
            app_dir,
            configuration="Release",
            minimized=False,
            settings=settings_override,
            display="none",
            extra_env=desktop.app_env()
        ) as app:
            time.sleep(4.0)

            # --- Check 1: Profiles ---
            print("[Check 1] Profiles empty state and creation...")
            desktop.accessibility(click="Profiles")
            time.sleep(1.5)
            tree = desktop.accessibility()["tree"]
            report["profiles_nav"] = "ProfilesSection" in all_names(tree)
            desktop.screenshot(out_dir / "Profiles-live-empty.png")

            # Click Create first profile
            click_res = desktop.accessibility(click="Create first profile")["clicked"]
            if not (click_res and click_res.get("ok")):
                click_res = desktop.accessibility(click="Create Profile")["clicked"]
            report["profiles_create_first_clicked"] = click_res
            time.sleep(1.5)

            # In editor, click Save
            tree = desktop.accessibility()["tree"]
            desktop.screenshot(out_dir / "Profiles-live-editor.png")
            click_save = desktop.accessibility(click="Save")["clicked"]
            report["profiles_saved_clicked"] = click_save
            time.sleep(1.5)

            tree = desktop.accessibility()["tree"]
            desktop.screenshot(out_dir / "Profiles-live-populated.png")
            saved_profiles = json.loads((data_dir / "profiles.json").read_text())
            report["profiles_item_created"] = (
                "New profile" in all_names(tree)
                and any(profile.get("Name") == "New profile" for profile in saved_profiles)
            )
            print(f"  Profiles created success: {report['profiles_item_created']}")

            # --- Check 2: History ---
            print("[Check 2] History clear-filters and restored rows...")
            desktop.accessibility(click="History")
            time.sleep(1.5)
            tree = desktop.accessibility()["tree"]
            desktop.screenshot(out_dir / "History-live-seeded.png")
            report["history_seeded_visible"] = any("Restored client notes" in n for n in all_names(tree))

            # Filter with search
            search_lbl = find_node(tree, "Search", role="label")
            if search_lbl and "extents" in search_lbl:
                lx, ly, lw, lh = search_lbl["extents"]
                desktop.input([
                    {"move": [lx + 50, ly + lh + 15]},
                    {"click": [lx + 50, ly + lh + 15]},
                ] + type_text_steps("nomatchquery\n"), persistent=True)
                time.sleep(1.0)
            tree = desktop.accessibility()["tree"]
            desktop.screenshot(out_dir / "History-live-filtered-empty.png")

            # Click Clear filters
            clear_filters = desktop.accessibility(click="Clear filters")["clicked"]
            report["history_clear_filters_clicked"] = clear_filters
            time.sleep(1.5)
            tree = desktop.accessibility()["tree"]
            desktop.screenshot(out_dir / "History-live-restored.png")
            report["history_restored"] = any("Restored client notes" in n for n in all_names(tree))
            print(f"  History clear-filters success: {clear_filters}, restored: {report['history_restored']}")

            # --- Check 3: Dictionary ---
            print("[Check 3] Dictionary clear-search and restored entry...")
            desktop.accessibility(click="Dictionary")
            time.sleep(1.5)
            tree = desktop.accessibility()["tree"]
            desktop.screenshot(out_dir / "Dictionary-live-seeded.png")
            report["dict_seeded_visible"] = "Kubernetes" in all_names(tree)

            # Filter with search
            desktop.input([
                {"move": [484, 265]},
                {"click": [484, 265]},
            ] + type_text_steps("unmatchedterm\n"), persistent=True)
            time.sleep(1.0)
            tree = desktop.accessibility()["tree"]
            desktop.screenshot(out_dir / "Dictionary-live-filtered-empty.png")

            # Click Clear search
            clear_search = desktop.accessibility(click="Clear search")["clicked"]
            report["dict_clear_search_clicked"] = clear_search
            time.sleep(1.5)
            tree = desktop.accessibility()["tree"]
            desktop.screenshot(out_dir / "Dictionary-live-restored.png")
            report["dict_restored"] = "Kubernetes" in all_names(tree)
            print(f"  Dictionary clear-search success: {clear_search}, restored: {report['dict_restored']}")

            # --- Check 4: Plugins ---
            print("[Check 4] Plugins tab and disabled filter action...")
            desktop.accessibility(click="Plugins")
            time.sleep(1.5)
            tree = desktop.accessibility()["tree"]
            desktop.screenshot(out_dir / "Plugins-live-enabled.png")

            # Switch to Disabled tab
            disabled_tab_clicked = None
            for name in all_names(tree):
                if name.startswith("Disabled"):
                    disabled_tab_clicked = desktop.accessibility(click=name)["clicked"]
                    time.sleep(1.0)
                    break

            tree = desktop.accessibility()["tree"]
            desktop.screenshot(out_dir / "Plugins-live-disabled.png")
            report["plugins_disabled_tab_clicked"] = disabled_tab_clicked
            print(f"  Plugins disabled tab success: {disabled_tab_clicked}")

            # --- Check 5: Shortcuts ---
            print("[Check 5] Shortcuts diagnostics toggle...")
            desktop.accessibility(click="Shortcuts")
            time.sleep(1.5)
            tree = desktop.accessibility()["tree"]
            desktop.screenshot(out_dir / "Shortcuts-live-collapsed.png")

            click_diag = desktop.accessibility(click="Detailed diagnostics")["clicked"]
            report["shortcuts_diagnostics_toggle_clicked"] = click_diag
            time.sleep(1.5)
            tree = desktop.accessibility()["tree"]
            desktop.screenshot(out_dir / "Shortcuts-live-expanded.png")
            report["shortcuts_diagnostics_expanded"] = "Display server" in all_names(tree) or any("Capture scope" in n for n in all_names(tree))
            print(f"  Shortcuts diagnostics toggle success: {click_diag}")

    report_path = out_dir / "live-ui-library-report.json"
    isolated_app.write_json(report_path, report)
    isolated_app.write_json(out_dir / "run.json", isolated_app.run_manifest({"kind": "live-ui-library-checks"}))
    print("Report written to:", report_path)
    failures = [name for name, value in report.items()
                if value is False or value is None
                or (isinstance(value, dict) and value.get("ok") is not True)]
    if failures:
        raise RuntimeError(f"Live UI checks failed: {', '.join(failures)}")
    return 0


if __name__ == "__main__":
    out_dir = Path("artifacts/verification/2026-11-02-ui-implementation/live-library-checks")
    sys.exit(run_checks(out_dir))
