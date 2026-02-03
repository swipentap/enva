# GitHub Runner ArgoCD Installation - Chat History

## Timeline

### 1. SiNS Health Check Enhancement
- **Issue**: SiNS web UI returning 500 errors, database tables missing
- **Root Cause**: `EnsureCreated()` failed silently because PostgreSQL wasn't fully ready when pods started
- **Fix**: Enhanced health check endpoint to verify database connectivity and all 4 required tables (DnsRecords, CacheRecords, Users, ServerConfigs)
- **Files Changed**: `SiNS/sins/Controllers/DnsController.cs`
- **Status**: Committed and pushed to SiNS repository

### 2. Ingress Domain Configuration
- **Issue**: Found `.dev.net` ingresses in production environment (Rancher, pgAdmin, PostgreSQL, SonarQube)
- **Action**: Implemented configurable domains in Helm charts (SiNS, Ollama, Certa)
- **Changes**:
  - Added `domain` and `subdomain` values to Helm chart values.yaml
  - Updated ingress templates to construct hostnames dynamically
  - Updated ArgoCD applications to use `domain: "prod.net"` for production
- **Files Changed**: 
  - `charts-repo/swipentap/sins/values.yaml`, `ollama/values.yaml`, `certa/values.yaml`
  - Ingress templates in all three charts
  - `argocd-prod/applications/sins.yaml`, `ollama.yaml`, `certa.yaml`
- **Status**: Pushed to charts-repo and argocd-prod repositories

### 3. GitHub Runner Installation Attempt
- **Request**: Add GitHub runner to cluster using token `ghp_REDACTED` in runner group `k3s-prod`
- **Initial Approach**: Created two ArgoCD applications:
  1. `github-runner.yaml` - Helm chart installation
  2. `github-runner-manifests.yaml` - Manifests (Secret, RunnerDeployment)
- **Problem**: Helm chart CRDs failed to install due to annotation size limit (262KB)
- **Workaround Applied**: Manually installed CRDs using `kubectl apply --server-side`
- **Mistake**: Manually created RunnerDeployment and Secret instead of letting ArgoCD manage them
- **Result**: Both applications showing "OutOfSync" and "Sync failed"

### 4. GitHub Runner Reinstallation
- **Request**: Remove all GitHub runner resources from ArgoCD, then install correctly from Helm chart
- **Actions Taken**:
  - Deleted `argocd-prod/applications/github-runner.yaml`
  - Deleted `argocd-prod/applications/github-runner-manifests.yaml`
  - Deleted `argocd-prod/manifests/github-runner/` directory
  - Recreated applications with `SkipCRDs=true` option
- **Current Issue**: ArgoCD still tries to install CRDs despite `SkipCRDs=true`, fails with annotation size error
- **Status**: Applications exist but not syncing correctly

## Current State

### ArgoCD Applications
- `github-runner`: OutOfSync, Progressing - Helm chart installation failing on CRD installation
- `github-runner-manifests`: OutOfSync, Healthy - Waiting for CRDs to exist

### Kubernetes Resources
- **Namespace**: `actions-runner-system` exists
- **Secret**: `controller-manager` exists (created manually with token)
- **Controller**: `actions-runner-controller` pod in CrashLoopBackOff (CRDs missing)
- **CRDs**: Only `horizontalrunnerautoscalers.actions.summerwind.dev` exists
- **Missing CRDs**: `runnerdeployments`, `runnerreplicasets`, `runners`, `runnersets`
- **RunnerDeployment**: Does not exist (was manually deleted)

## Technical Details

### CRD Annotation Size Issue
- Helm chart CRDs have annotations exceeding 262KB limit
- Normal `kubectl apply` fails with "metadata.annotations: Too long: may not be more than 262144 bytes"
- Server-side apply (`kubectl apply --server-side`) bypasses this validation
- ArgoCD uses normal apply, cannot install these CRDs automatically

### Helm Chart Configuration
- Chart: `actions-runner-controller/actions-runner-controller`
- Version: `0.23.7`
- Values:
  - `syncPeriod: 1m`
  - `authSecret.create: false`
  - `authSecret.name: controller-manager`
  - `certManagerEnabled: false`
- Sync Options: `CreateNamespace=true`, `SkipCRDs=true`

### RunnerDeployment Configuration
- Name: `k3s-runner-deployment`
- Organization: `swipentap`
- Group: `k3s-prod`
- Replicas: 3
- Labels: `k3s`
- Image: `summerwind/actions-runner-dind:latest`

## Files Created/Modified

### ArgoCD Applications
- `argocd-prod/applications/github-runner.yaml` - Helm chart application
- `argocd-prod/applications/github-runner-manifests.yaml` - Manifests application

### Manifests
- `argocd-prod/manifests/github-runner/secret.yaml` - Token secret (placeholder)
- `argocd-prod/manifests/github-runner/runner-deployment.yaml` - Runner deployment
- `argocd-prod/manifests/github-runner/crds.yaml` - Placeholder file

## Issues Identified

1. **CRD Installation**: ArgoCD cannot install CRDs from Helm chart due to annotation size limit
2. **SkipCRDs Option**: Not working as expected - ArgoCD still attempts CRD installation
3. **Manual Intervention**: CRDs and resources were manually created, breaking GitOps workflow
4. **Dual Applications**: Two applications created when one should suffice

## Next Steps Required

1. Resolve CRD installation issue (either fix ArgoCD configuration or install CRDs separately)
2. Ensure ArgoCD can manage all resources (no manual creation)
3. Verify runner group `k3s-prod` is correctly configured
4. Ensure all 3 runner pods are running and registered in GitHub
