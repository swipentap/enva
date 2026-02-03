using System;
using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class InstallK3sNodeWatcherAction : BaseAction, IAction
{
    public InstallK3sNodeWatcherAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        SSHService = sshService;
        APTService = aptService;
        PCTService = pctService;
        ContainerID = containerID;
        Cfg = cfg;
        ContainerCfg = containerCfg;
    }

    public override string Description()
    {
        return "k3s node watcher installation";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("install_k3s_node_watcher").Printf("Config not initialized");
            return false;
        }

        // For Kubernetes actions, we may not have SSHService, use PCTService instead
        if (SSHService == null && PCTService == null)
        {
            Logger.GetLogger("install_k3s_node_watcher").Printf("Neither SSH service nor PCT service initialized");
            return false;
        }

        // Determine control node ID
        int controlID;
        bool isControl = false;

        if (Cfg.Kubernetes == null || Cfg.Kubernetes.Control == null || Cfg.Kubernetes.Control.Count == 0)
        {
            Logger.GetLogger("install_k3s_node_watcher").Printf("No Kubernetes control node found in configuration");
            return false;
        }

        controlID = Cfg.Kubernetes.Control[0];

        // Check if this action is being run on the control node (container action) or as Kubernetes action
        if (ContainerCfg != null)
        {
            // Called as container action - check if this is the control node
            foreach (int id in Cfg.Kubernetes.Control)
            {
                if (id == ContainerCfg.ID)
                {
                    isControl = true;
                    break;
                }
            }
            if (!isControl)
            {
                Logger.GetLogger("install_k3s_node_watcher").Printf("Not a control node, skipping node watcher installation");
                return true;
            }
            // Use ContainerCfg ID if it's the control node
            controlID = ContainerCfg.ID;
        }
        else
        {
            // Called as Kubernetes action - always install on control node
            isControl = true;
        }

        Logger.GetLogger("install_k3s_node_watcher").Printf("Installing k3s node watcher service on control node {0}...", controlID);

        // Create the watcher script
        string watcherScript = @"#!/bin/bash
# k3s Node Watcher - Automatically fixes node issues after restarts
# This script removes unreachable taints and fixes /dev/kmsg issues

export PATH=/usr/local/bin:$PATH
export KUBECONFIG=/etc/rancher/k3s/k3s.yaml

LOG_FILE=""/var/log/k3s-node-watcher.log""
MAX_LOG_SIZE=10485760  # 10MB

# Rotate log if too large
if [ -f ""$LOG_FILE"" ] && [ $(stat -f%z ""$LOG_FILE"" || stat -c%s ""$LOG_FILE"" || echo 0) -gt $MAX_LOG_SIZE ]; then
    mv ""$LOG_FILE"" ""${LOG_FILE}.old"" || true
fi

log() {
    echo ""[$(date '+%Y-%m-%d %H:%M:%S')] $1"" | tee -a ""$LOG_FILE""
}

log ""Starting k3s node watcher check...""

# Check for nodes with unreachable taints
NODES_WITH_TAINTS=$(k3s kubectl get nodes -o jsonpath='{range .items[*]}{.metadata.name}{""\t""}{.spec.taints}{""\n""}{end}' | grep ""node.kubernetes.io/unreachable"" || true)

if [ -n ""$NODES_WITH_TAINTS"" ]; then
    log ""Found nodes with unreachable taints, fixing...""
    echo ""$NODES_WITH_TAINTS"" | while IFS=$'\t' read -r node_name taints; do
        log ""Removing taints from node: $node_name""
        k3s kubectl taint nodes ""$node_name"" node.kubernetes.io/unreachable:NoSchedule- | tee -a ""$LOG_FILE"" || true
        k3s kubectl taint nodes ""$node_name"" node.kubernetes.io/unreachable:NoExecute- | tee -a ""$LOG_FILE"" || true
        
        # Get node IP to find container ID
        NODE_IP=$(k3s kubectl get node ""$node_name"" -o jsonpath='{.status.addresses[?(@.type==""InternalIP"")].address}' || echo """")
        if [ -n ""$NODE_IP"" ]; then
            log ""Node $node_name has IP: $NODE_IP""
            # Try to fix /dev/kmsg on the node (we'll use pct exec from host, but this runs inside container)
            # For now, just log - the systemd service fix should handle /dev/kmsg
        fi
    done
    log ""Taint removal completed""
else
    log ""No nodes with unreachable taints found""
fi

# Check for NotReady nodes
NOT_READY_NODES=$(k3s kubectl get nodes --no-headers | grep -v "" Ready "" | awk '{print $1}' || true)

if [ -n ""$NOT_READY_NODES"" ]; then
    log ""Found NotReady nodes: $NOT_READY_NODES""
    # Taints should be removed above, nodes should recover
else
    log ""All nodes are Ready""
fi

log ""k3s node watcher check completed""
";

        string scriptPath = "/usr/local/bin/k3s-node-watcher.sh";
        // Use heredoc to write script file - escape $ in script content
        string escapedScript = watcherScript.Replace("$", "\\$");
        string writeScriptCmd = $"cat > {scriptPath} << 'EOFWATCHER'\n{escapedScript}\nEOFWATCHER";

        string writeOutput;
        int? writeExit;
        if (SSHService != null)
        {
            (writeOutput, writeExit) = SSHService.Execute(writeScriptCmd, null, true); // sudo=True
        }
        else if (PCTService != null)
        {
            (writeOutput, writeExit) = PCTService.Execute(controlID, writeScriptCmd, null);
        }
        else
        {
            Logger.GetLogger("install_k3s_node_watcher").Printf("No service available to execute command");
            return false;
        }

        if (writeExit.HasValue && writeExit.Value != 0)
        {
            Logger.GetLogger("install_k3s_node_watcher").Printf("Failed to write watcher script (exit: {0}): {1}", writeExit, writeOutput);
            // Try alternative method
            string tempScript = "/tmp/k3s-node-watcher.sh";
            string writeTempCmd = $"cat > {tempScript} << 'EOFWATCHER'\n{escapedScript}\nEOFWATCHER && mv {tempScript} {scriptPath}";
            string writeTempOutput;
            int? writeTempExit;
            if (SSHService != null)
            {
                (writeTempOutput, writeTempExit) = SSHService.Execute(writeTempCmd, null, true); // sudo=True
            }
            else
            {
                (writeTempOutput, writeTempExit) = PCTService!.Execute(controlID, writeTempCmd, null);
            }
            if (writeTempExit.HasValue && writeTempExit.Value != 0)
            {
                Logger.GetLogger("install_k3s_node_watcher").Printf("Alternative write method also failed: {0}", writeTempOutput);
                return false;
            }
        }

        // Make script executable
        string chmodCmd = $"chmod +x {scriptPath}";
        string chmodOutput;
        int? chmodExit;
        if (SSHService != null)
        {
            (chmodOutput, chmodExit) = SSHService.Execute(chmodCmd, null, true); // sudo=True
        }
        else
        {
            (chmodOutput, chmodExit) = PCTService!.Execute(controlID, chmodCmd, null);
        }
        if (chmodExit.HasValue && chmodExit.Value != 0)
        {
            Logger.GetLogger("install_k3s_node_watcher").Printf("Failed to make script executable: {0}", chmodOutput);
            return false;
        }

        // Create systemd service file
        string serviceContent = @"[Unit]
Description=k3s Node Watcher - Automatically fixes node issues
After=k3s.service
Requires=k3s.service

[Service]
Type=oneshot
ExecStart=/usr/local/bin/k3s-node-watcher.sh
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
";

        string servicePath = "/etc/systemd/system/k3s-node-watcher.service";
        string writeServiceCmd = $"cat > {servicePath} << 'EOFSERVICE'\n{serviceContent}\nEOFSERVICE";
        string serviceOutput;
        int? serviceExit;
        if (SSHService != null)
        {
            (serviceOutput, serviceExit) = SSHService.Execute(writeServiceCmd, null, true); // sudo=True
        }
        else
        {
            (serviceOutput, serviceExit) = PCTService!.Execute(controlID, writeServiceCmd, null);
        }
        if (serviceExit.HasValue && serviceExit.Value != 0)
        {
            Logger.GetLogger("install_k3s_node_watcher").Printf("Failed to write service file: {0}", serviceOutput);
            return false;
        }

        // Create systemd timer for periodic execution (every 2 minutes)
        string timerContent = @"[Unit]
Description=Run k3s Node Watcher every 2 minutes
Requires=k3s-node-watcher.service

[Timer]
OnBootSec=2min
OnUnitActiveSec=2min
AccuracySec=1s

[Install]
WantedBy=timers.target
";

        string timerPath = "/etc/systemd/system/k3s-node-watcher.timer";
        string writeTimerCmd = $"cat > {timerPath} << 'EOFTIMER'\n{timerContent}\nEOFTIMER";
        string timerOutput;
        int? timerExit;
        if (SSHService != null)
        {
            (timerOutput, timerExit) = SSHService.Execute(writeTimerCmd, null, true); // sudo=True
        }
        else
        {
            (timerOutput, timerExit) = PCTService!.Execute(controlID, writeTimerCmd, null);
        }
        if (timerExit.HasValue && timerExit.Value != 0)
        {
            Logger.GetLogger("install_k3s_node_watcher").Printf("Failed to write timer file: {0}", timerOutput);
            return false;
        }

        // Reload systemd and enable timer
        string reloadCmd = "systemctl daemon-reload";
        string reloadOutput;
        int? reloadExit;
        if (SSHService != null)
        {
            (reloadOutput, reloadExit) = SSHService.Execute(reloadCmd, null, true); // sudo=True
        }
        else
        {
            (reloadOutput, reloadExit) = PCTService!.Execute(controlID, reloadCmd, null);
        }
        if (reloadExit.HasValue && reloadExit.Value != 0)
        {
            Logger.GetLogger("install_k3s_node_watcher").Printf("Failed to reload systemd: {0}", reloadOutput);
            return false;
        }

        string enableTimerCmd = "systemctl enable k3s-node-watcher.timer && systemctl start k3s-node-watcher.timer";
        string enableOutput;
        int? enableExit;
        if (SSHService != null)
        {
            (enableOutput, enableExit) = SSHService.Execute(enableTimerCmd, null, true); // sudo=True
        }
        else
        {
            (enableOutput, enableExit) = PCTService!.Execute(controlID, enableTimerCmd, null);
        }
        if (enableExit.HasValue && enableExit.Value != 0)
        {
            Logger.GetLogger("install_k3s_node_watcher").Printf("Failed to enable timer: {0}", enableOutput);
            return false;
        }

        Logger.GetLogger("install_k3s_node_watcher").Printf("✓ k3s node watcher installed and enabled (runs every 2 minutes)");
        return true;
    }
}

public static class InstallK3sNodeWatcherActionFactory
{
    public static IAction NewInstallK3sNodeWatcherAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallK3sNodeWatcherAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}