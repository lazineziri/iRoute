using System.Collections.Frozen;
using iRoute.Contracts;
using iRoute.Core;

namespace iRoute.Infrastructure;

public sealed class BuiltInTaskDefinitionRegistry : ITaskDefinitionRegistry
{
    private static readonly FrozenDictionary<string, TaskDefinition> Definitions =
        new Dictionary<string, TaskDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["email.draft"] = new(
                "email.draft", 1, "text.generation", 800, 0.80m, true, SideEffectClass.None, "email.draft",
                DefaultMaxInputTokens: 4000,
                DefaultDeadlineMilliseconds: 30000,
                DefaultMaxModelCalls: 3,
                AllowedCapabilities: ["text.generation"]),
            ["email.send"] = new(
                "email.send", 1, "email.send", 800, 0.90m, true, SideEffectClass.IrreversibleWrite, "email.send.receipt",
                DefaultMaxInputTokens: 4000,
                DefaultDeadlineMilliseconds: 30000,
                DefaultMaxModelCalls: 0,
                DefaultMaxToolCalls: 3,
                AllowedCapabilities: ["email.send"],
                PermissionScopes: ["email:send"],
                ApprovalRequired: true),
            ["calendar.find_slots"] = new(
                "calendar.find_slots", 1, "calendar.read", 400, 0.95m, true, SideEffectClass.ReadOnly, "calendar.slot-proposal",
                DefaultMaxInputTokens: 3000,
                DefaultDeadlineMilliseconds: 20000,
                DefaultMaxModelCalls: 0,
                DefaultMaxToolCalls: 3,
                AllowedCapabilities: ["calendar.read"],
                PermissionScopes: ["calendar:read"]),
            ["database.answer"] = new(
                "database.answer", 1, "database.read", 600, 0.95m, true, SideEffectClass.ReadOnly, "database.answer",
                DefaultMaxInputTokens: 3000,
                DefaultDeadlineMilliseconds: 20000,
                DefaultMaxModelCalls: 0,
                DefaultMaxToolCalls: 3,
                AllowedCapabilities: ["database.read"],
                PermissionScopes: ["database:read"]),
            ["document.summarize"] = new(
                "document.summarize", 1, "text.summarization", 1200, 0.85m, true, SideEffectClass.None, "document.summary",
                DefaultMaxInputTokens: 8000,
                DefaultDeadlineMilliseconds: 45000,
                DefaultMaxModelCalls: 3,
                AllowedCapabilities: ["text.summarization"]),
            ["project.decision.get"] = new(
                "project.decision.get", 1, "project.memory.read", 400, 1m, true, SideEffectClass.ReadOnly, "project.decision",
                DefaultMaxInputTokens: 1000,
                DefaultDeadlineMilliseconds: 5000,
                DefaultMaxModelCalls: 0,
                DefaultMaxToolCalls: 3,
                AllowedCapabilities: ["project.memory.read"],
                PermissionScopes: ["project:read"]),
            ["project.fact.get"] = new(
                "project.fact.get", 1, "project.memory.read", 400, 1m, true, SideEffectClass.ReadOnly, "project.fact",
                DefaultMaxInputTokens: 1000,
                DefaultDeadlineMilliseconds: 5000,
                DefaultMaxModelCalls: 0,
                DefaultMaxToolCalls: 3,
                AllowedCapabilities: ["project.memory.read"],
                PermissionScopes: ["project:read"])
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public Task<TaskDefinition?> FindAsync(string taskType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Definitions.GetValueOrDefault(taskType));
    }
}
