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
    write_text(
        root / "Directory.Build.props",
        """
        <Project>
            <PropertyGroup>
                <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
                <WarningsAsErrors>$(WarningsAsErrors);CS8509;IDE0057;IDE0066;IDE1006;CA1510;CA1511;CA1512;CA1513;CA1826;CA1859;CA1860;CA1865;CA1866;CA1867;CA2201;SYSLIB1045</WarningsAsErrors>
            </PropertyGroup>
        </Project>
        """,
    )
    write_text(
        root / ".editorconfig",
        """
        root = true
        [*.cs]
        dotnet_diagnostic.CS8509.severity = error
        dotnet_diagnostic.IDE0057.severity = warning
        dotnet_diagnostic.IDE0066.severity = warning
        dotnet_diagnostic.CA1510.severity = warning
        dotnet_diagnostic.CA1511.severity = warning
        dotnet_diagnostic.CA1512.severity = warning
        dotnet_diagnostic.CA1513.severity = warning
        dotnet_diagnostic.CA1826.severity = warning
        dotnet_diagnostic.CA1859.severity = warning
        dotnet_diagnostic.CA1860.severity = warning
        dotnet_diagnostic.CA1865.severity = warning
        dotnet_diagnostic.CA1866.severity = warning
        dotnet_diagnostic.CA1867.severity = warning
        dotnet_diagnostic.CA2201.severity = warning
        dotnet_diagnostic.SYSLIB1045.severity = warning
        """,
    )


class CodeGuidelinesLintTests(unittest.TestCase):
    def test_flags_switch_expression_hiding_remaining_enum_variant_in_discard(self):
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
                        _ => "#00f",
                    };
                }
                """,
            )

            findings = linter.lint_repository(root)
            codes = [f.rule for f in findings]
            self.assertIn("BAML001", codes)
            self.assertTrue(any("Color.Blue" in f.message for f in findings))

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

    def test_flags_raw_integer_milliseconds_in_delay_sleep_and_cancel_after(self):
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
                    public static async Task RunAsync(CancellationTokenSource cts)
                    {
                        await Task.Delay(250);
                        cts.CancelAfter(500);
                        Thread.Sleep(100);
                    }
                }
                """,
            )

            findings = linter.lint_repository(root)
            baml002 = [f for f in findings if f.rule == "BAML002"]
            self.assertEqual(3, len(baml002))

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

    def test_flags_static_regex_with_string_literal_instead_of_generated_regex(self):
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
                    public static string Clean(string input) =>
                        Regex.Replace(input, @"[a-z]+", " ");
                }
                """,
            )

            findings = linter.lint_repository(root)
            self.assertIn("BAML004", [f.rule for f in findings])

    def test_allows_dynamic_regex_pattern_built_at_runtime(self):
        linter = load_linter()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write_valid_build_config(root)
            write_text(
                root / "src/Sample/DynamicMatcher.cs",
                """
                using System.Text.RegularExpressions;

                namespace Sample;
                public static class DynamicMatcher
                {
                    public static string Scrub(string text, string word, string replacement) =>
                        Regex.Replace(text, @"\\b" + Regex.Escape(word) + @"\\b", replacement);
                }
                """,
            )

            self.assertEqual([], linter.lint_repository(root))

    def test_flags_missing_compiler_enforcement_in_build_props_or_editorconfig(self):
        linter = load_linter()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write_text(root / "Directory.Build.props", "<Project></Project>")
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
            "Code guideline lint findings:\n" + "\n".join(str(f) for f in findings),
        )


if __name__ == "__main__":
    unittest.main()
