import importlib.util
from pathlib import Path
import sys
import tempfile
import textwrap
import unittest

SCRIPT_PATH = Path(__file__).parents[2] / "scripts/verification/code_guidelines_lint.py"


def load_linter():
    spec = importlib.util.spec_from_file_location("code_guidelines_lint", SCRIPT_PATH)
    if spec is None or spec.loader is None:
        raise FileNotFoundError(f"Missing linter module at {SCRIPT_PATH}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def write_text(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(textwrap.dedent(content).lstrip(), encoding="utf-8")


def write_valid_build_config(root: Path) -> None:
    linter = load_linter()
    codes = ";".join(linter.REQUIRED_BUILD_ERROR_CODES)
    write_text(
        root / "Directory.Build.props",
        f"""
        <Project>
            <PropertyGroup>
                <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
                <WarningsAsErrors>$(WarningsAsErrors);{codes}</WarningsAsErrors>
            </PropertyGroup>
        </Project>
        """,
    )
    ec_lines = ["root = true", "[*.cs]"]
    for code in linter.REQUIRED_EDITORCONFIG_DIAGNOSTICS:
        sev = "error" if code == "CS8509" else "warning"
        ec_lines.append(f"dotnet_diagnostic.{code}.severity = {sev}")
    write_text(root / ".editorconfig", "\n".join(ec_lines) + "\n")


class CodeGuidelinesLintTests(unittest.TestCase):
    def test_flags_switch_expression_hiding_single_or_multiple_enum_variants_in_discard(self):
        linter = load_linter()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write_valid_build_config(root)
            write_text(
                root / "src/Sample/Color.cs",
                """
                namespace Sample;
                public enum Color
                {
                    Red,
                    Green,
                    Blue,
                    Yellow,
                }
                """,
            )
            write_text(
                root / "src/Sample/Hex.cs",
                """
                namespace Sample;
                public static class Hex
                {
                    public static string Format(Color c) => c switch
                    {
                        Color.Red => "#f00",
                        Color.Green => "#0f0",
                        _ => "#00f",
                    };
                }
                """,
            )

            findings = linter.lint_repository(root)
            codes = [f.rule for f in findings]
            self.assertIn("BAML001", codes)
            self.assertTrue(any("Color.Blue" in f.message and "Color.Yellow" in f.message for f in findings))

    def test_accepts_exhaustive_switch_expression_naming_all_enum_variants(self):
        linter = load_linter()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write_valid_build_config(root)
            write_text(
                root / "src/Sample/Color.cs",
                """
                namespace Sample;
                public enum Color
                {
                    Red,
                    Green,
                    Blue,
                }
                """,
            )
            write_text(
                root / "src/Sample/Hex.cs",
                """
                namespace Sample;
                public static class Hex
                {
                    public static string Format(Color c) => c switch
                    {
                        Color.Red => "#f00",
                        Color.Green => "#0f0",
                        Color.Blue or _ => "#00f",
                    };
                }
                """,
            )

            self.assertEqual([], linter.lint_repository(root))

    def test_flags_switch_statement_missing_enum_variants(self):
        linter = load_linter()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write_valid_build_config(root)
            write_text(
                root / "src/Sample/Color.cs",
                """
                namespace Sample;
                public enum Color
                {
                    Red,
                    Green,
                    Blue,
                }
                """,
            )
            write_text(
                root / "src/Sample/Printer.cs",
                """
                namespace Sample;
                public static class Printer
                {
                    public static string Describe(Color c)
                    {
                        switch (c)
                        {
                            case Color.Red:
                                return "red";
                            default:
                                return "other";
                        }
                    }
                }
                """,
            )

            findings = linter.lint_repository(root)
            self.assertIn("BAML001", [f.rule for f in findings])
            self.assertTrue(any("Color.Blue" in f.message and "Color.Green" in f.message for f in findings))

    def test_accepts_switch_statement_naming_all_enum_variants(self):
        linter = load_linter()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write_valid_build_config(root)
            write_text(
                root / "src/Sample/Color.cs",
                """
                namespace Sample;
                public enum Color
                {
                    Red,
                    Green,
                    Blue,
                }
                """,
            )
            write_text(
                root / "src/Sample/Printer.cs",
                """
                namespace Sample;
                public static class Printer
                {
                    public static string Describe(Color c)
                    {
                        switch (c)
                        {
                            case Color.Red:
                                return "red";
                            case Color.Green:
                            case Color.Blue:
                            default:
                                return "other";
                        }
                    }
                }
                """,
            )

            self.assertEqual([], linter.lint_repository(root))

    def test_flags_if_else_if_chain_on_same_enum_variable(self):
        linter = load_linter()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write_valid_build_config(root)
            write_text(
                root / "src/Sample/Color.cs",
                """
                namespace Sample;
                public enum Color
                {
                    Red,
                    Green,
                }
                """,
            )
            write_text(
                root / "src/Sample/Hex.cs",
                """
                namespace Sample;
                public static class Hex
                {
                    public static string Format(Color c)
                    {
                        if (c == Color.Red)
                        {
                            return "#f00";
                        }
                        else if (c == Color.Green)
                        {
                            return "#0f0";
                        }
                        return "#000";
                    }
                }
                """,
            )

            findings = linter.lint_repository(root)
            self.assertIn("BAML001", [f.rule for f in findings])

    def test_flags_raw_integer_milliseconds_in_delay_sleep_timers_and_waits(self):
        linter = load_linter()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write_valid_build_config(root)
            write_text(
                root / "src/Sample/Worker.cs",
                """
                using System.Threading;
                using System.Threading.Tasks;

                namespace Sample;
                public static class Worker
                {
                    public static async Task RunAsync(CancellationTokenSource cts, SemaphoreSlim gate)
                    {
                        await Task.Delay(250);
                        cts.CancelAfter(500);
                        Thread.Sleep(100);
                        await gate.WaitAsync(0);
                        using var timer = new System.Timers.Timer(100);
                        using var timedCts = new CancellationTokenSource(1000);
                    }
                }
                """,
            )

            findings = linter.lint_repository(root)
            baml002 = [f for f in findings if f.rule == "BAML002"]
            self.assertEqual(6, len(baml002))

    def test_flags_throwing_generic_exception_types(self):
        linter = load_linter()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write_valid_build_config(root)
            write_text(
                root / "src/Sample/BadError.cs",
                """
                using System;

                namespace Sample;
                public static class BadError
                {
                    public static void Fail()
                    {
                        throw new Exception("boom");
                    }
                }
                """,
            )

            findings = linter.lint_repository(root)
            self.assertIn("BAML003", [f.rule for f in findings])

    def test_flags_runtime_regex_with_string_literal(self):
        linter = load_linter()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write_valid_build_config(root)
            write_text(
                root / "src/Sample/Matcher.cs",
                """
                using System.Text.RegularExpressions;

                namespace Sample;
                public static class Matcher
                {
                    public static bool Check(string input) =>
                        Regex.IsMatch(input, "^[a-z]+$");
                }
                """,
            )

            findings = linter.lint_repository(root)
            self.assertIn("BAML004", [f.rule for f in findings])

    def test_flags_missing_warnings_as_errors_configuration(self):
        linter = load_linter()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write_text(
                root / "Directory.Build.props",
                """
                <Project>
                    <PropertyGroup>
                        <Version>1.0.0</Version>
                    </PropertyGroup>
                </Project>
                """,
            )
            write_text(root / ".editorconfig", "root = true\n")

            findings = linter.lint_repository(root)
            self.assertIn("BAML005", [f.rule for f in findings])

    def test_current_repository_passes_all_code_guideline_checks(self):
        linter = load_linter()
        repo_root = Path(__file__).parents[2]
        findings = linter.lint_repository(repo_root)
        self.assertEqual(
            [],
            findings,
            "Expected zero code guideline violations in repository, found:\n"
            + "\n".join(str(f) for f in findings),
        )


if __name__ == "__main__":
    unittest.main()
