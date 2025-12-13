# EnvA - Go Implementation

This is the Go implementation of the EnvA CLI tool for managing Proxmox LXC containers and Docker Swarm.

## Building

```bash
cd src
go build -o enva main.go
```

## Running

```bash
./enva --help
./enva deploy
./enva status
./enva cleanup
```

## Project Structure

- `libs/` - Core libraries (config, logger, common utilities)
- `services/` - Service abstractions (SSH, LXC, PCT, APT, Template)
- `cli/` - CLI command wrappers
- `actions/` - Action modules for container setup
- `commands/` - Command implementations (deploy, cleanup, status, backup, restore)
- `orchestration/` - Orchestration modules (Kubernetes, GlusterFS)
- `main.go` - Main entry point

## Configuration

Configuration is loaded from `enva.yaml` (same format as Python version).

## Status

This is a work-in-progress conversion from Python to Go. Core infrastructure is in place:
- Configuration loading and parsing
- SSH service
- LXC/PCT services
- Basic command structure
- CLI framework (Cobra)

Remaining work:
- Complete CLI wrappers
- Implement all action modules
- Complete command implementations
- Add orchestration modules
- Add tests

