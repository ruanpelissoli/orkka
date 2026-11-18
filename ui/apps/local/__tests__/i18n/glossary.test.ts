import { describe, it, expect } from "vitest";

import { GLOSSARY_KEYS } from "../../../../packages/ui/src/components/ui/glossary";

import enCommon from "../../../../packages/i18n/messages/en/common.json";
import deCommon from "../../../../packages/i18n/messages/de/common.json";
import esCommon from "../../../../packages/i18n/messages/es/common.json";
import frCommon from "../../../../packages/i18n/messages/fr/common.json";
import ptCommon from "../../../../packages/i18n/messages/pt/common.json";

// The glossary is the plain-language layer over the jargon that has to stay in the UI.
// A key without copy renders a raw "common.glossary.x.term" path in the hover, and a
// tag without a handler renders "<repo>" literally to visitors — so pin both here.
const commons = { en: enCommon, de: deCommon, es: esCommon, fr: frCommon, pt: ptCommon } as const;

type Entry = { term?: string; definition?: string };

describe("glossary catalogs", () => {
  for (const [locale, common] of Object.entries(commons)) {
    const glossary = (common as { glossary: Record<string, Entry> }).glossary;

    it(`has term + definition for every glossary key in ${locale}`, () => {
      for (const key of GLOSSARY_KEYS) {
        const entry = glossary[key];
        expect(entry, `${locale} is missing glossary "${key}"`).toBeDefined();
        expect(entry.term?.trim(), `${locale}.${key}.term is empty`).toBeTruthy();
        expect(
          entry.definition?.trim(),
          `${locale}.${key}.definition is empty`
        ).toBeTruthy();
      }
    });

    it(`has no orphaned glossary copy in ${locale}`, () => {
      expect(Object.keys(glossary).sort()).toEqual([...GLOSSARY_KEYS].sort());
    });
  }
});
