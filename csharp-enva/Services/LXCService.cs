using Enva.Libs;

namespace Enva.Services;

public class LXCService : ILXCService
{
    private string lxcHost;
    private SSHConfig sshConfig;
    private SSHService sshService;

    public LXCService(string lxcHost, SSHConfig sshConfig)
    {
        this.lxcHost = lxcHost;
        this.sshConfig = sshConfig;
        this.sshService = new SSHService(lxcHost, sshConfig);
    }

    public bool Connect()
    {
        return sshService.Connect();
    }

    public void Disconnect()
    {
        sshService.Disconnect();
    }

    public bool IsConnected()
    {
        return sshService.IsConnected();
    }

    public (string output, int? exitCode) Execute(string command, int? timeout)
    {
        return sshService.Execute(command, timeout);
    }
}
