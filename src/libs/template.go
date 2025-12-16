package libs

import (
	"fmt"
	"strings"
)

// GetBaseTemplate gets base Ubuntu template, download if needed
// Note: lxcService parameter is required to avoid import cycle
func GetBaseTemplate(lxcHost string, cfg *LabConfig, lxcService LXCServiceInterface) *string {
	if lxcService == nil {
		GetLogger("template").Printf("lxcService required for GetBaseTemplate")
		return nil
	}
	templates := cfg.TemplateConfig.Base
	templateDir := cfg.LXCTemplateDir()

	for _, template := range templates {
		checkCmd := fmt.Sprintf("test -f %s/%s && echo exists || echo missing", templateDir, template)
		checkResult, _ := lxcService.Execute(checkCmd, nil)
		if strings.Contains(checkResult, "exists") {
			return &template
		}
	}

	// Download last template in list
	if len(templates) == 0 {
		return nil
	}
	templateToDownload := templates[len(templates)-1]
	GetLogger("template").Printf("Base template not found. Downloading %s...", templateToDownload)

	// Run pveam download with live output
	downloadCmd := fmt.Sprintf("pveam download local %s", templateToDownload)
	GetLogger("template").Debug("Running: %s", downloadCmd)

	timeout := 300
	lxcService.Execute(downloadCmd, &timeout)

	// Verify download completed
	verifyCmd := fmt.Sprintf("test -f %s/%s && echo exists || echo missing", templateDir, templateToDownload)
	verifyResult, _ := lxcService.Execute(verifyCmd, nil)

	if !strings.Contains(verifyResult, "exists") {
		GetLogger("template").Printf("Template %s was not downloaded successfully", templateToDownload)
		return nil
	}
	GetLogger("template").Printf("Template %s downloaded successfully", templateToDownload)
	return &templateToDownload
}
