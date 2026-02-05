### EnvA CLI

EnvA is a CLI for deploying and managing Kubernetes (k3s) clusters and supporting services on LXC containers (HAProxy, ArgoCD, etc.).

#### Global flags

- **`-v, --verbose`**: Show stdout from SSH service and enable debug-level logging.
- **`-c, --config`**: Path to YAML configuration file (default: `enva.yaml` in `AppContext.BaseDirectory` or current directory).
- **`--github-token`**: GitHub token for creating GitHub runner secrets (used during deploy when applicable).

#### Commands

- **`enva init`**

  - **Description**: Create `enva.yaml` from the embedded example.
  - **Flags**:
    - **`-o, --output`**: Output path (default: `./enva.yaml`).
    - **`--force`**: Overwrite existing file.
  - **Examples**:
    - `enva init`
    - `enva init -o my-enva.yaml`
    - `enva init --force`
- **`enva deploy [environment]`**

  - **Description**: Deploy complete environment: apt-cache, templates, LXC containers, k3s, ArgoCD, etc., according to `enva.yaml`.
  - **Arguments**:
    - **`environment`**: Name of the environment in `enva.yaml` (e.g. `dev`, `test`, `prod`).
  - **Flags**:
    - **`--start-step int`**: Start from this step (default: 1).
    - **`--end-step int`**: End at this step (default: last step, 0 means last).
    - **`--planonly`**: Show deployment plan and exit without executing.
    - **`--update-control-node-ssh-key`**: After deploy, update `~/.ssh/known_hosts` for the K3s control node.
    - **`--get-ready-kubectl`**: After deploy, configure kubectl context for this environment.
  - **Examples**:
    - `enva deploy dev`
    - `enva deploy dev --start-step 3`
    - `enva deploy dev --planonly`
    - `enva deploy dev --get-ready-kubectl`
- **`enva cleanup [environment]`**

  - **Description**: Remove all containers and templates for the given environment.
  - **Arguments**:
    - **`environment`**: Name of the environment in `enva.yaml`.
  - **Examples**:
    - `enva cleanup dev`
- **`enva redeploy [environment]`**

  - **Description**: Cleanup and then deploy the complete environment.
  - **Arguments**:
    - **`environment`**: Name of the environment in `enva.yaml`.
  - **Flags**:
    - **`--start-step int`**, **`--end-step int`**, **`--planonly`**, **`--update-control-node-ssh-key`**, **`--get-ready-kubectl`**: Same as for `deploy`.
  - **Examples**:
    - `enva redeploy dev`
    - `enva redeploy dev --get-ready-kubectl`
- **`enva status [environment]`**

  - **Description**: Show current environment status (containers, templates) as defined in `enva.yaml`.
  - **Arguments**:
    - **`environment`**: Name of the environment in `enva.yaml`.
  - **Examples**:
    - `enva status dev`

#### Exit codes

- **`0`**: Command completed successfully.
- **Non-zero**: An error occurred; details are printed to stderr and logged via the EnvA logger.
