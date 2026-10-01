using System.Reflection;
using iRoute.Core;
using iRoute.Runtime.Api;
using iRoute.Services;
using Xunit;

namespace iRoute.Tests.Architecture;

public sealed class ExecutionBoundaryTests
{
    [Fact]
    public void ExecutionServicesHaveFocusedConstructorDependencies()
    {
        Type[] services =
        [
            typeof(ExecutionService), typeof(ExecutionSubmissionService), typeof(ExecutionPreparationService),
            typeof(ExecutionApprovalService), typeof(PlanExecutionService), typeof(ExecutionOutcomeService),
            typeof(QueuedExecutionService), typeof(ExecutionCancellationService), typeof(ExternalActionReconciliationService)
        ];
        foreach (var service in services)
        {
            Assert.InRange(Assert.Single(service.GetConstructors()).GetParameters().Length, 1, 10);
        }

        Assert.Equal(5, Assert.Single(typeof(ExecutionService).GetConstructors()).GetParameters().Length);
    }

    [Theory]
    [InlineData("ExecutionQueryEndpoints", "CancelAsync")]
    [InlineData("ExternalActionEndpoints", "ReconcileActionAsync")]
    [InlineData("ExternalActionEndpoints", "ListUnresolvedActionsAsync")]
    public void HttpBusinessCommandsDelegateToCoreInsteadOfPersistence(string typeName, string methodName)
    {
        var type = typeof(ExecutionEndpoints).Assembly.GetType($"iRoute.Runtime.Api.{typeName}", throwOnError: true)!;
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)!;
        var parameters = method.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        Assert.Contains(typeof(ExecutionOrchestrator), parameters);
        Assert.DoesNotContain(parameters, parameter =>
            parameter.Name.EndsWith("Store", StringComparison.Ordinal) || parameter == typeof(TimeProvider));
    }
}
