# Kmila-9s VHDL Interpreter - Technical Documentation

**Version:** 0.15.0
**Date:** December 3, 2025
**Author:** Ulrich Tamayo Daniel
**Maintainer:** Kmila-9s Development Team

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Key Components](#key-components)
4. [Recent Changes (v0.15.0)](#recent-changes-v0150)
5. [Code Examples](#code-examples)
6. [Common Issues and Solutions](#common-issues-and-solutions)
7. [Developer Guide](#developer-guide)

---

## Overview

The VHDL Interpreter is a behavioral simulation engine that executes VHDL code without requiring synthesis or physical hardware. It processes the token stream from the Parser module and simulates digital circuit behavior through event-driven execution.

### Key Capabilities

- **Expression Evaluation**: Full support for arithmetic, logical, and comparison operators
- **Control Structures**: if/elsif/else, case/when, for/while/loop constructs
- **IEEE Functions**: rising_edge, falling_edge, type conversions, vector operations
- **Signal Attributes**: 'event, 'last_value, 'stable, 'active
- **Nested Structures**: Unlimited nesting of if, case, and loop statements
- **Component Handling**: Skips structural instantiation, focuses on behavioral simulation

---

## Architecture

### Parsing Pipeline

```
VHDL Source Code
    ↓
[Parser Module] - Tokenization
    ↓
Token Stream
    ↓
[Entity] - Entity/Port/Generic parsing
    ↓
[Architecture] - Signal declarations, behavior synthesis
    ↓
[Process] - Sequential statement parsing
    ↓
[Control Structures] - If/Case/Loop synthesis
    ↓
[Operation] - Expression evaluation
    ↓
[ShuntingYard] - Infix to postfix conversion
    ↓
[Operator] - Actual computation
    ↓
Result (Updated signal/variable values)
```

### Class Hierarchy

```
ISControl (Interface)
├── IfStructure
├── CaseStructure
├── ForLoopStructure
├── WhileLoopStructure
├── SimpleLoopStructure
├── Process
└── Architecture

IOperation (Interface)
├── IArithOperation
│   └── Operator (implements arithmetic)
└── ILogicOperation
    └── Operator (implements logic)
```

---

## Key Components

### 1. ShuntingYard (Expression Evaluator)

**File:** `/Repositories/ShuntingYard.cs`

**Purpose:** Converts infix expressions to postfix notation for correct precedence evaluation.

**Algorithm:**
1. Scan tokens left to right
2. Operands go directly to output queue
3. Operators go to operator stack based on precedence
4. Parentheses force precedence groups
5. Final output is postfix (RPN) expression

**v0.15.0 Fix:**
```csharp
// Changed from direct cast to Convert.ToInt32()
// Handles Precedence being stored as 'object' containing enum
Convert.ToInt32(_operators.Peek().Precedence) <= Convert.ToInt32(operation.Precedence)
```

**Why This Matters:**
- Precedence property is `object` type (can be PrecedencesLogicOperators or PrecedencesArithOperators)
- Direct cast `(int)` fails when object contains enum
- `Convert.ToInt32()` properly unboxes enum values

### 2. Operation (Expression Handler)

**File:** `/Services/Operation.cs`

**Purpose:** Orchestrates expression parsing, synthesis, and evaluation.

**Key Methods:**
- `Syntetize()`: Tokenizes and processes expression into evaluable form
- `Execute()`: Evaluates expression using ShuntingYard and returns result
- `TryParseStandardFunction()`: Detects and evaluates IEEE standard functions
- `ParseIndexedAccess()`: Handles vector indexing and slicing

**v0.15.0 Improvements:**

1. **Keyword Skipping:**
   ```csharp
   // Skip 'downto'/'to' in ranges
   if (currentItem.ToLower() == "downto" || currentItem.ToLower() == "to")
       continue;

   // Skip 'when...else' in conditional assignments
   if (currentItem.ToLower() == "when")
       /* skip to 'else' */
   ```

2. **Unary Operator Type Checking:**
   ```csharp
   // Only treat as unary NOT if it's a LOGIC_OPERATION
   bool isUnaryNot = op.OperatorType == LOGIC_OPERATION &&
                     op.Precedence is PrecedencesLogicOperators &&
                     (PrecedencesLogicOperators)op.Precedence == NOT;
   ```

### 3. Architecture (Behavioral Synthesizer)

**File:** `/Services/Architecture.cs`

**Purpose:** Parses and manages the architecture's declarative and behavioral regions.

**v0.15.0 Features:**

1. **Labeled Statement Detection:**
   ```vhdl
   -- Labeled process
   timer_proc : process(clk, reset)

   -- Labeled component instantiation
   adder_inst : Adder port map (a => sig_a, b => sig_b);

   -- Labeled generate
   gen_label : for i in 0 to 7 generate
   ```

2. **Component Instantiation Skipping:**
   - Detects `label : IDENTIFIER` pattern
   - Tracks parenthesis depth through generic/port maps
   - Skips to semicolon ending the instantiation
   - Allows behavioral simulation to focus on processes

### 4. IfStructure (Conditional Handler)

**File:** `/Repositories/IfStructure.cs`

**Purpose:** Manages if-elsif-else conditional execution.

**v0.15.0 Enhancement:**
```csharp
case "case":
    // Nested case statement - allows case statements inside if/elsif/else branches
    CaseStructure nestedCase = new(_variables, _streamer);
    nestedCase.Syntetize();
    branch.Tasks.Add(nestedCase);
    ConsumeEndKeyword("case");
    break;
```

**Why This Matters:**
- Complex state machines often use case inside if branches
- Example: `if enable='1' then case state is when IDLE => ...`

### 5. VariableSyntetizer (Declaration Parser)

**File:** `/Services/VariableSyntetizer.cs`

**Purpose:** Parses signal, variable, port, and generic declarations.

**v0.15.0 Critical Fix:**
```csharp
// For ports/generics with complex expressions, skip evaluation
if (_isPort || _isGeneric)
{
    // Skip entire range without evaluating
    // Example: (NUM_INPUTS * DATA_WIDTH) - 1 downto 0
    int parenDepth = 1;
    while (parenDepth > 0) {
        if (current == "(") parenDepth++;
        else if (current == ")") parenDepth--;
        moveNext();
    }
    VectorSize = 8; // Default for complex expressions
}
```

**Why This Matters:**
- Port widths may use generics: `std_logic_vector(DATA_WIDTH-1 downto 0)`
- May use functions defined later: `std_logic_vector(clog2(DEPTH) downto 0)`
- Cannot evaluate during entity parsing - generics have no values yet

### 6. StandardFunctions (IEEE Library)

**File:** `/Services/StandardFunctions.cs`

**Purpose:** Implements IEEE std_logic_1164 and numeric_std functions.

**v0.15.0 ToInteger() Enhancement:**
```csharp
public static int ToInteger(string slv)
{
    string cleaned = slv.Replace("\"", "").Replace("'", "").Trim();

    // Safety checks (v0.15.0)
    if (string.IsNullOrEmpty(cleaned)) return 0;
    if (cleaned.Contains('U') || cleaned.Contains('X') || cleaned.Contains('Z')) return 0;

    // Try decimal first
    if (int.TryParse(cleaned, out int dec)) return dec;

    // Try binary with error handling
    try { return Convert.ToInt32(cleaned, 2); }
    catch { return 0; }
}
```

**Supported Functions:**

| Function | Description | Example |
|----------|-------------|---------|
| `rising_edge(clk)` | Detects 0→1 transition | `if rising_edge(clk) then` |
| `falling_edge(clk)` | Detects 1→0 transition | `if falling_edge(reset) then` |
| `to_integer(vec)` | Converts vector to int | `count := to_integer(counter);` |
| `to_unsigned(val, width)` | Int to unsigned vector | `temp := to_unsigned(5, 8);` |
| `resize(vec, width)` | Resize vector | `extended := resize(data, 16);` |
| `shift_left(vec, amt)` | Logical left shift | `shifted := shift_left(reg, 2);` |
| `clog2(val)` | Ceiling log₂ | `BITS : integer := clog2(DEPTH);` |

---

## Recent Changes (v0.15.0)

### 1. Critical Regex Pattern Fixes

**Problem:** Unanchored patterns caused token misclassification

**Files Modified:**
- `/Parser/Models/Constants.cs`

**Details:**

| Pattern | Before | After | Issue Fixed |
|---------|--------|-------|-------------|
| ASSIGNATION_OPERATORS | `@"^[(\<\=)|\:\=]"` | `@"^((\<\=)|\:\=)$"` | `[` created char class including `(` |
| COMPARATION_OPERATORS | `@"^[=\|<\|>\|!=]"` | `@"^(=\|!=\|/=\|<\|>\|<=\|>=)$"` | Incomplete operator set |
| LOGIC_OPERATORS | `"(and\|or\|xor...)"` | `@"^(and\|or\|xor\|...)$"` | Matched "or" in "memory" |
| ARITH_OPERATORS | `@"(\+\|-\|...)"` | `@"^(\+\|-\|...)$"` | Matched substrings |

**Impact:** Resolved 9 test failures related to expression parsing

### 2. Operator Type Checking

**File:** `/Services/Operation.cs`

**Issue:** `**` (power operator) was treated as unary NOT

**Root Cause:**
- `PrecedencesArithOperators.UNARY = 0`
- `PrecedencesLogicOperators.NOT = 0`
- Without type check, precedence values match

**Solution:**
```csharp
bool isUnaryNot = op.OperatorType == LOGIC_OPERATION &&
                  op.Precedence is PrecedencesLogicOperators &&
                  (PrecedencesLogicOperators)op.Precedence == NOT;
```

### 3. Function/Procedure Depth Tracking

**File:** `/Program.cs`

**Issue:** `end FunctionName;` mistaken for `end ArchitectureName;`

**Solution:**
```csharp
int funcProcDepth = 0;

// When entering function/procedure
if (line.Contains("function ") && line.Contains(" is"))
    funcProcDepth++;

// When exiting
if (line.StartsWith("end ") && funcProcDepth > 0)
{
    funcProcDepth--;
    continue; // Don't check for architecture end
}

// Only check architecture end when depth == 0
if (funcProcDepth > 0) continue;
```

### 4. Nested Case in If Branches

**File:** `/Repositories/IfStructure.cs`

**Added:** Case statement handling in `SyntetizeBranchBody()`

**Enables:**
```vhdl
if button_pressed = '1' then
    case operation is
        when "00" => result <= add;
        when "01" => result <= subtract;
        when others => result <= zero;
    end case;
end if;
```

### 5. Component Instantiation

**File:** `/Services/Architecture.cs`

**Added:** `SkipComponentInstantiation()` method

**Handles:**
```vhdl
cpu_inst : CPU
    generic map (WIDTH => 32)
    port map (
        clk => clk,
        data_in => data,
        data_out => result
    );
```

---

## Code Examples

### Example 1: Using Standard Functions

```vhdl
process(clk, reset)
    variable count : integer := 0;
begin
    if rising_edge(clk) then
        if reset = '1' then
            count := 0;
            output <= (others => '0');
        else
            count := count + 1;
            output <= to_unsigned(count, 8);
        end if;
    end if;
end process;
```

**Interpreter Behavior:**
1. `rising_edge(clk)` calls `StandardFunctions.RisingEdge()`
2. Checks `SignalAttributeHandler.GetEvent(clk)` for signal transition
3. Returns true if clk changed and current value is '1'
4. `to_unsigned(count, 8)` converts integer to 8-bit vector

### Example 2: Nested Case in If

```vhdl
if button_flag = '1' then
    case selector is
        when "00" => opcode <= ADD;
        when "01" => opcode <= SUB;
        when others => opcode <= NOP;
    end case;
elsif timeout = '1' then
    opcode <= RESET;
end if;
```

**Interpreter Flow:**
1. `IfStructure.Syntetize()` parses main if branch
2. In `SyntetizeBranchBody()`, detects `case` keyword
3. Creates `CaseStructure` for nested case
4. `CaseStructure.Syntetize()` parses all when branches
5. Adds case to if branch tasks
6. `ConsumeEndKeyword("case")` consumes `end case;`

### Example 3: Complex Port Expressions

```vhdl
entity Adder is
    generic (
        NUM_INPUTS : integer := 8;
        DATA_WIDTH : integer := 16
    );
    port (
        inputs : in std_logic_vector((NUM_INPUTS * DATA_WIDTH) - 1 downto 0);
        sum : out std_logic_vector(DATA_WIDTH + clog2(NUM_INPUTS) - 1 downto 0)
    );
end Adder;
```

**Interpreter Behavior:**
1. Entity parser reads generic declarations (NUM_INPUTS, DATA_WIDTH)
2. Port parser detects complex expression `(NUM_INPUTS * DATA_WIDTH) - 1`
3. Since `_isPort = true`, skips evaluation (v0.15.0 fix)
4. Tracks parenthesis depth to find closing `)`
5. Uses default VectorSize = 8 for type checking
6. Actual size computed at runtime when generics have values

---

## Common Issues and Solutions

### Issue 1: "Expected KEYWORDS, found IDENTIFIERS"

**Symptom:** Error during entity or architecture parsing

**Common Causes:**
- Component instantiation not properly skipped
- Labeled statement not recognized
- Function/procedure end mistaken for architecture end

**Solution (v0.15.0):**
- Added labeled component detection: `if label : IDENTIFIER`
- Added `SkipComponentInstantiation()` method
- Added funcProcDepth tracking in Program.cs

### Issue 2: "Instance not in this scope"

**Symptom:** Variable/signal not found during expression evaluation

**Common Causes:**
- Keywords (downto, to, when) treated as identifiers
- Generic used in port expression before generics parsed
- Local variable declared but scope check fails

**Solutions:**
- v0.15.0: Skip downto/to/when keywords in Operation.Syntetize()
- v0.15.0: Skip evaluation of port expressions with generics
- v0.15.0: Removed erroneous scope check in VariableSyntetizer

### Issue 3: "Unable to cast object to Int32"

**Symptom:** ShuntingYard fails during precedence comparison

**Cause:** Operator.Precedence is `object` type, direct cast fails for enums

**Solution (v0.15.0):**
```csharp
// WRONG
(int)_operators.Peek().Precedence

// CORRECT
Convert.ToInt32(_operators.Peek().Precedence)
```

### Issue 4: Parentheses Treated as Assignment Operators

**Symptom:** Expression parsing fails with "malformed expression"

**Cause:** Regex `@"^[(\<\=)|\:\=]"` uses `[` creating character class

**Solution (v0.15.0):**
```csharp
// WRONG - [ creates char class including (
ASSIGNATION_OPERATORS = @"^[(\<\=)|\:\=]"

// CORRECT - Use grouping () not char class []
ASSIGNATION_OPERATORS = @"^((\<\=)|\:\=)$"
```

---

## Developer Guide

### Adding a New Standard Function

1. **Add to IsStandardFunction():**
```csharp
public static bool IsStandardFunction(string name)
{
    return name.ToLower() switch
    {
        "my_function" => true,
        // ... existing functions
        _ => false
    };
}
```

2. **Implement the function:**
```csharp
public static int MyFunction(int arg1, int arg2)
{
    // Implementation
    return result;
}
```

3. **Add to EvaluateFunction():**
```csharp
case "my_function":
    if (arguments.Length >= 2)
    {
        int val1 = GetIntValue(arguments[0], variables);
        int val2 = GetIntValue(arguments[1], variables);
        return MyFunction(val1, val2);
    }
    return 0;
```

### Adding a New Control Structure

1. **Create class implementing ISControl:**
```csharp
public class MyStructure : ISControl
{
    public List<object> Tasks { get; private set; } = new();

    public void Syntetize() { /* Parse from token stream */ }
    public async Task ExecuteAsync() { /* Execute tasks */ }
    public void Syntetize(string[] data) => throw new NotImplementedException();
}
```

2. **Add to Architecture.SyntetizeBehaviour():**
```csharp
case "mystructure":
    MyStructure myStruct = new(comparators.ToHashSet(), _streamer);
    myStruct.Syntetize();
    Tasks.Add(myStruct);
    break;
```

3. **Add to Process.SyntetizeBehaviour():** (if applicable)

### Debugging Tips

1. **Enable Debug Output:**
   ```bash
   # Add to top of file
   #define DEBUG

   # Or build in Debug config
   dotnet build -c Debug
   ```

2. **Token Stream Debugging:**
   ```csharp
   #if DEBUG
   Console.WriteLine($"Current: '{_streamer.Current?.Value}' Type: {_streamer.Current?.Type} Line: {_streamer.Current?.Line}");
   #endif
   ```

3. **Expression Debugging:**
   - Add `#define DEBUG_OPERATION` in Operation.cs
   - Shows tokenization, variables, syntetized items

4. **Common Checkpoints:**
   - After `Expect()` calls - verify Current advanced
   - Before while loops - verify exit condition
   - After synthesis - verify token stream at correct position

### Testing Best Practices

1. **Create Minimal Test Case:**
   ```vhdl
   -- test_minimal.vhdl
   entity Test is
   end Test;

   architecture Arch of Test is
   begin
       -- Minimal code to reproduce issue
   end Arch;
   ```

2. **Use Debug Output:** Add debug at suspected failure point

3. **Check Token Stream:** Verify tokens are correctly classified

4. **Verify Precedence:** Ensure operators have correct precedence values

---

## Version History Summary

| Version | Date | Tests Passing | Key Features |
|---------|------|---------------|--------------|
| v0.12.0 | 2025-11-26 | ~5/20 | If-elsif-else support |
| v0.13.0 | 2025-11-27 | 8/8 | Case, loops, signal attributes |
| v0.14.0 | 2025-11-27 | 3/20 | IEEE standard functions |
| v0.15.0 | 2025-12-03 | **16/20** | **Regex fixes, component handling** |

---

## References

- **VHDL Language Reference Manual (LRM)** - IEEE Std 1076-1993
- **IEEE Standard Libraries** - std_logic_1164, numeric_std
- **Shunting-yard Algorithm** - Dijkstra, 1961
- **Project Repository** - /mnt/ssd1tb/Codigos/Kmila-9s/

---

## Contact

**Project Author:** Ulrich Tamayo Daniel
**Email:** danielulrichtamayo@gmail.com
**Institutional Email:** dulricht1700@alumno.ipn.mx

For bugs, feature requests, or contributions, please refer to the project repository or contact the author directly.

---

*Last Updated: December 3, 2025*
*Documentation Version: 1.0 (aligned with Interpreter v0.15.0)*
