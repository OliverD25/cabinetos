# CabinetOS — Modern System Commander: Project Constitution

> **Status: protected.** This document defines the concepts and principles of the
> project. No part of it — wording or content — may be changed without explicit
> approval from the project creator.
>
> Adopted: 2026-09-28

## North Star Vision

"An approachable, high-performance file manager that marries the raw dual-pane efficiency of Total Commander with the deep extensibility and workspace architecture of VS Code."

## 1. Zero-Compromise Performance

Speed is the ultimate metric. The architecture (Rust core + WinUI 3 frontend) must guarantee instant directory loading, zero-latency UI threading, and real-time NT-level file indexing. The interface must never freeze during heavy file operations.

## 2. Free & Open Source

The core application and marketplace infrastructure will remain free and open-source, fostering a community-driven ecosystem for continuous improvement, security audits, and plugin development.

## 3. Native Modern Aesthetics

The UI must feel indistinguishable from a first-party Windows 11 application. It requires genuine Fluent Design, Mica backdrop materials, smooth animations, and high-quality native typography without sacrificing information density.

## 4. Progressive Disclosure

The system must be instantly intuitive for a casual user starting their first session, hiding unnecessary complexity. Simultaneously, the underlying architecture must expose absolute control, raw power, and programmable interfaces for power users when they seek it.

## 5. Dual-Pane Foundation

The primary structural paradigm is a dual-pane layout, optimizing file transfers, comparisons, and navigation efficiency inherited from classic orthodox file managers.

## 6. Universal Configuration

Every single preference, theme, and system setting must be perfectly mirrored. A user can configure the application entirely through a beautiful graphical settings menu, or by directly editing the underlying raw configuration files (JSON/YAML), with changes syncing in real-time.

## 7. Absolute Keyboard Control & Command Palette

The mouse is optional. The application is built around a comprehensive command architecture mirroring VS Code:

- **The Command Palette:** Every action in the system is accessible via a central searchable command palette.
- **Universal Shortcuts:** Every command has a discrete name and can be bound to a shortcut.
- **Inline Editing:** Shortcuts can be viewed, reassigned, and edited directly from within the Command Palette or the raw configuration file.
- **Chord Keybindings:** The system fully supports sequential key combinations (e.g., pressing `Ctrl+K`, releasing, then pressing `Ctrl+C` within a time window) to multiply the available shortcut real estate.
- **Immutable System Tier:** A protected tier of fundamental system shortcuts exists to guarantee baseline navigation and prevent users from permanently locking themselves out of core functions.

## 8. Sandboxed Extensibility

The feature set is infinitely expandable via a WebAssembly (WASM) plugin ecosystem and a JSON-based theme engine. Users can write, install, and share extensions via a centralized marketplace. Plugins operate within strict capability sandboxes to guarantee stability and security, ensuring bad code never crashes the file manager.

## 9. Workspace & Terminal Integration

The system integrates embedded developer tools, allowing users to spawn integrated terminals scoped to the active directory, manage project-specific workspaces, and execute developer workflows directly from the file management interface.

## 10. The Zero-Bloat Foundation (Opt-In Complexity)

The core application ships strictly as a hyper-optimized, bare-metal file navigation engine. Out of the box, it contains zero supplementary tools, specialized viewers, or heavy integrations. Every piece of advanced functionality is strictly opt-in via the extension marketplace. This guarantees the application remains infinitely lightweight for purists, while scaling into a massive, IDE-like powerhouse only for the users who explicitly build it to be one.

## 11. Bifurcated Extension Architecture

Extensions are strictly separated into two distinct architectural layers to protect performance and UI stability:

- **Core Plugins (The Invisible Engine):** Headless WebAssembly modules that hook directly into the backend core to intercept I/O, modify background behavior, or add commands, completely decoupled from the UI.
- **Tool Extensions (The Visual Workspace):** Frontend UI-driven applets hosted in dedicated dockable "Tool Panes" (e.g., bash terminals, hex editors, markdown previews) that visualize or interact with selected files without touching the core I/O pipeline.
