package actions

import (
	"fmt"
	"strings"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// InstallAptCacherAction installs apt-cacher-ng package
type InstallAptCacherAction struct {
	*BaseAction
}

func NewInstallAptCacherAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &InstallAptCacherAction{
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

func (a *InstallAptCacherAction) Description() string {
	return "apt-cacher-ng installation"
}

func (a *InstallAptCacherAction) Execute() bool {
	if a.APTService == nil || a.SSHService == nil {
		libs.GetLogger("install_apt_cacher").Error("Services not initialized")
		return false
	}
	
	libs.GetLogger("install_apt_cacher").Info("Installing apt-cacher-ng package...")
	
	// Update apt first (matching Python: apt_service.execute() always runs apt-get update first)
	// Note: We use SSHService.Execute() directly with custom command, so we need to call Update() manually
	_, updateExitCode := a.APTService.Update()
	if updateExitCode == nil || *updateExitCode != 0 {
		libs.GetLogger("install_apt_cacher").Error("apt-get update failed")
		return false
	}
	
	// Pre-configure debconf
	debconfSelections := "apt-cacher-ng apt-cacher-ng/tunnelenable boolean false\napt-cacher-ng apt-cacher-ng/bindaddress string 0.0.0.0\n"
	debconfCmd := fmt.Sprintf("echo '%s' | debconf-set-selections", debconfSelections)
	debconfOutput, debconfExit := a.SSHService.Execute(debconfCmd, nil, true) // sudo=True
	if debconfExit != nil && *debconfExit != 0 {
		libs.GetLogger("install_apt_cacher").Warning("Failed to set debconf selections: %s", debconfOutput)
	}
	
	// Install package with dpkg options and environment variables (matching Python)
	dpkgOptions := map[string]string{
		"Dpkg::Options::": "--force-confdef --force-confold",
		"DPkg::Pre-Install-Pkgs": "",
	}
	installCmd := cli.NewApt().UseAptGet().Options(dpkgOptions).Install([]string{"apt-cacher-ng"})
	// Wrap with sudo, environment variables and stdin redirect (matching Python)
	// Python uses: self.apt_service.execute(f"DEBIAN_PRIORITY=critical DEBIAN_FRONTEND=noninteractive {install_cmd} < /dev/null")
	// and apt_service.execute wraps with sudo -n
	fullCmd := fmt.Sprintf("DEBIAN_PRIORITY=critical DEBIAN_FRONTEND=noninteractive sudo -n %s < /dev/null", installCmd)
	output, exitCode := a.SSHService.Execute(fullCmd, libs.IntPtr(600))
	if exitCode == nil || *exitCode != 0 {
		libs.GetLogger("install_apt_cacher").Error("apt-cacher-ng installation failed")
		if output != "" {
			outputLen := len(output)
			start := 0
			if outputLen > 500 {
				start = outputLen - 500
			}
			libs.GetLogger("install_apt_cacher").Error("Installation output (last 500 chars): %s", output[start:])
		}
		// Verify if package was actually installed despite error
		checkCmd := cli.IsInstalledCheckCmd("apt-cacher-ng")
		checkOutput, checkExit := a.SSHService.Execute(checkCmd, nil)
		if checkExit != nil && *checkExit == 0 && cli.ParseIsInstalled(checkOutput) {
			libs.GetLogger("install_apt_cacher").Warning("apt-cacher-ng binary exists despite installation error, checking service unit...")
			// Still need to verify service unit exists even if binary exists
			serviceCheckCmd := "systemctl list-unit-files apt-cacher-ng.service | grep -q apt-cacher-ng.service && echo 'exists' || echo 'missing'"
			serviceCheck, serviceExit := a.SSHService.Execute(serviceCheckCmd, nil)
			if serviceExit != nil && *serviceExit == 0 && strings.Contains(serviceCheck, "exists") {
				libs.GetLogger("install_apt_cacher").Warning("apt-cacher-ng service unit exists, treating as success")
				return true
			}
			libs.GetLogger("install_apt_cacher").Error("apt-cacher-ng service unit not found despite binary existing. Check: %s", serviceCheck)
			dpkgCheck := "dpkg -l | grep apt-cacher-ng"
			dpkgOutput, _ := a.SSHService.Execute(dpkgCheck, nil)
			libs.GetLogger("install_apt_cacher").Error("dpkg status: %s", dpkgOutput)
			return false
		}
		return false
	}
	
	// Verify binary exists
	checkCmd := cli.IsInstalledCheckCmd("apt-cacher-ng")
	checkOutput, checkExit := a.SSHService.Execute(checkCmd, nil)
	if checkExit == nil || *checkExit != 0 || !cli.ParseIsInstalled(checkOutput) {
		libs.GetLogger("install_apt_cacher").Error("apt-cacher-ng binary not found after installation")
		return false
	}
	
	// Verify service unit exists
	serviceCheckCmd := "systemctl list-unit-files apt-cacher-ng.service | grep -q apt-cacher-ng.service && echo 'exists' || echo 'missing'"
	serviceCheck, serviceExit := a.SSHService.Execute(serviceCheckCmd, nil)
	if serviceExit == nil || *serviceExit != 0 || !strings.Contains(serviceCheck, "exists") {
		libs.GetLogger("install_apt_cacher").Error("apt-cacher-ng service unit not found after installation. Check: %s", serviceCheck)
		dpkgCheck := "dpkg -l | grep apt-cacher-ng"
		dpkgOutput, _ := a.SSHService.Execute(dpkgCheck, nil)
		libs.GetLogger("install_apt_cacher").Error("dpkg status: %s", dpkgOutput)
		return false
	}
	return true
}

