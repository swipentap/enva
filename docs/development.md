### Development and Build

#### Requirements

- Go toolchain (version as specified in `go.mod`).
- Access to a Proxmox/LXC environment for full integration testing.

#### Build

```bash
# From repository root
cd src

go build -o enva main.go
```

#### Run

```bash
# Show help
./enva --help

# Use a specific config file
./enva -c /path/to/enva.yaml deploy lab

# Enable verbose SSH output
./enva -v deploy lab
```

#### Testing / Validation

- Unit tests (if present) can be run with:

```bash
cd src

go test ./...
```

- Integration testing typically requires:
  - A reachable LXC/Proxmox host with the expected templates.
  - An `enva.yaml` describing your lab environment.

#### Debian Packaging (Overview)

- The `debian/` directory contains standard Debian packaging metadata:
  - `control`, `changelog`, `rules`, `source/format`, etc.
- A typical workflow (on a Debian/Ubuntu machine with build tools installed) might look like:

```bash
# From repository root
# (commands may vary depending on your packaging setup)
dpkg-buildpackage -us -uc
```

This produces `.deb` packages that can be installed with `dpkg -i` or `apt` as appropriate.

#### Project Layout

- **`src/`**: Go module root (code, `go.mod`, `go.sum`, `enva.yaml` example).
- **`src/README.md`**: Additional project details specific to the Go implementation.
- **`debian/`**: Packaging configuration.
- **`docs/`**: This documentation set.

