using BenchmarkDotNet.Running;

namespace Feather.GraphQL.Benchmarks.Interpreted;

/// <summary>
/// The harness entry point. No arguments runs the menu; <c>--filter *</c> runs everything.
/// </summary>
internal static class Program
{
    private static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
