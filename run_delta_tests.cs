// Simple test runner for delta cycle tests
// Usage: dotnet run --project . -- --test-delta

using Interpreter.Tests;

namespace Interpreter;

/// <summary>
/// Entry point for running delta cycle engine unit tests from the command line.
/// </summary>
/// <remarks>
/// Invoked when the interpreter is launched with the <c>--test-delta</c> or <c>-td</c> flag.
/// Delegates to <see cref="DeltaCycleEngineTests.RunAllTests"/> and returns the failure count.
/// </remarks>
public static class DeltaTestRunner
{
    /// <summary>
    /// Runs all delta cycle engine tests and returns the number of failures.
    /// </summary>
    /// <returns>The number of failed tests. Zero indicates all tests passed.</returns>
    public static async Task<int> RunDeltaTests()
    {
        return await DeltaCycleEngineTests.RunAllTests();
    }
}
