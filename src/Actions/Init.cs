using Enva.Services;

namespace Enva.Actions;

public static class ActionInit
{
    public static void Initialize()
    {
        // Register all actions
        ActionRegistry.RegisterAction("cloud-init wait", CloudInitWaitActionFactory.NewCloudInitWaitAction);
        ActionRegistry.RegisterAction("system upgrade", SystemUpgradeActionFactory.NewSystemUpgradeAction);
        ActionRegistry.RegisterAction("haproxy installation", InstallHaproxyActionFactory.NewInstallHaproxyAction);
        ActionRegistry.RegisterAction("haproxy configuration", ConfigureHaproxyActionFactory.NewConfigureHaproxyAction);
        ActionRegistry.RegisterAction("haproxy systemd override", ConfigureHaproxySystemdActionFactory.NewConfigureHaproxySystemdAction);
        ActionRegistry.RegisterAction("haproxy service enablement", EnableHaproxyServiceActionFactory.NewEnableHaproxyServiceAction);
        ActionRegistry.RegisterAction("lxc sysctl access configuration", ConfigureLxcSysctlAccessActionFactory.NewConfigureLxcSysctlAccessAction);
        ActionRegistry.RegisterAction("apt sources fix", FixAptSourcesActionFactory.NewFixAptSourcesAction);
        ActionRegistry.RegisterAction("openssh-server installation", InstallOpensshServerActionFactory.NewInstallOpensshServerAction);
        ActionRegistry.RegisterAction("SSH service enablement", EnableSshServiceActionFactory.NewEnableSshServiceAction);
        ActionRegistry.RegisterAction("base tools installation", InstallBaseToolsActionFactory.NewInstallBaseToolsAction);
        ActionRegistry.RegisterAction("locale configuration", LocaleConfigurationActionFactory.NewLocaleConfigurationAction);
        ActionRegistry.RegisterAction("template cleanup", TemplateCleanupActionFactory.NewTemplateCleanupAction);
        ActionRegistry.RegisterAction("template archive creation", CreateTemplateArchiveActionFactory.NewCreateTemplateArchiveAction);
        // Note: "create container" is not registered here - it's called directly with plan parameter
        // Note: "setup kubernetes" is not registered here - it's called directly in DeployCommand
        ActionRegistry.RegisterAction("k3s installation", InstallK3sActionFactory.NewInstallK3sAction);
        ActionRegistry.RegisterAction("glusterfs server installation", InstallGlusterfsActionFactory.NewInstallGlusterfsAction);
        ActionRegistry.RegisterAction("install k3s node watcher", InstallK3sNodeWatcherActionFactory.NewInstallK3sNodeWatcherAction);
        ActionRegistry.RegisterAction("install argocd", InstallArgoCDActionFactory.NewInstallArgoCDAction);
        ActionRegistry.RegisterAction("install argocd apps", InstallArgoCDAppsActionFactory.NewInstallArgoCDAppsAction);
        ActionRegistry.RegisterAction("create github runner secret", CreateGithubRunnerSecretActionFactory.NewCreateGithubRunnerSecretAction);
        ActionRegistry.RegisterAction("update haproxy configuration", UpdateHaproxyConfigurationActionFactory.NewUpdateHaproxyConfigurationAction);
        ActionRegistry.RegisterAction("install dnsdist", InstallDnsdistActionFactory.NewInstallDnsdistAction);
        ActionRegistry.RegisterAction("install metallb", InstallMetalLBActionFactory.NewInstallMetalLBAction);
    }
}
