using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class CreateContainerAction : BaseAction, IAction
{
    public DeployPlan? Plan { get; set; }

    public CreateContainerAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg, DeployPlan? plan)
    {
        SSHService = sshService;
        APTService = aptService;
        PCTService = pctService;
        ContainerID = containerID;
        Cfg = cfg;
        ContainerCfg = containerCfg;
        Plan = plan;
    }

    public override string Description()
    {
        return "create container";
    }

    public bool Execute()
    {
        if (ContainerCfg == null || Cfg == null)
        {
            Logger.GetLogger("create_container").Printf("Container config or lab config is missing");
            return false;
        }

        string lxcHost = Cfg.LXCHost();
        int containerIDInt = ContainerCfg.ID;
        string ipAddress = ContainerCfg.IPAddress ?? "";
        string hostname = ContainerCfg.Hostname;
        string gateway = Cfg.GetGateway();
        string? templateName = ContainerCfg.Template;
        string? templateNameStr = null;
        if (templateName != null && templateName != "base" && templateName != "")
        {
            templateNameStr = templateName;
        }

        var lxcService = new LXCService(lxcHost, Cfg.SSH);
        if (!lxcService.Connect())
        {
            Logger.GetLogger("create_container").Printf("Failed to connect to LXC host {0}", lxcHost);
            return false;
        }

        try
        {
            var pctService = new PCTService(lxcService);
            var templateService = new TemplateService(lxcService);
            containerIDInt = ContainerCfg.ID;
            pctService.Destroy(containerIDInt, true);
            string templatePath = templateService.GetTemplatePath(templateNameStr, Cfg);
            if (string.IsNullOrEmpty(templatePath))
            {
                Logger.GetLogger("create_container").Printf("Template path is empty - template download may have failed");
                return false;
            }
            if (!templateService.ValidateTemplate(templatePath))
            {
                Logger.GetLogger("create_container").Printf("Template file {0} is missing or not readable", templatePath);
                return false;
            }

            bool shouldBePrivileged = ContainerCfg.Privileged ?? false;
            bool shouldBeNested = ContainerCfg.Nested ?? true;

            Logger.GetLogger("create_container").Printf("Checking if container {0} already exists...", containerIDInt);
            bool containerAlreadyExists = Common.ContainerExists(lxcHost, containerIDInt, Cfg, lxcService);
            if (containerAlreadyExists && Plan != null && Plan.StartStep > 1)
            {
                if (shouldBePrivileged)
                {
                    string configCmd = $"pct config {containerIDInt} | grep -E '^unprivileged:' || echo 'unprivileged: 1'";
                    (string configOutput, _) = lxcService.Execute(configCmd, null);
                    bool isUnprivileged = configOutput.Contains("unprivileged: 1");
                    if (isUnprivileged)
                    {
                        Logger.GetLogger("create_container").Printf("Container {0} exists but is unprivileged - config requires privileged. Destroying and recreating...", containerIDInt);
                        Common.DestroyContainer(lxcHost, containerIDInt, Cfg, lxcService);
                        containerAlreadyExists = false;
                    }
                    else
                    {
                        Logger.GetLogger("create_container").Printf("Container {0} already exists and privilege status matches config, skipping creation", containerIDInt);
                        (string statusOutput, _) = pctService.Status(containerIDInt);
                        if (!statusOutput.Contains("running"))
                        {
                            Logger.GetLogger("create_container").Printf("Starting existing container {0}...", containerIDInt);
                            pctService.Start(containerIDInt);
                            Thread.Sleep(3000);
                        }
                        return true;
                    }
                }
                else
                {
                    Logger.GetLogger("create_container").Printf("Container {0} already exists and start_step is {1}, skipping creation", containerIDInt, Plan.StartStep);
                    (string statusOutput, _) = pctService.Status(containerIDInt);
                    if (!statusOutput.Contains("running"))
                    {
                        Logger.GetLogger("create_container").Printf("Starting existing container {0}...", containerIDInt);
                        pctService.Start(containerIDInt);
                        Thread.Sleep(3000);
                    }
                    return true;
                }
            }

            if (containerAlreadyExists)
            {
                Logger.GetLogger("create_container").Printf("Container {0} already exists, destroying it first...", containerIDInt);
                Common.DestroyContainer(lxcHost, containerIDInt, Cfg, lxcService);
            }

            ContainerResources? resources = ContainerCfg.Resources;
            if (resources == null)
            {
                resources = new ContainerResources
                {
                    Memory = 2048,
                    Swap = 2048,
                    Cores = 4,
                    RootfsSize = 20
                };
            }

            bool unprivileged = !shouldBePrivileged;
            Logger.GetLogger("create_container").Printf("Creating container {0} from template...", containerIDInt);
            (string output, int? exitCode) = pctService.Create(containerIDInt, templatePath, hostname, resources.Memory, resources.Swap, resources.Cores, ipAddress, gateway, Cfg.LXC.Bridge, Cfg.LXC.Storage, resources.RootfsSize, unprivileged, "ubuntu", "amd64");
            if (exitCode.HasValue && exitCode.Value != 0)
            {
                Logger.GetLogger("create_container").Printf("Failed to create container {0}: {1}", containerIDInt, output);
                return false;
            }

            Logger.GetLogger("create_container").Printf("Setting container features...");
            (output, exitCode) = pctService.SetFeatures(containerIDInt, shouldBeNested, true, true);
            if (exitCode.HasValue && exitCode.Value != 0)
            {
                Logger.GetLogger("create_container").Printf("Failed to set container features: {0}", output);
            }

            bool autostart = ContainerCfg.Autostart ?? true;
            Logger.GetLogger("create_container").Printf("Setting autostart for container {0} (onboot={1})...", containerIDInt, autostart ? "1" : "0");
            (output, exitCode) = pctService.SetOnboot(containerIDInt, autostart);
            if (exitCode.HasValue && exitCode.Value != 0)
            {
                Logger.GetLogger("create_container").Printf("Failed to set autostart (onboot) for container {0}: {1}", containerIDInt, output);
                return false;
            }

            Logger.GetLogger("create_container").Printf("Starting container {0}...", containerIDInt);
            (output, exitCode) = pctService.Start(containerIDInt);
            if (exitCode.HasValue && exitCode.Value != 0)
            {
                Logger.GetLogger("create_container").Printf("Failed to start container {0}: {1}", containerIDInt, output);
                return false;
            }

            Logger.GetLogger("create_container").Printf("Bringing up network interface...");
            string pingCmd = "ping -c 1 8.8.8.8";
            int timeout = 10;
            (output, exitCode) = pctService.Execute(ContainerCfg.ID, pingCmd, timeout);
            if (exitCode.HasValue && exitCode.Value != 0)
            {
                Logger.GetLogger("create_container").Printf("Ping to 8.8.8.8 failed (network may still be initializing): {0}", output);
            }
            else
            {
                Logger.GetLogger("create_container").Printf("Network interface is up and reachable");
            }

            foreach (var userCfg in Cfg.Users.Users)
            {
                string username = userCfg.Name;
                string sudoGroup = userCfg.SudoGroup;
                string checkCmd = CLI.Users.NewUser().Username(username).CheckExists();
                string addCmd = CLI.Users.NewUser().Username(username).Shell("/bin/bash").Groups(new List<string> { sudoGroup }).CreateHome(true).Add();
                string userCheckCmd = $"{checkCmd} || {addCmd}";
                (output, exitCode) = pctService.Execute(ContainerCfg.ID, userCheckCmd, null);
                if (exitCode.HasValue && exitCode.Value != 0)
                {
                    Logger.GetLogger("create_container").Printf("Failed to create user {0}: {1}", username, output);
                    return false;
                }

                if (userCfg.Password != null && userCfg.Password != "")
                {
                    string passwordCmd = $"echo '{username}:{userCfg.Password}' | chpasswd";
                    (output, exitCode) = pctService.Execute(ContainerCfg.ID, passwordCmd, null);
                    if (exitCode.HasValue && exitCode.Value != 0)
                    {
                        Logger.GetLogger("create_container").Printf("Failed to set password for user {0}: {1}", username, output);
                        return false;
                    }
                    Logger.GetLogger("create_container").Printf("Password set for user {0}", username);
                }

                string sudoersPath = $"/etc/sudoers.d/{username}";
                string sudoersContent = $"{username} ALL=(ALL) NOPASSWD: ALL\n";
                string sudoersWriteCmd = CLI.Files.NewFileOps().Write(sudoersPath, sudoersContent).ToCommand();
                (output, exitCode) = pctService.Execute(ContainerCfg.ID, sudoersWriteCmd, null);
                if (exitCode.HasValue && exitCode.Value != 0)
                {
                    Logger.GetLogger("create_container").Printf("Failed to write sudoers file for user {0}: {1}", username, output);
                    return false;
                }

                string sudoersChmodCmd = CLI.Files.NewFileOps().Chmod(sudoersPath, "440");
                (output, exitCode) = pctService.Execute(ContainerCfg.ID, sudoersChmodCmd, null);
                if (exitCode.HasValue && exitCode.Value != 0)
                {
                    Logger.GetLogger("create_container").Printf("Failed to secure sudoers file for user {0}: {1}", username, output);
                    return false;
                }
            }

            string defaultUser = Cfg.Users.DefaultUser();
            if (!Common.SetupSSHKey(containerIDInt, ipAddress, Cfg, lxcService, pctService))
            {
                Logger.GetLogger("create_container").Printf("Failed to setup SSH key");
                return false;
            }

            if (!pctService.EnsureSSHServiceRunning(containerIDInt, Cfg))
            {
                Logger.GetLogger("create_container").Printf("Failed to ensure SSH service is running");
                return false;
            }

            Logger.GetLogger("create_container").Printf("Waiting for container to be ready with SSH connectivity (up to 10 minutes)...");
            if (!pctService.WaitForContainer(containerIDInt, ipAddress, Cfg, defaultUser))
            {
                Logger.GetLogger("create_container").Printf("Container {0} did not become ready within 10 minutes", containerIDInt);
                return false;
            }

            Logger.GetLogger("create_container").Printf("Container {0} created successfully", containerIDInt);
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }
}

public static class CreateContainerActionFactory
{
    public static IAction NewCreateContainerAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg, DeployPlan? plan)
    {
        return new CreateContainerAction(sshService, aptService, pctService, containerID, cfg, containerCfg, plan);
    }
}