# MetalLB cluster fix – match chatlog configuration

**Date:** 2026-02-04  
**Goal:** Make cluster match the manual setup from chat: MetalLB assigns one IP from pool 10.11.2.20–10.11.2.30 to Traefik LoadBalancer; HAProxy load-balances across the full pool; k3s ServiceLB must not override MetalLB.

---

## Step 0 – Pre-check (recorded)

- Traefik service type: LoadBalancer.
- Traefik status had node IPs (10.11.2.11–14) instead of MetalLB IP 10.11.2.20.
- k3s service-lb-controller was creating DaemonSet `svclb-traefik-*` and overwriting LoadBalancer status.
- MetalLB pool 10.11.2.20–10.11.2.30 and L2Advertisement present; MetalLB had assigned 10.11.2.20 but it was later overwritten.
- HAProxy configured for backends 10.11.2.20–30; 10.11.2.20 was unreachable (no MetalLB advertisement).

---

## Step 1 – Disable k3s ServiceLB on control node

K3s must be started with ServiceLB disabled so MetalLB can own LoadBalancer services.

**Action:** Add `disable: [servicelb]` to `/etc/rancher/k3s/config.yaml` on control node 10.11.2.11, then restart k3s.

**Result:** Config updated. Contents:
```yaml
disable:
  - servicelb
```

---

## Step 2 – Restart k3s on control node

**Action:** `systemctl restart k3s` on 10.11.2.11 and wait for API to be ready.

**Result:** k3s restarted. `kubectl get nodes` succeeded after a short wait. No `svclb-*` DaemonSets in kube-system (ServiceLB no longer creates them).

---

## Step 3 – Allow MetalLB to own Traefik LoadBalancer status

After disabling ServiceLB, the Traefik service still had `status.loadBalancer.ingress` set to node IPs (10.11.2.11–14) and a finalizer `service.kubernetes.io/load-balancer-cleanup` that kept the old controller in charge.

**Action:** Patch the Traefik service to remove the finalizer, then delete the service. ArgoCD recreated it from git; MetalLB then set `status.loadBalancer.ingress` to 10.11.2.20.

Commands used:
```bash
kubectl patch svc traefik -n kube-system -p '{"metadata":{"finalizers":null}}' --type=merge
kubectl delete svc traefik -n kube-system
# ArgoCD self-heal recreated the service; MetalLB assigned 10.11.2.20
```

**Result:** Traefik service recreated with `EXTERNAL-IP 10.11.2.20`. MetalLB is now the sole owner of the LoadBalancer status.

---

## Step 4 – Verify connectivity

**Action:** Test from local machine and from HAProxy host.

- `nc -vz 10.11.2.20 80` → Connection succeeded
- `nc -vz 10.11.2.20 443` → Connection succeeded
- `curl -skI http://10.11.2.20/` → HTTP/1.1 404 (Traefik, no Host header)
- From 10.11.2.17: `curl -skI -H 'Host: rancher.prod.net' http://10.11.2.17/` → HTTP/1.1 302 to https://rancher.prod.net/

**Result:** MetalLB IP 10.11.2.20 is reachable. HAProxy forwards to the MetalLB pool and ingress works.

---

## Summary

| Item | Before | After |
|------|--------|--------|
| k3s ServiceLB | Enabled (set LoadBalancer status to node IPs) | Disabled in `/etc/rancher/k3s/config.yaml` |
| Traefik LoadBalancer status | 10.11.2.11, 10.11.2.12, 10.11.2.13, 10.11.2.14 | 10.11.2.20 (MetalLB) |
| HAProxy backends | 10.11.2.20–30 (only .20 needed; was unreachable) | 10.11.2.20 reachable; traffic works |
| svclb-* DaemonSets | Present (before restart) | Gone after servicelb disable + restart |

---

## Fixes applied to enva (see below)

1. **k3s_installation.cs** – Add `disable: [servicelb]` to the generated k3s config on the control node so new clusters use MetalLB for LoadBalancer from the start.
2. **install_metallb.cs** – After creating IPAddressPool and L2Advertisement:
   - Ensure k3s config on the control node contains `disable: [servicelb]`; if not, append it and restart k3s, then wait for API (up to 120s).
   - Delete any `svclb-*` DaemonSets in kube-system.
   - If Traefik service exists and its `status.loadBalancer.ingress` has multiple IPs (comma in jsonpath output), patch finalizers to null and delete the service so ArgoCD recreates it and MetalLB assigns an IP from the pool.

**Files changed:**
- `src/Actions/k3s_installation.cs` – added `disable: [servicelb]` to the generated k3s config YAML for the control node.
- `src/Actions/install_metallb.cs` – added the steps above after L2Advertisement creation.

