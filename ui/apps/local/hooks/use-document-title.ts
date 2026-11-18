import { useEffect } from "react";

/** The app's own title, used whenever no page names one. Set by the locale layout. */
let defaultTitle = "Orkka";

export function setDefaultDocumentTitle(title: string): void {
  // A page's own title stays: only a tab currently showing the default follows the change.
  if (document.title === defaultTitle) document.title = title;
  defaultTitle = title;
}

/**
 * Sets the tab title to "<title> | Orkka" while the page is mounted, and puts the app's default
 * title back when it unmounts — so a page without a title of its own never keeps the previous
 * page's.
 */
export function useDocumentTitle(title: string | null | undefined): void {
  useEffect(() => {
    if (!title) return;
    document.title = `${title} | Orkka`;
    return () => {
      document.title = defaultTitle;
    };
  }, [title]);
}
