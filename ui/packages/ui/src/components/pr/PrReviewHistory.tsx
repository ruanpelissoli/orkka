import { useTranslations } from "use-intl";
import { Badge } from "@/components/ui";
import { formatRelativeTime } from "@/lib/utils";
import type { PrReview } from "@/lib/types";

interface PrReviewHistoryProps {
  reviews: PrReview[];
}

const STATE_BADGE: Record<string, { variant: "success" | "error" | "warning" | "default"; labelKey: string }> = {
  APPROVED: { variant: "success", labelKey: "stateApproved" },
  CHANGES_REQUESTED: { variant: "error", labelKey: "stateChangesRequested" },
  COMMENTED: { variant: "default", labelKey: "stateCommented" },
  DISMISSED: { variant: "warning", labelKey: "stateDismissed" },
};

function PrReviewHistory({ reviews }: PrReviewHistoryProps) {
  const t = useTranslations("dashboard.prReview");
  if (reviews.length === 0) return null;

  return (
    <div className="space-y-2">
      <h4 className="text-label px-1">
        {t("reviews")}
      </h4>
      <div className="space-y-2">
        {reviews.map((review) => {
          const badge = STATE_BADGE[review.state] ?? STATE_BADGE.COMMENTED;

          return (
            <div key={review.id} className="flex items-start gap-2 rounded-lg bg-white/[0.02] px-3 py-2">
              {review.authorAvatarUrl ? (
                <img
                  src={review.authorAvatarUrl}
                  alt={review.authorLogin}
                  className="w-5 h-5 rounded-full shrink-0 mt-0.5"
                />
              ) : (
                <div className="w-5 h-5 rounded-full bg-white/[0.06] flex items-center justify-center text-xs font-semibold text-gray-400 shrink-0 mt-0.5">
                  {review.authorLogin[0]?.toUpperCase()}
                </div>
              )}
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 flex-wrap">
                  <span className="text-xs font-semibold text-gray-300">
                    {review.authorLogin}
                  </span>
                  <Badge variant={badge.variant} size="sm">
                    {t(badge.labelKey)}
                  </Badge>
                  <span className="text-xs text-gray-400">
                    {formatRelativeTime(review.submittedAt)}
                  </span>
                </div>
                {review.body && (
                  <p className="text-xs text-gray-400 mt-1 whitespace-pre-wrap">
                    {review.body}
                  </p>
                )}
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}

export { PrReviewHistory };
