import { useCallback, useEffect, useState } from "react";
import { useTranslations } from "use-intl";
import { Button, GlassCard, Input } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { useClientApi } from "@/hooks/use-client-api";
import { api } from "@/lib/api";
import type { BrandKit, BrandPalette } from "@/lib/types";

interface BrandKitPanelProps {
  projectId: string;
}

/** Ordered as they read on screen: brand hues, then surfaces, then text, then chrome. */
const SWATCHES: (keyof BrandPalette)[] = [
  "primary",
  "primaryHover",
  "secondary",
  "accent",
  "background",
  "surface",
  "text",
  "textMuted",
  "border",
];

const HEX = /^#[0-9a-fA-F]{6}$/;

/**
 * The project's decided palette, and the one place to correct it.
 *
 * Correcting it here is the reason the kit is persisted at all: the same values reach the app's
 * stylesheet and every asset the Designer makes, so a wrong guess is fixed once instead of
 * being argued with on each new logo.
 */
export function BrandKitPanel({ projectId }: BrandKitPanelProps) {
  const t = useTranslations("dashboard");
  const withToken = useClientApi();

  const [kit, setKit] = useState<BrandKit | null>(null);
  const [draft, setDraft] = useState<BrandKit | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await withToken((token) => api.brand.get(projectId, token));
        if (!cancelled) setKit(result);
      } catch {
        // A brand kit is supporting detail on this page — its absence renders as nothing.
      } finally {
        if (!cancelled) setLoaded(true);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [withToken, projectId]);

  const save = useCallback(async () => {
    if (!draft) return;

    const invalid = SWATCHES.find((key) => !HEX.test(draft.palette[key]));
    if (invalid) {
      setError(t("brandPanel.invalidHex", { field: t(`brandKit.swatches.${invalid}`) }));
      return;
    }

    setBusy(true);
    setError(null);
    try {
      const saved = await withToken((token) =>
        api.brand.update(
          projectId,
          {
            palette: draft.palette,
            displayFont: draft.displayFont,
            bodyFont: draft.bodyFont,
            tone: draft.tone,
          },
          token
        )
      );
      setKit(saved);
      setDraft(null);
    } catch {
      setError(t("brandPanel.saveFailed"));
    } finally {
      setBusy(false);
    }
  }, [draft, withToken, projectId, t]);

  // Nothing to show until the fetch settles, and nothing to show for a project that predates
  // brand kits — an empty swatch grid would imply a palette that does not exist.
  if (!loaded || !kit) return null;

  const shown = draft ?? kit;

  return (
    <GlassCard className="p-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h3 className="font-display text-base font-bold text-white">
            {t("brandPanel.title")}
          </h3>
          <p className="mt-1 text-xs text-gray-400">
            {t(`brandPanel.source.${kit.source}`)}
          </p>
        </div>

        <RequirePermission permission="DesignReview">
          {draft === null ? (
            <Button size="sm" variant="ghost" onClick={() => setDraft(kit)}>
              {t("brandPanel.edit")}
            </Button>
          ) : (
            <div className="flex gap-2">
              <Button size="sm" onClick={save} disabled={busy}>
                {t("brandPanel.save")}
              </Button>
              <Button
                size="sm"
                variant="ghost"
                onClick={() => {
                  setDraft(null);
                  setError(null);
                }}
                disabled={busy}
              >
                {t("brandPanel.cancel")}
              </Button>
            </div>
          )}
        </RequirePermission>
      </div>

      {shown.tone && <p className="mt-2 text-sm italic text-gray-400">{shown.tone}</p>}

      {error && <p className="mt-3 text-xs text-amber-400">{error}</p>}

      <div className="mt-4 grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
        {SWATCHES.map((key) => (
          <div key={key} className="min-w-0">
            <div
              className="h-10 w-full rounded-md border border-white/[0.12]"
              style={{ backgroundColor: shown.palette[key] }}
            />
            <p className="mt-1 truncate text-label">
              {t(`brandKit.swatches.${key}`)}
            </p>
            {draft === null ? (
              <p className="font-mono text-xs text-gray-400">{shown.palette[key]}</p>
            ) : (
              <Input
                value={draft.palette[key]}
                onChange={(e) =>
                  setDraft({
                    ...draft,
                    palette: { ...draft.palette, [key]: e.target.value },
                  })
                }
                maxLength={7}
                className="mt-0.5 text-xs"
              />
            )}
          </div>
        ))}
      </div>

      {(shown.displayFont || shown.bodyFont || draft !== null) && (
        <div className="mt-4 grid grid-cols-1 gap-3 sm:grid-cols-2">
          <FontField
            label={t("brandPanel.displayFont")}
            value={shown.displayFont}
            editing={draft !== null}
            onChange={(v) => draft && setDraft({ ...draft, displayFont: v })}
          />
          <FontField
            label={t("brandPanel.bodyFont")}
            value={shown.bodyFont}
            editing={draft !== null}
            onChange={(v) => draft && setDraft({ ...draft, bodyFont: v })}
          />
        </div>
      )}
    </GlassCard>
  );
}

function FontField({
  label,
  value,
  editing,
  onChange,
}: {
  label: string;
  value: string | null;
  editing: boolean;
  onChange: (value: string) => void;
}) {
  return (
    <div>
      <label className="mb-1 block text-xs text-gray-400">{label}</label>
      {editing ? (
        <Input value={value ?? ""} onChange={(e) => onChange(e.target.value)} maxLength={200} />
      ) : (
        <p className="text-sm text-gray-300">{value ?? "—"}</p>
      )}
    </div>
  );
}
