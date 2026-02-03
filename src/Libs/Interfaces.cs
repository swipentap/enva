namespace Enva.Libs;

public interface ILXCService
{
    bool Connect();
    void Disconnect();
    bool IsConnected();
    (string output, int? exitCode) Execute(string command, int? timeout = null);
}

public interface IPCTService
{
    (string output, int? exitCode) Execute(int containerID, string command, int? timeout = null);
    bool SetupSSHKey(int containerID, string ipAddress, LabConfig cfg);
    bool EnsureSSHServiceRunning(int containerID, LabConfig cfg);
    bool WaitForContainer(int containerID, string ipAddress, LabConfig cfg, string defaultUser);
    ILXCService? GetLXCService();
    (string output, int? exitCode) Start(int containerID);
    (string output, int? exitCode) Stop(int containerID, bool force);
}
