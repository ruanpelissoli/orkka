import { describe, it, expect } from "vitest";
import fs from "node:fs";
import path from "node:path";

/**
 * Page titles are the one class of UI string the test suite cannot otherwise catch: the
 * intl mock echoes keys back, so a component asking for a key nobody translated still
 * renders "happily" in tests and ships as a raw key — or, before this pass, as hardcoded
 * English on a page rendered in another language. This walks the real catalogs instead.
 */
const APP_DIRS = [path.resolve(__dirname, "../app")];
const MESSAGES_DIR = path.resolve(__dirname, "../../../packages/i18n/messages");
const LOCALES = ["en", "es", "pt", "fr", "de"] as const;
const NAMESPACES = ["common", "dashboard"] as const;

type Catalog = Record<string, unknown>;

function loadCatalog(locale: string, namespace: string): Catalog {
  return JSON.parse(
    fs.readFileSync(path.join(MESSAGES_DIR, locale, `${namespace}.json`), "utf8")
  );
}

function lookup(catalog: Catalog, key: string): unknown {
  return key
    .split(".")
    .reduce<unknown>(
      (node, part) =>
        node && typeof node === "object"
          ? (node as Record<string, unknown>)[part]
          : undefined,
      catalog
    );
}

function walk(dir: string): string[] {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) return walk(full);
    return entry.name.endsWith(".tsx") ? [full] : [];
  });
}

/**
 * Every page title key, paired with its namespace: a `t("...")` inside a generateMetadata block,
 * or the key a client page hands to `useDocumentTitle(t("..."))` (its translator's namespace is
 * read from the `const t = useTranslations("...")` that declares it).
 */
function metadataKeyReferences(): { file: string; namespace: string; key: string }[] {
  const found: { file: string; namespace: string; key: string }[] = [];
  for (const file of APP_DIRS.flatMap(walk)) {
    const source = fs.readFileSync(file, "utf8");
    for (const call of source.matchAll(/useDocumentTitle\((\w+)\("([^"]+)"\)\)/g)) {
      const [, translator, key] = call;
      const scope = source.match(
        new RegExp(`const ${translator} = useTranslations\\("([^"]+)"\\)`)
      )?.[1];
      if (!scope) continue;
      const [namespace, ...prefix] = scope.split(".");
      found.push({ file, namespace: namespace!, key: [...prefix, key].join(".") });
    }
    const block = source.match(
      /export async function generateMetadata[\s\S]*?\n\}/
    );
    if (!block) continue;
    const namespace = block[0].match(/getTranslations\("([^"]+)"\)/)?.[1];
    if (!namespace) continue;
    for (const match of block[0].matchAll(/\bt\("([^"]+)"\)/g)) {
      found.push({ file, namespace, key: match[1]! });
    }
  }
  return found;
}

describe("page metadata translations", () => {
  const references = metadataKeyReferences();

  it("finds a localized title on every page that declares one", () => {
    // Guards the walker itself: a regex that silently matches nothing would make every
    // assertion below vacuously pass.
    expect(references.length).toBeGreaterThan(20);
  });

  /**
   * app/layout.tsx is the one legitimate holdout: it sits ABOVE the [locale] segment, so it
   * has no locale to translate against, and its job is the title TEMPLATE plus a default for
   * routes outside the localized tree. Every localized route is shadowed by the [locale]
   * layout's own generateMetadata, so nothing a user browses in another language reads from
   * it. Anything else appearing here is a page that regressed to a hardcoded English title.
   */
  const ROOT_LAYOUTS = APP_DIRS.map((dir) => path.join(dir, "layout.tsx"));

  it("has no page left with a hardcoded English title", () => {
    const hardcoded = APP_DIRS.flatMap(walk)
      .filter((file) => !ROOT_LAYOUTS.includes(file))
      .filter((file) =>
        /export const metadata: Metadata = \{[\s\S]*?title:/.test(
          fs.readFileSync(file, "utf8")
        )
      );
    expect(hardcoded).toEqual([]);
  });

  it.each(LOCALES)("resolves every referenced key in %s", (locale) => {
    const catalogs = Object.fromEntries(
      NAMESPACES.map((ns) => [ns, loadCatalog(locale, ns)])
    );
    const missing = references
      .filter(({ namespace, key }) => typeof lookup(catalogs[namespace]!, key) !== "string")
      .map(({ file, namespace, key }) => `${file}: ${namespace}.${key}`);
    expect(missing).toEqual([]);
  });

  it.each(NAMESPACES)("keeps the %s meta group in step across locales", (namespace) => {
    const shape = (locale: string) => {
      const meta = lookup(loadCatalog(locale, namespace), "meta");
      const flatten = (node: unknown, prefix = ""): string[] =>
        node && typeof node === "object"
          ? Object.entries(node as Record<string, unknown>).flatMap(([k, v]) =>
              flatten(v, prefix ? `${prefix}.${k}` : k)
            )
          : [prefix];
      return flatten(meta).sort();
    };

    const english = shape("en");
    expect(english.length).toBeGreaterThan(0);
    for (const locale of LOCALES) {
      expect({ locale, keys: shape(locale) }).toEqual({ locale, keys: english });
    }
  });
});
