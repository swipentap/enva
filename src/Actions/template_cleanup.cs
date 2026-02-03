using System;
using System.Linq;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class TemplateCleanupAction : BaseAction, IAction
{
    public TemplateCleanupAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "template cleanup";
    }

    public bool Execute()
    {
        if (PCTService == null || Cfg == null || string.IsNullOrEmpty(ContainerID))
        {
            Logger.GetLogger("template_cleanup").Printf("PCT service, config, or container ID not available");
            return false;
        }
        if (!int.TryParse(ContainerID, out int containerIDInt))
        {
            Logger.GetLogger("template_cleanup").Printf("Invalid container ID: {0}", ContainerID);
            return false;
        }
        string defaultUser = Cfg.Users.DefaultUser();
        var cleanupCommands = new[]
        {
            new { desc = "Remove apt proxy configuration", cmd = CLI.Files.NewFileOps().Force(true).Remove("/etc/apt/apt.conf.d/01proxy") },
            new { desc = "Remove SSH host keys", cmd = CLI.Files.NewFileOps().Force(true).AllowGlob().Remove("/etc/ssh/ssh_host_*") },
            new { desc = "Truncate machine-id", cmd = CLI.Files.NewFileOps().Truncate("/etc/machine-id") },
            new { desc = "Remove DBus machine-id", cmd = CLI.Files.NewFileOps().Force(true).Remove("/var/lib/dbus/machine-id") },
            new { desc = "Recreate DBus machine-id symlink", cmd = CLI.Files.NewFileOps().Symlink("/etc/machine-id", "/var/lib/dbus/machine-id") },
            new { desc = "Remove apt lists", cmd = CLI.Files.NewFileOps().Force(true).Recursive().AllowGlob().Remove("/var/lib/apt/lists/*") },
            new { desc = "Remove log files", cmd = CLI.Files.NewFileOps().SuppressErrors().FindDelete("/var/log", "*.log", "f") },
            new { desc = "Remove compressed logs", cmd = CLI.Files.NewFileOps().SuppressErrors().FindDelete("/var/log", "*.gz", "f") },
            new { desc = "Clear root history", cmd = CLI.Files.NewFileOps().SuppressErrors().Truncate("/root/.bash_history") },
            new { desc = $"Clear {defaultUser} history", cmd = CLI.Files.NewFileOps().SuppressErrors().Truncate($"/home/{defaultUser}/.bash_history") },
        };
        foreach (var item in cleanupCommands)
        {
            var (cmdOutput, cmdExitCode) = PCTService.Execute(containerIDInt, item.cmd, null);
            if (cmdExitCode.HasValue && cmdExitCode.Value != 0)
            {
                Logger.GetLogger("template_cleanup").Printf("Failed to {0}: {1}", item.desc, cmdOutput);
            }
        }
        string cleanCmd = "apt-get clean";
        var (cleanOutput, cleanExitCode) = PCTService.Execute(containerIDInt, cleanCmd, null);
        if (cleanExitCode.HasValue && cleanExitCode.Value != 0)
        {
            Logger.GetLogger("template_cleanup").Printf("Failed to clean apt cache: {0}", cleanOutput);
        }
        return true;
    }
}

public static class TemplateCleanupActionFactory
{
    public static IAction NewTemplateCleanupAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new TemplateCleanupAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
