# EnvA Deployment Tool

Deploys and manages a Kubernetes (k3s) cluster with supporting services on Proxmox LXC containers.

## Components
- **k3s cluster**: Control node + 3 worker nodes
- **PostgreSQL**: Database server
- **HAProxy**: Load balancer
- **DNS**: SiNS DNS server
- **GlusterFS**: Distributed storage (3 nodes)
- **Rancher**: Kubernetes management UI
- **Backup**: Automated backup/restore system

## Usage
- `enva.py deploy` - Deploy entire environment
- `enva.py redeploy` - Redeploy from scratch
- `enva.py backup` - Create backup
- `enva.py restore --backup-name <name>` - Restore from backup
- `enva.py status` - Show cluster status


