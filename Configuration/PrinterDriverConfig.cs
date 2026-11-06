namespace AutoSetupCLI.Configuration;

public class PrinterDriverConfig
{
    public string InfPath { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string PortName { get; set; } = "";
    public string PrinterIp { get; set; } = "";
    public string PrinterName { get; set; } = "";
    
    // 복합기 사번/부서코드 자동 주입 레지스트리 경로
    public string? DepartmentCodeRegistryKey { get; set; }
    public string? DepartmentCodeValueName { get; set; }
    public string? DepartmentCode { get; set; }
}

