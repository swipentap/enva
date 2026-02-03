using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Enva.CLI;
using Enva.Libs;
using Enva.Services;

namespace Enva.Orchestration;

public class NodeInfo
{
    public int ContainerID { get; set; }
    public string Hostname { get; set; } = "";
    public string IPAddress { get; set; } = "";
}

public static class Gluster
{
    public static bool SetupGlusterFS(LabConfig cfg)
    {
        var logger = Logger.GetLogger("gluster");
        logger.Printf("[5/7] Setting up GlusterFS distributed storage...");
        if (cfg.GlusterFS == null)
        {
            logger.Printf("GlusterFS configuration not found, skipping...");
            return true;
        }
        var glusterCfg = cfg.GlusterFS;
        var (manager, workers) = CollectGlusterNodes(cfg);
        if (manager == null || workers == null || workers.Count == 0)
        {
            return false;
        }
        var allNodes = new List<NodeInfo> { manager };
        allNodes.AddRange(workers);
        var (aptCacheIP, aptCachePort) = GetAPTCacheProxy(cfg);
        logger.Printf("Installing GlusterFS server on all nodes...");
        bool failureDetected = false;
        if (!FixAPTSources(allNodes, cfg))
        {
            failureDetected = true;
        }
        if (!failureDetected && !InstallGlusterPackages(allNodes, aptCacheIP, aptCachePort, cfg))
        {
            failureDetected = true;
        }
        if (!failureDetected)
        {
            Thread.Sleep(cfg.Waits.GlusterFSSetup * 1000);
        }
        if (!failureDetected && !CreateBricks(allNodes, glusterCfg.BrickPath, cfg))
        {
            failureDetected = true;
        }
        string glusterCmd = "";
        if (!failureDetected)
        {
            glusterCmd = ResolveGlusterCmd(manager, cfg);
            if (string.IsNullOrEmpty(glusterCmd))
            {
                failureDetected = true;
            }
        }
        if (!failureDetected && !PeerWorkers(manager, workers, glusterCmd, cfg))
        {
            failureDetected = true;
        }
        if (!failureDetected)
        {
            bool peersReady = WaitForPeers(manager, workers, glusterCmd, cfg);
            if (!peersReady)
            {
                logger.Printf("Not all peers may be fully connected, continuing anyway...");
            }
        }
        if (!failureDetected && !EnsureVolume(manager, workers, glusterCmd, glusterCfg, cfg))
        {
            failureDetected = true;
        }
        if (!failureDetected && !MountGlusterVolume(manager, workers, glusterCfg, cfg))
        {
            failureDetected = true;
        }
        if (!failureDetected)
        {
            bool clientMountResult = MountGlusterOnClients(manager, glusterCfg, cfg);
            if (!clientMountResult)
            {
                failureDetected = true;
            }
        }
        if (failureDetected)
        {
            return false;
        }
        LogGlusterSummary(glusterCfg);
        return true;
    }

    private static (NodeInfo?, List<NodeInfo>?) CollectGlusterNodes(LabConfig cfg)
    {
        if (cfg.GlusterFS == null)
        {
            return (null, null);
        }
        if (cfg.GlusterFS.ClusterNodes == null || cfg.GlusterFS.ClusterNodes.Count == 0)
        {
            Logger.GetLogger("gluster").Printf("GlusterFS cluster_nodes configuration not found");
            return (null, null);
        }
        var clusterNodeIDs = new HashSet<int>();
        foreach (var node in cfg.GlusterFS.ClusterNodes)
        {
            clusterNodeIDs.Add(node.ID);
        }
        var glusterNodes = new List<NodeInfo>();
        foreach (var container in cfg.Containers)
        {
            if (clusterNodeIDs.Contains(container.ID))
            {
                glusterNodes.Add(FromContainer(container));
            }
        }
        if (glusterNodes.Count < 2)
        {
            Logger.GetLogger("gluster").Printf("Need at least 2 GlusterFS cluster nodes, found {0}", glusterNodes.Count);
            return (null, null);
        }
        return (glusterNodes[0], glusterNodes.Skip(1).ToList());
    }

    private static NodeInfo FromContainer(ContainerConfig containerCfg)
    {
        return new NodeInfo
        {
            ContainerID = containerCfg.ID,
            Hostname = containerCfg.Hostname,
            IPAddress = containerCfg.IPAddress ?? ""
        };
    }

    private static (string?, string?) GetAPTCacheProxy(LabConfig cfg)
    {
        ContainerConfig? aptCache = null;
        foreach (var container in cfg.Containers)
        {
            if (container.Name == cfg.APTCacheCT)
            {
                aptCache = container;
                break;
            }
        }
        if (aptCache == null || string.IsNullOrEmpty(aptCache.IPAddress))
        {
            return (null, null);
        }
        int port = cfg.APTCachePort();
        return (aptCache.IPAddress, port.ToString());
    }

    private static bool FixAPTSources(List<NodeInfo> nodes, LabConfig cfg)
    {
        string lxcHost = cfg.LXCHost();
        var lxcService = new LXCService(lxcHost, cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }
        try
        {
            var pctService = new PCTService(lxcService);
            foreach (var node in nodes)
            {
                FixAPTSourcesForNode(node, cfg, pctService);
            }
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }

    private static void FixAPTSourcesForNode(NodeInfo node, LabConfig cfg, PCTService pctService)
    {
        string sourcesCmd = "sed -i 's/oracular/plucky/g' /etc/apt/sources.list || true; if ! grep -q '^deb.*plucky.*main' /etc/apt/sources.list; then echo 'deb http://archive.ubuntu.com/ubuntu plucky main universe multiverse' > /etc/apt/sources.list; echo 'deb http://archive.ubuntu.com/ubuntu plucky-updates main universe multiverse' >> /etc/apt/sources.list; echo 'deb http://archive.ubuntu.com/ubuntu plucky-security main universe multiverse' >> /etc/apt/sources.list; fi";
        var (sourcesResult, _) = pctService.Execute(node.ContainerID, sourcesCmd, null);
        if (sourcesResult.ToLower().Contains("error"))
        {
            int outputLen = sourcesResult.Length;
            int start = outputLen > 200 ? outputLen - 200 : 0;
            Logger.GetLogger("gluster").Printf("Apt sources fix had issues on {0}: {1}", node.Hostname, sourcesResult.Substring(start));
        }
    }

    private static bool InstallGlusterPackages(List<NodeInfo> nodes, string? aptCacheIP, string? aptCachePort, LabConfig cfg)
    {
        foreach (var node in nodes)
        {
            Logger.GetLogger("gluster").Printf("Installing on {0}...", node.Hostname);
            if (!ConfigureGlusterNode(node, aptCacheIP, aptCachePort, cfg))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ConfigureGlusterNode(NodeInfo node, string? aptCacheIP, string? aptCachePort, LabConfig cfg)
    {
        int maxRetries = 2;
        string lxcHost = cfg.LXCHost();
        var lxcService = new LXCService(lxcHost, cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }
        try
        {
            var pctService = new PCTService(lxcService);
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                ConfigureProxy(node.ContainerID, attempt == 1, aptCacheIP, aptCachePort, cfg, pctService);
                string updateCmd = Apt.NewApt().Quiet().Update();
                int timeout = 600;
                var (updateOutput, _) = pctService.Execute(node.ContainerID, updateCmd, timeout);
                if (ShouldRetryUpdate(updateOutput) && attempt < maxRetries)
                {
                    Logger.GetLogger("gluster").Printf("apt update failed, will retry without proxy...");
                    continue;
                }
                string installCmd = Apt.NewApt().Quiet().Install(new[] { "glusterfs-server", "glusterfs-client" });
                timeout = 300;
                pctService.Execute(node.ContainerID, installCmd, timeout);
                string verifyCmd = CLI.Gluster.NewGluster().IsInstalledCheck();
                timeout = 10;
                var (verifyOutput, _) = pctService.Execute(node.ContainerID, verifyCmd, timeout);
                if (CLI.Gluster.ParseGlusterIsInstalled(verifyOutput))
                {
                    Logger.GetLogger("gluster").Printf("GlusterFS installed successfully on {0}", node.Hostname);
                    return EnsureGlusterdRunning(node, cfg, pctService);
                }
                if (attempt < maxRetries)
                {
                    Logger.GetLogger("gluster").Printf("Retrying without proxy...");
                    Thread.Sleep(2000);
                }
            }
            Logger.GetLogger("gluster").Printf("Failed to install GlusterFS on {0} after {1} attempts", node.Hostname, maxRetries);
            return false;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }

    private static void ConfigureProxy(int containerID, bool useProxy, string? aptCacheIP, string? aptCachePort, LabConfig cfg, PCTService pctService)
    {
        if (useProxy && !string.IsNullOrEmpty(aptCacheIP) && !string.IsNullOrEmpty(aptCachePort))
        {
            string proxyCmd = $"echo 'Acquire::http::Proxy \"http://{aptCacheIP}:{aptCachePort}\";' > /etc/apt/apt.conf.d/01proxy || true";
            int timeout = 10;
            var (proxyResult, _) = pctService.Execute(containerID, proxyCmd, timeout);
            if (proxyResult.ToLower().Contains("error"))
            {
                int outputLen = proxyResult.Length;
                int start = outputLen > 200 ? outputLen - 200 : 0;
                Logger.GetLogger("gluster").Printf("Proxy configuration had issues: {0}", proxyResult.Substring(start));
            }
        }
        else
        {
            var (rmProxyResult, _) = pctService.Execute(containerID, "rm -f /etc/apt/apt.conf.d/01proxy", 10);
            if (rmProxyResult.ToLower().Contains("error"))
            {
                int outputLen = rmProxyResult.Length;
                int start = outputLen > 200 ? outputLen - 200 : 0;
                Logger.GetLogger("gluster").Printf("Proxy removal had issues: {0}", rmProxyResult.Substring(start));
            }
        }
    }

    private static bool ShouldRetryUpdate(string updateOutput)
    {
        return updateOutput.Contains("Failed to fetch") || updateOutput.Contains("Unable to connect");
    }

    private static bool EnsureGlusterdRunning(NodeInfo node, LabConfig cfg, PCTService pctService)
    {
        Logger.GetLogger("gluster").Printf("Starting glusterd service on {0}...", node.Hostname);
        string enableCmd = SystemCtl.NewSystemCtl().Service("glusterd").Enable();
        string startCmd = SystemCtl.NewSystemCtl().Service("glusterd").Start();
        int timeout = 30;
        var (enableOutput, _) = pctService.Execute(node.ContainerID, enableCmd, timeout);
        var (glusterdStartOutput, _) = pctService.Execute(node.ContainerID, startCmd, timeout);
        if (enableOutput.ToLower().Contains("error"))
        {
            Logger.GetLogger("gluster").Printf("Failed to enable glusterd on {0}: {1}", node.Hostname, enableOutput);
        }
        if (glusterdStartOutput.ToLower().Contains("error"))
        {
            Logger.GetLogger("gluster").Printf("Failed to start glusterd on {0}: {1}", node.Hostname, glusterdStartOutput);
            return false;
        }
        Thread.Sleep(3000);
        string isActiveCmd = SystemCtl.NewSystemCtl().Service("glusterd").IsActive();
        timeout = 10;
        var (glusterdCheckOutput, _) = pctService.Execute(node.ContainerID, isActiveCmd, timeout);
        if (SystemCtl.ParseIsActive(glusterdCheckOutput))
        {
            Logger.GetLogger("gluster").Printf("{0}: GlusterFS installed and glusterd running", node.Hostname);
            return true;
        }
        Logger.GetLogger("gluster").Printf("{0}: GlusterFS installed but glusterd is not running: {1}", node.Hostname, glusterdCheckOutput);
        return false;
    }

    private static bool CreateBricks(List<NodeInfo> nodes, string brickPath, LabConfig cfg)
    {
        Logger.GetLogger("gluster").Printf("Creating brick directories on all nodes...");
        string lxcHost = cfg.LXCHost();
        var lxcService = new LXCService(lxcHost, cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }
        try
        {
            var pctService = new PCTService(lxcService);
            foreach (var node in nodes)
            {
                Logger.GetLogger("gluster").Printf("Creating brick on {0}...", node.Hostname);
                string brickCmd = $"mkdir -p {brickPath} && chmod 755 {brickPath}";
                var (brickResult, _) = pctService.Execute(node.ContainerID, brickCmd, null);
                if (brickResult.ToLower().Contains("error"))
                {
                    int outputLen = brickResult.Length;
                    int start = outputLen > 300 ? outputLen - 300 : 0;
                    Logger.GetLogger("gluster").Printf("Failed to create brick directory on {0}: {1}", node.Hostname, brickResult.Substring(start));
                    return false;
                }
            }
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }

    private static string ResolveGlusterCmd(NodeInfo manager, LabConfig cfg)
    {
        var lxcService = new LXCService(cfg.LXCHost(), cfg.SSH);
        if (!lxcService.Connect())
        {
            return "";
        }
        try
        {
            var pctService = new PCTService(lxcService);
            string findGlusterCmd = CLI.Gluster.NewGluster().FindGluster();
            int timeout = 10;
            var (glusterPath, _) = pctService.Execute(manager.ContainerID, findGlusterCmd, timeout);
            if (string.IsNullOrEmpty(glusterPath))
            {
                Logger.GetLogger("gluster").Printf("Unable to locate gluster binary");
                return "";
            }
            if (!string.IsNullOrEmpty(glusterPath) && !string.IsNullOrWhiteSpace(glusterPath))
            {
                string[] lines = glusterPath.Trim().Split('\n');
                if (lines.Length > 0)
                {
                    string firstLine = lines[0].Trim();
                    if (!string.IsNullOrEmpty(firstLine))
                    {
                        return firstLine;
                    }
                }
                return "gluster";
            }
            return "gluster";
        }
        finally
        {
            lxcService.Disconnect();
        }
    }

    private static bool PeerWorkers(NodeInfo manager, List<NodeInfo> workers, string glusterCmd, LabConfig cfg)
    {
        Logger.GetLogger("gluster").Printf("Peering worker nodes together...");
        string lxcHost = cfg.LXCHost();
        var lxcService = new LXCService(lxcHost, cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }
        try
        {
            var pctService = new PCTService(lxcService);
            foreach (var worker in workers)
            {
                Logger.GetLogger("gluster").Printf("Adding {0} ({1}) to cluster...", worker.Hostname, worker.IPAddress);
                string probeCmd = $"{CLI.Gluster.NewGluster().GlusterCmd(glusterCmd).PeerProbe(worker.Hostname)} || {CLI.Gluster.NewGluster().GlusterCmd(glusterCmd).PeerProbe(worker.IPAddress)}";
                var (probeOutput, _) = pctService.Execute(manager.ContainerID, probeCmd, null);
                if (probeOutput.ToLower().Contains("error") && !probeOutput.ToLower().Contains("already") && !probeOutput.ToLower().Contains("already in peer list"))
                {
                    Logger.GetLogger("gluster").Printf("Peer probe had issues for {0}: {1}", worker.Hostname, probeOutput);
                }
            }
            Thread.Sleep(10000);
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }

    private static bool WaitForPeers(NodeInfo manager, List<NodeInfo> workers, string glusterCmd, LabConfig cfg)
    {
        Logger.GetLogger("gluster").Printf("Verifying peer status...");
        string lxcHost = cfg.LXCHost();
        var lxcService = new LXCService(lxcHost, cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }
        try
        {
            var pctService = new PCTService(lxcService);
            int maxPeerAttempts = 10;
            for (int attempt = 1; attempt <= maxPeerAttempts; attempt++)
            {
                string peerStatusCmd = CLI.Gluster.NewGluster().GlusterCmd(glusterCmd).PeerStatus();
                var (peerStatus, _) = pctService.Execute(manager.ContainerID, peerStatusCmd, null);
                if (string.IsNullOrEmpty(peerStatus))
                {
                    if (attempt < maxPeerAttempts)
                    {
                        Logger.GetLogger("gluster").Printf("Waiting for peers to connect... ({0}/{1})", attempt, maxPeerAttempts);
                        Thread.Sleep(3000);
                        continue;
                    }
                    return false;
                }
                Logger.GetLogger("gluster").Printf(peerStatus);
                int connectedCount = peerStatus.Split(new[] { "Peer in Cluster (Connected)" }, StringSplitOptions.None).Length - 1;
                if (connectedCount >= workers.Count)
                {
                    Logger.GetLogger("gluster").Printf("All {0} worker peers connected", connectedCount);
                    return true;
                }
                if (attempt < maxPeerAttempts)
                {
                    Logger.GetLogger("gluster").Printf("Waiting for peers to connect... ({0}/{1})", attempt, maxPeerAttempts);
                    Thread.Sleep(3000);
                }
            }
            return false;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }

    private static bool EnsureVolume(NodeInfo manager, List<NodeInfo> workers, string glusterCmd, GlusterFSConfig glusterCfg, LabConfig cfg)
    {
        string lxcHost = cfg.LXCHost();
        string volumeName = glusterCfg.VolumeName;
        string brickPath = glusterCfg.BrickPath;
        int replicaCount = glusterCfg.ReplicaCount;
        var lxcService = new LXCService(lxcHost, cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }
        try
        {
            var pctService = new PCTService(lxcService);
            Logger.GetLogger("gluster").Printf("Creating GlusterFS volume '{0}'...", volumeName);
            string volumeExistsCmd = CLI.Gluster.NewGluster().GlusterCmd(glusterCmd).VolumeExistsCheck(volumeName);
            var (volumeExistsOutput, _) = pctService.Execute(manager.ContainerID, volumeExistsCmd, null);
            if (CLI.Gluster.ParseVolumeExists(volumeExistsOutput))
            {
                Logger.GetLogger("gluster").Printf("Volume '{0}' already exists", volumeName);
                return true;
            }
            var allNodes = new List<NodeInfo> { manager };
            allNodes.AddRange(workers);
            var brickList = new List<string>();
            foreach (var node in allNodes)
            {
                brickList.Add($"{node.IPAddress}:{brickPath}");
            }
            string createCmd = CLI.Gluster.NewGluster().GlusterCmd(glusterCmd).Force(true).VolumeCreate(volumeName, replicaCount, brickList);
            var (createOutput, _) = pctService.Execute(manager.ContainerID, createCmd, null);
            Logger.GetLogger("gluster").Printf(createOutput);
            if (!createOutput.ToLower().Contains("created") && !createOutput.ToLower().Contains("success"))
            {
                Logger.GetLogger("gluster").Printf("Volume creation failed: {0}", createOutput);
                return false;
            }
            Logger.GetLogger("gluster").Printf("Starting volume '{0}'...", volumeName);
            string startCmd = CLI.Gluster.NewGluster().GlusterCmd(glusterCmd).VolumeStart(volumeName);
            var (startOutput, _) = pctService.Execute(manager.ContainerID, startCmd, null);
            Logger.GetLogger("gluster").Printf(startOutput);
            Logger.GetLogger("gluster").Printf("Verifying volume status...");
            string volStatusCmd = CLI.Gluster.NewGluster().GlusterCmd(glusterCmd).VolumeStatus(volumeName);
            var (volStatus, _) = pctService.Execute(manager.ContainerID, volStatusCmd, null);
            if (!string.IsNullOrEmpty(volStatus))
            {
                Logger.GetLogger("gluster").Printf(volStatus);
            }
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }

    private static bool MountGlusterVolume(NodeInfo manager, List<NodeInfo> workers, GlusterFSConfig glusterCfg, LabConfig cfg)
    {
        var nodes = new List<NodeInfo> { manager };
        nodes.AddRange(workers);
        string volumeName = glusterCfg.VolumeName;
        string mountPoint = glusterCfg.MountPoint;
        string lxcHost = cfg.LXCHost();
        var lxcService = new LXCService(lxcHost, cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }
        try
        {
            var pctService = new PCTService(lxcService);
            Logger.GetLogger("gluster").Printf("Mounting GlusterFS volume on all nodes...");
            foreach (var node in nodes)
            {
                Logger.GetLogger("gluster").Printf("Mounting on {0}...", node.Hostname);
                string mkdirCmd = $"mkdir -p {mountPoint}";
                var (mkdirResult, _) = pctService.Execute(node.ContainerID, mkdirCmd, null);
                if (mkdirResult.ToLower().Contains("error"))
                {
                    int outputLen = mkdirResult.Length;
                    int start = outputLen > 300 ? outputLen - 300 : 0;
                    Logger.GetLogger("gluster").Printf("Failed to create mount point on {0}: {1}", node.Hostname, mkdirResult.Substring(start));
                    return false;
                }
                string fstabEntry = $"{manager.Hostname}:/{volumeName} {mountPoint} glusterfs defaults,_netdev 0 0";
                string fstabCmd = $"grep -q '{mountPoint}' /etc/fstab || echo '{fstabEntry}' >> /etc/fstab";
                var (fstabResult, _) = pctService.Execute(node.ContainerID, fstabCmd, null);
                if (fstabResult.ToLower().Contains("error"))
                {
                    int outputLen = fstabResult.Length;
                    int start = outputLen > 200 ? outputLen - 200 : 0;
                    Logger.GetLogger("gluster").Printf("fstab update had issues on {0}: {1}", node.Hostname, fstabResult.Substring(start));
                }
                string mountCmd = $"/usr/sbin/mount.glusterfs {manager.Hostname}:/{volumeName} {mountPoint} || /usr/sbin/mount.glusterfs {manager.IPAddress}:/{volumeName} {mountPoint}";
                var (mountResult, _) = pctService.Execute(node.ContainerID, mountCmd, null);
                if (mountResult.ToLower().Contains("error") && !mountResult.ToLower().Contains("already mounted"))
                {
                    int outputLen = mountResult.Length;
                    int start = outputLen > 300 ? outputLen - 300 : 0;
                    Logger.GetLogger("gluster").Printf("Failed to mount GlusterFS on {0}: {1}", node.Hostname, mountResult.Substring(start));
                    return false;
                }
                if (!VerifyMount(node, mountPoint, cfg, pctService))
                {
                    return false;
                }
            }
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }

    private static bool VerifyMount(NodeInfo node, string mountPoint, LabConfig cfg, PCTService pctService)
    {
        string mountVerifyCmd = $"mount | grep -q '{mountPoint}' && mount | grep '{mountPoint}' | grep -q gluster && echo mounted || echo not_mounted";
        var (mountVerify, _) = pctService.Execute(node.ContainerID, mountVerifyCmd, null);
        if (mountVerify.Contains("mounted") && !mountVerify.Contains("not_mounted"))
        {
            Logger.GetLogger("gluster").Printf("{0}: Volume mounted successfully", node.Hostname);
            return true;
        }
        string mountInfoCmd = $"mount | grep {mountPoint} || echo 'NOT_MOUNTED'";
        var (mountInfo, _) = pctService.Execute(node.ContainerID, mountInfoCmd, null);
        if (mountInfo.Contains("NOT_MOUNTED") || string.IsNullOrEmpty(mountInfo))
        {
            Logger.GetLogger("gluster").Printf("{0}: Mount failed - volume not mounted", node.Hostname);
            return false;
        }
        int outputLen = mountInfo.Length;
        int start = outputLen > 80 ? outputLen - 80 : 0;
        Logger.GetLogger("gluster").Printf("{0}: Mount status unclear - {1}", node.Hostname, mountInfo.Substring(start));
        return true;
    }

    private static bool MountGlusterOnClients(NodeInfo manager, GlusterFSConfig glusterCfg, LabConfig cfg)
    {
        string lxcHost = cfg.LXCHost();
        string volumeName = glusterCfg.VolumeName;
        string mountPoint = glusterCfg.MountPoint;
        var clientNodes = new List<ContainerConfig>();
        if (cfg.Kubernetes != null)
        {
            if (cfg.Kubernetes.Control != null)
            {
                foreach (var container in cfg.Containers)
                {
                    foreach (var id in cfg.Kubernetes.Control)
                    {
                        if (container.ID == id)
                        {
                            clientNodes.Add(container);
                            break;
                        }
                    }
                }
            }
            if (cfg.Kubernetes.Workers != null)
            {
                foreach (var container in cfg.Containers)
                {
                    foreach (var id in cfg.Kubernetes.Workers)
                    {
                        if (container.ID == id)
                        {
                            clientNodes.Add(container);
                            break;
                        }
                    }
                }
            }
        }
        var seen = new HashSet<int>();
        var uniqueClientNodes = new List<ContainerConfig>();
        foreach (var node in clientNodes)
        {
            if (!seen.Contains(node.ID))
            {
                seen.Add(node.ID);
                uniqueClientNodes.Add(node);
            }
        }
        clientNodes = uniqueClientNodes;
        if (clientNodes.Count == 0)
        {
            Logger.GetLogger("gluster").Printf("No K3s nodes found for GlusterFS client mounting");
            return true;
        }
        var lxcService = new LXCService(lxcHost, cfg.SSH);
        if (!lxcService.Connect())
        {
            Logger.GetLogger("gluster").Printf("Failed to connect to LXC host for client mounting");
            return false;
        }
        try
        {
            var pctService = new PCTService(lxcService);
            Logger.GetLogger("gluster").Printf("Mounting GlusterFS volume on {0} client nodes...", clientNodes.Count);
            var installationFailed = new List<string>();
            foreach (var node in clientNodes)
            {
                Logger.GetLogger("gluster").Printf("Installing glusterfs-client on {0}...", node.Hostname);
                string verifyCmd = "test -x /usr/sbin/mount.glusterfs && echo installed || echo not_installed";
                int timeout = 10;
                var (verifyOutput, _) = pctService.Execute(node.ID, verifyCmd, timeout);
                if (!string.IsNullOrEmpty(verifyOutput) && verifyOutput.Trim() == "installed")
                {
                    Logger.GetLogger("gluster").Printf("glusterfs-client already installed on {0}", node.Hostname);
                    continue;
                }
                FixAPTSourcesForNode(FromContainer(node), cfg, pctService);
                string updateCmd = Apt.NewApt().Quiet().Update();
                timeout = 600;
                var (updateOutput, updateExit) = pctService.Execute(node.ID, updateCmd, timeout);
                if (updateExit.HasValue && updateExit.Value != 0)
                {
                    int outputLen = updateOutput.Length;
                    int start = outputLen > 200 ? outputLen - 200 : 0;
                    Logger.GetLogger("gluster").Printf("Failed to update apt on {0}: {1}", node.Hostname, updateOutput.Substring(start));
                    installationFailed.Add(node.Hostname);
                    continue;
                }
                string installCmd = Apt.NewApt().Quiet().Install(new[] { "glusterfs-client" });
                timeout = 300;
                var (installOutput, exitCode) = pctService.Execute(node.ID, installCmd, timeout);
                if (exitCode.HasValue && exitCode.Value != 0)
                {
                    int outputLen = installOutput.Length;
                    int start = outputLen > 300 ? outputLen - 300 : 0;
                    Logger.GetLogger("gluster").Printf("Failed to install glusterfs-client on {0}: {1}", node.Hostname, installOutput.Substring(start));
                    installationFailed.Add(node.Hostname);
                    continue;
                }
                string verifyCmd2 = "test -x /usr/sbin/mount.glusterfs && echo installed || echo not_installed";
                timeout = 10;
                var (verifyOutput2, _) = pctService.Execute(node.ID, verifyCmd2, timeout);
                if (string.IsNullOrEmpty(verifyOutput2) || verifyOutput2.Trim() != "installed")
                {
                    Logger.GetLogger("gluster").Printf("glusterfs-client installation verification failed on {0} - /usr/sbin/mount.glusterfs not found", node.Hostname);
                    installationFailed.Add(node.Hostname);
                }
            }
            if (installationFailed.Count > 0)
            {
                Logger.GetLogger("gluster").Printf("Failed to install glusterfs-client on {0} node(s): {1}", installationFailed.Count, string.Join(", ", installationFailed));
                return false;
            }
            var mountFailed = new List<string>();
            foreach (var node in clientNodes)
            {
                Logger.GetLogger("gluster").Printf("Mounting GlusterFS on {0}...", node.Hostname);
                string mkdirCmd = $"mkdir -p {mountPoint}";
                int timeout = 10;
                var (mkdirResult, mkdirExit) = pctService.Execute(node.ID, mkdirCmd, timeout);
                if (mkdirExit.HasValue && mkdirExit.Value != 0)
                {
                    int outputLen = mkdirResult.Length;
                    int start = outputLen > 200 ? outputLen - 200 : 0;
                    Logger.GetLogger("gluster").Printf("Failed to create mount point on {0}: {1}", node.Hostname, mkdirResult.Substring(start));
                    mountFailed.Add(node.Hostname);
                    continue;
                }
                string fstabEntry = $"{manager.Hostname}:/{volumeName} {mountPoint} glusterfs defaults,_netdev 0 0";
                string fstabCmd = $"grep -q '{mountPoint}' /etc/fstab || echo '{fstabEntry}' >> /etc/fstab";
                timeout = 10;
                pctService.Execute(node.ID, fstabCmd, timeout);
                string reloadCmd = "systemctl daemon-reload";
                pctService.Execute(node.ID, reloadCmd, timeout);
                string mountCmd = $"/usr/sbin/mount.glusterfs {manager.Hostname}:/{volumeName} {mountPoint} || /usr/sbin/mount.glusterfs {manager.IPAddress}:/{volumeName} {mountPoint}";
                timeout = 30;
                var (mountResult, mountExit) = pctService.Execute(node.ID, mountCmd, timeout);
                if (mountExit.HasValue && mountExit.Value != 0)
                {
                    if (!mountResult.ToLower().Contains("already mounted"))
                    {
                        int outputLen = mountResult.Length;
                        int start = outputLen > 300 ? outputLen - 300 : 0;
                        Logger.GetLogger("gluster").Printf("Failed to mount GlusterFS on {0}: {1}", node.Hostname, mountResult.Substring(start));
                        mountFailed.Add(node.Hostname);
                    }
                    else
                    {
                        Logger.GetLogger("gluster").Printf("GlusterFS already mounted on {0}", node.Hostname);
                    }
                }
                else
                {
                    string verifyMountCmd = $"mount | grep -q '{mountPoint}' && mount | grep '{mountPoint}' | grep -q gluster && echo mounted || echo not_mounted";
                    timeout = 10;
                    var (verifyMountOutput, _) = pctService.Execute(node.ID, verifyMountCmd, timeout);
                    if (verifyMountOutput.Contains("mounted"))
                    {
                        Logger.GetLogger("gluster").Printf("GlusterFS mounted successfully on {0}", node.Hostname);
                    }
                    else
                    {
                        Logger.GetLogger("gluster").Printf("Mount verification failed on {0}", node.Hostname);
                        mountFailed.Add(node.Hostname);
                    }
                }
            }
            if (mountFailed.Count > 0)
            {
                Logger.GetLogger("gluster").Printf("Failed to mount GlusterFS on {0} node(s): {1}", mountFailed.Count, string.Join(", ", mountFailed));
                return false;
            }
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }

    private static void LogGlusterSummary(GlusterFSConfig glusterCfg)
    {
        Logger.GetLogger("gluster").Printf("GlusterFS distributed storage setup complete");
        Logger.GetLogger("gluster").Printf("  Volume: {0}", glusterCfg.VolumeName);
        Logger.GetLogger("gluster").Printf("  Mount point: {0} on all nodes", glusterCfg.MountPoint);
        Logger.GetLogger("gluster").Printf("  Replication: {0}x", glusterCfg.ReplicaCount);
    }
}
