package actions

import (
	"enva/libs"
	"enva/services"
)

// Action is the base interface for container setup actions
type Action interface {
	Execute() bool
	Description() string
}

// BaseAction provides base functionality for actions
type BaseAction struct {
	SSHService   *services.SSHService
	APTService   *services.APTService
	PCTService   *services.PCTService
	ContainerID *string
	Cfg          *libs.LabConfig
	ContainerCfg *libs.ContainerConfig
}

// Description returns the action description
func (b *BaseAction) Description() string {
	return ""
}

