# WUWAScope

**An interactive laboratory for exploring how distributed systems work.**

WUWAScope（分散式系統互動視覺化實驗室）是一個以 **Simulation-first** 為核心的 Web App，讓抽象的分散式系統概念變成可觀察、可操作、可重播的事件。除了從叢集全景追蹤請求與訊息，也可以進入任一節點，檢視內部模組、佇列、狀態轉移與故障恢復過程。

> **Project status:** Planning / specification. 尚未完成可執行的 App；以下功能為設計目標，不代表已實作。

## Product vision

WUWAScope 不只是顯示節點在線狀態的 Dashboard，而是結合 **System Diagram + Discrete-event Simulator + Node Inspector + Guided Learning** 的互動式實驗環境。

三層視角：

1. **Cluster View** — 顯示 Client、Coordinator／Master、Worker、Storage 等角色，觀察網路拓樸、節點狀態與訊息路徑。
2. **Node View** — 點進節點，查看 Request Handler、Scheduler、Task Queue、Message Transport、State Machine、Storage Adapter 等邏輯模組及其資料流。
3. **Module View** — 查看模組當前狀態、輸入／輸出訊息、等待佇列、事件紀錄與狀態變化。依場景展示相關模組，不假設所有節點具有相同內部結構。

### Key interactions

- **Play / Pause / Step / Replay / Speed**：逐事件播放、暫停、單步推進、重新開始和調整速度。
- **Animated message tracing**：追蹤訊息發送、在途、送達、逾時及重試，顯示事件因果順序。
- **Drill-down inspection**：從 Cluster → Node → Module 持續深入，保留可返回的導覽路徑。
- **Fault injection**：操作 Crash / Recover、Message Delay / Drop、Timeout 等故障情境。
- **Timeline & event log**：用事件時間軸、可篩選日誌和選中事件的詳細資訊理解系統行為。
- **Explain the why**：對應情境呈現原理、角色、關鍵不變量及理論筆記連結。
- **Truthful metrics**：清楚標註 simulated metrics；不得把預設或估計數據稱為真實量測。

## Interface concept

採用 **Desktop-first、互動式架構圖** 的操作體驗，靈感來自 [Designed to Scale — Request Lifecycle](https://designed-to-scale.vercel.app/#/request-lifecycle/3) 的逐步導覽和動畫教學，但 WUWAScope 會有自己的 UI 與互動模型，而非照搬其視覺資產。

| 區域 | 主要用途 |
| --- | --- |
| Top toolbar | 情境名稱、播放／暫停／單步／重設、速度、故障操作 |
| Left sidebar | 場景清單、節點清單、概念導覽 |
| Center canvas | 可縮放的叢集圖、互動節點、帶方向的訊息動畫 |
| Right inspector | 選中節點／模組的內部結構、Queue、State、Metadata |
| Bottom timeline | 事件時間軸、訊息追蹤、狀態變化、事件篩選 |

設計原則：事件與狀態是視覺化的唯一資料來源；不能只播放看起來合理、卻沒有對應模擬狀態的裝飾動畫。

## First playable scenario: Distributed Task Scheduling

初版以「**1 Coordinator + 3 Workers**」建立可驗證的端到端模擬：

1. Client 發出 Job Request。
2. Coordinator 接收請求，建立 Job 與 Task，放進 Queue。
3. Scheduler 選擇 Worker，透過模擬網路傳送 Dispatch 訊息。
4. Worker 接收並執行 Task，送回 Result／Completion。
5. Coordinator 更新 Job 狀態，回覆 Client。
6. 故障實驗：Worker Crash、Heartbeat 逾時、Task Reassignment、重複完成訊息處理。

可以選中 Worker，直接檢視 Inbox、Task Executor、Local State、Heartbeat 與 Outbox 等內部模組。MVP 重點是 **讓訊息、排程、故障與恢復在畫面中有可追溯的因果關係**。

後續情境可擴展為 Logical Clock、Global Snapshot、Paxos／Raft Leader Election、Replication、Distributed Lock、Gossip、MapReduce 等；每個情境均須先定義協定與驗收條件，再加入 UI。

## Theory references

本專案的主要學理、術語與推導依據，統一參照獨立維護的 [Distributed-System 學習筆記](https://github.com/johnson931128/Distributed-System)。**WUWAScope 負責互動模擬與視覺化，不重複建立另一套課程講義。**

目前已公開的對應內容：

- [Course Introduction](https://github.com/johnson931128/Distributed-System/blob/main/notes/00-course-introduction.md)
- [Logical Clocks, Global States & Checkpointing](https://github.com/johnson931128/Distributed-System/blob/main/notes/01-logical-clocks-checkpointing.md)
- [Fault Tolerance, Paxos & Raft](https://github.com/johnson931128/Distributed-System/blob/main/notes/02-fault-tolerance-paxos-raft.md)
- [Notes index / progress](https://github.com/johnson931128/Distributed-System/blob/main/notes/README.md)

後續章節可能仍在規劃中；沒有實際存在的筆記，不建立假連結或宣稱已完成。

## Proposed architecture

- **Frontend**：React + TypeScript；互動節點圖可評估 React Flow（@xyflow/react）。
- **Simulation core**：與 UI 解耦的 TypeScript 離散事件引擎，以可重現的事件佇列與狀態轉移驅動畫面。
- **State and trace**：結構化事件日誌、可序列化的模擬狀態、可重現的 seed。
- **Testing**：模擬引擎單元測試、故障情境測試及基本 UI 互動測試。
- **Future real-cluster mode**：未來可透過 Backend / WebSocket / Container 對接真實服務；與 Simulation Mode 明確區分。

技術選擇尚屬提案，實作前可依需求調整；不要只為展示而架設不必要的真實微服務。

## Roadmap

- **Phase 0 — Interactive shell**：完整介面骨架、節點可點選、Cluster / Node / Module 導覽、假資料明確標記。
- **Phase 1 — Working simulator (MVP)**：任務分派、事件佇列、逐步播放、訊息動畫、時間軸、Worker 故障與重派。
- **Phase 2 — Distributed-systems lessons**：Logical Clocks、Snapshots、Leader Election 與 Consensus 情境。
- **Phase 3 — Advanced experiments**：Network Partition、Replication、可重播測試、情境比較與效能指標。
- **Phase 4 — Live integrations (optional)**：將真實節點事件映射到相同可視化介面。

## Development handoff

- [PRODUCT_SPEC.md](docs/PRODUCT_SPEC.md)：MVP 互動規格、狀態模型與驗收條件。
- [AGENTS.md](AGENTS.md)：AI Agent 開發規範及理論引用規則。
- [GEMINI.md](GEMINI.md)：Gemini／Antigravity 入口，指向共用規範。

現階段尚未建立程式碼及安裝腳本；應先完成 Phase 0 的可運作介面，再實作 Phase 1 的可信模擬引擎。