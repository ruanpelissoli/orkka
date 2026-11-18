import js from "@eslint/js";
import globals from "globals";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";
import { defineConfig, globalIgnores } from "eslint/config";
import { designSystemRules } from "./packages/ui/src/eslint-design-system.mjs";

/**
 * One config for the whole UI: the app (apps/local) and the shared packages. Run `npm run lint`
 * from ui/ or apps/local; both lint everything.
 */
export default defineConfig([
  globalIgnores(["**/node_modules/**", "**/dist/**", "**/coverage/**"]),
  {
    files: ["**/*.{ts,tsx}"],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat["recommended-latest"],
    ],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
    },
    rules: {
      // Typography, contrast and palette contract — see the shared config for why.
      "no-restricted-syntax": ["error", ...designSystemRules],
      "@typescript-eslint/no-unused-vars": [
        "error",
        { argsIgnorePattern: "^_", varsIgnorePattern: "^_", caughtErrorsIgnorePattern: "^_" },
      ],
    },
  },
  {
    // BrandLockup is the component that is *allowed* to set the brand font, and the theme
    // constants are where the palette is defined rather than consumed.
    files: ["packages/ui/src/components/ui/BrandLockup.tsx", "packages/core/src/lib/constants.ts"],
    rules: { "no-restricted-syntax": "off" },
  },
  {
    // Hot reload keeps a page's state only when its module exports nothing but components, so
    // the route modules (pages, layouts, the route table) are held to that. Shared modules
    // deliberately pair a provider with its hook, or Link with the router hooks; editing one
    // of those reloads the page in `npm run dev`, which is all the rule protects.
    files: ["apps/local/app/**/*.tsx", "apps/local/src/**/*.tsx"],
    extends: [reactRefresh.configs.vite],
  },
  {
    // Tests and build config run in Node.
    files: ["apps/local/__tests__/**", "apps/local/*.{ts,mts}"],
    languageOptions: { globals: { ...globals.browser, ...globals.node } },
  },
]);
