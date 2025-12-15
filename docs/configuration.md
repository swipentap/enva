### Configuration (`enva.yaml`)

EnvA is driven by a YAML configuration file named `enva.yaml`. The file must be accessible either next to the compiled `enva` binary or via the `--config` flag.

#### Config Resolution Order

1. **`--config` flag** (`-c`): if provided, EnvA uses this path.
2. **Executable directory**: `enva.yaml` living next to the `enva` binary.
3. **Current working directory**: `enva.yaml` in the directory where you run the command.

If no file is found, EnvA will still attempt to use `enva.yaml` and fail with a descriptive error if it cannot be loaded.

#### Environments

- The top-level configuration is expected to define one or more **environments** (e.g. `lab`, `prod`, `staging`).
- CLI commands accept an `environment` argument and use it to select the corresponding environment configuration in `enva.yaml`.

#### Typical Sections (Conceptual)

Although the exact schema is defined in `libs/config.go`, the configuration typically includes:

- **Global settings**
  - Logging options and defaults.
  - Default backup paths.
- **SSH configuration**
  - Host, user, private key or auth method.
  - Options such as port, sudo usage, and connection behavior.
- **LXC/PCT settings**
  - Proxmox node or LXC host definitions.
  - Storage pools, template names, defaults for containers.
- **Environments** (per environment, e.g. `lab`)
  - Node lists (control plane, workers, storage nodes).
  - Image/template references.
  - APT cache settings and base tools.
  - Kubernetes (k3s) parameters and network configuration.
  - GlusterFS or other storage settings.
- **Backup/restore**
  - Backup destinations.
  - Retention or naming patterns.

#### Best Practices

- Keep `enva.yaml` in version control but **exclude sensitive secrets** (SSH keys, passwords) or inject them via external means.
- Define at least one non-production environment (e.g. `lab`) for testing changes.
- Use consistent naming for environments and reference the same names when invoking CLI commands.

