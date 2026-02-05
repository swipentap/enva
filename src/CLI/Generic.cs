namespace Enva.CLI;

public class Generic
{
    public static Generic NewGeneric()
    {
        return new Generic();
    }

    public static string Passthrough(string command)
    {
        return command;
    }
}
