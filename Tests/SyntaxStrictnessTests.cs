using Interpreter.Services;
using Parser.Models;

namespace Interpreter.Tests;

/// <summary>
/// Regression coverage for the P1 syntax-strictness hardening (2026-10-20).
/// The build path (Interpreter synthesis: <see cref="Architecture"/> →
/// <see cref="Process"/> / IfStructure / CaseStructure) used to "consume
/// expected tokens only when present, never error on absence", so several
/// pure-syntax errors GHDL rejects were silently accepted. These tests drive
/// <see cref="Entity.Syntetize(string[])"/> + <see cref="Architecture.Syntetize(string[])"/>
/// (the same calls the Debugger line-scanner makes) and assert each bad case
/// now throws, while well-formed code still parses.
///
/// Fixtures mirror Kmila-9s/ReviewSyntaxGap/ (S1, S4, S6, S9) plus a valid
/// control. The one-line-entity scanner fix lives in the Debugger module and is
/// regression-covered by the CLI fixture gate, not here (Interpreter cannot
/// reference Debugger).
/// </summary>
public static class SyntaxStrictnessTests
{
    /// <summary>Runs every syntax-strictness test, printing pass/fail for each.</summary>
    /// <returns>The number of tests that failed.</returns>
    public static int RunAllTests()
    {
        int failed = 0;
        Console.WriteLine("=== Syntax Strictness Tests ===");

        var tests = new (string Name, Func<bool> Run)[]
        {
            ("S1: missing ';' after 'end process' is rejected", Test_MissingSemicolonEndProcess),
            ("S4: missing '=>' in a case choice is rejected",   Test_MissingArrowInCase),
            ("S6: ':=' on a signal (concurrent) is rejected",   Test_VarAssignOnSignal),
            ("S9: missing 'end if;' is rejected",               Test_MissingEndIf),
            ("C1: undeclared case choice is rejected",          Test_UndeclaredCaseChoice),
            ("C7: duplicate case choice is rejected",           Test_DuplicateCaseChoice),
            ("C6: non-exhaustive std_logic_vector case rejected", Test_NonExhaustiveCase),
            ("C10: enum choice not in the type is rejected",    Test_EnumChoiceNotInType),
            ("Valid if/case/process still parses",              Test_ValidControlParses),
            ("Valid literal case (with others) still parses",   Test_ValidLiteralCaseParses),
        };

        foreach (var (name, run) in tests)
        {
            try
            {
                bool ok = run();
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
                if (!ok) failed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [FAIL] {name} — exception: {ex.GetType().Name}: {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine($"Results: {tests.Length - failed} passed, {failed} failed");
        return failed;
    }

    // Shared entity used by the architecture-level cases.
    private static readonly string[] Ent2 =
    {
        "entity dut is",
        "    port (a : in std_logic; sel : in std_logic_vector(1 downto 0); y : out std_logic);",
        "end entity dut;",
    };

    // --- negative cases: synthesis must throw ---

    private static bool Test_MissingSemicolonEndProcess() => ExpectThrows(Ent2, new[]
    {
        "architecture rtl of dut is",
        "begin",
        "    process(a)",
        "    begin",
        "        y <= a;",
        "    end process",          // ERROR: missing ';'
        "end architecture rtl;",
    });

    private static bool Test_MissingArrowInCase() => ExpectThrows(Ent2, new[]
    {
        "architecture rtl of dut is",
        "begin",
        "    process(sel)",
        "    begin",
        "        case sel is",
        "            when \"00\"    y <= '0';",   // ERROR: missing '=>'
        "            when others => y <= '1';",
        "        end case;",
        "    end process;",
        "end architecture rtl;",
    });

    private static bool Test_VarAssignOnSignal() => ExpectThrows(Ent2, new[]
    {
        "architecture rtl of dut is",
        "begin",
        "    y := a;",              // ERROR: ':=' on a signal in a concurrent statement
        "end architecture rtl;",
    });

    private static bool Test_MissingEndIf() => ExpectThrows(Ent2, new[]
    {
        "architecture rtl of dut is",
        "begin",
        "    process(a)",
        "    begin",
        "        if a = '1' then",
        "            y <= '1';",
        "    end process;",          // ERROR: 'end if;' missing before 'end process'
        "end architecture rtl;",
    });

    private static bool Test_UndeclaredCaseChoice() => ExpectThrows(Ent2, new[]
    {
        "architecture rtl of dut is",
        "begin",
        "    process(sel)",
        "    begin",
        "        case sel is",
        "            when \"00\" => y <= '0';",
        "            when C     => y <= '1';",   // ERROR: C is undeclared
        "            when others => y <= '0';",
        "        end case;",
        "    end process;",
        "end architecture rtl;",
    });

    private static bool Test_DuplicateCaseChoice() => ExpectThrows(Ent2, new[]
    {
        "architecture rtl of dut is",
        "begin",
        "    process(sel)",
        "    begin",
        "        case sel is",
        "            when \"00\" => y <= '0';",
        "            when \"00\" => y <= '1';",   // ERROR: choice "00" already covered
        "            when others => y <= '0';",
        "        end case;",
        "    end process;",
        "end architecture rtl;",
    });

    private static bool Test_NonExhaustiveCase() => ExpectThrows(Ent2, new[]
    {
        "architecture rtl of dut is",
        "begin",
        "    process(sel)",
        "    begin",
        "        case sel is",
        "            when \"00\" => y <= '0';",
        "            when \"01\" => y <= '1';",   // ERROR: "10"/"11" uncovered, no others
        "        end case;",
        "    end process;",
        "end architecture rtl;",
    });

    private static bool Test_EnumChoiceNotInType() => ExpectThrows(Ent2, new[]
    {
        "architecture rtl of dut is",
        "    type state_t is (IDLE, RUN, DONE);",
        "    signal st : state_t := IDLE;",
        "begin",
        "    process(st)",
        "    begin",
        "        case st is",
        "            when IDLE   => y <= '0';",
        "            when STOP   => y <= '1';",   // ERROR: STOP is not a literal of state_t
        "            when others => y <= '0';",
        "        end case;",
        "    end process;",
        "end architecture rtl;",
    });

    // --- positive controls: synthesis must NOT throw ---

    private static bool Test_ValidLiteralCaseParses() => ExpectParses(Ent2, new[]
    {
        "architecture rtl of dut is",
        "begin",
        "    process(sel)",
        "    begin",
        "        case sel is",
        "            when \"00\" => y <= '0';",
        "            when \"01\" => y <= '1';",
        "            when others => y <= '0';",
        "        end case;",
        "    end process;",
        "end architecture rtl;",
    });

    private static bool Test_ValidControlParses() => ExpectParses(Ent2, new[]
    {
        "architecture rtl of dut is",
        "begin",
        "    process(a, sel)",
        "    begin",
        "        case sel is",
        "            when \"00\" => y <= '0';",
        "            when others => y <= a;",
        "        end case;",
        "        if sel(0) = '1' then",
        "            y <= '1';",
        "        end if;",
        "    end process;",
        "end architecture rtl;",
    });

    // --- helpers ---

    private static bool ExpectThrows(string[] entityLines, string[] archLines)
    {
        try { Synthesize(entityLines, archLines); return false; } // no error → test fails
        catch (ParserException) { return true; }
        catch (Parser.Repositories.TokenStream.StreamerException) { return true; }
    }

    private static bool ExpectParses(string[] entityLines, string[] archLines)
    {
        try { Synthesize(entityLines, archLines); return true; }
        catch { return false; }
    }

    private static void Synthesize(string[] entityLines, string[] archLines)
    {
        var entity = new Entity();
        entity.Syntetize(entityLines);
        var arch = new Architecture(entity.Ports, entity.Generics);
        arch.Syntetize(archLines);
    }
}
