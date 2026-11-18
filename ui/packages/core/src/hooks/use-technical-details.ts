import { useUserPreferencesContext } from "@/components/providers/UserPreferencesProvider";

/**
 * User-level simple vs. advanced mode. Default is OFF (simple): technical surfaces —
 * repos, deployments, model pickers, the developer onboarding path — should lead with
 * plain-language alternatives and only surface detail when this is on.
 */
export function useTechnicalDetails() {
  const { showTechnicalDetails, setShowTechnicalDetails } =
    useUserPreferencesContext();
  return { showTechnicalDetails, setShowTechnicalDetails };
}
