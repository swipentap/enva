# GitHub Runner Installation - Continuation Guide

## Current State

### ArgoCD Applications
- **github-runner** (Helm chart): OutOfSync, Progressing
  - Chart: `actions-runner-controller/actions-runner-controller` v0.23.7
  - Issue: CRDs fail to install due to annotation size limit (262KB)
  - Error: "metadata.annotations: Too long: may not be more than 262144 bytes"
  - Location: `argocd-prod/applications/github-runner.yaml`

- **github-runner-manifests** (manifests): OutOfSync, Healthy
  - Path: `manifests/github-runner`
  - Contains: Secret (placeholder), RunnerDeployment
  - Location: `argocd-prod/applications/github-runner-manifests.yaml`

### Kubernetes Cluster State
- **Namespace**: `actions-runner-system` exists
- **Secret**: `controller-manager` exists (created manually with token)
- **Controller Pod**: `actions-runner-controller` in CrashLoopBackOff (CRDs missing)
- **CRDs Installed**: Only `horizontalrunnerautoscalers.actions.summerwind.dev`
- **CRDs Missing**: `runnerdeployments`, `runnerreplicasets`, `runners`, `runnersets`
- **RunnerDeployment**: Does not exist (was manually deleted)

### GitHub Runner Configuration
- **Token**: `ghp_REDACTED`
- **Organization**: `swipentap`
- **Runner Group**: `k3s-prod`
- **Replicas**: 3
- **Labels**: `k3s`

## Problem

The Helm chart's CRDs have annotations that exceed Kubernetes' 262KB limit when installed via normal `kubectl apply`. ArgoCD uses normal apply, so it cannot install them. Server-side apply (`kubectl apply --server-side`) bypasses this validation and works.

## Files

### ArgoCD Applications
- `argocd-prod/applications/github-runner.yaml` - Helm chart application with `SkipCRDs=true` (not working)
- `argocd-prod/applications/github-runner-manifests.yaml` - Manifests application

### Manifests
- `argocd-prod/manifests/github-runner/secret.yaml` - Token secret (has placeholder "REPLACE_WITH_TOKEN")
- `argocd-prod/manifests/github-runner/runner-deployment.yaml` - Runner deployment spec
- `argocd-prod/manifests/github-runner/crds.yaml` - Placeholder file

## What Needs to Be Done

1. **Install CRDs**: CRDs must be installed using server-side apply before ArgoCD can sync the Helm chart
2. **Fix ArgoCD Sync**: Either configure ArgoCD to use server-side apply for CRDs, or install CRDs separately and let ArgoCD manage the rest
3. **Create RunnerDeployment**: Once CRDs exist, ArgoCD should be able to create the RunnerDeployment from manifests
4. **Verify Runners**: Ensure 3 runner pods are running and registered in GitHub under group `k3s-prod`

## Commands Reference

### Install CRDs with server-side apply
```bash
helm show crds actions-runner-controller/actions-runner-controller --version 0.23.7 | \
  ssh root@10.11.2.4 "cat > /tmp/arc-crds.yaml && \
  pct push 2011 /tmp/arc-crds.yaml /tmp/arc-crds.yaml && \
  pct exec 2011 -- bash -c 'export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && \
  kubectl apply --server-side -f /tmp/arc-crds.yaml'"
```

### Check status
```bash
ssh root@10.11.2.4 "pct exec 2011 -- bash -c 'export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && \
  kubectl get crd | grep actions && \
  kubectl get pods -n actions-runner-system && \
  kubectl get runnerdeployment -n actions-runner-system'"
```

### Create secret manually (if needed)
```bash
ssh root@10.11.2.4 "pct exec 2011 -- bash -c 'export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && \
  kubectl create secret generic controller-manager \
  --from-literal=github_token=\"ghp_REDACTED\" \
  -n actions-runner-system --dry-run=client -o yaml | kubectl apply -f -'"
```

## Cluster Access

- **Proxmox Host**: `root@10.11.2.4`
- **k3s Control Node**: Container ID `2011`
- **kubeconfig**: `/etc/rancher/k3s/k3s.yaml` (inside container)
- **SSH Command Pattern**: `ssh root@10.11.2.4 "pct exec 2011 -- bash -c 'export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && <kubectl command>'`

## Important Notes

- **DO NOT** manually create resources - let ArgoCD manage them
- **DO NOT** assume or decide anything - ask if unsure
- GitHub blocks pushes containing tokens - secret must be created manually or use external secrets
- The Helm chart is official and should work - the issue is ArgoCD's inability to install large CRDs
- Server-side apply is the only way to install these CRDs due to annotation size

## Related Work

- SiNS health check was enhanced to verify database connectivity and tables
- Ingress domains were made configurable for SiNS, Ollama, and Certa charts
- Some ingresses still use hardcoded `dev.net` (Rancher, pgAdmin, PostgreSQL, SonarQube) - these are raw manifests, not Helm charts
