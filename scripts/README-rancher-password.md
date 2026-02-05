# Rancher Password Secret Generator

This script generates a Kubernetes Secret for Rancher admin password using the same hashing algorithm as Rancher (PBKDF2-SHA3-512 with 210,000 iterations).

## Usage

```bash
cd scripts
go run generate-rancher-password-secret.go -password "your-password" -username "user-84slx"
```

## Options

- `-password`: Password to hash (required)
- `-username`: User resource name (e.g., `user-84slx`) (required)
- `-namespace`: Namespace for the secret (default: `cattle-local-user-passwords`)

## Example

```bash
# Generate secret for admin user
go run generate-rancher-password-secret.go -password "admin123" -username "user-84slx" > secret.yaml

# Apply to cluster
kubectl apply -f secret.yaml
```

## How it works

The script uses the same algorithm as Rancher:
- **Algorithm**: PBKDF2 with SHA3-512
- **Iterations**: 210,000
- **Key length**: 32 bytes
- **Salt length**: 32 bytes (random)

This matches the implementation in `github.com/rancher/rancher/pkg/auth/providers/local/pbkdf2`.
