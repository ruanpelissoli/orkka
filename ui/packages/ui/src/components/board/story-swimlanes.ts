import type { BoardLaneResponse, BoardStory, TaskSummary } from "@/lib/types";

/** How the board's columns are split into rows. */
export type BoardGroupBy = "none" | "story";

/** The swimlane for cards that belong to no story. */
export const NO_STORY_SWIMLANE = "no-story";

export interface StorySwimlane {
  /** The story's group id, or {@link NO_STORY_SWIMLANE}. */
  id: string;
  /** The story as the board response described it; null for "no story", and for a story whose
   * first card arrived live (SignalR) after the board loaded — the header then falls back to
   * what its cards carry. */
  story: BoardStory | null;
  storyKey: string | null;
  /** Members of the story (all of them, not only the ones the filters leave). */
  memberCount: number;
  /** The board's columns, each holding only this swimlane's cards — in story order. */
  lanes: BoardLaneResponse[];
  /** Cards shown in this swimlane. */
  count: number;
}

const byStoryOrder = (a: TaskSummary, b: TaskSummary) =>
  (a.storyOrder ?? Number.MAX_SAFE_INTEGER) - (b.storyOrder ?? Number.MAX_SAFE_INTEGER);

/**
 * Splits the board's (already filtered) columns into one row per Story, then a row for every
 * card that belongs to no story. Story rows come in the order the board response listed them,
 * then stories only seen in live cards; a story with no card left after filtering is left out.
 * Cross-repo groups are not stories: their cards go to the last row.
 */
export function buildStorySwimlanes(
  lanes: BoardLaneResponse[],
  stories: BoardStory[] | null | undefined,
): StorySwimlane[] {
  const storyOf = (task: TaskSummary) => (task.groupId && task.storyKey ? task.groupId : NO_STORY_SWIMLANE);

  const order: string[] = [];
  const known = new Map<string, BoardStory>();
  for (const story of stories ?? []) {
    known.set(story.groupId, story);
    order.push(story.groupId);
  }
  const fromCards = new Map<string, TaskSummary>();
  for (const lane of lanes) {
    for (const task of lane.tasks) {
      const id = storyOf(task);
      if (id !== NO_STORY_SWIMLANE && !known.has(id) && !fromCards.has(id)) {
        fromCards.set(id, task);
        order.push(id);
      }
    }
  }
  order.push(NO_STORY_SWIMLANE);

  return order
    .map((id) => {
      const rowLanes = lanes.map((lane) => {
        const tasks = lane.tasks.filter((t) => storyOf(t) === id);
        return { ...lane, tasks: id === NO_STORY_SWIMLANE ? tasks : [...tasks].sort(byStoryOrder) };
      });
      const story = known.get(id) ?? null;
      const sample = fromCards.get(id);
      return {
        id,
        story,
        storyKey: story?.key ?? sample?.storyKey ?? null,
        memberCount: story?.memberCount ?? sample?.storyCount ?? 0,
        lanes: rowLanes,
        count: rowLanes.reduce((sum, l) => sum + l.tasks.length, 0),
      };
    })
    .filter((row) => row.count > 0);
}

/** The pull request state a story's header shows: the furthest one across its repositories. */
export function storyPrState(story: BoardStory | null): "none" | "draft" | "ready" | "merged" | "closed" {
  const prs = story?.pullRequests ?? [];
  if (prs.length === 0) return "none";
  if (prs.every((p) => p.state === "merged")) return "merged";
  if (prs.every((p) => p.state === "closed" || p.state === "merged") && prs.some((p) => p.state === "closed"))
    return "closed";
  return story?.readyAt && prs.every((p) => !p.draft) ? "ready" : "draft";
}
