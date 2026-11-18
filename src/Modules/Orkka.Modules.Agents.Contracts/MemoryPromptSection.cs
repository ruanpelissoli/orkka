namespace Orkka.Modules.Agents.Contracts;

using System.Text;

/// <summary>
/// Renders retrieved memories into the prompt block every run and chat turn shares, so the
/// pipeline (Execution) and chat (Agents) can't drift on how memory reads. Standing rules
/// (pinned, decisions, conventions, preferences) come first; then what was relevant to this
/// work; then the agent's own recent learnings — under a hard character budget.
/// </summary>
public static class MemoryPromptSection
{
    public const int DefaultCharBudget = 4_000;
    public const int EntryCharCap = 500;

    public const string Heading = "## Project memory";

    /// <summary>The instruction agents see once tools are on: how to add to the memory.</summary>
    public const string ToolHint =
        "When you learn something durable — a project convention, a decision and its reason, a " +
        "gotcha that cost you time — save it with the `remember` tool (short title, 1–3 sentences; " +
        "scope \"team\" unless it is only about how you personally work). Use `recall` to search " +
        "the project memory for a topic before making a choice the team may already have made.";

    public static string Render(IReadOnlyList<MemoryItem> memories, int charBudget = DefaultCharBudget, bool includeToolHint = false)
    {
        if (memories.Count == 0)
            return includeToolHint ? $"\n{Heading}\nNothing recorded yet. {ToolHint}\n" : "";

        var rules = memories.Where(m => m.IsPinned || m.Type is MemoryType.Decision or MemoryType.Convention or MemoryType.Preference).ToList();
        var rest = memories.Except(rules).ToList();

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine(Heading);
        var used = 0;
        var shown = 0;

        void Group(string heading, IEnumerable<MemoryItem> items)
        {
            var wrote = false;
            foreach (var m in items)
            {
                var content = m.Content.Length > EntryCharCap ? m.Content[..EntryCharCap] + " […]" : m.Content;
                var line = $"- [{m.Type}] {m.Title}: {content}";
                if (used + line.Length > charBudget) return;
                if (!wrote) { sb.AppendLine(heading); wrote = true; }
                sb.AppendLine(line);
                used += line.Length;
                shown++;
            }
        }

        Group("### Decisions, conventions & preferences (follow these)", rules);
        Group("### Relevant learnings & past mistakes", rest);

        if (shown < memories.Count)
            sb.AppendLine($"({memories.Count - shown} more not shown for space — use `recall` to search)");
        if (includeToolHint)
            sb.AppendLine(ToolHint);
        return sb.ToString();
    }
}
