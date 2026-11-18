import {
  forwardRef,
  useCallback,
  useEffect,
  useImperativeHandle,
  useMemo,
  useState,
  type KeyboardEvent,
} from "react";
import { useTranslations } from "use-intl";
import { useClientApi } from "@/hooks/use-client-api";
import { api } from "@/lib/api";
import { cn } from "@/lib/utils";
import type { Skill } from "@/lib/types";

interface SkillSlashMenuProps {
  projectId: string;
  /** Skills assigned to this agent float to the top of the list. */
  agentId: string;
  /** Current composer text — the menu opens while it is a bare `/prefix`. */
  inputValue: string;
  /** Called with the full replacement text (`/slug `) when the user picks a skill. */
  onPick: (text: string) => void;
  disabled?: boolean;
}

export interface SkillSlashMenuHandle {
  /** Give the composer's keydown to the menu first; true = consumed. */
  handleKeyDown: (e: KeyboardEvent<HTMLTextAreaElement>) => boolean;
}

const SLASH_PREFIX = /^\/([a-z0-9-]*)$/i;

/**
 * "/" autocomplete for the agent chat composer: lists the project's enabled skills
 * (playbooks) while the message is a bare slash command, assigned ones first, and completes
 * it to `/slug ` on pick. Purely a convenience — the backend parses `/slug` on its own.
 */
const SkillSlashMenu = forwardRef<SkillSlashMenuHandle, SkillSlashMenuProps>(function SkillSlashMenu(
  { projectId, agentId, inputValue, onPick, disabled },
  ref
) {
  const t = useTranslations("dashboard.skillsPage");
  const withToken = useClientApi();
  const [skills, setSkills] = useState<Skill[] | null>(null);
  const [active, setActive] = useState(0);

  const match = disabled ? null : SLASH_PREFIX.exec(inputValue.trimStart());
  const query = match ? match[1]!.toLowerCase() : null;
  const open = query !== null;

  // Fetch lazily the first time the menu opens; the list is small and rarely changes.
  useEffect(() => {
    if (!open || skills !== null) return;
    let cancelled = false;
    withToken((token) => api.skills.list(projectId, token))
      .then((list) => {
        if (!cancelled) setSkills(list.filter((s) => s.isEnabled));
      })
      .catch(() => {
        if (!cancelled) setSkills([]);
      });
    return () => {
      cancelled = true;
    };
  }, [open, skills, withToken, projectId]);

  const items = useMemo(() => {
    if (!open || !skills) return [];
    const q = query ?? "";
    return skills
      .filter((s) => s.slug.startsWith(q) || s.name.toLowerCase().includes(q))
      .sort((a, b) => {
        const aa = a.assignedAgentIds.includes(agentId) ? 0 : 1;
        const bb = b.assignedAgentIds.includes(agentId) ? 0 : 1;
        return aa - bb || a.slug.localeCompare(b.slug);
      })
      .slice(0, 8);
  }, [open, skills, query, agentId]);

  // Reset the highlight when the filter changes — derived during render, no effect needed.
  const [activeKey, setActiveKey] = useState<string | null>(null);
  if (activeKey !== query) {
    setActiveKey(query);
    setActive(0);
  }

  const pick = useCallback(
    (skill: Skill) => onPick(`/${skill.slug} `),
    [onPick]
  );

  useImperativeHandle(
    ref,
    () => ({
      handleKeyDown: (e) => {
        if (!open || items.length === 0) return false;
        if (e.key === "ArrowDown") {
          e.preventDefault();
          setActive((i) => (i + 1) % items.length);
          return true;
        }
        if (e.key === "ArrowUp") {
          e.preventDefault();
          setActive((i) => (i - 1 + items.length) % items.length);
          return true;
        }
        if (e.key === "Tab" || e.key === "Enter") {
          const skill = items[active];
          if (!skill) return false;
          e.preventDefault();
          pick(skill);
          return true;
        }
        return false;
      },
    }),
    [open, items, active, pick]
  );

  if (!open) return null;

  return (
    <div
      role="listbox"
      data-testid="skill-slash-menu"
      className="absolute bottom-full left-0 z-30 mb-2 w-full max-w-md overflow-hidden rounded-xl border border-white/[0.08] bg-gray-900/95 shadow-[0_8px_30px_rgba(0,0,0,0.5)] backdrop-blur-lg"
    >
      {skills === null ? (
        <div className="px-3 py-2 text-xs text-gray-400">…</div>
      ) : items.length === 0 ? (
        <div className="px-3 py-2 text-xs text-gray-400">{t("slashNoMatch")}</div>
      ) : (
        <ul className="max-h-64 overflow-y-auto py-1">
          {items.map((skill, i) => {
            const assigned = skill.assignedAgentIds.includes(agentId);
            return (
              <li key={skill.id}>
                <button
                  type="button"
                  role="option"
                  aria-selected={i === active}
                  onMouseDown={(e) => {
                    // mousedown, not click: keep focus in the textarea.
                    e.preventDefault();
                    pick(skill);
                  }}
                  onMouseEnter={() => setActive(i)}
                  className={cn(
                    "flex w-full items-start gap-2 px-3 py-2 text-left",
                    i === active ? "bg-white/[0.08]" : "hover:bg-white/[0.04]"
                  )}
                >
                  <span className="mt-0.5 shrink-0 font-mono text-xs text-primary-400">/{skill.slug}</span>
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-xs text-gray-100">
                      {skill.name}
                      {assigned && (
                        <span className="ml-1.5 rounded bg-white/[0.06] px-1 text-label text-gray-400">
                          {t("slashAssigned")}
                        </span>
                      )}
                    </span>
                    <span className="block truncate text-xs text-gray-400">{skill.description}</span>
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
});

export { SkillSlashMenu };
