/**
 * Starter automations offered on the Automation page.
 *
 * These are UI templates, not seeded rows: nothing exists in the database until the user
 * adds one, and what gets created is an ordinary schedule they can edit or delete. Adding
 * always creates it DISABLED — a starter that began spending money the moment it appeared
 * would be exactly the surprise this page is trying to avoid.
 *
 * Skills are matched by slug against the project's built-ins (SkillSeeder.cs). A renamed or
 * disabled skill just means the part installs with its message only, which still works.
 */
import type { AgentRoleKey } from "@/lib/constants";
import type { ProjectAgent, Skill, UpsertScheduleRequest } from "@/lib/types";

export type StandingKind = "Playbook" | "AutopilotStart" | "AutopilotStop";

export interface TemplatePart {
  kind: StandingKind;
  /** 5-field cron; keep to shapes `humanizeCron` can phrase. */
  cron: string;
  /** Key suffix under `templates.<templateKey>.` holding this part's schedule name. */
  nameKey: string;
  /** Preferred built-in skill, by slug. Optional — the message alone is a valid turn. */
  skillSlug?: string;
  /** Roles that should own the turn, best first. Falls back to the project's default agent. */
  roles?: AgentRoleKey[];
  /** Key suffix under `templates.<templateKey>.` holding the message sent to the agent. */
  messageKey?: string;
  capUsd?: number;
}

export interface AutomationTemplate {
  /** Identity, React key, and the i18n prefix `templates.<key>.`. */
  key: string;
  /** Badge on the card. A start/stop pair advertises itself as an Autopilot window. */
  kind: StandingKind;
  parts: TemplatePart[];
}

export const AUTOMATION_TEMPLATES: readonly AutomationTemplate[] = [
  {
    key: "groomBacklog",
    kind: "Playbook",
    parts: [
      {
        kind: "Playbook",
        cron: "0 9 * * 1",
        nameKey: "scheduleName",
        skillSlug: "groom-backlog",
        roles: ["PM"],
        messageKey: "message",
      },
    ],
  },
  {
    key: "releaseNotes",
    kind: "Playbook",
    parts: [
      {
        kind: "Playbook",
        cron: "0 17 * * 5",
        nameKey: "scheduleName",
        skillSlug: "release-notes",
        roles: ["PM"],
        messageKey: "message",
      },
    ],
  },
  {
    key: "securityAudit",
    kind: "Playbook",
    parts: [
      {
        kind: "Playbook",
        cron: "0 8 * * 3",
        nameKey: "scheduleName",
        skillSlug: "security-audit",
        roles: ["CodeReviewer", "SolutionArchitect", "DevOps"],
        messageKey: "message",
      },
    ],
  },
  {
    key: "dependencyUpdate",
    kind: "Playbook",
    parts: [
      {
        kind: "Playbook",
        cron: "0 8 * * 4",
        nameKey: "scheduleName",
        skillSlug: "dependency-update",
        roles: ["DevOps", "BackendDev", "FullStackDev"],
        messageKey: "message",
      },
    ],
  },
  {
    // The pairing the page keeps recommending, as one click.
    key: "nightShift",
    kind: "AutopilotStart",
    parts: [
      { kind: "AutopilotStart", cron: "0 22 * * *", nameKey: "startName", capUsd: 20 },
      { kind: "AutopilotStop", cron: "0 6 * * *", nameKey: "stopName" },
    ],
  },
] as const;

/** Localized lookup for `templates.<templateKey>.<suffix>`. */
export type TemplateText = (templateKey: string, suffix: string) => string;

export interface ResolvedTemplate {
  /** Bodies to POST, in order. Empty when the template can't be installed. */
  requests: UpsertScheduleRequest[];
  /** Set when a playbook part found no agent to run it — the project has no agents. */
  blocked: boolean;
}

/**
 * Turns a template into create requests bound to this project's agents and skills.
 * Every request is disabled: the user turns it on with the row's toggle once they have
 * read what it does.
 */
export function resolveTemplate(
  template: AutomationTemplate,
  agents: ProjectAgent[],
  skills: Skill[],
  timeZoneId: string,
  text: TemplateText
): ResolvedTemplate {
  const requests: UpsertScheduleRequest[] = [];

  for (const part of template.parts) {
    const name = text(template.key, part.nameKey);
    const message = part.messageKey ? text(template.key, part.messageKey) : null;

    if (part.kind === "Playbook") {
      const agent = pickAgent(agents, part.roles);
      if (!agent) return { requests: [], blocked: true };
      const skill = part.skillSlug
        ? skills.find((s) => s.isEnabled && s.slug === part.skillSlug)
        : undefined;

      requests.push({
        name,
        kind: "Playbook",
        cronExpression: part.cron,
        timeZoneId,
        agentId: agent.id,
        skillId: skill?.id ?? null,
        message,
        isEnabled: false,
      });
      continue;
    }

    requests.push({
      name,
      kind: part.kind,
      cronExpression: part.cron,
      timeZoneId,
      autopilotCapUsd: part.kind === "AutopilotStart" ? (part.capUsd ?? null) : null,
      isEnabled: false,
    });
  }

  return { requests, blocked: false };
}

/** First agent in role preference order, else the project default, else the first one. */
function pickAgent(agents: ProjectAgent[], roles?: AgentRoleKey[]): ProjectAgent | undefined {
  for (const role of roles ?? []) {
    const match = agents.find((a) => a.role === role);
    if (match) return match;
  }
  return agents.find((a) => a.isDefault) ?? agents[0];
}
