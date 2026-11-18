/**
 * The two fixture apps under fixtures/repos. They are no longer published anywhere: each test
 * seeds a local bare remote from one of them (src/testdata/bare-remote.ts) or copies one into a
 * local folder (src/testdata/local-folder.ts).
 *
 * Command trust: each fixture's orkka.json DECLARES its setup/check commands (the values below).
 * A bare remote is seeded with orkka.json on its default branch, so the runner runs them without
 * an approval. A linked local folder has no `origin/HEAD`, so there nothing vouches for them — the
 * runner refuses and the task thread carries a note with `./orkka commands approve <id>`; the
 * flow still lands (src/runner/commands.ts approves one when a spec needs it).
 */
export interface FixtureRepo {
  key: "web" | "api";
  /** Folder under fixtures/repos. */
  dir: string;
  defaultBranch: string;
  /** Path of the file the standard agent task edits. */
  targetFile: string;
  /** Substring that must appear in `targetFile` after the standard task lands. */
  featureListMarker: string;
  /** orkka.json `setup` (null when the fixture needs none). */
  setupCommand: string | null;
  /** orkka.json `check`. */
  checkCommand: string;
  runPort: number;
}

export const fixtureRepos: Record<"web" | "api", FixtureRepo> = {
  web: {
    key: "web",
    dir: "orkka-e2e-fixture-web",
    defaultBranch: "main",
    targetFile: "app/features.ts",
    featureListMarker: "export const features",
    setupCommand: "npm install",
    checkCommand: "npm run lint && npm test",
    runPort: 3000,
  },
  api: {
    key: "api",
    dir: "orkka-e2e-fixture-api",
    defaultBranch: "main",
    targetFile: "src/features.js",
    featureListMarker: "features",
    setupCommand: null,
    checkCommand: "npm test",
    runPort: 4000,
  },
};

/** The standard, tiny, verifiable agent task. */
export function standardTask(repo: FixtureRepo, token: string) {
  return {
    title: `Add feature entry "${token}"`,
    description: [
      `Add a new entry with the exact text "${token}" to the features list in \`${repo.targetFile}\`.`,
      "Do not change anything else. Keep the existing entries and their order. Run the project's check command before finishing.",
      "This is an automated end-to-end test of the Orkka pipeline; the change is intentionally trivial.",
    ].join("\n\n"),
  };
}
