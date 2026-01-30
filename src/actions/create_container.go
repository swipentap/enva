package actions

import (
	"enva/cli"
	"enva/libs"
	"enva/services"
	"fmt"
	"strings"
	"time"
)

// CreateContainerAction creates container without executing actions
type CreateContainerAction struct {
	*BaseAction
	Plan *DeployPlan
}

func NewCreateContainerAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig, plan *DeployPlan) Action {
	return &CreateContainerAction{
		BaseAction: &BaseAction{
			SSHService:   sshService,
			APTService:   aptService,
			PCTService:   pctService,
			ContainerID:  containerID,
			Cfg:          cfg,
			ContainerCfg: containerCfg,
		},
		Plan: plan,
	}
}

func (a *CreateContainerAction) Description() string {
	return "create container"
}

func (a *CreateContainerAction) Execute() bool {
	if a.ContainerCfg == nil || a.Cfg == nil {
		libs.GetLogger("create_container").Printf("Container config or lab config is missing")
		return false
	}
	lxcHost := a.Cfg.LXCHost()
	containerIDInt := a.ContainerCfg.ID
	ipAddress := ""
	if a.ContainerCfg.IPAddress != nil {
		ipAddress = *a.ContainerCfg.IPAddress
	}
	hostname := a.ContainerCfg.Hostname
	gateway := a.Cfg.GetGateway()
	templateName := a.ContainerCfg.Template
	var templateNameStr *string
	if templateName == nil || *templateName == "base" || *templateName == "" {
		templateNameStr = nil
	} else {
		templateNameStr = templateName
	}
	lxcService := services.NewLXCService(lxcHost, &a.Cfg.SSH)
	if !lxcService.Connect() {
		libs.GetLogger("create_container").Printf("Failed to connect to LXC host %s", lxcHost)
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)
	templateService := services.NewTemplateService(lxcService)
	containerIDInt = a.ContainerCfg.ID
	pctService.Destroy(containerIDInt, true)
	templatePath := templateService.GetTemplatePath(templateNameStr, a.Cfg)
	if !templateService.ValidateTemplate(templatePath) {
		libs.GetLogger("create_container").Printf("Template file %s is missing or not readable", templatePath)
		return false
	}
	shouldBePrivileged := false
	if a.ContainerCfg.Privileged != nil {
		shouldBePrivileged = *a.ContainerCfg.Privileged
	}
	shouldBeNested := true
	if a.ContainerCfg.Nested != nil {
		shouldBeNested = *a.ContainerCfg.Nested
	}
	libs.GetLogger("create_container").Printf("Checking if container %d already exists...", containerIDInt)
	containerAlreadyExists := libs.ContainerExists(lxcHost, containerIDInt, a.Cfg, lxcService)
	if containerAlreadyExists && a.Plan != nil && a.Plan.StartStep > 1 {
		if shouldBePrivileged {
			configCmd := fmt.Sprintf("pct config %d | grep -E '^unprivileged:' || echo 'unprivileged: 1'", containerIDInt)
			configOutput, _ := lxcService.Execute(configCmd, nil)
			isUnprivileged := strings.Contains(configOutput, "unprivileged: 1")
			if isUnprivileged {
				libs.GetLogger("create_container").Printf("Container %d exists but is unprivileged - config requires privileged. Destroying and recreating...", containerIDInt)
				libs.DestroyContainer(lxcHost, containerIDInt, a.Cfg, lxcService)
				containerAlreadyExists = false
			} else {
				libs.GetLogger("create_container").Printf("Container %d already exists and privilege status matches config, skipping creation", containerIDInt)
				statusOutput, _ := pctService.Status(&containerIDInt)
				if !strings.Contains(statusOutput, "running") {
					libs.GetLogger("create_container").Printf("Starting existing container %d...", containerIDInt)
					pctService.Start(containerIDInt)
					time.Sleep(3 * time.Second)
				}
				return true
			}
		} else {
			libs.GetLogger("create_container").Printf("Container %d already exists and start_step is %d, skipping creation", containerIDInt, a.Plan.StartStep)
			statusOutput, _ := pctService.Status(&containerIDInt)
			if !strings.Contains(statusOutput, "running") {
				libs.GetLogger("create_container").Printf("Starting existing container %d...", containerIDInt)
				pctService.Start(containerIDInt)
				time.Sleep(3 * time.Second)
			}
			return true
		}
	}
	if containerAlreadyExists {
		libs.GetLogger("create_container").Printf("Container %d already exists, destroying it first...", containerIDInt)
		libs.DestroyContainer(lxcHost, containerIDInt, a.Cfg, lxcService)
	}
	resources := a.ContainerCfg.Resources
	if resources == nil {
		resources = &libs.ContainerResources{
			Memory:     2048,
			Swap:       2048,
			Cores:      4,
			RootfsSize: 20,
		}
	}
	unprivileged := !shouldBePrivileged
	libs.GetLogger("create_container").Printf("Creating container %d from template...", containerIDInt)
	output, exitCode := pctService.Create(containerIDInt, templatePath, hostname, resources.Memory, resources.Swap, resources.Cores, ipAddress, gateway, a.Cfg.LXC.Bridge, a.Cfg.LXC.Storage, resources.RootfsSize, unprivileged, "ubuntu", "amd64")
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("create_container").Printf("Failed to create container %d: %s", containerIDInt, output)
		return false
	}
	libs.GetLogger("create_container").Printf("Setting container features...")
	output, exitCode = pctService.SetFeatures(containerIDInt, shouldBeNested, true, true)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("create_container").Printf("Failed to set container features: %s", output)
	}
	autostart := true
	if a.ContainerCfg.Autostart != nil {
		autostart = *a.ContainerCfg.Autostart
	}
	libs.GetLogger("create_container").Printf("Setting autostart for container %d (onboot=%s)...", containerIDInt, map[bool]string{true: "1", false: "0"}[autostart])
	output, exitCode = pctService.SetOnboot(containerIDInt, autostart)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("create_container").Printf("Failed to set autostart (onboot) for container %d: %s", containerIDInt, output)
		return false
	}
	libs.GetLogger("create_container").Printf("Starting container %d...", containerIDInt)
	output, exitCode = pctService.Start(containerIDInt)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("create_container").Printf("Failed to start container %d: %s", containerIDInt, output)
		return false
	}
	libs.GetLogger("create_container").Printf("Bringing up network interface...")
	pingCmd := "ping -c 1 8.8.8.8"
	timeout := 10
	output, exitCode = pctService.Execute(a.ContainerCfg.ID, pingCmd, &timeout)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("create_container").Printf("Ping to 8.8.8.8 failed (network may still be initializing): %s", output)
	} else {
		libs.GetLogger("create_container").Printf("Network interface is up and reachable")
	}
	for _, userCfg := range a.Cfg.Users.Users {
		username := userCfg.Name
		sudoGroup := userCfg.SudoGroup
		checkCmd := cli.NewUser().Username(username).CheckExists()
		addCmd := cli.NewUser().Username(username).Shell("/bin/bash").Groups([]string{sudoGroup}).CreateHome(true).Add()
		userCheckCmd := fmt.Sprintf("%s || %s", checkCmd, addCmd)
		output, exitCode = pctService.Execute(a.ContainerCfg.ID, userCheckCmd, nil)
		if exitCode != nil && *exitCode != 0 {
			libs.GetLogger("create_container").Printf("Failed to create user %s: %s", username, output)
			return false
		}
		if userCfg.Password != nil && *userCfg.Password != "" {
			passwordCmd := fmt.Sprintf("echo '%s:%s' | chpasswd", username, *userCfg.Password)
			output, exitCode = pctService.Execute(a.ContainerCfg.ID, passwordCmd, nil)
			if exitCode != nil && *exitCode != 0 {
				libs.GetLogger("create_container").Printf("Failed to set password for user %s: %s", username, output)
				return false
			}
			libs.GetLogger("create_container").Printf("Password set for user %s", username)
		}
		sudoersPath := fmt.Sprintf("/etc/sudoers.d/%s", username)
		sudoersContent := fmt.Sprintf("%s ALL=(ALL) NOPASSWD: ALL\n", username)
		sudoersWriteCmd := cli.NewFileOps().Write(sudoersPath, sudoersContent)
		output, exitCode = pctService.Execute(a.ContainerCfg.ID, sudoersWriteCmd, nil)
		if exitCode != nil && *exitCode != 0 {
			libs.GetLogger("create_container").Printf("Failed to write sudoers file for user %s: %s", username, output)
			return false
		}
		sudoersChmodCmd := cli.NewFileOps().Chmod(sudoersPath, "440")
		output, exitCode = pctService.Execute(a.ContainerCfg.ID, sudoersChmodCmd, nil)
		if exitCode != nil && *exitCode != 0 {
			libs.GetLogger("create_container").Printf("Failed to secure sudoers file for user %s: %s", username, output)
			return false
		}
	}
	defaultUser := a.Cfg.Users.DefaultUser()
	if !libs.SetupSSHKey(containerIDInt, ipAddress, a.Cfg, lxcService, pctService) {
		libs.GetLogger("create_container").Printf("Failed to setup SSH key")
		return false
	}
	if !pctService.EnsureSSHServiceRunning(containerIDInt, a.Cfg) {
		libs.GetLogger("create_container").Printf("Failed to ensure SSH service is running")
		return false
	}
	libs.GetLogger("create_container").Printf("Waiting for container to be ready with SSH connectivity (up to 10 minutes)...")
	if !pctService.WaitForContainer(containerIDInt, ipAddress, a.Cfg, defaultUser) {
		libs.GetLogger("create_container").Printf("Container %d did not become ready within 10 minutes", containerIDInt)
		return false
	}
	libs.GetLogger("create_container").Printf("Container %d created successfully", containerIDInt)
	return true
}
