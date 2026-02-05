# EnvA

CLI for deploying and managing Kubernetes (k3s) clusters with supporting services on LXC containers (HAProxy, ArgoCD, etc.).

## Install

**Homebrew (macOS):**

```bash
brew tap swipentap/enva
brew install enva
```

**From source:**

```bash
cd src
dotnet build -c Release
dotnet run -c Release -- --help
```

## Quick Start

Create a config from the example, then deploy:

```bash
enva init                    # create ./enva.yaml from example
enva init -o my-enva.yaml    # or write to a custom path
enva deploy dev              # deploy the dev environment
```

Use `--config` / `-c` to point at a config file; default is `enva.yaml` in the current directory.

## Commands

| Command | Description |
|---------|-------------|
| `init` | Create `enva.yaml` from the embedded example (`-o` path, `--force` to overwrite) |
| `deploy <env>` | Deploy environment (templates, LXC containers, k3s, ArgoCD, etc.) |
| `redeploy <env>` | Cleanup then deploy |
| `cleanup <env>` | Remove all containers and templates for the environment |
| `status <env>` | Show containers and templates for the environment |

**Deploy options:** `--start-step`, `--end-step`, `--planonly`, `--update-control-node-ssh-key`, `--get-ready-kubectl`

**Global options:** `--config` / `-c`, `--verbose` / `-v`, `--github-token`

## Documentation

- `docs/cli.md` – CLI commands, flags, and examples
- `docs/architecture.md` – Architecture and layering (libs, services, actions, commands, orchestration)
- `docs/configuration.md` – How `enva.yaml` is found and structured
- `docs/development.md` – Build, run, test

Source: `src/` (C# .NET 9, single-file publish for releases).
