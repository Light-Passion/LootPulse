# Project Memory: LootPulse

This file serves as the session source of truth for development milestones, architectural constraints, and technical insights.

## Project Metadata
- **Project Name**: LootPulse Overlay
- **Tech Stack**: C# WPF (.NET 9.0)
- **Framework Type**: Unpackaged Windows Desktop App (allowing full file system access)
- **Active Phase**: Phase 3 - Completed & Verified

## Key Architecture Decisions
- **UI Framework**: Selected WPF over WinUI 3 due to superior support for reliable window transparency, simple Win32 click-through overlays, and unsandboxed file reads/writes for user profile files.
- **Path of Exile 2 Constraints**: Enforcing rules from `poe2_validator.md`:
  - **No Gold Trading**: Fail any calculations or UI designs attempting to trade gold or list gold values on player-to-player markets.
  - **Base Currencies**: Focus primary economy calculations on Exalted Orbs, Divine Orbs, and Chaos Orbs.
  - **Gem Progression**: Skill checks must respect the "Uncut Gem" framework.
  - **SME Validation Loop**: All sub-agents must actively confer with the validation engine ([poe2_validator.md](file:///d:/Gemini/PoE2_MarketFilter/.agents/skills/poe2_validator.md)). If any check returns a `FAIL`, agents must halt and run self-correction cycles based on the validator's feedback before completing tasks.
- **Overlay & Keybindings**:
  - **Interactive Mode Toggle Hotkey**: Configurable by the end user via a Settings/Config UI panel. The default hotkey is set to `Ctrl + Shift + O`.
- **Character & Zone Tracking**:
  - **Client Log Monitoring**: Automatically reads `Client.txt` to track zone entry messages and update current character level and region dynamically.
- **Build Parsing Support**:
  - **PoE2 Build Planner Format**: Parse the native JSON `.build` schemas containing `name`, `ascendancy`, `passives`, `skills`, and `inventory_slots`.
  - **PoB2 Integration**: Parse Path of Building 2 URL-safe Base64 compressed XML strings and compile them to the native `.build` JSON planner format.

## Done / Milestones
- [2026-06-14] Installed .NET SDK 9.0 (v9.0.315).
- [2026-06-14] Initialized the GOTCHA framework directory structure.
- [2026-06-14] Implemented core model structures (MarketItem, PoeBuild, PlayerState).
- [2026-06-14] Implemented PoeNinjaClient, BuildProfileParser, ClientLogMonitor, and FilterBuilder services.
- [2026-06-14] Implemented transparent overlay UI with click-through toggle logic in MainWindow.
- [2026-06-14] Added filter reload warning/instruction to user interface status updates.
- [2026-06-14] Audited and verified all features against the PoE2 SME Validator.
- [2026-06-14] Renamed application and namespaces from PoE2 MarketFilter to LootPulse.
