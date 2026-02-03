using System.Linq;

namespace Enva.Libs;

public static class Template
{
    public static string? GetBaseTemplate(string lxcHost, LabConfig cfg, ILXCService? lxcService)
    {
        if (lxcService == null)
        {
            Logger.GetLogger("template").Printf("lxcService required for GetBaseTemplate");
            return null;
        }
        
        var templates = cfg.TemplateConfig.Base;
        var templateDir = cfg.LXCTemplateDir();

        foreach (var template in templates)
        {
            var checkCmd = $"test -f {templateDir}/{template} && echo exists || echo missing";
            var (checkResult, _) = lxcService.Execute(checkCmd, null);
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
        var templateToDownload = templates.Last();
        Logger.GetLogger("template").Printf("Base template not found. Downloading {0}...", templateToDownload);

        // Update Proxmox repository cache before downloading
        Logger.GetLogger("template").Printf("Updating Proxmox repository cache...");
        var updateCmd = "pveam update";
        var updateTimeout = 60;
        var (updateOutput, updateExitCode) = lxcService.Execute(updateCmd, updateTimeout);
        if (updateExitCode != null && updateExitCode.Value != 0)
        {
            Logger.GetLogger("template").Printf("Warning: pveam update failed (exit code: {0}): {1}", updateExitCode.Value, updateOutput);
        }
        else
        {
            Logger.GetLogger("template").Printf("Repository cache updated successfully");
        }

        // Run pveam download with live output
        var downloadCmd = $"pveam download local {templateToDownload}";
        Logger.GetLogger("template").Debug("Running: {0}", downloadCmd);

        var timeout = 300;
        lxcService.Execute(downloadCmd, timeout);

        // Verify download completed
        var verifyCmd = $"test -f {templateDir}/{templateToDownload} && echo exists || echo missing";
        var (verifyResult, _) = lxcService.Execute(verifyCmd, null);

        if (!verifyResult.Contains("exists"))
        {
            Logger.GetLogger("template").Printf("Template {0} was not downloaded successfully", templateToDownload);
            return null;
        }
        Logger.GetLogger("template").Printf("Template {0} downloaded successfully", templateToDownload);
        return templateToDownload;
    }
}
