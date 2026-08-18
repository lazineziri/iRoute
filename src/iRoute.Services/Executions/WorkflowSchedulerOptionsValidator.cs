using iRoute.Common;
using Microsoft.Extensions.Options;

namespace iRoute.Services;

[OptionsValidator]
public sealed partial class WorkflowSchedulerOptionsValidator : IValidateOptions<WorkflowSchedulerOptions>
{
}
