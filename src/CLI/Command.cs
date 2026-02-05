namespace Enva.CLI;

public class Command
{
    private string? commandName;

    public static Command NewCommand()
    {
        return new Command();
    }

    public Command SetCommand(string name)
    {
        commandName = name;
        return this;
    }

    public string Exists()
    {
        if (string.IsNullOrEmpty(commandName))
        {
            throw new Exception("Command name must be set");
        }
        return $"command -v {Quote(commandName)}";
    }

    private string Quote(string s)
    {
        if (s.Contains(" ") || s.Contains("$"))
        {
            return $"'{s.Replace("'", "'\"'\"'")}'";
        }
        return s;
    }
}
