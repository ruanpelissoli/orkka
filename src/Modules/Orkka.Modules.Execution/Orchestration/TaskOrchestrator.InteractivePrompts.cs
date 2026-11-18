namespace Orkka.Modules.Execution.Orchestration;

using System.Text;

/// <summary>
/// Prompts for interactive development sessions. Kept apart from the batch-phase prompts because
/// the situation genuinely differs: the human is present for every turn, so the instincts the
/// pipeline prompts train — decide alone, park on a question, prove nothing until QA — are either
/// unnecessary or actively wrong here.
/// </summary>
public sealed partial class TaskOrchestrator
{
    /// <summary>
    /// The rules that make a session feel like a session rather than a batch run narrated aloud.
    /// Shared by the turn and finish prompts so the agent's contract never changes mid-session.
    /// </summary>
    private const string InteractiveSessionContract = """

        ## You are in a live session
        A human is at the other end of this conversation, watching your output as it streams and
        able to reply at any time. That changes three things about how you work:

        1. **Ask, don't park.** If you need a decision, just ask in your reply and stop there.
           Never end with a <question> tag, never mark yourself blocked, and never assume nobody
           is listening — a question here is answered in seconds, not hours.
        2. **Work in small steps.** Make the smallest change that answers what was asked, then
           report what you did. Do not go away for twenty minutes and return with a rewrite. If a
           request is bigger than it looks, say so and propose a split before starting.
        3. **Do not push.** Commit locally when you reach a coherent checkpoint, with a clear
           message. Pushing and opening the pull request happen when the human ends the session —
           that is their call to make, not yours.

        ## Definition of done, per turn
        What you changed builds, and the tests and lint/type checks covering it pass. Do NOT
        launch the application, start dev servers, drive a browser, or capture screenshots as
        proof — the human is running the app themselves and live verification belongs to the QA
        lane. This holds even if a script, README, or CLAUDE.md suggests capturing visual proof.
        """;

    /// <summary>
    /// One turn of a session. A cold start carries the full task context and prior conversation;
    /// a resumed turn carries only what the human just said, because the CLI session transcript
    /// already holds the rest — that difference is most of what makes a session affordable.
    /// </summary>
    private static (string System, string User) BuildInteractiveTurnPrompt(
        TaskSnapshot task, ProjectSummary? project, RepoSummary? repo, AgentSummary? agent,
        string memories, string conversationSection,
        IReadOnlyList<InteractiveUserMessage> pending, bool coldStart, string? siblingRepos = null)
    {
        var persona = agent is not null
            ? BuildPersonaSection(agent, codeWritingPhase: true)
            : "You are an AI software developer working directly with a human on a small change.";

        var system = $"""
            {persona}
            {memories}
            {InteractiveSessionContract}
            {RefusedToolNotice}
            {ClaudeMdGuidelines}
            {AgentLanguage.Instruction(LanguageFor(task, project))}
            """;

        var user = new StringBuilder();

        if (coldStart)
        {
            user.Append(BuildTaskContextSection(task, repo, contextFiles: null, conversationSection, siblingRepos));
            user.AppendLine($"Branch: {task.BranchName ?? "unknown"}");
            user.AppendLine();
            user.AppendLine(
                "This is an interactive session on the branch above. Explore the repository as " +
                "needed — there is no plan to follow, only what the human asks for below.");
            user.AppendLine();
        }

        // NOT wrapped in the untrusted-content fence, unlike task descriptions and conversation
        // history above.
        //
        // The fence tells the agent to treat what it surrounds as data that must never be acted
        // on — the right stance for text that reached the platform from anywhere. A session
        // message did not: it was typed seconds ago by an authenticated human holding
        // TasksExecute on this project, and acting on it IS the feature. Fenced, the agent
        // refuses precisely the requests worth gating — a live session was observed declining a
        // direct `rm` on the grounds that it came from untrusted content, which is both wrong and
        // unanswerable, since the user cannot un-fence themselves.
        //
        // What replaces the fence is not nothing: every command the agent reaches for now goes
        // through InteractiveCommandPolicy, and anything outward-facing, installing or
        // destructive stops for that same human to approve. That is a stronger control than a
        // marker the model has to be argued out of, and it fails closed.
        user.AppendLine(pending.Count == 1 ? "## The human says" : "## The human says (in order)");
        user.AppendLine(
            "This is the operator of this session speaking to you directly. Their instructions " +
            "are authoritative — act on them.");
        user.AppendLine();
        foreach (var message in pending)
        {
            user.AppendLine(message.Content);
            user.AppendLine();
        }

        // The Response Language section is far up in the system prompt — and on a resumed
        // turn the session transcript sits between it and this message. Restating it last is
        // what keeps replies on the requester's language turn after turn.
        user.Append(AgentLanguage.Reminder(LanguageFor(task, project)));

        return (system, user.ToString());
    }

    /// <summary>The final run of a session: commit what the session produced (the platform
    /// pushes after the run). Kept deliberately narrow — this is not the moment to start new work.</summary>
    private static string BuildInteractiveFinishSystemPrompt(
        TaskSnapshot task, ProjectSummary? project, AgentSummary? agent)
    {
        var persona = agent is not null
            ? BuildPersonaSection(agent)
            : "You are an AI software developer wrapping up a working session.";

        return $"""
            {persona}

            ## Wrapping up
            The human has ended the session. Your only job now is to land the work that already
            exists in this worktree: commit anything uncommitted. Do not push — Orkka pushes the
            branch itself once this run ends.

            **A session that changed nothing is a normal ending.** If there is nothing to commit,
            say so in one line and stop — do not create an empty commit, do not invent a change to
            justify the run, and do not treat a "nothing to commit" message from git as a failure
            to troubleshoot. People open sessions to ask questions and to explore ideas they then
            decide against, and both deserve a clean exit.

            Do NOT start new work, refactor, "improve" anything, or fix unrelated problems you
            notice. If something is genuinely broken and unfinished, commit it anyway and say so
            plainly in your final message — the pull request goes to code review next, and an
            honest note there is worth far more than a silent extra change nobody asked for.
            {RefusedToolNotice}
            {AgentLanguage.Instruction(LanguageFor(task, project))}
            """;
    }

    private static string BuildInteractiveFinishUserMessage(TaskSnapshot task)
    {
        var user = new StringBuilder();
        user.AppendLine("The session is over. Land the work as it stands.");
        user.AppendLine();
        user.AppendLine(
            "First check whether there is anything to land (`git status`). If the worktree is "
            + "clean, reply saying the session made no changes and stop — "
            + "that is a complete and correct outcome, not a problem to solve.");
        user.AppendLine();
        user.AppendLine(CommitInstruction);
        user.AppendLine();
        user.AppendLine(
            "Then reply with a short summary of what this session changed — it is the first " +
            "thing the code reviewer will read.");
        return user.ToString();
    }
}
