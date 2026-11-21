# WUWAScope — Product Specification (v0.1)

Status: **Design proposal / not implemented**
Source of truth for project intent: README.md
Agent implementation rules: AGENTS.md
Primary theory reference: https://github.com/johnson931128/Distributed-System
Interaction inspiration: https://designed-to-scale.vercel.app/#/request-lifecycle/3

## 1. Product statement

WUWAScope is a browser-based **interactive visual simulator for distributed systems**. Its defining interaction is **zooming from a cluster into a node and then into its internal modules**, while the same event engine continues to control the simulation.

The product answers:
- What happens when a client submits work to a coordinator?
- Which messages are in flight, waiting, lost, retried, or acknowledged?
- Why did a scheduler choose a particular worker?
- What changes inside a node when a message arrives?
- What fails when a worker crashes, and what exactly restores progress?
- How does an abstract distributed-system principle correspond to observable events?

**Not in MVP:** actual networked processes, a hosted SaaS backend, container orchestration, production telemetry, full Raft/Paxos, or arbitrary user-authored topologies.

## 2. Primary target

A learner with basic operating systems, networking and programming knowledge who wants to reason through distributed message flows, node internals, scheduling and fault tolerance visually.

The first release should also serve as a strong interactive engineering portfolio demonstration: runnable, testable, replayable, and technically accurate.

## 3. Information architecture

### 3.1 Main workspace

| Region | Required elements | Core interaction |
| --- | --- | --- |
| Header | Project / scenario title, simulated/live mode badge | Switch scenario; know which mode is active |
| Simulation toolbar | Play, Pause, Step, Reset, speed slider | Control **one** shared engine clock |
| Scenario sidebar | Job Scheduling (available), other scenarios (planned/disabled) | Select scenario; read objective and assumptions |
| Cluster canvas | Client, Coordinator, 3 Workers, directional links | Select node, select message, zoom and pan |
| Inspector | Overview / Queue / Modules / Events | Inspect selected node with live simulation state |
| Timeline | Ordered event rows, sim time, source → target, category | Select event and highlight its causal path |
| Context/explanation | Short description + deep links to theory | Explain the current event / state change |

### 3.2 Drill-down model

**Level A — Cluster:**
- Visual topology, connection direction, node role/status, message travel and task allocation.
- Click Coordinator → its Node View; click Worker 2 → Worker 2's Node View.
- Hover a message to see sender, recipient, type, ID and attempt.

**Level B — Node:**
- An interactive diagram of that node's logical modules.
- Coordinator: Request Handler → Job Queue → Scheduler → Outbox; Heartbeat Monitor → Worker Registry; Result Handler.
- Worker: Inbox → Task Executor → Result Outbox; Heartbeat Emitter; Local Task State.
- Display active module, queue contents, task IDs, state transitions, and pending events.
- Navigate back to cluster without resetting the simulation.

**Level C — Module:**
- Show active state, inputs, outputs, relevant counters, pending queue entries, and recent events.
- Selecting an event in the global timeline should focus its related module where supported.
- Details may appear in a focused module pane; an actual nested operating system/process emulator is out of scope.

The UI must remain synchronized if the simulation advances while a node/module is inspected.

## 4. Visual direction

Reference interaction: Designed to Scale uses a guided progression through architecture concepts with animated connections. WUWAScope should adopt the **clarity of step-driven visual explanation**, while building its own component language, spacing, typographic system, diagrams and colors.

Visual proposal:
- Quiet dark-slate background and high-contrast information cards; optional light theme later.
- Center-stage diagrams with restrained motion and clearly indicated direction.
- Node styling differentiates roles, not random colors.
- Event states use both text/icons and color; avoid color-only communication.
- Status labels: Idle, Queued, Processing, Healthy, Suspected, Failed, Recovering.
- Play/pause controls remain visible throughout drill-down.
- Timeline stays legible at desktop widths; panels may collapse on narrow screens.

Do not copy reference-site assets or try to imitate a screenshot pixel-for-pixel. The requested original interaction — a navigable view of **server internals** — should distinguish WUWAScope.

## 5. MVP scenario: Coordinator / Worker task scheduling

Topology:
- Client × 1
- Coordinator × 1
- Worker × 3
- Logical result storage is optional and should not imply a physical database

Suggested teaching-friendly configuration:
- One job containing one or several independent tasks.
- Coordinator maintains a queue and a registry of available workers.
- A scheduler assigns tasks to healthy workers with capacity.
- A simulated network delivers messages with configurable delay/drop rules.
- Workers execute tasks using scheduled completion events.
- Workers emit periodic heartbeats; coordinator detects absence via a timeout.
- Each dispatch carries an attempt ID; stale completions do not replace a newer accepted result.

This is a **teaching model of scheduling and fault recovery**, not a claim to implement a particular production framework.

### 5.1 Happy path

1. Client submits Job J1.
2. Coordinator receives J1 and creates Task T1.
3. T1 appears in Coordinator Queue.
4. Scheduler selects Worker W1 and dispatches T1 with Attempt A1.
5. Message traverses the topology link and arrives at W1.
6. W1 moves T1 into Running, then produces a completion event.
7. Result message returns to Coordinator.
8. Coordinator accepts a terminal result and responds to Client.

Each numbered action must be observable through a combination of canvas highlighting, module inspection and timeline entries. Event splitting may be finer as long as transitions remain consistent.

### 5.2 Fault path

1. Submit work and begin execution.
2. Crash a selected Worker (for example W1).
3. Show an explicit failure or absence-of-heartbeat interval; do not instantly reassign without a detection policy.
4. Coordinator times out and marks the relevant attempt lost.
5. T1 returns to a queue and is dispatched to W2 with Attempt A2.
6. W2 completes; Coordinator accepts A2.
7. If A1 later surfaces, label it stale and ignore it.
8. Recovery returns W1 to eligible state only under defined conditions.

Controls: **Crash / Recover Worker**, optionally **Delay / Drop Next Message**, Reset, and playback speed.

### 5.3 Event vocabulary (initial proposal)

- JOB_SUBMITTED, JOB_RECEIVED, TASK_ENQUEUED
- TASK_DISPATCHED, MESSAGE_SENT, MESSAGE_DELIVERED, MESSAGE_DROPPED
- TASK_STARTED, TASK_EXECUTION_FINISHED, RESULT_RECEIVED
- HEARTBEAT_SENT, HEARTBEAT_RECEIVED, WORKER_TIMED_OUT
- WORKER_CRASHED, WORKER_RECOVERED, TASK_REQUEUED
- STALE_RESULT_IGNORED, JOB_FINISHED, CLIENT_RESPONDED

Events must have stable identifiers, simulation time, type, source/target, and structured payload where relevant. Internal engine events and messages on the network may be distinct domain types.

## 6. Simulation model

### 6.1 Engine contract

Proposed minimal API:
- createSimulation(scenarioConfig, seed)
- getState()
- step() → next event plus state snapshot/result
- reset()
- dispatchAction(action) → queue external user action into deterministic order
- subscribe(listener) or adapter for view updates

An event queue orders by (simulated timestamp, monotonically increasing sequence). The engine is pure and independent of display frame rate; UI timers may only decide *when* to call step(), not what the next state transition means.

### 6.2 Core entities

- Node: id, role, health, capacity, module states, inbox/outbox.
- Link: source, target, configured delay/drop behavior.
- Job: id, task IDs, derived status.
- Task: id, current status, assigned worker, current attempt.
- Attempt: id, task ID, worker ID, generation, status.
- Message: id, kind, source/target, task/attempt reference, simulated send/delivery timestamps.
- Event: id, timestamp, sequence, type, details and cause/correlation IDs.

MVP may derive most UI information from engine state and event log; avoid multiple mutable copies of the same truth.

### 6.3 Behavioral invariants

- Exactly one terminal **accepted result** per task in the simulator (not exactly-once execution).
- Retry creates a new attempt generation and cannot accept a stale earlier attempt as authoritative.
- Crashed workers do not begin new task execution.
- Pending + running + terminal counts equal known tasks by the documented status model.
- Step processes exactly one queued event and exposes its effects.
- Pause leaves simulated time unchanged until Step or Play.
- Reset restores the same start state/configuration/seed.
- Replay with the same external actions and seed produces the same event trace.

### 6.4 Metrics

MVP may show job duration (simulated milliseconds), queue depth, number of dispatched tasks, retry count, message count, and worker utilization derived from simulated busy intervals.

All values must be visibly labeled **Simulated**. Do not display invented CPU percentages, real P99 latency, memory usage or measured throughput.

## 7. Suggested repository architecture (once implementation begins)

    src/
      app/                 application shell, route and layout
      simulation/          typed engine, clock, event queue, state machine
      scenarios/           task-scheduling configuration and scenarios
      components/          reusable toolbar, navigation, panels, dialogs
      features/cluster/    topology and message visualization
      features/inspector/  cluster/node/module inspectors
      features/timeline/   event trace and timeline
      styles/              design tokens and app styling
    tests/
      simulation/          deterministic engine and failure tests
      ui/                  important interactions
    docs/
      PRODUCT_SPEC.md

This is a recommended future structure. Do not create empty folders just to match this diagram.

## 8. Acceptance criteria

### Phase 0 — Interactive shell

- [ ] App boots locally using documented commands.
- [ ] All 5 cluster nodes are visible with labeled roles.
- [ ] Nodes can be selected and highlighted.
- [ ] Node selection opens the correct internally structured inspector.
- [ ] A module is selectable, and its contextual details differ per module.
- [ ] Cluster/Node/Module navigation works without losing current view unexpectedly.
- [ ] Playback UI exists; nonfunctional features are disabled or explicitly called prototypes.
- [ ] Narrow view remains usable; no essential actions are obscured.
- [ ] README lists actual runnable commands only after validation.

### Phase 1 — First real simulation

- [ ] Stable, deterministic event queue independent of UI animation.
- [ ] Happy path completes with a traceable timeline and visible message movements.
- [ ] Inspector modules show actual queues and states as events advance.
- [ ] Pause blocks engine progression; Step processes exactly one event.
- [ ] Reset and deterministic replay pass unit tests.
- [ ] Crashing a worker triggers a documented detection/retry flow.
- [ ] A healthy worker completes a requeued task.
- [ ] Late results from stale attempts are safely ignored.
- [ ] Queue/task counters reconcile with engine state.
- [ ] At least one automated test covers normal completion and one covers worker failure.
- [ ] Simulated metrics are identifiable and reproducible.

## 9. Future learning modules (not implemented)

These should be built as separate scenario definitions using a shared engine / visual vocabulary.

| Topic | Future simulation focus | Primary reading |
| --- | --- | --- |
| Logical clocks | Happens-before ordering, Lamport / Vector Clock updates | Distributed-System notes/01 |
| Global snapshots | Marker propagation and consistent cuts | Distributed-System notes/01 |
| Fault tolerance | Failure detector assumptions and recovery | Distributed-System notes/02 |
| Paxos / Raft | Leader/term and message-driven consensus behavior | Distributed-System notes/02 |
| ZooKeeper, Gossip, Replication, HDFS, Kafka | Planned after references exist and are verified | Distributed-System notes index |

Do not represent planned scenarios as working features.

## 10. Delivery and collaboration

For the first Antigravity run:
1. Read README.md, AGENTS.md and this specification.
2. Propose a lean frontend stack and a Phase 0 implementation plan.
3. Implement Phase 0 end-to-end; include a launchable local UI, meaningful node/module drill-down and clean component boundaries.
4. Establish typed simulation interfaces without pretending Phase 1 has been implemented.
5. Run typecheck, build, tests and (if browser access exists) exercise the interactions.
6. Update project docs with accurate status, commands and limitations.

Keep all teaching links pointed at the separate Distributed-System repository.