using Parser.Models;

namespace Interpreter.Repositories;

/// <summary>
/// Represents a VHDL <c>block</c> statement:
/// <code>
/// label : block [(guard_expression)]
///     -- local declarative part (signals, constants)
/// begin
///     -- concurrent statements (may include guarded assignments and nested blocks)
/// end block [label];
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// Blocks are elaborated by <see cref="Interpreter.Services.Architecture"/> by
/// <em>flattening</em>: the block's local signals are hoisted into the enclosing
/// architecture's signal scope and its concurrent statements are appended to the
/// architecture's task list. This matches the flatten semantics the sibling
/// <c>Parser</c> module already uses and integrates directly with the flat
/// <see cref="Interpreter.Services.DeltaCycleEngine"/> (which consumes a single
/// task list + signal set).
/// </para>
/// <para><strong>Guard simulation.</strong> A guarded block carries a guard
/// expression. Guarded concurrent assignments inside it
/// (<c>target &lt;= guarded waveform;</c>) are gated on that guard via
/// <see cref="BuildGuardedRhs"/>: the driver only drives the waveform while the
/// guard is true, and is disconnected (drives the high-impedance / disconnection
/// value) while the guard is false. Because the guard expression is re-evaluated
/// on every delta cycle by the resulting <see cref="ConditionalAssignment"/>, the
/// guard is genuinely simulated dynamically rather than resolved once at
/// elaboration time.
/// </para>
/// <para><strong>Limitations.</strong> Block-local signals are flattened into the
/// architecture scope (names must not collide with architecture-level signals).
/// Disconnection is modeled by driving the disconnection value immediately; the
/// <c>disconnect ... after</c> timing specification is not modeled.
/// </para>
/// </remarks>
public class BlockStatement
{
    /// <summary>Gets the block's label.</summary>
    public string Label { get; }

    /// <summary>
    /// Gets or sets the guard expression captured from <c>block (guard_expression)</c>,
    /// or the empty string for an unguarded block.
    /// </summary>
    public string GuardExpression { get; set; } = string.Empty;

    /// <summary>Gets whether this block declares a guard expression.</summary>
    public bool IsGuarded => !string.IsNullOrWhiteSpace(GuardExpression);

    /// <summary>Gets the block's local signal declarations (hoisted to architecture scope).</summary>
    public List<Variable> LocalSignals { get; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BlockStatement"/> class.
    /// </summary>
    /// <param name="label">The block label.</param>
    public BlockStatement(string label)
    {
        Label = label;
    }

    /// <summary>
    /// Lowers a guarded concurrent assignment's right-hand side into an equivalent
    /// guard-gated <c>when/else</c> chain.
    /// </summary>
    /// <remarks>
    /// The result prepends a disconnection branch so the whole original waveform
    /// (including its own default) still applies while the guard is true:
    /// <code>
    /// disconnectValue when not ( guard ) else originalRhs
    /// </code>
    /// When the guard is false the first branch fires and drives
    /// <paramref name="disconnectValue"/> (driver disconnected); otherwise the
    /// original waveform is used unchanged. The chain is re-evaluated every delta
    /// cycle, so the guard is honored dynamically.
    /// </remarks>
    /// <param name="originalRhs">The original right-hand side (with the <c>guarded</c> keyword already stripped).</param>
    /// <param name="disconnectValue">The value driven while the guard is false (e.g. <c>'Z'</c> or <c>( others =&gt; 'Z' )</c>).</param>
    /// <returns>The gated right-hand side expression string.</returns>
    public string BuildGuardedRhs(string originalRhs, string disconnectValue)
        => $"{disconnectValue} when not ( {GuardExpression} ) else {originalRhs}";
}
