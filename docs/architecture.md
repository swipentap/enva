### EnvA Architecture

EnvA is a C# (.NET 9) application with a clear separation between configuration, services, actions, CLI entrypoint, and orchestration.

#### High-level components

- **`Program.cs`**: System.CommandLine-based CLI entrypoint; wires commands (`init`, `deploy`, `cleanup`, `redeploy`, `status`) to their implementations and initializes logging and config resolution.
- **`Libs/`**: Core library layer.
  - **`ConfigLoader.cs`**: Configuration loading/parsing; converts YAML into `LabConfig` and related types.
  - **`Logger.cs`**: Logging initialization and helpers (`InitLogger`, `GetLogger`, log levels, file handling).
  - **`LabConfig.cs`**, **`EnvaConfig.cs`**: Typed configuration and environment selection.
  - **`Template.cs`**: Template rendering utilities.
- **`Services/`**: Abstractions over external systems.
  - **`SSHService.cs`**: SSH service for running commands remotely (with optional verbose echoing of stdout).
  - **`LXCService.cs`** / **`PCTService.cs`**: LXC/PCT wrappers for container lifecycle management.
  - **`APTService.cs`**: APT-related helpers.
  - **`TemplateService.cs`**: Service for applying templates remotely.
- **`CLI/`**: Thin wrappers around system tools used by services and actions.
  - Files such as `Apt.cs`, `PCT.cs`, `CloudInit.cs`, `Files.cs`, `SystemCtl.cs`, `Vzdump.cs`, etc., encapsulate calls to specific command-line tools with consistent logging and error handling.
- **`Actions/`**: Higher-level units of work executed as steps in deployment/cleanup flows.
  - Examples: APT cache configuration, base tools installation, k3s installation, HAProxy configuration, sysctl overrides, template creation and cleanup.
  - **`Init.cs`** and **`Registry.cs`** coordinate the ordered list of actions for a deployment.
- **`Commands/`**: User-facing operations invoked by the CLI.
  - **`DeployCommand.cs`**: Multi-step deployment flow using actions and services.
  - **`CleanupCommand.cs`**: Removes containers/templates and performs environment cleanup.
  - **`StatusCommand.cs`**: Inspects the current environment and reports status.
  - **`InitCommand.cs`**: Writes the embedded example `enva.yaml` to disk.
  - **`UpdateControlNodeSshKeyCommand.cs`**, **`GetReadyKubectlCommand.cs`**: Post-deploy helpers for SSH and kubectl.
- **`Orchestration/`**: Orchestrators for specific stacks.
  - **`Kubernetes.cs`**: Logic for standing up and configuring k3s clusters (including Rancher, ArgoCD, cert-manager, etc.).
  - **`Gluster.cs`**: GlusterFS orchestration.

#### Execution flow

1. **CLI entry**
   - `Program.cs` initializes logging based on `-v/--verbose` and builds the System.CommandLine command tree.
   - Global options (`--config`, `--verbose`, `--github-token`) are bound and passed into command handlers.

2. **Config resolution**
   - When a command runs, it calls `GetConfig(environment)`.
   - `GetConfig`:
     - Resolves the config path (explicit `--config`, then `AppContext.BaseDirectory`/`enva.yaml`, then current directory `enva.yaml`).
     - Uses `ConfigLoader.LoadConfig` to parse YAML.
     - Uses `ConfigLoader.ToLabConfig` to build a typed `LabConfig` for the selected environment.

3. **Service wiring**
   - `LXCService` and `PCTService` are created from config (SSH host and options from `LabConfig`).
   - These services hide raw SSH/CLI details from the higher-level commands.

4. **Command logic**
   - Command classes in `Commands/` receive `LabConfig` and services.
   - They orchestrate actions in a defined order, logging each step and handling failures (e.g. `redeploy` continues after cleanup errors).

5. **Actions and CLI wrappers**
   - Each action in `Actions/` encapsulates one logical step (e.g. install k3s, configure HAProxy).
   - Actions use service APIs, which delegate to `CLI/` helpers for concrete commands (e.g. `pct`, `systemctl`).

#### Design goals

- **Testable units**: Services and actions are structured so they can be tested in isolation where practical.
- **Clear layering**: Commands → actions → services → CLI wrappers.
