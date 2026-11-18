import { useEffect } from "react";
import { useRouter } from "@/i18n/navigation";

/**
 * Replaces the current entry with `to` (an unprefixed app path; the i18n router adds the
 * locale). Redirect-only routes (old URLs, section roots) render this instead of calling
 * Next's server `redirect()`.
 */
export function ClientRedirect({ to }: { to: string }) {
  const router = useRouter();
  useEffect(() => {
    router.replace(to);
  }, [router, to]);
  return null;
}
