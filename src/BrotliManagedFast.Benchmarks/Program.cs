
using System.Diagnostics;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Mathematics.OutlierDetection;

namespace BrotliManagedFast.Benchmarks;

/// <summary>
/// Entry point for the benchmark suite.
///
/// Usage:
///   dotnet run -c Release                     decode and encode against native and BrotliSharpLib
///   dotnet run -c Release -- --ratio          compressed sizes per implementation (not timed)
///   dotnet run -c Release -- --all            sizes and timings in one run
///   dotnet run -c Release -- --report         faster job for publishing tables (README numbers)
///   dotnet run -c Release -- --quick          smoke test only; never quote its numbers
///   dotnet run -c Release -- --filter "*json*"   any BenchmarkDotNet filter still works
///
/// Do not compare a Mean from one session against a Mean from another: laptops throttle, and the
/// same code has measured 2x apart in consecutive sessions. Only ratios measured inside one run
/// mean anything.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var flags = new HashSet<string>(args.Where(a => a.StartsWith("--")), StringComparer.OrdinalIgnoreCase);
        var passthrough = args.Where(a => !IsOurFlag(a)).ToArray();
        bool quick = flags.Contains("--quick");
        bool report = flags.Contains("--report");

        PrintProvenance();

        // A faster wrong answer is not an improvement. Gate every session on correctness and fail
        // loudly rather than reporting numbers for a broken codec.
        try
        {
            Correctness.Run();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        if (flags.Contains("--ratio"))
        {
            RatioReport.Print();
            if (!flags.Contains("--all") && !flags.Contains("--competitive")) return 0;
        }

        if (quick)
        {
            Console.WriteLine();
            Console.WriteLine("WARNING: --quick is a smoke test. Its error margins are far too wide to");
            Console.WriteLine("decide whether an optimization helped. Never quote a --quick number.");
        }

        Console.WriteLine();
        var config = BuildConfig(quick, report);

        Summary[] summaries = BenchmarkRunner.Run([typeof(DecodeBenchmarks), typeof(EncodeBenchmarks)], config, passthrough);

        return ReportOutcome(summaries);
    }
    private static bool IsOurFlag(string arg) =>
        new[] { "--competitive", "--ratio", "--all", "--quick", "--report" }
            .Any(f => arg.Equals(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>A benchmark run that silently failed but exited 0 looks like a clean result. Surface it.</summary>
    private static int ReportOutcome(Summary[] summaries)
    {
        int problems = 0;
        foreach (var summary in summaries)
        {
            if (summary.HasCriticalValidationErrors)
            {
                Console.Error.WriteLine($"{summary.Title}: critical validation errors.");
                problems++;
            }

            foreach (var report in summary.Reports.Where(r => !r.Success))
            {
                Console.Error.WriteLine($"FAILED: {report.BenchmarkCase.DisplayInfo}");
                problems++;
            }
        }

        if (problems > 0)
        {
            Console.Error.WriteLine($"{problems} benchmark problem(s); results are not trustworthy.");
            return 1;
        }

        return 0;
    }

    private static IConfig BuildConfig(bool quick, bool report)
    {
        Job job;
        if (quick)
        {
            job = Job.ShortRun.WithWarmupCount(1).WithIterationCount(3).WithId("Smoke");
        }
        else if (report)
        {
            // For publishing a table across many cases, where the useful precision is "which is
            // faster and roughly by how much" rather than "did this commit move it 5%".
            job = Job.ShortRun.WithWarmupCount(3).WithIterationCount(5).WithId("Report");
        }
        else
        {
            // Not a fixed iteration count: pinning it disables the adaptive stopping rule, and five
            // iterations cannot separate a 5% change from noise on a throttling laptop. One launch,
            // 100 ms iterations, and a 2% relative-error target.
            job = Job.Default
                .WithMinWarmupCount(4)
                .WithMinIterationCount(10)
                .WithMaxIterationCount(30)
                .WithIterationTime(Perfolizer.Horology.TimeInterval.FromMilliseconds(100))
                .WithMaxRelativeError(0.02)
                .WithLaunchCount(1)
                // A run that slowed because the package got hot is evidence, not an outlier.
                .WithOutlierMode(OutlierMode.DontRemove)
                .WithId("Decide");
        }

        // The optimizations validator is off because BrotliSharpLib ships without the optimized
        // attribute set and would abort every run. VerifyReleaseBuild checks this library's assembly.
        return ManualConfig.Create(DefaultConfig.Instance)
            .WithBuildTimeout(TimeSpan.FromMinutes(20))   // the slow ARM boards take minutes to build a benchmark project
            .WithOptions(ConfigOptions.DisableOptimizationsValidator)
            .AddJob(job)
            .AddDiagnoser(MemoryDiagnoser.Default);
    }

    /// <summary>Stamps the revision under test and checks that the library is an optimized build.</summary>
    private static void PrintProvenance()
    {
        Console.WriteLine($"BrotliManagedFast benchmark suite  |  commit {GitDescribe()}  |  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine("Absolute times are NOT comparable across sessions; only ratios within one run are.");
        VerifyReleaseBuild();
        Console.WriteLine();
    }

    /// <summary>A Debug build measures nothing useful, and nothing in the output would say so.</summary>
    private static void VerifyReleaseBuild()
    {
        foreach (var assembly in new[] { typeof(BrotliDecoder).Assembly })
        {
            var debuggable = assembly.GetCustomAttributes(typeof(DebuggableAttribute), false).OfType<DebuggableAttribute>().FirstOrDefault();
            if (debuggable is not null && debuggable.IsJITOptimizerDisabled)
                throw new InvalidOperationException($"{assembly.GetName().Name} is a Debug build; benchmark numbers would be meaningless. Build with -c Release.");
        }
    }

    private static string GitDescribe()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "describe --always --dirty --tags")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process is null) return "unknown";
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(3000);
            return string.IsNullOrEmpty(output) ? "unknown" : output;
        }
        catch
        {
            return "unknown";
        }
    }
}
