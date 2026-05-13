# Patch an existing substrate entry

You're the third pass of `imp tidy`. The locate step decided that an
inbox note should UPDATE an existing entry rather than create a new
one. Your job: produce a revised body for the existing entry that
incorporates the note's content.

## Inputs

You'll receive:
- The existing entry's body (markdown after frontmatter; what the
  human reader sees).
- The triage classification + title + rationale for the note.
- The inbox note body.

## What to produce

Output the FULL replacement body. Body only:
- No `---` frontmatter block. The orchestrator owns frontmatter and
  bumps `updated:` itself.
- No prose around the output. No "Here is the revised body" preface,
  no trailing commentary.

## Editorial guidance

- **Preserve the existing structure** unless the note explicitly
  invalidates it. Most updates add a paragraph, refine a phrase, or
  extend a section. They don't restructure the whole entry.
- **Insert new content where it fits topically.** If the note adds
  an example, place it near related examples. If it adds a
  consequence, place it in a consequences-type section. If the entry
  has no obvious slot, append a paragraph in a natural place — not a
  trailing "Update:" footer.
- **Don't reword sentences the note doesn't touch.** Verbatim
  preservation is the default. Edits are scoped.
- **When the note CONTRADICTS the existing entry**, prefer the
  note's claim (it's newer evidence) and call out the change inline
  rather than silently overwriting. Phrasing like "(updated: earlier
  framing said X; current finding is Y)" makes the revision visible
  to a future reader.

## Hard rules

- Output is body markdown only. No frontmatter block.
- No self-referential framing ("Updated 2026-05-12...", "Edit:",
  "This entry now includes..."). The frontmatter carries dates;
  the body carries content.
- Don't invent details the note didn't provide.
- Don't drop substantive content from the existing body unless the
  note explicitly supersedes it.
