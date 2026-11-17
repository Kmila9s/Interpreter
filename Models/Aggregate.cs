using Interpreter.Services;
using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// A VHDL aggregate — <c>(lo =&gt; a, hi =&gt; b)</c>, <c>(a, b)</c>,
/// <c>(0 =&gt; a, 6 =&gt; '1', others =&gt; '0')</c>, <c>(others =&gt; (others =&gt; '0'))</c> —
/// evaluated against the shape of what it initialises or is assigned to: a
/// record (choices are field names, positions are fields in order), an array
/// or vector (choices are indices or ranges, positions follow the declared
/// order). The single aggregate evaluator for declarations (evaluated once)
/// and expressions (an <see cref="IDeferredOperand"/> re-evaluated each run).
/// </summary>
public sealed class Aggregate : IDeferredOperand
{
    private abstract record Choice;
    private sealed record OthersChoice : Choice;
    private sealed record NameChoice(string Name) : Choice;
    private sealed record IndexChoice(IndexValue Left, IndexValue? Right) : Choice;

    /// <summary>One association: choices (null = positional) and the value text.</summary>
    private sealed class Element(IReadOnlyList<Choice>? choices, string text)
    {
        public IReadOnlyList<Choice>? Choices { get; } = choices;
        public string Text { get; } = text;
        public string? Literal { get; init; }
        /// <summary>Inner text when the value is itself an aggregate (<c>(others =&gt; '0')</c>), else null.</summary>
        public string? NestedInner { get; init; }
        public Operation? Value { get; set; }
        public Dictionary<Variable, Aggregate> Nested { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private readonly Variable? _shape;
    private readonly IReadOnlyList<Element> _elements;
    private readonly HashSet<Variable> _scope;

    /// <summary>Element templates of arrays of vectors without one (by width), for nested aggregates.</summary>
    private readonly Dictionary<int, Variable> _unitShapes = [];

    private Aggregate(Variable? shape, IReadOnlyList<Element> elements, HashSet<Variable> scope)
    {
        _shape = shape;
        _elements = elements;
        _scope = scope;
    }

    /// <summary>True when the text inside a pair of parentheses is an aggregate (a top-level <c>,</c> or <c>=&gt;</c>).</summary>
    public static bool IsAggregate(string inner) =>
        VhdlText.TopLevelSplit(inner, ",").Count > 1 || VhdlText.TopLevelSplit(inner, "=>").Count > 1;

    /// <summary>
    /// Parses the text between the aggregate's parentheses for a target of
    /// <paramref name="shape"/> (null: positional elements are concatenated as
    /// written). Identifiers in values and index choices resolve in <paramref name="scope"/>.
    /// </summary>
    public static Aggregate Parse(string inner, Variable? shape, HashSet<Variable> scope)
    {
        var elements = new List<Element>();
        foreach (string association in VhdlText.TopLevelSplit(inner, ","))
        {
            var sides = VhdlText.TopLevelSplit(association, "=>");
            string valueText = sides[^1].Trim();
            IReadOnlyList<Choice>? choices = sides.Count > 1
                ? VhdlText.TopLevelSplit(sides[0], "|").Select(c => ParseChoice(c.Trim(), shape, scope)).ToList()
                : null;
            string? nested = valueText.Length >= 2 && valueText[0] == '(' && VhdlText.MatchingParen(valueText, 0) == valueText.Length - 1
                             && IsAggregate(valueText[1..^1]) ? valueText[1..^1] : null;
            elements.Add(new Element(choices, valueText)
            {
                Literal = HdlLiteral.TryBits(valueText, out string bits) ? bits : null,
                NestedInner = nested,
            });
        }
        return new Aggregate(shape, elements, scope);
    }

    /// <inheritdoc/>
    public int Width => _shape?.Layout?.Width ?? Math.Max(_shape?.Size ?? 0, 0);

    /// <summary>Evaluates every element now and returns the packed bits.</summary>
    public string Bits()
    {
        if (_shape?.Layout is { } layout) return RecordBits(layout);
        if (_shape is null || _shape.Size <= 0)
            return string.Concat(_elements.Select(e => FieldCodec.LogicBits(ValueOf(e), 1)));
        return ArrayBits(_shape);
    }

    /// <inheritdoc/>
    public Variable Evaluate()
    {
        string bits = Bits();
        var value = new Variable("Aggregate", bits, Constants.DATATYPES.STD_LOGIC_VECTOR, bits.Length);
        if (_shape is not null) value.CopyShapeFrom(_shape);
        return value;
    }

    /// <inheritdoc/>
    public IEnumerable<Variable>? Inputs => _elements.All(e => e.Literal is not null) ? [] : null;

    private string RecordBits(RecordLayout layout)
    {
        var parts = new string?[layout.Fields.Count];
        Element? others = null;
        int position = 0;
        foreach (var e in _elements)
        {
            if (e.Choices is null)
            {
                if (position < parts.Length) parts[position] = FieldBits(e, layout.Fields[position]);
                position++;
                continue;
            }
            foreach (var choice in e.Choices)
            {
                if (choice is OthersChoice) others = e;
                else if (choice is NameChoice n && layout.IndexOf(n.Name) is int i and >= 0)
                    parts[i] = FieldBits(e, layout.Fields[i]);
            }
        }
        for (int i = 0; i < parts.Length; i++)
            parts[i] ??= others is not null ? FieldBits(others, layout.Fields[i]) : FieldCodec.DefaultBits(layout.Fields[i]);
        return string.Concat(parts);
    }

    private string ArrayBits(Variable shape)
    {
        int width = shape.Size;
        int unit = shape.ElementSize > 1 ? shape.ElementSize : 1;
        var parts = new string?[width / unit];
        Element? others = null;
        int position = 0;
        foreach (var e in _elements)
        {
            if (e.Choices is null)
            {
                if (position < parts.Length) parts[position] = UnitBits(e, unit, shape.ElementShape);
                position++;
                continue;
            }
            foreach (var choice in e.Choices)
            {
                if (choice is OthersChoice) { others = e; continue; }
                if (choice is not IndexChoice ix) continue;
                int left = ix.Left.Evaluate(), right = ix.Right?.Evaluate() ?? left;
                if (left < 0 || right < 0) continue;
                string bits = UnitBits(e, unit, shape.ElementShape);
                for (int index = Math.Min(left, right); index <= Math.Max(left, right); index++)
                    if (VectorIndexing.Element(shape, index, width) is { } slot)
                        parts[slot.Start / unit] = bits;
            }
        }
        string fill = others is not null ? UnitBits(others, unit, shape.ElementShape) : new string('U', unit);
        for (int i = 0; i < parts.Length; i++)
            parts[i] ??= fill;
        return string.Concat(parts);
    }

    private string FieldBits(Element e, FieldSlot field) =>
        NestedFor(e, field.Shape) is { } nested
            ? VectorIndexing.Fit(nested.Bits(), field.Width)
            : field.Kind == FieldKind.Vector
                ? VectorBits(ValueOf(e), field.Width)
                : FieldCodec.Encode(field, ValueOf(e));

    private string UnitBits(Element e, int width, Variable? elementShape)
    {
        if (e.NestedInner is null) return VectorBits(ValueOf(e), width);
        if (elementShape is null && !_unitShapes.TryGetValue(width, out elementShape))
            _unitShapes[width] = elementShape = new Variable { Type = Constants.DATATYPES.STD_LOGIC_VECTOR, Size = width };
        return VectorIndexing.Fit(NestedFor(e, elementShape)!.Bits(), width);
    }

    /// <summary>The element's value as a nested aggregate of <paramref name="shape"/>, when it is one.</summary>
    private Aggregate? NestedFor(Element e, Variable shape)
    {
        if (e.NestedInner is null) return null;
        if (!e.Nested.TryGetValue(shape, out var nested))
            e.Nested[shape] = nested = Parse(e.NestedInner, shape, _scope);
        return nested;
    }

    private object? ValueOf(Element e)
    {
        if (e.Literal is not null) return e.Literal;
        e.Value ??= new Operation(e.Text, _scope);
        return e.Value.Evaluate() switch
        {
            Variable v => v.Value,
            var other => other,
        };
    }

    /// <summary>
    /// Bits of a vector element: bit strings as written, decimal integers in
    /// binary (negative values masked), anything else <c>'U'</c>; right-aligned
    /// to <paramref name="width"/>.
    /// </summary>
    private static string VectorBits(object? value, int width)
    {
        string s = FieldCodec.LogicBits(value, width);
        if (s.Length == 0) return new string('U', width);
        if (!s.All(c => c is '0' or '1' or 'U' or 'X' or 'Z' or '-' or 'u' or 'x' or 'z'))
        {
            if (!long.TryParse(s, out long n)) return new string('U', width);
            s = FieldCodec.IntegerBits(n, Math.Max(width, 1));
        }
        return VectorIndexing.Fit(s, width);
    }

    private static Choice ParseChoice(string text, Variable? shape, HashSet<Variable> scope)
    {
        if (text.Equals("others", StringComparison.OrdinalIgnoreCase)) return new OthersChoice();
        if (shape?.Layout is not null) return new NameChoice(text);
        var (left, right) = AssignmentTarget.SplitRange(text);
        return new IndexChoice(IndexValue.Parse(left, scope), right is null ? null : IndexValue.Parse(right, scope));
    }

}
