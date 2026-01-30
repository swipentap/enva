package orchestration

import (
	"enva/cli"
	"enva/libs"
	"enva/services"
	"fmt"
	"strings"
	"time"
)

// NodeInfo represents a container needed for orchestration steps
type NodeInfo struct {
	ContainerID int
	Hostname    string
	IPAddress   string
}

// FromContainer builds node info from container configuration
func FromContainer(containerCfg *libs.ContainerConfig) *NodeInfo {
	return &NodeInfo{
		ContainerID: containerCfg.ID,
		Hostname:    containerCfg.Hostname,
		IPAddress:   *containerCfg.IPAddress,
	}
}

// SetupGlusterFS sets up GlusterFS distributed storage
func SetupGlusterFS(cfg *libs.LabConfig) bool {
	libs.GetLogger("gluster").Printf("\n[5/7] Setting up GlusterFS distributed storage...")
	if cfg.GlusterFS == nil {
		libs.GetLogger("gluster").Printf("GlusterFS configuration not found, skipping...")
		return true
	}
	glusterCfg := cfg.GlusterFS
	manager, workers := collectGlusterNodes(cfg)
	if manager == nil || len(workers) == 0 {
		return false
	}
	allNodes := append([]*NodeInfo{manager}, workers...)
	aptCacheIP, aptCachePort := getAPTCacheProxy(cfg)
	proxySettings := [2]*string{aptCacheIP, aptCachePort}
	libs.GetLogger("gluster").Printf("Installing GlusterFS server on all nodes...")
	failureDetected := false
	if !fixAPTSources(allNodes, cfg) {
		failureDetected = true
	}
	if !failureDetected && !installGlusterPackages(allNodes, proxySettings, cfg) {
		failureDetected = true
	}
	if !failureDetected {
		time.Sleep(time.Duration(cfg.Waits.GlusterFSSetup) * time.Second)
	}
	if !failureDetected && !createBricks(allNodes, glusterCfg.BrickPath, cfg) {
		failureDetected = true
	}
	glusterCmd := ""
	if !failureDetected {
		glusterCmd = resolveGlusterCmd(manager, cfg)
		if glusterCmd == "" {
			failureDetected = true
		}
	}
	if !failureDetected && !peerWorkers(manager, workers, glusterCmd, cfg) {
		failureDetected = true
	}
	if !failureDetected {
		peersReady := waitForPeers(manager, workers, glusterCmd, cfg)
		if !peersReady {
			libs.GetLogger("gluster").Printf("Not all peers may be fully connected, continuing anyway...")
		}
	}
	if !failureDetected && !ensureVolume(manager, workers, glusterCmd, glusterCfg, cfg) {
		failureDetected = true
	}
	if !failureDetected && !mountGlusterVolume(manager, workers, glusterCfg, cfg) {
		failureDetected = true
	}
	if !failureDetected {
		clientMountResult := mountGlusterOnClients(manager, glusterCfg, cfg)
		if !clientMountResult {
			failureDetected = true
		}
	}
	if failureDetected {
		return false
	}
	logGlusterSummary(glusterCfg)
	return true
}

func collectGlusterNodes(cfg *libs.LabConfig) (*NodeInfo, []*NodeInfo) {
	if cfg.GlusterFS == nil {
		return nil, nil
	}
	if cfg.GlusterFS.ClusterNodes == nil || len(cfg.GlusterFS.ClusterNodes) == 0 {
		libs.GetLogger("gluster").Printf("GlusterFS cluster_nodes configuration not found")
		return nil, nil
	}
	clusterNodeIDs := make(map[int]bool)
	for _, node := range cfg.GlusterFS.ClusterNodes {
		clusterNodeIDs[node.ID] = true
	}
	var glusterNodes []*NodeInfo
	for i := range cfg.Containers {
		if clusterNodeIDs[cfg.Containers[i].ID] {
			glusterNodes = append(glusterNodes, FromContainer(&cfg.Containers[i]))
		}
	}
	if len(glusterNodes) < 2 {
		libs.GetLogger("gluster").Printf("Need at least 2 GlusterFS cluster nodes, found %d", len(glusterNodes))
		return nil, nil
	}
	return glusterNodes[0], glusterNodes[1:]
}

func getAPTCacheProxy(cfg *libs.LabConfig) (*string, *string) {
	var aptCache *libs.ContainerConfig
	for i := range cfg.Containers {
		if cfg.Containers[i].Name == cfg.APTCacheCT {
			aptCache = &cfg.Containers[i]
			break
		}
	}
	if aptCache == nil {
		return nil, nil
	}
	if aptCache.IPAddress == nil {
		return nil, nil
	}
	port := cfg.APTCachePort()
	portStr := fmt.Sprintf("%d", port)
	return aptCache.IPAddress, &portStr
}

func fixAPTSources(nodes []*NodeInfo, cfg *libs.LabConfig) bool {
	lxcHost := cfg.LXCHost()
	lxcService := services.NewLXCService(lxcHost, &cfg.SSH)
	if !lxcService.Connect() {
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)
	for _, node := range nodes {
		fixAPTSourcesForNode(node, cfg, pctService)
	}
	return true
}

func fixAPTSourcesForNode(node *NodeInfo, cfg *libs.LabConfig, pctService *services.PCTService) {
	sourcesCmd := "sed -i 's/oracular/plucky/g' /etc/apt/sources.list || true; if ! grep -q '^deb.*plucky.*main' /etc/apt/sources.list; then echo 'deb http://archive.ubuntu.com/ubuntu plucky main universe multiverse' > /etc/apt/sources.list; echo 'deb http://archive.ubuntu.com/ubuntu plucky-updates main universe multiverse' >> /etc/apt/sources.list; echo 'deb http://archive.ubuntu.com/ubuntu plucky-security main universe multiverse' >> /etc/apt/sources.list; fi"
	sourcesResult, _ := pctService.Execute(node.ContainerID, sourcesCmd, nil)
	if strings.Contains(strings.ToLower(sourcesResult), "error") {
		libs.GetLogger("gluster").Printf("Apt sources fix had issues on %s: %s", node.Hostname, sourcesResult[len(sourcesResult)-200:])
	}
}

func installGlusterPackages(nodes []*NodeInfo, proxySettings [2]*string, cfg *libs.LabConfig) bool {
	for _, node := range nodes {
		libs.GetLogger("gluster").Printf("Installing on %s...", node.Hostname)
		if !configureGlusterNode(node, proxySettings, cfg) {
			return false
		}
	}
	return true
}

func configureGlusterNode(node *NodeInfo, proxySettings [2]*string, cfg *libs.LabConfig) bool {
	maxRetries := 2
	lxcHost := cfg.LXCHost()
	lxcService := services.NewLXCService(lxcHost, &cfg.SSH)
	if !lxcService.Connect() {
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)
	for attempt := 1; attempt <= maxRetries; attempt++ {
		configureProxy(node.ContainerID, attempt == 1, proxySettings, cfg, pctService)
		updateCmd := cli.NewApt().Update()
		timeout := 600
		updateOutput, _ := pctService.Execute(node.ContainerID, updateCmd, &timeout)
		if shouldRetryUpdate(updateOutput) && attempt < maxRetries {
			libs.GetLogger("gluster").Printf("apt update failed, will retry without proxy...")
			continue
		}
		installCmd := cli.NewApt().Install([]string{"glusterfs-server", "glusterfs-client"})
		timeout = 300
		pctService.Execute(node.ContainerID, installCmd, &timeout)
		verifyCmd := cli.NewGluster().IsInstalledCheck()
		timeout = 10
		verifyOutput, _ := pctService.Execute(node.ContainerID, verifyCmd, &timeout)
		if cli.ParseGlusterIsInstalled(verifyOutput) {
			libs.GetLogger("gluster").Printf("GlusterFS installed successfully on %s", node.Hostname)
			return ensureGlusterdRunning(node, cfg, pctService)
		}
		if attempt < maxRetries {
			libs.GetLogger("gluster").Printf("Retrying without proxy...")
			time.Sleep(2 * time.Second)
		}
	}
	libs.GetLogger("gluster").Printf("Failed to install GlusterFS on %s after %d attempts", node.Hostname, maxRetries)
	return false
}

func configureProxy(containerID int, useProxy bool, proxySettings [2]*string, cfg *libs.LabConfig, pctService *services.PCTService) {
	aptCacheIP := proxySettings[0]
	aptCachePort := proxySettings[1]
	if useProxy && aptCacheIP != nil && aptCachePort != nil {
		proxyCmd := fmt.Sprintf("echo 'Acquire::http::Proxy \"http://%s:%d\";' > /etc/apt/apt.conf.d/01proxy || true", *aptCacheIP, *aptCachePort)
		timeout := 10
		proxyResult, _ := pctService.Execute(containerID, proxyCmd, &timeout)
		if strings.Contains(strings.ToLower(proxyResult), "error") {
			outputLen := len(proxyResult)
			start := 0
			if outputLen > 200 {
				start = outputLen - 200
			}
			libs.GetLogger("gluster").Printf("Proxy configuration had issues: %s", proxyResult[start:])
		}
	} else {
		rmProxyResult, _ := pctService.Execute(containerID, "rm -f /etc/apt/apt.conf.d/01proxy", libs.IntPtr(10))
		if strings.Contains(strings.ToLower(rmProxyResult), "error") {
			outputLen := len(rmProxyResult)
			start := 0
			if outputLen > 200 {
				start = outputLen - 200
			}
			libs.GetLogger("gluster").Printf("Proxy removal had issues: %s", rmProxyResult[start:])
		}
	}
}

func shouldRetryUpdate(updateOutput string) bool {
	return strings.Contains(updateOutput, "Failed to fetch") || strings.Contains(updateOutput, "Unable to connect")
}

func ensureGlusterdRunning(node *NodeInfo, cfg *libs.LabConfig, pctService *services.PCTService) bool {
	libs.GetLogger("gluster").Printf("Starting glusterd service on %s...", node.Hostname)
	enableCmd := cli.NewSystemCtl().Service("glusterd").Enable()
	startCmd := cli.NewSystemCtl().Service("glusterd").Start()
	timeout := 30
	enableOutput, _ := pctService.Execute(node.ContainerID, enableCmd, &timeout)
	glusterdStartOutput, _ := pctService.Execute(node.ContainerID, startCmd, &timeout)
	if strings.Contains(strings.ToLower(enableOutput), "error") {
		libs.GetLogger("gluster").Printf("Failed to enable glusterd on %s: %s", node.Hostname, enableOutput)
	}
	if strings.Contains(strings.ToLower(glusterdStartOutput), "error") {
		libs.GetLogger("gluster").Printf("Failed to start glusterd on %s: %s", node.Hostname, glusterdStartOutput)
		return false
	}
	time.Sleep(3 * time.Second)
	isActiveCmd := cli.NewSystemCtl().Service("glusterd").IsActive()
	timeout = 10
	glusterdCheckOutput, _ := pctService.Execute(node.ContainerID, isActiveCmd, &timeout)
	if cli.ParseIsActive(glusterdCheckOutput) {
		libs.GetLogger("gluster").Printf("%s: GlusterFS installed and glusterd running", node.Hostname)
		return true
	}
	libs.GetLogger("gluster").Printf("%s: GlusterFS installed but glusterd is not running: %s", node.Hostname, glusterdCheckOutput)
	return false
}

func createBricks(nodes []*NodeInfo, brickPath string, cfg *libs.LabConfig) bool {
	libs.GetLogger("gluster").Printf("Creating brick directories on all nodes...")
	lxcHost := cfg.LXCHost()
	lxcService := services.NewLXCService(lxcHost, &cfg.SSH)
	if !lxcService.Connect() {
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)
	for _, node := range nodes {
		libs.GetLogger("gluster").Printf("Creating brick on %s...", node.Hostname)
		brickCmd := fmt.Sprintf("mkdir -p %s && chmod 755 %s", brickPath, brickPath)
		brickResult, _ := pctService.Execute(node.ContainerID, brickCmd, nil)
		if strings.Contains(strings.ToLower(brickResult), "error") {
			outputLen := len(brickResult)
			start := 0
			if outputLen > 300 {
				start = outputLen - 300
			}
			libs.GetLogger("gluster").Printf("Failed to create brick directory on %s: %s", node.Hostname, brickResult[start:])
			return false
		}
	}
	return true
}

func resolveGlusterCmd(manager *NodeInfo, cfg *libs.LabConfig) string {
	lxcService := services.NewLXCService(cfg.LXCHost(), &cfg.SSH)
	if !lxcService.Connect() {
		return ""
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)
	findGlusterCmd := cli.NewGluster().FindGluster()
	timeout := 10
	glusterPath, _ := pctService.Execute(manager.ContainerID, findGlusterCmd, &timeout)
	if glusterPath == "" {
		libs.GetLogger("gluster").Printf("Unable to locate gluster binary")
		return ""
	}
	if glusterPath != "" && strings.TrimSpace(glusterPath) != "" {
		lines := strings.Split(strings.TrimSpace(glusterPath), "\n")
		if len(lines) > 0 {
			firstLine := strings.TrimSpace(lines[0])
			if firstLine != "" {
				return firstLine
			}
		}
		return "gluster"
	}
	return "gluster"
}

func peerWorkers(manager *NodeInfo, workers []*NodeInfo, glusterCmd string, cfg *libs.LabConfig) bool {
	libs.GetLogger("gluster").Printf("Peering worker nodes together...")
	lxcHost := cfg.LXCHost()
	lxcService := services.NewLXCService(lxcHost, &cfg.SSH)
	if !lxcService.Connect() {
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)
	for _, worker := range workers {
		libs.GetLogger("gluster").Printf("Adding %s (%s) to cluster...", worker.Hostname, worker.IPAddress)
		probeCmd := fmt.Sprintf("%s || %s", cli.NewGluster().GlusterCmd(glusterCmd).PeerProbe(worker.Hostname), cli.NewGluster().GlusterCmd(glusterCmd).PeerProbe(worker.IPAddress))
		probeOutput, _ := pctService.Execute(manager.ContainerID, probeCmd, nil)
		if strings.Contains(strings.ToLower(probeOutput), "error") && !strings.Contains(strings.ToLower(probeOutput), "already") && !strings.Contains(strings.ToLower(probeOutput), "already in peer list") {
			libs.GetLogger("gluster").Printf("Peer probe had issues for %s: %s", worker.Hostname, probeOutput)
		}
	}
	time.Sleep(10 * time.Second)
	return true
}

func waitForPeers(manager *NodeInfo, workers []*NodeInfo, glusterCmd string, cfg *libs.LabConfig) bool {
	libs.GetLogger("gluster").Printf("Verifying peer status...")
	lxcHost := cfg.LXCHost()
	lxcService := services.NewLXCService(lxcHost, &cfg.SSH)
	if !lxcService.Connect() {
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)
	maxPeerAttempts := 10
	for attempt := 1; attempt <= maxPeerAttempts; attempt++ {
		peerStatusCmd := cli.NewGluster().GlusterCmd(glusterCmd).PeerStatus()
		peerStatus, _ := pctService.Execute(manager.ContainerID, peerStatusCmd, nil)
		if peerStatus == "" {
			if attempt < maxPeerAttempts {
				libs.GetLogger("gluster").Printf("Waiting for peers to connect... (%d/%d)", attempt, maxPeerAttempts)
				time.Sleep(3 * time.Second)
				continue
			}
			return false
		}
		libs.GetLogger("gluster").Printf(peerStatus)
		connectedCount := strings.Count(peerStatus, "Peer in Cluster (Connected)")
		if connectedCount >= len(workers) {
			libs.GetLogger("gluster").Printf("All %d worker peers connected", connectedCount)
			return true
		}
		if attempt < maxPeerAttempts {
			libs.GetLogger("gluster").Printf("Waiting for peers to connect... (%d/%d)", attempt, maxPeerAttempts)
			time.Sleep(3 * time.Second)
		}
	}
	return false
}

func ensureVolume(manager *NodeInfo, workers []*NodeInfo, glusterCmd string, glusterCfg *libs.GlusterFSConfig, cfg *libs.LabConfig) bool {
	lxcHost := cfg.LXCHost()
	volumeName := glusterCfg.VolumeName
	brickPath := glusterCfg.BrickPath
	replicaCount := glusterCfg.ReplicaCount
	lxcService := services.NewLXCService(lxcHost, &cfg.SSH)
	if !lxcService.Connect() {
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)
	libs.GetLogger("gluster").Printf("Creating GlusterFS volume '%s'...", volumeName)
	volumeExistsCmd := cli.NewGluster().GlusterCmd(glusterCmd).VolumeExistsCheck(volumeName)
	volumeExistsOutput, _ := pctService.Execute(manager.ContainerID, volumeExistsCmd, nil)
	if cli.ParseVolumeExists(volumeExistsOutput) {
		libs.GetLogger("gluster").Printf("Volume '%s' already exists", volumeName)
		return true
	}
	allNodes := append([]*NodeInfo{manager}, workers...)
	brickList := make([]string, len(allNodes))
	for i, node := range allNodes {
		brickList[i] = fmt.Sprintf("%s:%s", node.IPAddress, brickPath)
	}
	createCmd := cli.NewGluster().GlusterCmd(glusterCmd).Force(true).VolumeCreate(volumeName, replicaCount, brickList)
	createOutput, _ := pctService.Execute(manager.ContainerID, createCmd, nil)
	libs.GetLogger("gluster").Printf("%s", createOutput)
	if !strings.Contains(strings.ToLower(createOutput), "created") && !strings.Contains(strings.ToLower(createOutput), "success") {
		libs.GetLogger("gluster").Printf("Volume creation failed: %s", createOutput)
		return false
	}
	libs.GetLogger("gluster").Printf("Starting volume '%s'...", volumeName)
	startCmd := cli.NewGluster().GlusterCmd(glusterCmd).VolumeStart(volumeName)
	startOutput, _ := pctService.Execute(manager.ContainerID, startCmd, nil)
	libs.GetLogger("gluster").Printf("%s", startOutput)
	libs.GetLogger("gluster").Printf("Verifying volume status...")
	volStatusCmd := cli.NewGluster().GlusterCmd(glusterCmd).VolumeStatus(volumeName)
	volStatus, _ := pctService.Execute(manager.ContainerID, volStatusCmd, nil)
	if volStatus != "" {
		libs.GetLogger("gluster").Printf(volStatus)
	}
	return true
}

func mountGlusterVolume(manager *NodeInfo, workers []*NodeInfo, glusterCfg *libs.GlusterFSConfig, cfg *libs.LabConfig) bool {
	nodes := append([]*NodeInfo{manager}, workers...)
	volumeName := glusterCfg.VolumeName
	mountPoint := glusterCfg.MountPoint
	lxcHost := cfg.LXCHost()
	lxcService := services.NewLXCService(lxcHost, &cfg.SSH)
	if !lxcService.Connect() {
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)
	libs.GetLogger("gluster").Printf("Mounting GlusterFS volume on all nodes...")
	for _, node := range nodes {
		libs.GetLogger("gluster").Printf("Mounting on %s...", node.Hostname)
		mkdirCmd := fmt.Sprintf("mkdir -p %s", mountPoint)
		mkdirResult, _ := pctService.Execute(node.ContainerID, mkdirCmd, nil)
		if strings.Contains(strings.ToLower(mkdirResult), "error") {
			outputLen := len(mkdirResult)
			start := 0
			if outputLen > 300 {
				start = outputLen - 300
			}
			libs.GetLogger("gluster").Printf("Failed to create mount point on %s: %s", node.Hostname, mkdirResult[start:])
			return false
		}
		fstabEntry := fmt.Sprintf("%s:/%s %s glusterfs defaults,_netdev 0 0", manager.Hostname, volumeName, mountPoint)
		fstabCmd := fmt.Sprintf("grep -q '%s' /etc/fstab || echo '%s' >> /etc/fstab", mountPoint, fstabEntry)
		fstabResult, _ := pctService.Execute(node.ContainerID, fstabCmd, nil)
		if strings.Contains(strings.ToLower(fstabResult), "error") {
			outputLen := len(fstabResult)
			start := 0
			if outputLen > 200 {
				start = outputLen - 200
			}
			libs.GetLogger("gluster").Printf("fstab update had issues on %s: %s", node.Hostname, fstabResult[start:])
		}
		mountCmd := fmt.Sprintf("/usr/sbin/mount.glusterfs %s:/%s %s || /usr/sbin/mount.glusterfs %s:/%s %s", manager.Hostname, volumeName, mountPoint, manager.IPAddress, volumeName, mountPoint)
		mountResult, _ := pctService.Execute(node.ContainerID, mountCmd, nil)
		if strings.Contains(strings.ToLower(mountResult), "error") && !strings.Contains(strings.ToLower(mountResult), "already mounted") {
			outputLen := len(mountResult)
			start := 0
			if outputLen > 300 {
				start = outputLen - 300
			}
			libs.GetLogger("gluster").Printf("Failed to mount GlusterFS on %s: %s", node.Hostname, mountResult[start:])
			return false
		}
		if !verifyMount(node, mountPoint, cfg, pctService) {
			return false
		}
	}
	return true
}

func verifyMount(node *NodeInfo, mountPoint string, cfg *libs.LabConfig, pctService *services.PCTService) bool {
	mountVerifyCmd := fmt.Sprintf("mount | grep -q '%s' && mount | grep '%s' | grep -q gluster && echo mounted || echo not_mounted", mountPoint, mountPoint)
	mountVerify, _ := pctService.Execute(node.ContainerID, mountVerifyCmd, nil)
	if strings.Contains(mountVerify, "mounted") && !strings.Contains(mountVerify, "not_mounted") {
		libs.GetLogger("gluster").Printf("%s: Volume mounted successfully", node.Hostname)
		return true
	}
	mountInfoCmd := fmt.Sprintf("mount | grep %s || echo 'NOT_MOUNTED'", mountPoint)
	mountInfo, _ := pctService.Execute(node.ContainerID, mountInfoCmd, nil)
	if strings.Contains(mountInfo, "NOT_MOUNTED") || mountInfo == "" {
		libs.GetLogger("gluster").Printf("%s: Mount failed - volume not mounted", node.Hostname)
		return false
	}
	outputLen := len(mountInfo)
	start := 0
	if outputLen > 80 {
		start = outputLen - 80
	}
	libs.GetLogger("gluster").Printf("%s: Mount status unclear - %s", node.Hostname, mountInfo[start:])
	return true
}

func mountGlusterOnClients(manager *NodeInfo, glusterCfg *libs.GlusterFSConfig, cfg *libs.LabConfig) bool {
	lxcHost := cfg.LXCHost()
	volumeName := glusterCfg.VolumeName
	mountPoint := glusterCfg.MountPoint
	var clientNodes []*libs.ContainerConfig
	if cfg.Kubernetes != nil {
		if cfg.Kubernetes.Control != nil {
			for i := range cfg.Containers {
				for _, id := range cfg.Kubernetes.Control {
					if cfg.Containers[i].ID == id {
						clientNodes = append(clientNodes, &cfg.Containers[i])
						break
					}
				}
			}
		}
		if cfg.Kubernetes.Workers != nil {
			for i := range cfg.Containers {
				for _, id := range cfg.Kubernetes.Workers {
					if cfg.Containers[i].ID == id {
						clientNodes = append(clientNodes, &cfg.Containers[i])
						break
					}
				}
			}
		}
	}
	seen := make(map[int]bool)
	var uniqueClientNodes []*libs.ContainerConfig
	for _, node := range clientNodes {
		if !seen[node.ID] {
			seen[node.ID] = true
			uniqueClientNodes = append(uniqueClientNodes, node)
		}
	}
	clientNodes = uniqueClientNodes
	if len(clientNodes) == 0 {
		libs.GetLogger("gluster").Printf("No K3s nodes found for GlusterFS client mounting")
		return true
	}
	lxcService := services.NewLXCService(lxcHost, &cfg.SSH)
	if !lxcService.Connect() {
		libs.GetLogger("gluster").Printf("Failed to connect to LXC host for client mounting")
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)
	libs.GetLogger("gluster").Printf("Mounting GlusterFS volume on %d client nodes...", len(clientNodes))
	var installationFailed []string
	for _, node := range clientNodes {
		libs.GetLogger("gluster").Printf("Installing glusterfs-client on %s...", node.Hostname)
		verifyCmd := "test -x /usr/sbin/mount.glusterfs && echo installed || echo not_installed"
		timeout := 10
		verifyOutput, _ := pctService.Execute(node.ID, verifyCmd, &timeout)
		if verifyOutput != "" && strings.TrimSpace(verifyOutput) == "installed" {
			libs.GetLogger("gluster").Printf("glusterfs-client already installed on %s", node.Hostname)
			continue
		}
		fixAPTSourcesForNode(FromContainer(node), cfg, pctService)
		updateCmd := cli.NewApt().Update()
		timeout = 600
		updateOutput, updateExit := pctService.Execute(node.ID, updateCmd, &timeout)
		if updateExit != nil && *updateExit != 0 {
			outputLen := len(updateOutput)
			start := 0
			if outputLen > 200 {
				start = outputLen - 200
			}
			libs.GetLogger("gluster").Printf("Failed to update apt on %s: %s", node.Hostname, updateOutput[start:])
			installationFailed = append(installationFailed, node.Hostname)
			continue
		}
		installCmd := cli.NewApt().Install([]string{"glusterfs-client"})
		timeout = 300
		installOutput, exitCode := pctService.Execute(node.ID, installCmd, &timeout)
		if exitCode != nil && *exitCode != 0 {
			outputLen := len(installOutput)
			start := 0
			if outputLen > 300 {
				start = outputLen - 300
			}
			libs.GetLogger("gluster").Printf("Failed to install glusterfs-client on %s: %s", node.Hostname, installOutput[start:])
			installationFailed = append(installationFailed, node.Hostname)
			continue
		}
		verifyCmd2 := "test -x /usr/sbin/mount.glusterfs && echo installed || echo not_installed"
		timeout = 10
		verifyOutput2, _ := pctService.Execute(node.ID, verifyCmd2, &timeout)
		if verifyOutput2 == "" || strings.TrimSpace(verifyOutput2) != "installed" {
			libs.GetLogger("gluster").Printf("glusterfs-client installation verification failed on %s - /usr/sbin/mount.glusterfs not found", node.Hostname)
			installationFailed = append(installationFailed, node.Hostname)
		}
	}
	if len(installationFailed) > 0 {
		libs.GetLogger("gluster").Printf("Failed to install glusterfs-client on %d node(s): %s", len(installationFailed), strings.Join(installationFailed, ", "))
		return false
	}
	var mountFailed []string
	for _, node := range clientNodes {
		libs.GetLogger("gluster").Printf("Mounting GlusterFS on %s...", node.Hostname)
		mkdirCmd := fmt.Sprintf("mkdir -p %s", mountPoint)
		timeout := 10
		mkdirResult, mkdirExit := pctService.Execute(node.ID, mkdirCmd, &timeout)
		if mkdirExit != nil && *mkdirExit != 0 {
			outputLen := len(mkdirResult)
			start := 0
			if outputLen > 200 {
				start = outputLen - 200
			}
			libs.GetLogger("gluster").Printf("Failed to create mount point on %s: %s", node.Hostname, mkdirResult[start:])
			mountFailed = append(mountFailed, node.Hostname)
			continue
		}
		fstabEntry := fmt.Sprintf("%s:/%s %s glusterfs defaults,_netdev 0 0", manager.Hostname, volumeName, mountPoint)
		fstabCmd := fmt.Sprintf("grep -q '%s' /etc/fstab || echo '%s' >> /etc/fstab", mountPoint, fstabEntry)
		timeout = 10
		pctService.Execute(node.ID, fstabCmd, &timeout)
		reloadCmd := "systemctl daemon-reload"
		pctService.Execute(node.ID, reloadCmd, &timeout)
		mountCmd := fmt.Sprintf("/usr/sbin/mount.glusterfs %s:/%s %s || /usr/sbin/mount.glusterfs %s:/%s %s", manager.Hostname, volumeName, mountPoint, manager.IPAddress, volumeName, mountPoint)
		timeout = 30
		mountResult, mountExit := pctService.Execute(node.ID, mountCmd, &timeout)
		if mountExit != nil && *mountExit != 0 {
			if !strings.Contains(strings.ToLower(mountResult), "already mounted") {
				outputLen := len(mountResult)
				start := 0
				if outputLen > 300 {
					start = outputLen - 300
				}
				libs.GetLogger("gluster").Printf("Failed to mount GlusterFS on %s: %s", node.Hostname, mountResult[start:])
				mountFailed = append(mountFailed, node.Hostname)
			} else {
				libs.GetLogger("gluster").Printf("GlusterFS already mounted on %s", node.Hostname)
			}
		} else {
			verifyMountCmd := fmt.Sprintf("mount | grep -q '%s' && mount | grep '%s' | grep -q gluster && echo mounted || echo not_mounted", mountPoint, mountPoint)
			timeout = 10
			verifyMountOutput, _ := pctService.Execute(node.ID, verifyMountCmd, &timeout)
			if strings.Contains(verifyMountOutput, "mounted") {
				libs.GetLogger("gluster").Printf("GlusterFS mounted successfully on %s", node.Hostname)
			} else {
				libs.GetLogger("gluster").Printf("Mount verification failed on %s", node.Hostname)
				mountFailed = append(mountFailed, node.Hostname)
			}
		}
	}
	if len(mountFailed) > 0 {
		libs.GetLogger("gluster").Printf("Failed to mount GlusterFS on %d node(s): %s", len(mountFailed), strings.Join(mountFailed, ", "))
		return false
	}
	return true
}

func logGlusterSummary(glusterCfg *libs.GlusterFSConfig) {
	libs.GetLogger("gluster").Printf("GlusterFS distributed storage setup complete")
	libs.GetLogger("gluster").Printf("  Volume: %s", glusterCfg.VolumeName)
	libs.GetLogger("gluster").Printf("  Mount point: %s on all nodes", glusterCfg.MountPoint)
	libs.GetLogger("gluster").Printf("  Replication: %dx", glusterCfg.ReplicaCount)
}
