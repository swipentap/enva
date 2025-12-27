package actions

import (
	"enva/libs"
	"enva/services"
)

func init() {
	// Register all actions
	RegisterAction("cloud-init wait", NewCloudInitWaitAction)
	RegisterAction("apparmor parser stub", NewAppArmorParserStubAction)
	RegisterAction("disable automatic apt units", NewDisableAptUnitsAction)
	RegisterAction("systemd sysctl override", NewSysctlOverrideAction)
	RegisterAction("system upgrade", NewSystemUpgradeAction)
	RegisterAction("apt-cacher-ng installation", NewInstallAptCacherAction)
	RegisterAction("apt-cacher-ng port configuration", NewConfigureCachePortAction)
	RegisterAction("apt-cacher-ng service enablement", NewEnableCacheServiceAction)
	RegisterAction("haproxy installation", NewInstallHaproxyAction)
	RegisterAction("haproxy configuration", NewConfigureHaproxyAction)
	RegisterAction("haproxy systemd override", NewConfigureHaproxySystemdAction)
	RegisterAction("haproxy service enablement", NewEnableHaproxyServiceAction)
	RegisterAction("postgresql installation", NewInstallPostgresqlAction)
	RegisterAction("postgresql service configuration", NewConfigurePostgresServiceAction)
	RegisterAction("postgresql files configuration", NewConfigurePostgresFilesAction)
	RegisterAction("postgresql password setup", NewSetPostgresPasswordAction)
	RegisterAction("dotnet installation", NewInstallDotnetAction)
	RegisterAction("sins dns installation", NewInstallSinsDnsAction)
	RegisterAction("sins dns service configuration", NewConfigureSinsServiceAction)
	RegisterAction("sins dns service enablement", NewEnableSinsServiceAction)
	RegisterAction("configure dns records", NewConfigureDnsRecordsAction)
	RegisterAction("disable systemd resolved", NewDisableSystemdResolvedAction)
	RegisterAction("docker installation", NewInstallDockerAction)
	RegisterAction("docker service start", NewStartDockerServiceAction)
	RegisterAction("docker sysctl configuration", NewConfigureDockerSysctlAction)
	RegisterAction("lxc sysctl access configuration", NewConfigureLxcSysctlAccessAction)
	RegisterAction("apt cache proxy configuration", NewConfigureAptProxyAction)
	RegisterAction("apt sources fix", NewFixAptSourcesAction)
	RegisterAction("openssh-server installation", NewInstallOpensshServerAction)
	RegisterAction("SSH service enablement", NewEnableSshServiceAction)
	RegisterAction("base tools installation", NewInstallBaseToolsAction)
	RegisterAction("template cleanup", NewTemplateCleanupAction)
	RegisterAction("template archive creation", NewCreateTemplateArchiveAction)
	RegisterAction("wait apt-cache ready", NewWaitAptCacheReadyAction)
	// Note: "create container" is not registered here - it's called directly with plan parameter
	RegisterAction("setup kubernetes", NewSetupKubernetesAction)
	RegisterAction("k3s installation", NewInstallK3sAction)
	RegisterAction("glusterfs server installation", NewInstallGlusterfsAction)
	RegisterAction("install cockroachdb", NewInstallCockroachdbAction)
	RegisterAction("install github runner", NewInstallGithubRunnerAction)
	RegisterAction("install certa", NewInstallCertAAction)
	RegisterAction("install k3s node watcher", NewInstallK3sNodeWatcherAction)
}

// ActionFactory type alias for clarity
type ActionFactory func(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action
