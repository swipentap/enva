# Access Points - K3s Cluster

## Cluster Information

### Kubernetes Cluster Nodes
- **Control Node:** `k3s-control` - `10.11.2.11`
- **Worker Nodes:**
  - `k3s-worker-1` - `10.11.2.12`
  - `k3s-worker-2` - `10.11.2.13`
  - `k3s-worker-3` - `10.11.2.14`

### SSH Access
```bash
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11
```

**Note:** When running kubectl commands via SSH, use `sudo`:
```bash
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl <command>"
```

---

## ArgoCD

### Web UI Access
- **HTTP NodePort:** `30083`
- **HTTPS NodePort:** `30444`
- **Access URLs:**
  - HTTP: `http://10.11.2.11:30083` (or any node IP)
  - HTTPS: `https://10.11.2.11:30444` (or any node IP)

### Credentials
- **Username:** `admin`
- **Password:** `x9YrGXQyIa4BmNgP`

### Namespace
- **Namespace:** `argocd`

### CLI Access
```bash
# Via SSH
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl get application -n argocd"

# Get admin password
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl get secret -n argocd argocd-initial-admin-secret -o jsonpath='{.data.password}' | base64 -d"
```

---

## Weave GitOps (Flux Web UI)

### Web UI Access
- **NodePort:** `31385`
- **Access URLs:**
  - `http://10.11.2.11:31385` (control node)
  - `http://10.11.2.12:31385` (worker-1)
  - `http://10.11.2.13:31385` (worker-2)
  - `http://10.11.2.14:31385` (worker-3)

### Credentials
- **Username:** `admin`
- **Password:** `admin123`

### Namespace
- **Namespace:** `flux-system`

### Port-Forward Alternative (if needed)
```bash
# Port-forward via SSH
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl port-forward -n flux-system svc/weave-gitops 9001:9001"
# Then access: http://localhost:9001

# Or directly to deployment
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl port-forward -n flux-system deployment/weave-gitops 9001:9001"
```

---

## Flux (FluxCD)

### CLI Access
Flux CLI is available on the remote server. Connect via SSH first:
```bash
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11

# Then run Flux commands
flux check                    # Check Flux components status
flux get all                  # List all Flux resources
flux get helmreleases         # List Helm releases
flux get sources              # List sources (Git repos, Helm repos)
```

### Kubernetes Resources Access
```bash
# Via SSH with kubectl
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl get helmreleases -A"
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl get gitrepositories -A"
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl get helmrepositories -A"
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl get kustomizations -A"
```

### Namespace
- **Namespace:** `flux-system`

### Check Status
```bash
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl get pods -n flux-system"
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl logs -n flux-system -l app=helm-controller"
```

### Version
- **Flux Version:** 2.7.5
- **Helm Chart Version:** 2.17.2

---

## CockroachDB

### Service Endpoints
- **Cluster Service:** `cockroachdb.cockroachdb.svc.cluster.local`
  - SQL Port: `26257`
  - HTTP Port: `8080`
- **Public Service:** `cockroachdb-public.cockroachdb.svc.cluster.local`
  - SQL Port: `26257`
  - HTTP Port: `8080`

### Pod FQDNs (when running)
- `cockroachdb-0.cockroachdb.cockroachdb.svc.cluster.local:26257`
- `cockroachdb-1.cockroachdb.cockroachdb.svc.cluster.local:26257`
- `cockroachdb-2.cockroachdb.cockroachdb.svc.cluster.local:26257`

### Namespace
- **Namespace:** `cockroachdb`

### Status
- **Current Status:** Not successfully deployed
- **Application:** Configured in ArgoCD but stuck in OutOfSync/Missing state
- **Chart:** Official CockroachDB chart version `19.0.4` from `https://charts.cockroachdb.com/`

### Access (when deployed)
```bash
# Port-forward to SQL port
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl port-forward -n cockroachdb svc/cockroachdb-public 26257:26257"

# Port-forward to Admin UI
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl port-forward -n cockroachdb svc/cockroachdb-public 8080:8080"
# Then access: http://localhost:8080
```

---

## Kubernetes API Access

### Server URL
- **Internal:** `https://kubernetes.default.svc`
- **External:** Not exposed (access via SSH to control node)

### kubectl Access
All kubectl commands must be run on the remote server or via SSH:
```bash
# Direct SSH command
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl <command>"

# Or SSH into server first
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11
sudo kubectl get nodes
sudo kubectl get pods -A
```

---

## Helm Repositories

### Configured Repositories
- **swipentap:** `https://swipentap.github.io/charts`
- **cockroachdb (official):** `https://charts.cockroachdb.com/`
- **fluxcd:** `https://fluxcd-community.github.io/helm-charts`
- **weaveworks (OCI):** `oci://ghcr.io/weaveworks/charts/weave-gitops`

### List Repositories
```bash
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo helm repo list"
```

---

## Namespaces

### Active Namespaces
- `argocd` - ArgoCD installation
- `flux-system` - Flux and Weave GitOps
- `cockroachdb` - CockroachDB (configured but not deployed)
- `kube-system` - Kubernetes system components

### Check Namespaces
```bash
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl get namespaces"
```

---

## Quick Reference Commands

### SSH to Cluster
```bash
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11
```

### Check Cluster Status
```bash
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl get nodes"
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl get pods -A"
```

### Access ArgoCD UI
- Open browser: `http://10.11.2.11:30083`
- Login: `admin` / `x9YrGXQyIa4BmNgP`

### Access Weave GitOps UI
- Open browser: `http://10.11.2.11:31385`
- Login: `admin` / `admin123`

### Check ArgoCD Applications
```bash
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl get application -n argocd"
```

### Check Flux Resources
```bash
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl get helmreleases -A"
ssh -i ~/.ssh/id_rsa jaal@10.11.2.11 "sudo kubectl get gitrepositories -A"
```
