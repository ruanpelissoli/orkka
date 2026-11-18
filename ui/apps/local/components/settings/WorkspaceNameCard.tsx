import { useState } from "react";
import { useTranslations } from "use-intl";
import { Button, GlassCard, Input } from "@/components/ui";
import { usePermissions } from "@/hooks/use-permissions";
import { useClientApi } from "@/hooks/use-client-api";
import { api, extractErrorMessage, invalidateApiCache } from "@/lib/api";
import { refreshData } from "@/lib/data-refresh";

const MAX_LENGTH = 200;

/** Renames the current workspace (one per job) — the name the workspace switcher shows. */
export function WorkspaceNameCard() {
  const t = useTranslations("dashboard.workspaceSettings");
  const { me } = usePermissions();
  const withToken = useClientApi();

  const initial = me?.orgName ?? "";
  const [name, setName] = useState(initial);
  const [saved, setSaved] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  const trimmed = name.trim();
  const canSave = !busy && trimmed.length > 0 && trimmed.length <= MAX_LENGTH && trimmed !== saved;

  async function handleSave() {
    if (!canSave) return;
    setBusy(true);
    setError(null);
    setDone(false);
    try {
      const profile = await withToken((token) => api.org.rename(trimmed, token));
      // org.me and the workspace list are cached and carry the name.
      invalidateApiCache();
      setName(profile.name);
      setSaved(profile.name);
      setDone(true);
      refreshData();
    } catch (err) {
      setError(extractErrorMessage(err, t("errorGeneric")));
    } finally {
      setBusy(false);
    }
  }

  return (
    <GlassCard padding="md">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-end">
        <div className="min-w-0 flex-1">
          <Input
            label={t("nameLabel")}
            value={name}
            maxLength={MAX_LENGTH}
            disabled={busy}
            onChange={(e) => {
              setName(e.target.value);
              setDone(false);
            }}
            hint={t("nameHint")}
          />
        </div>
        <Button onClick={() => void handleSave()} disabled={!canSave} className="shrink-0">
          {busy ? t("saving") : t("save")}
        </Button>
      </div>
      {done && <p className="mt-2 text-xs text-emerald-400">{t("saved")}</p>}
      {error && <p className="mt-2 text-xs text-red-400">{error}</p>}
    </GlassCard>
  );
}
