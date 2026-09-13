using System.Reflection;
using NUnitLite;

namespace Feather.GraphQL.Http.Tests;

internal static class Program
{
    private static int Main(string[] args)
        => new AutoRun(Assembly.GetExecutingAssembly()).Execute(args);
}
