namespace AutoSetupCLI.Configuration;

public enum RuleType
{
    Registry,
    File,
    Service,
    Process,
    Command
}

public enum RuleOperator
{
    Exists,
    Equals,
    Contains,
    VersionGreaterOrEqual
}

public class DetectionRule
{
    public RuleType Type { get; set; } = RuleType.Registry;
    public RuleOperator Operator { get; set; } = RuleOperator.Exists;
    
    // Registry specific
    public string? RegistryKey { get; set; }
    public string? ValueName { get; set; }
    public string? ExpectedValue { get; set; }
    
    // File specific
    public string? FilePath { get; set; }
    
    // Service specific
    public string? ServiceName { get; set; }
    
    // Process specific
    public string? ProcessName { get; set; }

    // If true, condition is inverted (e.g. "Trigger only if NOT installed")
    public bool Invert { get; set; } = false;
}

