# Testing Plan: k3s Node Resilience and Rancher Accessibility

## Overview
This document outlines the testing plan for verifying that k3s nodes remain healthy and Rancher remains accessible after node restarts, with automatic recovery mechanisms in place.

## Test Environment
- **Environment**: prod
- **k3s Control Node**: Container ID 2011 (k3s-control)
- **k3s Worker Nodes**: Container IDs 2012, 2013, 2014 (k3s-worker-1, k3s-worker-2, k3s-worker-3)
- **HAProxy**: Container ID 2017
- **Rancher**: Deployed on k3s cluster, accessible via `https://rancher.prod.net/`

## Test Scenarios

### 1. Initial Deployment Verification
**Objective**: Verify all fixes are applied during initial deployment

**Steps**:
1. Run `./enva redeploy prod`
2. Monitor deployment logs for:
   - `/dev/kmsg` fix being added to systemd service files
   - Node watcher installation on control node
   - Node restart + verification at end of deployment
3. After deployment completes, verify:
   - All 4 nodes are Ready: `kubectl get nodes`
   - No unreachable taints: `kubectl get nodes -o custom-columns=NAME:.metadata.name,TAINTS:.spec.taints`
   - `/dev/kmsg` exists on all nodes: `ls -la /dev/kmsg` (on each node)
   - Systemd service files have ExecStartPre fix: `grep -A 3 ExecStartPre /etc/systemd/system/k3s.service` (and k3s-agent.service)
   - Node watcher timer is active: `systemctl status k3s-node-watcher.timer` (on control node)
   - Rancher is accessible: `curl -k https://10.11.2.17/ -H "Host: rancher.prod.net"`

**Expected Results**:
- All nodes Ready
- No unreachable taints
- `/dev/kmsg` symlink exists on all nodes
- Systemd service files contain ExecStartPre fix
- Node watcher timer is enabled and active
- Rancher returns HTTP 200

---

### 2. Control Node Restart Test
**Objective**: Verify control node recovers correctly after restart

**Steps**:
1. Verify initial state: `kubectl get nodes` (all Ready)
2. Restart control node: `pct reboot 2011` (or `systemctl reboot` inside container)
3. Wait 2-3 minutes for node to come back online
4. Check node status: `kubectl get nodes`
5. Verify `/dev/kmsg` exists: `pct exec 2011 -- ls -la /dev/kmsg`
6. Check k3s service status: `pct exec 2011 -- systemctl status k3s`
7. Verify Rancher accessibility: `curl -k https://10.11.2.17/ -H "Host: rancher.prod.net"`

**Expected Results**:
- Control node becomes Ready within 2-3 minutes
- `/dev/kmsg` exists (created by ExecStartPre)
- k3s service is active and running
- Rancher remains accessible
- No manual intervention required

---

### 3. Worker Node Restart Test
**Objective**: Verify worker nodes recover correctly after restart

**Steps**:
1. Verify initial state: `kubectl get nodes` (all Ready)
2. Restart worker node: `pct reboot 2012` (or any worker node)
3. Wait 2-3 minutes for node to come back online
4. Check for unreachable taints: `kubectl get nodes -o custom-columns=NAME:.metadata.name,TAINTS:.spec.taints`
5. Wait up to 4 minutes (2 watcher cycles) for automatic taint removal
6. Check node status: `kubectl get nodes`
7. Verify `/dev/kmsg` exists: `pct exec 2012 -- ls -la /dev/kmsg`
8. Check k3s-agent service status: `pct exec 2012 -- systemctl status k3s-agent`
9. Verify Rancher pod rescheduling (if needed): `kubectl get pods -n cattle-system -l app=rancher -o wide`

**Expected Results**:
- Worker node becomes Ready within 2-3 minutes
- Any unreachable taints are automatically removed within 4 minutes (2 watcher cycles)
- `/dev/kmsg` exists (created by ExecStartPre)
- k3s-agent service is active and running
- Rancher pod reschedules if needed
- Rancher remains accessible
- No manual intervention required

---

### 4. Multiple Node Restart Test
**Objective**: Verify system handles multiple simultaneous restarts

**Steps**:
1. Verify initial state: `kubectl get nodes` (all Ready)
2. Restart 2 worker nodes simultaneously: `pct reboot 2012` and `pct reboot 2013`
3. Wait 3-4 minutes for nodes to come back online
4. Monitor node watcher logs: `pct exec 2011 -- tail -f /var/log/k3s-node-watcher.log`
5. Check node status: `kubectl get nodes`
6. Verify taints are removed: `kubectl get nodes -o custom-columns=NAME:.metadata.name,TAINTS:.spec.taints`
7. Verify Rancher accessibility: `curl -k https://10.11.2.17/ -H "Host: rancher.prod.net"`

**Expected Results**:
- Both worker nodes become Ready within 3-4 minutes
- Unreachable taints are automatically removed
- Node watcher logs show taint removal actions
- Rancher remains accessible
- No manual intervention required

---

### 5. Node Watcher Functionality Test
**Objective**: Verify node watcher automatically fixes issues

**Steps**:
1. Manually add unreachable taint to a worker: `kubectl taint nodes k3s-worker-1 node.kubernetes.io/unreachable:NoSchedule`
2. Check node watcher log: `pct exec 2011 -- tail -20 /var/log/k3s-node-watcher.log`
3. Wait 2 minutes for next watcher cycle
4. Check taints again: `kubectl get nodes -o custom-columns=NAME:.metadata.name,TAINTS:.spec.taints`
5. Verify node status: `kubectl get nodes`

**Expected Results**:
- Node watcher detects taint within 2 minutes
- Taint is automatically removed
- Node remains Ready (or becomes Ready if it was NotReady)
- Log file shows taint removal action

---

### 6. Rancher Pod Rescheduling Test
**Objective**: Verify Rancher pod reschedules correctly when node restarts

**Steps**:
1. Identify which node Rancher pod is running on: `kubectl get pods -n cattle-system -l app=rancher -o wide`
2. Note the node name
3. Restart that specific node
4. Wait for pod to terminate and reschedule
5. Verify new pod is running: `kubectl get pods -n cattle-system -l app=rancher`
6. Wait for pod to become Ready
7. Verify Rancher accessibility: `curl -k https://10.11.2.17/ -H "Host: rancher.prod.net"`

**Expected Results**:
- Old pod terminates gracefully
- New pod is scheduled on a different Ready node
- New pod becomes Ready within 2-3 minutes
- Rancher remains accessible (may have brief downtime during reschedule)

---

### 7. Systemd Service File Persistence Test
**Objective**: Verify ExecStartPre fix persists across restarts

**Steps**:
1. Before restart, verify service file: `pct exec 2012 -- grep -A 3 ExecStartPre /etc/systemd/system/k3s-agent.service`
2. Restart node: `pct reboot 2012`
3. Wait for node to come back
4. Verify service file still has fix: `pct exec 2012 -- grep -A 3 ExecStartPre /etc/systemd/system/k3s-agent.service`
5. Verify `/dev/kmsg` exists: `pct exec 2012 -- ls -la /dev/kmsg`
6. Check k3s-agent started successfully: `pct exec 2012 -- systemctl status k3s-agent`

**Expected Results**:
- Service file still contains ExecStartPre fix after restart
- `/dev/kmsg` exists (created by ExecStartPre)
- k3s-agent service starts successfully without errors
- Node becomes Ready

---

### 8. Full Cluster Restart Test
**Objective**: Verify entire cluster recovers after all nodes restart

**Steps**:
1. Restart all nodes: `pct reboot 2011 2012 2013 2014`
2. Wait 5-6 minutes for all nodes to come back
3. Check node status: `kubectl get nodes`
4. Wait for node watcher to remove any taints (up to 4 minutes)
5. Verify all nodes Ready: `kubectl get nodes`
6. Verify Rancher pods: `kubectl get pods -n cattle-system -l app=rancher`
7. Verify Rancher accessibility: `curl -k https://10.11.2.17/ -H "Host: rancher.prod.net"`

**Expected Results**:
- All nodes become Ready within 5-6 minutes
- Any unreachable taints are automatically removed
- Rancher pods are running
- Rancher is accessible
- No manual intervention required

---

## Verification Commands

### Check Node Status
```bash
kubectl get nodes
kubectl get nodes -o custom-columns=NAME:.metadata.name,STATUS:.status.conditions[?(@.type=="Ready")].status,TAINTS:.spec.taints
```

### Check /dev/kmsg
```bash
for id in 2011 2012 2013 2014; do
  echo "Node $id:"
  pct exec $id -- ls -la /dev/kmsg
done
```

### Check Systemd Service Files
```bash
# Control node
pct exec 2011 -- grep -A 3 ExecStartPre /etc/systemd/system/k3s.service

# Worker nodes
for id in 2012 2013 2014; do
  echo "Node $id:"
  pct exec $id -- grep -A 3 ExecStartPre /etc/systemd/system/k3s-agent.service
done
```

### Check Node Watcher
```bash
# Status
pct exec 2011 -- systemctl status k3s-node-watcher.timer

# Logs
pct exec 2011 -- tail -50 /var/log/k3s-node-watcher.log

# Last run
pct exec 2011 -- systemctl list-timers k3s-node-watcher.timer
```

### Check Rancher
```bash
# Pod status
kubectl get pods -n cattle-system -l app=rancher -o wide

# Service
kubectl get svc rancher -n cattle-system

# Accessibility
curl -k -I https://10.11.2.17/ -H "Host: rancher.prod.net"
```

---

## Success Criteria

### Deployment Phase
- ✅ All nodes Ready after deployment
- ✅ Systemd service files contain ExecStartPre fix
- ✅ Node watcher installed and enabled
- ✅ Rancher accessible after deployment

### Post-Restart Phase
- ✅ Nodes become Ready within 3 minutes of restart
- ✅ `/dev/kmsg` exists on all nodes after restart
- ✅ Unreachable taints automatically removed within 4 minutes
- ✅ Rancher remains accessible (may have brief downtime during pod reschedule)
- ✅ No manual intervention required

### Ongoing Monitoring
- ✅ Node watcher runs every 2 minutes
- ✅ Node watcher logs show activity
- ✅ Issues are automatically detected and fixed

---

## Failure Scenarios and Troubleshooting

### Node Stays NotReady
**Symptoms**: Node remains NotReady after restart
**Check**:
1. `/dev/kmsg` exists: `ls -la /dev/kmsg`
2. k3s service status: `systemctl status k3s` or `systemctl status k3s-agent`
3. Service logs: `journalctl -u k3s -n 50` or `journalctl -u k3s-agent -n 50`
4. Unreachable taints: `kubectl get nodes -o custom-columns=NAME:.metadata.name,TAINTS:.spec.taints`

**Fix**:
- If `/dev/kmsg` missing: `rm -f /dev/kmsg && ln -sf /dev/console /dev/kmsg && systemctl restart k3s` (or k3s-agent)
- If taints present: `kubectl taint nodes <node-name> node.kubernetes.io/unreachable:NoSchedule-` and `kubectl taint nodes <node-name> node.kubernetes.io/unreachable:NoExecute-`

### Rancher Not Accessible
**Symptoms**: `curl` to Rancher returns 503 or timeout
**Check**:
1. Rancher pod status: `kubectl get pods -n cattle-system -l app=rancher`
2. HAProxy backend status: `curl -s http://10.11.2.17:8404/stats | grep backend_rancher`
3. NodePort accessibility: `ss -tlnp | grep 30443` (on any node)
4. Service type: `kubectl get svc rancher -n cattle-system`

**Fix**:
- If pod not running: Check pod events: `kubectl describe pod <pod-name> -n cattle-system`
- If HAProxy backend DOWN: Check if NodePort is accessible, may need to change service to LoadBalancer
- If svclb pods Pending: Check node taints and resources

### Node Watcher Not Running
**Symptoms**: Taints not being removed automatically
**Check**:
1. Timer status: `systemctl status k3s-node-watcher.timer`
2. Service file exists: `ls -la /etc/systemd/system/k3s-node-watcher.*`
3. Script exists: `ls -la /usr/local/bin/k3s-node-watcher.sh`
4. Logs: `tail -50 /var/log/k3s-node-watcher.log`

**Fix**:
- Enable timer: `systemctl enable k3s-node-watcher.timer && systemctl start k3s-node-watcher.timer`
- Check script permissions: `chmod +x /usr/local/bin/k3s-node-watcher.sh`
- Manually run: `/usr/local/bin/k3s-node-watcher.sh`

---

## Test Schedule

1. **Initial Deployment Test**: Run after code changes
2. **Single Node Restart Tests**: Run daily for first week
3. **Multiple Node Restart Test**: Run weekly
4. **Node Watcher Test**: Run after any watcher code changes
5. **Full Cluster Restart Test**: Run monthly or after major updates

---

## Notes

- Node watcher runs every 2 minutes, so taint removal may take up to 4 minutes (2 cycles) in worst case
- Rancher pod rescheduling may cause brief downtime (30-60 seconds)
- Control node restart may cause brief API unavailability
- All tests should be run during maintenance windows when possible
