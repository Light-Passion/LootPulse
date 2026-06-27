# LootPulse ⚡ (Path of Exile 2 Overlay)

> [!WARNING]
> **Project Status: Incomplete & Under Development / Testing**
> This application is currently in an early development and testing phase. Features are subject to change, and bugs are expected. Use with caution.

LootPulse is a lightweight, high-performance WPF overlay helper for **Path of Exile 2**, designed to streamline your in-game trading, zone tracking, and build progression.

The tool provides a transparent, click-through overlay window that monitors your game state and dynamically updates your Path of Exile 2 loot filters based on real-time market valuations and build-specific necessities.

---

## 🚀 Key Features

- **🎮 Dynamic Click-Through HUD Overlay**: Toggle between **Edit Mode** (interactive dashboard) and **HUD Mode** (completely transparent, click-through overlay) using the `Ctrl + Shift + O` hotkey. The dashboard and the HUD have **independent opacity controls**, and the whole dashboard (not just its border) fades with the Dashboard Opacity slider.
- **🗂️ Dashboard / Settings split**: The main dashboard stays focused on live state and economy data — Active State (top-left), build config, and the full-width commodities panel — while paths, economy thresholds, appearance, and HUD options live on a dedicated **Settings** page reached via the header gear.
- **📈 Real-Time Economy Sync** *(Currency Exchange Rates tab)*: Fetches real-time commodity, currency, and unique valuations from `poe.ninja` for the Path of Exile 2 Trade Economy focusing on Exalted Orbs, Divine Orbs, and Mirrors (excluding Chaos Orbs as a player-to-player trade base), ranked highest-to-lowest by value.
- **🛒 Trade Market & BiS Pricing** *(Trade Market tab)*: Prices the gear in your loaded `.build` against the **official Path of Exile 2 trade site** (`pathofexile.com/trade2`). It displays the **cheapest live listings you can currently equip** — filtered by character level, with uniques excluded from base-type searches. It also supports **Best-in-Slot (BiS) mode**, letting you configure custom affix weights (`Required`, `Very Important`, `Important`, `Wanted`) to dynamically generate GGG weighted queries and rank live listings by build utility.
- **🔄 Dynamic Metadata Updates**: Harvests GGG's official `/api/trade2/data/static` catalog and scrapes `poe2db.tw` to dynamically discover and update weapon/armor item bases and economy sync categories without needing to recompile the app.
- **📋 Build Planner Integration**:
  - Parses native Path of Exile 2 `.build` JSON schemas.
  - Decodes and parses Path of Building 2 (PoB2) compressed XML share codes.
  - Remembers the last loaded `.build` file and automatically reloads it (and resyncs economy data) on startup.
- **🔍 Active Zone & Character Tracking**: Monitors your `Client.txt` log file to detect zone transitions and character level-ups in real time. On startup it identifies the **character you're actually playing** (from the active-character instance-load line, not merely whichever character leveled up most recently) and recovers its name and level by scanning backwards through the log — so the HUD starts with accurate state even with multiple characters and very large log files.
- **🛡️ Custom Filter Builder**: Generates tailored `.filter` files on-the-fly, highlighting high-value economic drops and build-specific uniques suitable for your current level. Skill/support gems are highlighted via PoE2's "Uncut Gem" framework rather than named BaseTypes, since PoE2 only drops generic uncut gems on the ground.
- **🎨 Style Editor**: Customize filter highlight colors with HSV sliders, custom RGB input, and a screen eyedropper picker.

---

## 🛠️ Technology Stack

- **Core**: C# / WPF (.NET 9.0)
- **Windows Integration**: Win32 API Interop (Window transparency, click-through penetrability, global hotkey hooks)
- **Embedded Browser**: Microsoft Edge WebView2 — authenticated access to the official PoE2 trade API (same-origin requests through your signed-in session)
- **Installer**: [Inno Setup](https://jrsoftware.org/isinfo.php) — per-user, version-aware installer with clean in-place upgrades
- **Tests**: MSTest / xUnit test suite

---

## ⚙️ Getting Started

### Install (end users)
Download the latest `LootPulse-Setup-<version>.exe` from the
[**Releases**](https://github.com/Light-Passion/LootPulse/releases/latest) page and run it. The installer
is **per-user** (no admin rights required), detects any existing install and **upgrades in place**, and
won't downgrade over a newer version.

**Runtime requirements:**
- [.NET 9 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/9.0/runtime) — the
  installer checks for this and links the download if it's missing.
- The **WebView2 Runtime** (ships with Windows 11; preinstalled on most Win10 too) — only needed for
  the Trade Market tab's sign-in.

### Prerequisites (development)
- [.NET 9.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) or higher.

### How to Build
Restore dependencies and build the solution:
```bash
dotnet build
```

### How to Run the App
Launch the WPF desktop application:
```bash
dotnet run --project LootPulse.csproj
```

### How to Run Tests
The repository includes a comprehensive unit and integration test suite (covering log monitoring, build profile parsing, and filter compilation). Run them using:
```bash
dotnet test LootPulse.Tests/LootPulse.Tests.csproj
```

### How to Build the Installer
Produces a version-aware `LootPulse-Setup-<version>.exe` (framework-dependent, per-user). Requires
[Inno Setup 6](https://jrsoftware.org/isdl.php) (`winget install --id JRSoftware.InnoSetup -e`):
```powershell
pwsh installer\build-installer.ps1
```
The version is taken from `LootPulse.csproj` `<Version>`; the output lands in `installer\Output\`.

---

## 📄 License & Disclaimers
This is a fan-made tool and is not affiliated with, authorized, or endorsed by Grinding Gear Games. Path of Exile 2 is a trademark of Grinding Gear Games.
