import { useLocale } from "use-intl";
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { routing } from "@/i18n/routing";
import { api, invalidateApiCache } from "@/lib/api";
import type { MemberPreferences } from "@/lib/types";

interface UserPreferencesContextValue {
  /** Simple mode by default — technical surfaces (repos, deploys, models) opt in. */
  showTechnicalDetails: boolean;
  setShowTechnicalDetails: (next: boolean) => Promise<void>;
  /** True once the product tour has been finished or skipped. Gates the one-time
   *  guided tour that runs on the member's first Ready project. */
  onboardingTourSeen: boolean;
  setOnboardingTourSeen: (next: boolean) => Promise<void>;
  /** The saved interface language, or null when the member never picked one. */
  locale: string | null;
  setLocale: (next: string) => Promise<void>;
}

const UserPreferencesContext = createContext<UserPreferencesContextValue>({
  showTechnicalDetails: false,
  setShowTechnicalDetails: async () => {},
  onboardingTourSeen: false,
  setOnboardingTourSeen: async () => {},
  locale: null,
  setLocale: async () => {},
});

/** Auth-agnostic (the app passes Clerk's getToken). Optimistic
 * updates: the toggle flips immediately and reverts if the API call fails. */
export function UserPreferencesProvider({
  initial,
  tokenFactory,
  children,
}: {
  initial: MemberPreferences | null;
  /** Token for the API calls. The local UI passes an empty one: the session cookie authenticates. */
  tokenFactory: () => Promise<string | null>;
  children: React.ReactNode;
}) {
  const [showTechnicalDetails, setShown] = useState(
    initial?.showTechnicalDetails ?? false
  );
  const [onboardingTourSeen, setTourSeen] = useState(
    initial?.onboardingTourSeen ?? false
  );
  /**
   * One-time adoption for members who picked their language before it was ever stored.
   * Without it the stored preference only starts working for people who happen to open the
   * switcher again, and the symptom this exists to fix (a Portuguese interface whose agents
   * answer in English) survives for everyone else.
   *
   * Scoped tightly on purpose: only when nothing is stored yet, and only for a NON-default
   * interface language. Someone browsing in English has expressed no preference, and pinning
   * "en" for them would override the Accept-Language they may later change. The adopted value
   * seeds the state directly; the effect below only persists it.
   */
  const activeLocale = useLocale();
  const adoptLocale =
    initial && !initial.locale && activeLocale && activeLocale !== routing.defaultLocale
      ? activeLocale
      : null;
  const [locale, setLocaleValue] = useState(initial?.locale ?? adoptLocale);

  const setShowTechnicalDetails = useCallback(
    async (next: boolean) => {
      const previous = showTechnicalDetails;
      setShown(next);
      try {
        const token = (await tokenFactory()) ?? "";
        await api.org.updateMyPreferences({ showTechnicalDetails: next }, token);
        invalidateApiCache();
      } catch {
        setShown(previous);
      }
    },
    [showTechnicalDetails, tokenFactory]
  );

  /**
   * Unlike the technical-details toggle this does NOT revert on failure. The local flip is
   * what stops the overlay, and re-showing a tour someone just dismissed because a write
   * failed is worse than losing the flag until the next reload.
   */
  const setOnboardingTourSeen = useCallback(
    async (next: boolean) => {
      setTourSeen(next);
      try {
        const token = (await tokenFactory()) ?? "";
        await api.org.updateMyPreferences({ onboardingTourSeen: next }, token);
        invalidateApiCache();
      } catch {
        // Best effort — see above.
      }
    },
    [tokenFactory]
  );

  /**
   * Persists the language the member picked. Fire-and-forget and deliberately without the
   * revert-on-failure the technical-details toggle does: the interface has already switched
   * by the time this runs, so restoring the old value would only leave the stored preference
   * disagreeing with what the user is looking at. Losing the write costs the durable half of
   * the setting, not this session — Accept-Language still carries the language until reload.
   *
   * What it buys: the API prefers this over the header when telling agents which language to
   * answer in, so a client whose header cannot be trusted still gets replies in the right one.
   */
  const persistLocale = useCallback(
    async (next: string) => {
      try {
        const token = (await tokenFactory()) ?? "";
        await api.org.updateMyPreferences({ locale: next }, token);
        invalidateApiCache();
      } catch {
        // Best effort — see above.
      }
    },
    [tokenFactory]
  );

  const setLocale = useCallback(
    async (next: string) => {
      setLocaleValue(next);
      await persistLocale(next);
    },
    [persistLocale]
  );

  const adopted = useRef(false);
  useEffect(() => {
    if (adopted.current || !adoptLocale) return;
    adopted.current = true;
    void persistLocale(adoptLocale);
  }, [adoptLocale, persistLocale]);

  const value = useMemo(
    () => ({
      showTechnicalDetails,
      setShowTechnicalDetails,
      onboardingTourSeen,
      setOnboardingTourSeen,
      locale,
      setLocale,
    }),
    [
      showTechnicalDetails,
      setShowTechnicalDetails,
      onboardingTourSeen,
      setOnboardingTourSeen,
      locale,
      setLocale,
    ]
  );

  return (
    <UserPreferencesContext.Provider value={value}>
      {children}
    </UserPreferencesContext.Provider>
  );
}

export function useUserPreferencesContext() {
  return useContext(UserPreferencesContext);
}
