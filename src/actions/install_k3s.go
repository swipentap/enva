package actions

import (
	"fmt"
	"strings"
	"time"
	"enva/cli"
	"enva/libs"
	"enva/services"
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
			ContainerID: containerID,
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
		versionCmd := "k3s --version 2>&1"
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
				removeCmd := "rm -f /dev/kmsg 2>/dev/null || true"
				a.SSHService.Execute(removeCmd, nil)
				createKmsgCmd := "ln -sf /dev/console /dev/kmsg 2>&1"
				createOutput, createExit := a.SSHService.Execute(createKmsgCmd, nil)
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
				verifyOutput, _ := a.SSHService.Execute(verifyCmd, nil)
				if verifyOutput != "" {
					libs.GetLogger("install_k3s").Printf("/dev/kmsg status: %s", strings.TrimSpace(verifyOutput))
				}
				restartCmd := "systemctl restart k3s 2>&1"
				restartOutput, restartExit := a.SSHService.Execute(restartCmd, nil)
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
		removeCmd := "rm -f /dev/kmsg 2>/dev/null || true"
		a.SSHService.Execute(removeCmd, nil)
		createKmsgCmd := "ln -sf /dev/console /dev/kmsg 2>&1"
		createOutput, createExit := a.SSHService.Execute(createKmsgCmd, nil)
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
		verifyOutput, verifyExit := a.SSHService.Execute(verifyCmd, nil)
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
		configOutput, configExit := a.SSHService.Execute(createConfigCmd, nil)
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
	
	versionCmd := "k3s --version 2>&1"
	versionOutput, versionExit := a.SSHService.Execute(versionCmd, nil)
	if versionExit == nil || *versionExit != 0 || versionOutput == "" || !strings.Contains(strings.ToLower(versionOutput), "k3s") {
		libs.GetLogger("install_k3s").Printf("k3s installation failed - verification shows k3s is not installed")
		return false
	}
	libs.GetLogger("install_k3s").Printf("k3s installed successfully: %s", strings.TrimSpace(versionOutput))
	
	// Setup kubectl PATH and kubeconfig for root user
	if isControl {
		libs.GetLogger("install_k3s").Printf("Setting up kubectl PATH and kubeconfig...")
		symlinkCmd := "ln -sf /usr/local/bin/kubectl /usr/bin/kubectl 2>/dev/null || true"
		a.SSHService.Execute(symlinkCmd, nil)
		maxWait := 60
		waitTime := 0
		kubeconfigReady := false
		for waitTime < maxWait {
			checkCmd := "test -f /etc/rancher/k3s/k3s.yaml && echo exists || echo missing"
			checkOutput3, checkExit3 := a.SSHService.Execute(checkCmd, nil)
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
		fixOutput, fixExit := a.SSHService.Execute(fixKubeconfigCmd, nil)
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
		verifyOutput2, verifyExit2 := a.SSHService.Execute(verifyCmd, nil)
		if verifyExit2 == nil || *verifyExit2 != 0 || verifyOutput2 == "" || !strings.Contains(verifyOutput2, "exists") {
			libs.GetLogger("install_k3s").Printf("kubeconfig was not copied to /root/.kube/config")
			return false
		}
		libs.GetLogger("install_k3s").Printf("kubeconfig setup completed")
	}
	return true
}

