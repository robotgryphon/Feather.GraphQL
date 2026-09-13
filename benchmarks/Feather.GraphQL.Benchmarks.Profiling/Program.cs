using System.Diagnostics;
using Feather.GraphQL.Benchmarks;
using Feather.GraphQL.Http;
using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Providers;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Benchmarks.Profiling;

/// <summary>
/// The two pipelines, in loops long enough to profile.
/// </summary>
/// <remarks>
/// <para>
/// Not a benchmark. BenchmarkDotNet runs each case in its own process, around its own harness,
/// and reports a distribution — which is what you want for a number and exactly what you do not
/// want under a profiler, where the harness is most of what gets attributed. This runs one phase
/// at a time, in one process, doing nothing else.
/// </para>
/// <para>
/// The phases are named and separated by a settled GC, so a timeline shows which is which without
/// having to read call stacks to find out. Each is warmed before it is timed, so the profile is of
/// steady-state code rather than of the JIT.
/// </para>
/// <example>
/// <code>
/// dotnet run -c Release --project benchmarks/Feather.GraphQL.Benchmarks.Profiling -- --rows 25 --seconds 10
/// dotnet run -c Release --project benchmarks/Feather.GraphQL.Benchmarks.Profiling -- --only static --pause
/// </code>
/// </example>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (Options.Parse(args) is not { } options)
            return 1;

        Console.WriteLine($"rows={options.Rows}  seconds={options.Seconds}  only={options.Only}");
        Console.WriteLine($"payload={Payloads.NestedBody(options.Rows).Length:n0} bytes\n");

        using var transport = new CannedTransport(Payloads.NestedBody(options.Rows));
        using var client = transport.Client();

        var phases = new List<(string Name, Func<HttpClient, Task<int>> Run)>();

        if (options.Only is "static" or "both")
            phases.Add(("static", Pipelines.StaticAsync));

        if (options.Only is "static-filtered" or "both")
            phases.Add(("static-filtered", Pipelines.StaticFilteredAsync));

        if (options.Only is "linq" or "both")
            phases.Add(("linq", Pipelines.CompiledAsync));

        if (options.Only is "linq-filtered" or "both")
            phases.Add(("linq-filtered", Pipelines.CompiledFilteredAsync));

        foreach (var (name, run) in phases)
        {
            // Warm separately from the timing, so what is profiled is the code that runs once
            // everything has settled rather than the tiering that got it there.
            await Loop(run, client, TimeSpan.FromSeconds(1));

            Settle();

            if (options.Pause)
            {
                Console.WriteLine($"[{name}] ready — start collection, then press enter");
                Console.ReadLine();
            }

            Console.WriteLine($"[{name}] running for {options.Seconds}s");

            var (iterations, elapsed, rows) = await Loop(run, client, TimeSpan.FromSeconds(options.Seconds));

            Console.WriteLine(
                $"[{name}] {iterations:n0} iterations in {elapsed.TotalSeconds:n2}s"
                + $"  ({elapsed.TotalMilliseconds * 1_000_000 / iterations:n0} ns/op, {rows:n0} rows read)");

            if (options.Pause)
            {
                Console.WriteLine($"[{name}] done — stop collection, then press enter");
                Console.ReadLine();
            }

            Settle();
        }

        return 0;
    }

    /// <summary>
    /// Runs one pipeline until the clock runs out, counting what it read.
    /// </summary>
    /// <remarks>
    /// The rows are summed and returned rather than discarded: a loop whose result nothing reads
    /// is a loop the optimiser is entitled to delete, and a profile of deleted code shows nothing
    /// at all.
    /// </remarks>
    private static async Task<(long Iterations, TimeSpan Elapsed, long Rows)> Loop(
        Func<HttpClient, Task<int>> run,
        HttpClient client,
        TimeSpan duration)
    {
        long iterations = 0;
        long rows = 0;
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < duration)
        {
            // A batch between clock reads: reading the clock every iteration would put the
            // timer in the profile beside the thing being profiled.
            for (int i = 0; i < 64; i++)
            {
                rows += await run(client).ConfigureAwait(false);
                iterations++;
            }
        }

        clock.Stop();

        return (iterations, clock.Elapsed, rows);
    }

    /// <summary>Leaves the heap quiet, so one phase's garbage is not the next one's profile.</summary>
    private static void Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>What the command line asked for.</summary>
    private sealed record Options(int Rows, int Seconds, string Only, bool Pause)
    {
        public static Options? Parse(string[] args)
        {
            int rows = 25;
            int seconds = 10;
            string only = "both";
            bool pause = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--rows" when i + 1 < args.Length && int.TryParse(args[++i], out int parsed):
                        rows = parsed;
                        break;

                    case "--seconds" when i + 1 < args.Length && int.TryParse(args[++i], out int taken):
                        seconds = taken;
                        break;

                    case "--only" when i + 1 < args.Length:
                        only = args[++i];
                        break;

                    case "--pause":
                        pause = true;
                        break;

                    default:
                        Console.Error.WriteLine($"unrecognised argument: {args[i]}");
                        Console.Error.WriteLine(
                            "usage: [--rows N] [--seconds N] "
                            + "[--only static|static-filtered|linq|linq-filtered|both] [--pause]");

                        return null;
                }
            }

            return new Options(rows, seconds, only, pause);
        }
    }
}
