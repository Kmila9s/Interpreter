# Changelog

All notable changes to the Kmila-9s VHDL Interpreter module will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.15.0] - 2025-12-03

### Added
- Case statement support in If branches (`IfStructure.cs`) - case statements can now be nested inside if/elsif/else branches
- Component instantiation skipping (`Architecture.cs`) - handles `label : ComponentName [generic map (...)] port map (...);` syntax
- Port/Generic expression skipping (`VariableSyntetizer.cs`) - special handling for complex expressions like `(NUM_INPUTS * DATA_WIDTH) - 1`
- Comprehensive debug logging in Entity and VariableSyntetizer classes

### Fixed
- **Critical regex fixes in `Constants.cs`**:
  - `ASSIGNATION_OPERATORS`: Fixed bracket creating character class that included `(`, changed to proper group syntax
  - `COMPARATION_OPERATORS`: Added proper operators and fixed character class issue
  - `LOGIC_OPERATORS`: Added anchors to prevent partial matches (e.g., "std_logic_vector" matching "or")
  - `ARITH_OPERATORS`: Added anchors to ensure exact matches
- Unary NOT operator detection (`Operation.cs`) - added type check to prevent `**` power operator from being treated as NOT
- ShuntingYard precedence casting (`ShuntingYard.cs:47`) - changed from direct cast to `Convert.ToInt32()` for proper enum handling
- Labeled process detection (`Architecture.cs`) - added support for `label : process(...)` syntax
- Variable scope check (`VariableSyntetizer.cs`) - removed erroneous check preventing local variable declarations
- Keyword handling in expressions (`Operation.cs`) - added explicit handling for `downto`, `to`, `when`, `else`
- Function/procedure parsing (`Program.cs`) - added depth tracking to prevent `end FunctionName;` from being mistaken for architecture end
- ToInteger safety (`StandardFunctions.cs`) - added checks for empty, 'U', 'X', 'Z' values

### Changed
- Token stream consumption improved in entity/port parsing
- Better parenthesis depth tracking for complex port expressions

### Test Results
- **Passing**: 16/20 tests (up from 7/20)
- **Newly passing**: test8, test9, test10, test12, test13, test14, test15, test16, test19
- **Remaining failures**: test11, test17, test18, test20

## [0.14.0] - 2025-11-27

### Added
- IEEE Standard Functions (`StandardFunctions.cs`):
  - `rising_edge(signal)` - Edge detection for clock signals
  - `falling_edge(signal)` - Falling edge detection
  - `to_integer()` / `conv_integer()` - Type conversion functions
  - `to_unsigned()` / `unsigned()` - Unsigned vector conversion
  - `to_signed()` / `signed()` - Signed vector conversion
  - `resize()` - Vector resizing
  - `shift_left()` / `shift_right()` - Logical shifts
  - Vector arithmetic: `AddVectors()`, `SubtractVectors()`
- Standard function detection in `Operation.cs`:
  - `TryParseStandardFunction()` - Single token function parsing
  - `ParseFunctionCall()` - Multi-token function parsing

### Fixed
- `_rawData` assignment bug in `Operation.cs:60` - line was inverted
- Condition expression support - expressions without assignment operator now work correctly
- Case-insensitive variable matching - uses `StringComparison.OrdinalIgnoreCase`
- CaseStructure `=>` tokenization - changed to look for `=>` as single token
- Added safety iteration limits in CaseStructure to prevent infinite loops

### Test Results
- **Passing**: 3/20 tests (test1, test2, test3)

## [0.13.0] - 2025-11-27

### Added
- Case/When statement support (`CaseStructure.cs`):
  - Multiple choices per branch using `|` separator
  - `when others =>` default branch
  - Nested control structures
- For loop support (`ForLoopStructure.cs`):
  - Ascending loops with `to` keyword
  - Descending loops with `downto` keyword
  - Automatic loop variable management
- While loop support (`WhileLoopStructure.cs`):
  - Condition evaluation per iteration
  - Safety iteration limit (10,000)
- Simple loop support (`SimpleLoopStructure.cs`):
  - `exit;` unconditional exit
  - `exit when condition;` conditional exit
- Signal attributes (`SignalAttributeHandler.cs`):
  - `signal'event` - Change detection
  - `signal'last_value` - Previous value
  - `signal'stable` - Stability check
  - `signal'active` - Transaction detection
  - Extension methods: `RisingEdge()`, `FallingEdge()`
- Process body parsing in `Process.cs`

### Fixed
- Less-than operator in `Operator.cs:225` - was using `==` instead of `<`
- Added missing comparison operators: `>=`, `<=`, `/=`
- STD_LOGIC_VECTOR comparison support

### Test Results
- **Passing**: 8/8 tests

## [0.12.0] - 2025-11-26

### Added
- Complete `if-elsif-else` support in `IfStructure` class:
  - Simple `if-then-end if` statements
  - Multiple `elsif` branches
  - Optional `else` default branch
  - Nested if statements
- `IfBranch` helper class for branch representation

### Fixed
- `Architecture.SyntetizeAssingations()` - changed termination from "then" to ";"
- Architecture end detection in `Program.cs` - proper distinction between control structure ends and architecture end
- Added `ConsumeEndIf()` method for proper token consumption

### Changed
- `IfStructure` redesigned from recursive chain to flat `List<IfBranch>` structure

## Earlier Versions

See git history for changes prior to v0.12.0.
