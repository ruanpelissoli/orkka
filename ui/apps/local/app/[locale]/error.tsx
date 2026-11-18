import { useNavigate, useLocation, useRouteError } from "react-router";
import { PageError } from "@/components/page";

/**
 * A chunk that no longer exists on the server: the UI was rebuilt while this tab was open, so
 * its hashed file names moved. Only a full reload fetches the new index.html.
 */
function isStaleChunk(error: unknown): boolean {
  return (
    error instanceof Error &&
    /Failed to fetch dynamically imported module|Importing a module script failed|error loading dynamically imported module/i.test(
      error.message
    )
  );
}

/** The route error boundary: a page threw while rendering. "Try again" re-renders the route. */
export default function RootError() {
  const error = useRouteError();
  const navigate = useNavigate();
  const location = useLocation();
  const retry = () => {
    if (isStaleChunk(error)) window.location.reload();
    // A new navigation clears the router's error state and renders the route again.
    else void navigate(location, { replace: true });
  };
  return <PageError error={error} onRetry={retry} fullScreen />;
}
