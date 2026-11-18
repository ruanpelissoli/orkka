import { describe, it, expect } from "vitest";
import {
  classifyRepoUrl,
  detectRepoForge,
  isRemoteRepoUrl,
  repoNameFromUrl,
  repoUrlHasEmbeddedCredential,
  repoUrlHost,
  repoUrlKind,
} from "@/lib/repo-url";

/** Mirrors the cases orkka-api's RepoUrl.cs is built around — the two must agree. */
describe("repo-url", () => {
  describe("isRemoteRepoUrl / repoUrlKind", () => {
    it.each([
      ["https://github.com/acme/api.git", "https"],
      ["http://git.internal/acme/api.git", "https"],
      ["ssh://git@github.com/acme/api.git", "ssh"],
      ["git@github.com:acme/api.git", "scp"],
      ["git@github-work:acme/api.git", "scp"],
      ["git@ssh.dev.azure.com:v3/org/proj/repo", "scp"],
    ] as const)("%s is a %s remote", (url, kind) => {
      expect(isRemoteRepoUrl(url)).toBe(true);
      expect(repoUrlKind(url)).toBe(kind);
    });

    it.each([
      "C:\\code\\api",
      "C:/code/api",
      "/home/me/code/api",
      "./api",
      "file:///home/me/api",
      "some/dir:with-colon",
    ])("%s is a folder, not a remote", (url) => {
      expect(isRemoteRepoUrl(url)).toBe(false);
      expect(repoUrlKind(url)).toBe("local");
    });

    it("says nothing for an empty field", () => {
      expect(repoUrlKind("")).toBeNull();
      expect(repoUrlKind("   ")).toBeNull();
      expect(classifyRepoUrl("")).toBeNull();
    });
  });

  describe("repoUrlHost", () => {
    it("reads hosts from every remote shape, lower-cased", () => {
      expect(repoUrlHost("https://GitHub.com/acme/api")).toBe("github.com");
      expect(repoUrlHost("ssh://git@gitlab.com/acme/api.git")).toBe("gitlab.com");
      expect(repoUrlHost("git@github-work:acme/api.git")).toBe("github-work");
      expect(repoUrlHost("C:\\code\\api")).toBeNull();
    });
  });

  describe("detectRepoForge", () => {
    it.each([
      ["https://github.com/acme/api.git", "github"],
      ["git@github.com:acme/api.git", "github"],
      ["https://acme.ghe.github.com/acme/api", "github"],
      ["https://org@dev.azure.com/org/proj/_git/repo", "azuredevops"],
      ["git@ssh.dev.azure.com:v3/org/proj/repo", "azuredevops"],
      ["https://org.visualstudio.com/proj/_git/repo", "azuredevops"],
      ["https://gitlab.com/group/sub/api.git", "gitlab"],
      ["https://gitlab.acme.io/group/api.git", "gitlab"],
      ["git@bitbucket.org:acme/api.git", "bitbucket"],
      ["https://git.internal/acme/api.git", "other"],
      // A host alias from ~/.ssh/config cannot be read — the user overrides it.
      ["git@github-work:acme/api.git", "other"],
      ["C:\\code\\api", "other"],
    ] as const)("%s → %s", (url, forge) => {
      expect(detectRepoForge(url)).toBe(forge);
    });
  });

  describe("repoUrlHasEmbeddedCredential", () => {
    it.each([
      "https://user:secret@github.com/acme/api.git",
      "https://ghp_abc123@github.com/acme/api.git",
      "https://github_pat_xyz@github.com/acme/api.git",
      "https://x-access-token:abc@github.com/acme/api.git",
      "https://oauth2:glpat-abc@gitlab.com/acme/api.git",
      "https://glpat-abc@gitlab.com/acme/api.git",
    ])("%s carries a secret", (url) => {
      expect(repoUrlHasEmbeddedCredential(url)).toBe(true);
    });

    it.each([
      // Azure DevOps' own clone URLs carry a bare user name.
      "https://org@dev.azure.com/org/proj/_git/repo",
      "https://github.com/acme/api.git",
      // ssh users are not secrets.
      "ssh://git@github.com/acme/api.git",
      "git@github.com:acme/api.git",
    ])("%s carries no secret", (url) => {
      expect(repoUrlHasEmbeddedCredential(url)).toBe(false);
    });
  });

  describe("repoNameFromUrl", () => {
    it.each([
      ["https://github.com/acme/api.git", "api"],
      ["git@github-work:acme/web-app.git", "web-app"],
      ["ssh://git@gitlab.com/group/sub/svc", "svc"],
      ["https://org@dev.azure.com/org/proj/_git/My%20Repo", "My Repo"],
      ["git@ssh.dev.azure.com:v3/org/proj/repo", "repo"],
      ["C:\\code\\api", ""],
    ] as const)("%s → %s", (url, name) => {
      expect(repoNameFromUrl(url)).toBe(name);
    });
  });

  describe("classifyRepoUrl", () => {
    it("accepts an ssh alias remote and suggests its name", () => {
      expect(classifyRepoUrl("git@github-work:acme/api.git")).toEqual({
        kind: "scp",
        host: "github-work",
        forge: "other",
        hasEmbeddedCredential: false,
        suggestedName: "api",
        acceptable: true,
      });
    });

    it("refuses a folder path and a URL with a token in it", () => {
      expect(classifyRepoUrl("C:\\code\\api")?.acceptable).toBe(false);
      expect(classifyRepoUrl("https://ghp_x@github.com/acme/api.git")?.acceptable).toBe(false);
    });
  });
});
