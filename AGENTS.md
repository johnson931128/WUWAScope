# AGENTS.md — WUWAScope Development Contract

- **Repository:** https://github.com/johnson931128/WUWAScope
- **Project:** WUWAScope — Interactive Distributed Systems Desktop Simulation Lab
- **Target platform & stack:** Local cross-platform desktop app built with **C# (.NET) + Avalonia UI**
- **Language & writing style:** Write all documentation and UI copy in **plain, approachable English**. Avoid walls of dense jargon — whenever a technical term is needed (such as *deterministic*, *heartbeat timeout*, or *stale result*), explain what it means in everyday words.

---

## 1. Read This Before Coding

1. Read `README.md` and `docs/PRODUCT_SPEC.md` in full. Follow the product design and MVP acceptance criteria.
2. Inspect the current repository before making changes. Never assume a file, command, package, or test already exists.
3. Preserve user edits, project intent, and clean git history. Prefer small, easy-to-review changes.
4. The first goal is a working **local desktop simulator**, **not** a web app, a static architecture poster, or a fake server monitoring dashboard.
5. Do not quietly expand scope into cloud hosting, Kubernetes, production monitoring, or real distributed backend services.

---

## 2. Non-Negotiable Product Principles

- **Three zoom levels (Cluster → Node → Module):** Clicking into a machine or module must visibly update the workspace and always provide a clear way back without losing your simulation state.
- **Animation follows real simulation state:** Every moving message, status badge, queue item, log line, and metric must come directly from the simulation state or event log. Never fake visual activity with unrelated UI timers.
- **Repeatable runs (Deterministic simulation):** Given the same scenario settings, random seed, and user actions, the simulator must produce the exact same event order and final results every time.
- **Clear cause and effect:** Every meaningful state change should record an event ID, simulated timestamp, source, target (if any), event type, cause ID, and payload.
- **Failures are first-class citizens:** Show crashes, recoveries, delayed/dropped messages, and heartbeat timeouts honestly step by step. Never show instant "magical" failover.
- **Honest labeling:** Clearly mark all simulated numbers as **Simulated**. Never pretend local simulated nodes are real OS processes, containers, or measured servers.
- **Narrow and accurate first:** Build one complete, rock-solid scenario end to end before adding many shallow features.
- **Original design:** Use the reference web app for step-by-step storytelling inspiration only — never copy its assets, code, or layout pixel-for-pixel.

---

## 3. Authoritative Theory Reference

All formal distributed-systems theory lives in this external repository:

https://github.com/johnson931128/Distributed-System

Verified existing notes:
- Notes index: https://github.com/johnson931128/Distributed-System/blob/main/notes/README.md
- Course foundations: https://github.com/johnson931128/Distributed-System/blob/main/notes/00-course-introduction.md
- Logical Clocks & Snapshots: https://github.com/johnson931128/Distributed-System/blob/main/notes/01-logical-clocks-checkpointing.md
- Fault Tolerance, Paxos & Raft: https://github.com/johnson931128/Distributed-System/blob/main/notes/02-fault-tolerance-paxos-raft.md

Rules:
1. Check that a referenced note actually exists and read the relevant section before claiming the simulator follows that note.
2. Clearly separate course notes, original research papers, and WUWAScope's simplified teaching models. Always state what was simplified.
3. Never invent formulas, citations, page numbers, notebook links, or fake progress.
4. Do not copy large blocks of text from the theory repo into WUWAScope — link to it instead.
5. Topics not yet covered in published notes may be listed as planned, never presented as verified.
6. Protocol logic must be accurate and backed by documented rules (invariants). Pretty visuals with broken logic fail acceptance.

---

## 4. UI / UX Direction

Interaction inspiration: https://designed-to-scale.vercel.app/#/request-lifecycle/3

Design a clean, modern, desktop-first engineering app in **Avalonia UI**:
- **Top bar:** Scenario title, `SIMULATED` badge, playback controls (`Play`, `Pause`, `Step`, `Reset`, `Speed`), and failure triggers.
- **Left sidebar:** Scenario list and machine outline.
- **Center canvas:** Interactive zoom/pan diagram for the Cluster and Node internals, with animated message paths.
- **Right inspector:** Live details for the selected Cluster, Node, or Module (status, queues, counters, and plain-English explanations).
- **Bottom timeline:** Ordered event log with filters and click-to-inspect details.
- Moving from **Cluster → Node → Module** should feel like zooming inside the system, not opening disconnected windows.
- Use clear, distinct labels and icons (not color alone) for `Idle`, `Queued`, `Processing`, `Healthy`, `Suspected`, `Failed`, and `Recovering`.
- Support keyboard navigation, clear focus indicators, readable contrast, crisp HiDPI vector rendering, and graceful resizing on smaller desktop windows.

---

## 5. Technical Architecture (C# + Avalonia UI)

Stack: **C# (.NET)** + **Avalonia UI** (MVVM pattern) + **xUnit** for automated tests. Keep third-party NuGet dependencies minimal and justified by real features.

Separate responsibilities cleanly across projects/folders:
- **Simulation engine (`WUWAScope.Simulation`):** Pure C# class library containing the discrete-event queue, simulated clock, domain models, state transitions, and scenario definitions. **Zero references to Avalonia, UI threads, or rendering.**
- **Desktop application (`WUWAScope.App`):** Avalonia Views (XAML), ViewModels (mapping engine state to screen state), interactive canvas controls, toolbar, inspector, and timeline.
- **Tests (`WUWAScope.Simulation.Tests` & `WUWAScope.App.Tests`):** Automated unit tests for simulation correctness and ViewModel/UI interactions.
- **Future adapters (Optional):** Any future live process ingestion must live in a separate adapter project, never mixed into the simulation engine.

Engine design rules:
- Use stable IDs for jobs, tasks, nodes, messages, events, and attempts.
- Process queued events in a strict, deterministic order: `(SimulatedTimeMs, SequenceNumber)`.
- Keep simulation logic independent of wall-clock time and UI frame rate.
- Playback speed changes how fast the visual playback clock runs — never the causal order or outcome.
- Avoid hidden in-place mutations that corrupt event history or break repeatable replays.
- Support clean `Reset()` and deterministic replay (a full reverse-time engine is not required for MVP).
- State message delivery rules explicitly (delayed, dropped, retried). Never claim "exactly-once execution" without a real mechanism.
- Use strongly typed C# models (`record`, `enum`, explicit state transitions) — no scattered magic strings in UI files.
- Never display made-up CPU, RAM, or real latency labels; mark all metrics **Simulated** and make their formulas clear.

---

## 6. First Scenario and Core Invariants

MVP topology: **1 Client, 1 Coordinator, 3 Workers** (all running locally inside the C# simulator).

- **Happy path:** Submit job → enqueue task → pick healthy worker → dispatch → execute → return result → update coordinator → reply to client.
- **Failure path:** Worker crashes (or message is delayed) → missed heartbeats → coordinator timeout → mark attempt lost → put task back in queue → assign new attempt to healthy worker → complete.

Rules that must always hold in the simulation:
1. At most one accepted final result per task.
2. An outdated completion from an older attempt cannot overwrite a newer accepted result (tag attempts with generation/attempt IDs).
3. A crashed worker does not run or receive new tasks until recovered.
4. Displayed pending / running / completed counts always match engine state.
5. Pausing stops simulated time; stepping advances exactly one event.
6. Reset restores the exact same starting state for the same scenario and seed.
7. Every state change traces back to a logged event.

*(These are guarantees of our simulation model, not claims about real-world networks.)*

---

## 7. Implementation Order

- **Phase 0 (Interactive Shell):** Build a runnable Avalonia desktop app with the five-region layout, interactive 5-node cluster diagram, clickable Cluster → Node → Module drill-down, inspector, and playback toolbar. Stub/sample data is allowed only when clearly labeled as prototype/stub and separated behind clean Phase 1 engine interfaces.
- **Phase 1 (First Playable MVP):** Replace stubs with the real pure-C# event engine, Task Scheduling scenario, smooth state-driven message animations, event timeline, worker crash/recovery, message delay/drop, and stale-result rejection.
- **Phase 2 (Theory-Linked Lessons):** Add scenarios such as Logical Clocks, Global Snapshots, and Leader Election after verifying the corresponding notes in `Distributed-System` and writing invariants + tests.

Never claim a phase is finished until its acceptance criteria pass. Never create empty placeholder files for unbuilt features.

---

## 8. Quality, Testing, and Verification

- Enable C# nullable reference types (`<Nullable>enable</Nullable>`) and treat warnings seriously. Keep module boundaries clean and dependencies lean.
- Write deterministic xUnit tests for event ordering, reset/replay, retries, heartbeat timeouts, stale results, and worker recovery.
- Write ViewModel / Avalonia UI tests for node selection, Cluster → Node → Module navigation, pause/step, timeline selection, and fault controls.
- Before claiming work is complete, run real commands (`dotnet build`, `dotnet test`) and fix any failures (or clearly report real blockers).
- Exercise at least one full happy path and one failure path in the running desktop app and report actual verified results.
- Keep `README.md` and `docs/PRODUCT_SPEC.md` aligned with the real code and working commands.
- Automatically commit and push changes (`git add`, `git commit`, `git push`) after completing and verifying each task. Use small, clear commit messages. Never force-push, discard unrelated changes, or fabricate command output.
- After changes, summarize modified files, what works, exact commands/tests run, and known limitations.

---

## 9. How to Collaborate

Before any major change, share a short plan listing the files to create/edit, expected behavior, and how we will test it. Then build the smallest working vertical slice first. If a detail is unspecified, pick a simple, reversible option, explain the assumption, and keep moving.

Current priority: build **Phase 0 first** as a clean **C# + Avalonia UI** desktop app, while setting up the real C# interfaces needed for Phase 1. Do not fake a finished simulator just for a pretty screenshot.
