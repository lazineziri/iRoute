using System.Text.Json;
using iRoute.Common;
using iRoute.Core;
using Xunit;

namespace iRoute.Tests.Core;

public sealed class ExecutionOrchestratorTests
{
    [Fact]
    public async Task FacadeDelegatesEveryOperationToTheExecutionService()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ExecutionSnapshot(
            Guid.CreateVersion7(),
            "email.draft",
            ExecutionStatus.Accepted,
            now,
            now);
        var approval = new ApprovalResult(
            new ApprovalSnapshot(
                snapshot.ExecutionId,
                "send",
                ApprovalStatus.Pending,
                "email.send",
                SideEffectClass.IrreversibleWrite,
                ["email.send"],
                "requester",
                null,
                "input",
                "idempotency",
                now),
            snapshot);
        var service = new RecordingExecutionService(snapshot, approval);
        var orchestrator = new ExecutionOrchestrator(service);
        using var document = JsonDocument.Parse("{}");
        var request = new TaskRequest("email.draft", document.RootElement.Clone());
        var decision = new ApprovalDecision("send", true);
        using var cancellation = new CancellationTokenSource();

        Assert.Same(snapshot, await orchestrator.ExecuteAsync(request, cancellation.Token));
        Assert.Same(snapshot, await orchestrator.SubmitAsync(request, cancellation.Token));
        Assert.Same(
            approval,
            await orchestrator.SubmitApprovalAsync(
                snapshot.ExecutionId,
                decision,
                "tenant",
                "actor",
                ["email.send"],
                cancellation.Token));
        Assert.Same(
            approval,
            await orchestrator.SubmitApprovalForQueueAsync(
                snapshot.ExecutionId,
                decision,
                "tenant",
                "actor",
                ["email.send"],
                cancellation.Token));
        Assert.Same(
            snapshot,
            await orchestrator.ProcessQueuedAsync(snapshot.ExecutionId, cancellation.Token));

        Assert.Equal(
            ["execute", "submit", "approve", "approve-queue", "process"],
            service.Calls);
        Assert.Same(request, service.Request);
        Assert.Same(decision, service.Decision);
        Assert.Equal(snapshot.ExecutionId, service.ExecutionId);
    }

    private sealed class RecordingExecutionService(
        ExecutionSnapshot snapshot,
        ApprovalResult approval) : IExecutionService
    {
        public List<string> Calls { get; } = [];

        public TaskRequest? Request { get; private set; }

        public ApprovalDecision? Decision { get; private set; }

        public Guid ExecutionId { get; private set; }

        public Task<ExecutionSnapshot> ExecuteAsync(
            TaskRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("execute");
            Request = request;
            return Task.FromResult(snapshot);
        }

        public Task<ExecutionSnapshot> SubmitAsync(
            TaskRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("submit");
            Request = request;
            return Task.FromResult(snapshot);
        }

        public Task<ApprovalResult> SubmitApprovalAsync(
            Guid executionId,
            ApprovalDecision decision,
            string tenantId,
            string actorId,
            IReadOnlyCollection<string> permissionScopes,
            CancellationToken cancellationToken) =>
            RecordApproval(
                "approve",
                executionId,
                decision,
                tenantId,
                actorId,
                permissionScopes,
                cancellationToken);

        public Task<ApprovalResult> SubmitApprovalForQueueAsync(
            Guid executionId,
            ApprovalDecision decision,
            string tenantId,
            string actorId,
            IReadOnlyCollection<string> permissionScopes,
            CancellationToken cancellationToken) =>
            RecordApproval(
                "approve-queue",
                executionId,
                decision,
                tenantId,
                actorId,
                permissionScopes,
                cancellationToken);

        public Task<ExecutionSnapshot> ProcessQueuedAsync(
            Guid executionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("process");
            ExecutionId = executionId;
            return Task.FromResult(snapshot);
        }

        private Task<ApprovalResult> RecordApproval(
            string call,
            Guid executionId,
            ApprovalDecision decision,
            string tenantId,
            string actorId,
            IReadOnlyCollection<string> permissionScopes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(string.IsNullOrWhiteSpace(tenantId));
            Assert.False(string.IsNullOrWhiteSpace(actorId));
            Assert.NotEmpty(permissionScopes);
            Calls.Add(call);
            ExecutionId = executionId;
            Decision = decision;
            return Task.FromResult(approval);
        }
    }
}
