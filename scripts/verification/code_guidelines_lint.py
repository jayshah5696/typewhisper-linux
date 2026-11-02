#!/usr/bin/env python3
"""Static lint checks for BAML-style C# coding guidelines and compiler enforcement."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from pathlib import Path
import re
import sys

REQUIRED_BUILD_ERROR_CODES = (
    "CS8509",
    "IDE0057",
    "IDE0066",
    "IDE1006",
    "CA1510",
    "CA1511",
    "CA1512",
    "CA1513",
    "CA1826",
    "CA1859",
    "CA1860",
    "CA1865",
    "CA1866",
    "CA1867",
    "CA2201",
    "SYSLIB1045",
)

REQUIRED_EDITORCONFIG_DIAGNOSTICS = (
    "CS8509",
    "IDE0057",
    "IDE0066",
    "CA1510",
    "CA1511",
    "CA1512",
    "CA1513",
    "CA1826",
    "CA1859",
    "CA1860",
    "CA1865",
    "CA1866",
    "CA1867",
    "CA2201",
    "SYSLIB1045",
)

ENUM_DECL_RE = re.compile(
    r"\benum\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?::\s*[A-Za-z0-9_]+)?\s*\{([^}]*)\}",
    re.S,
)
SWITCH_START_RE = re.compile(r"\bswitch\s*\{", re.S)
ENUM_IF_CHAIN_RE = re.compile(
    r"\bif\s*\(\s*([A-Za-z0-9_.]+)\s*==\s*([A-Za-z_][A-Za-z0-9_]*)\.[A-Za-z_][A-Za-z0-9_]*\s*\)"
    r"\s*(?:\{[^{}]*\}|[^{;\n]+;)\s*else\s+if\s*\(\s*\1\s*==\s*\2\.[A-Za-z_][A-Za-z0-9_]*\s*\)",
    re.S,
)
RAW_DELAY_RE = re.compile(
    r"\b(?:Task\.Delay|Thread\.Sleep|CancelAfter)\s*\(\s*([0-9][0-9_]*)\s*(?:,|\))"
)
GENERIC_EXCEPTION_RE = re.compile(
    r"\bthrow\s+new\s+(?:System\.)?(Exception|ApplicationException|SystemException)\s*\("
)
STATIC_REGEX_LITERAL_RE = re.compile(
    r"\b(?:new\s+Regex|Regex\.(?:IsMatch|Match|Matches|Replace|Split))\s*\("
    r"(?:[^,;\n()]*?,\s*)?(?:@\"(?:[^\"]|\"\")*\"|\"(?:\\.|[^\"\\])*\")\s*(?:,|\))"
)


@dataclass(frozen=True)
class LintFinding:
    path: str
    line: int
    rule: str
    message: str

    def __str__(self) -> str:
        return f"{self.path}:{self.line}: {self.rule}: {self.message}"


def _strip_comments(source: str) -> str:
    def replace_keep_newlines(match: re.Match[str]) -> str:
        text = match.group(0)
        if text.startswith(("\"", "@\"", "$\"", "$@\"", "@$\"")):
            return text
        return "".join("\n" if ch == "\n" else " " for ch in text)

    pattern = re.compile(
        r"@\"(?:[^\"]|\"\")*\"|\"(?:\\.|[^\"\\])*\"|//[^\n]*|/\*.*?\*/",
        re.S,
    )
    return pattern.sub(replace_keep_newlines, source)


def _iter_source_files(root: Path) -> list[Path]:
    files: list[Path] = []
    for folder in ("src", "plugins"):
        base = root / folder
        if not base.exists():
            continue
        for path in sorted(base.rglob("*.cs")):
            parts = set(path.parts)
            if "bin" in parts or "obj" in parts:
                continue
            files.append(path)
    return files


def discover_enums(files: list[Path]) -> dict[str, set[str]]:
    enums: dict[str, set[str]] = {}
    for path in files:
        cleaned = _strip_comments(path.read_text(encoding="utf-8"))
        for match in ENUM_DECL_RE.finditer(cleaned):
            enum_name = match.group(1)
            body = match.group(2)
            members: list[str] = []
            for raw_item in body.split(","):
                item = raw_item.strip()
                if not item:
                    continue
                member = item.split("=")[0].strip()
                member = re.sub(r"\[.*?\]\s*", "", member).strip()
                if re.match(r"^[A-Za-z_][A-Za-z0-9_]*$", member):
                    members.append(member)
            if len(members) >= 2:
                enums[enum_name] = set(members)
    return enums


def _has_suppression(lines: list[str], line_number: int, tokens: tuple[str, ...]) -> bool:
    start = max(0, line_number - 4)
    end = min(len(lines), line_number + 1)
    window = "\n".join(lines[start:end])
    return any(token in window for token in tokens)


def _check_switch_expressions(
    rel_path: str,
    raw: str,
    cleaned: str,
    lines: list[str],
    enums: dict[str, set[str]],
) -> list[LintFinding]:
    findings: list[LintFinding] = []
    for match in SWITCH_START_RE.finditer(cleaned):
        start = match.start()
        depth = 0
        end = None
        for idx in range(match.end() - 1, len(cleaned)):
            ch = cleaned[idx]
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
                if depth == 0:
                    end = idx + 1
                    break
        if end is None:
            continue

        block = cleaned[start:end]
        if len(SWITCH_START_RE.findall(block)) > 1:
            continue

        patterns = re.findall(r"(?:^|\{|,)\s*([^,=]+?)\s*=>", block, flags=re.S)
        by_enum: dict[str, set[str]] = {}
        for pat in patterns:
            if re.search(r"\bwhen\b", pat):
                continue
            for enum_name, member in re.findall(
                r"\b([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)\b", pat
            ):
                if enum_name in enums and member in enums[enum_name]:
                    by_enum.setdefault(enum_name, set()).add(member)

        if len(by_enum) != 1:
            continue

        enum_name, matched_members = next(iter(by_enum.items()))
        missing = enums[enum_name] - matched_members
        has_non_throwing_discard = re.search(
            r"(?:^|\{|,)\s*_\s*=>\s*(?!throw\b)", block, flags=re.S
        )
        if has_non_throwing_discard and len(missing) == 1:
            line_no = raw[:start].count("\n") + 1
            if _has_suppression(
                lines,
                line_no,
                (
                    "BAML001",
                    "IDE0072",
                    "SwitchExpressionHandlesSomeKnownEnumValues",
                ),
            ):
                continue
            missing_member = sorted(missing)[0]
            findings.append(
                LintFinding(
                    path=rel_path,
                    line=line_no,
                    rule="BAML001",
                    message=(
                        f"Switch on enum '{enum_name}' hides '{enum_name}.{missing_member}' "
                        f"in a non-throwing '_ =>' arm. Name '{enum_name}.{missing_member}' "
                        "explicitly so adding a new enum variant cannot silently fall through."
                    ),
                )
            )
    return findings


def _check_enum_if_chains(
    rel_path: str,
    raw: str,
    cleaned: str,
    lines: list[str],
    enums: dict[str, set[str]],
) -> list[LintFinding]:
    findings: list[LintFinding] = []
    for match in ENUM_IF_CHAIN_RE.finditer(cleaned):
        enum_name = match.group(2)
        if enum_name not in enums:
            continue
        line_no = raw[: match.start()].count("\n") + 1
        if _has_suppression(
            lines,
            line_no,
            ("BAML001", "ConvertIfStatementToSwitchStatement", "ConvertIfStatementToSwitchExpression"),
        ):
            continue
        findings.append(
            LintFinding(
                path=rel_path,
                line=line_no,
                rule="BAML001",
                message=(
                    f"Use a switch expression or statement instead of an if/else-if chain "
                    f"on enum '{enum_name}'."
                ),
            )
        )
    return findings


def _check_file_rules(
    root: Path, path: Path, enums: dict[str, set[str]]
) -> list[LintFinding]:
    rel_path = path.relative_to(root).as_posix()
    raw = path.read_text(encoding="utf-8")
    cleaned = _strip_comments(raw)
    lines = raw.splitlines()
    findings: list[LintFinding] = []

    findings.extend(_check_switch_expressions(rel_path, raw, cleaned, lines, enums))
    findings.extend(_check_enum_if_chains(rel_path, raw, cleaned, lines, enums))

    for match in RAW_DELAY_RE.finditer(cleaned):
        line_no = raw[: match.start()].count("\n") + 1
        if _has_suppression(lines, line_no, ("BAML002",)):
            continue
        findings.append(
            LintFinding(
                path=rel_path,
                line=line_no,
                rule="BAML002",
                message=(
                    "Pass an explicit TimeSpan (such as TimeSpan.FromMilliseconds or "
                    "TimeSpan.FromSeconds) instead of a raw integer millisecond literal."
                ),
            )
        )

    for match in GENERIC_EXCEPTION_RE.finditer(cleaned):
        line_no = raw[: match.start()].count("\n") + 1
        if _has_suppression(lines, line_no, ("BAML003", "CA2201")):
            continue
        exc_type = match.group(1)
        findings.append(
            LintFinding(
                path=rel_path,
                line=line_no,
                rule="BAML003",
                message=(
                    f"Do not throw generic '{exc_type}'. Throw a specific exception "
                    "type carrying the relevant domain or argument context."
                ),
            )
        )

    for match in STATIC_REGEX_LITERAL_RE.finditer(cleaned):
        line_no = raw[: match.start()].count("\n") + 1
        if _has_suppression(lines, line_no, ("BAML004", "SYSLIB1045")):
            continue
        findings.append(
            LintFinding(
                path=rel_path,
                line=line_no,
                rule="BAML004",
                message=(
                    "Use a compile-time [GeneratedRegex] method with a descriptive name "
                    "instead of a runtime Regex with a constant string literal."
                ),
            )
        )

    return findings


def _check_build_configuration(root: Path) -> list[LintFinding]:
    findings: list[LintFinding] = []
    build_props = root / "Directory.Build.props"
    if not build_props.exists():
        findings.append(
            LintFinding(
                path="Directory.Build.props",
                line=1,
                rule="BAML005",
                message="Missing Directory.Build.props at repository root.",
            )
        )
    else:
        props_text = build_props.read_text(encoding="utf-8")
        if "<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>" not in props_text:
            findings.append(
                LintFinding(
                    path="Directory.Build.props",
                    line=1,
                    rule="BAML005",
                    message="Directory.Build.props must set <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>.",
                )
            )
        for code in REQUIRED_BUILD_ERROR_CODES:
            if not re.search(rf"\b{re.escape(code)}\b", props_text):
                findings.append(
                    LintFinding(
                        path="Directory.Build.props",
                        line=1,
                        rule="BAML005",
                        message=f"Directory.Build.props must include '{code}' in <WarningsAsErrors>.",
                    )
                )

    editorconfig = root / ".editorconfig"
    if not editorconfig.exists():
        findings.append(
            LintFinding(
                path=".editorconfig",
                line=1,
                rule="BAML005",
                message="Missing .editorconfig at repository root.",
            )
        )
    else:
        ec_text = editorconfig.read_text(encoding="utf-8")
        for code in REQUIRED_EDITORCONFIG_DIAGNOSTICS:
            pattern = re.compile(
                rf"^\s*dotnet_diagnostic\.{re.escape(code)}\.severity\s*=\s*(warning|error)\b",
                re.I | re.M,
            )
            if not pattern.search(ec_text):
                findings.append(
                    LintFinding(
                        path=".editorconfig",
                        line=1,
                        rule="BAML005",
                        message=(
                            f".editorconfig must configure 'dotnet_diagnostic.{code}.severity' "
                            "as 'warning' or 'error'."
                        ),
                    )
                )
    return findings


def lint_repository(root: Path) -> list[LintFinding]:
    findings = _check_build_configuration(root)
    files = _iter_source_files(root)
    enums = discover_enums(files)
    for path in files:
        findings.extend(_check_file_rules(root, path, enums))
    return findings


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--root",
        type=Path,
        default=Path(__file__).resolve().parents[2],
        help="Repository root directory.",
    )
    args = parser.parse_args(argv)
    findings = lint_repository(args.root.resolve())
    if findings:
        for finding in findings:
            print(finding, file=sys.stderr)
        print(f"\nFound {len(findings)} code guideline violation(s).", file=sys.stderr)
        return 1
    print("All code guideline checks passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
