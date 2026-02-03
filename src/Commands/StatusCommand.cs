using System;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Commands;

public class StatusCommand
{
    private LabConfig? cfg;
    private ILXCService? lxcService;
    private PCTService? pctService;

    public StatusCommand(LabConfig? cfg, ILXCService? lxcService, PCTService? pctService)
    {
        this.cfg = cfg;
        this.lxcService = lxcService;
        this.pctService = pctService;
    }

    public void Run()
    {
        var logger = Logger.GetLogger("status");
        if (lxcService == null || cfg == null)
        {
            logger.Printf("LXC service or config not initialized");
            return;
        }
        if (!lxcService.Connect())
        {
            logger.Printf("Failed to connect to LXC host {0}", cfg.LXCHost());
            return;
        }

        try
        {
            logger.Printf("=== Lab Status ===");
            logger.Printf("Containers:");
            string listCmd = CLI.PCT.NewPCT().List();
            (string result, _) = lxcService.Execute(listCmd, null);
            if (!string.IsNullOrEmpty(result))
            {
                logger.Printf(result);
            }
            else
            {
                logger.Printf("  No containers found");
            }
            string templateDir = cfg.LXCTemplateDir();
            logger.Printf("Templates:");
            string templateCmd = $"ls -lh {templateDir}/*.tar.zst || echo 'No templates'";
            (result, _) = lxcService.Execute(templateCmd, null);
            if (!string.IsNullOrEmpty(result))
            {
                logger.Printf(result);
            }
            else
            {
                logger.Printf("  No templates found");
            }
        }
        finally
        {
            lxcService.Disconnect();
        }
    }
}