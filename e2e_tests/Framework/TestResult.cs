namespace Valve.E2ETests.Framework;

public sealed class TestResult
{
    public required string SuiteName { get; init; }
    public required string TestName { get; init; }
    public required TestTier Tier { get; init; }
    public required string Description { get; init; }
    public bool Passed { get; set; }
    public long DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StackTrace { get; set; }
}

public sealed class TestRunReport
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public int TotalTests { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public long DurationMs { get; set; }
    public bool Success => Failed == 0;
    public List<TestResult> Results { get; set; } = new();
}
