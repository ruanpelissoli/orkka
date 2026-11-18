import { vi } from "vitest";
import React from "react";

// The translation function simply echoes back the key for assertions
const createTranslationFn = () => {
  const t = (key: string) => key;
  t.rich = (key: string) => key;
  t.markup = (key: string) => key;
  t.raw = (key: string) => key;
  t.has = () => true;
  return t;
};

vi.mock("use-intl", () => ({
  useTranslations: vi.fn(() => createTranslationFn()),
  useLocale: vi.fn(() => "en"),
  useMessages: vi.fn(() => ({})),
  useFormatter: vi.fn(() => ({
    dateTime: (d: Date) => d.toISOString(),
    number: (n: number) => String(n),
    relativeTime: () => "just now",
  })),
  useNow: vi.fn(() => new Date()),
  useTimeZone: vi.fn(() => "UTC"),
  IntlProvider: ({ children }: { children: React.ReactNode }) => children,
}));
