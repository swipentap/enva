using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Enva.Actions;
using Enva.Libs;
using Enva.Orchestration;
using Enva.Services;

namespace Enva.Commands;

public class DeployError : Exception
{
    public DeployError(string message) : base(message) { }
}

public class PortFailure
{
    public string Name { get; set; } = "";
    public string IP { get; set; } = "";
    public int Port { get; set; }
}

public class DeployCommand
{
    private LabConfig? cfg;
    private ILXCService? lxcService;
    private PCTService? pctService;
    private string? rancherBootstrapPassword;

    public DeployCommand(LabConfig? cfg, ILXCService? lxcService, PCTService? pctService)
    {
        this.cfg = cfg;
        this.lxcService = lxcService;
        this.pctService = pctService;
    }

    public void Run(int startStep, int? endStep, bool planOnly)
    {
        var logger = Logger.GetLogger("deploy");
        try
        {
            var plan = BuildPlan(startStep, endStep);
            if (planOnly)
            {
                LogDeployPlan(plan);
                logger.Printf("");
                logger.Printf("Plan-only mode: Exiting without executing deployment.");
                return;
            }
            RunDeploy(plan);
        }
        catch (Exception ex)
        {
            var logger2 = Logger.GetLogger("deploy");
            logger2.Printf("Error during deployment: {0}", ex.Message);
            if (ex is DeployError)
            {
                throw;
            }
            throw new DeployError($"Error during deployment: {ex.Message}");
        }
    }

    private void RunDeploy(DeployPlan plan)
    {
        var logger = Logger.GetLogger("deploy");
        plan = BuildPlan(plan.StartStep, plan.EndStep);
        LogDeployPlan(plan);
        logger.Printf("=== Executing Deployment ===");
        if (plan.AptCacheContainer != null)
        {
            int aptCacheSteps = 1 + CountActions(plan.AptCacheContainer);
            int aptCacheStart = plan.CurrentActionStep + 1;
            int aptCacheEnd = aptCacheStart + aptCacheSteps - 1;
            if (plan.StartStep <= aptCacheEnd)
            {
                // Temporarily set template to null (base template) for apt-cache, matching Python behavior
                string? originalTemplate = plan.AptCacheContainer.Template;
                plan.AptCacheContainer.Template = null;
                ExecuteContainerActions(plan, plan.AptCacheContainer, "apt-cache");
                plan.AptCacheContainer.Template = originalTemplate;
            }
            else
            {
                plan.CurrentActionStep += aptCacheSteps;
            }
            if (plan.EndStep.HasValue && plan.CurrentActionStep >= plan.EndStep.Value)
            {
                logger.Printf("Reached end step {0}, stopping deployment", plan.EndStep.Value);
                List<PortFailure> failedPorts = new List<PortFailure>();
                if (plan.EndStep.Value == plan.TotalSteps)
                {
                    failedPorts = CheckServicePorts();
                }
                LogDeploySummary(cfg, failedPorts, rancherBootstrapPassword);
                if (failedPorts.Count > 0)
                {
                    throw CreatePortError(failedPorts);
                }
                return;
            }
        }
        foreach (var templateCfg in plan.Templates)
        {
            int templateSteps = 1 + CountTemplateActions(templateCfg);
            int templateStart = plan.CurrentActionStep + 1;
            int templateEnd = templateStart + templateSteps - 1;
            if (plan.StartStep <= templateEnd)
            {
                ExecuteTemplateActions(plan, templateCfg);
            }
            else
            {
                plan.CurrentActionStep += templateSteps;
            }
            if (plan.EndStep.HasValue && plan.CurrentActionStep >= plan.EndStep.Value)
            {
                logger.Printf("Reached end step {0}, stopping deployment", plan.EndStep.Value);
                List<PortFailure> failedPorts = new List<PortFailure>();
                if (plan.EndStep.Value == plan.TotalSteps)
                {
                    failedPorts = CheckServicePorts();
                }
                LogDeploySummary(cfg, failedPorts, rancherBootstrapPassword);
                if (failedPorts.Count > 0)
                {
                    throw CreatePortError(failedPorts);
                }
                return;
            }
        }
        foreach (var containerCfg in plan.ContainersList)
        {
            int containerSteps = 1 + CountActions(containerCfg);
            int containerStart = plan.CurrentActionStep + 1;
            int containerEnd = containerStart + containerSteps - 1;
            if (plan.StartStep <= containerEnd)
            {
                ExecuteContainerActions(plan, containerCfg, containerCfg.Name);
                if (plan.EndStep.HasValue && plan.CurrentActionStep >= plan.EndStep.Value)
                {
                    logger.Printf("Reached end step {0}, stopping deployment", plan.EndStep.Value);
                    List<PortFailure> failedPorts = new List<PortFailure>();
                    if (plan.EndStep.Value == plan.TotalSteps)
                    {
                        failedPorts = CheckServicePorts();
                    }
                    LogDeploySummary(cfg, failedPorts, rancherBootstrapPassword);
                    if (failedPorts.Count > 0)
                    {
                        throw CreatePortError(failedPorts);
                    }
                    return;
                }
            }
            else
            {
                plan.CurrentActionStep += containerSteps;
                if (plan.EndStep.HasValue && plan.CurrentActionStep >= plan.EndStep.Value)
                {
                    logger.Printf("Reached end step {0}, stopping deployment", plan.EndStep.Value);
                    List<PortFailure> failedPorts = new List<PortFailure>();
                    if (plan.EndStep.Value == plan.TotalSteps)
                    {
                        failedPorts = CheckServicePorts();
                    }
                    LogDeploySummary(cfg, failedPorts, rancherBootstrapPassword);
                    if (failedPorts.Count > 0)
                    {
                        throw CreatePortError(failedPorts);
                    }
                    return;
                }
            }
        }
        List<ContainerConfig> kubernetesContainers = new List<ContainerConfig>();
        if (cfg != null && cfg.Kubernetes != null)
        {
            HashSet<int> k8sIDs = new HashSet<int>();
            foreach (int id in cfg.Kubernetes.Control)
            {
                k8sIDs.Add(id);
            }
            foreach (int id in cfg.Kubernetes.Workers)
            {
                k8sIDs.Add(id);
            }
            foreach (var container in cfg.Containers)
            {
                if (k8sIDs.Contains(container.ID))
                {
                    kubernetesContainers.Add(container);
                }
            }
        }
        foreach (var containerCfg in kubernetesContainers)
        {
            int containerSteps = 1 + CountActions(containerCfg);
            int containerStart = plan.CurrentActionStep + 1;
            int containerEnd = containerStart + containerSteps - 1;
            if (plan.StartStep <= containerEnd)
            {
                ExecuteContainerActions(plan, containerCfg, containerCfg.Name);
                if (plan.EndStep.HasValue && plan.CurrentActionStep >= plan.EndStep.Value)
                {
                    logger.Printf("Reached end step {0}, stopping deployment", plan.EndStep.Value);
                    List<PortFailure> failedPorts = new List<PortFailure>();
                    if (plan.EndStep.Value == plan.TotalSteps)
                    {
                        failedPorts = CheckServicePorts();
                    }
                    LogDeploySummary(cfg, failedPorts, rancherBootstrapPassword);
                    if (failedPorts.Count > 0)
                    {
                        throw CreatePortError(failedPorts);
                    }
                    return;
                }
            }
            else
            {
                plan.CurrentActionStep += containerSteps;
                if (plan.EndStep.HasValue && plan.CurrentActionStep >= plan.EndStep.Value)
                {
                    logger.Printf("Reached end step {0}, stopping deployment", plan.EndStep.Value);
                    List<PortFailure> failedPorts = new List<PortFailure>();
                    if (plan.EndStep.Value == plan.TotalSteps)
                    {
                        failedPorts = CheckServicePorts();
                    }
                    LogDeploySummary(cfg, failedPorts, rancherBootstrapPassword);
                    if (failedPorts.Count > 0)
                    {
                        throw CreatePortError(failedPorts);
                    }
                    return;
                }
            }
        }
        if (kubernetesContainers.Count > 0)
        {
            plan.CurrentActionStep++;
            if (plan.CurrentActionStep < plan.StartStep)
            {
                logger.Printf("Skipping setup kubernetes (step {0} < start_step {1})", plan.CurrentActionStep, plan.StartStep);
            }
            else if (plan.EndStep.HasValue && plan.CurrentActionStep > plan.EndStep.Value)
            {
                logger.Printf("Reached end step {0}, stopping deployment", plan.EndStep.Value);
                List<PortFailure> failedPorts = new List<PortFailure>();
                if (plan.EndStep.Value == plan.TotalSteps)
                {
                    failedPorts = CheckServicePorts();
                }
                LogDeploySummary(cfg, failedPorts, rancherBootstrapPassword);
                if (failedPorts.Count > 0)
                {
                    throw CreatePortError(failedPorts);
                }
                return;
            }
            else
            {
                int overallPct = (int)((double)plan.CurrentActionStep / plan.TotalSteps * 100);
                logger.Printf("=== [Overall: {0}%] [Step: {1}/{2}] Executing: kubernetes - setup kubernetes ===", overallPct, plan.CurrentActionStep, plan.TotalSteps);
                if (lxcService == null || !lxcService.IsConnected())
                {
                    if (lxcService == null || !lxcService.Connect())
                    {
                        throw new DeployError($"Failed to connect to LXC host {cfg?.LXCHost()}");
                    }
                }
                if (pctService == null && lxcService != null)
                {
                    pctService = new PCTService(lxcService);
                }
                var setupKubernetesAction = SetupKubernetesActionFactory.NewSetupKubernetesAction(null, null, pctService, null, cfg, null);
                if (!setupKubernetesAction.Execute())
                {
                    throw new DeployError("Failed to execute setup kubernetes action");
                }
                // Retrieve Rancher bootstrap password after Kubernetes setup
                if (cfg != null && cfg.Kubernetes != null && cfg.Kubernetes.Control != null && cfg.Kubernetes.Control.Count > 0 && cfg.Services.Services != null && cfg.Services.Services.ContainsKey("rancher") && pctService != null)
                {
                    int controlID = cfg.Kubernetes.Control[0];
                    rancherBootstrapPassword = GetRancherBootstrapPassword(controlID, pctService);
                }
            }
            if (plan.EndStep.HasValue && plan.CurrentActionStep >= plan.EndStep.Value)
            {
                logger.Printf("Reached end step {0}, stopping deployment", plan.EndStep.Value);
                List<PortFailure> failedPorts = new List<PortFailure>();
                if (plan.EndStep.Value == plan.TotalSteps)
                {
                    failedPorts = CheckServicePorts();
                }
                LogDeploySummary(cfg, failedPorts, rancherBootstrapPassword);
                if (failedPorts.Count > 0)
                {
                    throw CreatePortError(failedPorts);
                }
                return;
            }
        }
        if (cfg != null && cfg.GlusterFS != null)
        {
            plan.CurrentActionStep++;
            if (plan.CurrentActionStep < plan.StartStep)
            {
                logger.Printf("Skipping GlusterFS setup (step {0} < start_step {1})", plan.CurrentActionStep, plan.StartStep);
            }
            else if (plan.EndStep.HasValue && plan.CurrentActionStep > plan.EndStep.Value)
            {
                logger.Printf("Reached end step {0}, stopping deployment", plan.EndStep.Value);
            }
            else
            {
                int overallPct = (int)((double)plan.CurrentActionStep / plan.TotalSteps * 100);
                logger.Printf("=== [Overall: {0}%] [Step: {1}/{2}] Executing: GlusterFS setup ===", overallPct, plan.CurrentActionStep, plan.TotalSteps);
                if (!Gluster.SetupGlusterFS(cfg))
                {
                    throw new DeployError("GlusterFS setup failed");
                }
            }
        }
        if (cfg != null && cfg.Kubernetes != null && cfg.Kubernetes.Actions != null && cfg.Kubernetes.GetActionNames().Count > 0)
        {
            // Get control node info for SSH service
            SSHService? sshService = null;
            APTService? aptService = null;
            if (cfg.Kubernetes.Control != null && cfg.Kubernetes.Control.Count > 0)
            {
                int controlID = cfg.Kubernetes.Control[0];
                ContainerConfig? controlNode = null;
                foreach (var ct in cfg.Containers)
                {
                    if (ct.ID == controlID)
                    {
                        controlNode = ct;
                        break;
                    }
                }
                if (controlNode != null && controlNode.IPAddress != null)
                {
                    string defaultUser = cfg.Users.DefaultUser();
                    SSHConfig containerSSHConfig = new SSHConfig
                    {
                        ConnectTimeout = cfg.SSH.ConnectTimeout,
                        BatchMode = cfg.SSH.BatchMode,
                        DefaultExecTimeout = cfg.SSH.DefaultExecTimeout,
                        ReadBufferSize = cfg.SSH.ReadBufferSize,
                        PollInterval = cfg.SSH.PollInterval,
                        DefaultUsername = defaultUser,
                        LookForKeys = cfg.SSH.LookForKeys,
                        AllowAgent = cfg.SSH.AllowAgent,
                        Verbose = cfg.SSH.Verbose
                    };
                    sshService = new SSHService($"{defaultUser}@{controlNode.IPAddress}", containerSSHConfig);
                    if (sshService.Connect())
                    {
                        aptService = new APTService(sshService);
                    }
                    else
                    {
                        sshService = null;
                    }
                }
            }
            foreach (string actionName in cfg.Kubernetes.GetActionNames())
            {
                plan.CurrentActionStep++;
                if (plan.CurrentActionStep < plan.StartStep)
                {
                    logger.Printf("Skipping Kubernetes action '{0}' (step {1} < start_step {2})", actionName, plan.CurrentActionStep, plan.StartStep);
                    continue;
                }
                if (plan.EndStep.HasValue && plan.CurrentActionStep > plan.EndStep.Value)
                {
                    logger.Printf("Reached end step {0}, stopping deployment", plan.EndStep.Value);
                    break;
                }
                int overallPct = (int)((double)plan.CurrentActionStep / plan.TotalSteps * 100);
                logger.Printf("=== [Overall: {0}%] [Step: {1}/{2}] Executing: Kubernetes action - {3} ===", overallPct, plan.CurrentActionStep, plan.TotalSteps, actionName);
                try
                {
                    var action = ActionRegistry.GetAction(actionName, sshService, aptService, pctService, null, cfg, null);
                    if (!action.Execute())
                    {
                        if (sshService != null)
                        {
                            sshService.Disconnect();
                        }
                        throw new DeployError($"Kubernetes action '{actionName}' failed");
                    }
                }
                catch (Exception ex)
                {
                    if (sshService != null)
                    {
                        sshService.Disconnect();
                    }
                    throw new DeployError($"Kubernetes action '{actionName}' not found: {ex.Message}");
                }
            }
            if (sshService != null)
            {
                sshService.Disconnect();
            }
        }
        List<PortFailure> finalFailedPorts = CheckServicePorts();
        LogDeploySummary(cfg, finalFailedPorts, rancherBootstrapPassword);
        if (finalFailedPorts.Count > 0)
        {
            throw CreatePortError(finalFailedPorts);
        }
    }

    private void ExecuteContainerActions(DeployPlan plan, ContainerConfig containerCfg, string containerName)
    {
        var logger = Logger.GetLogger("deploy");
        if (cfg == null || pctService == null)
        {
            throw new DeployError("Config or PCT service not initialized");
        }
        string containerIDStr = containerCfg.ID.ToString();
        string ipAddress = containerCfg.IPAddress ?? "";
        plan.CurrentActionStep++;
        bool skipContainerCreation = plan.CurrentActionStep < plan.StartStep;
        if (skipContainerCreation)
        {
            logger.Printf("Skipping container '{0}' creation (step {1} < start_step {2})", containerName, plan.CurrentActionStep, plan.StartStep);
        }
        if (plan.EndStep.HasValue && plan.CurrentActionStep > plan.EndStep.Value)
        {
            logger.Printf("Reached end step {0}, stopping deployment", plan.EndStep.Value);
            return;
        }
        if (!skipContainerCreation)
        {
            int overallPct = (int)((double)plan.CurrentActionStep / plan.TotalSteps * 100);
            logger.Printf("=== [Overall: {0}%] [Step: {1}/{2}] Executing: {3} - create container ===", overallPct, plan.CurrentActionStep, plan.TotalSteps, containerName);
            var createAction = CreateContainerActionFactory.NewCreateContainerAction(null, null, null, null, cfg, containerCfg, plan);
            if (!createAction.Execute())
            {
                throw new DeployError($"Failed to create container: {containerName}");
            }
            logger.Printf("Container '{0}' created successfully", containerName);
        }
        string defaultUser = cfg.Users.DefaultUser();
        logger.Printf("Setting up SSH connection to container {0}...", containerName);
        SSHConfig containerSSHConfig = new SSHConfig
        {
            ConnectTimeout = cfg.SSH.ConnectTimeout,
            BatchMode = cfg.SSH.BatchMode,
            DefaultExecTimeout = cfg.SSH.DefaultExecTimeout,
            ReadBufferSize = cfg.SSH.ReadBufferSize,
            PollInterval = cfg.SSH.PollInterval,
            DefaultUsername = defaultUser,
            LookForKeys = cfg.SSH.LookForKeys,
            AllowAgent = cfg.SSH.AllowAgent,
            Verbose = cfg.SSH.Verbose
        };
        SSHService sshService = new SSHService($"{defaultUser}@{ipAddress}", containerSSHConfig);
        if (!sshService.Connect())
        {
            throw new DeployError($"Failed to connect to container {containerName} via SSH");
        }
        try
        {
            Thread.Sleep(2000);
            APTService aptService = new APTService(sshService);
            if (lxcService == null || !lxcService.IsConnected())
            {
                if (lxcService == null || !lxcService.Connect())
                {
                    throw new DeployError($"Failed to connect to LXC host {cfg.LXCHost()}");
                }
            }
            List<string> actionNames = containerCfg.GetActionNames();
            foreach (string actionName in actionNames)
            {
                plan.CurrentActionStep++;
                if (plan.CurrentActionStep < plan.StartStep)
                {
                    continue;
                }
                if (plan.EndStep.HasValue && plan.CurrentActionStep > plan.EndStep.Value)
                {
                    logger.Printf("Reached end step {0}, stopping action execution", plan.EndStep.Value);
                    return;
                }
                int overallPct = (int)((double)plan.CurrentActionStep / plan.TotalSteps * 100);
                logger.Printf("=== [Overall: {0}%] [Step: {1}/{2}] Executing: {3} - {4} ===", overallPct, plan.CurrentActionStep, plan.TotalSteps, containerName, actionName);
                try
                {
                    var action = ActionRegistry.GetAction(actionName, sshService, aptService, pctService, containerIDStr, cfg, containerCfg);
                    if (!action.Execute())
                    {
                        throw new DeployError($"Failed to execute action '{actionName}' for container '{containerName}'");
                    }
                    logger.Printf("Action '{0}' for container '{1}' completed successfully", actionName, containerName);
                }
                catch (Exception ex)
                {
                    throw new DeployError($"Action '{actionName}' not found for container '{containerName}': {ex.Message}");
                }
            }
        }
        finally
        {
            sshService.Disconnect();
        }
    }

    private void ExecuteTemplateActions(DeployPlan plan, TemplateConfig templateCfg)
    {
        var logger = Logger.GetLogger("deploy");
        var containerCfg = new ContainerConfig
        {
            Name = templateCfg.Name,
            ID = templateCfg.ID,
            IP = templateCfg.IP,
            Hostname = templateCfg.Hostname,
            Template = templateCfg.Template,
            Resources = templateCfg.Resources,
            IPAddress = templateCfg.IPAddress,
            Actions = templateCfg.Actions,
            Privileged = templateCfg.Privileged,
            Nested = templateCfg.Nested
        };
        ExecuteContainerActions(plan, containerCfg, templateCfg.Name);
        logger.Printf("Destroying template container {0} after processing...", containerCfg.ID);
        if (cfg == null || lxcService == null)
        {
            throw new DeployError("Config or LXC service not initialized");
        }
        Common.DestroyContainer(cfg.LXCHost(), containerCfg.ID, cfg, lxcService);
    }

    private int CountActions(ContainerConfig containerCfg)
    {
        return containerCfg.GetActionNames().Count;
    }

    private int CountTemplateActions(TemplateConfig templateCfg)
    {
        return templateCfg.GetActionNames().Count;
    }

    private void LogDeployPlan(DeployPlan plan)
    {
        var logger = Logger.GetLogger("deploy");
        List<(int num, string label)> steps = new List<(int, string)>();
        int stepNum = 1;
        if (plan.AptCacheContainer != null)
        {
            var c = plan.AptCacheContainer;
            steps.Add((stepNum, $"{c.Name}: create container"));
            stepNum++;
            foreach (string action in c.GetActionNames())
            {
                steps.Add((stepNum, $"{c.Name}: {action}"));
                stepNum++;
            }
        }
        foreach (var tmpl in plan.Templates)
        {
            steps.Add((stepNum, $"{tmpl.Name}: create template"));
            stepNum++;
            foreach (string action in tmpl.GetActionNames())
            {
                steps.Add((stepNum, $"{tmpl.Name}: {action}"));
                stepNum++;
            }
        }
        foreach (var c in plan.ContainersList)
        {
            steps.Add((stepNum, $"{c.Name}: create container"));
            stepNum++;
            foreach (string action in c.GetActionNames())
            {
                steps.Add((stepNum, $"{c.Name}: {action}"));
                stepNum++;
            }
        }
        List<ContainerConfig> kubernetesContainers = new List<ContainerConfig>();
        if (cfg != null && cfg.Kubernetes != null)
        {
            HashSet<int> k8sIDs = new HashSet<int>();
            foreach (int id in cfg.Kubernetes.Control)
            {
                k8sIDs.Add(id);
            }
            foreach (int id in cfg.Kubernetes.Workers)
            {
                k8sIDs.Add(id);
            }
            foreach (var container in cfg.Containers)
            {
                if (k8sIDs.Contains(container.ID))
                {
                    kubernetesContainers.Add(container);
                }
            }
        }
        foreach (var c in kubernetesContainers)
        {
            steps.Add((stepNum, $"{c.Name}: create container"));
            stepNum++;
            if (c.Actions != null)
            {
                foreach (string action in c.Actions)
                {
                    steps.Add((stepNum, $"{c.Name}: {action}"));
                    stepNum++;
                }
            }
        }
        if (kubernetesContainers.Count > 0)
        {
            steps.Add((stepNum, "kubernetes: setup kubernetes"));
            stepNum++;
        }
        if (cfg != null && cfg.GlusterFS != null)
        {
            steps.Add((stepNum, "glusterfs: setup glusterfs"));
            stepNum++;
        }
        if (cfg != null && cfg.Kubernetes != null && cfg.Kubernetes.Actions != null && cfg.Kubernetes.GetActionNames().Count > 0)
        {
            foreach (string actionName in cfg.Kubernetes.GetActionNames())
            {
                steps.Add((stepNum, $"kubernetes: {actionName}"));
                stepNum++;
            }
        }
        logger.Printf("");
        int endStepDisplay = plan.TotalSteps;
        if (plan.EndStep.HasValue)
        {
            endStepDisplay = plan.EndStep.Value;
        }
        logger.Printf("Deploy plan (total {0} steps, running {1}-{2}):", plan.TotalSteps, plan.StartStep, endStepDisplay);
        foreach (var (num, label) in steps)
        {
            int endStep = plan.TotalSteps;
            if (plan.EndStep.HasValue)
            {
                endStep = plan.EndStep.Value;
            }
            string marker = "skip";
            if (plan.StartStep <= num && num <= endStep)
            {
                marker = "RUN";
            }
            logger.Printf("  [{0,2}] {1,-4} {2}", num, marker, label);
        }
    }

    private DeployPlan BuildPlan(int startStep, int? endStep)
    {
        if (cfg == null)
        {
            throw new DeployError("Config not initialized");
        }
        var containers = cfg.Containers;
        ContainerConfig? aptCacheContainer = null;
        foreach (var ct in containers)
        {
            if (ct.Name == cfg.APTCacheCT)
            {
                aptCacheContainer = ct;
                break;
            }
        }
        List<TemplateConfig> templates = new List<TemplateConfig>();
        foreach (var tmpl in cfg.Templates)
        {
            templates.Add(tmpl);
        }
        HashSet<int> k8sIDs = new HashSet<int>();
        if (cfg.Kubernetes != null)
        {
            foreach (int id in cfg.Kubernetes.Control)
            {
                k8sIDs.Add(id);
            }
            foreach (int id in cfg.Kubernetes.Workers)
            {
                k8sIDs.Add(id);
            }
        }
        List<ContainerConfig> containersList = new List<ContainerConfig>();
        foreach (var ct in containers)
        {
            if (!k8sIDs.Contains(ct.ID) && ct.Name != cfg.APTCacheCT)
            {
                containersList.Add(ct);
            }
        }
        int totalSteps = 0;
        if (aptCacheContainer != null)
        {
            totalSteps++;
            totalSteps += CountActions(aptCacheContainer);
        }
        foreach (var template in templates)
        {
            totalSteps++;
            totalSteps += CountTemplateActions(template);
        }
        foreach (var container in containersList)
        {
            totalSteps++;
            totalSteps += CountActions(container);
        }
        List<ContainerConfig> kubernetesContainers = new List<ContainerConfig>();
        if (cfg.Kubernetes != null)
        {
            foreach (var ct in containers)
            {
                if (k8sIDs.Contains(ct.ID))
                {
                    kubernetesContainers.Add(ct);
                }
            }
        }
        foreach (var container in kubernetesContainers)
        {
            totalSteps++;
            totalSteps += CountActions(container);
        }
        if (kubernetesContainers.Count > 0)
        {
            totalSteps++;
        }
        if (cfg.GlusterFS != null)
        {
            totalSteps++;
        }
        if (cfg.Kubernetes != null && cfg.Kubernetes.Actions != null)
        {
            totalSteps += cfg.Kubernetes.GetActionNames().Count;
        }
        // apt-cache container is optional (not required if not configured)
        // if (aptCacheContainer == null)
        // {
        //     throw new DeployError($"apt-cache container '{cfg.APTCacheCT}' not found in configuration");
        // }
        if (endStep == null)
        {
            endStep = totalSteps;
        }
        return new DeployPlan
        {
            AptCacheContainer = aptCacheContainer,
            Templates = templates,
            ContainersList = containersList,
            TotalSteps = totalSteps,
            Step = 1,
            StartStep = startStep,
            EndStep = endStep,
            CurrentActionStep = 0,
            PlanOnly = false
        };
    }

    private List<PortFailure> CheckServicePorts()
    {
        var logger = Logger.GetLogger("deploy");
        logger.Printf("Checking service ports...");
        Thread.Sleep(5000);
        List<PortFailure> failedPorts = new List<PortFailure>();
        if (cfg == null)
        {
            return failedPorts;
        }
        ContainerConfig? aptCacheCT = null;
        foreach (var ct in cfg.Containers)
        {
            if (ct.Name == cfg.APTCacheCT)
            {
                aptCacheCT = ct;
                break;
            }
        }
        if (aptCacheCT != null && aptCacheCT.IPAddress != null && cfg.Services.Services != null && cfg.Services.Services.TryGetValue("apt_cache", out var aptCacheService))
        {
            int port = 80;
            if (aptCacheService.Ports != null && aptCacheService.Ports.Count > 0)
            {
                port = aptCacheService.Ports[0].Port;
            }
            if (CheckPort(aptCacheCT.IPAddress, port))
            {
                logger.Printf("  ✓ apt-cache: {0}:{1}", aptCacheCT.IPAddress, port);
            }
            else
            {
                logger.Printf("  ✗ apt-cache: {0}:{1} - NOT RESPONDING", aptCacheCT.IPAddress, port);
                failedPorts.Add(new PortFailure { Name = "apt-cache", IP = aptCacheCT.IPAddress, Port = port });
            }
        }
        ContainerConfig? pgsqlCT = null;
        foreach (var ct in cfg.Containers)
        {
            if (ct.Name == "pgsql")
            {
                pgsqlCT = ct;
                break;
            }
        }
        if (pgsqlCT != null && pgsqlCT.IPAddress != null && cfg.Services.Services != null && cfg.Services.Services.TryGetValue("postgresql", out var postgresqlService))
        {
            int port = 5432;
            if (postgresqlService.Ports != null && postgresqlService.Ports.Count > 0)
            {
                port = postgresqlService.Ports[0].Port;
            }
            if (CheckPort(pgsqlCT.IPAddress, port))
            {
                logger.Printf("  ✓ PostgreSQL: {0}:{1}", pgsqlCT.IPAddress, port);
            }
            else
            {
                logger.Printf("  ✗ PostgreSQL: {0}:{1} - NOT RESPONDING", pgsqlCT.IPAddress, port);
                failedPorts.Add(new PortFailure { Name = "PostgreSQL", IP = pgsqlCT.IPAddress, Port = port });
            }
        }
        ContainerConfig? haproxyCT = null;
        foreach (var ct in cfg.Containers)
        {
            if (ct.Name == "haproxy")
            {
                haproxyCT = ct;
                break;
            }
        }
        if (haproxyCT != null && haproxyCT.IPAddress != null && cfg.Services.Services != null && cfg.Services.Services.TryGetValue("haproxy", out var haproxyService))
        {
            int httpPort = 80;
            if (haproxyService.Ports != null && haproxyService.Ports.Count > 0)
            {
                httpPort = haproxyService.Ports[0].Port;
            }
            if (CheckPort(haproxyCT.IPAddress, httpPort))
            {
                logger.Printf("  ✓ HAProxy HTTP: {0}:{1}", haproxyCT.IPAddress, httpPort);
            }
            else
            {
                logger.Printf("  ✗ HAProxy HTTP: {0}:{1} - NOT RESPONDING", haproxyCT.IPAddress, httpPort);
                failedPorts.Add(new PortFailure { Name = "HAProxy HTTP", IP = haproxyCT.IPAddress, Port = httpPort });
            }
            // Note: stats port removed from services structure - check if needed elsewhere
            int statsPort = 8404;
            if (CheckPort(haproxyCT.IPAddress, statsPort))
            {
                logger.Printf("  ✓ HAProxy Stats: {0}:{1}", haproxyCT.IPAddress, statsPort);
            }
            else
            {
                logger.Printf("  ✗ HAProxy Stats: {0}:{1} - NOT RESPONDING", haproxyCT.IPAddress, statsPort);
                failedPorts.Add(new PortFailure { Name = "HAProxy Stats", IP = haproxyCT.IPAddress, Port = statsPort });
            }
        }
        ContainerConfig? dnsCT = null;
        foreach (var ct in cfg.Containers)
        {
            if (ct.Name == "dns")
            {
                dnsCT = ct;
                break;
            }
        }
        if (dnsCT != null && dnsCT.IPAddress != null)
        {
            int port = 53;
            if (dnsCT.Params != null && dnsCT.Params.ContainsKey("dns_port"))
            {
                if (dnsCT.Params["dns_port"] is int p)
                {
                    port = p;
                }
            }
            if (CheckPort(dnsCT.IPAddress, port))
            {
                logger.Printf("  ✓ DNS: {0}:{1}", dnsCT.IPAddress, port);
            }
            else
            {
                logger.Printf("  ✗ DNS: {0}:{1} - NOT RESPONDING", dnsCT.IPAddress, port);
                failedPorts.Add(new PortFailure { Name = "DNS", IP = dnsCT.IPAddress, Port = port });
            }
        }
        if (cfg.GlusterFS != null)
        {
            ContainerConfig? glusterfsNode = null;
            if (cfg.GlusterFS.ClusterNodes != null && cfg.GlusterFS.ClusterNodes.Count > 0)
            {
                HashSet<int> clusterNodeIDs = new HashSet<int>();
                foreach (var node in cfg.GlusterFS.ClusterNodes)
                {
                    clusterNodeIDs.Add(node.ID);
                }
                foreach (var ct in cfg.Containers)
                {
                    if (clusterNodeIDs.Contains(ct.ID))
                    {
                        glusterfsNode = ct;
                        break;
                    }
                }
            }
            if (glusterfsNode != null && glusterfsNode.IPAddress != null)
            {
                if (CheckPort(glusterfsNode.IPAddress, 24007))
                {
                    logger.Printf("  ✓ GlusterFS: {0}:24007", glusterfsNode.IPAddress);
                }
                else
                {
                    logger.Printf("  ✗ GlusterFS: {0}:24007 - NOT RESPONDING", glusterfsNode.IPAddress);
                    failedPorts.Add(new PortFailure { Name = "GlusterFS", IP = glusterfsNode.IPAddress, Port = 24007 });
                }
            }
        }
        if (cfg.Kubernetes != null && cfg.Kubernetes.Control != null && cfg.Kubernetes.Control.Count > 0 && cfg.Services.Services != null && cfg.Services.Services.TryGetValue("rancher", out var rancherService))
        {
            int controlID = cfg.Kubernetes.Control[0];
            ContainerConfig? controlNode = null;
            foreach (var ct in cfg.Containers)
            {
                if (ct.ID == controlID)
                {
                    controlNode = ct;
                    break;
                }
            }
            if (controlNode != null && controlNode.IPAddress != null)
            {
                int httpsPort = 30443;
                if (rancherService.Ports != null && rancherService.Ports.Count > 0)
                {
                    httpsPort = rancherService.Ports[0].Port;
                }
                if (CheckPort(controlNode.IPAddress, httpsPort))
                {
                    logger.Printf("  ✓ Rancher: {0}:{1}", controlNode.IPAddress, httpsPort);
                }
                else
                {
                    logger.Printf("  ✗ Rancher: {0}:{1} - NOT RESPONDING", controlNode.IPAddress, httpsPort);
                    failedPorts.Add(new PortFailure { Name = "Rancher", IP = controlNode.IPAddress, Port = httpsPort });
                }
            }
        }
        if (cfg.Kubernetes != null && cfg.Kubernetes.Control != null && cfg.Kubernetes.Control.Count > 0 && cfg.Services.Services != null && cfg.Services.Services.TryGetValue("argocd", out var argocdService))
        {
            int controlID = cfg.Kubernetes.Control[0];
            ContainerConfig? controlNode = null;
            foreach (var ct in cfg.Containers)
            {
                if (ct.ID == controlID)
                {
                    controlNode = ct;
                    break;
                }
            }
            if (controlNode != null && controlNode.IPAddress != null)
            {
                int httpPort = 30080;
                if (argocdService.Ports != null && argocdService.Ports.Count > 0)
                {
                    httpPort = argocdService.Ports[0].Port;
                }
                if (CheckPort(controlNode.IPAddress, httpPort))
                {
                    logger.Printf("  ✓ ArgoCD: {0}:{1}", controlNode.IPAddress, httpPort);
                }
                else
                {
                    logger.Printf("  ✗ ArgoCD: {0}:{1} - NOT RESPONDING", controlNode.IPAddress, httpPort);
                    failedPorts.Add(new PortFailure { Name = "ArgoCD", IP = controlNode.IPAddress, Port = httpPort });
                }
            }
        }
        return failedPorts;
    }

    private static bool CheckPort(string ipAddress, int port)
    {
        try
        {
            using (var client = new TcpClient())
            {
                var result = client.BeginConnect(ipAddress, port, null, null);
                var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                if (!success)
                {
                    client.Close();
                    return false;
                }
                try
                {
                    client.EndConnect(result);
                    return true;
                }
                catch (SocketException)
                {
                    return false;
                }
            }
        }
        catch (SocketException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string GetRancherBootstrapPassword(int controlID, PCTService pctService)
    {
        string bootstrapPassword = "admin";
        // Check if namespace exists
        string checkNamespaceCmd = "kubectl get namespace cattle-system";
        (string namespaceOutput, int? namespaceExitCode) = pctService.Execute(controlID, checkNamespaceCmd, null);
        if (namespaceExitCode.HasValue && namespaceExitCode.Value == 0)
        {
            // Namespace exists, check if secret exists
            string checkSecretCmd = "kubectl get secret bootstrap-secret -n cattle-system";
            (string secretOutput, int? secretExitCode) = pctService.Execute(controlID, checkSecretCmd, null);
            if (secretExitCode.HasValue && secretExitCode.Value == 0)
            {
                // Secret exists, get the password
                string getPasswordCmd = "kubectl get secret bootstrap-secret -n cattle-system -o go-template='{{.data.bootstrapPassword|base64decode}}'";
                (string passwordOutput, int? passwordExitCode) = pctService.Execute(controlID, getPasswordCmd, null);
                if (passwordExitCode.HasValue && passwordExitCode.Value == 0 && !string.IsNullOrEmpty(passwordOutput))
                {
                    bootstrapPassword = passwordOutput.Trim();
                }
            }
        }
        return bootstrapPassword;
    }

    private DeployError CreatePortError(List<PortFailure> failedPorts)
    {
        string errorMsg = "Deploy failed: The following ports are not responding:\n";
        foreach (var pf in failedPorts)
        {
            errorMsg += $"  - {pf.Name}: {pf.IP}:{pf.Port}\n";
        }
        return new DeployError(errorMsg);
    }

    private void LogDeploySummary(LabConfig? cfg, List<PortFailure> failedPorts, string? rancherPassword = null)
    {
        var logger = Logger.GetLogger("deploy");
        string message = "Deploy Complete!";
        if (failedPorts.Count > 0)
        {
            message = "Deploy Complete (with port failures)";
        }
        logger.Printf("=== {0} ===", message);
        logger.Printf("Containers:");
        if (cfg != null)
        {
            foreach (var ct in cfg.Containers)
            {
                string ipAddr = ct.IPAddress ?? "N/A";
                logger.Printf("  - {0}: {1} ({2})", ct.ID, ct.Name, ipAddr);
            }
            ContainerConfig? pgsql = null;
            foreach (var ct in cfg.Containers)
            {
                if (ct.Name == "pgsql")
                {
                    pgsql = ct;
                    break;
                }
            }
            if (pgsql != null && pgsql.IPAddress != null && cfg.Services.Services != null && cfg.Services.Services.ContainsKey("postgresql"))
            {
                int pgPort = 5432;
                if (cfg.Services.Services.TryGetValue("postgresql", out var pgService) && pgService.Ports != null && pgService.Ports.Count > 0)
                {
                    pgPort = pgService.Ports[0].Port;
                }
                // Get PostgreSQL credentials from action properties (postgresql password setup action)
                string pgUser = "postgres";
                string pgPassword = "postgres";
                string pgDatabase = "postgres";
                foreach (var action in cfg.Containers.SelectMany(c => c.Actions ?? new List<object>()))
                {
                    if (action is Dictionary<string, object> actionDict && actionDict.ContainsKey("name") && actionDict["name"]?.ToString() == "postgresql password setup")
                    {
                        if (actionDict.ContainsKey("properties") && actionDict["properties"] is Dictionary<string, object> props)
                        {
                            if (props.TryGetValue("username", out var userObj)) pgUser = userObj?.ToString() ?? "postgres";
                            if (props.TryGetValue("password", out var passObj)) pgPassword = passObj?.ToString() ?? "postgres";
                            if (props.TryGetValue("database", out var dbObj)) pgDatabase = dbObj?.ToString() ?? "postgres";
                        }
                    }
                }
                logger.Printf("PostgreSQL: {0}:{1}", pgsql.IPAddress, pgPort);
                logger.Printf("  Username: {0}", pgUser);
                logger.Printf("  Password: {0}", pgPassword);
                logger.Printf("  Database: {0}", pgDatabase);
                logger.Printf("  Connection: postgresql://{0}:{1}@{2}:{3}/{4}", pgUser, pgPassword, pgsql.IPAddress, pgPort, pgDatabase);
            }
            ContainerConfig? dnsCT = null;
            foreach (var ct in cfg.Containers)
            {
                if (ct.Name == "dns")
                {
                    dnsCT = ct;
                    break;
                }
            }
            if (dnsCT != null && dnsCT.IPAddress != null)
            {
                int dnsPort = 53;
                if (dnsCT.Params != null && dnsCT.Params.ContainsKey("dns_port"))
                {
                    if (dnsCT.Params["dns_port"] is int p)
                    {
                        dnsPort = p;
                    }
                }
                int webPort = 80;
                if (dnsCT.Params != null && dnsCT.Params.ContainsKey("web_port"))
                {
                    if (dnsCT.Params["web_port"] is int p)
                    {
                        webPort = p;
                    }
                }
                logger.Printf("DNS: {0}:{1} (TCP/UDP)", dnsCT.IPAddress, dnsPort);
                logger.Printf("  Web Interface: http://{0}:{1}", dnsCT.IPAddress, webPort);
                logger.Printf("  Use as DNS server: {0}", dnsCT.IPAddress);
            }
            ContainerConfig? haproxy = null;
            foreach (var ct in cfg.Containers)
            {
                if (ct.Name == "haproxy")
                {
                    haproxy = ct;
                    break;
                }
            }
            if (haproxy != null && haproxy.IPAddress != null)
            {
                int httpPort = 80;
                int statsPort = 8404;
                if (haproxy.Params != null)
                {
                    if (haproxy.Params.ContainsKey("http_port") && haproxy.Params["http_port"] is int p1)
                    {
                        httpPort = p1;
                    }
                    if (haproxy.Params.ContainsKey("stats_port") && haproxy.Params["stats_port"] is int p2)
                    {
                        statsPort = p2;
                    }
                }
                logger.Printf("HAProxy: http://{0}:{1} (Stats: http://{0}:{2})", haproxy.IPAddress, httpPort, statsPort);
            }
            if (cfg.GlusterFS != null)
            {
                logger.Printf("GlusterFS:");
                logger.Printf("  Volume: {0}", cfg.GlusterFS.VolumeName);
                logger.Printf("  Mount: {0} on all nodes", cfg.GlusterFS.MountPoint);
            }
            if (cfg.Kubernetes != null && cfg.Kubernetes.Control != null && cfg.Kubernetes.Control.Count > 0 && cfg.Services.Services != null && cfg.Services.Services.TryGetValue("cockroachdb", out var cockroachService))
            {
                int controlID = cfg.Kubernetes.Control[0];
                ContainerConfig? controlNode = null;
                foreach (var ct in cfg.Containers)
                {
                    if (ct.ID == controlID)
                    {
                        controlNode = ct;
                        break;
                    }
                }
                if (controlNode != null && controlNode.IPAddress != null)
                {
                    // Get ports from action properties (install cockroachdb action)
                    int sqlPort = 32657;
                    int httpPort = 30080;
                    foreach (var action in cfg.Kubernetes.Actions ?? new List<object>())
                    {
                        if (action is Dictionary<string, object> actionDict && actionDict.ContainsKey("name") && actionDict["name"]?.ToString()?.ToLower().Contains("cockroachdb") == true)
                        {
                            if (actionDict.ContainsKey("properties") && actionDict["properties"] is Dictionary<string, object> props)
                            {
                                if (props.TryGetValue("sql_port", out var sqlPortObj) && sqlPortObj is int sp) sqlPort = sp;
                                else if (props.TryGetValue("sql_port", out var sqlPortStr) && int.TryParse(sqlPortStr?.ToString(), out var sp2)) sqlPort = sp2;
                                if (props.TryGetValue("http_port", out var httpPortObj) && httpPortObj is int hp) httpPort = hp;
                                else if (props.TryGetValue("http_port", out var httpPortStr) && int.TryParse(httpPortStr?.ToString(), out var hp2)) httpPort = hp2;
                            }
                        }
                    }
                    logger.Printf("CockroachDB:");
                    logger.Printf("  SQL: {0}:{1} (postgresql://root:root123@{0}:{1}/defaultdb?sslmode=disable)", controlNode.IPAddress, sqlPort);
                    logger.Printf("  Admin UI: http://{0}:{1}", controlNode.IPAddress, httpPort);
                    logger.Printf("  Username: root");
                    logger.Printf("  Password: root123");
                }
            }
            if (cfg.Kubernetes != null && cfg.Kubernetes.Control != null && cfg.Kubernetes.Control.Count > 0 && cfg.Services.Services != null && cfg.Services.Services.TryGetValue("rancher", out var rancherService))
            {
                int controlID = cfg.Kubernetes.Control[0];
                ContainerConfig? controlNode = null;
                foreach (var ct in cfg.Containers)
                {
                    if (ct.ID == controlID)
                    {
                        controlNode = ct;
                        break;
                    }
                }
                if (controlNode != null && controlNode.IPAddress != null)
                {
                    int httpsPort = 30443;
                    if (rancherService.Ports != null && rancherService.Ports.Count > 0)
                    {
                        httpsPort = rancherService.Ports[0].Port;
                    }
                    string bootstrapPassword = rancherPassword ?? "admin";
                    logger.Printf("Rancher: https://{0}:{1}", controlNode.IPAddress, httpsPort);
                    logger.Printf("  Bootstrap Password: {0}", bootstrapPassword);
                }
            }
            if (cfg.Kubernetes != null && cfg.Kubernetes.Control != null && cfg.Kubernetes.Control.Count > 0 && cfg.Services.Services != null && cfg.Services.Services.TryGetValue("argocd", out var argocdService))
            {
                int controlID = cfg.Kubernetes.Control[0];
                ContainerConfig? controlNode = null;
                foreach (var ct in cfg.Containers)
                {
                    if (ct.ID == controlID)
                    {
                        controlNode = ct;
                        break;
                    }
                }
                if (controlNode != null && controlNode.IPAddress != null)
                {
                    int httpPort = 30080;
                    if (argocdService.Ports != null && argocdService.Ports.Count > 0)
                    {
                        httpPort = argocdService.Ports[0].Port;
                    }
                    logger.Printf("ArgoCD: http://{0}:{1}", controlNode.IPAddress, httpPort);
                    logger.Printf("  Username: admin");
                    logger.Printf("  Bootstrap Password: admin1234");
                }
            }
        }
        if (failedPorts.Count > 0)
        {
            logger.Printf("⚠ Port Status:");
            logger.Printf("  The following ports are NOT responding:");
            foreach (var pf in failedPorts)
            {
                logger.Printf("    ✗ {0}: {1}:{2}", pf.Name, pf.IP, pf.Port);
            }
        }
        else
        {
            logger.Printf("✓ All service ports are responding");
        }
    }
}