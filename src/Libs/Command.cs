namespace Enva.Libs;

public interface ICommand
{
    void Run(object? args);
}

public class BaseCommand
{
    public LabConfig? Cfg { get; set; }
}
