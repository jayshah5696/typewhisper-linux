#!/usr/bin/env python3
"""Generate high-resolution side-by-side comparison contact sheets for changed screens and states."""

from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
BEFORE_DIR = ROOT / "artifacts/verification/2026-11-02-ui-implementation/before"
AFTER_DIR = ROOT / "artifacts/verification/2026-11-02-ui-implementation/final-render"
LIVE_DIR = ROOT / "artifacts/verification/2026-11-02-ui-implementation/live-library-checks"
OUT_DIR = ROOT / "artifacts/verification/2026-11-02-ui-implementation/contact-sheets"

OUT_DIR.mkdir(parents=True, exist_ok=True)

try:
    font_large = ImageFont.truetype("/usr/share/fonts/TTF/DejaVuSans-Bold.ttf", 24)
    font_sub = ImageFont.truetype("/usr/share/fonts/TTF/DejaVuSans.ttf", 16)
except Exception:
    font_large = ImageFont.load_default()
    font_sub = ImageFont.load_default()


def create_side_by_side(title: str, left_path: Path, left_label: str, right_path: Path, right_label: str, out_path: Path):
    im_left = Image.open(left_path).convert("RGB")
    im_right = Image.open(right_path).convert("RGB")

    w1, h1 = im_left.size
    w2, h2 = im_right.size

    target_h = max(h1, h2)
    # Scale if heights differ
    if h1 != target_h:
        im_left = im_left.resize((int(w1 * target_h / h1), target_h), Image.Resampling.LANCZOS)
    if h2 != target_h:
        im_right = im_right.resize((int(w2 * target_h / h2), target_h), Image.Resampling.LANCZOS)

    w1, h1 = im_left.size
    w2, h2 = im_right.size

    header_h = 70
    gap = 20
    border = 20
    total_w = border * 2 + w1 + gap + w2
    total_h = border * 2 + header_h + target_h

    canvas = Image.new("RGB", (total_w, total_h), color=(24, 24, 27))  # dark slate
    draw = ImageDraw.Draw(canvas)

    # Title
    draw.text((border, border), title, fill=(244, 244, 245), font=font_large)

    # Column labels
    col1_x = border
    col2_x = border + w1 + gap
    labels_y = border + 36

    draw.text((col1_x, labels_y), left_label, fill=(248, 113, 113), font=font_sub)   # soft red
    draw.text((col2_x, labels_y), right_label, fill=(52, 211, 153), font=font_sub)  # emerald green

    # Paste images
    images_y = border + header_h
    canvas.paste(im_left, (col1_x, images_y))
    canvas.paste(im_right, (col2_x, images_y))

    canvas.save(out_path)
    print(f"Generated: {out_path.name} ({total_w}x{total_h})")


def create_three_way(title: str, imgs: list[tuple[Path, str]], out_path: Path):
    pil_imgs = [Image.open(p).convert("RGB") for p, _ in imgs]
    target_h = max(im.height for im in pil_imgs)
    resized = [im.resize((int(im.width * target_h / im.height), target_h), Image.Resampling.LANCZOS) for im in pil_imgs]

    header_h = 70
    gap = 16
    border = 20
    total_w = border * 2 + sum(im.width for im in resized) + gap * (len(resized) - 1)
    total_h = border * 2 + header_h + target_h

    canvas = Image.new("RGB", (total_w, total_h), color=(24, 24, 27))
    draw = ImageDraw.Draw(canvas)

    draw.text((border, border), title, fill=(244, 244, 245), font=font_large)

    cur_x = border
    labels_y = border + 36
    images_y = border + header_h

    colors = [(56, 189, 248), (251, 191, 36), (52, 211, 153)]
    for i, ((_, label), im) in enumerate(zip(imgs, resized)):
        draw.text((cur_x, labels_y), label, fill=colors[i % len(colors)], font=font_sub)
        canvas.paste(im, (cur_x, images_y))
        cur_x += im.width + gap

    canvas.save(out_path)
    print(f"Generated: {out_path.name} ({total_w}x{total_h})")


def main():
    create_side_by_side(
        "Dashboard and shell - Before and after",
        BEFORE_DIR / "Dashboard-en-dark-wide.png", "BEFORE: Original dashboard and navigation",
        AFTER_DIR / "Dashboard-en-dark-wide.png", "AFTER: Readiness, recent activity, expandable statistics",
        OUT_DIR / "Dashboard-comparison.png"
    )
    # 1. Five Main Screen Before vs After Comparisons
    create_side_by_side(
        "History Screen - Baseline vs After Implementation",
        BEFORE_DIR / "History-en-dark-wide.png",
        "BEFORE: Original empty history",
        AFTER_DIR / "History-en-dark-wide.png",
        "AFTER: FluentIcons SymbolIcon, Actionable Start Dictation & File Transcription Navigation",
        OUT_DIR / "History-comparison.png"
    )

    create_side_by_side(
        "Plugins Screen - Baseline vs After Implementation",
        BEFORE_DIR / "Plugins-en-dark-wide.png",
        "BEFORE: Original Plugins Screen",
        AFTER_DIR / "Plugins-en-dark-wide.png",
        "AFTER: Clean Filter State, FluentIcons Icons, Working Disabled Tab",
        OUT_DIR / "Plugins-comparison.png"
    )

    create_side_by_side(
        "Profiles Screen - Baseline vs After Implementation",
        BEFORE_DIR / "Profiles-en-dark-wide.png",
        "BEFORE: Original empty profile editor",
        AFTER_DIR / "Profiles-en-dark-wide.png",
        "AFTER: FluentIcons Person SymbolIcon, Actionable 'Create first profile'",
        OUT_DIR / "Profiles-comparison.png"
    )

    create_side_by_side(
        "Dictionary Screen - Baseline vs After Implementation",
        BEFORE_DIR / "Dictionary-en-dark-wide.png",
        "BEFORE: Original (Unicode Glyph in Empty State)",
        AFTER_DIR / "Dictionary-en-dark-wide.png",
        "AFTER: FluentIcons Book SymbolIcon, Clean Search State & Actionable Clear Search",
        OUT_DIR / "Dictionary-comparison.png"
    )

    create_side_by_side(
        "Shortcuts Screen - Baseline vs After Implementation",
        BEFORE_DIR / "Shortcuts-en-dark-wide.png",
        "BEFORE: Original Shortcuts Screen",
        AFTER_DIR / "Shortcuts-en-dark-wide.png",
        "AFTER: Status Message Banner Visible with Main Action Even When Diagnostics Collapsed",
        OUT_DIR / "Shortcuts-comparison.png"
    )

    # 2. Live Tested Behavior Contact Sheets
    if (LIVE_DIR / "History-live-seeded.png").exists():
        create_three_way(
            "History Live Search & Clear Filters Lifecycle (Isolated Seeded Data)",
            [
                (LIVE_DIR / "History-live-seeded.png", "1. Seeded Isolated History Row Visible"),
                (LIVE_DIR / "History-live-filtered-empty.png", "2. Filtered Empty with Search Query + 'Clear filters' Button"),
                (LIVE_DIR / "History-live-restored.png", "3. After Clicking 'Clear filters': History Row Restored"),
            ],
            OUT_DIR / "Live-States-History-Filter.png"
        )

    if (LIVE_DIR / "Dictionary-live-seeded.png").exists():
        create_three_way(
            "Dictionary Live Search & Clear Search Lifecycle (Isolated Seeded Entry)",
            [
                (LIVE_DIR / "Dictionary-live-seeded.png", "1. Seeded Dictionary Entry ('Kubernetes') Visible"),
                (LIVE_DIR / "Dictionary-live-filtered-empty.png", "2. Filtered Empty with Search Query + 'Clear search' Button"),
                (LIVE_DIR / "Dictionary-live-restored.png", "3. After Clicking 'Clear search': Entry Restored"),
            ],
            OUT_DIR / "Live-States-Dictionary-Search.png"
        )

    if (LIVE_DIR / "Profiles-live-empty.png").exists():
        create_side_by_side(
            "Profiles Live Creation Lifecycle (Isolated Test Fixture)",
            LIVE_DIR / "Profiles-live-empty.png",
            "1. Empty Profiles Screen with FluentIcons Person & 'Create first profile'",
            LIVE_DIR / "Profiles-live-populated.png",
            "2. After Creation & Save: Profile Saved and Present in Populated List",
            OUT_DIR / "Live-States-Profiles.png"
        )

    if (LIVE_DIR / "Shortcuts-live-collapsed.png").exists():
        create_side_by_side(
            "Shortcuts Diagnostics Expansion (Live Mutter Session)",
            LIVE_DIR / "Shortcuts-live-collapsed.png",
            "1. Diagnostics Collapsed (Main Action & Status Visible)",
            LIVE_DIR / "Shortcuts-live-expanded.png",
            "2. Diagnostics Expanded (Display Server & Capture Diagnostics Visible)",
            OUT_DIR / "Live-States-Shortcuts-Diagnostics.png"
        )

    if (LIVE_DIR / "Plugins-live-enabled.png").exists():
        create_side_by_side(
            "Plugins Tabs Lifecycle (Isolated Fixture with Disabled Plugin)",
            LIVE_DIR / "Plugins-live-enabled.png",
            "1. Enabled Tab (Active Extensions)",
            LIVE_DIR / "Plugins-live-disabled.png",
            "2. Disabled Tab (Disabled Plugins Filter State)",
            OUT_DIR / "Live-States-Plugins-Tabs.png"
        )

    print("All comparison contact sheets successfully generated in", OUT_DIR)


if __name__ == "__main__":
    main()
