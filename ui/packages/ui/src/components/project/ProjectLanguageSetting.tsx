import { useState } from "react";
import { useTranslations } from "use-intl";
import { Select } from "@/components/ui";
import { usePermissions } from "@/hooks/use-permissions";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import type { Project, ProjectSettings } from "@/lib/types";

/** Mirrors AgentLanguage.Names on the API — the languages agents can work in. */
const AGENT_LANGUAGES = ["en", "es", "pt", "fr", "de"] as const;

/** The value the select shows: the stored language, or AUTOMATIC when the project has none.
 *
 * It used to fall back to the CURRENT UI LOCALE, which made the page state a language the
 * project had never been given. Two things then went wrong together: the server's fallback is
 * the locale the browser SENDS (Accept-Language), not the one the dashboard is displayed in,
 * so a user reading an English UI from a pt-BR browser was told "English" and answered in
 * Portuguese — and picking "English" to fix it fired no change event, because English was
 * already selected. An unset setting must look unset. */
export function selectedAgentLanguage(stored: string | null | undefined): string {
  return stored && AGENT_LANGUAGES.includes(stored as never) ? stored : AUTOMATIC;
}

/** Sentinel for "no language stored" — saved as null, which is what makes the server fall
 * back to each requester's own locale. */
const AUTOMATIC = "";

/**
 * The project's working language: what its agents reply, narrate their reasoning and write
 * documents in. Independent of the navbar language switcher, which only changes UI text.
 * New projects start with the creator's interface language; projects created before the
 * setting existed have none stored and follow each requester's own locale until a language
 * is saved here — which the select says outright rather than implying a language.
 *
 * Saves instantly on change (the PATCH settings payload is a full replacement, so the
 * current settings are spread), optimistic with revert on failure — the same contract as
 * PipelineAutomationSettings.
 */
function ProjectLanguageSetting({ project }: { project: Project }) {
  const t = useTranslations("dashboard");
  const tc = useTranslations("common");
  const withToken = useClientApi();
  const { has } = usePermissions();
  const canManage = has("ProjectsManage");

  const [settings, setSettings] = useState<ProjectSettings>(
    project.settings ?? ({} as ProjectSettings)
  );
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const effective = selectedAgentLanguage(settings.language);

  async function handleChange(value: string) {
    const previous = settings;
    // AUTOMATIC saves null — the explicit "no project language" state, not the string "".
    const next = { ...settings, language: value === AUTOMATIC ? null : value };
    setSettings(next); // optimistic
    setSaving(true);
    setError(null);
    try {
      await withToken((token) =>
        api.projects.update(project.id, { settings: next }, token)
      );
    } catch {
      setSettings(previous); // revert
      setError(t("settingsPage.language.saveError"));
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="flex items-start justify-between gap-4">
      <div className="min-w-0">
        <p className="text-sm text-gray-300">{t("settingsPage.language.label")}</p>
        <p className="mt-0.5 text-xs text-gray-400">
          {t("settingsPage.language.description")}
        </p>
        {effective === AUTOMATIC && (
          <p className="mt-1 text-xs text-amber-300/90">
            {t("settingsPage.language.automaticWarning")}
          </p>
        )}
        {error && <p className="mt-1 text-xs text-red-400">{error}</p>}
      </div>
      <div className="shrink-0 pt-0.5">
        <Select
          size="sm"
          aria-label={t("settingsPage.language.label")}
          value={effective}
          disabled={!canManage || saving}
          onChange={(e) => handleChange(e.target.value)}
        >
          <option value={AUTOMATIC}>{t("settingsPage.language.automatic")}</option>
          {AGENT_LANGUAGES.map((code) => (
            <option key={code} value={code}>
              {tc(`locale.${code}`)}
            </option>
          ))}
        </Select>
      </div>
    </div>
  );
}

export { ProjectLanguageSetting };
