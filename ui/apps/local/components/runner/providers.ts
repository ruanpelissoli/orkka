/**
 * Runner provider helpers — thin re-exports of the shared module so the runner page and the org roster share one readiness rule with the model
 * picker (a provider is ready only when its CLI is found AND authenticated).
 */
export {
  CLI_PROVIDER_LABELS as PROVIDER_LABELS,
  cliProviderLabel as providerLabel,
  readyProviders,
} from "@/lib/model-billing";
