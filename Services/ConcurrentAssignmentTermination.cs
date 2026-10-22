using System;
using System.Collections.Generic;
using Parser.Models;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Services;

/// <summary>
/// Shared detector for the C11 check — a concurrent signal assignment whose RHS
/// ran past a missing ';' into the next statement. Centralises the single
/// false-positive-sensitive decision used by both the architecture-level parser
/// (<see cref="Architecture"/>) and generate blocks
/// (<c>Interpreter.Repositories.GenerateBlock</c>), so the tricky
/// conditional-context logic lives in exactly one auditable place.
/// </summary>
internal static class ConcurrentAssignmentTermination
{
    /// <summary>
    /// Operator keywords that the tokenizer may classify as <c>IDENTIFIERS</c>
    /// rather than <c>KEYWORDS</c> — notably <c>mod</c>, which is absent from the
    /// keyword regex. A token with one of these values is an operator, never an
    /// operand, so it must not satisfy the operand test below. (The rest are
    /// already typed <c>KEYWORDS</c>; they are listed for belt-and-suspenders.)
    /// </summary>
    private static readonly HashSet<string> OperatorKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "nand", "nor", "xor", "xnor",
        "mod", "rem", "abs", "sll", "srl", "sla", "sra", "rol", "ror",
    };

    /// <summary>
    /// True when <paramref name="cur"/> is a stray assignment operator at the top
    /// level of a concurrent RHS — i.e. the ';' terminating the assignment is
    /// missing and the next statement has already begun.
    /// </summary>
    /// <param name="cur">The token under inspection.</param>
    /// <param name="parenDepth">Current parenthesis nesting (0 = top level).</param>
    /// <param name="inCondition">True while inside a when…else CONDITION, where a
    /// '&lt;=' is a relational comparison rather than an assignment operator.</param>
    /// <param name="history">The RHS tokens emitted BEFORE <paramref name="cur"/>, in order.</param>
    public static bool IsStrayAssignmentOperator(
        Token cur, int parenDepth, bool inCondition, IReadOnlyList<Token> history)
    {
        if (parenDepth != 0) return false;
        var val = cur.Value;

        // ':=' is never valid inside a concurrent RHS expression → always a
        // missing ';' (the next statement is a variable assignment).
        if (val == ":=") return true;
        if (val != "<=") return false;

        // A '<=' in a waveform/value context (not inside a condition) is the next
        // statement's signal assignment.
        if (!inCondition) return true;

        // Inside a when…else CONDITION, a '<=' is normally a relational
        // comparison. It is the next statement ONLY when it reads as
        // `<operand> <target> <=` — two operands with no operator between them,
        // which is never valid in a boolean condition. The target may be a plain
        // identifier (`z <=`) or an indexed/sliced name (`z(0) <=`, whose last
        // token is ')').
        if (history is null || history.Count == 0) return false;

        Token last = history[^1];
        Token? target;
        int precedingIdx;

        if (last.Type == IDENTIFIERS)
        {
            target = last;
            precedingIdx = history.Count - 2;
        }
        else if (last.Value == ")")
        {
            // Walk back to the matching '(' then the identifier before it (the base
            // target name), so an indexed/sliced next-target `z(0) <=` is seen.
            int depth = 0, i = history.Count - 1;
            for (; i >= 0; i--)
            {
                if (history[i].Value == ")") depth++;
                else if (history[i].Value == "(") { depth--; if (depth == 0) break; }
            }
            if (i - 1 < 0 || history[i - 1].Type != IDENTIFIERS) return false;
            target = history[i - 1];
            precedingIdx = i - 2;
        }
        else
        {
            return false;
        }

        // Target must be a plain identifier that isn't an operator keyword leaking
        // in as IDENTIFIERS (mod). Time units / attributes are other types already.
        if (OperatorKeywords.Contains(target.Value)) return false;

        // The token before the target must END an operand (identifier, literal,
        // qualified name, or a closing ')'), and not be an operator keyword — so a
        // comparison like `a and cnt <= max` (preceding = `and`, KEYWORDS) and the
        // condition's first operand (preceding = `when`) are never flagged.
        if (precedingIdx < 0) return false;
        Token preceding = history[precedingIdx];
        bool precedingIsOperand =
            preceding.Type == IDENTIFIERS ||
            preceding.Type == LITERALS ||
            preceding.Type == QUALIFIED_IDENTIFIERS ||
            preceding.Value == ")";
        if (!precedingIsOperand) return false;
        if (OperatorKeywords.Contains(preceding.Value)) return false;

        return true;
    }
}
