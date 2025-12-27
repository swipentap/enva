package actions

import (
	"enva/libs"
	"enva/orchestration"
	"enva/services"
	"enva/verification"
	"time"
)

// SetupKubernetesAction sets up Kubernetes (k3s) cluster
type SetupKubernetesAction struct {
	*BaseAction
}

func NewSetupKubernetesAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &SetupKubernetesAction{
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

func (a *SetupKubernetesAction) Description() string {
	return "setup kubernetes"
}

func (a *SetupKubernetesAction) Execute() bool {
	if a.Cfg == nil {
		libs.GetLogger("setup_kubernetes").Printf("Lab configuration is missing for SetupKubernetesAction.")
		return false
	}
	libs.GetLogger("setup_kubernetes").Printf("Deploying Kubernetes (k3s) cluster...")
	if !orchestration.DeployKubernetes(a.Cfg) {
		libs.GetLogger("setup_kubernetes").Printf("Kubernetes deployment failed.")
		return false
	}
	libs.GetLogger("setup_kubernetes").Printf("Kubernetes deployment completed successfully.")

	// Verify cluster health after deployment
	if a.PCTService != nil {
		libs.GetLogger("setup_kubernetes").Printf("Verifying k3s cluster health...")
		time.Sleep(10 * time.Second) // Give services time to stabilize
		if !verification.VerifyKubernetesCluster(a.Cfg, a.PCTService) {
			libs.GetLogger("setup_kubernetes").Printf("⚠ Cluster health verification found issues, but deployment completed")
			// Don't fail deployment, just warn
		}
	}
	return true
}
