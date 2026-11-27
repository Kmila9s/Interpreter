using Parser.Models;

namespace Interpreter.Ir;

/// <summary>The direction of a port.</summary>
public enum IrPortMode
{
    /// <summary>Driven from outside, read inside.</summary>
    In,
    /// <summary>Driven inside.</summary>
    Out,
    /// <summary>Driven from both sides (resolved).</summary>
    InOut,
    /// <summary>Driven inside and readable inside (VHDL <c>buffer</c>).</summary>
    Buffer,
}

/// <summary>What a design object is: where it is declared and whether it can change.</summary>
public enum IrObjectKind
{
    /// <summary>A port of the design unit (<see cref="IrSignal.PortMode"/> says its direction).</summary>
    Port,
    /// <summary>A generic (constant for the run).</summary>
    Generic,
    /// <summary>A signal declared in the body (architecture, block).</summary>
    Signal,
    /// <summary>A constant declared in the body or in a process.</summary>
    Constant,
    /// <summary>A process-local variable.</summary>
    Variable,
}

/// <summary>The language-neutral logic type of an object.</summary>
public enum IrTypeKind
{
    /// <summary>One resolved/unresolved logic value (9 values: U X 0 1 Z W L H -).</summary>
    Logic,
    /// <summary>A vector of logic values (also signed/unsigned: see <see cref="IrSignal.Signed"/>).</summary>
    LogicVector,
    /// <summary>One two-valued bit.</summary>
    Bit,
    /// <summary>true/false.</summary>
    Boolean,
    /// <summary>An integer (or an integer subtype).</summary>
    Integer,
    /// <summary>An enumeration (<see cref="IrSignal.EnumLiterals"/>).</summary>
    Enum,
    /// <summary>A record (<see cref="IrSignal.Layout"/>).</summary>
    Record,
    /// <summary>An array of elements that are not plain logic values (memories, arrays of records).</summary>
    Array,
    /// <summary>One character.</summary>
    Character,
    /// <summary>A character string.</summary>
    String,
    /// <summary>A type the front-end could not classify.</summary>
    Unknown,
}

/// <summary>A declared index range: <c>Left</c> to/downto <c>Right</c>.</summary>
public sealed record IrRange(int Left, int Right, bool Downto);

/// <summary>
/// One object of a design (port, generic, signal, constant or process variable) as plain,
/// language-neutral data: what it is and its shape, never its value. A front-end produces it
/// (<see cref="Vhdl.VhdlDesignAdapter"/>); the run holds the value elsewhere (until v3.2.3,
/// in the parsed object <see cref="IrBinding.Cell"/> returns). Compared by reference: two
/// objects with the same shape are different objects.
/// </summary>
public sealed class IrSignal
{
    /// <summary>The declared name.</summary>
    public required string Name { get; init; }

    /// <summary>Where it is declared.</summary>
    public required IrObjectKind Kind { get; init; }

    /// <summary>The direction of a port; null for any other kind.</summary>
    public IrPortMode? PortMode { get; init; }

    /// <summary>Its logic type.</summary>
    public required IrTypeKind Type { get; init; }

    /// <summary>Width in bits (1 for a scalar).</summary>
    public required int Width { get; init; }

    /// <summary>The declared index range, or null when it has none (scalars, unconstrained).</summary>
    public IrRange? Range { get; init; }

    /// <summary>True for a vector read as a signed number (VHDL <c>signed</c>).</summary>
    public bool Signed { get; init; }

    /// <summary>The fields of a record (<see cref="IrTypeKind.Record"/>), or null.</summary>
    public RecordLayout? Layout { get; init; }

    /// <summary>The literals of an enumeration (<see cref="IrTypeKind.Enum"/>), or null.</summary>
    public IReadOnlyList<string>? EnumLiterals { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Kind} {Name} : {Type}[{Width}]";
}
