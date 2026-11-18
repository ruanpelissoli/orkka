/**
 * The plain-language glossary. Every key has a `term` and a `definition` string in
 * `common.json` under `glossary`, in every locale — the parity test pins that.
 *
 * Add a key here when a piece of jargon has to stay in the UI (a developer needs the
 * precise word) but a non-technical member will meet it: wrap it in `<Term k="…">` in
 * JSX, or tag it `<key>…</key>` inside a message and render that message with
 * `t.rich(key, glossaryTags)`.
 *
 * Keep definitions to one or two short sentences a person who has never written code
 * can read — what the thing is, and what it means for them in Orkka.
 */
export const GLOSSARY_KEYS = [
  "acceptanceCriteria",
  "agent",
  // Every duration Orkka reports is agent time, never wall-clock — the distinction the whole
  // board strip rests on, and the one a non-technical member will not guess.
  "agentTime",
  "apiKey",
  "autopilot",
  "backlog",
  "branch",
  "ci",
  "codeReview",
  "commit",
  "deploy",
  "diff",
  "github",
  "lane",
  "mcp",
  "merge",
  "milestone",
  "pipeline",
  "plan",
  "pr",
  "qa",
  "repo",
  "roadmap",
  "runner",
  "stack",
  "tokens",
  "worktree",
  // Board lanes — what happens to a task while it sits in each column.
  "laneDrafting",
  "laneReady",
  "lanePlanning",
  "lanePlanReady",
  "lanePendingQuestions",
  "laneDeveloping",
  "laneTesting",
  "laneCodeReview",
  "laneDone",
] as const;

export type GlossaryKey = (typeof GLOSSARY_KEYS)[number];

/** Lane key (as in `LANES` / `LANE_KEYS`) → the glossary entry explaining that column. */
export const LANE_GLOSSARY: Partial<Record<string, GlossaryKey>> = {
  Drafting: "laneDrafting",
  Ready: "laneReady",
  Planning: "lanePlanning",
  PlanReady: "lanePlanReady",
  PendingQuestions: "lanePendingQuestions",
  Developing: "laneDeveloping",
  Testing: "laneTesting",
  CodeReview: "laneCodeReview",
  Done: "laneDone",
};

export function isGlossaryKey(value: string): value is GlossaryKey {
  return (GLOSSARY_KEYS as readonly string[]).includes(value);
}
