using System;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class CreateTemplateArchiveAction : BaseAction, IAction
{
    public CreateTemplateArchiveAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        SSHService = sshService;
        APTService = aptService;
        PCTService = pctService;
        ContainerID = containerID;
        Cfg = cfg;
        ContainerCfg = containerCfg;
    }

    public override string Description()
    {
        return "template archive creation";
    }

    public bool Execute()
    {
        if (Cfg == null || ContainerID == null)
        {
            Logger.GetLogger("create_template_archive").Printf("Config or container ID missing");
            return false;
        }

        string lxcHost = Cfg.LXCHost();
        string containerID = ContainerID;
        string templateDir = Cfg.LXC.TemplateDir;

        var lxcService = new LXCService(lxcHost, Cfg.SSH);
        if (!lxcService.Connect())
        {
            Logger.GetLogger("create_template_archive").Printf("Failed to connect to LXC host");
            return false;
        }

        try
        {
            // Stop container
            Logger.GetLogger("create_template_archive").Printf("Stopping container...");
            string stopCmd = CLI.PCT.NewPCT().ContainerID(containerID).Stop();
            (string stopOutput, int? stopExitCode) = lxcService.Execute(stopCmd, null);
            if (!stopExitCode.HasValue || stopExitCode.Value != 0)
            {
                Logger.GetLogger("create_template_archive").Printf("Stop container failed, trying force stop");
                string forceStopCmd = CLI.PCT.NewPCT().ContainerID(containerID).Force().Stop();
                (string forceStopOutput, int? forceStopExitCode) = lxcService.Execute(forceStopCmd, null);
                if (!forceStopExitCode.HasValue || forceStopExitCode.Value != 0)
                {
                    Logger.GetLogger("create_template_archive").Printf("Force stop also failed: {0}", forceStopOutput);
                    return false;
                }
            }
            Thread.Sleep(2000);

            // Create template archive
            Logger.GetLogger("create_template_archive").Printf("Creating template archive for container {0} in directory {1}", containerID, templateDir);
            string vzdumpCmd = CLI.Vzdump.NewVzdump().Compress("zstd").Mode("stop").CreateTemplate(containerID, templateDir);
            Logger.GetLogger("create_template_archive").Printf("Executing vzdump command: {0}", vzdumpCmd);
            (string vzdumpOutput, _) = lxcService.Execute(vzdumpCmd, null);
            if (string.IsNullOrEmpty(vzdumpOutput))
            {
                Logger.GetLogger("create_template_archive").Printf("vzdump produced no output - command may have failed silently");
                return false;
            }
            if (vzdumpOutput.Length > 500)
            {
                Logger.GetLogger("create_template_archive").Printf("vzdump output (first 500 chars): {0}", vzdumpOutput.Substring(0, 500));
            }

            // Find archive file
            string findArchiveCmd = CLI.Vzdump.NewVzdump().FindArchive(templateDir, containerID);
            (string backupFile, _) = lxcService.Execute(findArchiveCmd, null);
            if (string.IsNullOrEmpty(backupFile))
            {
                Logger.GetLogger("create_template_archive").Printf("Template archive file not found after vzdump in directory {0}", templateDir);
                string checkCmd = $"ls -la {templateDir}/*vzdump* | head -10";
                (string checkOutput, _) = lxcService.Execute(checkCmd, null);
                Logger.GetLogger("create_template_archive").Printf("Files in template directory: {0}", checkOutput);
                return false;
            }
            backupFile = backupFile.Trim();
            Logger.GetLogger("create_template_archive").Printf("Template archive file found: {0}", backupFile);

            // Verify archive is not empty
            string sizeCmd = CLI.Vzdump.NewVzdump().GetArchiveSize(backupFile);
            (string sizeCheck, _) = lxcService.Execute(sizeCmd, null);
            if (string.IsNullOrEmpty(sizeCheck))
            {
                Logger.GetLogger("create_template_archive").Printf("Failed to get archive file size");
                return false;
            }
            int? fileSize = CLI.Vzdump.ParseArchiveSize(sizeCheck);
            if (!fileSize.HasValue || fileSize.Value < 10485760)
            {
                Logger.GetLogger("create_template_archive").Printf("Template archive is too small ({0} bytes if found), likely corrupted", fileSize?.ToString() ?? "null");
                return false;
            }
            Logger.GetLogger("create_template_archive").Printf("Template archive size: {0:F2} MB", (double)fileSize.Value / 1048576);

            // Rename template and move to storage location
            string templateName = ContainerCfg?.Name ?? "template";
            if (string.IsNullOrEmpty(templateName))
            {
                templateName = "template";
            }
            string dateStr = DateTime.Now.ToString("yyyyMMdd");
            string finalTemplateName = $"{templateName}_{dateStr}_amd64.tar.zst";
            Logger.GetLogger("create_template_archive").Printf("Final template name: {0}", finalTemplateName);

            string storageTemplateDir = Cfg.LXC.TemplateDir;
            string storageTemplatePath = $"{storageTemplateDir}/{finalTemplateName}";
            Logger.GetLogger("create_template_archive").Printf("Moving template from {0} to {1}", backupFile, storageTemplatePath);
            string moveCmd = $"mv '{backupFile}' {storageTemplatePath}";
            (string moveOutput, _) = lxcService.Execute(moveCmd, null);
            if (!string.IsNullOrEmpty(moveOutput))
            {
                Logger.GetLogger("create_template_archive").Printf("Move command output: {0}", moveOutput);
            }
            Logger.GetLogger("create_template_archive").Printf("Template moved to storage location: {0}", storageTemplatePath);

            // Update template list
            string pveamCmd = "pveam update";
            (string pveamOutput, _) = lxcService.Execute(pveamCmd, null);
            if (string.IsNullOrEmpty(pveamOutput))
            {
                Logger.GetLogger("create_template_archive").Printf("pveam update had issues");
            }

            // Cleanup other templates
            Logger.GetLogger("create_template_archive").Printf("Cleaning up other template archives...");
            string preservePatterns = string.Join(" ", Cfg.TemplateConfig.Preserve.Select(p => $"-not -name '{p}'"));
            string cleanupCacheCmd = $"find {templateDir} -maxdepth 1 -type f -name '*.tar.zst' ! -name '{finalTemplateName}' {preservePatterns} -delete";
            lxcService.Execute(cleanupCacheCmd, null);
            string cleanupStorageCmd = $"find {storageTemplateDir} -maxdepth 1 -type f -name '*.tar.zst' ! -name '{finalTemplateName}' {preservePatterns} ! -name 'ubuntu-24.10-standard_24.10-1_amd64.tar.zst' -delete";
            lxcService.Execute(cleanupStorageCmd, null);

            // Destroy container after archive is created
            if (int.TryParse(containerID, out int containerIDInt))
            {
                Common.DestroyContainer(lxcHost, containerIDInt, Cfg, lxcService);
                Logger.GetLogger("create_template_archive").Printf("Container {0} destroyed after template archive creation", containerID);
            }
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }

}

public static class CreateTemplateArchiveActionFactory
{
    public static IAction NewCreateTemplateArchiveAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new CreateTemplateArchiveAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}