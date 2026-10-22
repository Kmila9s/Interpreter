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
    /// <param name="prev">The token immediately before <paramref name="cur"/>, or null.</param>
    /// <param name="prevPrev">The token two before <paramref name="cur"/>, or null.</param>
    public static bool IsStrayAssignmentOperator(
        Token cur, int parenDepth, bool inCondition, Token? prev, Token? prevPrev)
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
        // `<operand> <target-identifier> <=` — two operands with no operator
        // between them, which is never valid in a boolean condition. Everything
        // below is a guard to keep false positives at zero.
        if (prev is null || prevPrev is null) return false;

        // (1) The target candidate must be a plain identifier. This already
        // excludes time units (TIME_VARIABLES), attributes (VECTORS_ATTRIBUTES),
        // ')', literals and operators by type; also exclude operator keywords
        // that leak in as IDENTIFIERS (mod).
        if (prev.Type != IDENTIFIERS) return false;
        if (OperatorKeywords.Contains(prev.Value)) return false;

        // (2) The token before the target must END an operand (identifier,
        // literal, qualified name, or a closing ')'), and must not be an operator
        // keyword — so a comparison like `a and cnt <= max` (prevPrev = `and`,
        // KEYWORDS) is never flagged.
        bool prevPrevIsOperand =
            prevPrev.Type == IDENTIFIERS ||
            prevPrev.Type == LITERALS ||
            prevPrev.Type == QUALIFIED_IDENTIFIERS ||
            prevPrev.Value == ")";
        if (!prevPrevIsOperand) return false;
        if (OperatorKeywords.Contains(prevPrev.Value)) return false;

        return true;
    }
}
