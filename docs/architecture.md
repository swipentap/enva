### EnvA Architecture

EnvA is structured as a Go module with a clear separation between configuration, services, actions, CLI entrypoint, and orchestration.

#### High-Level Components

- **`main.go`**: Cobra-based CLI entrypoint; wires commands (`deploy`, `cleanup`, `redeploy`, `status`, `backup`, `restore`) to their implementations and initializes logging and configuration.
- **`libs/`**: Core library layer.
  - **`config.go`**: Configuration loading/parsing; converts YAML into `LabConfig` and related structs.
  - **`logger.go`**: Logging initialization and helpers (`InitLogger`, `GetLogger`, log levels, file handling).
  - **`common.go` / `interfaces.go` / `command.go`**: Shared helpers, interfaces, and command abstractions used by services and actions.
  - **`template.go`**: Template rendering utilities (e.g., configuration files pushed into containers).
- **`services/`**: Abstractions over external systems.
  - **`ssh.go`**: SSH service for running commands remotely (with optional verbose echoing of stdout).
  - **`lxc.go` / `pct.go`**: LXC/PCT wrappers for container lifecycle management.
  - **`apt.go`**: APT-related helpers.
  - **`template.go`**: Service for applying templates remotely.
- **`cli/`**: Thin wrappers around system tools used by services and actions.
  - Files like `apt.go`, `docker.go`, `cloudinit.go`, `pct.go`, `systemctl.go`, `vzdump.go`, etc. each encapsulate calls to a specific command-line tool with consistent logging and error handling.
- **`actions/`**: Higher-level units of work executed as steps in deployment/cleanup flows.
  - Examples include configuring APT cache, installing base tools, installing Docker/k3s/PostgreSQL, configuring HAProxy, setting sysctls, creating templates, and cleaning them up.
  - `plan.go` and `registry.go` coordinate the ordered list of actions for a deployment.
- **`commands/`**: User-facing operations invoked by the CLI.
  - **`deploy.go`**: Implements the multi-step deployment flow using `actions` and `services`.
  - **`cleanup.go`**: Removes containers/templates and performs environment cleanup.
  - **`status.go`**: Inspects the current environment and reports status.
  - **`backup.go` / `restore.go`**: Backup/restore operations driven by configuration.
- **`orchestration/`**: Orchestrators for specific stacks.
  - **`kubernetes.go`**: Logic for standing up and configuring k3s clusters.
  - **`gluster.go`**: Logic for GlusterFS orchestration.

#### Execution Flow

1. **CLI Entry**
   - `main.go` initializes logging based on `-v/--verbose` and constructs the Cobra command tree.
   - Global flags (`--config`, `--verbose`) are bound to package-level variables.

2. **Config Resolution**
   - When a command runs, it calls `getConfig(environment)`.
   - `getConfig`:
     - Determines the config path (explicit `--config`, executable directory `enva.yaml`, or current directory fallback).
     - Uses `libs.LoadConfig` to parse YAML.
     - Uses `libs.FromDict` to build a typed `LabConfig`, including selected environment.

3. **Service Wiring**
   - `LXCService` and `PCTService` are instantiated from `services` using SSH configuration from `LabConfig`.
   - These services hide raw SSH/CLI details from the higher-level commands.

4. **Command Logic**
   - High-level command structs from `commands/` (`NewDeploy`, `NewCleanup`, etc.) receive `LabConfig` and services.
   - They orchestrate `actions` in a defined order, logging each step and handling partial failures according to design (e.g., `redeploy` ignores cleanup errors to match the original Python behavior).

5. **Actions and CLI Wrappers**
   - Each `actions/*.go` file encapsulates one logical step (e.g., "install Docker", "configure HAProxy", "set sysctl overrides").
   - Actions use `services` APIs, which in turn delegate to `cli` helpers for concrete commands (e.g., `pct`, `systemctl`, `docker`).

#### Design Goals

- **Parity with original Python implementation**: logging behavior, error handling, and step sequencing are kept close to the original.
- **Testable units**: services and actions are broken down so they can be tested in isolation where practical.
- **Clear layering**: top-level commands → actions → services → CLI wrappers.

