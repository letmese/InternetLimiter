namespace Valve.E2ETests.Framework;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TestCaseAttribute : Attribute
{
    public TestTier Tier { get; }
    public string Description { get; }

    public TestCaseAttribute(TestTier tier, string description = "")
    {
        Tier = tier;
        Description = description;
    }
}
