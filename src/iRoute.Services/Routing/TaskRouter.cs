using iRoute.Common;

namespace iRoute.Services;

public sealed class TaskRouter(
    IDirectPathSelector directPath,
    IBoundedTaskPlanner planner) : ITaskRouter
{
    public async Task<RoutingResult> RouteAsync(
        TaskRequest request,
        TaskDefinition definition,
        CancellationToken cancellationToken)
    {
        var direct = await directPath.TrySelectAsync(request, definition, cancellationToken);
        return direct ?? await planner.PlanAsync(request, definition, cancellationToken);
    }
}
