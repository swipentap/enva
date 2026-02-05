# HAProxy LXC (CT 2017) – why it goes down sometimes

**Date:** 2026-02-04  
**Conclusion:** The container is brought down by **OOM (out-of-memory)** in its cgroup. The host kernel kills processes inside CT 2017, then the container is deactivated.

---

## Evidence from host (10.11.2.4)

### 1. CT 2017 is repeatedly stopped and started

From `journalctl -u pve-container@2017`:

- Many **Deactivated successfully** events (container stopped).
- Each is followed later by **Started** (container started again, e.g. by `onboot: 1` or manual start).
- This happens **without** a host reboot (e.g. on 2026-02-04 at 00:36 and 12:03 the host had been up for days).

So something inside the container or its resource limit causes the stop, not a host reboot.

### 2. OOM killer runs inside the HAProxy container

**At 2026-02-04 00:35:38 (host kernel):**

- `oom-kill: ... oom_memcg=/lxc/2017 ...`
- `Memory cgroup out of memory: Killed process 1974108 (in:imuxsock)` (rsyslog).
- Before that, **oom_reaper** had reaped several **socat** processes.
- OOM dump shows many **socat** processes in `/lxc/2017`.
- Shortly after: `vmbr0: port 2(veth2017i0) entered disabled state` and CT 2017 **Deactivated** at 00:36:07.

**At 2026-02-04 12:02:01 (host kernel):**

- `Memory cgroup stats for /lxc/2017`
- `oom-kill: ... oom_memcg=/lxc/2017 ... task=systemd-network,pid=2362136`
- `Memory cgroup out of memory: Killed process 2362136 (systemd-network)`
- Later: `oom_reaper: reaped process 2362337 (socat)`
- CT 2017 is **Deactivated** at 12:03:09.

So every time the HAProxy LXC “goes down”, the **memory cgroup of CT 2017** has hit its limit and the kernel has killed one or more processes (rsyslog, systemd-networkd, and multiple socat). After that, the container is deactivated.

---

## Root causes

1. **Container memory limit**  
   CT 2017 has **memory: 1024** (and swap: 1024) in `enva.yaml`. All processes inside the container share this 1024 MB limit. When usage exceeds it, the kernel OOM killer runs inside the cgroup and kills processes; the container then goes down.

2. **Many socat processes (DNS UDP forwarder)**  
   The HAProxy container runs a **DNS UDP forwarder** to SiNS using **socat** with **`fork`**:

   ```text
   ExecStart=/usr/bin/socat UDP4-LISTEN:53,fork,reuseaddr UDP4:{target}:{port}
   ```

   With `fork`, socat spawns a **new process per UDP packet** (per DNS query). Under normal or high DNS traffic this creates many short-lived socat processes. Each uses some memory; together they can push the container over 1024 MB and trigger OOM. The kernel dumps and oom_reaper logs show many socat processes in `/lxc/2017`, consistent with this.

3. **Other services in the same container**  
   HAProxy, systemd-networkd, rsyslog, sshd, and the socat children all share the same 1024 MB. When the OOM killer needs to free memory, it picks processes (e.g. rsyslog, systemd-networkd, socat), which can make the container unstable and lead to deactivation.

---

## Recommendations

1. **Increase HAProxy LXC memory**  
   In `enva.yaml`, raise the HAProxy CT memory (e.g. to **2048** MB) so that HAProxy + systemd + rsyslog + many socat children stay within the cgroup limit and OOM stops occurring. This is the main fix to stop the container from going down.

2. **Reduce or limit socat usage (optional)**  
   To reduce memory pressure from socat:
   - Consider a UDP relay that does **not** fork per packet (e.g. a small single-process proxy), or
   - Restrict the number of concurrent socat children (e.g. via a wrapper or a different tool with concurrency limits), or
   - Move DNS UDP forwarding to another container/VM with more memory.

3. **Optional: reduce logging**  
   If rsyslog or other logging is heavy, reducing log volume or disabling unused log targets in the HAProxy container can slightly lower memory use and make OOM less likely.

---

## Summary

| What happens | Why |
|--------------|-----|
| HAProxy LXC “goes down” sometimes | OOM inside CT 2017: memory cgroup limit (1024 MB) is exceeded. |
| Kernel kills processes | OOM killer selects processes in `/lxc/2017` (e.g. rsyslog, systemd-networkd, socat). |
| Many socat processes | DNS UDP forwarder uses `socat ... fork`, spawning one process per DNS query and increasing memory use. |
| Fix | Increase HAProxy CT memory (e.g. to 2048 MB) in `enva.yaml`; optionally replace or limit the socat-based DNS forwarder. |
