import { describe, it, expect, beforeEach, vi } from "vitest";
import { act, render, screen, waitFor } from "@testing-library/react";
import { mockInvalidateApiCache } from "../mocks/api";
import { refreshData } from "@/lib/data-refresh";
import { useApiQuery } from "@/hooks/use-api-query";

/**
 * The page data layer: useApiQuery loads in the browser and refetches when refreshData() fires —
 * the replacement for revalidatePath / router.refresh().
 */
function Probe({ load, id = "a" }: { load: () => Promise<string>; id?: string }) {
  const { data, error, loading, reload } = useApiQuery(load, [id]);
  return (
    <div>
      <span data-testid="state">
        {loading ? "loading" : error ? `error:${(error as Error).message}` : `data:${data}`}
      </span>
      <button onClick={reload}>reload</button>
    </div>
  );
}

describe("useApiQuery", () => {
  beforeEach(() => {
    mockInvalidateApiCache.mockClear();
  });

  it("shows loading, then the data", async () => {
    render(<Probe load={() => Promise.resolve("one")} />);
    expect(screen.getByTestId("state")).toHaveTextContent("loading");
    await waitFor(() => expect(screen.getByTestId("state")).toHaveTextContent("data:one"));
  });

  it("refetches when refreshData() fires, dropping the API cache, without a loading flash", async () => {
    let n = 0;
    const load = vi.fn(() => Promise.resolve(`v${++n}`));
    render(<Probe load={load} />);
    await waitFor(() => expect(screen.getByTestId("state")).toHaveTextContent("data:v1"));

    act(() => refreshData());
    expect(mockInvalidateApiCache).toHaveBeenCalled();
    // The previous data stays on screen while the refetch is in flight.
    expect(screen.getByTestId("state")).toHaveTextContent("data:v1");
    await waitFor(() => expect(screen.getByTestId("state")).toHaveTextContent("data:v2"));
    expect(load).toHaveBeenCalledTimes(2);
  });

  it("reports a first-load failure as the error state and recovers on reload", async () => {
    const load = vi
      .fn<() => Promise<string>>()
      .mockRejectedValueOnce(new Error("down"))
      .mockResolvedValueOnce("back");
    render(<Probe load={load} />);
    await waitFor(() => expect(screen.getByTestId("state")).toHaveTextContent("error:down"));

    act(() => screen.getByText("reload").click());
    await waitFor(() => expect(screen.getByTestId("state")).toHaveTextContent("data:back"));
  });

  it("keeps what is on screen when a refetch fails", async () => {
    const load = vi
      .fn<() => Promise<string>>()
      .mockResolvedValueOnce("kept")
      .mockRejectedValueOnce(new Error("gone"));
    render(<Probe load={load} />);
    await waitFor(() => expect(screen.getByTestId("state")).toHaveTextContent("data:kept"));

    act(() => refreshData());
    await waitFor(() => expect(load).toHaveBeenCalledTimes(2));
    expect(screen.getByTestId("state")).toHaveTextContent("data:kept");
  });

  it("loads again when the key changes", async () => {
    const load = vi.fn((id: string) => Promise.resolve(`for-${id}`));
    const { rerender } = render(<Probe load={() => load("a")} id="a" />);
    await waitFor(() => expect(screen.getByTestId("state")).toHaveTextContent("data:for-a"));

    rerender(<Probe load={() => load("b")} id="b" />);
    await waitFor(() => expect(screen.getByTestId("state")).toHaveTextContent("data:for-b"));
  });
});
