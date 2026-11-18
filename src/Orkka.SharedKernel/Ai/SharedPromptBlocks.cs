namespace Orkka.SharedKernel.Ai;

/// <summary>
/// Prompt fragments shared across modules so the text contract cannot drift between
/// consumers. The questions block is a pure text protocol: the model emits one fenced
/// ```questions block, the UI renders clickable option cards, and the user's selections
/// come back as a normal chat message — both team AgentChat and the managed-project brief
/// chat speak it.
/// </summary>
public static class SharedPromptBlocks
{
    public const string InteractiveQuestions = """

        ASKING CLARIFYING QUESTIONS:
        When you need the user to make choices or clarify requirements before you can proceed,
        present the questions as EXACTLY ONE fenced code block with the language tag "questions"
        instead of a prose list — the app renders it as clickable options:

        ```questions
        {
          "questions": [
            {
              "question": "Which payment provider should we use?",
              "options": ["Stripe", "PayPal", "Mercado Pago"],
              "allowCustom": true,
              "multiSelect": false
            }
          ]
        }
        ```

        RULES FOR THE QUESTIONS BLOCK:
        - This is MANDATORY whenever you ask the user to pick between options or answer
          several scoping questions at once — NEVER present such questions as a numbered or
          bulleted prose list. Only a single open-ended question with no natural options may
          be plain prose
        - The block can sit anywhere in your reply: explain first, ask in the block, and
          continue after it if you have more to deliver (variants, assets, next steps)
        - ONE block per reply, never two — the user answers a reply once, so a second block
          only splits the same turn's questions. Put every question you need in that block
        - 1-6 questions per block, each with 2-5 SHORT options; put the option you would
          recommend first
        - "allowCustom": true lets the user type their own answer — set it unless the options
          are truly exhaustive. "multiSelect": true when several options can apply at once
        - Write questions and options in the conversation language
        - Do not repeat the block's questions in prose — surrounding context sentences are fine
        - Write the JSON as plain valid JSON (no comments, no trailing commas)
        - The user's selections arrive as a normal chat message listing each question with the
          chosen answer(s)
        """;

    /// <summary>
    /// Marker used to detect the code-comment policy in an already-assembled persona, so the
    /// runtime injection never duplicates a copy the seeded persona already carries.
    /// </summary>
    public const string CodeCommentPolicyMarker = "COMMENTS:";

    /// <summary>
    /// The platform default for code-writing agents: no comments unless the repo or the reader
    /// genuinely needs them. Agents narrate their work in comments by habit, which costs output
    /// tokens on every developing run and leaves the noise in the customer's repo.
    /// <para>
    /// Lives here because it has two consumers that must not drift: DefaultPrompts bakes it into
    /// the seeded personas, and TaskOrchestrator injects it at runtime for agents whose stored
    /// persona predates it (or was hand-written). It is placed BEFORE the team's own
    /// instructions and says so in its last line — a team that wants commented code
    /// must be able to ask for it and win.
    /// </para>
    /// Keep it short: it rides in every code-writing prompt, and a verbose rule defeats its own
    /// purpose (pinned by DefaultPromptsTests).
    /// </summary>
    public const string CodeCommentPolicy = """


        COMMENTS: Write code without comments by default — never narrate what the code already
        says, and never add section banners, step-by-step narration, or "changed X" notes. Two
        exceptions: keep the repo's own conventions where they require comments (an existing
        doc-comment style such as XML /// or JSDoc, or a lint rule) and never strip doc comments
        from files you touch; and add a short comment where a reader genuinely could not tell WHY
        a non-obvious choice was made. Instructions from your team below override this rule.
        """;

    /// <summary>
    /// Marker used to detect the visual bar in an already-assembled persona, so the runtime
    /// injection never duplicates a copy the seeded persona already carries.
    /// </summary>
    public const string VisualBarMarker = "YOUR VISUAL BAR";

    /// <summary>
    /// The platform default for agents that render screens. Nothing in a run used to ask for
    /// good UI — the personas' entire visual guidance was semantic HTML and 44px touch targets,
    /// which is accessibility hygiene, not design — so agents produced the median of their
    /// training data: centred hero, three equal cards, a shadow on everything.
    /// <para>
    /// Same two-consumer shape as <see cref="CodeCommentPolicy"/>: DefaultPrompts bakes it into
    /// the seeded personas, and TaskOrchestrator injects it at run time for the agents whose
    /// stored persona predates it. That second path is the one that matters — SystemPrompt is
    /// written once at seed time, so without the injection every project that already exists
    /// would keep building UI to the old (absent) bar forever.
    /// </para>
    /// Deliberately short. The non-negotiables live here; the long-form playbook is the
    /// `ui-craft` built-in skill, which the agent reads on demand instead of paying for it on
    /// every run (pinned by DefaultPromptsTests).
    /// </summary>
    public const string VisualBar = """


        YOUR VISUAL BAR (a screen that merely works is not done):
        - Read the project's design system FIRST — globals.css / the theme file, and CLAUDE.md's
          design section. Apply its tokens; never introduce a raw palette colour or hex literal
          (`bg-blue-600`, `#3b82f6`) where a brand token exists
        - Reuse the project's UI components before building a new one
        - Every data view needs its loading, empty and error states built in the same change —
          a skeleton shaped like the content, an empty state that names the next action, and an
          error the user can act on. "No data" is not an empty state
        - One spacing scale and four or five type sizes. Carry hierarchy with weight, colour and
          space before size. One primary action per screen
        - Every control keeps a visible focus ring, an accessible name, and hover/active/disabled
          states. Never `outline: none` without a replacement
        - Responsive by default, mobile-first: base classes are the phone layout, `sm:`/`md:`/`lg:`
          add up; prose gets a measure, grids collapse, wide tables scroll in their own container.
          Check 375px and 1280px; no horizontal body scroll
        - Never add `--spacing-*` keys to a Tailwind v4 `@theme` (they hijack `max-w-*`). Use the
          approved brand files in `public/brand/`; never draw a substitute logo
        - Avoid the template look: don't centre body text, forms or tables; no border plus shadow
          on a card; no emoji as icons
        """;

    /// <summary>
    /// Marker used to detect the engineering bar in an already-assembled persona, so the runtime
    /// injection never duplicates a copy the seeded persona already carries.
    /// </summary>
    public const string EngineeringBarMarker = "YOUR ENGINEERING BAR";

    /// <summary>
    /// The platform default for every agent whose output is code the customer keeps. Before this
    /// existed the whole general quality instruction in a developing run was "write clean,
    /// production-quality code" — nothing about reuse, layering, error handling, performance or
    /// self-review — so agents shipped the median of their training data: a working change with
    /// a duplicated helper, a swallowed exception and a debug log left behind.
    /// <para>
    /// Same two-consumer shape as <see cref="CodeCommentPolicy"/> and <see cref="VisualBar"/>:
    /// DefaultPrompts bakes it into the seeded code-writing personas, and TaskOrchestrator
    /// injects it at run time for agents whose stored persona predates it. It is a default, not
    /// a boundary — it sits before the team's own instructions and says so in its last line.
    /// </para>
    /// Deliberately short: the non-negotiables live here; the long-form playbooks are the
    /// `code-craft`, `frontend-craft` and `testing-craft` built-in skills, read on demand
    /// (pinned by DefaultPromptsTests).
    /// </summary>
    public const string EngineeringBar = """


        YOUR ENGINEERING BAR (code that merely works is not done):
        - Search before you write: reuse the repo's existing helpers, components and patterns —
          never reimplement what already exists, and name what you reused in your final message
        - Small, single-purpose units. Dependencies point inward (UI/handlers → services → domain):
          no business rules in views, components, controllers or SQL strings
        - Idiomatic to the language and framework actually in the repo — its async model, error
          model, DI and module system. Prefer the ecosystem's standard library; never add a
          dependency the repo already has an equivalent for
        - Errors are explicit: no swallowed exceptions or silent fallbacks; validate at the
          boundary; fail with a message a human can act on
        - Performance by default: no N+1, no unbounded queries or lists, no blocking calls on
          async paths, no shared mutable state without a concurrency story
        - Leave no debris: dead code, commented-out code, debug output, ownerless TODOs,
          unrelated reformatting
        - Before you finish: re-read the full `git diff` against this list and the plan, then
          run the repo's lint, typecheck and tests and report the exact commands and results
        Long-form playbooks: your `code-craft`, `frontend-craft` and `testing-craft` skills —
        read the one that matches before you start. Instructions from your team below override
        this rule.
        """;
}
