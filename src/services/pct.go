package services

import (
	"encoding/base64"
	"fmt"
	"strings"
	"time"
	"enva/libs"
)

// PCTService uses LXC service to execute PCT CLI commands
type PCTService struct {
	lxc   libs.LXCServiceInterface
	shell string
}

const (
	defaultShell      = "bash"
	base64DecodeCmd   = "base64 -d"
)

// NewPCTService creates a new PCT service
func NewPCTService(lxc libs.LXCServiceInterface) *PCTService {
	return &PCTService{
		lxc:   lxc,
		shell: defaultShell,
	}
}

// encodeCommand encodes command using base64 to avoid quote escaping issues
func (p *PCTService) encodeCommand(command string) string {
	encoded := base64.StdEncoding.EncodeToString([]byte(command))
	return encoded
}

// buildPCTExecCommand builds pct exec command string
func (p *PCTService) buildPCTExecCommand(containerID int, command string) string {
	encodedCmd := p.encodeCommand(command)
	return fmt.Sprintf(
		"pct exec %d -- %s -c \"echo %s | %s | %s\"",
		containerID, p.shell, encodedCmd, base64DecodeCmd, p.shell,
	)
}

// Execute executes command in container via pct exec
func (p *PCTService) Execute(containerID int, command string, timeout *int) (string, *int) {
	libs.GetLogger("pct").Printf("Running in container %d: %s", containerID, command)
	pctCmd := p.buildPCTExecCommand(containerID, command)
	return p.lxc.Execute(pctCmd, timeout)
}

// Create creates container using pct create
func (p *PCTService) Create(
	containerID int,
	templatePath string,
	hostname string,
	memory int,
	swap int,
	cores int,
	ipAddress string,
	gateway string,
	bridge string,
	storage string,
	rootfsSize int,
	unprivileged bool,
	ostype string,
	arch string,
) (string, *int) {
	unprivValue := "0"
	if unprivileged {
		unprivValue = "1"
	}
	cmd := fmt.Sprintf(
		"pct create %d %s --hostname %s --memory %d --swap %d --cores %d --net0 name=eth0,bridge=%s,ip=%s/24,gw=%s --rootfs %s:%d --unprivileged %s --ostype %s --arch %s",
		containerID, templatePath, hostname, memory, swap, cores, bridge, ipAddress, gateway, storage, rootfsSize, unprivValue, ostype, arch,
	)
	return p.lxc.Execute(cmd, nil)
}

// SetOption sets a container option using pct set
func (p *PCTService) SetOption(containerID int, option string, value string) (string, *int) {
	cmd := fmt.Sprintf("pct set %d --%s %s", containerID, option, value)
	return p.lxc.Execute(cmd, nil)
}

// SetOnboot configures container autostart on Proxmox boot
func (p *PCTService) SetOnboot(containerID int, autostart bool) (string, *int) {
	value := "1"
	if !autostart {
		value = "0"
	}
	return p.SetOption(containerID, "onboot", value)
}

// Start starts container using pct start
func (p *PCTService) Start(containerID int) (string, *int) {
	cmd := fmt.Sprintf("pct start %d", containerID)
	return p.lxc.Execute(cmd, nil)
}

// Stop stops container using pct stop
func (p *PCTService) Stop(containerID int, force bool) (string, *int) {
	cmd := fmt.Sprintf("pct stop %d", containerID)
	if force {
		cmd = fmt.Sprintf("pct stop %d --force", containerID)
	}
	return p.lxc.Execute(cmd, nil)
}

// Status gets container status using pct status
func (p *PCTService) Status(containerID *int) (string, *int) {
	if containerID != nil {
		cmd := fmt.Sprintf("pct status %d", *containerID)
		return p.lxc.Execute(cmd, nil)
	}
	return p.lxc.Execute("pct list", nil)
}

// Destroy destroys container using pct destroy
func (p *PCTService) Destroy(containerID int, force bool) (string, *int) {
	cmd := fmt.Sprintf("pct destroy %d", containerID)
	if force {
		cmd = fmt.Sprintf("pct destroy %d --force", containerID)
	}
	return p.lxc.Execute(cmd, nil)
}

// SetFeatures sets container features using pct set --features
func (p *PCTService) SetFeatures(containerID int, nesting bool, keyctl bool, fuse bool) (string, *int) {
	cmd := fmt.Sprintf("pct set %d --features nesting=%d,keyctl=%d,fuse=%d", containerID, boolToInt(nesting), boolToInt(keyctl), boolToInt(fuse))
	return p.lxc.Execute(cmd, nil)
}

// Config gets container configuration using pct config
func (p *PCTService) Config(containerID int) (string, *int) {
	cmd := fmt.Sprintf("pct config %d", containerID)
	return p.lxc.Execute(cmd, nil)
}

func boolToInt(b bool) int {
	if b {
		return 1
	}
	return 0
}

// SetupSSHKey sets up SSH key in container
func (p *PCTService) SetupSSHKey(containerID int, ipAddress string, cfg *libs.LabConfig) bool {
	return libs.SetupSSHKey(containerID, ipAddress, cfg, p.lxc, p)
}

// EnsureSSHServiceRunning ensures SSH service is installed and running
func (p *PCTService) EnsureSSHServiceRunning(containerID int, cfg *libs.LabConfig) bool {
	// Check if openssh-server is installed
	checkCmd := "dpkg -l | grep -q '^ii.*openssh-server' || echo 'not_installed'"
	checkOutput, exitCode := p.Execute(containerID, checkCmd, nil)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("pct").Printf("Failed to check openssh-server installation: %s", checkOutput)
		return false
	}
	if strings.Contains(checkOutput, "not_installed") {
		libs.GetLogger("pct").Printf("openssh-server not installed, installing...")
		// Update apt
		updateCmd := fmt.Sprintf("apt-get update -qq")
		updateOutput, updateExit := p.Execute(containerID, updateCmd, libs.IntPtr(300))
		if updateExit != nil && *updateExit != 0 {
			libs.GetLogger("pct").Printf("Failed to update apt: %s", updateOutput)
			return false
		}
		// Install openssh-server
		installCmd := fmt.Sprintf("apt-get install -y -qq openssh-server")
		installOutput, installExit := p.Execute(containerID, installCmd, libs.IntPtr(300))
		if installExit != nil && *installExit != 0 {
			libs.GetLogger("pct").Printf("Failed to install openssh-server: %s", installOutput)
			return false
		}
		libs.GetLogger("pct").Printf("openssh-server installed successfully")
	}
	// Enable and start SSH service
	enableCmd := fmt.Sprintf("systemctl enable ssh")
	enableOutput, enableExit := p.Execute(containerID, enableCmd, nil)
	if enableExit != nil && *enableExit != 0 {
		libs.GetLogger("pct").Printf("Failed to enable SSH service: %s", enableOutput)
	}
	startCmd := fmt.Sprintf("systemctl start ssh")
	startOutput, startExit := p.Execute(containerID, startCmd, nil)
	if startExit != nil && *startExit != 0 {
		libs.GetLogger("pct").Printf("Failed to start SSH service: %s", startOutput)
		return false
	}
	return true
}

// WaitForContainer waits for container to be ready with SSH connectivity
func (p *PCTService) WaitForContainer(containerID int, ipAddress string, cfg *libs.LabConfig, defaultUser string) bool {
	maxAttempts := 200 // 10 minutes with 3 second intervals
	sleepInterval := 3
	if cfg != nil {
		if cfg.Waits.ContainerReadyMaxAttempts > 0 {
			maxAttempts = cfg.Waits.ContainerReadyMaxAttempts
		}
		if cfg.Waits.ContainerReadySleep > 0 {
			sleepInterval = cfg.Waits.ContainerReadySleep
		}
	}
	for i := 1; i <= maxAttempts; i++ {
		statusCmd := fmt.Sprintf("pct status %d 2>&1", containerID)
		statusOutput, _ := p.lxc.Execute(statusCmd, nil)
		if strings.Contains(statusOutput, "running") {
			// Try ping
			pingCmd := fmt.Sprintf("ping -c 1 -W 2 %s 2>&1", ipAddress)
			pingOutput, pingExit := p.lxc.Execute(pingCmd, nil)
			if pingExit != nil && *pingExit == 0 && strings.Contains(pingOutput, "1 received") {
				libs.GetLogger("pct").Printf("Container is up!")
				return true
			}
			// Try SSH test
			testCmd := fmt.Sprintf("echo test")
			testOutput, testExit := p.Execute(containerID, testCmd, libs.IntPtr(5))
			if testExit != nil && *testExit == 0 && strings.Contains(testOutput, "test") {
				libs.GetLogger("pct").Printf("Container is up (SSH working)!")
				return true
			}
		}
		libs.GetLogger("pct").Printf("Waiting... (%d/%d)", i, maxAttempts)
		time.Sleep(time.Duration(sleepInterval) * time.Second)
	}
	libs.GetLogger("pct").Printf("Container may not be fully ready, but continuing...")
	return true // Continue anyway
}

// GetLXCService returns the underlying LXC service
func (p *PCTService) GetLXCService() libs.LXCServiceInterface {
	return p.lxc
}

