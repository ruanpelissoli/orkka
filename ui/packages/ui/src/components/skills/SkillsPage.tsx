import { useCallback, useMemo, useState } from "react";
import { useTranslations } from "use-intl";
import { Badge, Button, ConfirmDialog, GlassCard, Input, Markdown, Modal, Textarea, Toggle, useToast } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { usePermissions } from "@/hooks/use-permissions";
import { useClientApi } from "@/hooks/use-client-api";
import { api, extractErrorMessage } from "@/lib/api";
import { cn } from "@/lib/utils";
import type { ProjectAgent, Skill } from "@/lib/types";

interface SkillsPageProps {
  projectId: string;
  initialSkills: Skill[];
  /** For the "assigned to" chips and the assignment picker inside the editor. */
  agents: ProjectAgent[];
}

type EditorState =
  | { mode: "closed" }
  | { mode: "create" }
  | { mode: "edit"; skill: Skill };

const SLUG_RE = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

function slugify(name: string): string {
  return name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 64)
    .replace(/-+$/g, "");
}

/**
 * The project's skill library: SKILL.md-style playbooks the agents follow when work
 * matches, or that users invoke in chat with `/slug`. Built-ins are seeded and can be
 * disabled but not deleted; custom skills are fully editable.
 */
function SkillsPage({ projectId, initialSkills, agents }: SkillsPageProps) {
  const t = useTranslations("dashboard.skillsPage");
  const withToken = useClientApi();
  const { toast } = useToast();
  const { has } = usePermissions();
  const canEdit = has("AgentsConfigure");

  const [skills, setSkills] = useState<Skill[]>(initialSkills);
  const [editor, setEditor] = useState<EditorState>({ mode: "closed" });
  const [pendingDelete, setPendingDelete] = useState<Skill | null>(null);
  const [busy, setBusy] = useState(false);
  const [preview, setPreview] = useState<Skill | null>(null);

  const agentName = useCallback(
    (id: string) => agents.find((a) => a.id === id)?.name ?? "?",
    [agents]
  );

  const upsert = useCallback((skill: Skill) => {
    setSkills((prev) => {
      const idx = prev.findIndex((s) => s.id === skill.id);
      const next = idx === -1 ? [...prev, skill] : prev.map((s) => (s.id === skill.id ? skill : s));
      return next.sort((a, b) => a.name.localeCompare(b.name));
    });
  }, []);

  const toggleEnabled = useCallback(
    async (skill: Skill, enabled: boolean) => {
      // Optimistic; revert on failure.
      upsert({ ...skill, isEnabled: enabled });
      try {
        const saved = await withToken((token) => api.skills.update(projectId, skill.id, { isEnabled: enabled }, token));
        upsert(saved);
      } catch (err) {
        upsert(skill);
        toast(extractErrorMessage(err, t("saveFailed")), "error");
      }
    },
    [withToken, projectId, upsert, toast, t]
  );

  const remove = useCallback(async () => {
    if (!pendingDelete) return;
    setBusy(true);
    try {
      await withToken((token) => api.skills.delete(projectId, pendingDelete.id, token));
      setSkills((prev) => prev.filter((s) => s.id !== pendingDelete.id));
      toast(t("deleted"), "success");
      setPendingDelete(null);
    } catch (err) {
      toast(extractErrorMessage(err, t("saveFailed")), "error");
    } finally {
      setBusy(false);
    }
  }, [pendingDelete, withToken, projectId, toast, t]);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-sm text-gray-400">{t("intro")}</p>
        <RequirePermission permission="AgentsConfigure">
          <Button size="sm" onClick={() => setEditor({ mode: "create" })}>
            {t("newSkill")}
          </Button>
        </RequirePermission>
      </div>

      {skills.length === 0 ? (
        <GlassCard padding="lg">
          <p className="text-center text-sm text-gray-300">{t("empty")}</p>
        </GlassCard>
      ) : (
        <ul className="grid grid-cols-1 gap-3 md:grid-cols-2" data-testid="skills-list">
          {skills.map((skill) => (
            <li key={skill.id}>
              <GlassCard padding="md" className={cn("h-full", !skill.isEnabled && "opacity-70")}>
                <div className="flex items-start justify-between gap-3">
                  <div className="min-w-0">
                    <div className="flex flex-wrap items-center gap-2">
                      <h3 className="font-display text-sm font-semibold text-white">{skill.name}</h3>
                      <span className="font-mono text-xs text-primary-400">/{skill.slug}</span>
                      {skill.isBuiltIn && (
                        <Badge size="sm" variant="info">
                          {t("builtIn")}
                        </Badge>
                      )}
                    </div>
                    <p className="mt-1 text-xs text-gray-400">{skill.description}</p>
                  </div>
                  <div className="shrink-0" title={skill.isEnabled ? t("enabled") : t("disabled")}>
                    <Toggle
                      checked={skill.isEnabled}
                      disabled={!canEdit}
                      onChange={(v) => void toggleEnabled(skill, v)}
                    />
                  </div>
                </div>

                <div className="mt-3 flex flex-wrap items-center gap-1.5">
                  <span className="text-label">{t("assignedTo")}</span>
                  {skill.assignedAgentIds.length === 0 ? (
                    <span className="text-xs text-gray-400">{t("assignedNone")}</span>
                  ) : (
                    skill.assignedAgentIds.map((id) => (
                      <span
                        key={id}
                        className="rounded-md border border-white/[0.06] bg-white/[0.03] px-1.5 py-0.5 text-xs text-gray-200"
                      >
                        {agentName(id)}
                      </span>
                    ))
                  )}
                </div>

                <div className="mt-3 flex items-center gap-2 border-t border-white/[0.04] pt-3">
                  <Button size="sm" variant="ghost" onClick={() => setPreview(skill)}>
                    {t("view")}
                  </Button>
                  <RequirePermission permission="AgentsConfigure">
                    <Button size="sm" variant="ghost" onClick={() => setEditor({ mode: "edit", skill })}>
                      {t("edit")}
                    </Button>
                    {!skill.isBuiltIn && (
                      <Button size="sm" variant="ghost" className="text-red-400" onClick={() => setPendingDelete(skill)}>
                        {t("delete")}
                      </Button>
                    )}
                  </RequirePermission>
                </div>
              </GlassCard>
            </li>
          ))}
        </ul>
      )}

      {/* Read-only preview */}
      <Modal open={preview !== null} onClose={() => setPreview(null)} title={preview?.name}>
        {preview && (
          <div className="space-y-3">
            <p className="text-xs text-gray-400">
              <span className="font-mono text-primary-400">/{preview.slug}</span> · {preview.description}
            </p>
            <Markdown>{preview.content}</Markdown>
          </div>
        )}
      </Modal>

      {/* Create / edit */}
      {editor.mode !== "closed" && (
        <SkillEditor
          projectId={projectId}
          agents={agents}
          skill={editor.mode === "edit" ? editor.skill : null}
          onClose={() => setEditor({ mode: "closed" })}
          onSaved={(skill) => {
            upsert(skill);
            setEditor({ mode: "closed" });
          }}
        />
      )}

      <ConfirmDialog
        open={pendingDelete !== null}
        title={t("deleteTitle")}
        message={t("deleteMessage", { name: pendingDelete?.name ?? "" })}
        confirmLabel={t("delete")}
        cancelLabel={t("cancel")}
        danger
        pending={busy}
        onConfirm={() => void remove()}
        onCancel={() => setPendingDelete(null)}
      />
    </div>
  );
}

function SkillEditor({
  projectId,
  agents,
  skill,
  onClose,
  onSaved,
}: {
  projectId: string;
  agents: ProjectAgent[];
  skill: Skill | null;
  onClose: () => void;
  onSaved: (skill: Skill) => void;
}) {
  const t = useTranslations("dashboard.skillsPage");
  const withToken = useClientApi();
  const { toast } = useToast();

  const [name, setName] = useState(skill?.name ?? "");
  const [slug, setSlug] = useState(skill?.slug ?? "");
  const [slugTouched, setSlugTouched] = useState(Boolean(skill));
  const [description, setDescription] = useState(skill?.description ?? "");
  const [content, setContent] = useState(skill?.content ?? "");
  const [agentIds, setAgentIds] = useState<string[]>(skill?.assignedAgentIds ?? []);
  const [showPreview, setShowPreview] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const effectiveSlug = slugTouched ? slug : slugify(name);
  const slugValid = effectiveSlug.length > 0 && SLUG_RE.test(effectiveSlug);
  const canSave = name.trim().length > 0 && slugValid && !saving;

  const assignmentChanged = useMemo(() => {
    if (!skill) return agentIds.length > 0;
    const a = [...skill.assignedAgentIds].sort().join(",");
    const b = [...agentIds].sort().join(",");
    return a !== b;
  }, [skill, agentIds]);

  const save = useCallback(async () => {
    setSaving(true);
    setError(null);
    try {
      let saved: Skill;
      if (skill) {
        saved = await withToken((token) =>
          api.skills.update(
            projectId,
            skill.id,
            {
              name: name.trim(),
              description: description.trim(),
              content,
              // Built-in slugs stay stable — they're what schedules and muscle memory point at.
              slug: skill.isBuiltIn ? undefined : effectiveSlug,
            },
            token
          )
        );
        if (assignmentChanged) {
          // Assignment lives on the agents; sync each one whose membership changed.
          const before = new Set(skill.assignedAgentIds);
          const after = new Set(agentIds);
          const touched = agents.filter((a) => before.has(a.id) !== after.has(a.id));
          await Promise.all(
            touched.map((a) =>
              withToken((token) => {
                const current = new Set(a.skillIds ?? []);
                if (after.has(a.id)) current.add(skill.id);
                else current.delete(skill.id);
                return api.skills.setForAgent(projectId, a.id, [...current], token);
              })
            )
          );
          saved = { ...saved, assignedAgentIds: agentIds };
        }
      } else {
        saved = await withToken((token) =>
          api.skills.create(
            projectId,
            { name: name.trim(), slug: effectiveSlug, description: description.trim(), content, agentIds },
            token
          )
        );
      }
      toast(t("saved"), "success");
      onSaved(saved);
    } catch (err) {
      setError(extractErrorMessage(err, t("saveFailed")));
    } finally {
      setSaving(false);
    }
  }, [skill, withToken, projectId, name, description, content, effectiveSlug, assignmentChanged, agentIds, agents, toast, t, onSaved]);

  return (
    <Modal open onClose={onClose} title={skill ? t("editTitle") : t("createTitle")} className="max-w-3xl">
      <div className="space-y-4">
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <Input
            label={t("fieldName")}
            value={name}
            onChange={(e) => setName(e.target.value)}
            maxLength={120}
            autoFocus
          />
          <Input
            label={t("fieldSlug")}
            value={effectiveSlug}
            onChange={(e) => {
              setSlugTouched(true);
              setSlug(e.target.value.toLowerCase());
            }}
            disabled={Boolean(skill?.isBuiltIn)}
            maxLength={64}
            hint={t("fieldSlugHint")}
            error={effectiveSlug.length > 0 && !slugValid ? t("fieldSlugInvalid") : undefined}
          />
        </div>
        <Textarea
          label={t("fieldDescription")}
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          rows={2}
          maxLength={400}
          showCount
        />
        <div>
          <div className="mb-1.5 flex items-center justify-between">
            <span className="text-sm font-medium text-gray-300">{t("fieldContent")}</span>
            <button
              type="button"
              className="text-xs text-primary-400 hover:text-primary-300"
              onClick={() => setShowPreview((v) => !v)}
            >
              {showPreview ? t("editMarkdown") : t("previewMarkdown")}
            </button>
          </div>
          {showPreview ? (
            <div className="max-h-80 overflow-y-auto rounded-xl border border-white/[0.06] bg-white/[0.02] p-3">
              <Markdown>{content || "_…_"}</Markdown>
            </div>
          ) : (
            <Textarea
              value={content}
              onChange={(e) => setContent(e.target.value)}
              rows={12}
              maxLength={32000}
              className="font-mono text-xs"
              placeholder={t("fieldContentPlaceholder")}
            />
          )}
        </div>

        {agents.length > 0 && (
          <div>
            <span className="mb-1.5 block text-sm font-medium text-gray-300">{t("fieldAgents")}</span>
            <div className="flex flex-wrap gap-2">
              {agents.map((agent) => {
                const checked = agentIds.includes(agent.id);
                return (
                  <label
                    key={agent.id}
                    className={cn(
                      "inline-flex cursor-pointer items-center gap-2 rounded-lg border px-2.5 py-1.5 text-xs",
                      checked
                        ? "border-primary-500/40 bg-primary-500/10 text-gray-100"
                        : "border-white/[0.06] bg-white/[0.02] text-gray-300 hover:bg-white/[0.05]"
                    )}
                  >
                    <input
                      type="checkbox"
                      className="h-3.5 w-3.5"
                      checked={checked}
                      onChange={(e) =>
                        setAgentIds((prev) =>
                          e.target.checked ? [...prev, agent.id] : prev.filter((id) => id !== agent.id)
                        )
                      }
                    />
                    {agent.name}
                  </label>
                );
              })}
            </div>
          </div>
        )}

        {error && <p className="text-sm text-red-400">{error}</p>}

        <div className="flex justify-end gap-2 pt-2">
          <Button variant="ghost" onClick={onClose} disabled={saving}>
            {t("cancel")}
          </Button>
          <Button onClick={() => void save()} disabled={!canSave}>
            {saving ? t("saving") : t("save")}
          </Button>
        </div>
      </div>
    </Modal>
  );
}

export { SkillsPage };
