# LootPulse ⚡ (Path of Exile 2 Overlay)

> [!WARNING]
> **Project Status: Incomplete & Under Development / Testing**
> This application is currently in an early development and testing phase. Features are subject to change, and bugs are expected. Use with caution.

LootPulse is a lightweight, high-performance WPF overlay helper for **Path of Exile 2**, designed to streamline your in-game trading, zone tracking, and build progression.

The tool provides a transparent, click-through overlay window that monitors your game state and dynamically updates your Path of Exile 2 loot filters based on real-time market valuations and build-specific necessities.

---

## 🚀 Key Features

- **🎮 Dynamic Click-Through HUD Overlay**: Toggle between **Edit Mode** (adjust thresholds, select profiles, configure settings) and **HUD Mode** (completely transparent, click-through overlay) using the `Ctrl + Shift + O` hotkey.
- **📈 Real-Time Economy Sync**: Fetches real-time commodity, currency, and unique valuations from `poe.ninja` for the Path of Exile 2 Trade Economy (focusing on Exalted, Divine, and Chaos Orbs).
- **📋 Build Planner Integration**: 
  - Parses native Path of Exile 2 `.build` JSON schemas.
  - Decodes and parses Path of Building 2 (PoB2) compressed XML share codes.
- **🔍 Active Zone & Level Tracking**: Automatically monitors your `Client.txt` log file to detect zone transitions and character leveling dynamically.
- **🛡️ Custom Filter Builder**: Generates tailored `.filter` files on-the-fly, highlighting high-value economic drops and build-specific skill gems or uniques suitable for your current level.

---

## 🛠️ Technology Stack

- **Core**: C# / WPF (.NET 9.0)
- **Windows Integration**: Win32 API Interop (Window transparency, click-through penetrability, global hotkey hooks)
- **Tests**: MSTest / xUnit test suite

---

## ⚙️ Getting Started

### Prerequisites
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

---

## 📄 License & Disclaimers
This is a fan-made tool and is not affiliated with, authorized, or endorsed by Grinding Gear Games. Path of Exile 2 is a trademark of Grinding Gear Games.
