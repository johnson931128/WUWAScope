# WUWAScope — Product Specification (v0.2)

- **Status:** Phase 1 complete (First Playable MVP with pure C# deterministic discrete-event engine); Phase 2 theory-linked lessons are next
- **Tech stack:** Local desktop app built with **C# (.NET) + Avalonia UI**
- **Project overview:** [README.md](../README.md)
- **Engineering & agent rules:** [AGENTS.md](../AGENTS.md)
- **Theory reference:** https://github.com/johnson931128/Distributed-System
- **Interaction inspiration:** https://designed-to-scale.vercel.app/#/request-lifecycle/3

---

## 1. What WUWAScope Is

WUWAScope is a **local desktop app for visually simulating distributed systems**. Its core idea is simple: you can **zoom from a whole cluster into a single machine, and then into the parts inside that machine**, while one shared simulation engine keeps everything running consistently in the background.

The app helps you see and answer practical questions:
- What actually happens step by step when a Client sends work to a Coordinator?
- Which messages are currently in transit, waiting, dropped, delayed, or retried?
- Why did the Scheduler pick Worker 2 instead of Worker 1?
- What changes inside a machine the moment a message arrives?
- When a Worker crashes, how does the system notice, and how does the unfinished task recover?
- How do abstract distributed-systems concepts connect to real, observable events?

**Out of scope for MVP:** real networked servers, cloud hosting, Kubernetes/container orchestration, live server monitoring, full production Raft/Paxos, or a drag-and-drop custom cluster builder.

---

## 2. Who It Is For

- Learners with basic programming and networking familiarity who want a clear, visual way to understand distributed message flows, scheduling, and failure recovery without wading through walls of dense jargon.
- Engineers who want a clean, runnable, testable, and repeatable local desktop lab that demonstrates distributed systems accurately.

---

## 3. App Layout and Navigation

### 3.1 Main Desktop Window

| Area | What Goes Here | Main Actions |
| --- | --- | --- |
| **Header** | App & scenario title, clear `SIMULATED` mode badge | Switch scenarios; always know you are in simulation mode |
| **Playback toolbar** | Play, Pause, Step, Reset, speed slider, fault buttons | Control **one** shared simulation clock |
| **Left sidebar** | Scenario list (Task Scheduling active; others marked Planned), machine list | Pick a scenario; read its goal and simplifications |
| **Center canvas** | Interactive diagram of Client, Coordinator, and 3 Workers with directional links | Click nodes or messages, zoom in/out, pan around |
| **Right inspector** | Two clean tabs: **Status** (live state, queues, internal parts) and **Explain** (plain-English guide + theory links) | Inspect whichever cluster, node, or module is selected; one-click collapse to maximize the canvas |
| **Bottom timeline** | Ordered list of events with simulated time, sender → receiver, and category | Filter events; click an event to highlight its path and inspect details; collapsible |

### 3.2 Three-Level Drill-Down (Cluster → Node → Module)

**Level 1 — Cluster View (Whole System):**
- Shows all machines, how they connect, each machine's current status, messages moving along links, and which worker is running which task.
- Clicking **Coordinator** opens the Coordinator's Node View; clicking **Worker 2** opens Worker 2's Node View.
- Hovering over a moving message shows who sent it, who it is going to, its type, ID, and attempt number.

**Level 2 — Node View (Inside One Machine):**
- Shows an interactive diagram of the logical parts inside that specific machine:
  - **Coordinator:** `Request Handler` → `Job Queue` → `Scheduler` → `Outbox`; `Heartbeat Monitor` → `Worker Registry`; `Result Handler`.
  - **Worker:** `Inbox` → `Task Executor` → `Result Outbox`; `Heartbeat Sender`; `Local Task State`.
  - **Client:** `Job Submitter` → `Outbox`; `Response Inbox` → `Job Tracker`.
- Highlights whichever part is currently active and shows waiting items, task IDs, and state changes.
- Includes a clear breadcrumb bar (e.g., `Cluster / Coordinator / Scheduler`) so you can jump back out at any time **without resetting or pausing the simulation**.

**Level 3 — Module View (Inside One Internal Part):**
- Shows that module's current state, recent inputs and outputs, queue entries, counters, and related events.
- Clicking an event in the bottom timeline focuses the related machine and module whenever possible.
- This is a clean logical inspection view — we are not trying to emulate a real operating system kernel or CPU threads.

All three views stay live and in sync as the simulation advances.

---

## 4. Visual Style and Usability

WUWAScope borrows the **clear, step-by-step visual storytelling** feel of *Designed to Scale*, while using its own native desktop layout, colors, and diagrams.

- **Warm 2.5D isometric illustration style:** A warm cream paper canvas (`#FAF6EE`) with subtle diamond isometric grid lines, crisp dark ink outlines, soft pastel 2.5D rounded machine blocks (amber for Coordinator, sky blue for Workers, a minimalist isometric laptop on a platform for Client), floating pill-shaped badges, and outlined circular packet dots gliding along isometric links. (A dark theme can be added later.)
- **Consistent 2.5D node internals:** Zooming into a machine opens an isometric internal floorplate showing that machine's modules as matching 2.5D blocks connected by directional flow lines.
- **Calm, purposeful motion:** Animations show real message movement and state changes — no distracting decorative effects.
- **Role-based styling:** Client, Coordinator, and Workers have distinct, consistent visual identities.
- **Never rely on color alone:** Every status uses both a clear text badge/icon and a distinct color.
- **Standard status labels:** `Idle`, `Queued`, `Processing`, `Healthy`, `Suspected`, `Failed`, `Recovering`.
- **Always-visible playback controls:** Play, Pause, Step, and Reset stay accessible whether you are viewing the Cluster, a Node, or a Module.
- **Desktop ergonomics:** Supports keyboard navigation (`Space`, `N`, `R`, `Esc`), clear focus rings, crisp HiDPI scaling (Skia vector rendering in Avalonia), and one-click collapsible left, right, and bottom panels so the 2.5D canvas can take center stage.

---

## 5. MVP Scenario: Coordinator & Workers Task Scheduling

**Machines in this scenario:**
- 1 Client
- 1 Coordinator
- 3 Workers (`Worker 1`, `Worker 2`, `Worker 3`)
- *(Optional later: a simple logical Result Store — clearly marked as logical, not a real database.)*

**How this teaching model works:**
- The Client submits a Job containing one or more independent Tasks.
- The Coordinator keeps a waiting Task Queue and a Worker Registry (a list of which workers are alive and free).
- The Scheduler assigns waiting tasks to healthy workers that have free capacity.
- A simulated network delivers messages with configurable travel time (and optional delay or packet drop).
- Workers execute tasks and schedule a completion event after a simulated work duration.
- Workers regularly send **heartbeats** (short "I'm still alive" messages). If heartbeats stop arriving for too long, the Coordinator marks that worker as timed out.
- Every time a task is sent to a worker, it gets a new **Attempt ID / generation number** (like Attempt 1, Attempt 2). If a slow, outdated reply from Attempt 1 arrives after Attempt 2 already finished, the Coordinator ignores the old reply.

> **Teaching simplification:** This model teaches scheduling, heartbeat timeouts, retries, and stale-reply protection. It does not claim to replicate any specific production framework (like Spark or Kubernetes).

### 5.1 Normal Flow (Happy Path)

1. Client submits Job `J1`.
2. Coordinator receives `J1` and creates Task `T1`.
3. `T1` enters the Coordinator's Task Queue.
4. Scheduler chooses `Worker 1` and dispatches `T1` as **Attempt `A1`**.
5. The dispatch message travels across the link and arrives at `Worker 1`.
6. `Worker 1` marks `T1` as `Processing`, runs it, and finishes execution.
7. `Worker 1` sends the Result message back to the Coordinator.
8. Coordinator accepts `A1` as the final result and sends the response back to the Client.

Every step must be visible through canvas highlights, module updates, and timeline entries.

### 5.2 Failure Flow (Fault Path)

1. Submit a job and let `Worker 1` start working on Task `T1` (`Attempt A1`).
2. Trigger **Crash Worker 1** (or **Delay Next Message** to simulate a stalled worker/network).
3. Time moves forward — the Coordinator does **not** magically know instantly. Instead, expected heartbeats from `Worker 1` fail to arrive.
4. Once the heartbeat timeout expires, the Coordinator marks `Worker 1` as timed out and marks `Attempt A1` as lost.
5. Task `T1` goes back into the waiting queue and is dispatched to `Worker 2` as **Attempt `A2`**.
6. `Worker 2` finishes and sends back the result; the Coordinator accepts `A2`.
7. If `A1`'s late result arrives afterward (for example, after a delayed message finally delivers), the Coordinator labels it **stale** and ignores it.
8. Triggering **Recover Worker 1** brings `Worker 1` back online cleanly so it can accept future tasks.

**User failure controls:** `Crash / Recover Worker`, `Delay Next Message`, `Drop Next Message`, `Reset`, and `Playback Speed`.

### 5.3 Event Names (Initial Vocabulary)

- **Job & Queue:** `JOB_SUBMITTED`, `JOB_RECEIVED`, `TASK_ENQUEUED`
- **Dispatch & Network:** `TASK_DISPATCHED`, `MESSAGE_SENT`, `MESSAGE_DELIVERED`, `MESSAGE_DROPPED`
- **Execution & Results:** `TASK_STARTED`, `TASK_EXECUTION_FINISHED`, `RESULT_RECEIVED`, `STALE_RESULT_IGNORED`
- **Health & Failures:** `HEARTBEAT_SENT`, `HEARTBEAT_RECEIVED`, `WORKER_CRASHED`, `WORKER_TIMED_OUT`, `WORKER_RECOVERED`, `TASK_REQUEUED`
- **Completion:** `JOB_FINISHED`, `CLIENT_RESPONDED`

Every recorded event includes a unique ID, simulated timestamp, sequence number, event type, source/target (when applicable), cause ID (what triggered it), and structured details.

---

## 6. How the Simulation Engine Works

### 6.1 Engine Interface (Pure C#, No UI Code)

The simulation engine lives in its own C# library with zero references to Avalonia or any UI framework. Minimal contract:

- `CreateSimulation(ScenarioConfig config, int seed)` — creates a fresh simulation with a repeatable random seed.
- `GetState()` — returns the current read-only snapshot of all machines, links, messages, jobs, tasks, and metrics.
- `Step()` — processes **exactly one** next event from the queue and returns the event record plus updated state.
- `Reset()` — restores the exact starting state using the same config and seed.
- `DispatchAction(UserAction action)` — queues a user action (like "Submit Job" or "Crash Worker 1") into the simulation timeline in a repeatable order.
- `StateChanged` event / observable notification — lets the desktop UI refresh cleanly whenever state changes.

**Clock & Animation rule:**
- The engine orders queued events strictly by `(SimulatedTimeMs, SequenceNumber)`.
- The engine knows nothing about frame rates or real-world wall-clock time.
- During **Play**, the UI advances a visual playback clock smoothly so in-flight messages glide across links between their `SentAt` and `DeliverAt` timestamps, calling `Step()` at the exact simulated time each event happens. Changing playback speed only changes how fast the visual clock moves — never the order or outcome of events.

### 6.2 Core Data Models

- **Node:** `Id`, `Role` (`Client`, `Coordinator`, `Worker`), `Health`, `Capacity`, internal module states, inbox/outbox lists.
- **Link:** `SourceNodeId`, `TargetNodeId`, configured simulated latency, optional delay/drop rule.
- **Job:** `Id`, list of `TaskIds`, overall status.
- **Task:** `Id`, current status, assigned worker, active attempt ID, accepted attempt ID (if finished).
- **Attempt:** `Id`, `TaskId`, `WorkerId`, `Generation` (1, 2, 3...), status (`Dispatched`, `Running`, `Completed`, `Lost`, `StaleIgnored`).
- **Message:** `Id`, `Kind`, `SourceNodeId`, `TargetNodeId`, related task/attempt IDs, `SentAtMs`, `DeliverAtMs`, status (`InFlight`, `Delivered`, `Dropped`).
- **EventRecord:** `Id`, `TimestampMs`, `Sequence`, `Type`, `SourceNodeId`, `TargetNodeId`, `ModuleId`, `CauseEventId`, and structured payload.

The UI derives everything from engine state and the event log — no duplicate copies of truth scattered across view files.

### 6.3 Rules That Must Always Hold (Invariants)

These are guarantees of our simulation model (not claims that real networks provide free exactly-once delivery):

1. Each task can have **at most one accepted final result**.
2. A retry always increases the attempt generation number, and an older attempt can never overwrite a newer accepted result.
3. A crashed worker never starts new work or receives new assignments until it recovers.
4. Task counters (`Pending + Running + Completed/Failed`) always match the exact engine state.
5. `Step()` processes exactly one event and makes its effect visible.
6. `Pause` completely freezes simulated time until you press `Step` or `Play`.
7. `Reset()` restores the exact initial state for the same scenario and seed.
8. Running the same scenario, seed, and user actions again produces the **exact same event log**.

### 6.4 Simulated Metrics

The MVP can display:
- Job duration (simulated milliseconds)
- Coordinator queue depth
- Dispatched tasks count
- Retry count
- Total messages sent / dropped
- Worker busy percentage (calculated directly from simulated busy time vs total simulated time)

Every metric must clearly show a **Simulated** label. Never invent fake CPU%, memory MB, or real-world P99 latency numbers.

---

## 7. Project Structure (C# + Avalonia UI)

```text
WUWAScope.slnx
  src/
    WUWAScope.Simulation/       # Pure C# engine: models, contracts, Phase 0 prototype stub (Phase 1 engine next)
    WUWAScope.App/              # Avalonia UI desktop app (Views, ViewModels, 2.5D Isometric Canvas, Styles)
  tests/
    WUWAScope.Simulation.Tests/ # xUnit tests for models, contracts, and upcoming deterministic engine rules
    WUWAScope.App.Tests/        # ViewModel navigation, filtering, and collapsible panel tests
  docs/
    PRODUCT_SPEC.md
```

---

## 8. Acceptance Checklists

### Phase 0 — Interactive Desktop Shell

- [x] App builds and launches locally as a desktop window using documented `dotnet` commands.
- [x] All 5 cluster nodes (`Client`, `Coordinator`, `Worker 1`, `Worker 2`, `Worker 3`) appear with clear role labels and connecting links.
- [x] Clicking any node highlights it and updates the inspector.
- [x] Entering a node shows its role-specific internal modules (Coordinator modules differ from Worker modules).
- [x] Clicking different modules shows different, relevant details for each module.
- [x] Moving between Cluster, Node, and Module views works smoothly with a clear way back and does not lose state unexpectedly.
- [x] Playback toolbar and fault controls exist; controls not yet hooked to the Phase 1 engine are disabled or clearly marked as prototype/stub.
- [x] Resizing the desktop window keeps core controls usable without clipping or hiding essential actions.
- [x] `README.md` lists real, locally verified build/run/test commands.

### Phase 1 — First Working Simulator (MVP)

- [x] Pure C# deterministic event queue runs independently of the Avalonia UI loop.
- [x] Happy path finishes end to end with smooth message movement and a clear event timeline.
- [x] Node and Module views show live queues and state updates as events fire.
- [x] Pause freezes simulated time; Step advances exactly one event.
- [x] Reset and repeatable replay pass automated unit tests.
- [x] Crashing a worker triggers heartbeat timeout detection, marks the attempt lost, and requeues the task.
- [x] A healthy worker picks up and finishes the requeued task.
- [x] Late replies from old attempts are marked stale and safely ignored.
- [x] Queue and task counters always match engine state.
- [x] Automated tests cover at least one full happy path, one worker crash/reassignment path, and stale-result rejection.
- [x] All displayed metrics are clearly marked **Simulated** and reproducible.

---

## 9. Future Learning Scenarios (Planned, Not Built Yet)

Each future lesson will reuse the same C# simulation engine and desktop workspace:

| Topic | What You Will Explore | Theory Reference |
| --- | --- | --- |
| **Logical Clocks** | Ordering events without synchronized physical clocks (Lamport & Vector Clocks) | `Distributed-System` notes/01 |
| **Global Snapshots** | Taking a consistent picture of a running system using Chandy-Lamport markers | `Distributed-System` notes/01 |
| **Fault Tolerance** | How failure detectors work and why timeouts can be fooled | `Distributed-System` notes/02 |
| **Paxos / Raft** | Leader election, terms, voting, and reaching agreement despite crashes | `Distributed-System` notes/02 |
| **Replication, Locks, Gossip, Queues** | Planned only after verified course notes exist | `Distributed-System` notes index |

Never describe planned scenarios as working features.

---

## 10. Immediate Next Steps

1. Keep `README.md`, `AGENTS.md`, `GEMINI.md`, and this spec aligned with the **C# + Avalonia UI** desktop architecture.
2. Share a short Phase 0 file plan and create the `.NET` solution (`WUWAScope.Simulation`, `WUWAScope.App`, and test projects).
3. Build Phase 0 end to end: launchable Avalonia desktop window, 5-node cluster diagram, working Cluster → Node → Module drill-down, clearly labeled stub data, and clean Phase 1 engine interfaces.
4. Run `dotnet build` and `dotnet test`, launch and verify the desktop app locally, and update `README.md` with exact working commands.
