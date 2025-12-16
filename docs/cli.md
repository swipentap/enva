### EnvA CLI

EnvA is a Go-based CLI tool for deploying and managing Kubernetes (k3s) clusters and supporting services on LXC containers.

#### Global Flags

- **`-v, --verbose`**: Show stdout from SSH service and enable debug-level logging.
- **`-c, --config`**: Path to YAML configuration file (default: `enva.yaml` next to the binary or in the current directory).

#### Commands

- **`enva deploy [environment]`**
  - **Description**: Deploy complete environment: apt-cache, templates, and Docker-based (or k3s) cluster according to `enva.yaml`.
  - **Arguments**:
    - **`environment`**: Name of the environment in `enva.yaml`.
  - **Flags**:
    - **`--start-step int`**: Start from this step (default: 1).
    - **`--end-step int`**: End at this step (default: last step, 0 means last).
    - **`--planonly`**: Show deployment plan and exit without executing.
  - **Examples**:
    - `enva deploy lab`
    - `enva deploy lab --start-step 3`
    - `enva deploy lab --planonly`

- **`enva cleanup [environment]`**
  - **Description**: Remove all containers and templates for the given environment.
  - **Arguments**:
    - **`environment`**: Name of the environment in `enva.yaml`.
  - **Examples**:
    - `enva cleanup lab`

- **`enva redeploy [environment]`**
  - **Description**: Cleanup and then deploy the complete environment (convenience wrapper around `cleanup` + `deploy`).
  - **Arguments**:
    - **`environment`**: Name of the environment in `enva.yaml`.
  - **Flags**:
    - **`--start-step int`**: Start from this step (default: 1).
    - **`--end-step int`**: End at this step (default: last step, 0 means last).
  - **Notes**:
    - Cleanup errors are logged but do not prevent the deploy from running (matches original Python behavior).
  - **Examples**:
    - `enva redeploy lab`
    - `enva redeploy lab --start-step 2`

- **`enva status [environment]`**
  - **Description**: Show current environment status (containers, templates, services) as defined in `enva.yaml`.
  - **Arguments**:
    - **`environment`**: Name of the environment in `enva.yaml`.
  - **Examples**:
    - `enva status lab`

- **`enva backup [environment]`**
  - **Description**: Backup cluster according to `enva.yaml` configuration.
  - **Arguments**:
    - **`environment`**: Name of the environment in `enva.yaml`.
  - **Examples**:
    - `enva backup lab`

- **`enva restore [environment]`**
  - **Description**: Restore cluster from backup.
  - **Arguments**:
    - **`environment`**: Name of the environment in `enva.yaml`.
  - **Flags**:
    - **`--backup-name string`** (required): Name of the backup to restore (e.g. `backup-20251130_120000`).
  - **Examples**:
    - `enva restore lab --backup-name backup-20251130_120000`

#### Exit Codes

- **`0`**: Command completed successfully.
- **Non-zero**: An error occurred; details are printed to stderr and logged via the EnvA logger.

