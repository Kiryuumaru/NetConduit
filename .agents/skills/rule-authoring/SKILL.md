---
name: rule-authoring
description: Use only when creating or editing AGENTS.md or .agents/skills SKILL.md instruction files. Enforces imperative voice and formatting conventions.
---

# Rule Authoring

Write and maintain instruction files contributors actually follow. Contributor-only: load this skill ONLY when creating or editing `AGENTS.md` or `.agents/skills/<name>/SKILL.md`. For all other work, stay unloaded.

## 1. File Targets and Formats

- MUST write repo-wide always-on rules in `AGENTS.md`: plain Markdown, no frontmatter, no required fields, imperative MUST/NEVER voice throughout.
- MUST write skills in `.agents/skills/<name>/SKILL.md` per the Agent Skills open standard: YAML frontmatter with `name` and `description` is REQUIRED, body under 500 lines.
- MUST name skill directories in kebab-case matching the frontmatter `name` (e.g. `disciplined-fix`, `docs-sync`, `scope-guard`).
- MUST NEVER use Copilot-style frontmatter (`applyTo`) or `{topic}.instructions.md` naming — those belong to the retired `.github/instructions/` format.
- MUST keep `AGENTS.md` lean and always-on; MUST place specialized, load-on-demand guidance in skills, never in `AGENTS.md`.

## 2. Writing Voice

Rules use imperative, declarative voice:

- MUST use short sentences with no filler words
- MUST state the rule, not the reasoning — no explanations unless required for understanding
- MUST prefer the keyword forms in the table below over soft phrasing

| Avoid | Prefer |
|---|---|
| "You should always use..." | "MUST use..." |
| "It's recommended to..." | "MUST..." |
| "Try to avoid..." | "NEVER..." |
| "In most cases..." | State the rule directly |

## 3. Keywords

MUST use uppercase keywords with these meanings:

| Keyword | Meaning |
|---|---|
| `MUST` | Mandatory requirement |
| `MUST NOT` | Absolute prohibition |
| `NEVER` | Absolute prohibition (stronger emphasis) |
| `MAY` | Optional, permitted |
| `USE` | Recommended approach |
| `PREFER` | Preferred but not mandatory |

## 4. Bullets

- MUST start each rule bullet with a keyword (`MUST`, `NEVER`, `MAY`)
- MUST put one rule per bullet with no trailing period
- MUST keep parallel structure within each list

Example:

```markdown
Channel handling:
- MUST validate frame headers at the boundary
- MUST NOT allocate past slab limits
- MAY batch small writes
```

## 5. Tables, Code, Diagrams

- MUST use tables for supporting information ONLY — mappings, reference data, comparisons. MUST NEVER embed prescriptive MUST/NEVER rules ONLY as table cells with no governing bullet; rules live in bullets. Carve-out: reference and enumeration tables that LIST already-stated prohibitions (e.g. forbidden-pattern catalogs, category tables) are allowed
- MUST give every table a header row, default-left alignment, and concise cell content
- MUST use fenced code blocks with a language identifier for examples; MUST show minimal focused examples with no placeholder or ellipsis code and no comments except on non-obvious behavior
- MUST draw diagrams with plain ASCII only (`-`, `|`, `+` for boxes; `^`, `v`, `<-`, `->` for arrows). MUST NEVER emit decorative box-drawing or emoji glyphs that render as garbled characters — if a diagram cannot be drawn in plain ASCII, MUST describe it in words instead

## 6. Section Patterns

MUST shape sections after these patterns, retargeted to NetConduit concepts (never the retired Domain/Application layer examples):

Prohibition section:

```markdown
## Prohibited Patterns

- NEVER add auth to the mux — it belongs to transport or application
- NEVER reference `investigate/` paths in public issue or PR bodies
- NEVER invent unit tests asserting documentation content
```

Required-approach section:

```markdown
## Required Approach

- MUST run `dotnet build` with 0 warnings, 0 errors before commit
- MUST place core mux tests in `tests/NetConduit.UnitTests/`
- MUST cite `docs/concepts/scope.md` for scope decisions
```

Placement/mapping section (supporting info in a table, rules as bullets above it):

```markdown
## Test Placement

- MUST place each test in its conventional project

| Area | Location |
|---|---|
| Core | `tests/NetConduit.UnitTests/` |
| Transit | `tests/NetConduit.Transit.<Name>.UnitTests/` |
| Transport | `tests/NetConduit.Transport.<Name>.IntegrationTests/` |
```

## 7. Content Principles

1. MUST keep rules atomic — one concept per bullet or section
2. MUST NOT repeat a normative rule across files — state each rule once, in one owner skill, and cross-reference by skill name and section title (e.g. "see `dotnet-workflow` Test Categorization section"). A consumer MAY carry at most ONE summary line naming the rule plus the owner pointer (e.g. per `github-workflow` closing-keyword rules). Beyond one line is a violation
3. MUST cross-reference another file by skill name + section title, NEVER by section number alone — numbers rot on insert, titles survive
4. MUST show concrete NetConduit examples, not abstract advice
5. MUST use consistent terminology — same term for the same concept everywhere
6. MUST favor scannable bullets and tables over paragraphs

## 8. Anti-Patterns

- MUST NEVER use passive voice ("should be used")
- MUST NEVER hedge ("usually", "generally", "often")
- MUST NEVER explain why unless critical to understanding
- MUST NEVER use numbered lists for unordered items
- MUST NEVER nest bullets more than 2 levels
- MUST NEVER write a paragraph when a table works
- MUST NEVER repeat a normative rule across files beyond one summary line with owner pointer
- MUST NEVER document features, paths, or behaviors not verified against the repo with tools
