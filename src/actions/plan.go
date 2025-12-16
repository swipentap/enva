package actions

import "enva/libs"

// DeployPlan holds deployment sequencing information
type DeployPlan struct {
	AptCacheContainer *libs.ContainerConfig
	Templates         []*libs.TemplateConfig
	ContainersList    []*libs.ContainerConfig
	TotalSteps        int
	Step              int
	StartStep         int
	EndStep           *int
	CurrentActionStep int
	PlanOnly          bool
}

