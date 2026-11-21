# AGENTS.md — WUWAScope Development Contract

Repository: https://github.com/johnson931128/WUWAScope
Project: Interactive Distributed Systems Visualization & Simulation Lab
Primary language for documentation and UI copy: Traditional Chinese (zh-TW), with accurate American English technical terminology.

## 1. Read this before coding

1. Read README.md and docs/PRODUCT_SPEC.md in full. Follow the product definition and MVP acceptance criteria.
2. Inspect the existing repository before editing. Never assume a feature, command, dependency, test, or backend exists.
3. Preserve human-written changes, user-facing flow, and project intent. Prefer small, reviewable modifications.
4. The first implementation goal is a working browser-based interactive simulator, **not** a static architecture drawing or a generic server monitoring dashboard.
5. Do not silently expand scope into real production monitoring, Kubernetes integration, cloud deployment, or a fully distributed backend.

## 2. Product principles (non-negotiable)

- **Three levels of inspection:** Cluster → Node → Module. Every selection must visibly change the inspector content and preserve a way back.
- **Animation follows state:** all displayed message movement, status badges, queues, log entries, and metrics derive from simulation state or explicit trace records. Do not drive visual animations from unrelated timers.
- **Deterministic simulation:** given scenario configuration, seed and user action sequence, event order and outputs must be reproducible.
- **Explainable causality:** every meaningful transition should have an event ID, simulated timestamp, origin, destination (when applicable), event type, and payload/metadata.
- **Failure is a first-class scenario:** simulate crashes, recovery, dropped/delayed messages and timeouts with explicit effects. Never show instant magical failover.
- **Honest modes:** label simulated data as simulated. Do not imply that browser-only nodes are actual processes, containers or measured servers.
- **Progressive fidelity:** implement a narrow, correct scenario end-to-end before adding numerous shallow features.
- **Original design:** use the reference app as interaction inspiration, not as material to copy.

## 3. Authoritative theory source

The external **Distributed-System** repository is the project's canonical educational reference:

https://github.com/johnson931128/Distributed-System

Existing references:
- Notes index: https://github.com/johnson931128/Distributed-System/blob/main/notes/README.md
- Course foundations: https://github.com/johnson931128/Distributed-System/blob/main/notes/00-course-introduction.md
- Logical Clocks / Snapshots: https://github.com/johnson931128/Distributed-System/blob/main/notes/01-logical-clocks-checkpointing.md
- Fault Tolerance / Paxos / Raft: https://github.com/johnson931128/Distributed-System/blob/main/notes/02-fault-tolerance-paxos-raft.md

Rules:
1. Verify that a referenced note actually exists and read the relevant content before claiming the simulator implements an algorithm according to that note.
2. Distinguish the course notes, external original papers, and WUWAScope's own simplified teaching models. State simplifications and assumptions explicitly.
3. Never invent formulas, paper citations, lecture page numbers, notebook paths, or implementation progress.
4. Do not modify or copy large sections from the theory repository into WUWAScope. Link to the canonical source.
5. A topic absent from the published theory notes may be listed as planned but not presented as already validated against the course.
6. Use correct protocol semantics and document invariants. A visually pleasing but logically incorrect simulation fails acceptance.

## 4. UI/UX direction

Interaction inspiration: https://designed-to-scale.vercel.app/#/request-lifecycle/3

Use a clean, modern, engineering-oriented, desktop-first interface:
- Top: scenario title and playback controls (Play, Pause, Step, Reset, Speed).
- Left: lesson/scenario navigation and cluster outline.
- Center: primary interactive topology canvas, zoom/pan, animated message paths.
- Right: contextual inspector for the selected node/module, states, queues, counters and explanations.
- Bottom: ordered event timeline with filtering and an inspectable event detail view.
- Make Cluster → Node → Module feel like diving into the system, not opening unrelated pages.
- Clear semantic distinction between healthy, waiting, processing, failed, and recovering states.
- Keyboard access, focus indication, readable contrast, reduced-motion consideration, and responsive layout.
- Avoid UI libraries or visual patterns that obscure event ownership or causality.
- Do not reproduce proprietary assets, source code, screenshots, or layouts pixel-for-pixel.

## 5. Technical architecture

Recommended initial stack: React + TypeScript + Vite, with a graph/flow library such as @xyflow/react if useful. Other dependencies must be justified by actual features.

Separate these concerns:
- **Simulation engine:** pure state transitions, discrete event queue, simulated clock, events and deterministic scheduling; no React or DOM references.
- **Scenario definitions:** initial topology, node configuration, workload, failure settings and assertions.
- **View models/selectors:** map engine state to diagram nodes, messages, inspectors and metrics.
- **UI components:** canvas, controls, inspector, timeline and scenario navigation.
- **Optional future adapters:** real service/event ingestion must remain separate from simulation.

Engine requirements:
- Use stable IDs for jobs, tasks, nodes, messages, events and attempts.
- Process the next event in an explicit stable order: simulation timestamp, then sequence number.
- Keep core simulation independent of wall-clock time and React animation lifecycle.
- Playback speed affects event presentation and progression, not the causal result.
- Avoid in-place mutations that corrupt event history or reproducibility.
- Support reset and deterministic replay; a full arbitrary reverse-time engine is not necessary for MVP.
- Define delivery semantics for each scenario (e.g., delayed/lost/retried messages). Do not claim exactly-once execution without a supporting design.
- Use typed domain models and explicit state machines; no magic string transitions dispersed across UI files.
- Do not use real CPU/RAM/latency labels for fabricated measurements; mark synthetic numbers and document calculation logic.

## 6. First scenario and invariants

MVP: 1 Client, 1 Coordinator, 3 Workers, and an optional logical result store. A browser simulation is sufficient.

A correct happy path must show: submit job → enqueue task → choose worker → dispatch → execute → completion → update coordinator → respond.

Failure path must show: worker crash → missed heartbeat / timeout → mark attempt lost → requeue → dispatch to healthy worker → completion.

Invariants:
- At most one accepted terminal result for each task.
- A stale completion from an old attempt cannot overwrite a newer accepted result; tag attempts with generation/attempt IDs.
- A failed worker does not execute or receive new tasks until recovered.
- The displayed pending/running/completed task counts agree with engine state.
- Pausing stops simulation advancement; stepping advances exactly one event.
- Reset restores the same initial state with the same scenario and seed.
- Every state mutation is traceable to a logged event.

These are *simulation-model guarantees*, not claims about exactly-once execution in real networks.

## 7. Implementation order

**Phase 0:** Build a functioning shell with route/layout, interactive cluster diagram, selectable nodes/modules, inspector and playback toolbar. Stub data is allowed only when clearly labeled and isolated from the real engine.

**Phase 1 (first playable MVP):** Implement typed event engine + job dispatch scenario + message animation + event timeline + worker crash and reassignment. Replace temporary stubs with engine-backed state.

**Phase 2:** Add theory-linked scenarios such as logical clocks, snapshots, and leader election after validating the source material and writing scenarios/tests.

Avoid claiming a phase is finished unless its acceptance criteria pass. Do not generate empty files for imagined implementations.

## 8. Quality, testing and delivery

- Prefer TypeScript strict mode, clear module boundaries, reusable components, semantic naming, and minimal dependencies.
- Add deterministic unit tests for scheduling, event ordering, reset, retry, timeout, stale results and failure recovery.
- Add UI interaction tests for node selection, inspector drill-down, pause/step, timeline selection and fault controls.
- Before declaring success: run the real install, typecheck, test and build commands defined by the repository; fix failures or report actual blockers.
- Test at least one complete happy path and one failure path in the running UI. Record actual verification, not assumptions.
- Keep README.md and docs/PRODUCT_SPEC.md aligned with the implementation and supported commands.
- Prefer small commits with clear descriptions. Never force-push, discard unrelated changes or fabricate terminal results.
- After changes, summarize modified files, what works, exact tests performed, and known limitations.

## 9. Collaboration expectations

Before any large change, provide a short plan: affected files, intended behavior and acceptance tests. Then implement the smallest vertical slice that delivers visible user value. If requirements are under-specified, choose a reversible, simple design, document the assumption, and avoid blocking progress.

The current handoff is to build **Phase 0 first**, while establishing the necessary engine interfaces for Phase 1; do not fabricate a finished simulation just to create an attractive screenshot.