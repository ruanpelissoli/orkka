import type { AbstractIntlMessages } from "use-intl";
import type { Locale } from "./routing";

/**
 * The catalogs, one lazy chunk per file: only the active locale's JSON is downloaded. Each
 * locale has two namespaces, `common` and `dashboard` (packages/i18n/messages/{locale}/).
 */
const catalogs = import.meta.glob<AbstractIntlMessages>(
  "../../../packages/i18n/messages/*/*.json",
  { import: "default" }
);

const NAMESPACES = ["common", "dashboard"] as const;

export async function loadMessages(locale: Locale): Promise<AbstractIntlMessages> {
  const entries = await Promise.all(
    NAMESPACES.map(async (ns) => {
      const load = catalogs[`../../../packages/i18n/messages/${locale}/${ns}.json`];
      if (!load) throw new Error(`Missing message catalog ${locale}/${ns}.json`);
      return [ns, await load()] as const;
    })
  );
  return Object.fromEntries(entries);
}
