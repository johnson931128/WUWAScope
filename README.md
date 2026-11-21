# WUWAScope

**An interactive desktop lab for seeing how distributed systems really work.**

WUWAScope is a local desktop app built with **C# and Avalonia UI**. Instead of reading static diagrams and guessing what happens inside a multi-machine system, you can run a live simulation, pause it, step through events one by one, break things on purpose, and look inside any machine to see how it reacts.

> **Project status:** **Phase 2 (Theory-Linked Distributed Systems Lessons) is complete and runnable locally.** All 4 interactive desktop scenarios run on pure C# deterministic discrete-event engines: **Distributed Task Scheduling**, **Logical Clocks (Lamport & Vector)**, **Global Snapshots (Chandy-Lamport)**, and **Leader Election (Teaching Raft)**. **Phase 3 (Advanced Lab & Experiments)** is planned next.

## What Makes WUWAScope Different

WUWAScope is not a fake server monitoring dashboard and not a static slideshow. It combines an **interactive system diagram**, a **step-by-step simulator**, and a **deep node inspector** in one local desktop window.

You can explore the system at **three zoom levels**:

1. **Cluster View (The Big Picture)** — See all machines working together (Client, Coordinator, Workers), their current health, and messages traveling between them.
2. **Node View (Inside One Machine)** — Click any machine to open it up and see its internal parts (such as Request Handler, Scheduler, Task Queue, Inbox, Executor, and Heartbeat Sender) and how data flows between them.
3. **Module View (Inside One Part)** — Click any internal part to inspect its current state, waiting queue, recent inputs/outputs, and event history. Different machine roles have different internal parts.

### Core Features

- **Play / Pause / Step / Reset / Speed:** Run the simulation continuously, freeze time, advance exactly one event at a time, restart from the same starting point, or change playback speed.
- **Visible message travel:** Watch messages leave one machine, travel across the network, arrive, get delayed, get dropped, or get retried — always in the exact order events happened.
- **Zoom in and out seamlessly:** Move from Cluster → Node → Module and back without losing your place or restarting the simulation.
- **Built-in failure testing:** Crash a worker, recover it, or delay/drop a message to see how the system detects problems and recovers step by step.
- **Event timeline:** Follow an ordered log of everything that happened, filter by type or machine, and click any entry to see what caused it.
- **Clear explanations:** Plain-language notes explaining *why* something happened, with links to the full course theory notes.
- **Honest simulated numbers:** All metrics are clearly labeled as **Simulated**. Nothing pretends to be real CPU, RAM, or live network measurements.

## Desktop Layout

WUWAScope is designed as a clean, desktop-first engineering workspace. Its step-by-step storytelling is inspired by [Designed to Scale — Request Lifecycle](https://designed-to-scale.vercel.app/#/request-lifecycle/3), while its UI, visual style, and interactive node-internals view are designed from scratch for a desktop app.

| Area | What It Shows |
| --- | --- |
| **Top toolbar** | Current scenario name, Simulated mode badge, Play / Pause / Step / Reset, speed control, and failure triggers |
| **Left sidebar** | Scenario list, machine outline, and short concept guides |
| **Center canvas** | Interactive zoomable diagram of the cluster (or the selected node's internal modules) with animated message paths |
| **Right inspector** | Live details for the selected cluster, node, or module (status, queues, counters, and plain-language notes) |
| **Bottom timeline** | Ordered event log with filters and click-to-inspect details |

**Golden rule:** The simulation state and event log drive everything you see on screen. Animations never run on fake, disconnected timers.

## First Playable Scenario: Distributed Task Scheduling

The first working version focuses on one classic, easy-to-follow setup: **1 Client + 1 Coordinator + 3 Workers**.

**Normal flow (Happy Path):**
1. The Client sends a new Job.
2. The Coordinator receives the Job, creates a Task, and puts it in its waiting Queue.
3. The Scheduler picks a healthy Worker and sends the Task over the simulated network.
4. The Worker receives the Task, runs it, and sends the Result back.
5. The Coordinator records the Result and replies to the Client.

**Failure flow (When Things Go Wrong):**
1. You crash a Worker while it is busy (or delay its messages).
2. The Worker stops sending heartbeats (periodic "I'm alive" signals).
3. After a timeout, the Coordinator marks that Worker as failed and puts the unfinished Task back into the queue.
4. The Scheduler assigns a fresh attempt of that Task to another healthy Worker.
5. If a late reply from the old failed attempt shows up later, the Coordinator recognizes it as outdated and safely ignores it.

You can click into any machine during the run to watch its queues, state changes, and messages update in real time.

## Theory Reference

All formal distributed-systems theory, proofs, and course notes live in a separate repository: [Distributed-System Notes](https://github.com/johnson931128/Distributed-System). **WUWAScope focuses on interactive simulation and visual intuition instead of duplicating textbook chapters.**

Published reference notes currently available:

- [Notes Index & Progress](https://github.com/johnson931128/Distributed-System/blob/main/notes/README.md)
- [Course Introduction](https://github.com/johnson931128/Distributed-System/blob/main/notes/00-course-introduction.md)
- [Logical Clocks, Global States & Checkpointing](https://github.com/johnson931128/Distributed-System/blob/main/notes/01-logical-clocks-checkpointing.md)
- [Fault Tolerance, Paxos & Raft](https://github.com/johnson931128/Distributed-System/blob/main/notes/02-fault-tolerance-paxos-raft.md)

Future topics that do not yet have published notes may be listed as planned, never presented as already verified.

## Planned Architecture

WUWAScope is a native cross-platform desktop application written in **C# (.NET)** using **Avalonia UI**:

- **Simulation Core (`WUWAScope.Simulation`):** A pure C# library with zero UI dependencies. It runs the simulated clock, event queue, machine state transitions, and repeatable random seed.
- **Desktop UI (`WUWAScope.App`):** Built with **Avalonia UI** (using MVVM — separating UI layout from screen state logic) and hardware-accelerated vector graphics for smooth zooming, panning, and message animations.
- **Automated Tests (`WUWAScope.Simulation.Tests` & `WUWAScope.App.Tests`):** Unit tests that verify exact event ordering, repeatable replays, crash recovery, and UI interactions.
- **Future Live Mode (Optional):** If we ever connect real local processes later, that adapter will stay completely separate from the simulation core.

## Roadmap

- **Phase 0 — Interactive Shell:** Runnable Avalonia desktop app with the five-panel workspace, clickable Cluster → Node → Module navigation, clear placeholder/stub labels, and core simulation interfaces ready for Phase 1.
- **Phase 1 — First Working Simulator (MVP):** Real pure-C# event engine driving the Task Scheduling scenario, smooth message animations, Play/Pause/Step/Reset, Event Timeline, Worker Crash & Reassignment, Stale Result handling, and full unit tests.
- **Phase 2 — Core Distributed Systems Lessons:** New scenarios built on the same engine, linked to the theory notes — Logical Clocks, Global Snapshots, and Leader Election / Consensus (Raft/Paxos).
- **Phase 3 — Deeper Lab Tools:** Network splits (partitions), custom delay/drop settings, timeline scrubbing, trace export/import, and side-by-side policy comparisons.
- **Phase 4 — Live Mode (Optional):** Feed events from real local processes into the same visual workspace, clearly separated from Simulated Mode.

## Running Locally (Phase 2 — 4 Playable Scenarios)

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/) (verified locally on `.NET 10.0.102`).

```bash
# Build the entire solution
dotnet build WUWAScope.slnx

# Run all 26 automated unit tests (engine invariants + ViewModel navigation & scenario switching)
dotnet test WUWAScope.slnx

# Launch the local Avalonia desktop app
dotnet run --project src/WUWAScope.App

# Optional: launch directly into any scenario with autoplay or inside a specific machine
dotnet run --project src/WUWAScope.App -- --scenario logical-clocks --autoplay
dotnet run --project src/WUWAScope.App -- --scenario global-snapshots --autoplay
dotnet run --project src/WUWAScope.App -- --scenario raft-leader-election --autoplay
dotnet run --project src/WUWAScope.App -- --open-node coordinator
```

**Included playable scenarios (switch anytime in the left sidebar):**
1. **Distributed Task Scheduling** — 1 Client, 1 Coordinator, 3 Workers; heartbeat timeouts, worker crash/recovery, task reassignment, message delay/drop, and stale-result rejection.
2. **Logical Clocks (Lamport & Vector)** — 3 processes (`P1`, `P2`, `P3`) replaying the verified `notes/01` §3–4 lecture timeline plus interactive local events, message sends, and concurrency (`∥`) detection.
3. **Global Snapshots (Chandy-Lamport)** — 3 bank accounts (`$500`, `$200`, `$300` = `$1,000` conserved) over strict FIFO channels showing how Markers capture in-flight `$50` transfers in channel states (`SC`).
4. **Leader Election (Teaching Raft)** — 5 voting servers (`Quorum = 3/5`) demonstrating randomized election timeouts, split votes, higher-term step-down, leader crash failover, and quorum loss safety.

**Controls inside the app:**
- **Play / Pause (`Space`)**, **Step (`N`)** for one event at a time, **Reset (`R`)**, and **Speed** (`0.5x` – `4.0x`).
- **Scenario actions:** The toolbar adapts automatically to the active lesson (Submit Job / Crash Worker / Local Event / Send Message / Start Snapshot / Send `$50` / Force Timeout / Delay or Drop Next Msg).
- **2.5D Canvas:** **Single-click** a machine to inspect it, **double-click** (or click `Open Inside Machine →`) to open its 2.5D internal module layout, **drag** machines/modules to rearrange them, **scroll** to zoom, **drag empty space** to pan, and press **`Esc`** to step back up one level without losing simulation state.
- Use **Hide Menu / Hide Details / Hide Timeline** in the top-right to maximize the 2.5D canvas.

## Project Documents

- [PRODUCT_SPEC.md](docs/PRODUCT_SPEC.md) — Detailed product design, scenario walkthroughs, engine contract, and acceptance checklists.
- [AGENTS.md](AGENTS.md) — Rules and engineering standards for AI coding agents working on this repo.
- [GEMINI.md](GEMINI.md) — Entry file for Gemini / Antigravity pointing to `AGENTS.md`.
