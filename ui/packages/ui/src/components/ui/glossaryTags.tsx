import type { ReactNode } from "react";
import { Term } from "./Term";
import { GLOSSARY_KEYS, type GlossaryKey } from "./glossary";

/**
 * Rich-text tag handlers for `t.rich(key, glossaryTags)`: a message can wrap jargon
 * as `<repo>code repositories</repo>` and it renders as a `<Term k="repo">` hover.
 */
export const glossaryTags: Record<GlossaryKey, (chunks: ReactNode) => ReactNode> =
  Object.fromEntries(
    GLOSSARY_KEYS.map((k) => [
      k,
      (chunks: ReactNode) => <Term k={k}>{chunks}</Term>,
    ])
  ) as Record<GlossaryKey, (chunks: ReactNode) => ReactNode>;
