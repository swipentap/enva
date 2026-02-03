using System;
using System.Linq;
using Enva.Libs;

namespace Enva.Services;

public class TemplateService
{
    private ILXCService lxc;

    public TemplateService(ILXCService lxc)
    {
        this.lxc = lxc;
    }

    public string? GetBaseTemplate(LabConfig cfg)
    {
        var templates = cfg.TemplateConfig.Base;
        string templateDir = cfg.LXCTemplateDir();

        foreach (string template in templates)
        {
            string checkCmd = $"test -f {templateDir}/{template} && echo exists || echo missing";
            var (checkResult, _) = lxc.Execute(checkCmd, null);
            if (checkResult.Contains("exists"))
            {
                return template;
            }
        }

        // Download last template in list
        if (templates.Count == 0)
        {
            return null;
        }
        string templateToDownload = templates.Last();
        Logger.GetLogger("template").Printf("Base template not found. Downloading {0}...", templateToDownload);

        // Update Proxmox repository cache before downloading
        Logger.GetLogger("template").Printf("Updating Proxmox repository cache...");
        string updateCmd = "pveam update";
        int updateTimeout = 60;
        var (updateOutput, updateExitCode) = lxc.Execute(updateCmd, updateTimeout);
        if (updateExitCode.HasValue && updateExitCode.Value != 0)
        {
            Logger.GetLogger("template").Printf("Warning: pveam update failed (exit code: {0}): {1}", updateExitCode.Value, updateOutput);
        }
        else
        {
            Logger.GetLogger("template").Printf("Repository cache updated successfully");
        }

        // Run pveam download
        string downloadCmd = $"pveam download local {templateToDownload}";
        Logger.GetLogger("template").Debug("Running: {0}", downloadCmd);
        int timeout = cfg.Timeouts.UbuntuTemplate > 0 ? cfg.Timeouts.UbuntuTemplate : 1800; // Default 30 minutes if not configured
        var (output, exitCode) = lxc.Execute(downloadCmd, timeout);

        // Verify download completed
        string verifyCmd = $"test -f {templateDir}/{templateToDownload} && echo exists || echo missing";
        var (verifyResult, _) = lxc.Execute(verifyCmd, null);
        if (!verifyResult.Contains("exists"))
        {
            string errorMsg = $"Template {templateToDownload} download failed";
            if (!exitCode.HasValue)
            {
                errorMsg += $" (command timed out after {timeout} seconds)";
            }
            else if (exitCode.Value != 0)
            {
                errorMsg += $" (exit code: {exitCode.Value})";
            }
            if (!string.IsNullOrEmpty(output))
            {
                errorMsg += $". Output: {output}";
            }
            Logger.GetLogger("template").Printf(errorMsg);
            return null;
        }
        Logger.GetLogger("template").Printf("Template {0} downloaded successfully", templateToDownload);
        return templateToDownload;
    }

    public string GetTemplatePath(string? templateName, LabConfig cfg)
    {
        string templateDir = cfg.LXCTemplateDir();
        if (templateName == null)
        {
            // Use base template
            string? baseTemplate = GetBaseTemplate(cfg);
            if (baseTemplate == null)
            {
                return "";
            }
            return $"{templateDir}/{baseTemplate}";
        }
        // Check if it's a template file name or a template name
        if (templateName.Contains(".tar"))
        {
            // It's a template file
            return $"{templateDir}/{templateName}";
        }
        // It's a template name, find the file matching pattern (matching Python logic)
        // Find template config
        TemplateConfig? templateCfg = null;
        foreach (var tmpl in cfg.Templates)
        {
            if (tmpl.Name == templateName)
            {
                templateCfg = tmpl;
                break;
            }
        }
        if (templateCfg == null)
        {
            // Fallback to base template
            string? baseTemplate = GetBaseTemplate(cfg);
            if (baseTemplate == null)
            {
                return "";
            }
            return $"{templateDir}/{baseTemplate}";
        }
        // Find template file by name - search for files matching template name pattern
        string templateNamePattern = $"{templateCfg.Name}*.tar.zst";
        string findCmd = $"ls -t {templateDir}/{templateNamePattern} | head -1 | xargs basename";
        var (templateFile, _) = lxc.Execute(findCmd, null);
        if (!string.IsNullOrEmpty(templateFile) && !string.IsNullOrWhiteSpace(templateFile))
        {
            return $"{templateDir}/{templateFile.Trim()}";
        }
        // Fallback to base template
        string? baseTemplate2 = GetBaseTemplate(cfg);
        if (baseTemplate2 == null)
        {
            return "";
        }
        return $"{templateDir}/{baseTemplate2}";
    }

    public bool ValidateTemplate(string templatePath)
    {
        if (string.IsNullOrEmpty(templatePath))
        {
            return false;
        }
        string checkCmd = $"test -f {templatePath} && test -r {templatePath} && echo 'valid' || echo 'invalid'";
        var (result, _) = lxc.Execute(checkCmd, null);
        return result.Contains("valid");
    }
}
