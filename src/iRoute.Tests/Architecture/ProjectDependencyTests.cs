using System.Reflection;
using iRoute.Common;
using iRoute.Core;
using iRoute.Data;
using iRoute.Runtime.Client;
using iRoute.Services;
using Xunit;

namespace iRoute.Tests.Architecture;

public sealed class ProjectDependencyTests
{
    [Fact]
    public void ProductionProjectsFollowTheAllowedDependencyGraph()
    {
        AssertIRouteReferences(typeof(TaskRequest).Assembly);
        AssertIRouteReferences(typeof(TaskRouter).Assembly, "iRoute.Common");
        AssertIRouteReferences(typeof(ExecutionOrchestrator).Assembly, "iRoute.Common");
        AssertIRouteReferences(typeof(IRouteDbContext).Assembly, "iRoute.Common");
        AssertIRouteReferences(
            typeof(IRouteClient).Assembly,
            "iRoute.Common",
            "iRoute.Core",
            "iRoute.Data",
            "iRoute.Services");
    }

    [Fact]
    public void CommonIsTheOnlyProductionContractAssembly()
    {
        var implementationAssemblies = new[]
        {
            typeof(ExecutionOrchestrator).Assembly,
            typeof(TaskRouter).Assembly,
            typeof(IRouteDbContext).Assembly,
            typeof(IRouteClient).Assembly
        };
        var misplacedContracts = implementationAssemblies
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type.IsInterface || type.IsEnum || IsRecord(type))
            .Select(type => type.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(misplacedContracts);
    }

    private static void AssertIRouteReferences(Assembly assembly, params string[] expected)
    {
        var actual = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name?.StartsWith("iRoute.", StringComparison.Ordinal) is true)
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static bool IsRecord(Type type) =>
        type.GetMethod(
            "<Clone>$",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null;
}
