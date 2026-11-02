namespace Valve.E2ETests.Framework;

public static class TestAssert
{
    public static void IsTrue(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new AssertionException(message ?? "Expected condition to be TRUE, but was FALSE.");
        }
    }

    public static void IsFalse(bool condition, string? message = null)
    {
        if (condition)
        {
            throw new AssertionException(message ?? "Expected condition to be FALSE, but was TRUE.");
        }
    }

    public static void AreEqual<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            string detail = $"Expected: <{FormatValue(expected)}> but Actual was: <{FormatValue(actual)}>.";
            throw new AssertionException(message != null ? $"{message} ({detail})" : detail);
        }
    }

    public static void AreNotEqual<T>(T notExpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
        {
            string detail = $"Expected value NOT to equal: <{FormatValue(notExpected)}>.";
            throw new AssertionException(message != null ? $"{message} ({detail})" : detail);
        }
    }

    public static void IsNull(object? value, string? message = null)
    {
        if (value != null)
        {
            string detail = $"Expected null, but received: <{FormatValue(value)}>.";
            throw new AssertionException(message != null ? $"{message} ({detail})" : detail);
        }
    }

    public static void IsNotNull(object? value, string? message = null)
    {
        if (value == null)
        {
            throw new AssertionException(message ?? "Expected non-null object reference, but received null.");
        }
    }

    public static void Contains(string expectedSubstring, string? actual, string? message = null)
    {
        if (actual == null || !actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            string detail = $"Expected string to contain: <{expectedSubstring}>, but actual string was: <{actual}>.";
            throw new AssertionException(message != null ? $"{message} ({detail})" : detail);
        }
    }

    public static void InRange(double actual, double min, double max, string? message = null)
    {
        if (actual < min || actual > max)
        {
            string detail = $"Expected value in range [{min}, {max}], but actual was: <{actual}>.";
            throw new AssertionException(message != null ? $"{message} ({detail})" : detail);
        }
    }

    public static void Throws<TException>(Action action, string? message = null) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new AssertionException($"Expected exception of type {typeof(TException).Name}, but caught {ex.GetType().Name}: {ex.Message}.");
        }

        throw new AssertionException(message ?? $"Expected exception of type {typeof(TException).Name}, but no exception was thrown.");
    }

    private static string FormatValue(object? val)
    {
        if (val == null) return "null";
        if (val is string s) return $"\"{s}\"";
        return val.ToString() ?? "null";
    }
}

public sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message)
    {
    }
}
