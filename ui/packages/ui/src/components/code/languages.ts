import type { LanguageRegistration } from "shiki/core";

/**
 * The grammars Orkka ships. Each entry is a lazy import of one grammar module (the module's
 * default export already includes its hard dependencies, e.g. `vue` pulls `css`/`html`), so
 * the first file of a given language pays for that grammar and nothing else — the full shiki
 * bundle would otherwise drag ~350 grammars into the client.
 *
 * Add a language here (and to EXTENSIONS / FENCE_ALIASES below) and every code surface picks
 * it up: file viewer, diffs, markdown fences, plans.
 */
const LANG_LOADERS = {
  bash: () => import("shiki/langs/bash.mjs"),
  c: () => import("shiki/langs/c.mjs"),
  cpp: () => import("shiki/langs/cpp.mjs"),
  csharp: () => import("shiki/langs/csharp.mjs"),
  css: () => import("shiki/langs/css.mjs"),
  dart: () => import("shiki/langs/dart.mjs"),
  diff: () => import("shiki/langs/diff.mjs"),
  dockerfile: () => import("shiki/langs/dockerfile.mjs"),
  dotenv: () => import("shiki/langs/dotenv.mjs"),
  go: () => import("shiki/langs/go.mjs"),
  graphql: () => import("shiki/langs/graphql.mjs"),
  hcl: () => import("shiki/langs/hcl.mjs"),
  html: () => import("shiki/langs/html.mjs"),
  ini: () => import("shiki/langs/ini.mjs"),
  java: () => import("shiki/langs/java.mjs"),
  javascript: () => import("shiki/langs/javascript.mjs"),
  json: () => import("shiki/langs/json.mjs"),
  jsonc: () => import("shiki/langs/jsonc.mjs"),
  jsx: () => import("shiki/langs/jsx.mjs"),
  kotlin: () => import("shiki/langs/kotlin.mjs"),
  lua: () => import("shiki/langs/lua.mjs"),
  makefile: () => import("shiki/langs/makefile.mjs"),
  markdown: () => import("shiki/langs/markdown.mjs"),
  mdx: () => import("shiki/langs/mdx.mjs"),
  php: () => import("shiki/langs/php.mjs"),
  powershell: () => import("shiki/langs/powershell.mjs"),
  prisma: () => import("shiki/langs/prisma.mjs"),
  python: () => import("shiki/langs/python.mjs"),
  ruby: () => import("shiki/langs/ruby.mjs"),
  rust: () => import("shiki/langs/rust.mjs"),
  scss: () => import("shiki/langs/scss.mjs"),
  sql: () => import("shiki/langs/sql.mjs"),
  svelte: () => import("shiki/langs/svelte.mjs"),
  swift: () => import("shiki/langs/swift.mjs"),
  toml: () => import("shiki/langs/toml.mjs"),
  tsx: () => import("shiki/langs/tsx.mjs"),
  typescript: () => import("shiki/langs/typescript.mjs"),
  vue: () => import("shiki/langs/vue.mjs"),
  xml: () => import("shiki/langs/xml.mjs"),
  yaml: () => import("shiki/langs/yaml.mjs"),
} satisfies Record<string, () => Promise<{ default: LanguageRegistration[] }>>;

type CodeLang = keyof typeof LANG_LOADERS;

const CODE_LANGS = Object.keys(LANG_LOADERS) as CodeLang[];

function isCodeLang(value: string): value is CodeLang {
  return Object.prototype.hasOwnProperty.call(LANG_LOADERS, value);
}

/** File extension (lower-case, no dot) → grammar. */
const EXTENSIONS: Record<string, CodeLang> = {
  ts: "typescript",
  mts: "typescript",
  cts: "typescript",
  tsx: "tsx",
  js: "javascript",
  mjs: "javascript",
  cjs: "javascript",
  jsx: "jsx",
  json: "json",
  jsonc: "jsonc",
  json5: "jsonc",
  md: "markdown",
  mdx: "mdx",
  css: "css",
  scss: "scss",
  html: "html",
  htm: "html",
  xml: "xml",
  svg: "xml",
  xaml: "xml",
  csproj: "xml",
  props: "xml",
  targets: "xml",
  plist: "xml",
  yaml: "yaml",
  yml: "yaml",
  toml: "toml",
  py: "python",
  rs: "rust",
  go: "go",
  cs: "csharp",
  java: "java",
  kt: "kotlin",
  kts: "kotlin",
  swift: "swift",
  dart: "dart",
  c: "c",
  h: "c",
  cpp: "cpp",
  cc: "cpp",
  cxx: "cpp",
  hpp: "cpp",
  rb: "ruby",
  php: "php",
  lua: "lua",
  sh: "bash",
  bash: "bash",
  zsh: "bash",
  ps1: "powershell",
  psm1: "powershell",
  sql: "sql",
  graphql: "graphql",
  gql: "graphql",
  tf: "hcl",
  tfvars: "hcl",
  hcl: "hcl",
  vue: "vue",
  svelte: "svelte",
  prisma: "prisma",
  env: "dotenv",
  ini: "ini",
  cfg: "ini",
  conf: "ini",
  sln: "ini",
  editorconfig: "ini",
  gitattributes: "ini",
  diff: "diff",
  patch: "diff",
};

/** Filenames without a telling extension. Lower-case. */
const FILENAMES: Record<string, CodeLang> = {
  dockerfile: "dockerfile",
  containerfile: "dockerfile",
  makefile: "makefile",
  gnumakefile: "makefile",
  ".gitignore": "bash",
  ".dockerignore": "bash",
  ".npmrc": "ini",
  ".env": "dotenv",
  ".env.example": "dotenv",
  ".env.local": "dotenv",
  ".bashrc": "bash",
  ".zshrc": "bash",
  "cmakelists.txt": "makefile",
};

/** Markdown fence info strings and other aliases → grammar. Lower-case. */
const FENCE_ALIASES: Record<string, CodeLang> = {
  ts: "typescript",
  typescript: "typescript",
  tsx: "tsx",
  js: "javascript",
  javascript: "javascript",
  mjs: "javascript",
  cjs: "javascript",
  jsx: "jsx",
  json: "json",
  jsonc: "jsonc",
  json5: "jsonc",
  md: "markdown",
  markdown: "markdown",
  mdx: "mdx",
  css: "css",
  scss: "scss",
  html: "html",
  xml: "xml",
  svg: "xml",
  yaml: "yaml",
  yml: "yaml",
  toml: "toml",
  py: "python",
  python: "python",
  rs: "rust",
  rust: "rust",
  go: "go",
  golang: "go",
  cs: "csharp",
  "c#": "csharp",
  csharp: "csharp",
  java: "java",
  kt: "kotlin",
  kotlin: "kotlin",
  swift: "swift",
  dart: "dart",
  c: "c",
  cpp: "cpp",
  "c++": "cpp",
  rb: "ruby",
  ruby: "ruby",
  php: "php",
  lua: "lua",
  sh: "bash",
  shell: "bash",
  bash: "bash",
  zsh: "bash",
  console: "bash",
  shellscript: "bash",
  ps: "powershell",
  ps1: "powershell",
  pwsh: "powershell",
  powershell: "powershell",
  sql: "sql",
  psql: "sql",
  graphql: "graphql",
  gql: "graphql",
  tf: "hcl",
  terraform: "hcl",
  hcl: "hcl",
  vue: "vue",
  svelte: "svelte",
  prisma: "prisma",
  env: "dotenv",
  dotenv: "dotenv",
  ini: "ini",
  docker: "dockerfile",
  dockerfile: "dockerfile",
  makefile: "makefile",
  make: "makefile",
  diff: "diff",
  patch: "diff",
};

/**
 * Grammar for a file path, or null when Orkka has none — the caller renders plain text.
 * Accepts full paths (either separator) or bare filenames.
 */
function langForPath(filePath: string | null | undefined): CodeLang | null {
  if (!filePath) return null;
  const name = filePath.split(/[\\/]/).pop() ?? "";
  const lower = name.toLowerCase();
  if (!lower) return null;

  const byName = FILENAMES[lower];
  if (byName) return byName;
  if (lower.startsWith("dockerfile.")) return "dockerfile";
  if (lower.startsWith(".env.")) return "dotenv";

  const dot = lower.lastIndexOf(".");
  if (dot < 0) return null;
  return EXTENSIONS[lower.slice(dot + 1)] ?? null;
}

/**
 * Grammar for a markdown fence info string (`ts`, `c#`, `shell`…) or a loose language name.
 * Returns null for unknown / `text` / `plaintext`.
 */
function langForAlias(alias: string | null | undefined): CodeLang | null {
  if (!alias) return null;
  // Fence info can carry extras: "ts title=foo", "js {1,3}"
  const first = alias.trim().split(/[\s{]/)[0]?.toLowerCase() ?? "";
  if (!first) return null;
  if (isCodeLang(first)) return first;
  return FENCE_ALIASES[first] ?? null;
}

export { LANG_LOADERS, CODE_LANGS, isCodeLang, langForPath, langForAlias };
export type { CodeLang };
