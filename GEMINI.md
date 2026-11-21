# WUWAScope — Gemini / Google Antigravity Entrypoint

This file helps Gemini CLI and Google Antigravity pick up the same project rules as other coding agents. Keep **AGENTS.md** as the single source of truth — do not duplicate or fork its rules here.

@./AGENTS.md

## Startup Checklist

1. Read `README.md` and `docs/PRODUCT_SPEC.md` before writing code.
2. Check what files and `.NET` projects already exist in the workspace and report the current state honestly.
3. Build in small, verified steps starting with **Phase 0 — Interactive Shell** (local desktop app using **C# + Avalonia UI**).
4. Keep all documentation and UI text in **plain, easy-to-read English** (explain technical terms simply).
5. Focus first on smooth **Cluster → Node → Module** navigation and a clean separation between the pure C# simulation engine and the Avalonia UI.
6. Never pretend a prototype is a finished simulator or a real live cluster.
7. Run real `dotnet build` / `dotnet test` checks and report exact results plus what remains to be done.
8. Automatically commit and push verified changes with clear commit messages (never force-push).

- **Theory reference:** https://github.com/johnson931128/Distributed-System
- **Interaction inspiration:** https://designed-to-scale.vercel.app/#/request-lifecycle/3
