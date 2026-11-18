import { useTranslations } from "use-intl";
import { Button } from "@/components/ui";
import { OrcaError } from "@/components/animations";

/**
 * A page whose data could not load. Same visuals as the route error boundary
 * (app/[locale]/error.tsx); "Try again" refetches instead of remounting.
 */
export function PageError({
  error,
  onRetry,
  fullScreen = false,
}: {
  error?: unknown;
  onRetry?: () => void;
  fullScreen?: boolean;
}) {
  const t = useTranslations("dashboard");
  const message = error instanceof Error && error.message ? error.message : null;
  const digest =
    error && typeof error === "object" && "digest" in error
      ? String((error as { digest?: unknown }).digest ?? "")
      : "";
  return (
    <div
      role="alert"
      className={
        fullScreen
          ? "flex min-h-screen items-center justify-center px-4"
          : "flex items-center justify-center px-4 py-16"
      }
    >
      <div className="text-center">
        <OrcaError size="lg" />
        <h1 className="mt-4 font-display text-2xl font-bold text-white">
          {t("errorPage.somethingWentWrong")}
        </h1>
        <p className="mt-2 text-sm text-gray-400 max-w-md mx-auto">
          {message || t("errorPage.defaultError")}
        </p>
        {digest && (
          <p className="mt-2 font-mono text-xs text-gray-400">
            {t("errorPage.errorId")}: {digest}
          </p>
        )}
        {onRetry && (
          <div className="mt-6">
            <Button onClick={onRetry}>{t("errorPage.tryAgain")}</Button>
          </div>
        )}
      </div>
    </div>
  );
}
