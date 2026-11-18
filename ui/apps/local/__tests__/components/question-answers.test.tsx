import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import {
  formatQuestionAnswers,
  parseQuestionAnswers,
  QuestionAnswers,
  QuestionsCard,
  QuestionsGroup,
} from "@/components/chat/QuestionBlocks";

describe("question answers", () => {
  const answers = [
    { question: "Who is this app mainly for?", answer: "Patients booking across many clinics" },
    { question: "Which roles need to use the app?", answer: "Patients, Dentists, Clinic admin / owner" },
  ];

  it("round-trips the reply a QuestionsCard submits", () => {
    const reply = formatQuestionAnswers(answers);
    expect(reply).toBe(
      "**Who is this app mainly for?**\nPatients booking across many clinics\n\n" +
        "**Which roles need to use the app?**\nPatients, Dentists, Clinic admin / owner"
    );
    expect(parseQuestionAnswers(reply)).toEqual(answers);
  });

  it("keeps hand-typed messages as plain text", () => {
    expect(parseQuestionAnswers("Let patients self-book online")).toBeNull();
    expect(parseQuestionAnswers("I want **bold** text here")).toBeNull();
    // A heading-only block with no answer line is not a reply
    expect(parseQuestionAnswers("**Who is this for?**")).toBeNull();
    // One well-formed pair followed by free prose is still prose
    expect(
      parseQuestionAnswers("**Who is this for?**\nPatients\n\nAlso, make it fast please")
    ).toBeNull();
    expect(parseQuestionAnswers("")).toBeNull();
  });

  it("renders each pair without markdown markers", () => {
    render(<QuestionAnswers answers={parseQuestionAnswers(formatQuestionAnswers(answers))!} />);
    expect(screen.getByText("Who is this app mainly for?")).toBeInTheDocument();
    expect(screen.getByText("Patients, Dentists, Clinic admin / owner")).toBeInTheDocument();
    expect(document.body.textContent).not.toContain("**");
  });
});

describe("questions group", () => {
  const stack = {
    question: "Which stack?",
    options: ["Next.js", "Remix"],
    allowCustom: false,
    multiSelect: false,
  };
  const auth = {
    question: "Which auth?",
    options: ["Clerk", "Auth.js"],
    allowCustom: false,
    multiSelect: false,
  };

  /** Two ```questions blocks in one agent turn — the shape that used to lose an answer. */
  function renderTwoBlocks(onSubmit: (text: string) => void) {
    return render(
      <QuestionsGroup onSubmit={onSubmit}>
        <QuestionsCard questions={[stack]} interactive groupKey={0} ownsSubmit={false} />
        <QuestionsCard questions={[auth]} interactive groupKey={1} ownsSubmit />
      </QuestionsGroup>
    );
  }

  it("submits both blocks of a message as one reply", async () => {
    const onSubmit = vi.fn();
    const user = userEvent.setup();
    renderTwoBlocks(onSubmit);

    // Only the last block carries the button — two would each send half the answers
    const submit = screen.getByRole("button", { name: "questionsSubmit" });
    expect(screen.getAllByRole("button", { name: "questionsSubmit" })).toHaveLength(1);
    expect(submit).toBeDisabled();

    await user.click(screen.getByRole("button", { name: "Next.js" }));
    await user.click(screen.getByRole("button", { name: "Clerk" }));
    await user.click(submit);

    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(onSubmit.mock.calls[0][0]).toBe(
      formatQuestionAnswers([
        { question: "Which stack?", answer: "Next.js" },
        { question: "Which auth?", answer: "Clerk" },
      ])
    );
  });

  it("sends the blocks that were answered when one is left blank", async () => {
    const onSubmit = vi.fn();
    const user = userEvent.setup();
    renderTwoBlocks(onSubmit);

    await user.click(screen.getByRole("button", { name: "Remix" }));
    await user.click(screen.getByRole("button", { name: "questionsSubmit" }));

    expect(onSubmit).toHaveBeenCalledWith(
      formatQuestionAnswers([{ question: "Which stack?", answer: "Remix" }])
    );
  });

  it("keeps its own button when rendered outside a group", async () => {
    const onSubmit = vi.fn();
    const user = userEvent.setup();
    render(<QuestionsCard questions={[stack]} interactive onSubmit={onSubmit} />);

    await user.click(screen.getByRole("button", { name: "Next.js" }));
    await user.click(screen.getByRole("button", { name: "questionsSubmit" }));

    expect(onSubmit).toHaveBeenCalledWith(
      formatQuestionAnswers([{ question: "Which stack?", answer: "Next.js" }])
    );
  });
});

describe("multi-select questions", () => {
  const roles = {
    question: "Which roles need the app?",
    options: ["Patients", "Dentists", "Clinic admin"],
    allowCustom: true,
    multiSelect: true,
  };

  /** Multi-select used to render as plain pills, so people picked one and submitted. */
  it("renders its options as checkboxes and keeps every pick", async () => {
    const onSubmit = vi.fn();
    const user = userEvent.setup();
    render(<QuestionsCard questions={[roles]} interactive onSubmit={onSubmit} />);

    // The hint plus checkbox roles are what tell the user more than one answer fits
    expect(screen.getByText("questionsMultiHint")).toBeInTheDocument();
    expect(screen.getAllByRole("checkbox")).toHaveLength(4); // 3 options + "Other..."

    const patients = screen.getByRole("checkbox", { name: "Patients" });
    expect(patients).toHaveAttribute("aria-checked", "false");
    await user.click(patients);
    await user.click(screen.getByRole("checkbox", { name: "Dentists" }));
    expect(patients).toHaveAttribute("aria-checked", "true");

    await user.click(screen.getByRole("button", { name: "questionsSubmit" }));
    expect(onSubmit).toHaveBeenCalledWith(
      formatQuestionAnswers([{ question: roles.question, answer: "Patients, Dentists" }])
    );
  });

  it("leaves single-select options as toggle buttons", () => {
    render(
      <QuestionsCard
        questions={[{ ...roles, multiSelect: false }]}
        interactive
        onSubmit={vi.fn()}
      />
    );
    expect(screen.queryByRole("checkbox")).toBeNull();
    expect(screen.queryByText("questionsMultiHint")).toBeNull();
    expect(screen.getByRole("button", { name: "Patients" })).toHaveAttribute(
      "aria-pressed",
      "false"
    );
  });
});
