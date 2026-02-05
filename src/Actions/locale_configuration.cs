using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class LocaleConfigurationAction : BaseAction, IAction
{
    public LocaleConfigurationAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "locale configuration";
    }

    public bool Execute()
    {
        if (PCTService == null || ContainerID == null)
        {
            Logger.GetLogger("locale_configuration").Printf("PCTService and ContainerID are required");
            return false;
        }

        if (!int.TryParse(ContainerID, out int containerID))
        {
            Logger.GetLogger("locale_configuration").Printf("Invalid ContainerID: {0}", ContainerID);
            return false;
        }

        var logger = Logger.GetLogger("locale_configuration");
        logger.Printf("Configuring locale settings...");

        // Step 1: Generate the locale
        logger.Printf("Generating locale en_US.UTF-8...");
        string localeGenCmd = "locale-gen en_US.UTF-8";
        var (localeGenOutput, localeGenExit) = PCTService.Execute(containerID, localeGenCmd, null);
        if (!localeGenExit.HasValue || localeGenExit.Value != 0)
        {
            logger.Printf("Failed to generate locale: {0}", localeGenOutput);
            return false;
        }

        string updateLangCmd = "update-locale LANG=en_US.UTF-8";
        var (updateLangOutput, updateLangExit) = PCTService.Execute(containerID, updateLangCmd, null);
        if (!updateLangExit.HasValue || updateLangExit.Value != 0)
        {
            logger.Printf("Failed to update LANG: {0}", updateLangOutput);
            return false;
        }

        // Step 2: Fix LC_CTYPE (this is important)
        logger.Printf("Setting LC_CTYPE=en_US.UTF-8...");
        string updateCtypeCmd = "update-locale LC_CTYPE=en_US.UTF-8";
        var (updateCtypeOutput, updateCtypeExit) = PCTService.Execute(containerID, updateCtypeCmd, null);
        if (!updateCtypeExit.HasValue || updateCtypeExit.Value != 0)
        {
            logger.Printf("Failed to update LC_CTYPE: {0}", updateCtypeOutput);
            return false;
        }

        // Step 3: Verify
        logger.Printf("Verifying locale configuration...");
        string verifyCmd = "locale";
        var (verifyOutput, verifyExit) = PCTService.Execute(containerID, verifyCmd, null);
        if (!verifyExit.HasValue || verifyExit.Value != 0)
        {
            logger.Printf("Failed to verify locale: {0}", verifyOutput);
            return false;
        }

        // Check that both LANG and LC_CTYPE are set correctly
        // LC_CTYPE may be quoted in the output, so check for the locale value in the line
        bool langOk = verifyOutput.Contains("LANG=en_US.UTF-8");
        bool lcCtypeOk = verifyOutput.Contains("LC_CTYPE") && verifyOutput.Contains("en_US.UTF-8");
        if (!langOk || !lcCtypeOk)
        {
            logger.Printf("Locale verification failed. Output: {0}", verifyOutput);
            return false;
        }

        logger.Printf("Locale configuration completed successfully");
        return true;
    }
}

public static class LocaleConfigurationActionFactory
{
    public static IAction NewLocaleConfigurationAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new LocaleConfigurationAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
