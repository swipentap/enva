package actions

import (
	"enva/cli"
	"enva/libs"
	"enva/services"
	"fmt"
	"strings"
	"time"
)

// InstallK3sAction installs k3s
type InstallK3sAction struct {
	*BaseAction
}

func NewInstallK3sAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &InstallK3sAction{
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

func (a *InstallK3sAction) Description() string {
	return "k3s installation"
}

func (a *InstallK3sAction) Execute() bool {
	if a.SSHService == nil {
		libs.GetLogger("install_k3s").Printf("SSH service not initialized")
		return false
	}
	libs.GetLogger("install_k3s").Printf("Installing k3s...")

	// Check if k3s is already installed
	k3sCheckCmd := cli.NewCommand().SetCommand("k3s").Exists()
	checkOutput, checkExit := a.SSHService.Execute(k3sCheckCmd, nil)
	if checkExit != nil && *checkExit == 0 && checkOutput != "" {
		versionCmd := "k3s --version"
		versionOutput, versionExit := a.SSHService.Execute(versionCmd, nil)
		if versionExit != nil && *versionExit == 0 && versionOutput != "" && strings.Contains(strings.ToLower(versionOutput), "k3s") {
			libs.GetLogger("install_k3s").Printf("k3s is already installed: %s", strings.TrimSpace(versionOutput))
			// Ensure /dev/kmsg exists
			isControl := false
			if a.ContainerCfg != nil && a.Cfg != nil && a.Cfg.Kubernetes != nil {
				for _, id := range a.Cfg.Kubernetes.Control {
					if id == a.ContainerCfg.ID {
						isControl = true
						break
					}
				}
			}
			if isControl {
				libs.GetLogger("install_k3s").Printf("Ensuring /dev/kmsg exists for k3s (LXC requirement)...")
				removeCmd := "rm -f /dev/kmsg || true"
				a.SSHService.Execute(removeCmd, nil, true) // sudo=True
				createKmsgCmd := "ln -sf /dev/console /dev/kmsg"
				createOutput, createExit := a.SSHService.Execute(createKmsgCmd, nil, true) // sudo=True
				if createExit != nil && *createExit != 0 {
					outputLen := len(createOutput)
					start := 0
					if outputLen > 200 {
						start = outputLen - 200
					}
					libs.GetLogger("install_k3s").Printf("Failed to create /dev/kmsg symlink: %s", createOutput[start:])
					return false
				}
				libs.GetLogger("install_k3s").Printf("/dev/kmsg symlink created successfully")
				verifyCmd := "test -e /dev/kmsg && ls -l /dev/kmsg && echo exists || echo missing"
				verifyOutput, _ := a.SSHService.Execute(verifyCmd, nil, true) // sudo=True
				if verifyOutput != "" {
					libs.GetLogger("install_k3s").Printf("/dev/kmsg status: %s", strings.TrimSpace(verifyOutput))
				}
				restartCmd := "systemctl restart k3s"
				restartOutput, restartExit := a.SSHService.Execute(restartCmd, nil, true) // sudo=True
				if restartExit != nil && *restartExit != 0 {
					outputLen := len(restartOutput)
					start := 0
					if outputLen > 200 {
						start = outputLen - 200
					}
					libs.GetLogger("install_k3s").Printf("k3s restart had issues: %s", restartOutput[start:])
				} else {
					libs.GetLogger("install_k3s").Printf("k3s service restarted")
					time.Sleep(5 * time.Second)
				}
			}
			return true
		}
	}

	// Check if curl is available
	curlCheckOutput, curlCheckExit := a.SSHService.Execute(cli.NewCommand().SetCommand("curl").Exists(), nil)
	hasCurl := curlCheckExit != nil && *curlCheckExit == 0 && strings.Contains(curlCheckOutput, "curl")

	if !hasCurl {
		libs.GetLogger("install_k3s").Printf("curl not found, installing...")
		curlInstallOutput, exitCode := a.APTService.Install([]string{"curl"})
		if exitCode == nil || *exitCode != 0 {
			libs.GetLogger("install_k3s").Printf("Failed to install curl: %s", curlInstallOutput)
			return false
		}
		libs.GetLogger("install_k3s").Printf("curl installed successfully")
	}

	// Determine if this is a control node
	isControl := false
	if a.ContainerCfg != nil && a.Cfg != nil && a.Cfg.Kubernetes != nil {
		for _, id := range a.Cfg.Kubernetes.Control {
			if id == a.ContainerCfg.ID {
				isControl = true
				break
			}
		}
	}

	// Create /dev/kmsg device inside container (required for k3s in LXC)
	if isControl {
		libs.GetLogger("install_k3s").Printf("Creating /dev/kmsg device for k3s...")
		removeCmd := "rm -f /dev/kmsg || true"
		a.SSHService.Execute(removeCmd, nil, true) // sudo=True
		createKmsgCmd := "ln -sf /dev/console /dev/kmsg"
		createOutput, createExit := a.SSHService.Execute(createKmsgCmd, nil, true) // sudo=True
		if createExit != nil && *createExit != 0 {
			outputLen := len(createOutput)
			start := 0
			if outputLen > 200 {
				start = outputLen - 200
			}
			libs.GetLogger("install_k3s").Printf("Failed to create /dev/kmsg symlink: %s", createOutput[start:])
			return false
		}
		verifyCmd := "test -L /dev/kmsg && ls -l /dev/kmsg && echo symlink_ok || echo symlink_failed"
		verifyOutput, verifyExit := a.SSHService.Execute(verifyCmd, nil, true) // sudo=True
		if verifyExit != nil && *verifyExit == 0 && verifyOutput != "" && strings.Contains(verifyOutput, "symlink_ok") {
			libs.GetLogger("install_k3s").Printf("/dev/kmsg symlink verified: %s", strings.TrimSpace(verifyOutput))
		} else {
			libs.GetLogger("install_k3s").Printf("/dev/kmsg symlink creation failed: %s", verifyOutput)
			return false
		}
	}

	if isControl {
		libs.GetLogger("install_k3s").Printf("Installing k3s server (control node)...")
		configDir := "/etc/rancher/k3s"
		configFile := fmt.Sprintf("%s/config.yaml", configDir)
		controlIP := "127.0.0.1"
		if a.ContainerCfg != nil && a.ContainerCfg.IPAddress != nil {
			controlIP = *a.ContainerCfg.IPAddress
		}
		hostname := "k3s-control"
		if a.ContainerCfg != nil {
			hostname = a.ContainerCfg.Hostname
		}
		configContent := fmt.Sprintf(`# k3s configuration file
# This file is automatically generated
tls-san:
  - %s
  - %s
bind-address: 0.0.0.0
advertise-address: %s
`, controlIP, hostname, controlIP)
		createConfigCmd := fmt.Sprintf("mkdir -p %s && cat > %s << 'EOFCONFIG'\n%sEOFCONFIG", configDir, configFile, configContent)
		configOutput, configExit := a.SSHService.Execute(createConfigCmd, nil, true) // sudo=True
		if configExit != nil && *configExit != 0 {
			outputLen := len(configOutput)
			start := 0
			if outputLen > 200 {
				start = outputLen - 200
			}
			libs.GetLogger("install_k3s").Printf("Failed to create k3s config file: %s", configOutput[start:])
			return false
		}
		libs.GetLogger("install_k3s").Printf("k3s config file created successfully")

		installCmd := "curl -sfL https://get.k3s.io | sh -"
		timeout := 300
		installOutput, installExit := a.SSHService.Execute(installCmd, &timeout)
		if installExit != nil && *installExit != 0 {
			libs.GetLogger("install_k3s").Printf("k3s installation failed with exit code %d", *installExit)
			outputLen := len(installOutput)
			start := 0
			if outputLen > 1000 {
				start = outputLen - 1000
			}
			libs.GetLogger("install_k3s").Printf("k3s installation output: %s", installOutput[start:])
			return false
		}
	} else {
		libs.GetLogger("install_k3s").Printf("Skipping k3s agent installation for worker node (will be installed during orchestration)...")
		return true
	}

	// Verify k3s is installed
	k3sCheckCmd2 := cli.NewCommand().SetCommand("k3s").Exists()
	checkOutput2, checkExitCode := a.SSHService.Execute(k3sCheckCmd2, nil)
	if checkExitCode == nil || *checkExitCode != 0 || checkOutput2 == "" {
		libs.GetLogger("install_k3s").Printf("k3s installation failed - k3s command not found")
		return false
	}

	versionCmd := "k3s --version"
	versionOutput, versionExit := a.SSHService.Execute(versionCmd, nil)
	if versionExit == nil || *versionExit != 0 || versionOutput == "" || !strings.Contains(strings.ToLower(versionOutput), "k3s") {
		libs.GetLogger("install_k3s").Printf("k3s installation failed - verification shows k3s is not installed")
		return false
	}
	libs.GetLogger("install_k3s").Printf("k3s installed successfully: %s", strings.TrimSpace(versionOutput))

	// Fix systemd service files to ensure /dev/kmsg exists before k3s starts (persistent fix for LXC)
	libs.GetLogger("install_k3s").Printf("Configuring systemd service to ensure /dev/kmsg exists on startup...")
	serviceName := "k3s-agent"
	if isControl {
		serviceName = "k3s"
	}
	serviceFile := fmt.Sprintf("/etc/systemd/system/%s.service", serviceName)

	// Read current service file
	readServiceCmd := fmt.Sprintf("cat %s", serviceFile)
	serviceContent, _ := a.SSHService.Execute(readServiceCmd, nil, true) // sudo=True

	// Check if ExecStartPre for /dev/kmsg already exists
	if !strings.Contains(serviceContent, "/dev/kmsg") {
		// Add ExecStartPre to create /dev/kmsg before other ExecStartPre commands
		// Insert before the first ExecStartPre line (which is usually modprobe br_netfilter)
		// Use script file approach for reliable sed execution (avoids escaping issues)
		fixServiceScript := fmt.Sprintf(`serviceFile="%s"
export serviceFile
cat > /tmp/fix_k3s_service.sh << 'EOFSED'
#!/bin/bash
sed -i "/ExecStartPre=-\/sbin\/modprobe br_netfilter/i ExecStartPre=-/bin/bash -c \\\"rm -f /dev/kmsg && ln -sf /dev/console /dev/kmsg\\\"" "$serviceFile"
EOFSED
chmod +x /tmp/fix_k3s_service.sh
/tmp/fix_k3s_service.sh && echo "success" || echo "failed"
rm -f /tmp/fix_k3s_service.sh`, serviceFile)
		fixOutput, fixExit := a.SSHService.Execute(fixServiceScript, nil, true) // sudo=True
		if fixExit != nil && *fixExit == 0 && strings.Contains(fixOutput, "success") {
			libs.GetLogger("install_k3s").Printf("✓ Added /dev/kmsg fix to %s.service", serviceName)
			// Reload systemd to pick up changes
			reloadCmd := "systemctl daemon-reload"
			reloadOutput, reloadExit := a.SSHService.Execute(reloadCmd, nil, true) // sudo=True
			if reloadExit != nil && *reloadExit == 0 {
				libs.GetLogger("install_k3s").Printf("✓ Systemd daemon reloaded")
			} else {
				libs.GetLogger("install_k3s").Printf("✗ Failed to reload systemd: %s", reloadOutput)
				libs.GetLogger("install_k3s").Printf("✗ Deployment failed: systemd daemon-reload failed after adding ExecStartPre fix")
				return false
			}
		} else {
			libs.GetLogger("install_k3s").Printf("✗ Failed to modify %s.service: %s", serviceName, fixOutput)
			libs.GetLogger("install_k3s").Printf("✗ Deployment failed: %s.service must have ExecStartPre fix for /dev/kmsg (required for LXC containers)", serviceName)
			return false
		}
	} else {
		libs.GetLogger("install_k3s").Printf("✓ %s.service already has /dev/kmsg fix", serviceName)
	}

	// Setup kubectl PATH and kubeconfig for root user
	if isControl {
		libs.GetLogger("install_k3s").Printf("Setting up kubectl PATH and kubeconfig...")
		symlinkCmd := "ln -sf /usr/local/bin/kubectl /usr/bin/kubectl || true"
		a.SSHService.Execute(symlinkCmd, nil, true) // sudo=True
		maxWait := 60
		waitTime := 0
		kubeconfigReady := false
		for waitTime < maxWait {
			checkCmd := "test -f /etc/rancher/k3s/k3s.yaml && echo exists || echo missing"
			checkOutput3, checkExit3 := a.SSHService.Execute(checkCmd, nil, true) // sudo=True
			if checkExit3 != nil && *checkExit3 == 0 && checkOutput3 != "" && strings.Contains(checkOutput3, "exists") {
				kubeconfigReady = true
				break
			}
			time.Sleep(2 * time.Second)
			waitTime += 2
		}
		if !kubeconfigReady {
			libs.GetLogger("install_k3s").Printf("k3s kubeconfig not generated after %d seconds", maxWait)
			return false
		}
		libs.GetLogger("install_k3s").Printf("k3s kubeconfig generated")
		controlIP := "127.0.0.1"
		if a.ContainerCfg != nil && a.ContainerCfg.IPAddress != nil {
			controlIP = *a.ContainerCfg.IPAddress
		}
		fixKubeconfigCmd := fmt.Sprintf("sed -i 's|server: https://127.0.0.1:6443|server: https://%s:6443|g; s|server: https://0.0.0.0:6443|server: https://%s:6443|g' /etc/rancher/k3s/k3s.yaml && mkdir -p /root/.kube && cp /etc/rancher/k3s/k3s.yaml /root/.kube/config && chown root:root /root/.kube/config && chmod 600 /root/.kube/config", controlIP, controlIP)
		fixOutput, fixExit := a.SSHService.Execute(fixKubeconfigCmd, nil, true) // sudo=True
		if fixExit != nil && *fixExit != 0 {
			outputLen := len(fixOutput)
			start := 0
			if outputLen > 200 {
				start = outputLen - 200
			}
			libs.GetLogger("install_k3s").Printf("Failed to setup kubeconfig: %s", fixOutput[start:])
			return false
		}
		verifyCmd := "test -f /root/.kube/config && echo exists || echo missing"
		verifyOutput2, verifyExit2 := a.SSHService.Execute(verifyCmd, nil, true) // sudo=True
		if verifyExit2 == nil || *verifyExit2 != 0 || verifyOutput2 == "" || !strings.Contains(verifyOutput2, "exists") {
			libs.GetLogger("install_k3s").Printf("kubeconfig was not copied to /root/.kube/config")
			return false
		}
		libs.GetLogger("install_k3s").Printf("kubeconfig setup completed")
	}

	// Verify /dev/kmsg exists (especially important for worker nodes)
	if !isControl {
		libs.GetLogger("install_k3s").Printf("Verifying /dev/kmsg exists on worker node...")
		verifyKmsgCmd := "test -e /dev/kmsg && echo exists || echo missing"
		verifyKmsgOutput, verifyKmsgExit := a.SSHService.Execute(verifyKmsgCmd, nil, true) // sudo=True
		if verifyKmsgExit == nil || *verifyKmsgExit != 0 || !strings.Contains(verifyKmsgOutput, "exists") {
			libs.GetLogger("install_k3s").Printf("Creating /dev/kmsg symlink on worker node...")
			createKmsgCmd := "rm -f /dev/kmsg && ln -sf /dev/console /dev/kmsg"
			createOutput, createExit := a.SSHService.Execute(createKmsgCmd, nil, true) // sudo=True
			if createExit != nil && *createExit == 0 {
				libs.GetLogger("install_k3s").Printf("✓ /dev/kmsg created on worker node")
			} else {
				libs.GetLogger("install_k3s").Printf("⚠ Failed to create /dev/kmsg on worker node: %s", createOutput)
			}
		} else {
			libs.GetLogger("install_k3s").Printf("✓ /dev/kmsg exists on worker node")
		}
	}

	return true
}
