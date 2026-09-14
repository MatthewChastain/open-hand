// Shared assertion helper for the zero-framework state-test suite: a named
// case throws with its name on mismatch; the Program runner captures one
// exception per suite and keeps running the remaining suites.
internal static class TestHarness
{
    internal static void Equal<T>(T expected, T actual, string name) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
        }
    }
}
