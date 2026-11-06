namespace AutoSetupCLI.Configuration;

public enum HookType
{
    RegistrySet,
    RegistryDelete,
    FileWrite,
    FileDelete,
    ServiceControl,
    RunCommand
}

public class HookAction
{
    public HookType Type { get; set; } = HookType.RunCommand;
    public string Target { get; set; } = "";
    public string? ValueName { get; set; }
    public string? Value { get; set; }
    public string? ValueKind { get; set; } = "String"; // String, DWord, QWord
    public string? ServiceAction { get; set; } = "Start"; // Start, Stop, Restart
    public bool IgnoreErrors { get; set; } = true;
    public string Description { get; set; } = "";
}

