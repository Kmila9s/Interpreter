# Release Notes - v0.15.0

**Release Date:** December 3, 2025
**Version:** 0.15.0
**Author:** NakanoMiku13

## Overview

This release significantly improves the VHDL Interpreter's parsing capabilities, fixing critical regex issues and adding support for complex VHDL constructs. Test pass rate increased from **35% (7/20)** to **80% (16/20)**.

---

## Key Improvements

### 🔧 Critical Regex Fixes

Fixed fundamental regex pattern issues in `Constants.cs` that were causing widespread token misclassification:

- **ASSIGNATION_OPERATORS**: Fixed bracket `[` creating character class that included `(`, causing parentheses to be misclassified
- **LOGIC_OPERATORS**: Added anchors to prevent partial matches (e.g., "std_logic_vector" incorrectly matching "or")
- **ARITH_OPERATORS**: Added anchors to ensure exact operator matching
- **COMPARATION_OPERATORS**: Fixed character class issue and added complete operator set

**Impact:** These fixes resolved cascading errors in expression parsing throughout the codebase.

### 🆕 New Features

1. **Case Statements in If Branches** (`IfStructure.cs`)
   - Case statements can now be nested inside if/elsif/else branches
   - Enables complex control flow patterns common in VHDL state machines

2. **Component Instantiation Handling** (`Architecture.cs`)
   - Added `SkipComponentInstantiation()` method
   - Properly handles `label : ComponentName [generic map (...)] port map (...);` syntax
   - Tracks parenthesis depth for complex port mapping expressions

3. **Smart Port/Generic Expression Handling** (`VariableSyntetizer.cs`)
   - Skips evaluation of complex port width expressions during declaration
   - Handles expressions like `(NUM_INPUTS * DATA_WIDTH) - 1` and `DATA_WIDTH + clog2(NUM_INPUTS)`
   - Prevents errors from undefined functions during entity parsing

### 🐛 Major Bug Fixes

1. **Unary Operator Type Checking** (`Operation.cs`)
   - Fixed `**` (power) operator being incorrectly treated as unary NOT
   - Added proper type check before applying unary operation logic

2. **ShuntingYard Precedence Casting** (`ShuntingYard.cs`)
   - Changed from direct cast to `Convert.ToInt32()` for enum-to-int conversion
   - Properly handles mixed precedence types (ArithOperators vs LogicOperators)

3. **Function/Procedure Parsing** (`Program.cs`)
   - Added `funcProcDepth` tracking to distinguish `end FunctionName;` from `end ArchitectureName;`
   - Prevents premature architecture parsing termination

4. **Keyword Handling in Expressions** (`Operation.cs`)
   - Added explicit handling for `downto`, `to`, `when`, and `else` keywords
   - These keywords are now properly skipped during expression synthesis

5. **Variable Scope Validation** (`VariableSyntetizer.cs`)
   - Removed erroneous scope check that blocked local variable declarations in processes

6. **Labeled Process Detection** (`Architecture.cs`)
   - Added support for `label : process(...)` syntax where label precedes the process

7. **ToInteger Safety** (`StandardFunctions.cs`)
   - Added comprehensive checks for 'U', 'X', 'Z', and empty values
   - Proper error handling with try-catch fallback

---

## Test Results

### Summary
- **Passing Tests:** 16/20 (80%)
- **Previous:** 7/20 (35%)
- **Improvement:** +9 tests (+45%)

### Newly Passing Tests
- test8: Case statements in elsif branches
- test9: Complex calculator with nested case statements
- test10: Mixed control structures
- test12: Function declarations in architecture
- test13: Signal attributes and edge detection
- test14: Vector operations with generics
- test15: Multiple processes with signals
- test16: Port expressions with generic parameters
- test19: Advanced conditional logic

### Remaining Failures (4/20)

1. **test11, test17, test20** - Entity name token positioning issue
   - **Root Cause:** Token stream positioning after entity/port parsing
   - **Status:** Under investigation, requires deeper tokenizer analysis

2. **test18** - Bus keyword handling
   - **Issue:** Keyword `bus` appears in unexpected context
   - **Status:** Needs syntax analysis

---

## Technical Details

### Files Modified

#### Parser Module
- `Models/Constants.cs` - Regex pattern fixes

#### Interpreter Module - Core Services
- `Services/Architecture.cs` - Component instantiation, labeled statements
- `Services/Entity.cs` - Cleaned up debug code
- `Services/Operation.cs` - Keyword handling, unary operator fix
- `Services/Operator.cs` - Removed debug output
- `Services/VariableSyntetizer.cs` - Port expression handling, XML comment fix
- `Services/StandardFunctions.cs` - ToInteger safety improvements

#### Interpreter Module - Repositories
- `Repositories/IfStructure.cs` - Case statement nesting support
- `Repositories/ShuntingYard.cs` - Precedence casting fix
- `Program.cs` - Function/procedure depth tracking

#### Documentation
- `README.md` - Updated with v0.15.0 changelog
- `CHANGELOG.md` - Created comprehensive changelog
- Added inline documentation to key methods

---

## Migration Notes

### Breaking Changes
None. This release is fully backward compatible.

### Deprecated Features
None.

### Recommended Actions
- Review any custom expression parsing code - the regex fixes may affect edge cases
- Test complex port declarations with expressions using generics
- Verify case statements nested in if/elsif branches work as expected

---

## Known Limitations

The following VHDL features are still not fully supported:

1. **Parser-Level Requirements** (require Parser module changes):
   - Record types and custom type definitions
   - VHDL aggregates `(others => '0')`
   - Array slicing with complex expressions
   - Complete time literal support (`ns`, `ps`, `us`)
   - Full generate statement implementation

2. **Interpreter-Level Issues**:
   - Entity name token positioning (test11, test17, test20)
   - Bus keyword in certain contexts (test18)

---

## Performance Impact

- **Build Time:** No significant change
- **Runtime:** Slight improvement due to more efficient regex matching with anchors
- **Memory:** No significant change

---

## Acknowledgments

This release was made possible through systematic debugging and test-driven development, incrementally fixing issues revealed by the 20-test suite.

---

## Future Roadmap

### Short Term (v0.16.0)
- Fix remaining 4 test failures
- Improve error messages for unsupported constructs
- Add support for more VHDL attributes

### Medium Term (v0.17.0)
- Generate statement implementation
- Full aggregate support (`(others => value)`)
- Enhanced type conversion functions

### Long Term (v1.0.0)
- Complete VHDL-93 coverage
- Partial VHDL-2008 support
- Performance optimizations

---

For detailed technical information, see:
- `/mnt/ssd1tb/Codigos/Kmila-9s/Interpreter/CHANGELOG.md`
- `/mnt/ssd1tb/Codigos/Kmila-9s/Interpreter/README.md`

For bug reports and feature requests:
- Contact: danielulrichtamayo@gmail.com
