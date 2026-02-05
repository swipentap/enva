### Development and build

#### Requirements

- .NET 9 SDK.
- Access to an LXC/Proxmox environment for full integration testing.

#### Build

```bash
# From repository root
cd src

dotnet build -c Release
```

#### Run

```bash
# Show help
dotnet run -c Release -- --help

# Use a specific config file
dotnet run -c Release -- -c /path/to/enva.yaml deploy dev

# Verbose SSH output
dotnet run -c Release -- -v deploy dev
```

#### Publish (single-file)

```bash
cd src
dotnet publish -c Release -r osx-arm64 --self-contained true
# Binary: src/bin/Release/net9.0/osx-arm64/publish/Enva
```

Releases are built with `PublishSingleFile` so the published binary is a single executable.

#### Testing

- Unit tests (if present):

```bash
cd src
dotnet test -c Release
```

- Integration testing typically requires:
  - A reachable LXC/Proxmox host with the expected templates.
  - An `enva.yaml` describing your environment (e.g. `enva init` then edit).

#### Project layout

- **`src/`**: C# project root (`Enva.csproj`, `Program.cs`, `Libs/`, `Commands/`, `Actions/`, `Services/`, `Orchestration/`, `Verification/`, `CLI/`).
- **`enva.yaml`**: Example configuration at repo root; embedded as resource for `enva init`.
- **`docs/`**: This documentation set.
- **`.github/workflows/`**: CI (test, tag, build, release, Homebrew formula update).
