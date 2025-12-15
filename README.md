# EnvA 

This repository contains the Go implementation of **EnvA**, a CLI tool for managing LXC containers, Docker-based environments, and related infrastructure automation.

The main application code lives under the `src/` directory, which includes the Go module, CLI entrypoint, and supporting packages.

## Quick Start

```bash
# From the repository root
cd src

go build -o enva main.go

./enva --help
```

## Documentation

Additional documentation lives under the `docs/` directory:

- `docs/cli.md` – EnvA CLI commands, flags, and examples.
- `docs/architecture.md` – High-level architecture and layering (libs, services, actions, commands, orchestration).
- `docs/configuration.md` – How `enva.yaml` is found and structured conceptually.
- `docs/development.md` – Build, run, test, and Debian packaging notes.

For more details about the Go implementation itself, also see `src/README.md`.

