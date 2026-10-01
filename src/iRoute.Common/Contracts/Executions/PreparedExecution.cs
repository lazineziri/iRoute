namespace iRoute.Common;

public sealed record PreparedExecution(ExecutionSnapshot Snapshot, TaskDefinition Definition, ExecutionPlan? Plan = null, RoutingDecision? Routing = null);
