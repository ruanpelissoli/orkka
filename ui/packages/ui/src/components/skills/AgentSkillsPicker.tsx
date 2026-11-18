import { useEffect, useState } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { useClientApi } from "@/hooks/use-client-api";
import { api } from "@/lib/api";
import type { Skill } from "@/lib/types";

interface AgentSkillsPickerProps {
  projectId: string;
  /** The agent's current assignment — pre-checks the boxes. */
  skillIds: string[];
}

/**
 * Checkbox group for the agent config form. Renders `skillIds` inputs plus a
 * `skillIdsPresent` marker so the updateAgent action can tell "none checked" ([] = clear) from
 * "group not rendered" (undefined = unchanged) — the same contract the repo scope uses.
 * Self-fetching so the (per-app, duplicated) config tabs need only drop it in.
 */
function AgentSkillsPicker({ projectId, skillIds }: AgentSkillsPickerProps) {
  const t = useTranslations("dashboard.skillsPage");
  const withToken = useClientApi();
  const [skills, setSkills] = useState<Skill[] | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    withToken((token) => api.skills.list(projectId, token))
      .then((list) => {
        if (!cancelled) setSkills(list);
      })
      .catch(() => {
        if (!cancelled) setFailed(true);
      });
    return () => {
      cancelled = true;
    };
  }, [withToken, projectId]);

  // Older API without the skills endpoint: render nothing rather than a broken section, and
  // emit no marker so the save leaves the assignment untouched.
  if (failed) return null;

  return (
    <div data-testid="agent-skills-picker">
      <div className="mb-1.5 flex items-center justify-between">
        <span className="text-sm font-medium text-gray-300">{t("pickerTitle")}</span>
        <Link href={`/p/${projectId}/skills`} className="text-xs text-primary-400 hover:text-primary-300">
          {t("pickerManage")}
        </Link>
      </div>
      <p className="mb-2 text-xs text-gray-400">{t("pickerHint")}</p>
      {skills === null ? (
        <div className="h-8 animate-pulse rounded bg-white/[0.04]" />
      ) : skills.length === 0 ? (
        <p className="text-xs text-gray-400">{t("pickerEmpty")}</p>
      ) : (
        <>
          <input type="hidden" name="skillIdsPresent" value="1" />
          <div className="flex flex-wrap gap-2">
            {skills.map((skill) => (
              <label
                key={skill.id}
                className="inline-flex cursor-pointer items-center gap-2 rounded-lg border border-white/[0.06] bg-white/[0.02] px-2.5 py-1.5 text-xs text-gray-200 hover:bg-white/[0.05]"
                title={skill.description}
              >
                <input
                  type="checkbox"
                  name="skillIds"
                  value={skill.id}
                  defaultChecked={skillIds.includes(skill.id)}
                  className="h-3.5 w-3.5 rounded border-white/20 bg-transparent"
                />
                <span>{skill.name}</span>
                <span className="font-mono text-gray-400">/{skill.slug}</span>
                {!skill.isEnabled && (
                  <span className="rounded bg-white/[0.06] px-1 text-label text-gray-400">{t("disabled")}</span>
                )}
              </label>
            ))}
          </div>
        </>
      )}
    </div>
  );
}

export { AgentSkillsPicker };
