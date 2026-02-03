using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class CloudInitWaitAction : BaseAction, IAction
{
    public CloudInitWaitAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "cloud-init wait";
    }

    public bool Execute()
    {
        if (SSHService == null)
        {
            Logger.GetLogger("cloud_init_wait").Printf("SSH service not initialized");
            return false;
        }

        // Check if cloud-init exists
        string existsCmd = CLI.Command.NewCommand().SetCommand("cloud-init").Exists();
        var (_, exitCode) = SSHService.Execute(existsCmd, 10);

        // Skip if cloud-init is not installed
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("cloud_init_wait").Printf("cloud-init not found, skipping");
            return true;
        }

        // Wait for cloud-init to complete
        string waitCmd = CLI.CloudInit.NewCloudInit().LogFile("/tmp/cloud-init-wait.log").Wait(null);
        int timeout = 180;
        var (waitOutput, waitExitCode) = SSHService.Execute(waitCmd, timeout);

        if (!waitExitCode.HasValue || waitExitCode.Value != 0)
        {
            Logger.GetLogger("cloud_init_wait").Printf("cloud-init wait failed with exit code {0}", waitExitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(waitOutput))
            {
                int outputLen = waitOutput.Length;
                int start = outputLen > 500 ? outputLen - 500 : 0;
                Logger.GetLogger("cloud_init_wait").Printf("cloud-init wait output: {0}", waitOutput.Substring(start));
            }
        }

        // Clean cloud-init logs
        string cleanCmd = CLI.CloudInit.NewCloudInit().SuppressOutput(true).Clean(true, false, false).ToCommand();
        int cleanTimeout = 30;
        var (cleanOutput, cleanExitCode) = SSHService.Execute(cleanCmd, cleanTimeout);
        if (!cleanExitCode.HasValue || cleanExitCode.Value != 0)
        {
            Logger.GetLogger("cloud_init_wait").Printf("cloud-init clean failed with exit code {0}", cleanExitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(cleanOutput))
            {
                int outputLen = cleanOutput.Length;
                int start = outputLen > 500 ? outputLen - 500 : 0;
                Logger.GetLogger("cloud_init_wait").Printf("cloud-init clean output: {0}", cleanOutput.Substring(start));
            }
        }

        return true;
    }
}

public static class CloudInitWaitActionFactory
{
    public static IAction NewCloudInitWaitAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new CloudInitWaitAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
