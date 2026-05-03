using Interpreter.Models;
using LibraryCompiler.Repositories;
using LibraryCompiler.Services;

namespace Interpreter.Services;

/// <summary>
/// Backwards-compatible facade that delegates to the new
/// <see cref="LibraryCompiler.Services.LibraryCompiler"/> module while
/// keeping the legacy <c>IEEELibraryLoader.Instance.EnsureLoaded()</c>
/// call shape intact for existing callers (Interpreter test runner,
/// Debugger Parser, in-process probes).
/// </summary>
/// <remarks>
/// <para>
/// History: v1 of #20 shipped this class as the only IEEE entry point —
/// it walked candidate paths, regex-scanned the .vhd stubs, and seeded
/// <see cref="FunctionRegistry"/>. v2 (#22) lifted that work into the
/// <c>LibraryCompiler</c> sibling module behind a host-driven option
/// surface. To avoid breaking every existing call site, this class
/// remains as a thin process-singleton that synthesises a default
/// <c>LibraryCompilerOptions</c> appropriate for the standalone
/// Interpreter / Debugger test runners and forwards the work.
/// </para>
/// <para>
/// Hosts that want full control (custom blob store, plugin sources,
/// non-default policy) should construct <see cref="LibraryCompiler.Services.LibraryCompiler"/>
/// directly via DI; this class is for the no-host case (CLI runners,
/// unit tests).
/// </para>
/// </remarks>
public sealed class IEEELibraryLoader
{
    private static readonly IEEELibraryLoader _instance = new();
    /// <summary>Process-wide singleton (legacy entry point).</summary>
    public static IEEELibraryLoader Instance => _instance;

    private readonly object _lock = new();
    private bool _loaded = false;
    private LibraryCompiler.Services.LibraryCompiler? _compiler;

    private IEEELibraryLoader() { }

    /// <summary>Library names this loader seeds (forwarded to the compiler).</summary>
    public static readonly string[] SupportedPackages =
        { "std_logic_1164", "numeric_std" };

    /// <summary>True once the IEEE seeds have landed in <see cref="FunctionRegistry"/>.</summary>
    public bool IsLoaded => _loaded;

    /// <summary>
    /// For diagnostics — the source-key prefix the underlying compiler
    /// resolved from. Returns "(embedded resource fallback)" when the
    /// embedded .vhd files were used (the lib's portable default).
    /// </summary>
    public string? StubRoot { get; private set; }

    /// <summary>
    /// Seeds <see cref="FunctionRegistry"/> with every IEEE symbol the
    /// library compiler discovers. Idempotent and thread-safe.
    /// </summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_lock)
        {
            if (_loaded) return;

            // Build a default compiler. The standalone Interpreter / Debugger
            // runners have no host-supplied OpenLibrarySource — we hand the
            // compiler a callback that returns null for every key, which
            // makes it fall back to its embedded-resource builtins.
            _compiler = new LibraryCompiler.Services.LibraryCompiler(
                new LibraryCompilerOptions(
                    OpenLibrarySource: (_, _) => Task.FromResult<Stream?>(null),
                    CacheStore:        NullBlobStore.Instance,
                    AdditionalSources: null,
                    Policy:            LibraryBuildPolicy.AlwaysProcessCached));

            try
            {
                var libs = _compiler.CompileAsync(SupportedPackages).GetAwaiter().GetResult();
                foreach (var lib in libs)
                {
                    foreach (var sym in lib.Functions)
                    {
                        if (sym.Kind != LibrarySymbolKind.Function && sym.Kind != LibrarySymbolKind.Procedure)
                            continue;
                        FunctionRegistry.Instance.Register(new FunctionDefinition
                        {
                            Name        = sym.Name,
                            PackageName = lib.LibraryName,
                            IsPure      = sym.Kind == LibrarySymbolKind.Function
                        });
                    }
                }
                StubRoot = "(LibraryCompiler / embedded IEEE builtins)";
            }
            catch
            {
                // Degraded path — even the embedded fallback failed.
                // Seed the canonical name list directly so user code
                // that references IEEE functions still parses.
                SeedHardCodedFallback();
                StubRoot = "(hard-coded fallback — compiler unavailable)";
            }

            _loaded = true;
        }
    }

    /// <summary>
    /// Drops the loaded flag. Used by tests that want to re-exercise the
    /// load path; does NOT clear <see cref="FunctionRegistry"/> (caller's job).
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _loaded   = false;
            _compiler = null;
            StubRoot  = null;
        }
    }

    /// <summary>
    /// Last-resort name list used when even the embedded-resource lookup
    /// fails. Mirrors <c>LibraryBuilder.BuildFallback</c> verbatim so the
    /// degraded behaviour is consistent across both code paths.
    /// </summary>
    private static void SeedHardCodedFallback()
    {
        string[] names =
        {
            "rising_edge", "falling_edge",
            "to_integer", "to_unsigned", "to_signed",
            "resize",
            "shift_left", "shift_right", "rotate_left", "rotate_right",
            "to_x01", "to_x01z", "to_ux01", "is_x",
            "conv_integer", "conv_std_logic_vector"
        };
        foreach (var name in names)
        {
            FunctionRegistry.Instance.Register(new FunctionDefinition
            {
                Name        = name,
                PackageName = "std_logic_1164/numeric_std (fallback)",
                IsPure      = true
            });
        }
    }
}
