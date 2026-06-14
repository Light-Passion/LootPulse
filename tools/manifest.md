# Tools Manifest

This manifest documents the core automation and execution scripts used within the development workflow.

## Build and Run Tools

- **Restore Dependencies**:
  - Command: `dotnet restore`
  - Purpose: Restores NuGet packages required for the project.
- **Build Project**:
  - Command: `dotnet build`
  - Purpose: Builds the LootPulse WPF project.
- **Run Application**:
  - Command: `dotnet run --project LootPulse.csproj`
  - Purpose: Launches the desktop application.
- **Run Tests**:
  - Command: `dotnet test`
  - Purpose: Runs any automated unit and integration tests.
