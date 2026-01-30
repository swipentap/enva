using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class InstallDotnetAction : BaseAction, IAction
{
    public InstallDotnetAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "dotnet installation";
    }

    public bool Execute()
    {
        if (SSHService == null)
        {
            Logger.GetLogger("install_dotnet").Printf("SSH service not initialized");
            return false;
        }
        
        // Install prerequisites
        if (APTService == null)
        {
            Logger.GetLogger("install_dotnet").Printf("APT service not initialized");
            return false;
        }
        
        Logger.GetLogger("install_dotnet").Printf("Installing prerequisites...");
        var (prereqOutput, prereqExitCode) = APTService.Install(new[] { "wget", "gpg" });
        if (!prereqExitCode.HasValue || prereqExitCode.Value != 0)
        {
            Logger.GetLogger("install_dotnet").Printf("Failed to install prerequisites: {0}", prereqOutput);
            return false;
        }
        
        // Add Microsoft GPG key and repository
        Logger.GetLogger("install_dotnet").Printf("Adding Microsoft repository...");
        // Detect OS and use appropriate repository
        string detectOsCmd = "if [ -f /etc/os-release ]; then . /etc/os-release && echo $ID; else echo 'debian'; fi";
        (string osId, _) = SSHService.Execute(detectOsCmd, null, true);
        osId = osId.Trim().ToLower();
        
        string repoUrl;
        if (osId == "ubuntu")
        {
            // Detect Ubuntu version
            string detectVersionCmd = "if [ -f /etc/os-release ]; then . /etc/os-release && echo $VERSION_ID; else echo '22.04'; fi";
            (string versionId, _) = SSHService.Execute(detectVersionCmd, null, true);
            versionId = versionId.Trim();
            repoUrl = $"https://packages.microsoft.com/config/ubuntu/{versionId}/packages-microsoft-prod.deb";
        }
        else
        {
            // Default to Debian 12
            repoUrl = "https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb";
        }
        
        string gpgCmd = $"wget {repoUrl} -O /tmp/packages-microsoft-prod.deb && dpkg -i /tmp/packages-microsoft-prod.deb";
        (string gpgOutput, int? gpgExitCode) = SSHService.Execute(gpgCmd, null, true);
        if (!gpgExitCode.HasValue || gpgExitCode.Value != 0)
        {
            Logger.GetLogger("install_dotnet").Printf("Failed to add Microsoft repository: {0}", gpgOutput);
            return false;
        }
        
        // Update apt to include Microsoft repository
        Logger.GetLogger("install_dotnet").Printf("Updating apt repositories...");
        var (updateOutput, updateExitCode) = APTService.Update();
        if (!updateExitCode.HasValue || updateExitCode.Value != 0)
        {
            Logger.GetLogger("install_dotnet").Printf("Failed to update apt: {0}", updateOutput);
            return false;
        }
        
        // Install dotnet packages (correct package names)
        Logger.GetLogger("install_dotnet").Printf("Installing dotnet packages...");
        var (output, exitCode) = APTService.Install(new[] { "dotnet-sdk-8.0" });
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("install_dotnet").Printf("dotnet installation failed: {0}", output);
            return false;
        }
        return true;
    }
}

public static class InstallDotnetActionFactory
{
    public static IAction NewInstallDotnetAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallDotnetAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
