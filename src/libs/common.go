package libs

import (
	"encoding/base64"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"
)

// ContainerExists checks if container exists
func ContainerExists(proxmoxHost string, containerID int, cfg *LabConfig, lxcService LXCServiceInterface) bool {
	containerIDStr := fmt.Sprintf("%d", containerID)
	cmd := fmt.Sprintf("pct list | grep '^%s '", containerIDStr)
	
	var result string
	if lxcService != nil {
		result, _ = lxcService.Execute(cmd, nil)
	} else if cfg != nil {
		// Fallback: create temporary service
		// Note: This requires importing services, which creates a cycle
		// Callers should pass lxcService to avoid this
		return false
	} else {
		// Fallback to subprocess
		sshCmd := fmt.Sprintf("ssh -o ConnectTimeout=10 -o BatchMode=yes %s \"%s\"", proxmoxHost, cmd)
		output, err := exec.Command("sh", "-c", sshCmd).CombinedOutput()
		if err == nil {
			result = string(output)
		}
	}
	
	return result != "" && strings.Contains(result, containerIDStr)
}

// DestroyContainer destroys container if it exists
func DestroyContainer(proxmoxHost string, containerID int, cfg *LabConfig, lxcService LXCServiceInterface) {
	containerIDStr := fmt.Sprintf("%d", containerID)
	
	if lxcService == nil {
		GetLogger("common").Printf("lxc_service must be provided")
		return
	}
	
	// Check if container exists
	checkCmd := fmt.Sprintf("pct list | grep '^%s ' || echo 'not_found'", containerIDStr)
	checkOutput, _ := lxcService.Execute(checkCmd, nil)
	if checkOutput == "" || !strings.Contains(checkOutput, containerIDStr) || strings.Contains(checkOutput, "not_found") {
		GetLogger("common").Printf("Container %d does not exist, skipping", containerID)
		return
	}
	
	// Stop and destroy
	GetLogger("common").Printf("Stopping and destroying container %d...", containerID)
	destroyCmd := fmt.Sprintf("pct stop %d 2>/dev/null || true; sleep 2; pct destroy %d 2>&1", containerID, containerID)
	_, destroyExit := lxcService.Execute(destroyCmd, nil)
	if destroyExit != nil && *destroyExit != 0 {
		GetLogger("common").Printf("Destroy failed, trying force destroy...")
		forceCmd := fmt.Sprintf("pct destroy %s --force 2>&1 || true", containerIDStr)
		lxcService.Execute(forceCmd, nil)
		time.Sleep(1 * time.Second)
	}
	
	// Verify destruction
	verifyCmd := fmt.Sprintf("pct list | grep '^%s ' || echo 'not_found'", containerIDStr)
	verifyOutput, _ := lxcService.Execute(verifyCmd, nil)
	if verifyOutput == "" || !strings.Contains(verifyOutput, containerIDStr) || strings.Contains(verifyOutput, "not_found") {
		GetLogger("common").Printf("Container %s destroyed", containerIDStr)
	} else {
		GetLogger("common").Printf("Container %s still exists after destruction attempt", containerIDStr)
	}
}

// WaitForContainer waits for container to be ready
func WaitForContainer(proxmoxHost string, containerID int, ipAddress string, maxAttempts *int, sleepInterval *int, cfg *LabConfig) bool {
	maxAttemptsVal := 30
	if maxAttempts != nil {
		maxAttemptsVal = *maxAttempts
	} else if cfg != nil {
		maxAttemptsVal = cfg.Waits.ContainerReadyMaxAttempts
	}
	
	sleepIntervalVal := 3
	if sleepInterval != nil {
		sleepIntervalVal = *sleepInterval
	} else if cfg != nil {
		sleepIntervalVal = cfg.Waits.ContainerReadySleep
	}
	
	// Note: WaitForContainer needs lxcService parameter to avoid import cycle
	// This is a simplified version that uses subprocess fallback
	for i := 1; i <= maxAttemptsVal; i++ {
		var status string
		cmd := exec.Command("sh", "-c", fmt.Sprintf("ssh -o ConnectTimeout=10 %s \"pct status %d 2>&1\"", proxmoxHost, containerID))
		output, _ := cmd.CombinedOutput()
		status = string(output)
		
		if strings.Contains(status, "running") {
			// Try ping
			pingCmd := exec.Command("ping", "-c", "1", "-W", "2", ipAddress)
			if err := pingCmd.Run(); err == nil {
				GetLogger("common").Printf("Container is up!")
				return true
			}
		}
		GetLogger("common").Printf("Waiting... (%d/%d)", i, maxAttemptsVal)
		time.Sleep(time.Duration(sleepIntervalVal) * time.Second)
	}
	GetLogger("common").Printf("Container may not be fully ready, but continuing...")
	return true // Continue anyway
}

// GetSSHKey gets SSH public key
func GetSSHKey() string {
	homeDir, err := os.UserHomeDir()
	if err != nil {
		return ""
	}
	
	keyPaths := []string{
		filepath.Join(homeDir, ".ssh", "id_rsa.pub"),
		filepath.Join(homeDir, ".ssh", "id_ed25519.pub"),
	}
	
	for _, keyPath := range keyPaths {
		if data, err := os.ReadFile(keyPath); err == nil {
			return strings.TrimSpace(string(data))
		}
	}
	return ""
}

// SetupSSHKey sets up SSH key in container
func SetupSSHKey(containerID int, ipAddress string, cfg *LabConfig, lxcService LXCServiceInterface, pctService PCTServiceInterface) bool {
	sshKey := GetSSHKey()
	if sshKey == "" {
		GetLogger("common").Printf("SSH public key not found.")
		return false
	}
	if cfg == nil {
		GetLogger("common").Printf("Configuration required for SSH key setup")
		return false
	}
	if lxcService == nil || pctService == nil {
		GetLogger("common").Printf("LXC or PCT service not provided for SetupSSHKey, skipping.")
		return false
	}
	
	defaultUser := cfg.Users.DefaultUser()
	
	// Remove old host key
	exec.Command("sh", "-c", fmt.Sprintf("ssh-keygen -R %s 2>/dev/null", ipAddress)).Run()
	
	// Base64 encode the key to avoid any shell escaping problems
	keyB64 := base64.StdEncoding.EncodeToString([]byte(sshKey))
	
	// Add to default user
	userCmd := fmt.Sprintf(
		"mkdir -p /home/%s/.ssh && echo %s | base64 -d > /home/%s/.ssh/authorized_keys && chmod 600 /home/%s/.ssh/authorized_keys && chown %s:%s /home/%s/.ssh/authorized_keys",
		defaultUser, keyB64, defaultUser, defaultUser, defaultUser, defaultUser, defaultUser,
	)
	_, userExit := pctService.Execute(containerID, userCmd, nil)
	if userExit != nil && *userExit != 0 {
		GetLogger("common").Printf("Failed to add SSH key for user %s: %s", defaultUser, userCmd)
		return false
	}
	
	// Add to root user
	rootCmd := fmt.Sprintf(
		"mkdir -p /root/.ssh && echo %s | base64 -d > /root/.ssh/authorized_keys && chmod 600 /root/.ssh/authorized_keys",
		keyB64,
	)
	_, rootExit := pctService.Execute(containerID, rootCmd, nil)
	if rootExit != nil && *rootExit != 0 {
		GetLogger("common").Printf("Failed to add SSH key for root: %s", rootCmd)
		return false
	}
	
	// Verify the key file exists
	verifyCmd := fmt.Sprintf(
		"test -f /home/%s/.ssh/authorized_keys && test -f /root/.ssh/authorized_keys && echo 'keys_exist' || echo 'keys_missing'",
		defaultUser,
	)
	verifyOutput, _ := pctService.Execute(containerID, verifyCmd, nil)
	
	if strings.Contains(verifyOutput, "keys_exist") {
		GetLogger("common").Printf("SSH key setup verified successfully")
		return true
	}
	GetLogger("common").Printf("SSH key verification failed: %s", verifyOutput)
	return false
}

// IntPtr returns a pointer to an int
func IntPtr(i int) *int {
	return &i
}

