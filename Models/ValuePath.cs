using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// A name with selectors — <c>v</c>, <c>v(3)</c>, <c>v(3 downto 2)</c>,
/// <c>p.lo</c>, <c>q.a.hi</c>, <c>r.d(2)</c>, <c>m(i).lo</c> — and the single
/// rule that turns it into the span of the base object's MSB-first value it
/// denotes. Used for assignment targets (<see cref="AssignmentTarget"/>) and for
/// field reads in expressions, so a selector means the same on both sides.
/// </summary>
public sealed class ValuePath
{
    /// <summary>One selector: <c>(i)</c>, <c>(l to|downto r)</c> or <c>.field</c>.</summary>
    public abstract record Step;

    /// <summary>Index <c>(Left)</c> or slice <c>(Left to|downto Right)</c>.</summary>
    public sealed record IndexStep(IndexValue Left, IndexValue? Right) : Step;

    /// <summary>Record element selection <c>.Name</c>.</summary>
    public sealed record FieldStep(string Name) : Step;

    /// <summary>Where a path lands: span of the base value, the record field it ends on (if any) and that part's shape.</summary>
    public readonly record struct Span(int Start, int Width, FieldSlot? Field, Variable? Shape);

    /// <summary>The object the path starts from.</summary>
    public Variable Base { get; }

    /// <summary>Selectors in source order (empty for a bare name).</summary>
    public IReadOnlyList<Step> Steps { get; }

    private ValuePath(Variable baseVar, IReadOnlyList<Step> steps)
    {
        Base = baseVar;
        Steps = steps;
        _readName = $"{baseVar.Name}_path";
        _isStatic = steps.OfType<IndexStep>()
            .All(ix => ix.Left.StaticValue is not null && (ix.Right is null || ix.Right.StaticValue is not null));
    }

    private readonly string _readName;

    /// <summary>True when no selector depends on run-time values: the span is resolved once.</summary>
    private readonly bool _isStatic;

    /// <summary>True when every index is constant (the part written is known before simulation).</summary>
    public bool IsStatic => _isStatic;

    private Span? _staticSpan;

    /// <summary>True when some selector is a record field.</summary>
    public bool HasField => Steps.Any(s => s is FieldStep);

    /// <summary>
    /// Parses <paramref name="text"/> against <paramref name="scope"/>; null when
    /// it is not a declared name followed only by index/slice/field selectors.
    /// </summary>
    public static ValuePath? Parse(string text, HashSet<Variable> scope)
    {
        int pos = 0;
        string? name = Identifier(text, ref pos);
        if (name is null) return null;
        var baseVar = scope.FirstOrDefault(v => v is not null && !string.IsNullOrEmpty(v.Name)
                                                && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (baseVar is null) return null;

        var steps = new List<Step>();
        while (true)
        {
            SkipSpaces(text, ref pos);
            if (pos >= text.Length) break;
            if (text[pos] == '(')
            {
                int close = VhdlText.MatchingParen(text, pos);
                if (close < 0) return null;
                var (left, right, _) = AssignmentTarget.SplitRange(text[(pos + 1)..close]);
                steps.Add(new IndexStep(IndexValue.Parse(left, scope),
                    right == null ? null : IndexValue.Parse(right, scope)));
                pos = close + 1;
            }
            else if (text[pos] == '.')
            {
                pos++;
                string? field = Identifier(text, ref pos);
                if (field is null) return null;
                steps.Add(new FieldStep(field));
            }
            else return null;
        }
        return new ValuePath(baseVar, steps);
    }

    /// <summary>
    /// Evaluates the selectors (dynamic indices read their signals now) and
    /// returns the span they denote, or null when an index is unresolved or
    /// out of range, or a field does not exist.
    /// </summary>
    public Span? Resolve()
    {
        if (_staticSpan is { } cached) return cached;
        int start = 0;
        int width = Math.Max(Base.Value?.ToString()?.Length ?? 0, Base.Size);
        Variable? shape = Base;
        FieldSlot? field = null;
        foreach (var step in Steps)
        {
            if (shape is null) return null;
            switch (step)
            {
                case IndexStep ix:
                    int l = ix.Left.Evaluate();
                    int r = ix.Right?.Evaluate() ?? l;
                    if (l < 0 || r < 0) return null;
                    var part = ix.Right is null
                        ? (_isStatic ? VectorIndexing.Element(shape, l, width) : VectorIndexing.ElementAtRunTime(shape, l, width))
                        : VectorIndexing.Range(shape, l, r, width);
                    if (part is not { } p) return null;
                    start += p.Start;
                    width = p.Width;
                    // An element continues with the element template; a bit or a
                    // slice ends the path.
                    shape = ix.Right is null ? shape.ElementShape : null;
                    field = null;
                    break;
                case FieldStep f:
                    if (shape.Layout?.Field(f.Name) is not { } slot) return null;
                    start += slot.Offset;
                    width = slot.Width;
                    shape = slot.Shape;
                    field = slot;
                    break;
            }
        }
        var span = new Span(start, width, field, shape);
        if (_isStatic) _staticSpan = span;
        return span;
    }

    /// <summary>
    /// The span when every index is a constant (else null), as written indices
    /// of <see cref="Base"/>: for a plain <c>v(i)</c>/<c>v(l downto r)</c> the
    /// indices themselves; for field paths of an object without a declared range
    /// (records), the bit positions of the conventional <c>Size-1 downto 0</c>.
    /// </summary>
    public (int Left, int Right)? StaticIndices
    {
        get
        {
            if (Steps is [IndexStep { Left.StaticValue: int l } only])
                return only.Right is null ? (l, l) : only.Right.StaticValue is int r ? (l, r) : null;
            if (!HasField || Base.HasDeclaredRange
                || Steps.Any(s => s is IndexStep { Left.StaticValue: null } or IndexStep { Right.StaticValue: null, Right: not null }))
                return null;
            if (Resolve() is not { } span) return null;
            int size = Math.Max(Base.Size, span.Start + span.Width);
            return (size - 1 - span.Start, size - span.Start - span.Width);
        }
    }

    /// <summary>
    /// Reads the denoted part now, typed: a record field decodes through
    /// <see cref="FieldCodec"/> (integer, enum literal, boolean, bits), a bit
    /// is <c>STD_LOGIC</c>, anything wider a vector carrying its shape. An
    /// unresolved path reads <c>'U'</c>.
    /// </summary>
    public Variable Read()
    {
        string name = _readName;
        string vec = Base.Value?.ToString() ?? string.Empty;
        // A not-yet-driven object may hold the one-character shorthand ("U")
        // for its whole value: widen it so every field reads with its type.
        if (vec.Length < Base.Size)
            vec = vec.Length == 1 ? new string(vec[0], Base.Size) : vec.PadLeft(Base.Size, 'U');
        if (Resolve() is not { } at || at.Start + at.Width > vec.Length)
            return new Variable(name, "U", Constants.DATATYPES.STD_LOGIC);
        string bits = vec.Substring(at.Start, at.Width);
        if (at.Field is { } field)
            return FieldCodec.Decode(field, bits, name);
        if (at.Shape is null && at.Width == 1)
            return new Variable(name, bits, Constants.DATATYPES.STD_LOGIC);
        var part = new Variable(name, bits, Constants.DATATYPES.STD_LOGIC_VECTOR, bits.Length);
        if (at.Shape is not null) part.CopyShapeFrom(at.Shape);
        return part;
    }

    /// <summary>
    /// Shape of the denoted part without evaluating indices: an element is the
    /// array's element template, a field its subtype template, a slice the same
    /// shape as its prefix. Null when a step has no shape to continue with (a
    /// bit of a plain vector). Gives aggregates on the right-hand side their
    /// target type.
    /// </summary>
    public Variable? StaticShape
    {
        get
        {
            Variable? shape = Base;
            foreach (var step in Steps)
                if ((shape = NextShape(shape, step)) is null) return null;
            return shape;
        }
    }

    /// <summary>
    /// Why a field selection is invalid (prefix is not a record, or has no such
    /// field) — GHDL rejects these at analysis — or null when every field exists.
    /// </summary>
    public string? FieldError
    {
        get
        {
            Variable? shape = Base;
            string prefix = Base.Name;
            foreach (var step in Steps)
            {
                if (step is FieldStep f)
                {
                    if (shape?.Layout is not { } layout)
                        return $"\"{prefix}\" is not a record: no field \"{f.Name}\"";
                    if (layout.Field(f.Name) is not { } slot)
                        return $"no field \"{f.Name}\" in record type \"{layout.TypeName}\"";
                    prefix += "." + f.Name;
                }
                else prefix += "(…)";
                shape = NextShape(shape, step);
            }
            return null;
        }
    }

    /// <summary>
    /// The single shape transition of a selector: an element is the array's
    /// element template (a synthesized vector for arrays of vectors without
    /// one), a field its subtype template, a slice keeps its prefix's shape;
    /// null when there is nothing to continue with (a bit of a plain vector).
    /// </summary>
    private static Variable? NextShape(Variable? shape, Step step) => step switch
    {
        IndexStep { Right: not null } => shape,
        IndexStep => shape?.ElementShape
                     ?? (shape is { ElementSize: > 1 } array
                         ? new Variable { Type = Constants.DATATYPES.STD_LOGIC_VECTOR, Size = array.ElementSize }
                         : null),
        FieldStep f => shape?.Layout?.Field(f.Name)?.Shape,
        _ => null,
    };

    /// <summary>Every index expression of the path (for dependency tracking).</summary>
    public IEnumerable<IndexValue> Indices =>
        Steps.OfType<IndexStep>().SelectMany(s => s.Right is null ? new[] { s.Left } : new[] { s.Left, s.Right });

    private static string? Identifier(string text, ref int pos)
    {
        SkipSpaces(text, ref pos);
        int begin = pos;
        if (pos >= text.Length || !(char.IsLetter(text[pos]) || text[pos] == '_')) return null;
        while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_')) pos++;
        return text[begin..pos];
    }

    private static void SkipSpaces(string text, ref int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
    }
}
