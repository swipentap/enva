package actions

import (
	"fmt"
	"strings"
	"enva/libs"
	"enva/services"
)

var actionRegistry = make(map[string]ActionFactory)

// RegisterAction registers an action factory
func RegisterAction(name string, factory ActionFactory) {
	// Register with normalized name
	normalized := normalizeActionName(name)
	actionRegistry[normalized] = factory
}

// GetAction creates an action instance by name
func GetAction(actionName string, sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) (Action, error) {
	normalized := normalizeActionName(actionName)
	if factory, ok := actionRegistry[normalized]; ok {
		return factory(sshService, aptService, pctService, containerID, cfg, containerCfg), nil
	}
	return nil, fmt.Errorf("action '%s' not found. Available actions: %v", actionName, getAvailableActions())
}

func normalizeActionName(name string) string {
	// Normalize: lowercase, replace spaces/dashes/underscores with dashes
	name = strings.ToLower(name)
	name = strings.ReplaceAll(name, " ", "-")
	name = strings.ReplaceAll(name, "_", "-")
	return strings.TrimSpace(name)
}

func getAvailableActions() []string {
	actions := make([]string, 0, len(actionRegistry))
	for name := range actionRegistry {
		actions = append(actions, name)
	}
	return actions
}

