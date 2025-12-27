package actions

import (
	"enva/libs"
	"enva/services"
	"fmt"
	"strings"
)

// InstallK3sNodeWatcherAction installs a systemd service to watch and fix k3s node issues
type InstallK3sNodeWatcherAction struct {
	*BaseAction
}

func NewInstallK3sNodeWatcherAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &InstallK3sNodeWatcherAction{
		BaseAction: &BaseAction{
			SSHService:   sshService,
			APTService:   aptService,
			PCTService:   pctService,
			ContainerID:  containerID,
			Cfg:          cfg,
			ContainerCfg: containerCfg,
		},
	}
}

func (a *InstallK3sNodeWatcherAction) Description() string {
	return "k3s node watcher installation"
}

func (a *InstallK3sNodeWatcherAction) Execute() bool {
	if a.Cfg == nil {
		libs.GetLogger("install_k3s_node_watcher").Printf("Config not initialized")
		return false
	}
	
	// For Kubernetes actions, we may not have SSHService, use PCTService instead
	if a.SSHService == nil && a.PCTService == nil {
		libs.GetLogger("install_k3s_node_watcher").Printf("Neither SSH service nor PCT service initialized")
		return false
	}

	// Determine control node ID
	var controlID int
	isControl := false
	
	if a.Cfg == nil || a.Cfg.Kubernetes == nil || len(a.Cfg.Kubernetes.Control) == 0 {
		libs.GetLogger("install_k3s_node_watcher").Printf("No Kubernetes control node found in configuration")
		return false
	}
	
	controlID = a.Cfg.Kubernetes.Control[0]
	
	// Check if this action is being run on the control node (container action) or as Kubernetes action
	if a.ContainerCfg != nil {
		// Called as container action - check if this is the control node
		for _, id := range a.Cfg.Kubernetes.Control {
			if id == a.ContainerCfg.ID {
				isControl = true
				break
			}
		}
		if !isControl {
			libs.GetLogger("install_k3s_node_watcher").Printf("Not a control node, skipping node watcher installation")
			return true
		}
		// Use ContainerCfg ID if it's the control node
		controlID = a.ContainerCfg.ID
	} else {
		// Called as Kubernetes action - always install on control node
		isControl = true
	}

	libs.GetLogger("install_k3s_node_watcher").Printf("Installing k3s node watcher service on control node %d...", controlID)

	// Create the watcher script
	watcherScript := `#!/bin/bash
# k3s Node Watcher - Automatically fixes node issues after restarts
# This script removes unreachable taints and fixes /dev/kmsg issues

export PATH=/usr/local/bin:$PATH
export KUBECONFIG=/etc/rancher/k3s/k3s.yaml

LOG_FILE="/var/log/k3s-node-watcher.log"
MAX_LOG_SIZE=10485760  # 10MB

# Rotate log if too large
if [ -f "$LOG_FILE" ] && [ $(stat -f%z "$LOG_FILE" 2>/dev/null || stat -c%s "$LOG_FILE" 2>/dev/null || echo 0) -gt $MAX_LOG_SIZE ]; then
    mv "$LOG_FILE" "${LOG_FILE}.old" 2>/dev/null || true
fi

log() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] $1" | tee -a "$LOG_FILE"
}

log "Starting k3s node watcher check..."

# Check for nodes with unreachable taints
NODES_WITH_TAINTS=$(k3s kubectl get nodes -o jsonpath='{range .items[*]}{.metadata.name}{"\t"}{.spec.taints}{"\n"}{end}' 2>/dev/null | grep "node.kubernetes.io/unreachable" || true)

if [ -n "$NODES_WITH_TAINTS" ]; then
    log "Found nodes with unreachable taints, fixing..."
    echo "$NODES_WITH_TAINTS" | while IFS=$'\t' read -r node_name taints; do
        log "Removing taints from node: $node_name"
        k3s kubectl taint nodes "$node_name" node.kubernetes.io/unreachable:NoSchedule- 2>&1 | tee -a "$LOG_FILE" || true
        k3s kubectl taint nodes "$node_name" node.kubernetes.io/unreachable:NoExecute- 2>&1 | tee -a "$LOG_FILE" || true
        
        # Get node IP to find container ID
        NODE_IP=$(k3s kubectl get node "$node_name" -o jsonpath='{.status.addresses[?(@.type=="InternalIP")].address}' 2>/dev/null || echo "")
        if [ -n "$NODE_IP" ]; then
            log "Node $node_name has IP: $NODE_IP"
            # Try to fix /dev/kmsg on the node (we'll use pct exec from host, but this runs inside container)
            # For now, just log - the systemd service fix should handle /dev/kmsg
        fi
    done
    log "Taint removal completed"
else
    log "No nodes with unreachable taints found"
fi

# Check for NotReady nodes
NOT_READY_NODES=$(k3s kubectl get nodes --no-headers 2>/dev/null | grep -v " Ready " | awk '{print $1}' || true)

if [ -n "$NOT_READY_NODES" ]; then
    log "Found NotReady nodes: $NOT_READY_NODES"
    # Taints should be removed above, nodes should recover
else
    log "All nodes are Ready"
fi

log "k3s node watcher check completed"
`

	scriptPath := "/usr/local/bin/k3s-node-watcher.sh"
	// Use heredoc to write script file - escape $ in script content
	escapedScript := strings.ReplaceAll(watcherScript, "$", "\\$")
	writeScriptCmd := fmt.Sprintf("cat > %s << 'EOFWATCHER'\n%s\nEOFWATCHER", scriptPath, escapedScript)
	
	var writeOutput string
	var writeExit *int
	if a.SSHService != nil {
		writeOutput, writeExit = a.SSHService.Execute(writeScriptCmd, nil, true) // sudo=True
	} else if a.PCTService != nil {
		writeOutput, writeExit = a.PCTService.Execute(controlID, writeScriptCmd, nil)
	} else {
		libs.GetLogger("install_k3s_node_watcher").Printf("No service available to execute command")
		return false
	}
	
	if writeExit != nil && *writeExit != 0 {
		libs.GetLogger("install_k3s_node_watcher").Printf("Failed to write watcher script (exit: %v): %s", writeExit, writeOutput)
		// Try alternative method
		tempScript := "/tmp/k3s-node-watcher.sh"
		writeTempCmd := fmt.Sprintf("cat > %s << 'EOFWATCHER'\n%s\nEOFWATCHER && mv %s %s", tempScript, escapedScript, tempScript, scriptPath)
		var writeTempOutput string
		var writeTempExit *int
		if a.SSHService != nil {
			writeTempOutput, writeTempExit = a.SSHService.Execute(writeTempCmd, nil, true) // sudo=True
		} else {
			writeTempOutput, writeTempExit = a.PCTService.Execute(controlID, writeTempCmd, nil)
		}
		if writeTempExit != nil && *writeTempExit != 0 {
			libs.GetLogger("install_k3s_node_watcher").Printf("Alternative write method also failed: %s", writeTempOutput)
			return false
		}
	}

	// Make script executable
	chmodCmd := fmt.Sprintf("chmod +x %s", scriptPath)
	var chmodOutput string
	var chmodExit *int
	if a.SSHService != nil {
		chmodOutput, chmodExit = a.SSHService.Execute(chmodCmd, nil, true) // sudo=True
	} else {
		chmodOutput, chmodExit = a.PCTService.Execute(controlID, chmodCmd, nil)
	}
	if chmodExit != nil && *chmodExit != 0 {
		libs.GetLogger("install_k3s_node_watcher").Printf("Failed to make script executable: %s", chmodOutput)
		return false
	}

	// Create systemd service file
	serviceContent := `[Unit]
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
`

	servicePath := "/etc/systemd/system/k3s-node-watcher.service"
	writeServiceCmd := fmt.Sprintf("cat > %s << 'EOFSERVICE'\n%s\nEOFSERVICE", servicePath, serviceContent)
	var serviceOutput string
	var serviceExit *int
	if a.SSHService != nil {
		serviceOutput, serviceExit = a.SSHService.Execute(writeServiceCmd, nil, true) // sudo=True
	} else {
		serviceOutput, serviceExit = a.PCTService.Execute(controlID, writeServiceCmd, nil)
	}
	if serviceExit != nil && *serviceExit != 0 {
		libs.GetLogger("install_k3s_node_watcher").Printf("Failed to write service file: %s", serviceOutput)
		return false
	}

	// Create systemd timer for periodic execution (every 2 minutes)
	timerContent := `[Unit]
Description=Run k3s Node Watcher every 2 minutes
Requires=k3s-node-watcher.service

[Timer]
OnBootSec=2min
OnUnitActiveSec=2min
AccuracySec=1s

[Install]
WantedBy=timers.target
`

	timerPath := "/etc/systemd/system/k3s-node-watcher.timer"
	writeTimerCmd := fmt.Sprintf("cat > %s << 'EOFTIMER'\n%s\nEOFTIMER", timerPath, timerContent)
	var timerOutput string
	var timerExit *int
	if a.SSHService != nil {
		timerOutput, timerExit = a.SSHService.Execute(writeTimerCmd, nil, true) // sudo=True
	} else {
		timerOutput, timerExit = a.PCTService.Execute(controlID, writeTimerCmd, nil)
	}
	if timerExit != nil && *timerExit != 0 {
		libs.GetLogger("install_k3s_node_watcher").Printf("Failed to write timer file: %s", timerOutput)
		return false
	}

	// Reload systemd and enable timer
	reloadCmd := "systemctl daemon-reload 2>&1"
	var reloadOutput string
	var reloadExit *int
	if a.SSHService != nil {
		reloadOutput, reloadExit = a.SSHService.Execute(reloadCmd, nil, true) // sudo=True
	} else {
		reloadOutput, reloadExit = a.PCTService.Execute(controlID, reloadCmd, nil)
	}
	if reloadExit != nil && *reloadExit != 0 {
		libs.GetLogger("install_k3s_node_watcher").Printf("Failed to reload systemd: %s", reloadOutput)
		return false
	}

	enableTimerCmd := "systemctl enable k3s-node-watcher.timer 2>&1 && systemctl start k3s-node-watcher.timer 2>&1"
	var enableOutput string
	var enableExit *int
	if a.SSHService != nil {
		enableOutput, enableExit = a.SSHService.Execute(enableTimerCmd, nil, true) // sudo=True
	} else {
		enableOutput, enableExit = a.PCTService.Execute(controlID, enableTimerCmd, nil)
	}
	if enableExit != nil && *enableExit != 0 {
		libs.GetLogger("install_k3s_node_watcher").Printf("Failed to enable timer: %s", enableOutput)
		return false
	}

	libs.GetLogger("install_k3s_node_watcher").Printf("✓ k3s node watcher installed and enabled (runs every 2 minutes)")
	return true
}
