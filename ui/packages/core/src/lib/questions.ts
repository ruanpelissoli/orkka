/**
 * Parsing for the agent's structured ```questions blocks and for the replies they produce.
 *
 * Pure and renderer-free so every client answers identically: the web cards in
 * `packages/ui/src/components/chat/QuestionBlocks.tsx` and the React Native cards in
 * `orkka-mobile/src/components/chat/QuestionsCard.tsx` share this module. It lives in
 * `core` rather than next to the web card because `packages/ui` is not on the mobile
 * resolver chain — a mobile copy of this logic drifted into rendering raw JSON in chat.
 */

export interface AgentQuestion {
  question: string;
  options: string[];
  allowCustom: boolean;
  multiSelect: boolean;
  /** The option the agent would pick absent guidance — rendered as a chip, and what
   * autopilot answers with when nobody is around. */
  recommended?: string | null;
}

export const QUESTIONS_MARKER = "```questions";

/**
 * True when `marker` at `idx` opens a fence: at a line start, with nothing but spaces after
 * it on that line. A raw substring match also fired on the marker INSIDE a JSON string — a
 * knowledge base quoting "via ```design blocks" turned a documents reply into a
 * "Creating designs…" card — and "```document" would match "```documents".
 */
export function opensFence(content: string, marker: string, idx: number): boolean {
  if (idx > 0 && content[idx - 1] !== "\n") return false;
  let after = idx + marker.length;
  while (after < content.length && (content[after] === " " || content[after] === "\t")) after++;
  return after >= content.length || content[after] === "\n" || content[after] === "\r";
}

/**
 * Locates fenced JSON blocks for the given marker (```document, ```questions). Their
 * JSON payload may contain markdown with its own ``` fences, so a non-greedy regex
 * would cut the block at the first inner fence and leak raw JSON into the chat.
 * Instead each subsequent fence is tried as the terminator until the payload parses
 * as JSON. A block with no valid terminator yet (mid-stream) is reported as
 * in-progress from its marker onward.
 */
export function findJsonBlocks(content: string, marker: string): {
  ranges: Array<{ start: number; end: number; payload: unknown }>;
  inProgressStart: number | null;
} {
  const ranges: Array<{ start: number; end: number; payload: unknown }> = [];
  let inProgressStart: number | null = null;
  let idx = 0;

  while ((idx = content.indexOf(marker, idx)) !== -1) {
    if (!opensFence(content, marker, idx)) {
      idx += marker.length;
      continue;
    }
    const newline = content.indexOf("\n", idx + marker.length);
    if (newline === -1) {
      inProgressStart = idx;
      break;
    }
    const contentStart = newline + 1;
    let searchFrom = contentStart;
    let end = -1;
    let payload: unknown;
    for (;;) {
      const fence = content.indexOf("```", searchFrom);
      if (fence === -1) break;
      const candidate = content.slice(contentStart, fence).trim();
      if (candidate.startsWith("{")) {
        try {
          payload = JSON.parse(candidate);
          end = fence + 3;
          break;
        } catch {
          // Inner fence — extend the candidate to the next one.
        }
      }
      searchFrom = fence + 3;
    }
    if (end === -1) {
      inProgressStart = idx;
      break;
    }
    ranges.push({ start: idx, end, payload });
    idx = end;
  }

  return { ranges, inProgressStart };
}

/** Validates a ```questions payload into a renderable shape; null = fall back to raw text. */
export function parseQuestionsPayload(payload: unknown): AgentQuestion[] | null {
  if (payload === null || typeof payload !== "object") return null;
  const raw = (payload as { questions?: unknown }).questions;
  if (!Array.isArray(raw) || raw.length === 0) return null;

  const questions: AgentQuestion[] = [];
  for (const item of raw) {
    if (item === null || typeof item !== "object") return null;
    const q = item as Record<string, unknown>;
    if (typeof q.question !== "string" || !q.question.trim()) return null;
    const options = Array.isArray(q.options)
      ? q.options.filter((o): o is string => typeof o === "string" && o.trim().length > 0)
      : [];
    questions.push({
      question: q.question.trim(),
      options,
      // No options means the user can only type — force custom input on
      allowCustom: q.allowCustom !== false || options.length === 0,
      multiSelect: q.multiSelect === true,
      recommended:
        typeof q.recommended === "string" &&
        options.some((o) => o.toLowerCase() === (q.recommended as string).trim().toLowerCase())
          ? options.find((o) => o.toLowerCase() === (q.recommended as string).trim().toLowerCase())
          : null,
    });
  }
  return questions;
}

export interface QuestionAnswer {
  question: string;
  answer: string;
}

/**
 * The reply a questions card submits on the user's behalf: one `**question**` line followed
 * by the chosen answers, blank-line separated. It is stored and sent to the agent as
 * markdown, so the agent reads it like any other turn; the chat surfaces recognise the
 * shape (parseQuestionAnswers) and render it structured instead of showing raw `**`.
 */
export function formatQuestionAnswers(answers: QuestionAnswer[]): string {
  return answers.map((qa) => `**${qa.question}**\n${qa.answer}`).join("\n\n");
}

/**
 * Inverse of formatQuestionAnswers. Returns null unless the whole message has that exact
 * shape, so hand-typed messages (even ones using `**bold**`) keep rendering as plain text.
 */
export function parseQuestionAnswers(content: string): QuestionAnswer[] | null {
  const blocks = content.trim().split(/\n\s*\n/);
  const answers: QuestionAnswer[] = [];
  for (const block of blocks) {
    const match = /^\*\*(.+?)\*\*\n([\s\S]+)$/.exec(block.trim());
    if (!match) return null;
    const question = match[1].trim();
    const answer = match[2].trim();
    if (!question || !answer || answer.includes("**")) return null;
    answers.push({ question, answer });
  }
  return answers.length > 0 ? answers : null;
}

/**
 * Splits agent content into its leading prose and every valid ```questions block in it.
 * Returns null when the message carries no answerable questions, so the caller renders
 * the content as ordinary markdown.
 */
export function extractQuestions(
  content: string
): { intro: string; questions: AgentQuestion[] } | null {
  const { ranges } = findJsonBlocks(content, QUESTIONS_MARKER);
  if (ranges.length === 0) return null;
  const questions = ranges
    .map((r) => parseQuestionsPayload(r.payload))
    .filter((q): q is AgentQuestion[] => q !== null)
    .flat();
  if (questions.length === 0) return null;
  return { intro: content.slice(0, ranges[0].start).trim(), questions };
}

export type QuestionSegment =
  | { type: "text"; content: string }
  | { type: "questions"; questions: AgentQuestion[] }
  | { type: "questions-in-progress" };

/**
 * Splits agent content into prose and ```questions segments, in order. Unlike
 * `extractQuestions` it keeps the text BETWEEN and AFTER blocks, which is what a streaming
 * surface (the brief chat, the mobile chats) needs; a block whose fence has not arrived yet
 * becomes a trailing `questions-in-progress` so the raw JSON is never shown mid-stream.
 */
export function splitQuestionSegments(content: string): QuestionSegment[] {
  const { ranges, inProgressStart } = findJsonBlocks(content, QUESTIONS_MARKER);
  const segments: QuestionSegment[] = [];
  let cursor = 0;

  for (const range of ranges) {
    const questions = parseQuestionsPayload(range.payload);
    if (!questions) continue;
    if (range.start > cursor) {
      const text = content.slice(cursor, range.start).trim();
      if (text) segments.push({ type: "text", content: text });
    }
    segments.push({ type: "questions", questions });
    cursor = range.end;
  }

  const tailEnd = inProgressStart ?? content.length;
  if (tailEnd > cursor) {
    const text = content.slice(cursor, tailEnd).trim();
    if (text) segments.push({ type: "text", content: text });
  }
  if (inProgressStart !== null) segments.push({ type: "questions-in-progress" });

  return segments.length > 0 ? segments : [{ type: "text", content }];
}
