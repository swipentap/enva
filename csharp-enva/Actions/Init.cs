using Enva.Services;

namespace Enva.Actions;

public static class ActionInit
{
    public static void Initialize()
    {
        // Register all actions
        ActionRegistry.RegisterAction("cloud-init wait", CloudInitWaitActionFactory.NewCloudInitWaitAction);
        ActionRegistry.RegisterAction("apparmor parser stub", AppArmorParserStubActionFactory.NewAppArmorParserStubAction);
        ActionRegistry.RegisterAction("disable automatic apt units", DisableAptUnitsActionFactory.NewDisableAptUnitsAction);
        ActionRegistry.RegisterAction("systemd sysctl override", SysctlOverrideActionFactory.NewSysctlOverrideAction);
        ActionRegistry.RegisterAction("system upgrade", SystemUpgradeActionFactory.NewSystemUpgradeAction);
        ActionRegistry.RegisterAction("apt-cacher-ng installation", InstallAptCacherActionFactory.NewInstallAptCacherAction);
        ActionRegistry.RegisterAction("apt-cacher-ng port configuration", ConfigureCachePortActionFactory.NewConfigureCachePortAction);
        ActionRegistry.RegisterAction("apt-cacher-ng service enablement", EnableCacheServiceActionFactory.NewEnableCacheServiceAction);
        ActionRegistry.RegisterAction("haproxy installation", InstallHaproxyActionFactory.NewInstallHaproxyAction);
        ActionRegistry.RegisterAction("haproxy configuration", ConfigureHaproxyActionFactory.NewConfigureHaproxyAction);
        ActionRegistry.RegisterAction("haproxy systemd override", ConfigureHaproxySystemdActionFactory.NewConfigureHaproxySystemdAction);
        ActionRegistry.RegisterAction("haproxy service enablement", EnableHaproxyServiceActionFactory.NewEnableHaproxyServiceAction);
        ActionRegistry.RegisterAction("postgresql installation", InstallPostgresqlActionFactory.NewInstallPostgresqlAction);
        ActionRegistry.RegisterAction("postgresql service configuration", ConfigurePostgresServiceActionFactory.NewConfigurePostgresServiceAction);
        ActionRegistry.RegisterAction("postgresql files configuration", ConfigurePostgresFilesActionFactory.NewConfigurePostgresFilesAction);
        ActionRegistry.RegisterAction("postgresql password setup", SetPostgresPasswordActionFactory.NewSetPostgresPasswordAction);
        ActionRegistry.RegisterAction("dotnet installation", InstallDotnetActionFactory.NewInstallDotnetAction);
        ActionRegistry.RegisterAction("sins dns installation", InstallSinsDnsActionFactory.NewInstallSinsDnsAction);
        ActionRegistry.RegisterAction("sins dns service configuration", ConfigureSinsServiceActionFactory.NewConfigureSinsServiceAction);
        ActionRegistry.RegisterAction("sins dns service enablement", EnableSinsServiceActionFactory.NewEnableSinsServiceAction);
        ActionRegistry.RegisterAction("configure dns records", ConfigureDnsRecordsActionFactory.NewConfigureDnsRecordsAction);
        ActionRegistry.RegisterAction("disable systemd resolved", DisableSystemdResolvedActionFactory.NewDisableSystemdResolvedAction);
        ActionRegistry.RegisterAction("docker installation", InstallDockerActionFactory.NewInstallDockerAction);
        ActionRegistry.RegisterAction("docker service start", StartDockerServiceActionFactory.NewStartDockerServiceAction);
        ActionRegistry.RegisterAction("docker sysctl configuration", ConfigureDockerSysctlActionFactory.NewConfigureDockerSysctlAction);
        ActionRegistry.RegisterAction("lxc sysctl access configuration", ConfigureLxcSysctlAccessActionFactory.NewConfigureLxcSysctlAccessAction);
        ActionRegistry.RegisterAction("apt cache proxy configuration", ConfigureAptProxyActionFactory.NewConfigureAptProxyAction);
        ActionRegistry.RegisterAction("apt sources fix", FixAptSourcesActionFactory.NewFixAptSourcesAction);
        ActionRegistry.RegisterAction("openssh-server installation", InstallOpensshServerActionFactory.NewInstallOpensshServerAction);
        ActionRegistry.RegisterAction("SSH service enablement", EnableSshServiceActionFactory.NewEnableSshServiceAction);
        ActionRegistry.RegisterAction("base tools installation", InstallBaseToolsActionFactory.NewInstallBaseToolsAction);
        ActionRegistry.RegisterAction("locale configuration", LocaleConfigurationActionFactory.NewLocaleConfigurationAction);
        ActionRegistry.RegisterAction("template cleanup", TemplateCleanupActionFactory.NewTemplateCleanupAction);
        ActionRegistry.RegisterAction("template archive creation", CreateTemplateArchiveActionFactory.NewCreateTemplateArchiveAction);
        ActionRegistry.RegisterAction("wait apt-cache ready", WaitAptCacheReadyActionFactory.NewWaitAptCacheReadyAction);
        // Note: "create container" is not registered here - it's called directly with plan parameter
        ActionRegistry.RegisterAction("setup kubernetes", SetupKubernetesActionFactory.NewSetupKubernetesAction);
        ActionRegistry.RegisterAction("k3s installation", InstallK3sActionFactory.NewInstallK3sAction);
        ActionRegistry.RegisterAction("glusterfs server installation", InstallGlusterfsActionFactory.NewInstallGlusterfsAction);
        ActionRegistry.RegisterAction("install cockroachdb", InstallCockroachdbActionFactory.NewInstallCockroachdbAction);
        ActionRegistry.RegisterAction("install sonarqube", InstallSonarqubeActionFactory.NewInstallSonarqubeAction);
        ActionRegistry.RegisterAction("install github runner", InstallGithubRunnerActionFactory.NewInstallGithubRunnerAction);
        ActionRegistry.RegisterAction("install certa", InstallCertAActionFactory.NewInstallCertAAction);
        ActionRegistry.RegisterAction("install k3s node watcher", InstallK3sNodeWatcherActionFactory.NewInstallK3sNodeWatcherAction);
        ActionRegistry.RegisterAction("install rancher", InstallRancherActionFactory.NewInstallRancherAction);
        ActionRegistry.RegisterAction("install argocd", InstallArgoCDActionFactory.NewInstallArgoCDAction);
        ActionRegistry.RegisterAction("install argocd apps", InstallArgoCDAppsActionFactory.NewInstallArgoCDAppsAction);
        ActionRegistry.RegisterAction("update haproxy configuration", UpdateHaproxyConfigurationActionFactory.NewUpdateHaproxyConfigurationAction);
    }
}
