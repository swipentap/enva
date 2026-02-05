using System;
using System.Collections.Generic;
using System.Linq;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Commands;

public class CleanupError : Exception
{
    public CleanupError(string message) : base(message) { }
}

public class CleanupCommand
{
    private LabConfig? cfg;
    private ILXCService? lxcService;
    private PCTService? pctService;

    public CleanupCommand(LabConfig? cfg, ILXCService? lxcService, PCTService? pctService)
    {
        this.cfg = cfg;
        this.lxcService = lxcService;
        this.pctService = pctService;
    }

    /// <summary>List containers and templates that would be destroyed, without destroying anything.</summary>
    public void RunPlanOnly()
    {
        var logger = Logger.GetLogger("cleanup");
        if (lxcService == null || cfg == null)
        {
            logger.Printf("Cleanup plan: config or LXC service not initialized");
            return;
        }
        if (!lxcService.Connect())
        {
            logger.Printf("Cleanup plan: failed to connect to LXC host {0}", cfg.LXCHost());
            return;
        }
        try
        {
            logger.Printf("=== Cleanup (plan only): containers that would be destroyed ===");
            List<string> containerIDs = ListContainerIDs();
            if (containerIDs.Count == 0)
            {
                logger.Printf("  No containers found");
            }
            else
            {
                logger.Printf("  {0} container(s): {1}", containerIDs.Count, string.Join(", ", containerIDs));
            }
            logger.Printf("  Templates: all *.tar.zst in {0} would be removed", cfg.LXCTemplateDir());
        }
        finally
        {
            lxcService.Disconnect();
        }
    }

    public void Run()
    {
        var logger = Logger.GetLogger("cleanup");
        try
        {
            logger.Printf("=== Cleaning Up Lab Environment ===");
            logger.Printf("Destroying ALL containers and templates...");
            if (lxcService == null || cfg == null)
            {
                throw new CleanupError("LXC service or config not initialized");
            }
            if (!lxcService.Connect())
            {
                throw new CleanupError($"Failed to connect to LXC host {cfg.LXCHost()}");
            }

            try
            {
                DestroyContainers();
                RemoveTemplates();
            }
            finally
            {
                lxcService.Disconnect();
            }
        }
        catch (Exception ex)
        {
            var logger2 = Logger.GetLogger("cleanup");
            logger2.Printf("Error during cleanup: {0}", ex.Message);
            if (ex is CleanupError)
            {
                throw;
            }
            throw new CleanupError($"Error during cleanup: {ex.Message}");
        }
    }

    private void DestroyContainers()
    {
        var logger = Logger.GetLogger("cleanup");
        logger.Printf("Stopping and destroying containers...");
        List<string> containerIDs = ListContainerIDs();
        int total = containerIDs.Count;
        if (total == 0)
        {
            logger.Printf("No containers found");
            return;
        }
        logger.Printf("Found {0} containers to destroy: {1}", total, string.Join(", ", containerIDs));
        for (int idx = 0; idx < containerIDs.Count; idx++)
        {
            string cidStr = containerIDs[idx];
            logger.Printf("[{0}/{1}] Processing container {2}...", idx + 1, total, cidStr);
            if (int.TryParse(cidStr, out int cid))
            {
                Libs.Common.DestroyContainer(cfg!.LXCHost(), cid, cfg, lxcService!);
            }
            else
            {
                logger.Printf("Invalid container ID: {0}", cidStr);
            }
        }
        VerifyContainersRemoved();
    }

    private List<string> ListContainerIDs()
    {
        if (lxcService == null)
        {
            return new List<string>();
        }
        string listCmd = CLI.PCT.NewPCT().List();
        (string result, _) = lxcService.Execute(listCmd, null);
        List<string> containerIDs = new List<string>();
        if (!string.IsNullOrEmpty(result))
        {
            string[] lines = result.Trim().Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && IsNumeric(parts[0]))
                {
                    containerIDs.Add(parts[0]);
                }
            }
        }
        return containerIDs;
    }

    private void VerifyContainersRemoved()
    {
        var logger = Logger.GetLogger("cleanup");
        logger.Printf("Verifying all containers are destroyed...");
        if (lxcService == null)
        {
            return;
        }
        (string remainingResult, _) = lxcService.Execute(CLI.PCT.NewPCT().List(), null);
        List<string> remainingIDs = new List<string>();
        if (!string.IsNullOrEmpty(remainingResult))
        {
            string[] remainingLines = remainingResult.Trim().Split('\n');
            for (int i = 1; i < remainingLines.Length; i++)
            {
                string line = remainingLines[i];
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && IsNumeric(parts[0]))
                {
                    remainingIDs.Add(parts[0]);
                }
            }
        }
        if (remainingIDs.Count > 0)
        {
            throw new CleanupError($"{remainingIDs.Count} containers still exist: {string.Join(", ", remainingIDs)}");
        }
        logger.Printf("All containers destroyed");
    }

    private void RemoveTemplates()
    {
        var logger = Logger.GetLogger("cleanup");
        logger.Printf("Removing templates...");
        if (cfg == null || lxcService == null)
        {
            return;
        }
        string templateDir = cfg.LXCTemplateDir();
        logger.Printf("Cleaning template directory {0}...", templateDir);
        string countCmd = CLI.Find.NewFind().Directory(templateDir).Maxdepth(1).Type("f").Name("*.tar.zst").Count();
        (string countResult, _) = lxcService.Execute(countCmd, null);
        string templateCount = "0";
        if (!string.IsNullOrEmpty(countResult))
        {
            templateCount = countResult.Trim();
        }
        logger.Printf("Removing {0} template files...", templateCount);
        string deleteCmd = CLI.Find.NewFind().Directory(templateDir).Maxdepth(1).Type("f").Name("*.tar.zst").Delete();
        lxcService.Execute(deleteCmd, null);
        logger.Printf("Templates removed");
    }

    private static bool IsNumeric(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }
        foreach (char r in s)
        {
            if (r < '0' || r > '9')
            {
                return false;
            }
        }
        return true;
    }
}