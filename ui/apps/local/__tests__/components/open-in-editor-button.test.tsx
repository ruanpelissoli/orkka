import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";

import { OpenInEditorButton } from "@/components/task/OpenInEditorButton";

describe("OpenInEditorButton", () => {
  beforeEach(() => {
    mockApi.editors.list.mockClear();
    mockApi.editors.openTask.mockClear();
  });

  it("renders nothing when there is no machine to open on", async () => {
    // Covers every unsupported case at once — cloud task, offline runner, old runner,
    // relay not deployed. The API collapses them deliberately.
    mockApi.editors.list.mockResolvedValue({ supported: false, editors: [] });

    const { container } = render(<OpenInEditorButton taskId="task-1" />);

    await waitFor(() => expect(mockApi.editors.list).toHaveBeenCalled());
    expect(container).toBeEmptyDOMElement();
  });

  it("asks about the specific task, not the machine in general", async () => {
    mockApi.editors.list.mockResolvedValue({ supported: false, editors: [] });

    render(<OpenInEditorButton taskId="task-42" />);

    // The hosted API can only answer per task — the runner it must reach is the one
    // that ran that task.
    await waitFor(() =>
      expect(mockApi.editors.list).toHaveBeenCalledWith("task-42", "")
    );
  });

  it("opens directly when only one editor is installed", async () => {
    mockApi.editors.list.mockResolvedValue({
      supported: true,
      editors: [{ key: "vscode", displayName: "VS Code" }],
    });
    mockApi.editors.openTask.mockResolvedValue({ openedWith: "VS Code" });

    render(<OpenInEditorButton taskId="task-1" />);

    const button = await screen.findByRole("button");
    fireEvent.click(button);

    await waitFor(() =>
      expect(mockApi.editors.openTask).toHaveBeenCalledWith(
        "task-1",
        "",
        "vscode"
      )
    );
  });

  it("surfaces a failure instead of pretending it opened", async () => {
    mockApi.editors.list.mockResolvedValue({
      supported: true,
      editors: [{ key: "vscode", displayName: "VS Code" }],
    });
    mockApi.editors.openTask.mockRejectedValue(new Error("nope"));

    render(<OpenInEditorButton taskId="task-1" />);
    fireEvent.click(await screen.findByRole("button"));

    expect(await screen.findByText("failed")).toBeInTheDocument();
  });
});
