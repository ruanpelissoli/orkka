namespace Orkka.Modules.Execution.Agents;

using System.Text.Json;
using ih0.Claude.Agent.SDK.Types;

/// <summary>
/// Builds PreToolUse hook responses for the claude CLI control protocol.
///
/// EVERY field must be non-null. The SDK (ih0.Claude.Agent.SDK 0.1.6) serializes
/// <see cref="HookOutput"/> with default options — it is not registered in the SDK's
/// null-omitting source-gen context — so any property left unset goes on the wire as an
/// explicit null:
///   {"continue":true,"suppressOutput":null,"stopReason":null,"decision":null,
///    "systemMessage":null,"reason":null,"hookSpecificOutput":{...}}
/// CLI 2.1.207 validates that payload with a zod union of `.optional()` fields, where
/// absent passes and explicit null fails. On rejection the CLI logs a ZodError and
/// <b>runs the tool anyway</b> — the guard's deny is silently discarded, voiding
/// SECURITY.md Layer 3. Verified on the wire and end-to-end against CLI 2.1.207.
///
/// Nulls inside <c>hookSpecificOutput</c> are dropped by <see cref="HookJsonOptions"/>
/// for the same reason (that half broke first, on CLI 2.1.207).
/// </summary>
internal static class AgentHookResponses
{
    public static HookOutput Allow() => Build("allow", reason: null);

    public static HookOutput Deny(string reason) => Build("deny", reason);

    private static HookOutput Build(string permissionDecision, string? reason) => new()
    {
        Continue = true,
        SuppressOutput = false,
        StopReason = "",
        // Legacy twin of permissionDecision. The CLI's schema accepts only "approve" or
        // "block" here — never null — so it has to mirror the modern field exactly.
        Decision = permissionDecision == "allow" ? "approve" : "block",
        SystemMessage = "",
        Reason = reason ?? "",
        HookSpecificOutput = SerializeHookOutput(new PreToolUseHookSpecificOutput
        {
            PermissionDecision = permissionDecision,
            PermissionDecisionReason = reason,
        }),
    };

    private static readonly JsonSerializerOptions HookJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static JsonElement SerializeHookOutput(PreToolUseHookSpecificOutput output)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(output, HookJsonOptions);
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
