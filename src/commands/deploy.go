package commands

import (
	"enva/actions"
	"enva/libs"
	"enva/orchestration"
	"enva/services"
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"
)

// DeployError is raised when deployment fails
type DeployError struct {
	Message string
}

func (e *DeployError) Error() string {
	return e.Message
}

// Deploy handles deployment orchestration
type Deploy struct {
	cfg        *libs.LabConfig
	lxcService *services.LXCService
	pctService *services.PCTService
}

// NewDeploy creates a new Deploy command
func NewDeploy(cfg *libs.LabConfig, lxcService *services.LXCService, pctService *services.PCTService) *Deploy {
	return &Deploy{
		cfg:        cfg,
		lxcService: lxcService,
		pctService: pctService,
	}
}

// Run executes the deployment
func (d *Deploy) Run(startStep int, endStep *int, planOnly bool) error {
	logger := libs.GetLogger("deploy")
	defer func() {
		if r := recover(); r != nil {
			if err, ok := r.(error); ok {
				logger.Error("Error during deployment: %s", err.Error())
				logger.LogTraceback(err)
			} else {
				logger.Error("Error during deployment: %v", r)
			}
			os.Exit(1)
		}
	}()
	plan := d.buildPlan(startStep, endStep)
	if planOnly {
		d.logDeployPlan(plan)
		logger.Info("")
		logger.Info("Plan-only mode: Exiting without executing deployment.")
		return nil
	}
	err := d.runDeploy(plan)
	if err != nil {
		logger.Error("Error during deployment: %s", err.Error())
		logger.LogTraceback(err)
		return err
	}
	return nil
}

func (d *Deploy) runDeploy(plan *actions.DeployPlan) error {
	logger := libs.GetLogger("deploy")
	plan = d.buildPlan(plan.StartStep, plan.EndStep)
	d.logDeployPlan(plan)
	logger.Info("==================================================")
	logger.Info("Executing Deployment")
	logger.Info("==================================================")
	if plan.AptCacheContainer != nil {
		aptCacheSteps := 1 + d.countActions(plan.AptCacheContainer)
		aptCacheStart := plan.CurrentActionStep + 1
		aptCacheEnd := aptCacheStart + aptCacheSteps - 1
		if plan.StartStep <= aptCacheEnd {
			// Temporarily set template to nil (base template) for apt-cache, matching Python behavior
			originalTemplate := plan.AptCacheContainer.Template
			plan.AptCacheContainer.Template = nil
			err := d.executeContainerActions(plan, plan.AptCacheContainer, "apt-cache")
			plan.AptCacheContainer.Template = originalTemplate
			if err != nil {
				return err
			}
		} else {
			plan.CurrentActionStep += aptCacheSteps
		}
		if plan.EndStep != nil && plan.CurrentActionStep >= *plan.EndStep {
			logger.Info("Reached end step %d, stopping deployment", *plan.EndStep)
			failedPorts := []PortFailure{}
			if *plan.EndStep == plan.TotalSteps {
				failedPorts = d.checkServicePorts()
			}
			logDeploySummary(d.cfg, failedPorts)
			if len(failedPorts) > 0 {
				return d.createPortError(failedPorts)
			}
			return nil
		}
	}
	for _, templateCfg := range plan.Templates {
		templateSteps := 1 + d.countTemplateActions(templateCfg)
		templateStart := plan.CurrentActionStep + 1
		templateEnd := templateStart + templateSteps - 1
		if plan.StartStep <= templateEnd {
			if err := d.executeTemplateActions(plan, templateCfg); err != nil {
				return err
			}
		} else {
			plan.CurrentActionStep += templateSteps
		}
		if plan.EndStep != nil && plan.CurrentActionStep >= *plan.EndStep {
			logger.Info("Reached end step %d, stopping deployment", *plan.EndStep)
			failedPorts := []PortFailure{}
			if *plan.EndStep == plan.TotalSteps {
				failedPorts = d.checkServicePorts()
			}
			logDeploySummary(d.cfg, failedPorts)
			if len(failedPorts) > 0 {
				return d.createPortError(failedPorts)
			}
			return nil
		}
	}
	for _, containerCfg := range plan.ContainersList {
		containerSteps := 1 + d.countActions(containerCfg)
		containerStart := plan.CurrentActionStep + 1
		containerEnd := containerStart + containerSteps - 1
		if plan.StartStep <= containerEnd {
			if err := d.executeContainerActions(plan, containerCfg, containerCfg.Name); err != nil {
				return err
			}
			if plan.EndStep != nil && plan.CurrentActionStep >= *plan.EndStep {
				logger.Info("Reached end step %d, stopping deployment", *plan.EndStep)
				failedPorts := []PortFailure{}
				if *plan.EndStep == plan.TotalSteps {
					failedPorts = d.checkServicePorts()
				}
				logDeploySummary(d.cfg, failedPorts)
				if len(failedPorts) > 0 {
					return d.createPortError(failedPorts)
				}
				return nil
			}
		} else {
			plan.CurrentActionStep += containerSteps
			if plan.EndStep != nil && plan.CurrentActionStep >= *plan.EndStep {
				logger.Info("Reached end step %d, stopping deployment", *plan.EndStep)
				failedPorts := []PortFailure{}
				if *plan.EndStep == plan.TotalSteps {
					failedPorts = d.checkServicePorts()
				}
				logDeploySummary(d.cfg, failedPorts)
				if len(failedPorts) > 0 {
					return d.createPortError(failedPorts)
				}
				return nil
			}
		}
	}
	var kubernetesContainers []*libs.ContainerConfig
	if d.cfg.Kubernetes != nil {
		k8sIDs := make(map[int]bool)
		for _, id := range d.cfg.Kubernetes.Control {
			k8sIDs[id] = true
		}
		for _, id := range d.cfg.Kubernetes.Workers {
			k8sIDs[id] = true
		}
		for i := range d.cfg.Containers {
			if k8sIDs[d.cfg.Containers[i].ID] {
				kubernetesContainers = append(kubernetesContainers, &d.cfg.Containers[i])
			}
		}
	}
	for _, containerCfg := range kubernetesContainers {
		containerSteps := 1 + d.countActions(containerCfg)
		containerStart := plan.CurrentActionStep + 1
		containerEnd := containerStart + containerSteps - 1
		if plan.StartStep <= containerEnd {
			if err := d.executeContainerActions(plan, containerCfg, containerCfg.Name); err != nil {
				return err
			}
			if plan.EndStep != nil && plan.CurrentActionStep >= *plan.EndStep {
				logger.Info("Reached end step %d, stopping deployment", *plan.EndStep)
				failedPorts := []PortFailure{}
				if *plan.EndStep == plan.TotalSteps {
					failedPorts = d.checkServicePorts()
				}
				logDeploySummary(d.cfg, failedPorts)
				if len(failedPorts) > 0 {
					return d.createPortError(failedPorts)
				}
				return nil
			}
		} else {
			plan.CurrentActionStep += containerSteps
			if plan.EndStep != nil && plan.CurrentActionStep >= *plan.EndStep {
				logger.Info("Reached end step %d, stopping deployment", *plan.EndStep)
				failedPorts := []PortFailure{}
				if *plan.EndStep == plan.TotalSteps {
					failedPorts = d.checkServicePorts()
				}
				logDeploySummary(d.cfg, failedPorts)
				if len(failedPorts) > 0 {
					return d.createPortError(failedPorts)
				}
				return nil
			}
		}
	}
	if len(kubernetesContainers) > 0 {
		plan.CurrentActionStep++
		if plan.CurrentActionStep < plan.StartStep {
			logger.Info("Skipping setup kubernetes (step %d < start_step %d)", plan.CurrentActionStep, plan.StartStep)
		} else if plan.EndStep != nil && plan.CurrentActionStep > *plan.EndStep {
			logger.Info("Reached end step %d, stopping deployment", *plan.EndStep)
			failedPorts := []PortFailure{}
			if *plan.EndStep == plan.TotalSteps {
				failedPorts = d.checkServicePorts()
			}
			logDeploySummary(d.cfg, failedPorts)
			if len(failedPorts) > 0 {
				return d.createPortError(failedPorts)
			}
			return nil
		} else {
			overallPct := int((float64(plan.CurrentActionStep) / float64(plan.TotalSteps)) * 100)
			logger.Info("==================================================")
			logger.Info("[Overall: %d%%] [Step: %d/%d] Executing: kubernetes - setup kubernetes", overallPct, plan.CurrentActionStep, plan.TotalSteps)
			logger.Info("==================================================")
			if !d.lxcService.IsConnected() {
				if !d.lxcService.Connect() {
					return &DeployError{Message: fmt.Sprintf("Failed to connect to LXC host %s", d.cfg.LXCHost())}
				}
			}
			pctService := services.NewPCTService(d.lxcService)
			setupKubernetesAction := actions.NewSetupKubernetesAction(nil, nil, pctService, nil, d.cfg, nil)
			if !setupKubernetesAction.Execute() {
				return &DeployError{Message: "Failed to execute setup kubernetes action"}
			}
		}
		if plan.EndStep != nil && plan.CurrentActionStep >= *plan.EndStep {
			logger.Info("Reached end step %d, stopping deployment", *plan.EndStep)
			failedPorts := []PortFailure{}
			if *plan.EndStep == plan.TotalSteps {
				failedPorts = d.checkServicePorts()
			}
			logDeploySummary(d.cfg, failedPorts)
			if len(failedPorts) > 0 {
				return d.createPortError(failedPorts)
			}
			return nil
		}
	}
	if d.cfg.GlusterFS != nil {
		plan.CurrentActionStep++
		if plan.CurrentActionStep < plan.StartStep {
			logger.Info("Skipping GlusterFS setup (step %d < start_step %d)", plan.CurrentActionStep, plan.StartStep)
		} else if plan.EndStep != nil && plan.CurrentActionStep > *plan.EndStep {
			logger.Info("Reached end step %d, stopping deployment", *plan.EndStep)
		} else {
			overallPct := int((float64(plan.CurrentActionStep) / float64(plan.TotalSteps)) * 100)
			logger.Info("==================================================")
			logger.Info("[Overall: %d%%] [Step: %d/%d] Executing: GlusterFS setup", overallPct, plan.CurrentActionStep, plan.TotalSteps)
			logger.Info("==================================================")
			if !orchestration.SetupGlusterFS(d.cfg) {
				return &DeployError{Message: "GlusterFS setup failed"}
			}
		}
	}
	if d.cfg.Kubernetes != nil && d.cfg.KubernetesActions != nil && len(d.cfg.KubernetesActions) > 0 {
		// Get control node info for SSH service
		var sshService *services.SSHService
		var aptService *services.APTService
		if len(d.cfg.Kubernetes.Control) > 0 {
			controlID := d.cfg.Kubernetes.Control[0]
			var controlNode *libs.ContainerConfig
			for i := range d.cfg.Containers {
				if d.cfg.Containers[i].ID == controlID {
					controlNode = &d.cfg.Containers[i]
					break
				}
			}
			if controlNode != nil && controlNode.IPAddress != nil {
				defaultUser := d.cfg.Users.DefaultUser()
				containerSSHConfig := libs.SSHConfig{
					ConnectTimeout:     d.cfg.SSH.ConnectTimeout,
					BatchMode:          d.cfg.SSH.BatchMode,
					DefaultExecTimeout: d.cfg.SSH.DefaultExecTimeout,
					ReadBufferSize:     d.cfg.SSH.ReadBufferSize,
					PollInterval:       d.cfg.SSH.PollInterval,
					DefaultUsername:    defaultUser,
					LookForKeys:        d.cfg.SSH.LookForKeys,
					AllowAgent:         d.cfg.SSH.AllowAgent,
					Verbose:            d.cfg.SSH.Verbose,
				}
				sshService = services.NewSSHService(fmt.Sprintf("%s@%s", defaultUser, *controlNode.IPAddress), &containerSSHConfig)
				if sshService.Connect() {
					aptService = services.NewAPTService(sshService)
				} else {
					sshService = nil
				}
			}
		}
		for _, actionName := range d.cfg.KubernetesActions {
			plan.CurrentActionStep++
			if plan.CurrentActionStep < plan.StartStep {
				logger.Info("Skipping Kubernetes action '%s' (step %d < start_step %d)", actionName, plan.CurrentActionStep, plan.StartStep)
				continue
			}
			if plan.EndStep != nil && plan.CurrentActionStep > *plan.EndStep {
				logger.Info("Reached end step %d, stopping deployment", *plan.EndStep)
				break
			}
			overallPct := int((float64(plan.CurrentActionStep) / float64(plan.TotalSteps)) * 100)
			logger.Info("==================================================")
			logger.Info("[Overall: %d%%] [Step: %d/%d] Executing: Kubernetes action - %s", overallPct, plan.CurrentActionStep, plan.TotalSteps, actionName)
			logger.Info("==================================================")
			action, err := actions.GetAction(actionName, sshService, aptService, d.pctService, nil, d.cfg, nil)
			if err != nil {
				if sshService != nil {
					sshService.Disconnect()
				}
				return &DeployError{Message: fmt.Sprintf("Kubernetes action '%s' not found: %v", actionName, err)}
			}
			if !action.Execute() {
				if sshService != nil {
					sshService.Disconnect()
				}
				return &DeployError{Message: fmt.Sprintf("Kubernetes action '%s' failed", actionName)}
			}
		}
		if sshService != nil {
			sshService.Disconnect()
		}
	}
	failedPorts := d.checkServicePorts()
	logDeploySummary(d.cfg, failedPorts)
	if len(failedPorts) > 0 {
		return d.createPortError(failedPorts)
	}
	return nil
}

func (d *Deploy) executeContainerActions(plan *actions.DeployPlan, containerCfg *libs.ContainerConfig, containerName string) error {
	logger := libs.GetLogger("deploy")
	containerIDStr := strconv.Itoa(containerCfg.ID)
	ipAddress := ""
	if containerCfg.IPAddress != nil {
		ipAddress = *containerCfg.IPAddress
	}
	plan.CurrentActionStep++
	skipContainerCreation := plan.CurrentActionStep < plan.StartStep
	if skipContainerCreation {
		logger.Info("Skipping container '%s' creation (step %d < start_step %d)", containerName, plan.CurrentActionStep, plan.StartStep)
	}
	if plan.EndStep != nil && plan.CurrentActionStep > *plan.EndStep {
		logger.Info("Reached end step %d, stopping deployment", *plan.EndStep)
		return nil
	}
	if !skipContainerCreation {
		overallPct := int((float64(plan.CurrentActionStep) / float64(plan.TotalSteps)) * 100)
		logger.Info("==================================================")
		logger.Info("[Overall: %d%%] [Step: %d/%d] Executing: %s - create container", overallPct, plan.CurrentActionStep, plan.TotalSteps, containerName)
		logger.Info("==================================================")
		createAction := actions.NewCreateContainerAction(nil, nil, nil, nil, d.cfg, containerCfg, plan)
		if !createAction.Execute() {
			return &DeployError{Message: fmt.Sprintf("Failed to create container: %s", containerName)}
		}
		logger.Info("Container '%s' created successfully", containerName)
	}
	defaultUser := d.cfg.Users.DefaultUser()
	logger.Info("Setting up SSH connection to container %s...", containerName)
	containerSSHConfig := libs.SSHConfig{
		ConnectTimeout:     d.cfg.SSH.ConnectTimeout,
		BatchMode:          d.cfg.SSH.BatchMode,
		DefaultExecTimeout: d.cfg.SSH.DefaultExecTimeout,
		ReadBufferSize:     d.cfg.SSH.ReadBufferSize,
		PollInterval:       d.cfg.SSH.PollInterval,
		DefaultUsername:    defaultUser,
		LookForKeys:        d.cfg.SSH.LookForKeys,
		AllowAgent:         d.cfg.SSH.AllowAgent,
		Verbose:            d.cfg.SSH.Verbose,
	}
	sshService := services.NewSSHService(fmt.Sprintf("%s@%s", defaultUser, ipAddress), &containerSSHConfig)
	if !sshService.Connect() {
		return &DeployError{Message: fmt.Sprintf("Failed to connect to container %s via SSH", containerName)}
	}
	defer sshService.Disconnect()
	time.Sleep(2 * time.Second)
	aptService := services.NewAPTService(sshService)
	if !d.lxcService.IsConnected() {
		if !d.lxcService.Connect() {
			return &DeployError{Message: fmt.Sprintf("Failed to connect to LXC host %s", d.cfg.LXCHost())}
		}
	}
	actionNames := containerCfg.Actions
	if actionNames == nil {
		actionNames = []string{}
	}
	for _, actionName := range actionNames {
		plan.CurrentActionStep++
		if plan.CurrentActionStep < plan.StartStep {
			continue
		}
		if plan.EndStep != nil && plan.CurrentActionStep > *plan.EndStep {
			logger.Info("Reached end step %d, stopping action execution", *plan.EndStep)
			return nil
		}
		overallPct := int((float64(plan.CurrentActionStep) / float64(plan.TotalSteps)) * 100)
		logger.Info("==================================================")
		logger.Info("[Overall: %d%%] [Step: %d/%d] Executing: %s - %s", overallPct, plan.CurrentActionStep, plan.TotalSteps, containerName, actionName)
		logger.Info("==================================================")
		action, err := actions.GetAction(actionName, sshService, aptService, d.pctService, &containerIDStr, d.cfg, containerCfg)
		if err != nil {
			return &DeployError{Message: fmt.Sprintf("Action '%s' not found for container '%s': %v", actionName, containerName, err)}
		}
		if !action.Execute() {
			return &DeployError{Message: fmt.Sprintf("Failed to execute action '%s' for container '%s'", actionName, containerName)}
		}
		logger.Info("Action '%s' for container '%s' completed successfully", actionName, containerName)
	}
	return nil
}

func (d *Deploy) executeTemplateActions(plan *actions.DeployPlan, templateCfg *libs.TemplateConfig) error {
	logger := libs.GetLogger("deploy")
	containerCfg := &libs.ContainerConfig{
		Name:       templateCfg.Name,
		ID:         templateCfg.ID,
		IP:         templateCfg.IP,
		Hostname:   templateCfg.Hostname,
		Template:   templateCfg.Template,
		Resources:  templateCfg.Resources,
		IPAddress:  templateCfg.IPAddress,
		Actions:    templateCfg.Actions,
		Privileged: templateCfg.Privileged,
		Nested:     templateCfg.Nested,
	}
	if err := d.executeContainerActions(plan, containerCfg, templateCfg.Name); err != nil {
		return err
	}
	logger.Info("Destroying template container %d after processing...", containerCfg.ID)
	libs.DestroyContainer(d.cfg.LXCHost(), containerCfg.ID, d.cfg, d.lxcService)
	return nil
}

func (d *Deploy) countActions(containerCfg *libs.ContainerConfig) int {
	if containerCfg.Actions == nil {
		return 0
	}
	return len(containerCfg.Actions)
}

func (d *Deploy) countTemplateActions(templateCfg *libs.TemplateConfig) int {
	if templateCfg.Actions == nil {
		return 0
	}
	return len(templateCfg.Actions)
}

func (d *Deploy) logDeployPlan(plan *actions.DeployPlan) {
	logger := libs.GetLogger("deploy")
	type stepInfo struct {
		num   int
		label string
	}
	var steps []stepInfo
	stepNum := 1
	if plan.AptCacheContainer != nil {
		c := plan.AptCacheContainer
		steps = append(steps, stepInfo{stepNum, fmt.Sprintf("%s: create container", c.Name)})
		stepNum++
		if c.Actions != nil {
			for _, action := range c.Actions {
				steps = append(steps, stepInfo{stepNum, fmt.Sprintf("%s: %s", c.Name, action)})
				stepNum++
			}
		}
	}
	for _, tmpl := range plan.Templates {
		steps = append(steps, stepInfo{stepNum, fmt.Sprintf("%s: create template", tmpl.Name)})
		stepNum++
		if tmpl.Actions != nil {
			for _, action := range tmpl.Actions {
				steps = append(steps, stepInfo{stepNum, fmt.Sprintf("%s: %s", tmpl.Name, action)})
				stepNum++
			}
		}
	}
	for _, c := range plan.ContainersList {
		steps = append(steps, stepInfo{stepNum, fmt.Sprintf("%s: create container", c.Name)})
		stepNum++
		if c.Actions != nil {
			for _, action := range c.Actions {
				steps = append(steps, stepInfo{stepNum, fmt.Sprintf("%s: %s", c.Name, action)})
				stepNum++
			}
		}
	}
	var kubernetesContainers []*libs.ContainerConfig
	if d.cfg.Kubernetes != nil {
		k8sIDs := make(map[int]bool)
		for _, id := range d.cfg.Kubernetes.Control {
			k8sIDs[id] = true
		}
		for _, id := range d.cfg.Kubernetes.Workers {
			k8sIDs[id] = true
		}
		for i := range d.cfg.Containers {
			if k8sIDs[d.cfg.Containers[i].ID] {
				kubernetesContainers = append(kubernetesContainers, &d.cfg.Containers[i])
			}
		}
	}
	for _, c := range kubernetesContainers {
		steps = append(steps, stepInfo{stepNum, fmt.Sprintf("%s: create container", c.Name)})
		stepNum++
		if c.Actions != nil {
			for _, action := range c.Actions {
				steps = append(steps, stepInfo{stepNum, fmt.Sprintf("%s: %s", c.Name, action)})
				stepNum++
			}
		}
	}
	if len(kubernetesContainers) > 0 {
		steps = append(steps, stepInfo{stepNum, "kubernetes: setup kubernetes"})
		stepNum++
	}
	if d.cfg.GlusterFS != nil {
		steps = append(steps, stepInfo{stepNum, "glusterfs: setup glusterfs"})
		stepNum++
	}
	if d.cfg.KubernetesActions != nil && len(d.cfg.KubernetesActions) > 0 {
		for _, actionName := range d.cfg.KubernetesActions {
			steps = append(steps, stepInfo{stepNum, fmt.Sprintf("kubernetes: %s", actionName)})
			stepNum++
		}
	}
	logger.Info("")
	endStepDisplay := plan.TotalSteps
	if plan.EndStep != nil {
		endStepDisplay = *plan.EndStep
	}
	logger.Info("Deploy plan (total %d steps, running %d-%d):", plan.TotalSteps, plan.StartStep, endStepDisplay)
	for _, step := range steps {
		endStep := plan.TotalSteps
		if plan.EndStep != nil {
			endStep = *plan.EndStep
		}
		marker := "skip"
		if plan.StartStep <= step.num && step.num <= endStep {
			marker = "RUN"
		}
		logger.Info("  [%2d] %-4s %s", step.num, marker, step.label)
	}
}

func (d *Deploy) buildPlan(startStep int, endStep *int) *actions.DeployPlan {
	cfg := d.cfg
	containers := cfg.Containers
	var aptCacheContainer *libs.ContainerConfig
	for i := range containers {
		if containers[i].Name == cfg.APTCacheCT {
			aptCacheContainer = &containers[i]
			break
		}
	}
	templates := make([]*libs.TemplateConfig, len(cfg.Templates))
	for i := range cfg.Templates {
		templates[i] = &cfg.Templates[i]
	}
	k8sIDs := make(map[int]bool)
	if cfg.Kubernetes != nil {
		for _, id := range cfg.Kubernetes.Control {
			k8sIDs[id] = true
		}
		for _, id := range cfg.Kubernetes.Workers {
			k8sIDs[id] = true
		}
	}
	var containersList []*libs.ContainerConfig
	for i := range containers {
		if !k8sIDs[containers[i].ID] && containers[i].Name != cfg.APTCacheCT {
			containersList = append(containersList, &containers[i])
		}
	}
	totalSteps := 0
	if aptCacheContainer != nil {
		totalSteps++
		totalSteps += d.countActions(aptCacheContainer)
	}
	for _, template := range templates {
		totalSteps++
		totalSteps += d.countTemplateActions(template)
	}
	for _, container := range containersList {
		totalSteps++
		totalSteps += d.countActions(container)
	}
	var kubernetesContainers []*libs.ContainerConfig
	if cfg.Kubernetes != nil {
		for i := range containers {
			if k8sIDs[containers[i].ID] {
				kubernetesContainers = append(kubernetesContainers, &containers[i])
			}
		}
	}
	for _, container := range kubernetesContainers {
		totalSteps++
		totalSteps += d.countActions(container)
	}
	if len(kubernetesContainers) > 0 {
		totalSteps++
	}
	if cfg.GlusterFS != nil {
		totalSteps++
	}
	if cfg.KubernetesActions != nil {
		totalSteps += len(cfg.KubernetesActions)
	}
	if aptCacheContainer == nil {
		panic(&DeployError{Message: fmt.Sprintf("apt-cache container '%s' not found in configuration", cfg.APTCacheCT)})
	}
	if endStep == nil {
		es := totalSteps
		endStep = &es
	}
	return &actions.DeployPlan{
		AptCacheContainer: aptCacheContainer,
		Templates:         templates,
		ContainersList:    containersList,
		TotalSteps:        totalSteps,
		Step:              1,
		StartStep:         startStep,
		EndStep:           endStep,
		CurrentActionStep: 0,
		PlanOnly:          false,
	}
}

type PortFailure struct {
	Name string
	IP   string
	Port int
}

func (d *Deploy) checkServicePorts() []PortFailure {
	logger := libs.GetLogger("deploy")
	logger.Info("Checking service ports...")
	time.Sleep(5 * time.Second)
	var failedPorts []PortFailure
	if !d.lxcService.IsConnected() {
		if !d.lxcService.Connect() {
			logger.Error("Failed to connect to LXC host %s", d.cfg.LXCHost())
			return failedPorts
		}
	}
	var aptCacheCT *libs.ContainerConfig
	for i := range d.cfg.Containers {
		if d.cfg.Containers[i].Name == d.cfg.APTCacheCT {
			aptCacheCT = &d.cfg.Containers[i]
			break
		}
	}
	if aptCacheCT != nil && aptCacheCT.IPAddress != nil {
		port := 3142
		if d.cfg.Services.APTcache.Port != nil {
			port = *d.cfg.Services.APTcache.Port
		}
		cmd := fmt.Sprintf("nc -zv %s %d 2>&1", *aptCacheCT.IPAddress, port)
		result, _ := d.lxcService.Execute(cmd, nil)
		if result != "" && (strings.Contains(strings.ToLower(result), "open") || strings.Contains(strings.ToLower(result), "succeeded")) {
			logger.Info("  ✓ apt-cache: %s:%d", *aptCacheCT.IPAddress, port)
		} else {
			logger.Error("  ✗ apt-cache: %s:%d - NOT RESPONDING", *aptCacheCT.IPAddress, port)
			failedPorts = append(failedPorts, PortFailure{"apt-cache", *aptCacheCT.IPAddress, port})
		}
	}
	var pgsqlCT *libs.ContainerConfig
	for i := range d.cfg.Containers {
		if d.cfg.Containers[i].Name == "pgsql" {
			pgsqlCT = &d.cfg.Containers[i]
			break
		}
	}
	if pgsqlCT != nil && pgsqlCT.IPAddress != nil && d.cfg.Services.PostgreSQL != nil {
		port := 5432
		if d.cfg.Services.PostgreSQL.Port != nil {
			port = *d.cfg.Services.PostgreSQL.Port
		}
		cmd := fmt.Sprintf("nc -zv %s %d 2>&1", *pgsqlCT.IPAddress, port)
		result, _ := d.lxcService.Execute(cmd, nil)
		if result != "" && (strings.Contains(strings.ToLower(result), "open") || strings.Contains(strings.ToLower(result), "succeeded")) {
			logger.Info("  ✓ PostgreSQL: %s:%d", *pgsqlCT.IPAddress, port)
		} else {
			logger.Error("  ✗ PostgreSQL: %s:%d - NOT RESPONDING", *pgsqlCT.IPAddress, port)
			failedPorts = append(failedPorts, PortFailure{"PostgreSQL", *pgsqlCT.IPAddress, port})
		}
	}
	var haproxyCT *libs.ContainerConfig
	for i := range d.cfg.Containers {
		if d.cfg.Containers[i].Name == "haproxy" {
			haproxyCT = &d.cfg.Containers[i]
			break
		}
	}
	if haproxyCT != nil && haproxyCT.IPAddress != nil && d.cfg.Services.HAProxy != nil {
		httpPort := 80
		if d.cfg.Services.HAProxy.HTTPPort != nil {
			httpPort = *d.cfg.Services.HAProxy.HTTPPort
		}
		statsPort := 8404
		if d.cfg.Services.HAProxy.StatsPort != nil {
			statsPort = *d.cfg.Services.HAProxy.StatsPort
		}
		cmd := fmt.Sprintf("nc -zv %s %d 2>&1", *haproxyCT.IPAddress, httpPort)
		result, _ := d.lxcService.Execute(cmd, nil)
		if result != "" && (strings.Contains(strings.ToLower(result), "open") || strings.Contains(strings.ToLower(result), "succeeded")) {
			logger.Info("  ✓ HAProxy HTTP: %s:%d", *haproxyCT.IPAddress, httpPort)
		} else {
			logger.Error("  ✗ HAProxy HTTP: %s:%d - NOT RESPONDING", *haproxyCT.IPAddress, httpPort)
			failedPorts = append(failedPorts, PortFailure{"HAProxy HTTP", *haproxyCT.IPAddress, httpPort})
		}
		cmd = fmt.Sprintf("nc -zv %s %d 2>&1", *haproxyCT.IPAddress, statsPort)
		result, _ = d.lxcService.Execute(cmd, nil)
		if result != "" && (strings.Contains(strings.ToLower(result), "open") || strings.Contains(strings.ToLower(result), "succeeded")) {
			logger.Info("  ✓ HAProxy Stats: %s:%d", *haproxyCT.IPAddress, statsPort)
		} else {
			logger.Error("  ✗ HAProxy Stats: %s:%d - NOT RESPONDING", *haproxyCT.IPAddress, statsPort)
			failedPorts = append(failedPorts, PortFailure{"HAProxy Stats", *haproxyCT.IPAddress, statsPort})
		}
	}
	var dnsCT *libs.ContainerConfig
	for i := range d.cfg.Containers {
		if d.cfg.Containers[i].Name == "dns" {
			dnsCT = &d.cfg.Containers[i]
			break
		}
	}
	if dnsCT != nil && dnsCT.IPAddress != nil {
		port := 53
		if dnsCT.Params != nil {
			if p, ok := dnsCT.Params["dns_port"].(int); ok {
				port = p
			}
		}
		cmdTCP := fmt.Sprintf("nc -zv %s %d 2>&1", *dnsCT.IPAddress, port)
		cmdUDP := fmt.Sprintf("nc -zuv %s %d 2>&1", *dnsCT.IPAddress, port)
		resultTCP, _ := d.lxcService.Execute(cmdTCP, nil)
		resultUDP, _ := d.lxcService.Execute(cmdUDP, nil)
		if (resultTCP != "" && (strings.Contains(strings.ToLower(resultTCP), "open") || strings.Contains(strings.ToLower(resultTCP), "succeeded"))) ||
			(resultUDP != "" && (strings.Contains(strings.ToLower(resultUDP), "open") || strings.Contains(strings.ToLower(resultUDP), "succeeded"))) {
			logger.Info("  ✓ DNS: %s:%d", *dnsCT.IPAddress, port)
		} else {
			logger.Error("  ✗ DNS: %s:%d - NOT RESPONDING", *dnsCT.IPAddress, port)
			failedPorts = append(failedPorts, PortFailure{"DNS", *dnsCT.IPAddress, port})
		}
	}
	if d.cfg.GlusterFS != nil {
		var glusterfsNode *libs.ContainerConfig
		if len(d.cfg.GlusterFS.ClusterNodes) > 0 {
			clusterNodeIDs := make(map[int]bool)
			for _, node := range d.cfg.GlusterFS.ClusterNodes {
				clusterNodeIDs[node.ID] = true
			}
			for i := range d.cfg.Containers {
				if clusterNodeIDs[d.cfg.Containers[i].ID] {
					glusterfsNode = &d.cfg.Containers[i]
					break
				}
			}
		}
		if glusterfsNode != nil && glusterfsNode.IPAddress != nil {
			cmd := fmt.Sprintf("nc -zv %s 24007 2>&1", *glusterfsNode.IPAddress)
			result, _ := d.lxcService.Execute(cmd, nil)
			if result != "" && (strings.Contains(strings.ToLower(result), "open") || strings.Contains(strings.ToLower(result), "succeeded")) {
				logger.Info("  ✓ GlusterFS: %s:24007", *glusterfsNode.IPAddress)
			} else {
				logger.Error("  ✗ GlusterFS: %s:24007 - NOT RESPONDING", *glusterfsNode.IPAddress)
				failedPorts = append(failedPorts, PortFailure{"GlusterFS", *glusterfsNode.IPAddress, 24007})
			}
		}
	}
	return failedPorts
}

func (d *Deploy) createPortError(failedPorts []PortFailure) error {
	errorMsg := "Deploy failed: The following ports are not responding:\n"
	for _, pf := range failedPorts {
		errorMsg += fmt.Sprintf("  - %s: %s:%d\n", pf.Name, pf.IP, pf.Port)
	}
	return &DeployError{Message: errorMsg}
}

func logDeploySummary(cfg *libs.LabConfig, failedPorts []PortFailure) {
	logger := libs.GetLogger("deploy")
	logger.Info("==================================================")
	if len(failedPorts) > 0 {
		logger.Info("Deploy Complete (with port failures)")
	} else {
		logger.Info("Deploy Complete!")
	}
	logger.Info("==================================================")
	logger.Info("Containers:")
	for i := range cfg.Containers {
		ct := &cfg.Containers[i]
		ipAddr := "N/A"
		if ct.IPAddress != nil {
			ipAddr = *ct.IPAddress
		}
		logger.Info("  - %d: %s (%s)", ct.ID, ct.Name, ipAddr)
	}
	var pgsql *libs.ContainerConfig
	for i := range cfg.Containers {
		if cfg.Containers[i].Name == "pgsql" {
			pgsql = &cfg.Containers[i]
			break
		}
	}
	if pgsql != nil && pgsql.IPAddress != nil && cfg.Services.PostgreSQL != nil {
		pgPort := 5432
		if cfg.Services.PostgreSQL.Port != nil {
			pgPort = *cfg.Services.PostgreSQL.Port
		}
		pgUser := "postgres"
		if cfg.Services.PostgreSQL.Username != nil {
			pgUser = *cfg.Services.PostgreSQL.Username
		}
		pgPassword := "postgres"
		if cfg.Services.PostgreSQL.Password != nil {
			pgPassword = *cfg.Services.PostgreSQL.Password
		}
		pgDatabase := "postgres"
		if cfg.Services.PostgreSQL.Database != nil {
			pgDatabase = *cfg.Services.PostgreSQL.Database
		}
		logger.Info("PostgreSQL: %s:%d", *pgsql.IPAddress, pgPort)
		logger.Info("  Username: %s", pgUser)
		logger.Info("  Password: %s", pgPassword)
		logger.Info("  Database: %s", pgDatabase)
		logger.Info("  Connection: postgresql://%s:%s@%s:%d/%s", pgUser, pgPassword, *pgsql.IPAddress, pgPort, pgDatabase)
	}
	var dnsCT *libs.ContainerConfig
	for i := range cfg.Containers {
		if cfg.Containers[i].Name == "dns" {
			dnsCT = &cfg.Containers[i]
			break
		}
	}
	if dnsCT != nil && dnsCT.IPAddress != nil {
		dnsPort := 53
		if dnsCT.Params != nil {
			if p, ok := dnsCT.Params["dns_port"].(int); ok {
				dnsPort = p
			}
		}
		webPort := 80
		if dnsCT.Params != nil {
			if p, ok := dnsCT.Params["web_port"].(int); ok {
				webPort = p
			}
		}
		logger.Info("DNS: %s:%d (TCP/UDP)", *dnsCT.IPAddress, dnsPort)
		logger.Info("  Web Interface: http://%s:%d", *dnsCT.IPAddress, webPort)
		logger.Info("  Use as DNS server: %s", *dnsCT.IPAddress)
	}
	var haproxy *libs.ContainerConfig
	for i := range cfg.Containers {
		if cfg.Containers[i].Name == "haproxy" {
			haproxy = &cfg.Containers[i]
			break
		}
	}
	if haproxy != nil && haproxy.IPAddress != nil {
		httpPort := 80
		statsPort := 8404
		if haproxy.Params != nil {
			if p, ok := haproxy.Params["http_port"].(int); ok {
				httpPort = p
			}
			if p, ok := haproxy.Params["stats_port"].(int); ok {
				statsPort = p
			}
		}
		logger.Info("HAProxy: http://%s:%d (Stats: http://%s:%d)", *haproxy.IPAddress, httpPort, *haproxy.IPAddress, statsPort)
	}
	if cfg.GlusterFS != nil {
		logger.Info("GlusterFS:")
		logger.Info("  Volume: %s", cfg.GlusterFS.VolumeName)
		logger.Info("  Mount: %s on all nodes", cfg.GlusterFS.MountPoint)
	}
	if cfg.Kubernetes != nil && len(cfg.Kubernetes.Control) > 0 && cfg.Services.CockroachDB != nil {
		controlID := cfg.Kubernetes.Control[0]
		var controlNode *libs.ContainerConfig
		for i := range cfg.Containers {
			if cfg.Containers[i].ID == controlID {
				controlNode = &cfg.Containers[i]
				break
			}
		}
		if controlNode != nil && controlNode.IPAddress != nil {
			sqlPort := 32657
			if cfg.Services.CockroachDB.SQLPort != nil {
				sqlPort = *cfg.Services.CockroachDB.SQLPort
			}
			httpPort := 30080
			if cfg.Services.CockroachDB.HTTPPort2 != nil {
				httpPort = *cfg.Services.CockroachDB.HTTPPort2
			}
			logger.Info("CockroachDB:")
			logger.Info("  SQL: %s:%d (postgresql://root:root123@%s:%d/defaultdb?sslmode=disable)", *controlNode.IPAddress, sqlPort, *controlNode.IPAddress, sqlPort)
			logger.Info("  Admin UI: http://%s:%d", *controlNode.IPAddress, httpPort)
			logger.Info("  Username: root")
			logger.Info("  Password: root123")
		}
	}
	if cfg.Kubernetes != nil && len(cfg.Kubernetes.Control) > 0 && cfg.Services.Rancher != nil {
		controlID := cfg.Kubernetes.Control[0]
		var controlNode *libs.ContainerConfig
		for i := range cfg.Containers {
			if cfg.Containers[i].ID == controlID {
				controlNode = &cfg.Containers[i]
				break
			}
		}
		if controlNode != nil && controlNode.IPAddress != nil {
			httpsPort := 30443
			if cfg.Services.Rancher.Port != nil {
				httpsPort = *cfg.Services.Rancher.Port
			}
			bootstrapPassword := "admin"
			lxcService := services.NewLXCService(cfg.LXCHost(), &cfg.SSH)
			if lxcService.Connect() {
				defer lxcService.Disconnect()
				pctService := services.NewPCTService(lxcService)
				getPasswordCmd := "export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && /usr/local/bin/kubectl get secret --namespace cattle-system bootstrap-secret -o go-template='{{.data.bootstrapPassword|base64decode}}' 2>/dev/null || echo 'admin'"
				passwordOutput, _ := pctService.Execute(controlID, getPasswordCmd, nil)
				if passwordOutput != "" {
					bootstrapPassword = strings.TrimSpace(passwordOutput)
				}
			}
			logger.Info("Rancher: https://%s:%d", *controlNode.IPAddress, httpsPort)
			logger.Info("  Bootstrap Password: %s", bootstrapPassword)
		}
	}
	if len(failedPorts) > 0 {
		logger.Info("⚠ Port Status:")
		logger.Info("  The following ports are NOT responding:")
		for _, pf := range failedPorts {
			logger.Info("    ✗ %s: %s:%d", pf.Name, pf.IP, pf.Port)
		}
	} else {
		logger.Info("✓ All service ports are responding")
	}
}
