# Access Points - K3s Cluster

## Cluster Information

### Kubernetes Cluster Nodes
- **Control Node:** `k3s-control` - `10.11.2.11`
- **Worker Nodes:**
  - `k3s-worker-1` - `10.11.2.12`
  - `k3s-worker-2` - `10.11.2.13`
  - `k3s-worker-3` - `10.11.2.14`

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

---

## Kubernetes API Access

### Server URL
- **Internal:** `https://kubernetes.default.svc`
- **External:** Not exposed (access via SSH to control node)

### kubectl Access
# Direct command
kubectl <command>

```


