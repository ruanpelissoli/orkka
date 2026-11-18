namespace Orkka.Modules.Agents.Domain;

using Orkka.Modules.Agents.Contracts;

/// <summary>
/// The starter skills every project gets, mirroring how the agent roster is seeded. Each
/// built-in carries a stable <see cref="BuiltInSkillSpec.Key"/> so the startup backfill can
/// top up older projects without duplicating a skill the user renamed, and a list of roles
/// it is assigned to out of the box.
/// </summary>
internal static class SkillSeeder
{
    internal sealed record BuiltInSkillSpec(
        string Key,
        string Slug,
        string Name,
        string Description,
        string Content,
        IReadOnlyList<AgentRole> DefaultRoles);

    public static readonly IReadOnlyList<BuiltInSkillSpec> BuiltIns =
    [
        new(
            Key: "groom-backlog",
            Slug: "groom-backlog",
            Name: "Groom the backlog",
            Description: "Review Drafting tasks for clarity, duplicates and missing acceptance criteria; tidy titles, split oversized work, flag what is blocked. Use when asked to groom, triage or clean up the backlog.",
            Content: """
                # Groom the backlog

                You are tidying this project's backlog so the developer agents can pick work up without guessing.

                ## Steps
                1. List the tasks in the **Drafting** and **Ready** lanes (use `list_tasks`, or read the board if tools are unavailable).
                2. For each task, check:
                   - Is the title a clear, imperative outcome? Rewrite vague titles.
                   - Does the description state *what* and *why*, and at least one acceptance criterion?
                   - Is it a duplicate of, or subsumed by, another task? Note the pair.
                   - Is it too large for one run (touches several subsystems, or has 4+ independent outcomes)? Propose a split.
                   - Is it blocked by another task or by a decision only a human can make?
                3. Do **not** submit tasks or move lanes yourself — grooming is advisory.

                ## Output
                Reply with a short report, grouped as:
                - **Ready to go** — tasks that are clear (one line each).
                - **Needs detail** — task + the one question that would unblock it.
                - **Duplicates / merges** — pairs and which to keep.
                - **Split suggestions** — task → proposed sub-tasks (titles only).
                - **Blocked** — task + what it waits on.

                If you have the `create_tasks` tool and the human explicitly asked you to create the split sub-tasks, create them in Drafting; otherwise only propose.
                """,
            DefaultRoles: [AgentRole.PM]),

        new(
            Key: "release-notes",
            Slug: "release-notes",
            Name: "Write release notes",
            Description: "Turn recently completed tasks and merged PRs into user-facing release notes (Added / Changed / Fixed). Use when asked for release notes, a changelog, or 'what shipped'.",
            Content: """
                # Write release notes

                Produce release notes a customer could read, from what actually shipped.

                ## Steps
                1. Gather what shipped: tasks in **Done** since the last release (or the period the human named), plus their PR titles and merge dates. Use `list_tasks` / `get_task_history` when available; otherwise read the board and the git log of the repositories in your workspace.
                2. Ignore internal-only work (refactors with no user-visible effect, CI tweaks) unless the human asked for an engineering changelog.
                3. Group entries under **Added**, **Changed**, **Fixed**, **Removed**. One line per entry, written for the product's users — outcome first, no ticket numbers in the sentence (append the task key in parentheses).
                4. Open with a one-paragraph summary of the release theme when there are 5+ entries.

                ## Output
                Markdown only. If you can create documents, save it to the project's Documents library as `Release notes — <date or version>` and say so; otherwise reply with the markdown.
                """,
            DefaultRoles: [AgentRole.PM]),

        new(
            Key: "security-audit",
            Slug: "security-audit",
            Name: "Security audit",
            Description: "Review the codebase (or a diff) for common security issues: injection, auth/authz gaps, secrets in code, unsafe deserialization, SSRF, dependency CVEs. Use when asked for a security review or audit.",
            Content: """
                # Security audit

                Look for exploitable problems, not style. Read the code before you write anything.

                ## Checklist
                - **Injection**: SQL/NoSQL built by string concatenation, shell commands from user input, HTML rendered without escaping, path traversal in file access.
                - **AuthN/AuthZ**: endpoints missing auth, object-level access (IDOR: can user A read B's rows?), privilege escalation paths, tokens/sessions that never expire.
                - **Secrets**: keys, passwords or tokens in source, config committed with real values, secrets logged.
                - **Input validation**: unbounded sizes, missing type checks at trust boundaries, mass assignment.
                - **Crypto**: home-grown hashing, MD5/SHA1 for passwords, ECB mode, predictable randomness for tokens.
                - **Network**: SSRF (fetching user-supplied URLs), open redirects, permissive CORS.
                - **Dependencies**: known-vulnerable packages (check lockfiles against advisories you know; call out anything pinned years old).
                - **Data exposure**: verbose errors to clients, PII in logs, debug endpoints left on.

                ## Output
                A findings list, most severe first. For each: **severity** (Critical/High/Medium/Low), **where** (file:line), **what** could happen, **fix** (concrete). End with a two-line overall assessment. If you found nothing in a category, say so — silence reads as "not checked".
                Do not modify code unless the human asked you to fix the findings.
                """,
            DefaultRoles: [AgentRole.CodeReviewer]),

        new(
            Key: "dependency-update",
            Slug: "dependency-update",
            Name: "Dependency update",
            Description: "Inventory outdated or vulnerable dependencies across the project's package manifests and propose a safe update plan (patch/minor first, majors flagged). Use when asked to update, audit or bump dependencies.",
            Content: """
                # Dependency update

                Find what is stale, decide what is safe, and never bump blindly.

                ## Steps
                1. Locate every manifest in the workspace: `package.json`, `*.csproj` / `Directory.Packages.props`, `go.mod`, `requirements*.txt` / `pyproject.toml`, `Gemfile`, `pubspec.yaml`, `Cargo.toml`.
                2. For each dependency, note current version, and — using the package manager if available (`npm outdated`, `dotnet list package --outdated`, `go list -m -u all`, `pip list --outdated`) or your own knowledge otherwise — the latest patch, minor and major.
                3. Classify:
                   - **Patch/minor** — safe by default; batch them.
                   - **Major** — list breaking-change highlights you know of and what in this codebase touches them.
                   - **Security** — anything with a known advisory goes first, whatever the semver distance.
                4. If the human asked you to apply updates: do patch/minor in one change, run the project's install and tests, and stop at the first failure with a clear note. Leave majors for separate, explicitly requested work.

                ## Output
                A table: package · current · target · type (patch/minor/major/security) · risk note. Then a suggested order of operations. Mention any lockfile you regenerated.
                """,
            DefaultRoles: [AgentRole.DevOps]),

        new(
            Key: "ui-craft",
            Slug: "ui-craft",
            Name: "Ship UI that looks designed",
            Description: "The visual bar for any screen or component you build: brand tokens over raw palette colours, one spacing and type scale, and the five states every data view owes the user. Read it before writing UI.",
            Content: """
                # Ship UI that looks designed

                Default model output is the median of every bootstrap template ever written: centred
                hero, three equal cards, `shadow-lg` on everything, emoji as icons. That is the thing
                to beat. Work through this before you write a component, and again before you finish.

                ## 1. Find the design system before you invent one
                - Read `globals.css` (or the theme file), the Tailwind config, and CLAUDE.md's design
                  section. The project has decided colours, fonts and radii — you are applying them,
                  not choosing them.
                - **Never introduce a raw palette colour** (`bg-blue-600`, `text-gray-500`, `#3b82f6`)
                  when a brand token exists. If a token you need is missing, add it to the theme once
                  and use it everywhere — do not hard-code the hex at the call site.
                - Reuse the existing components (`ui/button`, `ui/card`, `ui/input`, `ui/badge`, …)
                  before building a new one. A second Button with a different padding scale is how a
                  UI stops looking like one product.

                ## 2. Layout
                - One spacing scale, 4px-based. Pick from it; never freehand `mt-[13px]`.
                - Give the page a max content width (`max-w-5xl`/`max-w-7xl` + `mx-auto`) and one
                  consistent gutter. Full-bleed text at 1920px reads as unfinished.
                - Align to a grid. Related things share an edge; unrelated things get real space
                  between them. Proximity does more for clarity than borders do.
                - Do not centre everything. Centre a hero or an empty state; left-align body content,
                  forms, and tables — centred paragraphs are hard to read.
                - Borders **or** shadows, not both. Pick one elevation language and hold it.
                - One border-radius value across the app (two at most: controls and containers).

                ## 3. Type
                - Four or five sizes total. If you need a sixth, you are solving a hierarchy problem
                  with font size instead of weight, colour or space.
                - Line height ~1.5 for body, ~1.2 for headings. Measure 60–75 characters for prose.
                - Weight and colour carry hierarchy better than size. A muted `text-secondary` label
                  above a strong value beats two similar greys.
                - Numbers in tables: tabular figures, right-aligned.

                ## 4. The five states every data view owes the user
                Build all of them. A screen that only handles the populated case is half-built.
                1. **Loading** — a skeleton shaped like the real content, not a centred spinner. In
                   Next.js this is `loading.tsx` or `<Suspense>` with a skeleton fallback.
                2. **Empty** — say what would be here, why it is not, and give the primary action as a
                   button. Never ship the string "No data".
                3. **Error** — what failed, in the user's words, plus a retry affordance. Never a
                   raw exception or a bare "Something went wrong" with no way forward.
                4. **Partial / unauthorised** — some rows hidden, or the user lacks access. Say which.
                5. **Populated** — and check it at one item, at a realistic count, and with one field
                   holding a 200-character string. Long content is where layouts break.

                ## 5. Forms
                - Label above the input, always. Placeholder is not a label.
                - Validate on blur and on submit; show the message next to the field, tied with
                  `aria-describedby`. A single summary at the top is not enough.
                - Disable submit and show a pending state while the action runs; never let a
                  double-click double-submit.
                - **Never clear the user's input on a failed submit.**
                - Mark required fields, and say what "valid" means before they get it wrong.

                ## 6. Interactive states
                Every clickable thing needs `hover`, `focus-visible`, `active`, and `disabled`.
                - **Never remove the focus ring** without replacing it with something at least as
                  visible. Keyboard users navigate by it.
                - Hit targets ≥44px on touch. Icon-only buttons need an `aria-label`.
                - Anything that navigates is an `<a>`/`<Link>`; anything that acts is a `<button>`.

                ## 7. Motion
                150–200ms, ease-out, on `opacity` and `transform` only. Animate to explain a change
                (a panel opening, an item leaving), never for decoration. Honour
                `prefers-reduced-motion: reduce`.

                ## 8. Contrast and dark mode
                - 4.5:1 for body text, 3:1 for large text and UI borders. Muted grey on white is the
                  most common failure — check it.
                - Never encode meaning in colour alone: pair status colour with an icon or a label.
                - If the project supports dark mode, build both. A screen that only works in light
                  mode is a bug, not a follow-up.

                ## 9. Responsive by default
                Every screen is built for the phone first and grows up, never the other way round.
                A layout that only holds at the width you happened to preview is not responsive.
                - **Mobile-first classes**: the unprefixed class is the phone layout; `sm:`, `md:`,
                  `lg:` add up. Never design at 1280 and bolt on `max-md:` fixes.
                - **Prose gets a measure, never a width**: `max-w-prose` / `max-w-2xl` on paragraphs
                  and intros. Never a fixed `w-[…]`, and never a token that resolves to something
                  tiny (see §10 — `max-w-md` reading a custom `--spacing-md` is how a paragraph ends
                  up one word per line).
                - **Grids collapse**: `grid-cols-1 sm:grid-cols-2 lg:grid-cols-4`, or
                  `grid-cols-[repeat(auto-fill,minmax(16rem,1fr))]`. Cards never get a fixed width.
                - **Images keep their ratio**: `next/image` with `fill` + `sizes` inside an
                  `aspect-square` / `aspect-video` box, or `width`/`height` plus `h-auto w-full`.
                - **Wide content scrolls in its own box**: tables, code and charts live inside
                  `overflow-x-auto`; tables become cards on phones or keep a visible scroll
                  affordance. The body itself never scrolls horizontally.
                - **The shell adapts**: the header collapses to a compact bar (logo + one action, or
                  a menu button) below `md:`; sidebars become a drawer or a bottom bar; sticky
                  elements never cover the content on a 375px-tall keyboard-open viewport.
                - **Fluid type where it matters**: hero headlines use `text-3xl sm:text-5xl` (or
                  `clamp()` in the theme), never one desktop size.
                - **Touch**: every target is at least 44px; hover-only affordances have a visible
                  default state.
                - Check 375px, 768px and 1280px before you call it done.

                ## 10. Tailwind v4 tokens — the namespaces are shared
                `@theme` variables are not free-form: Tailwind resolves utilities against namespaces,
                and a custom key can shadow a built-in scale.
                - **Never add `--spacing-*` keys** (`--spacing-sm`, `--spacing-md`, `--spacing-4`).
                  `max-w-md`, `w-md`, `size-md` and the whole numeric scale resolve against them
                  first — `--spacing-md: 1.25rem` turns every `max-w-md` into 1.25rem. Spacing uses
                  Tailwind's numeric scale (`p-4`, `gap-6`, `py-16`); there is nothing to add.
                - Leave `--container-*` and `--breakpoint-*` alone unless you mean to change every
                  breakpoint or container width in the app.
                - New tokens are roles in their own namespace: `--color-warning`, `--color-surface-
                  raised`, `--radius-card`, `--shadow-card`. Overriding `--radius-*` and `--font-*`
                  is fine — that is the brand deciding them.
                - Fonts named in `@theme` must be loaded somewhere (the root layout). A font token
                  nobody loads renders the system fallback and the brand quietly disappears.

                ## 11. Brand assets and the first impression
                - The approved logo, wordmark and spinner are committed under `public/brand/`
                  (and `src/app/icon.svg` is the favicon). The header shows the mark next to the
                  product name and links home; loading states use the spinner. Never a coloured
                  `<div>` with an initial, an emoji, or a text-only header when the mark exists.
                - The first screen the owner sees is populated: seed the data it needs (the deploy
                  runs the seed into an empty database). An empty state, a placeholder, lorem ipsum
                  or a random-photo service (`picsum`, `placehold`) on the landing screen is a defect.
                  Where a real photo does not exist yet, use an on-brand SVG illustration.
                - The product's name is the project's name — never the placeholder name a mockup
                  or preview used.

                ## Before you call it done
                - [ ] No raw palette colours or hex literals — brand tokens only
                - [ ] No `--spacing-*` / `--container-*` / `--breakpoint-*` keys added to `@theme`
                - [ ] Loading, empty, and error states exist for every data view I touched
                - [ ] Focus-visible survives on every control
                - [ ] Checked at 375px, 768px and 1280px: no horizontal body scroll, no one-word-per-line
                      wrapping, no overlapping or clipped elements, header usable on a phone
                - [ ] The approved brand files are used; the first screen opens on real content
                - [ ] Reused existing components; any new one follows their prop and size conventions
                - [ ] Nothing on screen is centred by accident, and no card has both a border and a shadow
                """,
            DefaultRoles: [AgentRole.FrontendDev, AgentRole.FullStackDev, AgentRole.MobileDev]),

        new(
            Key: "brand-consistency",
            Slug: "brand-consistency",
            Name: "Match the project's brand",
            Description: "Where this project's decided colours, fonts, tone and approved assets live, and how to apply them so every screen and asset agrees. Use before styling a screen or producing any visual asset.",
            Content: """
                # Match the project's brand

                This project has a decided brand: a palette, fonts and a tone, plus assets built from
                them. Every screen and every new asset must agree with it. Two agents inventing
                "something blue and modern" independently is exactly how a product stops looking like
                one product.

                ## Where the brand lives
                1. **Your prompt** — a "Project Brand" section carries the palette, fonts and tone
                   verbatim when the project has a brand kit. That is the authority; it beats anything
                   you infer from the code.
                2. **`globals.css` / the theme file** — the same tokens as CSS custom properties, and
                   what Tailwind utilities resolve against.
                3. **CLAUDE.md** — the design-system section, plus any convention the team added.
                4. **The Approved Design Assets section** of your prompt — logos, icons, illustrations
                   the human approved, with a ready-to-run download command each.

                If the prompt and the stylesheet disagree, the prompt's brand kit is correct and the
                stylesheet has drifted — say so in your final message rather than silently picking one.

                ## Applying it in code
                - Use the token, never the literal: `bg-primary`, not `bg-[#4f46e5]` and not
                  `bg-indigo-600`.
                - If a needed token does not exist, add it to the theme block once, named by role
                  (`--color-warning`) not by hue (`--color-orange`), and use it everywhere.
                - Fonts: load the decided display and body faces through the framework's font loader
                  and wire them to the theme. Do not leave a stack of system fallbacks in place and
                  call the brand done.
                - Assets: the approved identity (logo mark, wordmark, loading spinner) is usually
                  already committed under `public/brand/` — use those paths. Anything else, download
                  the approved file into the repo's static folder and reference the committed path.
                  **Never commit a signed URL** — it expires.
                - Do not invent a logo in code (a coloured `<div>` with the app's initial, an emoji,
                  a text-only header) when an approved logo asset exists — and never edit or
                  regenerate the approved files.
                - The product's name is the project's name. A mockup or preview may have used a
                  placeholder name; it never ships.

                ## Applying it in generated assets
                - Read the palette before generating anything, and name the exact colours in the
                  prompt you write for the image model.
                - Match the established tone. A playful brand does not get a corporate stock-photo
                  hero halfway through the project.
                - New assets in an existing family (icon sets, sprites, illustrations) must reference
                  the earlier asset so the style actually carries over.

                ## Tone is part of the brand
                Microcopy included. Button labels, empty states and error messages all carry voice —
                match the tone the brand kit names, and keep it consistent across screens.
                """,
            DefaultRoles: [
                AgentRole.Designer, AgentRole.FrontendDev, AgentRole.FullStackDev, AgentRole.MobileDev]),

        new(
            Key: "ux-direction",
            Slug: "ux-direction",
            Name: "Direct a screen's UX",
            Description: "Turn a feature into a concrete screen spec a dev agent can build: layout, hierarchy, the states, and the copy. Use when asked how a feature should look or work, or to review a screen's UX.",
            Content: """
                # Direct a screen's UX

                You decide how a screen is laid out, what it emphasises, and what it says. The dev
                agent builds to your spec. Be concrete — "make it clean and modern" is not direction,
                and the agent will fall back to the generic template if you leave the decisions open.

                ## Understand first
                - Who opens this screen, and what are they trying to finish? Name the single primary
                  job. Everything else on the screen is subordinate to it.
                - What do they arrive knowing, and what do they need the screen to tell them?
                - What is the state on first use — almost always empty. Design that first; it is the
                  screen most users see first and the one that gets skipped.

                ## Specify
                Deliver a spec with these sections. Reference existing components by name.

                1. **Layout** — regions top to bottom, and what governs width. Say which parts are
                   fixed and which scroll.
                2. **Hierarchy** — the one primary action, styled as the primary button. Everything
                   else is secondary or tertiary. If you cannot name one primary action, the screen is
                   doing two jobs and should be two screens.
                3. **Content and copy** — the actual strings. Headings, button labels, empty-state
                   text, error messages. Write them in the brand's voice; do not leave "TODO copy".
                4. **States** — loading, empty, error, partial, populated. Say what each shows and
                   what the user can do from it.
                5. **Responsive** — what changes at mobile: what stacks, what collapses, what is
                   dropped entirely.
                6. **Interaction** — what happens on submit, on failure, where focus goes after the
                   action, what the user sees while it is pending.

                ## Judgement calls worth making explicitly
                - Table vs cards: tables for scanning and comparing many rows; cards when each item
                  needs an image or more than ~4 fields.
                - Modal vs page: modals only for short, focused, interruptible tasks. Anything with
                  more than a few fields, or that the user might want to link to, is a page.
                - Inline edit vs form page: inline for one field; a form for a set of related fields.
                - Progressive disclosure beats a wall of options — but never hide the primary action.

                ## Reviewing an existing screen
                Say what is wrong and what to do instead, ordered by how much it hurts the user:
                does the primary action stand out; is anything centred that should be left-aligned; do
                the empty and error states exist and say something useful; is there one spacing and
                type scale; does it hold up at 375px; does the copy sound like this product. Give
                specific replacements, not adjectives.

                ## Boundaries
                You do not commit code. Hand the spec to the dev agent, or reply with it in chat.
                """,
            DefaultRoles: [AgentRole.Designer]),

        new(
            Key: "design-review",
            Slug: "design-review",
            Name: "Review UI changes",
            Description: "Rubric for reviewing a diff that touches components, pages or styles: brand tokens, missing states, hierarchy, accessibility. Use when the change under review renders anything.",
            Content: """
                # Review UI changes

                Apply this when the diff touches components, pages, or styles. It is in addition to
                the normal review — correctness still comes first.

                ## Blocking ([Critical]) — objective, not taste
                - A raw palette colour or hex literal where the project has a brand token
                  (`bg-blue-600`, `#3b82f6`, `text-gray-500`).
                - A data view with no empty state, or no error state, when it can plausibly hit either.
                - `outline: none` / `focus:outline-none` with no visible focus replacement.
                - An interactive control that is a `<div>` with an onClick and no role, tabindex, or
                  keyboard handler; an icon-only button with no accessible name.
                - Meaning carried by colour alone (a red dot with no label or icon).
                - Input lost on a failed submit, or a submit that can fire twice.
                - A new component duplicating one that already exists in the project's `ui/` folder.

                ## Advisory ([Suggestion]) — say it, do not block on it
                - Spacing or type values outside the project's scale.
                - A card carrying both a border and a shadow, or a radius that does not match its
                  neighbours.
                - Centred body text, forms, or tables.
                - A spinner where a skeleton would hold the layout.
                - Hierarchy: several buttons competing to be primary.
                - Copy that does not sound like the rest of the product.
                - Long-content and single-item cases the layout probably does not survive.

                ## How to write it
                Point at the line, name the token or component that should have been used, and give
                the replacement. "Use `bg-primary` (defined in globals.css) instead of `bg-blue-600`"
                is actionable; "the styling feels off" is not.

                Taste disagreements are always [Suggestion]. Do not bounce a task over a radius you
                would have chosen differently — the human decides visual direction, and a blocked
                merge costs a full review cycle.
                """,
            DefaultRoles: [AgentRole.CodeReviewer]),

        new(
            Key: "code-craft",
            Slug: "code-craft",
            Name: "Write code a Staff engineer would merge",
            Description: "The engineering bar for any code you write: read the architecture and reuse before writing, SOLID without over-engineering, error handling, async/concurrency, security defaults, per-language idioms, and the self-review before done. Read it before writing code.",
            Content: """
                # Write code a Staff engineer would merge

                The bar is not "it works". The bar is: a Staff engineer on this team reads the diff and
                has nothing to add. Work through this before you write, and again before you finish.

                ## 1. Read the architecture first
                - Find the layers and module boundaries before you write a line: where do endpoints,
                  handlers, domain logic, persistence and cross-cutting concerns live in *this* repo?
                  Put new code where its neighbours already are. A handler in the wrong folder is a bug.
                - Reuse before you write. Search for an existing helper, service, component or type
                  that does the job (`rg` the concept, not just the name). If you reuse it, **name it
                  in your final message**; if you deliberately did not, say why.
                - Follow the repo's conventions over your own: error type, logging, DI style, folder
                  layout, test framework. Consistency beats local optimality.

                ## 2. SOLID in practice
                - **SRP** — smell: the God class/service that handles HTTP, validation, persistence and
                  notification. Split by reason-to-change.
                - **OCP** — smell: a `switch` on a type/kind string that every new variant has to
                  edit. Strategy or polymorphism once the third case arrives.
                - **LSP** — smell: a subtype that throws `NotSupported` for an inherited member. If it
                  cannot honour the contract, it is not a subtype.
                - **ISP** — smell: a fat interface where implementers stub half the members. Split by
                  consumer.
                - **DIP** — smell: `new HttpClient()` / `new Repository()` inside a service. Depend on
                  the abstraction and let the constructor/container supply it.

                ## 3. Patterns worth reaching for — and when not to
                Reach for: **strategy** (behaviour varies by kind), **factory** (construction needs a
                decision), **repository / unit-of-work** (only if the repo already has them — EF and
                Prisma often *are* the repository), **decorator** (cross-cutting behaviour on one
                contract: caching, retry, metrics), **CQRS-lite** (separate read models when queries
                and writes diverge), **options object** (more than three parameters, or any optional).

                Do not: add an interface with one implementation "for testability" unless the
                ecosystem idiomatically needs one (Go and .NET DI often do; TypeScript rarely does);
                abstract a single call site; add a generic base class to save three lines.

                ## 4. Naming and API shape
                - Intention-revealing names: `retryAfter` not `ra`; `isExpired()` not `check()`; no
                  abbreviation a new hire would have to look up.
                - No boolean traps: `render(true, false)` becomes an enum, an options object, or two
                  methods.
                - Immutability by default: `readonly` / `record` / `val` / `let`. Mutate with a reason.
                - Small public surface: expose what is used elsewhere and nothing more; the rest stays
                  `internal` / unexported / `private`.
                - Do not leak infrastructure types (DbContext, HTTP response, ORM entity) across layer
                  boundaries; map to a DTO or domain type at the edge.

                ## 5. Error handling
                - Use the ecosystem's convention: `Result`/`Either` where the repo uses them,
                  exceptions where it does not; Go returns errors; Rust returns `Result`. Never mix
                  within a module.
                - Never an empty catch. Never a catch-all that swallows and continues. Catch what you
                  can handle; let the rest propagate to the boundary that can.
                - Messages carry context: the id, the operation, expected vs actual. "Failed" is
                  useless in a log at 3am.
                - Validate at boundaries (request, file, queue message, env) once; trust inside.

                ## 6. Performance and concurrency
                - Async all the way: no `.Result` / `.Wait()` / `runBlocking` on an async path, no sync
                  I/O in a request handler. Pass cancellation tokens/contexts through — never
                  `CancellationToken.None` where one was offered.
                - Bounded parallelism (`SemaphoreSlim`, worker pool, `p-limit`) — never `WhenAll` over
                  an unbounded list of I/O.
                - Idempotency for anything retried (queues, webhooks, jobs): a key, an upsert, a dedupe.
                - Caching comes with invalidation and a TTL, or it is a bug waiting for the next deploy.
                - Queries: select only what you need, no N+1 (eager-load or batch), paginate every
                  list, index what you filter and sort on.
                - No shared mutable state without a concurrency story (lock, immutable snapshot, actor,
                  single writer).
                - No per-request work that belongs at startup: regex compilation, client construction,
                  reading config files, building lookup tables.

                ## 7. Security defaults
                - Validate and encode at boundaries; parameterized queries only — never string-built SQL.
                - No secrets in code, tests or fixtures; read from env/secret store; never log them.
                - Least privilege: the narrowest scope, permission or role that works.
                - Authorization on every entry point, object-level: can *this* user reach *this* row?

                ## 8. Language idioms that mark an expert
                | Ecosystem | Use these |
                |---|---|
                | TypeScript / Node | discriminated unions + exhaustive `switch`; `satisfies`; zod (or the repo's schema lib) at boundaries; no `any` — `unknown` and narrow; `readonly` / `as const`; strict mode on |
                | C# / .NET | `record` for values; pattern matching (`is`, `switch` expressions); `IAsyncEnumerable` for streams; `sealed` by default; nullable reference types on and honoured; `Span<T>` / `ArrayPool` only in measured hot paths |
                | Go | wrap errors with `%w`, check with `errors.Is/As`; `context.Context` first param; small interfaces defined by the consumer; table tests; no package-level mutable state |
                | Python | type hints everywhere + a checker; dataclasses/pydantic for data; context managers for resources; generators over materialised lists; `pathlib` not `os.path` strings |
                | Kotlin | data/sealed classes + exhaustive `when`; null safety (no `!!`); coroutines with structured concurrency; extension functions; immutable collections by default |
                | Swift | value types (structs, enums with associated values); `guard let` unwrapping; protocols + extensions; `async/await` + actors; `Codable` |
                | Rust | `Result` + `?` (`thiserror`/`anyhow` as the repo chooses); ownership over `clone()`; iterators over index loops; exhaustive `match`; no `unwrap()` outside tests |

                ## 9. Before you call it done
                - [ ] I ran `git diff` and read every line the way a reviewer would
                - [ ] I named what I reused — and what I chose not to, and why
                - [ ] No debris: dead code, commented-out code, debug output, TODOs without an owner,
                      unrelated reformatting
                - [ ] Tests written for the behaviour I added or changed, including the error path
                - [ ] Lint, typecheck and the test suite actually ran — and I reported the real
                      results, not "should pass"
                """,
            DefaultRoles: [
                AgentRole.FrontendDev, AgentRole.BackendDev, AgentRole.FullStackDev,
                AgentRole.GameDev, AgentRole.MobileDev, AgentRole.DevOps]),

        new(
            Key: "frontend-craft",
            Slug: "frontend-craft",
            Name: "Build React/Next.js like a senior frontend engineer",
            Description: "How senior frontend code is structured: server/client boundary, data fetching without waterfalls, state placement, component composition, forms, Tailwind discipline, accessibility and bundle performance. Read it before writing React/Next.js; ui-craft owns the visual bar.",
            Content: """
                # Build React/Next.js like a senior frontend engineer

                The visual bar lives in the `ui-craft` skill. This one is about the code: where data
                loads, where state lives, how components compose, and what ships to the browser.

                ## 1. Server/client boundary
                - React Server Components first. A component is a server component until it needs
                  state, effects, browser APIs or event handlers — then add `"use client"` **at the
                  leaf**, not at the page. Wrapping a page in `"use client"` throws the model away.
                - Server actions for mutations (`"use server"`), called from forms or `useTransition`.
                  No ad-hoc `/api` route + client `fetch` for something the server can do directly.
                - **Never fetch in `useEffect`.** Server data loads in the server component (`await`
                  in the body), or through the repo's client data layer (React Query/SWR) when that is
                  already the convention. `useEffect` + `useState` + `fetch` is the anti-pattern.

                ## 2. Data fetching and caching
                - Await in parallel: `const [a, b] = await Promise.all([getA(), getB()])`. Sequential
                  awaits of independent data are a waterfall — the most common App Router perf bug.
                - Stream: `<Suspense>` around the slow part with a skeleton fallback; `loading.tsx`
                  for whole routes.
                - Dedupe per request with React `cache()`; invalidate after a mutation with
                  `revalidatePath` / `revalidateTag` — `router.refresh()` is not a substitute.
                - Be explicit about caching (`fetch` options, `unstable_cache` / `"use cache"` per the
                  Next version in the repo). Know whether the route is static or dynamic, and why.

                ## 3. State
                - URL as state for anything shareable or navigable: filters, tabs, pagination,
                  selected id (`searchParams`, `nuqs` if the repo has it). A refresh must not lose it.
                - Derive, don't duplicate: compute from props/other state during render. Never copy a
                  prop into state; `useMemo` only with a measurement.
                - Colocate: state lives in the lowest component that needs it; lift only when two
                  siblings need it.
                - A store (Zustand/Jotai/Redux) only for truly global *client* state: theme, session,
                  realtime connection. Server data is not client state.

                ## 4. Component composition
                - Compound components and `children`/slots over boolean props:
                  `<Card><CardHeader/>…</Card>` beats `<Card showHeader headerTitle=…>`.
                - Headless + styled split: logic in a hook (`useCombobox`), presentation in a component.
                - One responsibility per component. A 400-line component is three components and a hook.
                - Reuse the project's `ui/` primitives; never fork a Button.

                ## 5. Forms
                - Progressive enhancement: `<form action={serverAction}>` works without JS; enhance
                  with `useActionState` / `useFormStatus` for pending and errors.
                - **One validation schema** (zod/valibot) imported by both the client and the server
                  action. Two schemas drift.
                - Pending state on submit (disabled button + label change); optimistic UI only where
                  the outcome is predictable (`useOptimistic`).
                - Preserve input on error: return field values + errors from the action; never reset
                  the form. Tie field errors with `aria-describedby`; focus the first invalid field.

                ## 6. Tailwind discipline
                - Design tokens via `@theme` / CSS variables; classes reference tokens (`bg-primary`,
                  `text-muted-foreground`), never raw palette or hex.
                - Tailwind v4 namespaces are shared: never add `--spacing-*` keys to `@theme` (a
                  custom `--spacing-md` is what `max-w-md`/`w-md` resolve to, and paragraphs wrap
                  one word per line); leave `--container-*`/`--breakpoint-*` alone. New tokens are
                  roles (`--color-warning`, `--radius-card`). Fonts named in `@theme` are loaded in
                  the root layout (`next/font`), or they render as the system fallback.
                - `cn()` for conditional classes, `cva` for variants. No string-concatenated classes.
                - No arbitrary values (`mt-[13px]`, `w-[347px]`) — pick from the scale or extend the
                  theme once.
                - No inline `style` for static styling; no `!important`. A specificity fight is a
                  structure problem.

                ## 7. Responsive by default
                - Mobile-first: base classes are mobile, `md:` / `lg:` add up. Never `max-md:` as the
                  default direction. Nothing gets a fixed pixel width; prose gets a measure
                  (`max-w-prose`), grids collapse (`grid-cols-1 sm:grid-cols-2 lg:grid-cols-3`),
                  images sit in an aspect-ratio box with `next/image` + `sizes`.
                - Container queries (`@container`) for components that render in slots of different
                  widths.
                - The app shell adapts: a compact header on phones, sidebars as drawers, ≥44px targets.
                - Check 375 / 768 / 1280. The body never scrolls horizontally; wide content scrolls in
                  its own `overflow-x-auto` container.

                ## 8. Accessibility and semantics
                - Native elements first: `<button>`, `<a>`/`<Link>`, `<label htmlFor>`, `<nav>`,
                  `<main>`, the repo's dialog primitive. A `<div onClick>` is a bug.
                - Every input has a label; every icon-only button has `aria-label`; headings are in
                  order with no skipped levels.
                - Keyboard paths: Tab reaches everything interactive, Enter/Space activates, Escape
                  closes.
                - Focus management: move focus into a dialog on open and back to the trigger on close;
                  on route change, focus the heading. Visible `focus-visible` everywhere.
                - Tokens, states, contrast and hierarchy are in `ui-craft` — read both.

                ## 9. Performance
                - Keep heavy code out of the client bundle: server components by default;
                  `dynamic(() => import(...))` for charts, editors, maps, markdown renderers.
                - `next/image` (with `sizes`), `next/font`, `next/link` — not `<img>`, `@import`
                  fonts, or `<a>` for internal routes.
                - `memo` / `useMemo` / `useCallback` only with a measured reason (profiler, or the
                  React Compiler is on). Premature memo is noise.
                - Avoid prop-drilling re-render cascades: composition (`children`) or a small context
                  near its consumers.

                ## 10. Testing hooks
                - Query by accessible role and name (`getByRole('button', { name: /save/i })`), then
                  by label/text. `data-testid` only when no role fits — and name it for what the
                  element *is*.

                ## 11. Other frameworks — same principles, different spelling
                - **Remix / React Router**: `loader` / `action` are the server boundary; `<Form>` for
                  mutations; `useFetcher` for non-navigating ones.
                - **Vite SPA**: no server boundary — data via React Query; route state in the URL via
                  the router's search params; code-split routes with `lazy()`.
                - **Vue / Nuxt**: `useAsyncData` / `useFetch` on the server; composables for logic;
                  `<script setup>`; Pinia only for global client state.
                - **Svelte / SvelteKit**: `+page.server.ts` `load` + form actions; runes/stores for
                  local state; progressive enhancement with `use:enhance`.

                ## Before you call it done
                - [ ] No `useEffect` fetching; `"use client"` only at leaves
                - [ ] Independent awaits are parallel; slow parts stream behind `<Suspense>`
                - [ ] Shareable state is in the URL; no prop copied into state
                - [ ] One schema validates client and server; input survives a failed submit
                - [ ] No arbitrary Tailwind values, inline styles, or raw palette colours
                - [ ] Keyboard path and focus management verified on every dialog and form
                - [ ] Checked at 375 and 1280; heavy client code is behind `dynamic()`
                """,
            DefaultRoles: [AgentRole.FrontendDev, AgentRole.FullStackDev]),

        new(
            Key: "testing-craft",
            Slug: "testing-craft",
            Name: "Write tests that catch regressions",
            Description: "What to test and how: behaviour over implementation, the unit/integration/e2e split, AAA structure, fakes over mocks, determinism, builders, and per-ecosystem conventions. Read it before writing tests — a later phase runs your suite and a red or missing test counts against you.",
            Content: """
                # Write tests that catch regressions

                A test earns its place by failing when behaviour breaks and staying green when only
                the implementation changes. Anything else is maintenance cost.

                ## 1. What to test
                - **Behaviour and contracts**, through the public surface: given this input/state,
                  this output/effect. Not private methods, not internal call order, not "the mock was
                  called once".
                - **Boundaries**: request → handler, service → store, UI → user. That is where bugs
                  hide.
                - **Error paths**: invalid input, not found, conflict, unauthorised, a dependency
                  failing, timeout/cancellation. One happy-path test is not coverage.
                - **Edge values**: empty list, one item, max size, unicode, zero, negative, the dates
                  the domain actually cares about.
                - Do **not** test the framework, the ORM or the language. Do not snapshot everything —
                  a snapshot nobody reads is a rubber stamp.

                ## 2. The split and what each owes
                - **Unit** — pure logic, milliseconds, no I/O, no framework boot. Owes: every branch
                  of the domain/service logic, including the errors.
                - **Integration** — the real wiring: handler + database (Testcontainers; an in-memory
                  provider only if the repo already trusts it), HTTP endpoint through the real
                  pipeline, component with its real providers. Owes: every public entry point hit at
                  least once, auth and validation included.
                - **E2E** — a user journey through the running app (Playwright). Owes: the few
                  critical flows (sign-in, create, pay, the core loop). Not a test per button.
                - Push tests down the pyramid: if a unit test proves it, do not write an e2e for it.

                ## 3. Structure
                - **Arrange / Act / Assert**, visibly separated. One *behaviour* per test: several
                  asserts on one outcome are fine; two unrelated acts are two tests.
                - Names read as specs: `Returns404WhenTaskBelongsToAnotherOrg`,
                  `it('keeps the draft when the save fails')`. Not `test1`, not `works`.
                - No logic in tests: no `if`, no loops building expectations, no re-implementing the
                  algorithm to compute the expected value. Hard-code the expected result.
                - One assertion library — the repo's. Assert the field or message that matters, not
                  merely `not null`.

                ## 4. Test doubles
                - Prefer **fakes** (an in-memory repository, a fake clock) over mocks with scripted
                  returns: fakes test behaviour, mocks test call shape.
                - **Don't mock what you own** — drive your own classes for real; mock only the true
                  external edge (a third-party HTTP API, the clock, randomness, email).
                - **Never mock the thing under test**, or its collaborators so thoroughly that the
                  test passes with the logic deleted. If the test survives `return null`, it tests
                  nothing.
                - Verify outcomes, not interactions — unless the interaction *is* the contract (an
                  event published, an email sent).

                ## 5. Determinism
                - **No `sleep`**, no real time: inject a clock (`TimeProvider`, `vi.useFakeTimers`,
                  `freezegun`); await the actual condition with the framework's `waitFor`.
                - Fixed IDs, fixed seeds for randomness, fixed locale and timezone (`TZ=UTC` in the
                  runner).
                - No shared mutable state between tests: no static caches, no order dependence. Each
                  test creates what it needs and the fixture tears it down.
                - Isolated data: a unique org/tenant/user per test, or transaction rollback per test.
                  A test that fails only when run with others is a bug in the test.
                - Network off in unit tests. Flaky means broken — fix or delete, never
                  retry-until-green.

                ## 6. Data builders and fixtures
                - One builder per aggregate (`new TaskBuilder().InLane(Developing).Build()`,
                  `makeUser({ role })`) with sensible defaults, so a test states only what matters.
                - Fixtures own setup **and** teardown. Factory functions over a giant shared JSON
                  blob every test silently depends on.
                - Test data is obviously fake (`"org-a"`, `"alice@example.com"`): never real
                  credentials, never production-shaped PII.

                ## 7. Per-ecosystem conventions
                | Ecosystem | Convention |
                |---|---|
                | .NET — xUnit/NUnit + FluentAssertions | `[Fact]` / `[Theory]` + `[InlineData]`; `Should().BeEquivalentTo`; `WebApplicationFactory` + Testcontainers for integration; `TimeProvider` for time |
                | JS/TS — Jest/Vitest + Testing Library | `describe` / `it`; query by role/label; `userEvent` over `fireEvent`; `msw` for HTTP; fake timers; no `act()` warnings left behind |
                | Playwright | locators by role; `expect(locator).toBeVisible()` auto-waits (never `waitForTimeout`); one `describe` per journey; trace on failure |
                | Python — pytest | plain `assert`; fixtures + `conftest.py`; `parametrize`; `monkeypatch` / `freezegun`; `respx`/`responses` for HTTP |
                | Go — `testing` | table-driven tests with `t.Run`; `t.Helper()`; `t.Cleanup`; consumer-side interfaces make fakes trivial; run with `-race` |
                | Kotlin — JUnit 5 / Kotest | `@Test` + `@ParameterizedTest`; MockK sparingly; `runTest` for coroutines; AssertJ or Kotest matchers |
                | Swift — XCTest / Swift Testing | `XCTAssertEqual` with messages; `async` tests; `XCTUnwrap`; expectations at async boundaries; UI tests by accessibility identifier |

                ## Before you call it done
                - [ ] Every new or changed public entry point has a test, including an error path
                - [ ] No sleeps, no real clocks, no real network in unit tests
                - [ ] Nothing the repo owns is mocked; the test fails if the logic is deleted
                - [ ] Names read as specs; AAA is visible; no logic in tests
                - [ ] The suite actually ran green, and the command + result are in the final message

                A later pipeline phase **runs your suite**. A red test, or an endpoint with no test, is
                reported as a failure against *you* — not a follow-up for someone else.
                """,
            DefaultRoles: [
                AgentRole.FrontendDev, AgentRole.BackendDev, AgentRole.FullStackDev,
                AgentRole.GameDev, AgentRole.MobileDev]),

        new(
            Key: "engineering-review",
            Slug: "engineering-review",
            Name: "Review for design and engineering quality",
            Description: "Rubric for reviewing any diff on structure: duplication of existing code, logic in the wrong layer, missing tests, swallowed errors, blocking/N+1 on request paths, hard-coded config, debris, unvalidated entry points. Applies alongside correctness and security.",
            Content: """
                # Review for design and engineering quality

                Apply this to every diff, alongside correctness and security — those still come
                first. It is the review a Staff engineer gives on structure: where code lives, what it
                reuses, and what it leaves behind.

                ## Blocking ([Critical]) — objective, not taste
                - Duplicates logic or a component that already exists in the repo. Name the existing
                  one (`file:symbol`): "we already have `SlugGenerator` in SharedKernel".
                - Business logic placed in a view/component, controller/endpoint, or SQL string
                  instead of the service/domain layer the repo uses.
                - New or changed logic with no test, when the repo has a test framework.
                - A swallowed exception, empty catch, or ignored error result (`catch {}`,
                  `.catch(() => {})`, an unchecked `err`, a discarded `Result`).
                - A blocking call on an async path (`.Result`, `.Wait()`, sync file/HTTP I/O in a
                  handler), an N+1 query, or an unbounded query/list on a request path (no
                  pagination, no limit).
                - A hard-coded secret, connection string, URL, or environment-specific value that
                  belongs in config.
                - Dead code, commented-out code, or debug output (`console.log`, `Console.WriteLine`,
                  `print`, a leftover dump) in the diff.
                - A public entry point (endpoint, server action, handler, CLI command) without input
                  validation at the boundary.
                - A new dependency added when the repo already has an equivalent (a second HTTP
                  client, date library, validation library, UI kit).

                ## Advisory ([Suggestion]) — say it, do not block on it
                - Naming that hides intent, boolean parameters, abbreviations.
                - A pattern choice you would have made differently (strategy vs switch, class vs
                  function).
                - Over-abstraction: an interface with one implementation, a factory for one type, a
                  base class for three lines.
                - Minor duplication (two or three lines) that is not yet a pattern.
                - Function or file length, member ordering, import grouping.
                - Micro-performance suggestions without a measurement.

                ## How to write it
                Point at the line, name the existing helper/pattern/layer that should have been used,
                and give the replacement. "Move this to `OrderService.Cancel` next to the other state
                transitions; the endpoint should only map the request" is actionable; "this doesn't
                feel right" is not.

                Taste is always [Suggestion]. Do not bounce a task over a preference — a blocked merge
                costs a full review cycle, and the human decides style where the repo has not. When
                you block, the [Critical] must be one of the bullets above or a correctness/security
                finding, stated as such.
                """,
            DefaultRoles: [AgentRole.CodeReviewer]),
    ];

    /// <summary>Rows for the built-ins a project doesn't have yet.</summary>
    public static List<AgentSkill> CreateMissing(
        Ulid orgId, Ulid projectId, IReadOnlyCollection<string> existingBuiltInKeys)
        => BuiltIns
            .Where(spec => !existingBuiltInKeys.Contains(spec.Key))
            .Select(spec => new AgentSkill
            {
                Id = Ulid.NewUlid(),
                OrgId = orgId,
                ProjectId = projectId,
                Slug = spec.Slug,
                Name = spec.Name,
                Description = spec.Description,
                Content = spec.Content,
                IsEnabled = true,
                IsBuiltIn = true,
                BuiltInKey = spec.Key,
            })
            .ToList();

    /// <summary>
    /// Brings built-in rows nobody edited up to the current spec text. A skill is a database
    /// column written at seed time, so an improvement to a built-in's playbook reached only
    /// projects created afterwards — every existing project kept teaching its agents the old
    /// text forever. A row a human touched (<see cref="AgentSkill.UpdatedByUserId"/> set) is
    /// theirs and is never overwritten. Returns how many rows changed.
    /// </summary>
    public static int RefreshUnedited(IEnumerable<AgentSkill> builtInRows)
    {
        var specs = BuiltIns.ToDictionary(s => s.Key);
        var changed = 0;
        foreach (var skill in builtInRows)
        {
            if (skill.BuiltInKey is null || skill.UpdatedByUserId is not null)
                continue;
            if (!specs.TryGetValue(skill.BuiltInKey, out var spec))
                continue;
            if (skill.Name == spec.Name && skill.Description == spec.Description
                && skill.Content == spec.Content)
                continue;

            skill.Name = spec.Name;
            skill.Description = spec.Description;
            skill.Content = spec.Content;
            changed++;
        }
        return changed;
    }

    /// <summary>Attaches freshly seeded built-ins to the roles they are meant for. Only
    /// touches agents that don't already carry the skill.</summary>
    public static int AssignDefaults(IEnumerable<AgentSkill> seeded, IEnumerable<ProjectAgent> agents)
    {
        var byKey = seeded.Where(s => s.BuiltInKey is not null).ToDictionary(s => s.BuiltInKey!);
        var changed = 0;
        foreach (var agent in agents)
        {
            foreach (var spec in BuiltIns)
            {
                if (!spec.DefaultRoles.Contains(agent.Role) || !byKey.TryGetValue(spec.Key, out var skill))
                    continue;
                var guid = skill.Id.ToGuid();
                if (agent.SkillIds.Contains(guid)) continue;
                // Reassign rather than mutate: EF's array change detection compares the
                // reference/snapshot, so an in-place Add on a tracked list is missed.
                agent.SkillIds = [.. agent.SkillIds, guid];
                changed++;
            }
        }
        return changed;
    }
}
