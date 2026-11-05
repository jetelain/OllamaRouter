---
name: ncf-backlog-migrator
version: 1
description: Migrates external project backlogs, roadmaps, and TODOs into cohesive, PR-sized NightCodeForge tasks in docs/tasks with YAML frontmatter and database sync.
---

# >>> nightcodeforge (managed — do not edit between markers) >>>
# Backlog Migration Skill (`ncf-backlog-migrator`)

## 1. Executive Role & Objective
This skill transforms external, unstructured, or semi-structured backlog artifacts into clean, execution-ready, PR-sized **NightCodeForge** task markdown files in `docs/tasks/`.

It is designed for **external AI harnesses** (Antigravity IDE, Claude Code, Cursor, or command-line agents) operating directly on the workspace repository.

It ingests items from:
- `ROADMAP.md`, `TODO.md`, `CHANGELOG.md`
- Issue directories (`issues/*.md`), GitHub issue exports (markdown or JSON dumps)
- Architecture Decision Records (ADRs) with pending action items
- Meeting notes, scratchpads, or customer feedback dumps

---

## 2. Managing Task Files in `docs/tasks/`

NightCodeForge manages tasks as markdown files located under:
```
docs/tasks/
├── 2026-10-20-implement-ollama-health-check.md
├── 2026-10-20-configure-model-retry-policy.md
└── archive/
```

### Filename Convention
- Newly authored tasks should be named `docs/tasks/{yyyy-MM-dd}-{slug}.md` or simply `docs/tasks/{slug}.md` (e.g. `docs/tasks/ollama-health-check.md`).
- NightCodeForge canonicalizes the filename upon synchronization to `{yyyy-MM-dd}-{slug}.md`.
- Completed or cancelled tasks are eventually moved to `docs/tasks/archive/`.

---

## 3. Markdown Task Specification & YAML Frontmatter Schema

Each task in `docs/tasks/` consists of a strictly validated YAML frontmatter block followed by markdown sections.

### YAML Frontmatter Contract

```yaml
---
title: "Implement Ollama model health check endpoint"
priority: 3
merge_mode: "manual_review_required"
base_branch: "main"
depends_on: []
---
```

#### Field Reference:
- `title` *(string, required)*: Clear, imperative statement describing the task capability.
- `priority` *(integer, optional, default: 3)*: Priority level from 1 to 5:
  - `1`: **Critical / Urgent (P1)** — Immediate blockers, hotfixes, critical bugs.
  - `2`: **High (P2)** — Foundational architectural prerequisites, core milestones.
  - `3`: **Normal (P3)** — Standard feature development (default).
  - `4`: **Low (P4)** — Minor enhancements, secondary optimizations, polish.
  - `5`: **Lowest / Trivial (P5)** — Cosmetic refactorings, typos, background cleanup.
- `merge_mode` *(string, optional, default: "manual_review_required")*:
  - `"manual_review_required"`: Requires human maintainer approval before merging to base branch.
  - `"automatic"`: Automatically merges to base branch once all pipeline stages, tests, and SAST gates pass.
- `base_branch` *(string, optional, default: workspace default, typically "main")*: Target Git branch for the feature branch to branch from and merge into.
- `depends_on` *(list of integers or strings, optional, default: `[]`)*: Prerequisite tasks that must complete before this task can start. Supports numeric IDs (`[ 101 ]`), markdown filenames (`[ "2026-10-20-task-alpha.md" ]`, `[ "task-alpha.md" ]`), or task slugs/titles (`[ "task-alpha" ]`, `[ "Task Alpha" ]`). Upon synchronization, NightCodeForge resolves string references to database IDs and normalizes the markdown file in-place to canonical integer IDs.
- `id` *(FORBIDDEN on creation)*: **DO NOT include this field when authoring new tasks.** NightCodeForge assigns it automatically upon sync and writes it back to disk. Providing an explicit `id` causes database collisions, overwrites existing tasks, or triggers duplicate intake errors.
- `status` *(FORBIDDEN on creation)*: **DO NOT include this field when authoring new tasks.** New tasks automatically enter `Suggested` status. Status transitions must occur through workflow gates, not manual editing.

### Required Markdown Body Structure

```markdown
# <Imperative Task Title>

## 📋 Requirements & Description

### Summary
<Concise explanation of the user or system value delivered by this task.>

### Implementation Checklist
- [ ] <Sub-task 1: Concrete technical item to implement>
- [ ] <Sub-task 2: Concrete technical item to implement>
- [ ] <Sub-task 3: Fast unit tests implementation>

### Affected Files
- `<path/to/source.ext>`: <Short description of intended changes>
- `<tests/path/to/test.ext>`: <Unit test assertions>

### Acceptance Criteria
- [ ] <Verifiable criterion 1: expected behaviors, status codes, payload shapes>
- [ ] <Verifiable criterion 2: error handling and domain exceptions>
- [ ] <Verifiable criterion 3: deterministic unit test verification>

### Prerequisite Dependencies
- <None, or descriptive list of prerequisite tasks>
```

---

## 4. Automatic Database Synchronization

NightCodeForge maintains continuous bidirectional synchronization between disk files and its internal SQLite database via `TaskMarkdownSyncService`:

1. **Automatic Intake:**
   - A real-time `FileSystemWatcher` monitors `docs/tasks/*.md`.
   - When you create a new markdown file in `docs/tasks/`, NightCodeForge detects it, creates a corresponding `ForgeTask` record in the database with status `Suggested`, registers its dependencies, and allocates a unique numeric ID.
   - NightCodeForge rewrites the markdown file in-place to record the assigned `id: <allocated_id>` and canonical name.
2. **Synchronized Modifications:**
   - Edits made on disk to `title`, `priority`, `merge_mode`, `base_branch`, `description`, or `depends_on` are immediately detected and applied to the database.
   - Each modification generates an immutable temporal audit snapshot (`ForgeTaskHistory`) recording the change.
3. **Bidirectional Reflection:**
   - Changes occurring inside the NightCodeForge application (workflow execution progress, agent status transitions, stage logs, human Q&A clarifications) are automatically rendered back into the markdown file under `## ❓ Human Clarifications (Q&A)`.

---

## 5. Managing Dependencies

Dependencies define the execution DAG (Directed Acyclic Graph) for tasks:

1. **Prerequisite Semantics:**
   - A task cannot enter the active dev queue or run until all tasks listed in its `depends_on` have completed and merged.
2. **Referencing Existing Tasks:**
   - Use numeric database IDs: `depends_on: [ 101, 102 ]`.
   - Or reference existing tasks by file slug or title: `depends_on: [ "core-database-setup", "OAuth Authentication" ]`.
3. **Flexible Batch Authoring:**
   - When creating a batch of interdependent tasks from scratch, external harnesses and human developers do **not** need to wait for database IDs to be assigned.
   - Simply reference prerequisite tasks by filename (`"task-alpha.md"`), slug (`"task-alpha"`), or title (`"Task Alpha"`).
   - NightCodeForge analyzes the batch, orders task creation topologically so prerequisites receive IDs first, links the relationships in the database, and rewrites the markdown files in-place with normalized numeric IDs.
4. **Validation & Cycle Prevention:**
   - NightCodeForge strictly validates that all referenced dependencies exist within the workspace or within the pending batch.
   - Circular dependencies (`A -> B -> A`) and self-references (`A -> A`) are detected by cycle detection (`HasCycleDfs`) and rejected with a `TaskMarkdownSyncException`.

---

## 6. Sizing & Granularity Principles
When converting raw backlog items into tasks:

1. **One Task = One Complete PR/MR:**
   - Every task must be implementable, compilable, and testable within a single automated pipeline pass.
   - The result must leave the codebase clean, passing tests, and ready to deploy.
2. **No Micro-Tasks:**
   - Do NOT emit standalone tasks for trivial changes such as "Add configuration key", "Create empty interface", or "Fix typo in comment". Group related items into a cohesive feature task.
3. **No Monolithic Epics:**
   - Break large multi-month epics into logical milestones where each milestone compiles, has passing tests, and delivers incremental progress.

---

## 7. Migration Step-by-Step Workflow

1. **Scan Source Backlog:** Read existing roadmap files (`ROADMAP.md`, `TODO.md`, `issues/`) or review developer-provided backlog notes.
2. **Inspect Existing Tasks:** List `docs/tasks/*.md` to avoid duplicating tasks already tracked in the repository.
3. **Decompose & Group:** Cluster micro-items into feature tasks; split oversized epics into PR-sized milestones.
4. **Determine Dependencies:** Map out the sequence of prerequisites and assign priorities (P1–P5).
5. **Write Markdown Files:** Create files directly under `docs/tasks/` conforming to the YAML frontmatter and body specification.
6. **Verify Sync:** Check that NightCodeForge has picked up the files and stamped them with their assigned `id:`.
# <<< nightcodeforge <<<
