### Configuration (`enva.yaml`)

EnvA is driven by a YAML configuration file named `enva.yaml`. The file can be created with `enva init` or placed where EnvA will find it.

#### Config resolution order

1. **`--config` flag** (`-c`): If provided, EnvA uses this path.
2. **Base directory**: `enva.yaml` in `AppContext.BaseDirectory` (e.g. directory of the running executable, or current directory when published as single-file).
3. **Current working directory**: `enva.yaml` in the directory where you run the command.

If no file is found, EnvA still attempts to use the default path and fails with a descriptive error if the file cannot be loaded.

#### Creating a config

```bash
enva init                # create ./enva.yaml from embedded example
enva init -o my.yaml      # create my.yaml
enva init --force        # overwrite existing enva.yaml
```

#### Environments

- The top-level configuration defines one or more **environments** (e.g. `dev`, `test`, `prod`).
- CLI commands take an `environment` argument and use it to select the corresponding environment block in `enva.yaml`.

#### Typical sections (conceptual)

The schema is defined in `Libs/ConfigLoader.cs` and `Libs/LabConfig.cs`. The configuration typically includes:

- **`environments`**: Per-environment settings (id-base, network, domain, branch, LXC host, storage, bridge, template_dir, etc.).
- **`templates`**: Base template definitions (name, id, resources, actions).
- **`ct`**: Container definitions (haproxy, k3s-control, k3s-workers, etc.) with template, resources, params, and actions.
- **`kubernetes`**: Control/worker node IDs, and actions (e.g. install metallb, install argocd, install argocd apps).
- **`timeouts`**, **`users`**, **`services`**, **`dns`**, **`template_config`**, **`waits`**, **`glusterfs`**: Optional sections for timeouts, users, service ports, DNS, template patterns, and GlusterFS.

#### Best practices

- Keep `enva.yaml` in version control but **exclude sensitive secrets** (SSH keys, passwords) or inject them via environment or external means.
- Define at least one non-production environment (e.g. `dev`) for testing.
- Use consistent environment names when invoking CLI commands (`enva deploy dev`, `enva status dev`).
