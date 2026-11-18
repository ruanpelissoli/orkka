import { useState } from "react";
import { useTranslations } from "use-intl";
import { Select, Toggle } from "@/components/ui";
import { usePermissions } from "@/hooks/use-permissions";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { formatUsd } from "@/lib/utils";
import type { Project, ProjectSettings } from "@/lib/types";

type AutomationField =
  | "autoApproveCodeReview"
  | "autoApprovePlan"
  | "qaEnabled"
  | "manualVerificationEnabled"
  | "sameRepoParallelism"
  | "showAgentToolCalls"
  | "documentationSyncEnabled"
  | "autoArchiveDoneTasks";

const PARALLEL_OPTIONS = [1, 2, 3, 4, 5];

/** Bounds and starting value of the task runtime limit — mirrors ProjectSettingsDto. */
const RUNTIME_LIMIT_MIN_MINUTES = 1;
const RUNTIME_LIMIT_MAX_MINUTES = 1440;
const RUNTIME_LIMIT_DEFAULT_MINUTES = 30;

/**
 * Bounds and starting value of the task spend limit in US dollars (`taskSpendLimitUsd`, an
 * estimated list-price spend) — mirrors ProjectSettingsDto.
 */
const SPEND_LIMIT_MIN_USD = 0.1;
const SPEND_LIMIT_MAX_USD = 10_000;
const SPEND_LIMIT_DEFAULT_USD = 5;

/** Clamps to the accepted range and rounds to whole cents. */
function clampUsd(value: number) {
  const cents = Math.round(value * 100) / 100;
  return Math.min(SPEND_LIMIT_MAX_USD, Math.max(SPEND_LIMIT_MIN_USD, cents));
}

/**
 * The settings PATCH is a full replacement, so a field the API has retired must not ride along
 * from an older stored settings object — `taskSpendLimitCredits` (thousandths of a dollar) was
 * replaced by `taskSpendLimitUsd`.
 */
function withoutRetiredFields(settings: ProjectSettings): ProjectSettings {
  const rest: ProjectSettings & { taskSpendLimitCredits?: unknown } = { ...settings };
  delete rest.taskSpendLimitCredits;
  return rest;
}

/** Fallbacks for responses predating newer settings fields. */
const SETTINGS_DEFAULTS: ProjectSettings = {
  maxParallelTasks: 1,
  sameRepoParallelism: false,
  autoApproveCodeReview: false,
  autoApprovePlan: false,
  autoPushOnMerge: false,
  qaEnabled: false,
  qaRunCommand: null,
  manualVerificationEnabled: false,
  showAgentToolCalls: false,
  documentationSyncEnabled: false,
  autoArchiveDoneTasks: false,
  taskRuntimeLimitEnabled: false,
  taskRuntimeLimitMinutes: RUNTIME_LIMIT_DEFAULT_MINUTES,
  taskSpendLimitEnabled: false,
  taskSpendLimitUsd: SPEND_LIMIT_DEFAULT_USD,
};

interface PipelineAutomationSettingsProps {
  project: Project;
}

/**
 * Pipeline automation toggles. Saves instantly on toggle: the PATCH settings
 * payload is a FULL replacement of the settings JSON, so the current settings
 * are spread and only the toggled field overridden. Optimistic UI with revert
 * and an inline error on failure.
 */
function PipelineAutomationSettings({ project }: PipelineAutomationSettingsProps) {
  const t = useTranslations("dashboard");
  const withToken = useClientApi();
  const { has } = usePermissions();
  const canManage = has("ProjectsManage");

  const [settings, setSettings] = useState<ProjectSettings>(() =>
    withoutRetiredFields({ ...SETTINGS_DEFAULTS, ...(project.settings ?? {}) })
  );
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // The minutes box is edited as text so a half-typed value ("1" on the way to "120")
  // is never treated as the number to save. Committed on blur.
  const [limitDraft, setLimitDraft] = useState(
    String(
      project.settings?.taskRuntimeLimitMinutes ?? RUNTIME_LIMIT_DEFAULT_MINUTES
    )
  );
  const [spendDraft, setSpendDraft] = useState(
    String(project.settings?.taskSpendLimitUsd ?? SPEND_LIMIT_DEFAULT_USD)
  );

  async function saveSettings(next: ProjectSettings, previous: ProjectSettings) {
    setSettings(next); // optimistic
    setSaving(true);
    setError(null);
    try {
      await withToken((token) =>
        api.projects.update(project.id, { settings: next }, token)
      );
    } catch {
      setSettings(previous); // revert
      setError(t("pipelineSettings.saveError"));
    } finally {
      setSaving(false);
    }
  }

  async function handleToggle(field: AutomationField, value: boolean) {
    await saveSettings({ ...settings, [field]: value }, settings);
  }

  /** Turning the limit on carries the current minutes value with it, so the first save
   * already holds the number the box is showing rather than whatever was stored before. */
  async function handleRuntimeLimitToggle(value: boolean) {
    const minutes = clampMinutes(
      settings.taskRuntimeLimitMinutes ?? RUNTIME_LIMIT_DEFAULT_MINUTES
    );
    setLimitDraft(String(minutes));
    await saveSettings(
      {
        ...settings,
        taskRuntimeLimitEnabled: value,
        taskRuntimeLimitMinutes: minutes,
      },
      settings
    );
  }

  async function handleRuntimeLimitBlur(value: string) {
    const parsed = Number.parseInt(value.trim(), 10);
    const current =
      settings.taskRuntimeLimitMinutes ?? RUNTIME_LIMIT_DEFAULT_MINUTES;
    // Anything unparseable snaps back to what is stored — the field cannot be left empty,
    // because the limit is on and something has to enforce it.
    const minutes = Number.isFinite(parsed) ? clampMinutes(parsed) : current;
    setLimitDraft(String(minutes));
    if (minutes === current) return;
    await saveSettings({ ...settings, taskRuntimeLimitMinutes: minutes }, settings);
  }

  /** Same shape as the runtime limit: the toggle carries the number the box shows. */
  async function handleSpendLimitToggle(value: boolean) {
    const usd = clampUsd(settings.taskSpendLimitUsd ?? SPEND_LIMIT_DEFAULT_USD);
    setSpendDraft(String(usd));
    await saveSettings(
      { ...settings, taskSpendLimitEnabled: value, taskSpendLimitUsd: usd },
      settings
    );
  }

  async function handleSpendLimitBlur(value: string) {
    const parsed = Number.parseFloat(value.replace(/[^0-9.]/g, ""));
    const current = settings.taskSpendLimitUsd ?? SPEND_LIMIT_DEFAULT_USD;
    const usd = Number.isFinite(parsed) ? clampUsd(parsed) : current;
    setSpendDraft(String(usd));
    if (usd === current) return;
    await saveSettings({ ...settings, taskSpendLimitUsd: usd }, settings);
  }

  async function handleRunCommandBlur(value: string) {
    const command = value.trim() || null;
    if (command === (settings.qaRunCommand ?? null)) return;
    await saveSettings({ ...settings, qaRunCommand: command }, settings);
  }

  const spendLimitBox = (
    <div>
      <label className="text-sm text-gray-300" htmlFor="task-spend-limit">
        {t("pipelineSettings.spendLimitUsdLabel")}
      </label>
      <p className="mt-0.5 text-xs text-gray-400">
        {t("pipelineSettings.spendLimitUsdDescription", {
          min: formatUsd(SPEND_LIMIT_MIN_USD),
          max: formatUsd(SPEND_LIMIT_MAX_USD),
        })}
      </p>
      <input
        id="task-spend-limit"
        type="number"
        inputMode="decimal"
        min={SPEND_LIMIT_MIN_USD}
        max={SPEND_LIMIT_MAX_USD}
        step={0.5}
        value={spendDraft}
        disabled={!canManage || saving}
        onChange={(e) => setSpendDraft(e.target.value)}
        onBlur={(e) => handleSpendLimitBlur(e.target.value)}
        className="mt-2 w-40 rounded-lg border border-gray-700 bg-gray-900 px-3 py-2 text-sm text-gray-200 focus:border-gray-500 focus:outline-none disabled:opacity-50"
      />
    </div>
  );

  return (
    <div className="space-y-5">
      {/* Concurrency */}
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0">
          <p className="text-sm text-gray-300">{t("pipelineSettings.parallelTasks")}</p>
          <p className="mt-0.5 text-xs text-gray-400">
            {t("pipelineSettings.parallelTasksDescription")}
          </p>
        </div>
        <div className="shrink-0 pt-0.5">
          <Select
            size="sm"
            aria-label={t("pipelineSettings.parallelTasks")}
            value={settings.maxParallelTasks}
            disabled={!canManage || saving}
            onChange={(e) =>
              saveSettings(
                { ...settings, maxParallelTasks: Number(e.target.value) },
                settings
              )
            }
          >
            {PARALLEL_OPTIONS.map((n) => (
              <option key={n} value={n}>
                {n}
              </option>
            ))}
          </Select>
        </div>
      </div>

      <SettingRow
        title={t("pipelineSettings.sameRepoTitle")}
        description={t("pipelineSettings.sameRepoDescription")}
        checked={settings.sameRepoParallelism}
        disabled={!canManage || saving || settings.maxParallelTasks <= 1}
        onChange={(value) => handleToggle("sameRepoParallelism", value)}
      />

      <SettingRow
        title={t("pipelineSettings.autoApprovePlanTitle")}
        description={t("pipelineSettings.autoApprovePlanDescription")}
        checked={settings.autoApprovePlan ?? false}
        disabled={!canManage || saving}
        onChange={(value) => handleToggle("autoApprovePlan", value)}
      />

      <SettingRow
        title={t("pipelineSettings.autoApproveTitle")}
        description={t("pipelineSettings.autoApproveDescription")}
        checked={settings.autoApproveCodeReview}
        disabled={!canManage || saving}
        onChange={(value) => handleToggle("autoApproveCodeReview", value)}
      />

      <SettingRow
        title={t("pipelineSettings.qaTitle")}
        description={t("pipelineSettings.qaDescription")}
        checked={settings.qaEnabled}
        disabled={!canManage || saving}
        onChange={(value) => handleToggle("qaEnabled", value)}
      />

      <SettingRow
        title={t("pipelineSettings.manualVerificationTitle")}
        description={t("pipelineSettings.manualVerificationDescription")}
        checked={settings.manualVerificationEnabled}
        disabled={!canManage || saving}
        onChange={(value) => handleToggle("manualVerificationEnabled", value)}
      />

      <SettingRow
        title={t("pipelineSettings.liveDocsTitle")}
        description={t("pipelineSettings.liveDocsDescription")}
        checked={settings.documentationSyncEnabled}
        disabled={!canManage || saving}
        onChange={(value) => handleToggle("documentationSyncEnabled", value)}
      />

      <SettingRow
        title={t("pipelineSettings.autoArchiveTitle")}
        description={t("pipelineSettings.autoArchiveDescription")}
        checked={settings.autoArchiveDoneTasks ?? false}
        disabled={!canManage || saving}
        onChange={(value) => handleToggle("autoArchiveDoneTasks", value)}
      />

      <SettingRow
        title={t("pipelineSettings.runtimeLimitTitle")}
        description={t("pipelineSettings.runtimeLimitDescription")}
        checked={settings.taskRuntimeLimitEnabled ?? false}
        disabled={!canManage || saving}
        onChange={handleRuntimeLimitToggle}
      />

      {settings.taskRuntimeLimitEnabled && (
        <div>
          <label className="text-sm text-gray-300" htmlFor="task-runtime-limit">
            {t("pipelineSettings.runtimeLimitMinutesLabel")}
          </label>
          <p className="mt-0.5 text-xs text-gray-400">
            {t("pipelineSettings.runtimeLimitMinutesDescription", {
              min: RUNTIME_LIMIT_MIN_MINUTES,
              max: RUNTIME_LIMIT_MAX_MINUTES,
            })}
          </p>
          <input
            id="task-runtime-limit"
            type="number"
            inputMode="numeric"
            min={RUNTIME_LIMIT_MIN_MINUTES}
            max={RUNTIME_LIMIT_MAX_MINUTES}
            value={limitDraft}
            disabled={!canManage || saving}
            onChange={(e) => setLimitDraft(e.target.value)}
            onBlur={(e) => handleRuntimeLimitBlur(e.target.value)}
            className="mt-2 w-32 rounded-lg border border-gray-700 bg-gray-900 px-3 py-2 text-sm text-gray-200 focus:border-gray-500 focus:outline-none disabled:opacity-50"
          />
        </div>
      )}

      <SettingRow
        title={t("pipelineSettings.spendLimitTitle")}
        description={t("pipelineSettings.spendLimitDescription")}
        checked={settings.taskSpendLimitEnabled ?? false}
        disabled={!canManage || saving}
        onChange={handleSpendLimitToggle}
      />

      {settings.taskSpendLimitEnabled && spendLimitBox}

      <SettingRow
        title={t("pipelineSettings.showToolCallsTitle")}
        description={t("pipelineSettings.showToolCallsDescription")}
        checked={settings.showAgentToolCalls}
        disabled={!canManage || saving}
        onChange={(value) => handleToggle("showAgentToolCalls", value)}
      />

      {settings.qaEnabled && (
        <div className="pl-0">
          <label className="text-sm text-gray-300" htmlFor="qa-run-command">
            {t("pipelineSettings.qaRunCommandLabel")}
          </label>
          <p className="mt-0.5 text-xs text-gray-400">
            {t("pipelineSettings.qaRunCommandDescription")}
          </p>
          <input
            id="qa-run-command"
            type="text"
            defaultValue={settings.qaRunCommand ?? ""}
            placeholder="npm run dev"
            disabled={!canManage || saving}
            onBlur={(e) => handleRunCommandBlur(e.target.value)}
            className="mt-2 w-full max-w-md rounded-lg border border-gray-700 bg-gray-900 px-3 py-2 text-sm text-gray-200 placeholder:text-gray-500 focus:border-gray-500 focus:outline-none disabled:opacity-50"
          />
        </div>
      )}

      {error && <p className="text-sm text-red-400">{error}</p>}
    </div>
  );
}

function clampMinutes(value: number) {
  return Math.min(
    RUNTIME_LIMIT_MAX_MINUTES,
    Math.max(RUNTIME_LIMIT_MIN_MINUTES, Math.trunc(value))
  );
}

function SettingRow({
  title,
  description,
  checked,
  disabled,
  onChange,
}: {
  title: string;
  description: string;
  checked: boolean;
  disabled: boolean;
  onChange: (checked: boolean) => void;
}) {
  return (
    <div className="flex items-start justify-between gap-4">
      <div className="min-w-0">
        <p className="text-sm text-gray-300">{title}</p>
        <p className="mt-0.5 text-xs text-gray-400">{description}</p>
      </div>
      <div className="shrink-0 pt-0.5">
        <Toggle checked={checked} onChange={onChange} disabled={disabled} />
      </div>
    </div>
  );
}

export { PipelineAutomationSettings };
