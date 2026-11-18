import { describe, it, expect } from "vitest";
import {
  documentUploadTitle,
  isFileDocument,
  isPdfUpload,
  looksBinary,
} from "@/lib/utils";

// The upload input's `accept` list is only a dialog hint: a user who switches the file
// picker to "All files" can hand us anything. `isPdfUpload` routes PDFs to the store-as-is
// path; these guards are what keeps every other binary out of the markdown path.
describe("documentUploadTitle", () => {
  it("accepts the supported text extensions and strips them", () => {
    expect(documentUploadTitle("Project Brief.md")).toBe("Project Brief");
    expect(documentUploadTitle("notes.markdown")).toBe("notes");
    expect(documentUploadTitle("notes.txt")).toBe("notes");
    expect(documentUploadTitle("README.MD")).toBe("README");
  });

  it("rejects a PDF and other binary formats", () => {
    // A PDF is supported, but never as text — it goes through uploadFile instead, so this
    // must keep returning null or the bytes would be decoded into the document body again.
    expect(documentUploadTitle("05-resistance-and-stages.pdf")).toBeNull();
    expect(documentUploadTitle("diagram.png")).toBeNull();
    expect(documentUploadTitle("archive.zip")).toBeNull();
  });

  it("rejects names with no extension or no title left over", () => {
    expect(documentUploadTitle("README")).toBeNull();
    expect(documentUploadTitle(".md")).toBeNull();
    expect(documentUploadTitle("   .txt")).toBeNull();
  });

  it("keeps inner dots so only the final extension is stripped", () => {
    expect(documentUploadTitle("skill-tree-v4.json.md")).toBe("skill-tree-v4.json");
  });
});

describe("isPdfUpload", () => {
  it("routes PDFs to the store-as-is path, whatever the case", () => {
    expect(isPdfUpload("05-resistance-and-stages.pdf")).toBe(true);
    expect(isPdfUpload("SPEC.PDF")).toBe(true);
  });

  it("leaves every other format on the text path", () => {
    expect(isPdfUpload("notes.md")).toBe(false);
    expect(isPdfUpload("diagram.png")).toBe(false);
    // Only the final extension counts — this one is a markdown file about a PDF.
    expect(isPdfUpload("about-the.pdf.md")).toBe(false);
  });
});

describe("isFileDocument", () => {
  it("treats a document with a stored file name as a file document", () => {
    expect(isFileDocument({ fileName: "spec.pdf" })).toBe(true);
  });

  it("treats markdown documents as not files", () => {
    expect(isFileDocument({ fileName: null })).toBe(false);
    expect(isFileDocument({})).toBe(false);
  });
});

describe("looksBinary", () => {
  it("passes normal markdown through", () => {
    expect(looksBinary("# Title\n\nSome body text with an em dash — and accents: café.")).toBe(
      false
    );
  });

  it("catches a binary renamed to .md via NUL bytes", () => {
    const pdfBytes = "%PDF-1.4\n1 0 obj\n" + String.fromCharCode(0) + "stream\n";
    expect(looksBinary(pdfBytes)).toBe(true);
  });

  it("catches undecodable bytes that became replacement chars", () => {
    const garbage = "stream " + String.fromCharCode(0xfffd).repeat(40);
    expect(looksBinary(garbage)).toBe(true);
  });

  it("tolerates a lone replacement char in otherwise valid text", () => {
    const text = "A long document body ".repeat(20) + String.fromCharCode(0xfffd);
    expect(looksBinary(text)).toBe(false);
  });
});
