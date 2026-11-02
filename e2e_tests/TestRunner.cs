using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests;

public static class TestRunner
{
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("================================================================================");
        Console.WriteLine("           VALVE E2E TEST RUNNER - 4-TIER VERIFICATION HARNESS                 ");
        Console.WriteLine("================================================================================");

        string targetTier = "all";
        string? filterPattern = null;
        string? jsonOutputPath = null;
        bool verbose = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--tier", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                targetTier = args[++i].ToLowerInvariant();
            }
            else if (arg.Equals("--filter", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                filterPattern = args[++i];
            }
            else if (arg.Equals("--json", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                jsonOutputPath = args[++i];
            }
            else if (arg.Equals("--verbose", StringComparison.OrdinalIgnoreCase) || arg.Equals("-v", StringComparison.OrdinalIgnoreCase))
            {
                verbose = true;
            }
            else if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelp();
                return 0;
            }
        }

        Console.WriteLine($"Configuration: Tier Filter=[{targetTier}], Name Filter=[{filterPattern ?? "*"}], Verbose=[{verbose}]");
        Console.WriteLine("--------------------------------------------------------------------------------");

        var testMethods = DiscoverTests();
        var report = new TestRunReport();
        var globalStopwatch = Stopwatch.StartNew();

        int passed = 0;
        int failed = 0;
        int skipped = 0;

        foreach (var (method, attr) in testMethods)
        {
            // Check tier filter
            if (targetTier != "all")
            {
                int tierNum = (int)attr.Tier;
                if (!targetTier.Contains(tierNum.ToString()))
                {
                    skipped++;
                    continue;
                }
            }

            string suiteName = method.DeclaringType?.Name ?? "General";
            string testName = method.Name;
            string fullName = $"{suiteName}.{testName}";

            // Check name filter
            if (!string.IsNullOrEmpty(filterPattern) &&
                !fullName.Contains(filterPattern, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            Console.Write($"[{attr.Tier.ToString().ToUpperInvariant()}] {fullName} ");
            if (verbose && !string.IsNullOrEmpty(attr.Description))
            {
                Console.Write($"({attr.Description}) ");
            }

            var result = new TestResult
            {
                SuiteName = suiteName,
                TestName = testName,
                Tier = attr.Tier,
                Description = attr.Description,
            };

            var sw = Stopwatch.StartNew();
            try
            {
                object? instance = Activator.CreateInstance(method.DeclaringType!);
                if (method.ReturnType == typeof(Task))
                {
                    var task = (Task)method.Invoke(instance, null)!;
                    task.GetAwaiter().GetResult();
                }
                else
                {
                    method.Invoke(instance, null);
                }

                sw.Stop();
                result.Passed = true;
                result.DurationMs = sw.ElapsedMilliseconds;
                passed++;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"... PASS ({result.DurationMs} ms)");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.Passed = false;
                result.DurationMs = sw.ElapsedMilliseconds;

                Exception inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                result.ErrorMessage = inner.Message;
                result.StackTrace = inner.StackTrace;
                failed++;

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"... FAIL ({result.DurationMs} ms)");
                Console.WriteLine($"    Error: {result.ErrorMessage}");
                if (verbose && result.StackTrace != null)
                {
                    Console.WriteLine($"    Stack: {result.StackTrace}");
                }
                Console.ResetColor();
            }

            report.Results.Add(result);
        }

        globalStopwatch.Stop();
        report.TotalTests = passed + failed;
        report.Passed = passed;
        report.Failed = failed;
        report.DurationMs = globalStopwatch.ElapsedMilliseconds;

        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine($"Summary: {passed} Passed, {failed} Failed, {skipped} Skipped (Total Executed: {passed + failed})");
        Console.WriteLine($"Duration: {globalStopwatch.Elapsed:mm\\:ss\\.fff}");

        if (string.IsNullOrEmpty(jsonOutputPath))
        {
            string candidate = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../test_results.json"));
            jsonOutputPath = Directory.Exists(Path.GetDirectoryName(candidate)) ? candidate : "test_results.json";
        }

        if (jsonOutputPath != null)
        {
            try
            {
                string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(jsonOutputPath, json);
                Console.WriteLine($"Exported test report to: {jsonOutputPath}");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Failed to write JSON report to {jsonOutputPath}: {ex.Message}");
                Console.ResetColor();
            }
        }

        Console.WriteLine("================================================================================");
        if (failed == 0 && passed > 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Result: ALL TESTS PASSED (Exit Code: 0)");
            Console.ResetColor();
            return 0;
        }
        else if (passed == 0 && failed == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Result: NO TESTS MATCHED FILTER (Exit Code: 0)");
            Console.ResetColor();
            return 0;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Result: {failed} TEST(S) FAILED (Exit Code: 1)");
            Console.ResetColor();
            return 1;
        }
    }

    private static List<(MethodInfo Method, TestCaseAttribute Attr)> DiscoverTests()
    {
        var list = new List<(MethodInfo Method, TestCaseAttribute Attr)>();
        var assembly = Assembly.GetExecutingAssembly();

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsClass) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<TestCaseAttribute>();
                if (attr != null)
                {
                    list.Add((method, attr));
                }
            }
        }

        // Sort by Tier then Suite then Method
        return list.OrderBy(x => x.Attr.Tier)
                   .ThenBy(x => x.Method.DeclaringType?.Name)
                   .ThenBy(x => x.Method.Name)
                   .ToList();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: Valve.E2ETests.exe [options]");
        Console.WriteLine("Options:");
        Console.WriteLine("  --tier <1|2|3|4|all>    Filter tests by tier (default: all)");
        Console.WriteLine("  --filter <pattern>      Filter tests by suite or test name substring");
        Console.WriteLine("  --json <filepath>       Export results report as JSON");
        Console.WriteLine("  --verbose, -v           Enable verbose assertion details and descriptions");
        Console.WriteLine("  --help, -h              Show this help message");
    }
}
