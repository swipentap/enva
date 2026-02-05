using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Enva.Libs;

namespace Enva.Services;

public class SSHService
{
    private string host;
    private string hostname;
    private string username;
    private SSHConfig sshConfig;
    private SshClient? client;
    private bool connected;

    public SSHService(string host, SSHConfig sshConfig)
    {
        this.host = host;
        this.sshConfig = sshConfig;
        this.connected = false;

        // Parse host (format: user@host or just host)
        int idx = host.IndexOf("@");
        if (idx >= 0)
        {
            this.username = host.Substring(0, idx);
            this.hostname = host.Substring(idx + 1);
        }
        else
        {
            this.username = sshConfig.DefaultUsername;
            this.hostname = host;
        }
    }

    public bool Connect()
    {
        if (connected && client != null && client.IsConnected)
        {
            return true;
        }

        // Find and load private key
        string? keyFile = FindPrivateKey();
        if (string.IsNullOrEmpty(keyFile))
        {
            Logger.GetLogger("ssh").Printf("No private key found");
            return false;
        }

        // Load private key
        PrivateKeyFile? privateKey = LoadPrivateKey(keyFile);
        if (privateKey == null)
        {
            Logger.GetLogger("ssh").Printf("Failed to load private key {0}", keyFile);
            return false;
        }

        // Create SSH client
        try
        {
            ConnectionInfo connectionInfo = new ConnectionInfo(
                hostname,
                22,
                username,
                new PrivateKeyAuthenticationMethod(username, privateKey)
            );
            connectionInfo.Timeout = TimeSpan.FromSeconds(sshConfig.ConnectTimeout);

            client = new SshClient(connectionInfo);
            client.Connect();

            connected = true;
            Logger.GetLogger("ssh").Printf("SSH connection established to {0}@{1}", username, hostname);
            return true;
        }
        catch (Exception ex)
        {
            Logger.GetLogger("ssh").Printf("Failed to establish SSH connection to {0}: {1}", host, ex.Message);
            client = null;
            connected = false;
            return false;
        }
    }

    public void Disconnect()
    {
        if (client != null)
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
            client.Dispose();
            client = null;
            connected = false;
            Logger.GetLogger("ssh").Printf("SSH connection closed to {0}", host);
        }
    }

    public bool IsConnected()
    {
        return connected && client != null && client.IsConnected;
    }

    public (string output, int? exitCode) Execute(string command, int? timeout, bool sudo = false)
    {
        if (!IsConnected())
        {
            if (!Connect())
            {
                Logger.GetLogger("ssh").Printf("Cannot execute command: SSH connection not available");
                return ("", null);
            }
        }

        int execTimeout = timeout ?? sshConfig.DefaultExecTimeout;

        if (sudo)
        {
            Logger.GetLogger("ssh").Debug("Base64 source: {0}", command);

            // For multi-line scripts or commands with single quotes, use base64 encoding
            if (command.Contains("\n") || command.Contains("'"))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(command);
                string encoded = Convert.ToBase64String(bytes);
                command = $"sudo -n bash -c 'echo {encoded} | base64 -d | bash'";
            }
            else
            {
                // For single-line commands, wrap in bash -c with proper quoting
                command = $"sudo -n bash -c {QuoteCommand(command)}";
            }
        }

        Logger.GetLogger("ssh").Debug("Running: {0}", command);

        try
        {
            using (var cmd = client!.CreateCommand(command))
            {
                cmd.CommandTimeout = TimeSpan.FromSeconds(execTimeout);

                // Start command execution
                var asyncResult = cmd.BeginExecute();
                
                // Get streams after BeginExecute (they're only available after command starts)
                var stderrStream = cmd.ExtendedOutputStream;
                var stdoutStream = cmd.OutputStream;
                
                // Consume BOTH stdout and stderr in background to prevent EndExecute from hanging
                // SSH.NET's EndExecute() blocks if EITHER stream buffer fills up
                var stderrBuilder = new StringBuilder();
                var stdoutBuilder = new StringBuilder();
                var stderrReadComplete = new ManualResetEventSlim(false);
                var stdoutReadComplete = new ManualResetEventSlim(false);
                int stderrReadCount = 0;
                int stdoutReadCount = 0;
                byte[] stderrBuffer = new byte[4096];
                byte[] stdoutBuffer = new byte[4096];
                
                // Stderr callback
                AsyncCallback? stderrCallback = null;
                stderrCallback = (ar) =>
                {
                    stderrReadCount++;
                    try
                    {
                        int read = stderrStream.EndRead(ar);
                        
                        if (read > 0)
                        {
                            string stderrData = Encoding.UTF8.GetString(stderrBuffer, 0, read);
                            stderrBuilder.Append(stderrData);
                            // Display stderr immediately as it arrives
                            Logger.GetLogger("ssh-err").Debug(stderrData);
                            stderrStream.BeginRead(stderrBuffer, 0, stderrBuffer.Length, stderrCallback, null);
                        }
                        else
                        {
                            stderrReadComplete.Set();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.GetLogger("ssh-err").Error("Error reading stderr stream: {0}", ex.Message);
                        stderrReadComplete.Set();
                    }
                };
                
                // Stdout callback
                AsyncCallback? stdoutCallback = null;
                stdoutCallback = (ar) =>
                {
                    stdoutReadCount++;
                    try
                    {
                        int read = stdoutStream.EndRead(ar);
                        
                        if (read > 0)
                        {
                            string stdoutData = Encoding.UTF8.GetString(stdoutBuffer, 0, read);
                            stdoutBuilder.Append(stdoutData);
                            // Display stdout immediately as it arrives
                            Logger.GetLogger("ssh-out").Debug(stdoutData);
                            stdoutStream.BeginRead(stdoutBuffer, 0, stdoutBuffer.Length, stdoutCallback, null);
                        }
                        else
                        {
                            stdoutReadComplete.Set();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.GetLogger("ssh-out").Error("Error reading stdout stream: {0}", ex.Message);
                        stdoutReadComplete.Set();
                    }
                };
                
                // Start reading BOTH streams asynchronously BEFORE EndExecute
                try
                {
                    stderrStream.BeginRead(stderrBuffer, 0, stderrBuffer.Length, stderrCallback, null);
                    stdoutStream.BeginRead(stdoutBuffer, 0, stdoutBuffer.Length, stdoutCallback, null);
                    
                    // Give callbacks a moment to start before EndExecute
                    Thread.Sleep(10);
                }
                catch (Exception)
                {
                    stderrReadComplete.Set();
                    stdoutReadComplete.Set();
                }
                
                // Wait for command to complete
                cmd.EndExecute(asyncResult);
                
                // Wait for both readers to finish
                stderrReadComplete.Wait(5000);
                stdoutReadComplete.Wait(5000);
                
                // Get stdout and stderr (use our captured data, not cmd.Result which may block)
                string result = stdoutBuilder.ToString();
                string stderr = stderrBuilder.ToString();
                
                // After EndExecute returns, also check cmd.Result and cmd.Error for any remaining buffered data
                // that might not have been read from the streams
                try
                {
                    string? cmdResult = cmd.Result;
                    string? cmdError = cmd.Error;
                    
                    // If cmd.Result/cmd.Error have more data than what we captured, append the missing parts
                    if (!string.IsNullOrEmpty(cmdResult))
                    {
                        if (string.IsNullOrEmpty(result) || cmdResult.Length > result.Length)
                        {
                            // cmd.Result has more data, use it and log any new parts
                            string newData = cmdResult.Substring(result.Length);
                            if (!string.IsNullOrEmpty(newData))
                            {
                                Logger.GetLogger("ssh-out").Debug(newData);
                            }
                            result = cmdResult;
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(cmdError))
                    {
                        if (string.IsNullOrEmpty(stderr) || cmdError.Length > stderr.Length)
                        {
                            // cmd.Error has more data, use it and log any new parts
                            string newData = cmdError.Substring(stderr.Length);
                            if (!string.IsNullOrEmpty(newData))
                            {
                                Logger.GetLogger("ssh-err").Debug(newData);
                            }
                            stderr = cmdError;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.GetLogger("ssh").Error("Error reading remaining command output: {0}", ex.Message);
                }
                
                int exitCode = cmd.ExitStatus;

                string combined = result;
                if (!string.IsNullOrEmpty(stderr))
                {
                    if (!string.IsNullOrEmpty(result))
                    {
                        combined = result + "\n" + stderr;
                    }
                    else
                    {
                        combined = stderr;
                    }
                }

                // Output already displayed as it arrived, no need to display again

                return (combined.Trim(), exitCode);
            }
        }
        catch (Exception ex)
        {
            Logger.GetLogger("ssh").Printf("SSH command timeout after {0}s - COMMAND FAILED: {1}", execTimeout, ex.Message);
            return ("", null);
        }
    }

    private string? FindPrivateKey()
    {
        string? homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(homeDir))
        {
            return null;
        }

        string[] keyPaths = {
            Path.Combine(homeDir, ".ssh", "id_rsa"),
            Path.Combine(homeDir, ".ssh", "id_ed25519")
        };

        foreach (string keyPath in keyPaths)
        {
            if (File.Exists(keyPath))
            {
                return keyPath;
            }
        }
        return null;
    }

    private PrivateKeyFile? LoadPrivateKey(string keyFile)
    {
        try
        {
            // Try without passphrase first
            return new PrivateKeyFile(keyFile);
        }
        catch
        {
            try
            {
                // Try with empty passphrase
                return new PrivateKeyFile(keyFile, "");
            }
            catch (Exception ex)
            {
                Logger.GetLogger("ssh").Printf("Failed to parse private key: {0}", ex.Message);
                return null;
            }
        }
    }

    private string QuoteCommand(string cmd)
    {
        // Simple quoting - wrap in single quotes and escape single quotes
        if (cmd.Contains("'") || cmd.Contains(" ") || cmd.Contains("$"))
        {
            string escaped = cmd.Replace("'", "'\"'\"'");
            return $"'{escaped}'";
        }
        return $"'{cmd}'";
    }
}
