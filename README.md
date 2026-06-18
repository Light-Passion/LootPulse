# LootPulse ⚡ (Path of Exile 2 Overlay)

> [!WARNING]
> **Project Status: Incomplete & Under Development / Testing**
> This application is currently in an early development and testing phase. Features are subject to change, and bugs are expected. Use with caution.

LootPulse is a lightweight, high-performance WPF overlay helper for **Path of Exile 2**, designed to streamline your in-game trading, zone tracking, and build progression.

The tool provides a transparent, click-through overlay window that monitors your game state and dynamically updates your Path of Exile 2 loot filters based on real-time market valuations and build-specific necessities.

---

## 🚀 Key Features

- **🎮 Dynamic Click-Through HUD Overlay**: Toggle between **Edit Mode** (adjust thresholds, select profiles, configure settings) and **HUD Mode** (completely transparent, click-through overlay) using the `Ctrl + Shift + O` hotkey.
- **📈 Real-Time Economy Sync** *(Currency Exchange Rates tab)*: Fetches real-time commodity, currency, and unique valuations from `poe.ninja` for the Path of Exile 2 Trade Economy (focusing on Exalted, Divine, and Chaos Orbs), ranked highest-to-lowest by value.
- **🛒 Trade Market** *(Trade Market tab)*: Prices the gear in your loaded `.build` against the **official Path of Exile 2 trade site** (`pathofexile.com/trade2`). For each item it lists the **cheapest live listings you can currently equip** — filtered to your character's level, with uniques excluded from base-type searches — plus a one-click **Open on Trade Site** button. You sign in to pathofexile.com once via an embedded browser; requests are throttled to respect the trade API's rate limits.
- **📋 Build Planner Integration**:
  - Parses native Path of Exile 2 `.build` JSON schemas.
  - Decodes and parses Path of Building 2 (PoB2) compressed XML share codes.
  - Remembers the last loaded `.build` file and automatically reloads it (and resyncs economy data) on startup.
- **🔍 Active Zone & Level Tracking**: Monitors your `Client.txt` log file to detect zone transitions and character level-ups in real time, and scans recent log history on startup so the HUD starts with accurate state instead of defaults.
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
Download the latest `LootPulse-Setup-<version>.exe` and run it. The installer is **per-user** (no admin
rights required), detects any existing install and **upgrades in place**, and won't downgrade over a
newer version.

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
