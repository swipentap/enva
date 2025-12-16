package services

import (
	"fmt"
	"strings"
	"time"
)

const (
	aptLongTimeout = 600
	aptLockWait    = 600
)

// APTService manages apt/dpkg operations via SSH
type APTService struct {
	ssh         *SSHService
	lockWait    int
	longTimeout int
}

// NewAPTService creates a new APT service
func NewAPTService(sshService *SSHService) *APTService {
	return &APTService{
		ssh:         sshService,
		lockWait:    aptLockWait,
		longTimeout: aptLongTimeout,
	}
}

// Update updates package lists
func (a *APTService) Update() (string, *int) {
	cmd := "sudo -n apt-get update"
	timeout := a.longTimeout
	return a.ssh.Execute(cmd, &timeout)
}

// Install installs packages
// Matches Python: apt_service.execute() always runs apt-get update first via _wait_for_package_manager()
func (a *APTService) Install(packages []string) (string, *int) {
	// Always run apt-get update first (matching Python behavior)
	_, updateExitCode := a.Update()
	if updateExitCode != nil && *updateExitCode != 0 {
		return "", updateExitCode
	}
	cmd := fmt.Sprintf("sudo -n apt-get install -y %s", strings.Join(packages, " "))
	timeout := a.longTimeout
	return a.ssh.Execute(cmd, &timeout)
}

// Upgrade upgrades all packages
// Matches Python: apt_service.execute() always runs apt-get update first via _wait_for_package_manager()
func (a *APTService) Upgrade() (string, *int) {
	// Always run apt-get update first (matching Python behavior)
	_, updateExitCode := a.Update()
	if updateExitCode != nil && *updateExitCode != 0 {
		return "", updateExitCode
	}
	cmd := "sudo -n apt-get upgrade -y"
	timeout := a.longTimeout
	return a.ssh.Execute(cmd, &timeout)
}

// DistUpgrade performs distribution upgrade
// Matches Python: apt_service.execute() always runs apt-get update first via _wait_for_package_manager()
func (a *APTService) DistUpgrade() (string, *int) {
	// Always run apt-get update first (matching Python behavior)
	_, updateExitCode := a.Update()
	if updateExitCode != nil && *updateExitCode != 0 {
		return "", updateExitCode
	}
	cmd := "sudo -n apt-get dist-upgrade -y"
	timeout := a.longTimeout
	return a.ssh.Execute(cmd, &timeout)
}

// Clean cleans package cache
func (a *APTService) Clean() (string, *int) {
	cmd := "sudo -n apt-get clean"
	return a.ssh.Execute(cmd, nil)
}

// Autoremove removes unused packages
func (a *APTService) Autoremove() (string, *int) {
	cmd := "sudo -n apt-get autoremove -y"
	return a.ssh.Execute(cmd, nil)
}

// WaitForLock waits for apt lock to be released
func (a *APTService) WaitForLock() bool {
	maxAttempts := a.lockWait / 5
	for i := 0; i < maxAttempts; i++ {
		cmd := "lsof /var/lib/dpkg/lock-frontend /var/lib/dpkg/lock /var/cache/apt/archives/lock 2>&1 || echo 'no_lock'"
		output, _ := a.ssh.Execute(cmd, nil)
		if strings.Contains(output, "no_lock") {
			return true
		}
		time.Sleep(5 * time.Second)
	}
	return false
}

