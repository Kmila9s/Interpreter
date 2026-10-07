using Interpreter.Repositories;
using Interpreter.Services;
using Interpreter.Synthesis;
using Interpreter.Synthesis.Layout;
using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Tests;

/// <summary>
/// Sanity checks for the synthesis pipeline. Each test builds an
/// architecture programmatically (no VHDL parsing — we want to test the
/// synthesizer in isolation from the lexer/parser), runs the synthesizer,
/// and asserts on the resulting <see cref="Netlist"/>'s shape. The
/// layout pass also runs for every test — a layout crash on any
/// reasonable netlist is itself a bug.
/// </summary>
public static class SynthesisTests
{
    /// <summary>
    /// Runs every synthesis test in sequence, printing pass/fail for each.
    /// </summary>
    /// <returns>The number of tests that failed.</returns>
    public static async Task<int> RunAllTests()
    {
        int failed = 0;
        var tests = new (string Name, Func<bool> Run)[]
        {
            ("Concurrent AND assignment", TestSimpleAnd),
            ("Combined and/or expression", TestAndOr),
            ("NOT unary operator", TestNotUnary),
            ("Conditional when/else", TestConditional),
            ("Selected with/select", TestSelected),
            ("Clocked process → DFF", TestDff),
            ("Process with non-clocked body falls back to BLACKBOX", TestProcessBlackbox),
            ("Clocked process with sync reset → DFF + MUX", TestClockedSyncReset),
            ("Clocked process with elsif chain → MUX cascade + DFF", TestClockedElsifChain),
            ("Combinational process with if → MUX, no DFF", TestCombinationalIfMux),
            ("Process with case statement → MUX cascade", TestProcessCase),
            ("Layout produces deterministic coordinates", TestLayoutDeterminism),
            ("Probe: dip-adder resize/unsigned/+ chain",   TestDipAdderProbe),
            ("Async-reset (if reset then; elsif rising_edge) → DFF", TestAsyncResetDff),
            ("Component instantiation → BLACKBOX_COMPONENT cell",   TestComponentInstantiationBlackbox),
        };

        foreach (var (name, run) in tests)
        {
            Console.WriteLine($"=== Synthesis test: {name} ===");
            bool ok;
            try
            {
                ok = run();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                ok = false;
            }
            Console.WriteLine(ok ? "PASS" : "FAIL");
            if (!ok) failed++;
        }

        await Task.CompletedTask;
        return failed;
    }

    /// <summary>
    /// `y &lt;= a and b;` over std_logic ports produces exactly one AND
    /// cell whose output drives `y` through a BUF (the synthesizer's
    /// uniform LHS hookup).
    /// </summary>
    private static bool TestSimpleAnd()
    {
        var (entity, scope) = BuildEntity("e1", new[] {
            ("a", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("b", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("y", PortType.OUT, DATATYPES.STD_LOGIC, 1),
        });
        var op = new Operation("y <= a and b", scope);
        entity.Architecture.Tasks.Add(op);

        var net = Synthesizer.Synthesize(entity);
        int ands = net.Cells.Count(c => c.Kind == CellKind.AND);
        bool yIsPort = net.Ports.Any(p => p.Name == "y" && p.Direction == PortDir.Out);
        Console.WriteLine($"  cells: {string.Join(", ", net.Cells.Select(c => c.Kind))}");
        return ands == 1 && yIsPort;
    }

    /// <summary>
    /// `y &lt;= (a and b) or c;` should emit one AND and one OR.
    /// </summary>
    private static bool TestAndOr()
    {
        var (entity, scope) = BuildEntity("e2", new[] {
            ("a", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("b", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("c", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("y", PortType.OUT, DATATYPES.STD_LOGIC, 1),
        });
        entity.Architecture.Tasks.Add(new Operation("y <= ( a and b ) or c", scope));
        var net = Synthesizer.Synthesize(entity);
        int ands = net.Cells.Count(c => c.Kind == CellKind.AND);
        int ors = net.Cells.Count(c => c.Kind == CellKind.OR);
        Console.WriteLine($"  ANDs={ands} ORs={ors}");
        return ands == 1 && ors == 1;
    }

    private static bool TestNotUnary()
    {
        var (entity, scope) = BuildEntity("e3", new[] {
            ("a", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("y", PortType.OUT, DATATYPES.STD_LOGIC, 1),
        });
        entity.Architecture.Tasks.Add(new Operation("y <= not a", scope));
        var net = Synthesizer.Synthesize(entity);
        int nots = net.Cells.Count(c => c.Kind == CellKind.NOT);
        Console.WriteLine($"  NOTs={nots}");
        return nots == 1;
    }

    /// <summary>
    /// Conditional `y &lt;= a when sel = '1' else b;` should produce an
    /// EQ comparator and a MUX that switches between a and b.
    /// </summary>
    private static bool TestConditional()
    {
        var (entity, scope) = BuildEntity("e4", new[] {
            ("a", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("b", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("sel", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("y", PortType.OUT, DATATYPES.STD_LOGIC, 1),
        });
        var ca = ConditionalAssignment.TryParse("y <= a when sel = '1' else b", scope);
        if (ca == null) { Console.WriteLine("  TryParse returned null"); return false; }
        entity.Architecture.Tasks.Add(ca);
        var net = Synthesizer.Synthesize(entity);
        int muxes = net.Cells.Count(c => c.Kind == CellKind.MUX);
        Console.WriteLine($"  MUXes={muxes} cells={string.Join(",", net.Cells.Select(c => c.Kind))}");
        return muxes == 1;
    }

    /// <summary>
    /// Synthesize a SelectedAssignment built explicitly (no parser path).
    /// Two choices + others should yield two EQ comparators and two MUXes.
    /// </summary>
    private static bool TestSelected()
    {
        var (entity, scope) = BuildEntity("e5", new[] {
            ("sel", PortType.IN, DATATYPES.STD_LOGIC_VECTOR, 2),
            ("a", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("b", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("c", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("y", PortType.OUT, DATATYPES.STD_LOGIC, 1),
        });
        var sa = new SelectedAssignment(scope, "sel", "y");
        sa.Choices.Add(new SelectionChoice { ValueExpression = "a", Choices = { "\"00\"" } });
        sa.Choices.Add(new SelectionChoice { ValueExpression = "b", Choices = { "\"01\"" } });
        sa.Choices.Add(new SelectionChoice { ValueExpression = "c", IsOthers = true });
        entity.Architecture.Tasks.Add(sa);

        var net = Synthesizer.Synthesize(entity);
        int eqs = net.Cells.Count(c => c.Kind == CellKind.EQ);
        int muxes = net.Cells.Count(c => c.Kind == CellKind.MUX);
        Console.WriteLine($"  EQs={eqs} MUXes={muxes}");
        return eqs == 2 && muxes == 2;
    }

    /// <summary>
    /// Build a process with the canonical clock-edge shape and confirm
    /// the synthesizer produces a DFF cell.
    /// </summary>
    private static bool TestDff()
    {
        var (entity, scope) = BuildEntity("e6", new[] {
            ("clk", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("d", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("q", PortType.OUT, DATATYPES.STD_LOGIC, 1),
        });
        var process = BuildClockedProcess(scope, "clk", "q", "d");
        entity.Architecture.Tasks.Add(process);

        var net = Synthesizer.Synthesize(entity);
        int dffs = net.Cells.Count(c => c.Kind == CellKind.DFF);
        Console.WriteLine($"  DFFs={dffs}, cells={string.Join(",", net.Cells.Select(c => c.Kind))}");
        return dffs == 1;
    }

    /// <summary>
    /// Clocked process with synchronous reset:
    ///   if rising_edge(clk) then
    ///     if reset = '1' then q &lt;= '0' else q &lt;= d end if
    ///   end if
    /// Should produce one DFF (Q output), one MUX (D selector), and
    /// one EQ comparator (the reset = '1' check).
    /// </summary>
    private static bool TestClockedSyncReset()
    {
        var (entity, scope) = BuildEntity("e_dff_sync", new[] {
            ("clk", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("reset", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("d", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("q", PortType.OUT, DATATYPES.STD_LOGIC, 1),
        });
        var process = BuildClockedProcess(scope, "clk", innerBody: ifs =>
        {
            // Inner: if reset = '1' then q <= '0' else q <= d end if
            var innerIf = new IfStructure(scope, default!);
            var thenBr = new IfBranch
            {
                Condition = new Operation("_c <= reset = '1'",
                    new HashSet<Variable>(scope) { new Variable("_c", "false", DATATYPES.BOOLEAN) { PortType = PortType.OUT } }),
            };
            thenBr.Tasks.Add(new Operation("q <= '0'", scope));
            var elseBr = new IfBranch { IsElse = true };
            elseBr.Tasks.Add(new Operation("q <= d", scope));
            innerIf.Branches.Add(thenBr);
            innerIf.Branches.Add(elseBr);
            ifs.Branches[0].Tasks.Add(innerIf);
        });
        entity.Architecture.Tasks.Add(process);

        var net = Synthesizer.Synthesize(entity);
        int dff = net.Cells.Count(c => c.Kind == CellKind.DFF);
        int mux = net.Cells.Count(c => c.Kind == CellKind.MUX);
        Console.WriteLine($"  cells={string.Join(",", net.Cells.Select(c => c.Kind))}");
        return dff == 1 && mux >= 1 && !net.Cells.Any(c => c.Kind == CellKind.BLACKBOX_PROCESS);
    }

    /// <summary>
    /// Clocked process with if/elsif/else (priority encoder shape).
    /// Two branches + else = two MUXes in cascade, one DFF.
    /// </summary>
    private static bool TestClockedElsifChain()
    {
        var (entity, scope) = BuildEntity("e_dff_elsif", new[] {
            ("clk", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("a", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("b", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("c", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("d", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("e", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("q", PortType.OUT, DATATYPES.STD_LOGIC, 1),
        });
        var process = BuildClockedProcess(scope, "clk", innerBody: ifs =>
        {
            var innerIf = new IfStructure(scope, default!);
            var br1 = new IfBranch
            {
                Condition = new Operation("_c1 <= a = '1'",
                    new HashSet<Variable>(scope) { new Variable("_c1", "false", DATATYPES.BOOLEAN) { PortType = PortType.OUT } }),
            };
            br1.Tasks.Add(new Operation("q <= b", scope));
            var br2 = new IfBranch
            {
                Condition = new Operation("_c2 <= c = '1'",
                    new HashSet<Variable>(scope) { new Variable("_c2", "false", DATATYPES.BOOLEAN) { PortType = PortType.OUT } }),
            };
            br2.Tasks.Add(new Operation("q <= d", scope));
            var brEl = new IfBranch { IsElse = true };
            brEl.Tasks.Add(new Operation("q <= e", scope));
            innerIf.Branches.Add(br1);
            innerIf.Branches.Add(br2);
            innerIf.Branches.Add(brEl);
            ifs.Branches[0].Tasks.Add(innerIf);
        });
        entity.Architecture.Tasks.Add(process);

        var net = Synthesizer.Synthesize(entity);
        int dff = net.Cells.Count(c => c.Kind == CellKind.DFF);
        int mux = net.Cells.Count(c => c.Kind == CellKind.MUX);
        Console.WriteLine($"  DFFs={dff} MUXes={mux}");
        return dff == 1 && mux == 2 && !net.Cells.Any(c => c.Kind == CellKind.BLACKBOX_PROCESS);
    }

    /// <summary>
    /// Combinational process: process(sel,a,b) begin if sel='1' then x &lt;= a else x &lt;= b end if; end.
    /// Expected: one MUX (no DFF) driving x.
    /// </summary>
    private static bool TestCombinationalIfMux()
    {
        var (entity, scope) = BuildEntity("e_comb_mux", new[] {
            ("sel", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("a", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("b", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("x", PortType.OUT, DATATYPES.STD_LOGIC, 1),
        });
        var sel = scope.First(v => v.Name == "sel");
        var process = new Process(scope, default!);
        process.SensitiveList.Add(sel);
        var ifs = new IfStructure(scope, default!);
        var thenBr = new IfBranch
        {
            Condition = new Operation("_c <= sel = '1'",
                new HashSet<Variable>(scope) { new Variable("_c", "false", DATATYPES.BOOLEAN) { PortType = PortType.OUT } }),
        };
        thenBr.Tasks.Add(new Operation("x <= a", scope));
        var elseBr = new IfBranch { IsElse = true };
        elseBr.Tasks.Add(new Operation("x <= b", scope));
        ifs.Branches.Add(thenBr);
        ifs.Branches.Add(elseBr);
        process.Tasks.Add(ifs);
        entity.Architecture.Tasks.Add(process);

        var net = Synthesizer.Synthesize(entity);
        int dff = net.Cells.Count(c => c.Kind == CellKind.DFF);
        int mux = net.Cells.Count(c => c.Kind == CellKind.MUX);
        Console.WriteLine($"  DFFs={dff} MUXes={mux}");
        return dff == 0 && mux == 1 && !net.Cells.Any(c => c.Kind == CellKind.BLACKBOX_PROCESS);
    }

    /// <summary>
    /// Combinational process with a case statement on a 2-bit selector.
    /// Two whens + others should produce two EQ comparators and two
    /// MUX cells (one per non-others branch).
    /// </summary>
    private static bool TestProcessCase()
    {
        var (entity, scope) = BuildEntity("e_case", new[] {
            ("sel", PortType.IN, DATATYPES.STD_LOGIC_VECTOR, 2),
            ("a", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("b", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("c", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("y", PortType.OUT, DATATYPES.STD_LOGIC, 1),
        });
        var sel = scope.First(v => v.Name == "sel");
        var process = new Process(scope, default!);
        process.SensitiveList.Add(sel);
        var cs = new CaseStructure(scope, default!);
        // Inject the selector via reflection-free public surface — the
        // CaseStructure exposes a private setter via parsing only, so
        // for the test we cheat by reading-back-then-replacing through
        // the Branches list. The selector itself isn't strictly needed
        // for synthesis to detect what's a case body; we just need the
        // CaseStructure shape and the Selector property populated.
        // Simulate it by setting via the private field since it's an
        // internal-test scenario.
        var selectorField = typeof(CaseStructure).GetField("_selector",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        selectorField?.SetValue(cs, sel);

        var br1 = new CaseWhenBranch { Choices = { "\"00\"" } };
        br1.Tasks.Add(new Operation("y <= a", scope));
        var br2 = new CaseWhenBranch { Choices = { "\"01\"" } };
        br2.Tasks.Add(new Operation("y <= b", scope));
        var brOthers = new CaseWhenBranch { IsOthers = true };
        brOthers.Tasks.Add(new Operation("y <= c", scope));
        cs.Branches.Add(br1);
        cs.Branches.Add(br2);
        cs.Branches.Add(brOthers);
        process.Tasks.Add(cs);
        entity.Architecture.Tasks.Add(process);

        var net = Synthesizer.Synthesize(entity);
        int eq = net.Cells.Count(c => c.Kind == CellKind.EQ);
        int mux = net.Cells.Count(c => c.Kind == CellKind.MUX);
        bool noBlackbox = !net.Cells.Any(c => c.Kind == CellKind.BLACKBOX_PROCESS);
        Console.WriteLine($"  EQs={eq} MUXes={mux} noBlackbox={noBlackbox}");
        return eq == 2 && mux == 2 && noBlackbox;
    }

    /// <summary>
    /// A non-clocked process body should fall through to BLACKBOX_PROCESS
    /// with a Diagnostic explaining why.
    /// </summary>
    private static bool TestProcessBlackbox()
    {
        var (entity, scope) = BuildEntity("e7", new[] {
            ("a", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("y", PortType.OUT, DATATYPES.STD_LOGIC, 1),
        });
        var process = BuildSensitiveProcess(scope, "a", "y", "a");
        entity.Architecture.Tasks.Add(process);

        var net = Synthesizer.Synthesize(entity);
        bool blackbox = net.Cells.Any(c => c.Kind == CellKind.BLACKBOX_PROCESS);
        bool hasDiag = net.Diagnostics.Any(d => d.Kind == "BlackboxProcess");
        Console.WriteLine($"  blackbox={blackbox} diag={hasDiag}");
        return blackbox && hasDiag;
    }

    /// <summary>
    /// The layout pass should be a pure function — running it twice on
    /// the same netlist must produce identical coordinates. This makes
    /// snapshot testing meaningful in the future.
    /// </summary>
    /// <summary>
    /// Diagnostic probe — the exact dip-adder assignment
    /// `sum &lt;= std_logic_vector(resize(unsigned(a), 5) + resize(unsigned(b), 5))`.
    /// Prints the emitted CellKind sequence so we can see whether ADD, RESIZE, and
    /// the two `unsigned` BUF-casts land. Passes iff at least one ADD cell is emitted
    /// (the presence-check is what the schematic export failure boils down to).
    /// </summary>
    private static bool TestDipAdderProbe()
    {
        var (entity, scope) = BuildEntity("dip_adder", new[] {
            ("a",   PortType.IN,  DATATYPES.STD_LOGIC_VECTOR, 4),
            ("b",   PortType.IN,  DATATYPES.STD_LOGIC_VECTOR, 4),
            ("sum", PortType.OUT, DATATYPES.STD_LOGIC_VECTOR, 5),
        });
        entity.Architecture.Tasks.Add(new Operation(
            "sum <= std_logic_vector(resize(unsigned(a), 5) + resize(unsigned(b), 5))", scope));
        var net = Synthesizer.Synthesize(entity);
        var kinds = net.Cells.Select(c => c.Kind.ToString()).ToList();
        Console.WriteLine($"  cells: [{string.Join(", ", kinds)}]");
        int adds    = net.Cells.Count(c => c.Kind == CellKind.ADD);
        int resizes = net.Cells.Count(c => c.Kind == CellKind.RESIZE);
        int bufs    = net.Cells.Count(c => c.Kind == CellKind.BUF);
        Console.WriteLine($"  ADD={adds}  RESIZE={resizes}  BUF={bufs}");
        return adds >= 1;
    }

    /// <summary>
    /// Async-reset pattern: <c>process(clk, reset) if reset = '1' then q &lt;= '0'
    /// elsif rising_edge(clk) then q &lt;= d end if</c>. Must emit at least one DFF.
    /// Regression for the shift-register schematic bug where async-reset processes
    /// silently degraded to combinational (BUF/MUX only, no DFF).
    /// </summary>
    private static bool TestAsyncResetDff()
    {
        var (entity, scope) = BuildEntity("e_dff_async", new[] {
            ("clk",   PortType.IN,  DATATYPES.STD_LOGIC, 1),
            ("reset", PortType.IN,  DATATYPES.STD_LOGIC, 1),
            ("d",     PortType.IN,  DATATYPES.STD_LOGIC, 1),
            ("q",     PortType.OUT, DATATYPES.STD_LOGIC, 1),
        });
        var clk   = scope.First(v => v.Name == "clk");
        var reset = scope.First(v => v.Name == "reset");
        var proc  = new Process(scope, default!);
        proc.SensitiveList.Add(clk);
        proc.SensitiveList.Add(reset);
        var ifs = new IfStructure(scope, default!);
        var resetBr = new IfBranch
        {
            Condition = new Operation("_c <= reset = '1'",
                new HashSet<Variable>(scope) { new Variable("_c", "false", DATATYPES.BOOLEAN) { PortType = PortType.OUT } }),
        };
        resetBr.Tasks.Add(new Operation("q <= '0'", scope));
        var clkBr = new IfBranch
        {
            Condition = new Operation("_c <= rising_edge(clk)",
                new HashSet<Variable>(scope) { new Variable("_c", "false", DATATYPES.BOOLEAN) { PortType = PortType.OUT } }),
        };
        clkBr.Tasks.Add(new Operation("q <= d", scope));
        ifs.Branches.Add(resetBr);
        ifs.Branches.Add(clkBr);
        proc.Tasks.Add(ifs);
        entity.Architecture.Tasks.Add(proc);

        var net = Synthesizer.Synthesize(entity);
        int dff = net.Cells.Count(c => c.Kind == CellKind.DFF);
        int blackbox = net.Cells.Count(c => c.Kind == CellKind.BLACKBOX_PROCESS);
        Console.WriteLine($"  cells: [{string.Join(", ", net.Cells.Select(c => c.Kind))}]");
        return dff >= 1 && blackbox == 0;
    }

    /// <summary>
    /// Hierarchical design: `top_entity` port-maps to `sub_entity`. After
    /// registering the instance in <see cref="EntityRegistry"/>, the
    /// synthesizer must emit a BLACKBOX_COMPONENT cell whose pins are
    /// wired to `top_entity`'s ports/signals. Regression coverage for
    /// finding #9 — capstone-calc's invisible hierarchy.
    /// </summary>
    private static bool TestComponentInstantiationBlackbox()
    {
        // Reset the singleton so this test doesn't inherit state from prior tests
        // (or the test runner's earlier iterations of this same test).
        EntityRegistry.Reset();

        // Build the resolved sub-entity so the synthesizer can look up
        // formal-pin directions and place inputs/outputs on the correct
        // edges of the glyph.
        var subInfo = new EntityInfo
        {
            Name = "sub_entity",
            Ports = new HashSet<Variable>
            {
                new Variable("a") { Type = DATATYPES.STD_LOGIC, PortType = PortType.IN,  IsPort = true },
                new Variable("y") { Type = DATATYPES.STD_LOGIC, PortType = PortType.OUT, IsPort = true },
            },
        };
        EntityRegistry.Instance.RegisterEntity(subInfo);

        var (entity, _) = BuildEntity("top_entity", new[] {
            ("x", PortType.IN,  DATATYPES.STD_LOGIC, 1),
            ("z", PortType.OUT, DATATYPES.STD_LOGIC, 1),
        });
        var inst = new ComponentInstance
        {
            InstanceName = "u_sub",
            EntityName = "sub_entity",
            LibraryName = "work",
            PortMap = { ["a"] = "x", ["y"] = "z" },
        };
        EntityRegistry.Instance.RegisterComponentInstance("top_entity", inst);

        var net = Synthesizer.Synthesize(entity);
        int bb = net.Cells.Count(c => c.Kind == CellKind.BLACKBOX_COMPONENT);
        var cell = net.Cells.FirstOrDefault(c => c.Kind == CellKind.BLACKBOX_COMPONENT);
        Console.WriteLine($"  cells: [{string.Join(", ", net.Cells.Select(c => c.Kind))}]");
        if (cell == null) return false;
        Console.WriteLine($"  BLACKBOX_COMPONENT inputs=[{string.Join(", ", cell.Inputs.Select(p => p.Name))}] " +
                          $"outputs=[{string.Join(", ", cell.Outputs.Select(p => p.Name))}]");
        bool hasInputA  = cell.Inputs.Any(p => p.Name == "a");
        bool hasOutputY = cell.Outputs.Any(p => p.Name == "y");
        bool labelOk    = cell.Attrs != null && cell.Attrs.TryGetValue("Op", out var op) && op == "sub_entity";
        // Cleanup — don't leak state into the next test.
        EntityRegistry.Reset();
        return bb == 1 && hasInputA && hasOutputY && labelOk;
    }

    private static bool TestLayoutDeterminism()
    {
        var (entity, scope) = BuildEntity("e8", new[] {
            ("a", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("b", PortType.IN, DATATYPES.STD_LOGIC, 1),
            ("y", PortType.OUT, DATATYPES.STD_LOGIC, 1),
        });
        entity.Architecture.Tasks.Add(new Operation("y <= a and b", scope));
        var n = Synthesizer.Synthesize(entity);
        var l1 = LayeredLayout.Layout(n);
        var l2 = LayeredLayout.Layout(n);
        bool sameWidth = l1.CanvasWidth == l2.CanvasWidth;
        bool sameCells = l1.CellBounds.Count == l2.CellBounds.Count
            && l1.CellBounds.All(kv => l2.CellBounds.TryGetValue(kv.Key, out var r) && r == kv.Value);
        bool sameWires = l1.Wires.Count == l2.Wires.Count;
        Console.WriteLine($"  cells={sameCells} width={sameWidth} wires={sameWires}");
        return sameWidth && sameCells && sameWires;
    }

    // ─────────────────────── Helpers ───────────────────────

    private static (Entity, HashSet<Variable>) BuildEntity(
        string name, IEnumerable<(string Name, PortType Dir, DATATYPES Type, int Size)> ports)
    {
        var entity = new Entity(name);
        foreach (var (pname, dir, type, size) in ports)
        {
            var v = new Variable(pname);
            v.Type = type;
            if (type == DATATYPES.STD_LOGIC_VECTOR && size > 0) v.Size = size;
            v.PortType = dir;
            v.IsPort = true;
            entity.Ports.Add(v);
        }
        var arch = new Architecture(name, "rtl", entity.Ports, entity.Generics);
        entity.SetArchitecture(arch);
        var scope = new HashSet<Variable>(entity.Ports);
        return (entity, scope);
    }

    /// <summary>
    /// Builds a process node by hand: sensitivity list = {clk}, body =
    /// `if rising_edge(clk) then q &lt;= d; end if;`. We can't construct
    /// a Process without a TokenStream, so we synthesize the parts and
    /// stuff them into a minimal Process via reflection on the public
    /// surface.
    /// </summary>
    private static Process BuildClockedProcess(HashSet<Variable> scope, string clkName, string qName, string dName)
    {
        return BuildClockedProcess(scope, clkName, ifs =>
        {
            ifs.Branches[0].Tasks.Add(new Operation($"{qName} <= {dName}", scope));
        });
    }

    /// <summary>
    /// Builds a clocked process with the canonical
    /// <c>if rising_edge(clk) then … end if</c> wrapper and lets the
    /// caller fill the inner body via <paramref name="innerBody"/>.
    /// Used by the elsif / sync-reset tests to install nested
    /// IfStructures without duplicating the wrapper boilerplate.
    /// </summary>
    private static Process BuildClockedProcess(HashSet<Variable> scope, string clkName, Action<IfStructure> innerBody)
    {
        var clk = scope.First(v => v.Name == clkName);
        var p = new Process(scope, default!);
        p.SensitiveList.Add(clk);

        var ifs = new IfStructure(scope, default!);
        var br = new IfBranch();
        br.Condition = new Operation($"_cond <= rising_edge({clkName})",
            new HashSet<Variable>(scope) { new Variable("_cond", "false", DATATYPES.BOOLEAN) { PortType = PortType.OUT } });
        ifs.Branches.Add(br);
        innerBody(ifs);
        p.Tasks.Add(ifs);
        return p;
    }

    private static Process BuildSensitiveProcess(HashSet<Variable> scope, string sensName, string lhs, string rhs)
    {
        var sens = scope.First(v => v.Name == sensName);
        var p = new Process(scope, default!);
        p.SensitiveList.Add(sens);
        // Plain assignment plus a NullStatement — the synthesizer
        // walker handles Operation and (IfStructure / CaseStructure)
        // but bails on anything else (loops, function calls, null
        // statements). Pairing with a real assignment also exercises
        // the partial-then-blackbox path correctly.
        p.Tasks.Add(new Operation($"{lhs} <= not {rhs}", scope));
        p.Tasks.Add(new NullStatement(default!));
        return p;
    }
}
