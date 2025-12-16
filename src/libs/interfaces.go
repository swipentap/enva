package libs

// LXCServiceInterface defines the interface for LXC service
type LXCServiceInterface interface {
	Connect() bool
	Disconnect()
	IsConnected() bool
	Execute(command string, timeout *int) (string, *int)
}

// PCTServiceInterface defines the interface for PCT service
type PCTServiceInterface interface {
	Execute(containerID int, command string, timeout *int) (string, *int)
	SetupSSHKey(containerID int, ipAddress string, cfg *LabConfig) bool
	EnsureSSHServiceRunning(containerID int, cfg *LabConfig) bool
	WaitForContainer(containerID int, ipAddress string, cfg *LabConfig, defaultUser string) bool
}

