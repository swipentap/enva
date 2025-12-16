package actions

import (
	"fmt"
	"strings"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// InstallDockerAction installs Docker
type InstallDockerAction struct {
	*BaseAction
}

func NewInstallDockerAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &InstallDockerAction{
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

func (a *InstallDockerAction) Description() string {
	return "docker installation"
}

func (a *InstallDockerAction) Execute() bool {
	if a.SSHService == nil {
		libs.GetLogger("install_docker").Printf("SSH service not initialized")
		return false
	}
	libs.GetLogger("install_docker").Printf("Installing Docker...")
	
	// Check if curl is available
	curlCheckOutput, curlCheckExit := a.SSHService.Execute(cli.NewCommand().SetCommand("curl").Exists(), nil)
	hasCurl := curlCheckExit != nil && *curlCheckExit == 0 && strings.Contains(curlCheckOutput, "curl")
	
	if hasCurl {
		libs.GetLogger("install_docker").Printf("Downloading Docker install script...")
		downloadCmd := cli.NewCurl().FailSilently(true).Silent(true).ShowErrors(true).Location(true).Output("/tmp/get-docker.sh").URL("https://get.docker.com").Download()
		downloadOutput, downloadExit := a.SSHService.Execute(downloadCmd, nil)
		
		if downloadExit == nil || *downloadExit != 0 {
			libs.GetLogger("install_docker").Printf("Failed to download Docker install script: %s", downloadOutput)
			hasCurl = false
		} else {
			libs.GetLogger("install_docker").Printf("Running Docker install script...")
			scriptCmd := cli.NewShell().Script("/tmp/get-docker.sh").Execute()
			timeout := 300
			scriptOutput, scriptExit := a.SSHService.Execute(scriptCmd, &timeout)
			
			if strings.Contains(scriptOutput, "E: Package") || strings.Contains(scriptOutput, "Unable to locate package") || strings.Contains(scriptOutput, "has no installation candidate") {
				libs.GetLogger("install_docker").Printf("Docker install script failed, falling back to docker.io")
				hasCurl = false
			} else if scriptExit != nil && *scriptExit != 0 {
				libs.GetLogger("install_docker").Printf("Docker install script failed with exit code %d, falling back to docker.io", *scriptExit)
				hasCurl = false
			} else {
				dockerCheckCmd := cli.NewDocker().IsInstalledCheck()
				checkOutput, _ := a.SSHService.Execute(dockerCheckCmd, nil)
				if cli.ParseDockerIsInstalled(checkOutput) {
					libs.GetLogger("install_docker").Printf("Docker installed successfully via install script")
					return true
				}
				libs.GetLogger("install_docker").Printf("Docker install script completed but Docker not found, falling back to docker.io")
				hasCurl = false
			}
		}
	}
	
	// Fallback to docker.io via apt
	if !hasCurl {
		libs.GetLogger("install_docker").Printf("Installing docker.io via apt...")
		installCmd := cli.NewApt().Install([]string{"docker.io"})
		if a.APTService != nil {
			installOutput, exitCode := a.APTService.Install([]string{"docker.io"})
			if exitCode == nil || *exitCode != 0 {
				if strings.Contains(installOutput, "E: Package") || strings.Contains(installOutput, "Unable to locate package") || strings.Contains(installOutput, "has no installation candidate") {
					libs.GetLogger("install_docker").Printf("Docker installation failed - packages not found")
					outputLen := len(installOutput)
					start := 0
					if outputLen > 1000 {
						start = outputLen - 1000
					}
					libs.GetLogger("install_docker").Printf("Docker installation output: %s", installOutput[start:])
					return false
				}
			}
		} else {
			_, _ = a.SSHService.Execute(fmt.Sprintf("sudo -n %s", installCmd), nil)
			timeout := 300
			installOutput2, installExit2 := a.SSHService.Execute(installCmd, &timeout)
			if installExit2 != nil && *installExit2 != 0 {
				libs.GetLogger("install_docker").Printf("Docker installation failed with exit code %d", *installExit2)
				outputLen := len(installOutput2)
				start := 0
				if outputLen > 1000 {
					start = outputLen - 1000
				}
				libs.GetLogger("install_docker").Printf("Docker installation output: %s", installOutput2[start:])
				return false
			}
			if strings.Contains(installOutput2, "E: Package") || strings.Contains(installOutput2, "Unable to locate package") || strings.Contains(installOutput2, "has no installation candidate") {
				libs.GetLogger("install_docker").Printf("Docker installation failed - packages not found")
				outputLen := len(installOutput2)
				start := 0
				if outputLen > 1000 {
					start = outputLen - 1000
				}
				libs.GetLogger("install_docker").Printf("Docker installation output: %s", installOutput2[start:])
				return false
			}
		}
	}
	
	// Verify Docker is installed
	dockerCheckCmd := cli.NewDocker().IsInstalledCheck()
	checkOutput, _ := a.SSHService.Execute(dockerCheckCmd, nil)
	if !cli.ParseDockerIsInstalled(checkOutput) {
		libs.GetLogger("install_docker").Printf("Docker installation failed - verification shows Docker is not installed")
		return false
	}
	libs.GetLogger("install_docker").Printf("Docker installed successfully")
	return true
}

