import { describe, it, expect } from "vitest";
import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider, type RouteObject } from "react-router";
import { IntlProvider } from "use-intl";
import {
  Link,
  localizeHref,
  stripLocale,
  usePathname,
  useRouter,
} from "@/i18n/navigation";
import { useParams, useSearchParams, useUpdateSearchParams } from "@/lib/navigation";

/** Renders `ui` at `path` inside a router and an IntlProvider for `locale`. */
function renderAt(path: string, ui: React.ReactNode, locale = "en") {
  const routes: RouteObject[] = [
    {
      path: "/:locale/*",
      element: (
        <IntlProvider locale={locale} messages={{}} timeZone="UTC">
          {ui}
        </IntlProvider>
      ),
    },
  ];
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  render(<RouterProvider router={router} />);
  return router;
}

describe("localizeHref", () => {
  it("prefixes app-absolute paths with the locale", () => {
    expect(localizeHref("/p/1/board", "pt")).toBe("/pt/p/1/board");
    expect(localizeHref("/", "de")).toBe("/de");
    expect(localizeHref("/?x=1", "en")).toBe("/en?x=1");
    expect(localizeHref("/settings?tab=a#b", "fr")).toBe("/fr/settings?tab=a#b");
  });

  it("leaves relative, hash, protocol-relative and absolute URLs alone", () => {
    expect(localizeHref("board", "en")).toBe("board");
    expect(localizeHref("#top", "en")).toBe("#top");
    expect(localizeHref("//example.com/x", "en")).toBe("//example.com/x");
    expect(localizeHref("https://example.com", "en")).toBe("https://example.com");
  });
});

describe("stripLocale", () => {
  it("removes a locale prefix and nothing else", () => {
    expect(stripLocale("/en/p/1/board")).toBe("/p/1/board");
    expect(stripLocale("/pt")).toBe("/");
    expect(stripLocale("/pt/")).toBe("/");
    expect(stripLocale("/dashboard")).toBe("/dashboard");
    expect(stripLocale("/xx/dashboard")).toBe("/xx/dashboard");
  });
});

describe("Link", () => {
  it("renders the locale-prefixed href and navigates in-app", async () => {
    const router = renderAt("/es/dashboard", <Link href="/p/42/board">Board</Link>, "es");
    const link = screen.getByRole("link", { name: "Board" });
    expect(link).toHaveAttribute("href", "/es/p/42/board");
    await userEvent.click(link);
    expect(router.state.location.pathname).toBe("/es/p/42/board");
  });

  it("links to the same page in another locale", () => {
    renderAt("/en/settings", <Link href="/settings" locale="de">DE</Link>);
    expect(screen.getByRole("link", { name: "DE" })).toHaveAttribute("href", "/de/settings");
  });

  it("renders an external URL as a plain anchor", () => {
    renderAt("/en/dashboard", <Link href="https://example.com/docs">Docs</Link>);
    expect(screen.getByRole("link", { name: "Docs" })).toHaveAttribute(
      "href",
      "https://example.com/docs"
    );
  });
});

describe("useRouter / usePathname", () => {
  function Probe() {
    const router = useRouter();
    const pathname = usePathname();
    return (
      <>
        <p data-testid="pathname">{pathname}</p>
        <button onClick={() => router.push("/p/1/tasks")}>push</button>
        <button onClick={() => router.replace(pathname, { locale: "pt" })}>switch</button>
        <button onClick={() => router.back()}>back</button>
      </>
    );
  }

  it("reports the path without its locale", () => {
    renderAt("/fr/p/9/board", <Probe />, "fr");
    expect(screen.getByTestId("pathname")).toHaveTextContent("/p/9/board");
  });

  it("pushes locale-prefixed paths and goes back", async () => {
    const router = renderAt("/en/dashboard", <Probe />);
    await userEvent.click(screen.getByRole("button", { name: "push" }));
    expect(router.state.location.pathname).toBe("/en/p/1/tasks");
    expect(router.state.historyAction).toBe("PUSH");
    await userEvent.click(screen.getByRole("button", { name: "back" }));
    expect(router.state.location.pathname).toBe("/en/dashboard");
  });

  it("replaces the current page with the same page in another locale (the locale switcher)", async () => {
    const router = renderAt("/en/p/3/board", <Probe />);
    await userEvent.click(screen.getByRole("button", { name: "switch" }));
    expect(router.state.location.pathname).toBe("/pt/p/3/board");
    expect(router.state.historyAction).toBe("REPLACE");
  });
});

describe("@/lib/navigation", () => {
  function Probe() {
    const params = useParams<{ locale: string }>();
    const search = useSearchParams();
    const update = useUpdateSearchParams();
    return (
      <>
        <p data-testid="locale">{params.locale}</p>
        <p data-testid="task">{search.get("task") ?? "none"}</p>
        <button onClick={() => update((p) => p.set("task", "7"))}>open</button>
        <button onClick={() => update((p) => p.delete("task"))}>close</button>
      </>
    );
  }

  it("reads params and updates the query string without leaving the page", async () => {
    const router = renderAt("/de/p/1/board?view=code", <Probe />, "de");
    expect(screen.getByTestId("locale")).toHaveTextContent("de");
    expect(screen.getByTestId("task")).toHaveTextContent("none");

    await act(() => userEvent.click(screen.getByRole("button", { name: "open" })));
    expect(router.state.location.pathname).toBe("/de/p/1/board");
    expect(new URLSearchParams(router.state.location.search).get("task")).toBe("7");
    expect(new URLSearchParams(router.state.location.search).get("view")).toBe("code");
    expect(screen.getByTestId("task")).toHaveTextContent("7");

    await act(() => userEvent.click(screen.getByRole("button", { name: "close" })));
    expect(router.state.location.search).toBe("?view=code");
  });
});
