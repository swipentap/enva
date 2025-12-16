package services

import (
	"enva/libs"
	"fmt"
	"strings"
)

// TemplateService manages template operations on LXC host
type TemplateService struct {
	lxc libs.LXCServiceInterface
}

// NewTemplateService creates a new Template service
func NewTemplateService(lxc libs.LXCServiceInterface) *TemplateService {
	return &TemplateService{lxc: lxc}
}

// GetBaseTemplate gets base Ubuntu template, download if needed
func (t *TemplateService) GetBaseTemplate(cfg *libs.LabConfig) *string {
	templates := cfg.TemplateConfig.Base
	templateDir := cfg.LXCTemplateDir()

	for _, template := range templates {
		checkCmd := fmt.Sprintf("test -f %s/%s && echo exists || echo missing", templateDir, template)
		checkResult, _ := t.lxc.Execute(checkCmd, nil)
		if strings.Contains(checkResult, "exists") {
			return &template
		}
	}

	// Download last template in list
	if len(templates) == 0 {
		return nil
	}
	templateToDownload := templates[len(templates)-1]
	libs.GetLogger("template").Printf("Base template not found. Downloading %s...", templateToDownload)

	// Run pveam download
	downloadCmd := fmt.Sprintf("pveam download local %s", templateToDownload)
	libs.GetLogger("template").Debug("Running: %s", downloadCmd)
	timeout := 300
	output, exitCode := t.lxc.Execute(downloadCmd, &timeout)

	// Verify download completed
	verifyCmd := fmt.Sprintf("test -f %s/%s && echo exists || echo missing", templateDir, templateToDownload)
	verifyResult, _ := t.lxc.Execute(verifyCmd, nil)
	if !strings.Contains(verifyResult, "exists") {
		errorMsg := fmt.Sprintf("Template %s download failed", templateToDownload)
		if exitCode != nil && *exitCode != 0 {
			errorMsg += fmt.Sprintf(" (exit code: %d)", *exitCode)
		}
		if output != "" {
			errorMsg += fmt.Sprintf(". Output: %s", output)
		}
		libs.GetLogger("template").Printf(errorMsg)
		return nil
	}
	libs.GetLogger("template").Printf("Template %s downloaded successfully", templateToDownload)
	return &templateToDownload
}

// GetTemplatePath gets template path for given template name
func (t *TemplateService) GetTemplatePath(templateName *string, cfg *libs.LabConfig) string {
	templateDir := cfg.LXCTemplateDir()
	if templateName == nil {
		// Use base template
		baseTemplate := t.GetBaseTemplate(cfg)
		if baseTemplate == nil {
			return ""
		}
		return fmt.Sprintf("%s/%s", templateDir, *baseTemplate)
	}
	// Check if it's a template file name or a template name
	if strings.Contains(*templateName, ".tar") {
		// It's a template file
		return fmt.Sprintf("%s/%s", templateDir, *templateName)
	}
	// It's a template name, find the file matching pattern (matching Python logic)
	// Find template config
	var templateCfg *libs.TemplateConfig
	for _, tmpl := range cfg.Templates {
		if tmpl.Name == *templateName {
			templateCfg = &tmpl
			break
		}
	}
	if templateCfg == nil {
		// Fallback to base template
		baseTemplate := t.GetBaseTemplate(cfg)
		if baseTemplate == nil {
			return ""
		}
		return fmt.Sprintf("%s/%s", templateDir, *baseTemplate)
	}
	// Find template file by name - search for files matching template name pattern (matching Python: ls -t {template_name}*.tar.zst)
	templateNamePattern := fmt.Sprintf("%s*.tar.zst", templateCfg.Name)
	findCmd := fmt.Sprintf("ls -t %s/%s 2>/dev/null | head -1 | xargs basename 2>/dev/null", templateDir, templateNamePattern)
	templateFile, _ := t.lxc.Execute(findCmd, nil)
	if templateFile != "" && strings.TrimSpace(templateFile) != "" {
		return fmt.Sprintf("%s/%s", templateDir, strings.TrimSpace(templateFile))
	}
	// Fallback to base template
	baseTemplate := t.GetBaseTemplate(cfg)
	if baseTemplate == nil {
		return ""
	}
	return fmt.Sprintf("%s/%s", templateDir, *baseTemplate)
}

// ValidateTemplate validates template file exists and is readable
func (t *TemplateService) ValidateTemplate(templatePath string) bool {
	checkCmd := fmt.Sprintf("test -f %s && test -r %s && echo 'valid' || echo 'invalid'", templatePath, templatePath)
	result, _ := t.lxc.Execute(checkCmd, nil)
	return strings.Contains(result, "valid")
}
