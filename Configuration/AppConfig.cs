namespace AutoSetupCLI.Configuration;

public class AppConfig
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Order { get; set; } = 100;
    
    public InstallerEngineType Engine { get; set; } = InstallerEngineType.Executable;
    public string InstallerPath { get; set; } = "";
    public string SilentArguments { get; set; } = "";
    
    // InstallShield .iss template path (if Engine == InstallShield)
    public string? IssTemplatePath { get; set; }
    
    // Printer Driver specific configuration
    public PrinterDriverConfig? PrinterConfig { get; set; }
    
    // Environment detection rule to determine whether already installed
    public DetectionRule? Detection { get; set; }
    
    // Pre/Post install actions
    public List<HookAction> PreInstallHooks { get; set; } = new();
    public List<HookAction> PostInstallHooks { get; set; } = new();
    
    // Exit code evaluation
    public List<int> SuccessExitCodes { get; set; } = new() { 0, 3010 };
    public List<int> RebootExitCodes { get; set; } = new() { 3010 };
    
    public int TimeoutSeconds { get; set; } = 300;
}

