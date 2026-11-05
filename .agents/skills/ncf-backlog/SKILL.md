---
name: ncf-backlog
version: 1
description: Backlog management (ideation, planning, and non-blocking follow-up task creation) for docs/tasks with YAML frontmatter and database sync.
---

# Backlog Management Skill (`ncf-backlog`)
*Ideation, Planning, and Follow-up Task Management for NightCodeForge*

## 1. Executive Role & Objective
This skill provides comprehensive backlog management for **NightCodeForge** repositories. It is designed for **external AI harnesses** (Antigravity IDE, Claude Code, Cursor, or CLI agents) operating directly on the workspace repository.

It covers three core modes of operation:
1. **Ideation & Architecture:** Guided interactive brainstorming, technical discovery, and architectural decomposition for new features, subsystems, or refactorings.
2. **Planning & Sequencing:** Structuring execution DAGs, establishing milestone priorities (P1–P5), and linking prerequisite dependencies (`depends_on`).
3. **Non-Blocking Follow-up Creation:** Enabling agents and developers actively working on a task to capture non-blocking improvements, deferred technical debt, secondary edge cases, or optimizations without derailing current task scope.

All backlog items are authored as well-scoped, PR-sized **NightCodeForge** markdown task files stored under `docs/tasks/`, where they are automatically synchronized into the NightCodeForge database.

---

## 2. Managing Task Files in `docs/tasks/`

NightCodeForge manages project tasks as markdown files located under:
```
docs/tasks/
├── 2026-10-20-implement-model-latency-tracker.md
├── 2026-10-20-add-sliding-window-percentiles.md
└── archive/
```

### File Naming & Organization
- Name new task files `docs/tasks/{yyyy-MM-dd}-{slug}.md` or `docs/tasks/{slug}.md` (e.g. `docs/tasks/model-latency-tracker.md`).
- NightCodeForge canonicalizes filenames to `{yyyy-MM-dd}-{slug}.md` upon synchronization.
- When tasks are completed or cancelled, NightCodeForge moves them to `docs/tasks/archive/`.

---

## 3. Markdown Task Specification & YAML Frontmatter Schema

Each task created under `docs/tasks/` must contain valid YAML frontmatter followed by structured requirements.

### YAML Frontmatter Schema

> [!CAUTION]
> **NEVER provide an `id` field when creating new tasks.**
> Task IDs are allocated and managed exclusively by the NightCodeForge database. Manually guessing, assigning, or copy-pasting an `id` into new task markdown causes database collisions, overwrites existing task baselines, or creates duplicate tasks. Always omit `id` entirely when creating new tasks. NightCodeForge automatically assigns the next available unique numeric ID and rewrites the frontmatter in-place upon ingestion.
> 
> Similarly, **never provide `status`**, **`version`**, or **`content_hash`** on creation.

```yaml
---
title: "Implement ModelLatencyTracker in OllamaRouter"
priority: 3
merge_mode: "manual_review_required"
base_branch: "main"
depends_on: []
---
```

#### Schema Field Definitions:
- `title` *(string, required)*: Clear, imperative capability title (e.g. `Implement ModelLatencyTracker in OllamaRouter`).
- `priority` *(integer, optional, default: 3)*: Priority level from 1 to 5:
  - `1`: **Critical / Urgent (P1)** — Blockers, hotfixes, critical security bugs.
  - `2`: **High (P2)** — Foundational architectural prerequisites and core milestones.
  - `3`: **Normal (P3)** — Standard feature development and planned capabilities (default).
  - `4`: **Low (P4)** — Minor enhancements, non-critical polish, follow-up optimizations.
  - `5`: **Lowest / Trivial (P5)** — Cosmetic cleanup, typos, secondary maintenance.
- `merge_mode` *(string, optional, default: "manual_review_required")*:
  - `"manual_review_required"`: Requires maintainer sign-off in NightCodeForge Review Room before merging.
  - `"automatic"`: Automatically merges to base branch once pipeline stages, unit tests, and SAST checks pass.
- `base_branch` *(string, optional, default: workspace default, e.g. "main")*: Target Git branch for branching and merging.
- `depends_on` *(list of integers or strings, optional, default: `[]`)*: Prerequisite tasks that must complete first. Supports numeric IDs (`[ 101 ]`), markdown filenames (`[ "2026-10-20-task-alpha.md" ]`, `[ "task-alpha.md" ]`), or task slugs/titles (`[ "task-alpha" ]`, `[ "Task Alpha" ]`). Upon synchronization, NightCodeForge resolves string references to database IDs and normalizes the markdown file in-place to canonical integer IDs.
- `id` *(FORBIDDEN on creation)*: **DO NOT include this field when authoring new tasks.** NightCodeForge populates and manages `id` automatically upon ingestion. Including a manual `id` can overwrite existing tasks or cause duplicate ID errors.
- `status` *(FORBIDDEN on creation)*: **DO NOT include this field when authoring new tasks.** Newly created tasks automatically start as `Suggested`. Status transitions must occur through workflow gates, not manual editing.

### Markdown Body Specification

```markdown
# <Imperative Task Title>

## 📋 Requirements & Description

### Summary
<Concise explanation of the user or system value delivered by this task.>

### Implementation Checklist
- [ ] <Sub-task 1: Concrete technical item to implement>
- [ ] <Sub-task 2: Concrete technical item to implement>
- [ ] <Sub-task 3: Unit test implementation>

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

NightCodeForge maintains continuous bidirectional synchronization between markdown files in `docs/tasks/` and its database through `TaskMarkdownSyncService`:

1. **Automatic Intake & ID Allocation:**
   - A real-time `FileSystemWatcher` continuously monitors `docs/tasks/*.md`.
   - When a new markdown file is written, NightCodeForge creates a `ForgeTask` record in the database with status `Suggested`, registers dependencies, and allocates a unique numeric ID.
   - NightCodeForge rewrites the frontmatter in-place with `id: <allocated_id>`.
2. **Synchronized Modifications:**
   - Edits made on disk to `title`, `priority`, `merge_mode`, `base_branch`, `description`, or `depends_on` are immediately detected and synced to the database.
   - Each modification is recorded as an immutable temporal audit snapshot (`ForgeTaskHistory`).
3. **Bidirectional Reflection:**
   - Changes made inside NightCodeForge (workflow stage progression, status updates, agent execution logs, maintainer clarifications) are automatically reflected back into the markdown file under `## ❓ Human Clarifications (Q&A)`.

---

## 5. Managing Dependencies & Execution DAG

Dependencies define the execution DAG (Directed Acyclic Graph):

1. **Prerequisite Semantics:**
   - A task cannot enter the active queue or be scheduled until all tasks listed in its `depends_on` have completed and merged.
2. **Referencing Existing Tasks:**
   - Use numeric database IDs: `depends_on: [ 101, 102 ]`.
   - Or reference existing tasks by file slug or title: `depends_on: [ "core-database-setup", "OAuth Authentication" ]`.
3. **Flexible Batch Authoring:**
   - When decomposing a feature or planning a sequence of interdependent tasks, external harnesses and human developers do **not** need to wait for database IDs to be assigned.
   - Simply reference prerequisite tasks by filename (`"task-alpha.md"`), slug (`"task-alpha"`), or title (`"Task Alpha"`).
   - NightCodeForge analyzes the batch, orders task creation topologically so prerequisites receive IDs first, links the relationships in the database, and rewrites the markdown files in-place with normalized numeric IDs.
4. **Validation & Cycle Prevention:**
   - Referenced task IDs or names must exist within the workspace or within the pending batch.
   - Circular dependencies (`A -> B -> A`) and self-references (`A -> A`) are detected by cycle detection (`HasCycleDfs`) and rejected.

---

## 6. Sizing & Granularity Principles

1. **One Task = One Complete Pull Request:**
   - Every task represents a complete PR that builds deterministically, passes unit tests, passes SAST security gates, and undergoes code review.
2. **Never Create Micro-Tasks:**
   - Do NOT emit standalone tasks for trivial items (e.g. "Add config key", "Create empty interface", "Fix typo"). Group related sub-tasks into a cohesive feature task.
3. **Never Create Monolithic Epics:**
   - Break large features into milestone-sized PRs where each milestone compiles, has passing tests, and leaves the codebase in a healthy state.

---

## 7. Workflow Mode A: Interactive Ideation & Architecture

Use this mode when a developer wants to brainstorm, design, or decompose a brand new capability or major refactoring:

### Step 1: Codebase Exploration
- Examine repository layout, project manifests (`.csproj`, `package.json`, etc.), and `.nightcodeforge/config.json`.
- Inspect existing classes, domain models, services, and test patterns to identify reusable code and avoid duplication.
- Check existing `docs/tasks/` to ensure the proposed work does not overlap with existing tasks.

### Step 2: Interactive Interview
Engage the developer with focused, clarifying questions:
- *"What is the main user problem, capability, or architectural goal being targeted?"*
- *"Are there existing classes, abstractions, or services in the repository that should be extended rather than rebuilt?"*
- *"What test harness exists (unit tests, CLI invocation, integration tests) and what is the testing strategy?"*
- *"Are there specific non-functional constraints (e.g., performance budgets, thread safety, backward compatibility)?"*

### Step 3: Architectural Synthesis & Task Decomposition
Synthesize the requirements into sequential, PR-sized tasks following the contract in §3.

### Step 4: Write Task Markdown Files
Create the markdown files directly in `docs/tasks/` with YAML frontmatter (**WITHOUT an `id` or `status`**). NightCodeForge will immediately synchronize them into the database and assign task IDs.

---

## 8. Workflow Mode B: Planning & Dependency Structuring

Use this mode when organizing a multi-stage initiative or sequencing complex milestones:

1. **Layered Milestone Decomposition:**
   - **Milestone 1 (Base Domain/Contracts):** Domain models, value objects, core interfaces, domain exceptions, and unit tests (`priority: 2`).
   - **Milestone 2 (Infrastructure/Persistence):** Repositories, adapters, external service clients, data access, and contract unit tests (`priority: 2` or `3`, `depends_on: [ "Milestone 1" ]`).
   - **Milestone 3 (Presentation/API/CLI):** Endpoints, controllers, Blazor components, or CLI commands (`priority: 3`, `depends_on: [ "Milestone 2" ]`).
2. **Cross-Cutting Follow-ups:**
   - Non-critical optimizations, telemetry dashboards, or extended test suites can be scheduled with `priority: 4` and appropriate dependencies.

---

## 9. Workflow Mode C: Creating Non-Blocking Follow-up Tasks (Agent In-Flight Workflow)

This mode is designed for **agents and developers currently executing or reviewing a task** who uncover adjacent improvements, secondary edge cases, or technical debt.

### Scope Discipline: The "Do It Now vs File Follow-up" Matrix

Agents must avoid scope creep. When working on Task A, you will often spot other things that could be improved. Apply this strict decision matrix:

| Situation | Action | Rationale |
| :--- | :--- | :--- |
| **Direct Acceptance Criterion** | **Do It Now** | Required to satisfy the current task contract. |
| **Regression Caused by Current Task** | **Do It Now** | The current PR must leave tests passing and the codebase clean. |
| **Trivial 1–2 Line Fix in Touched File** | **Do It Now** | Extremely low risk and directly adjacent to modified code. |
| **Non-blocking Edge Case or Secondary Validation** | **File Follow-up Task** | Expands scope; deserve its own test suite and isolated review. |
| **Refactoring Adjacent Existing Code** | **File Follow-up Task** | Conflating refactoring with feature code complicates PR reviews and git bisect. |
| **Performance Optimization / Caching** | **File Follow-up Task** | Unless the current task explicitly has a latency SLA, optimize in a follow-up. |
| **Missing Test Coverage in Unrelated Areas** | **File Follow-up Task** | Captures testing debt without bloating the active PR. |
| **UI/UX Polish or Secondary Tooling** | **File Follow-up Task** | Nice-to-have items should not delay merging core capabilities. |

### Step-by-Step: How an Agent Files a Non-Blocking Follow-up Task

When you identify a non-blocking follow-up item while working on an active task:

1. **Formulate a Clear Capability Title:**
   - Use an imperative title: e.g., `Add sliding window percentile calculation to ModelLatencyTracker` or `Refactor OllamaProcessManager to use asynchronous process exit`.
2. **Determine Dependency Relationship:**
   - **Dependent Follow-up:** If the follow-up directly builds on top of the code being written in the current task, specify the current task as a prerequisite in `depends_on`:
     ```yaml
     depends_on: [ "2026-10-20-implement-model-latency-tracker.md" ]
     ```
   - **Independent Follow-up:** If the follow-up touches an adjacent subsystem and does not require the current task to merge first, keep `depends_on: []`.
3. **Assign Appropriate Priority:**
   - `priority: 3` (Normal): If it is a valuable planned follow-up or secondary feature.
   - `priority: 4` (Low): If it is an optional optimization, minor refactoring, or secondary polish item.
   - `priority: 5` (Lowest): If it is trivial cosmetic cleanup or comment clarification.
4. **Author the Markdown File Directly:**
   - Create `docs/tasks/{yyyy-MM-dd}-{slug}.md` (e.g. `docs/tasks/2026-10-20-add-sliding-window-percentiles.md`).
   - Populate the YAML frontmatter (**WITHOUT an `id` or `status`**, which NightCodeForge automatically assigns on intake) and all sections:
     - **Summary:** State clearly that this was identified during the execution of the parent task, explaining the rationale and technical value.
     - **Implementation Checklist:** Specific, actionable technical steps.
     - **Affected Files:** Paths expected to be modified or created.
     - **Acceptance Criteria:** Verifiable conditions and test requirements.
5. **Report to Maintainer / Task Transcript:**
   - In your task completion summary or review notes, mention the follow-up task you created:
     > *"Identified non-blocking improvement: Filed follow-up task [2026-10-20-add-sliding-window-percentiles.md](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/docs/tasks/2026-10-20-add-sliding-window-percentiles.md) with priority 4."*
