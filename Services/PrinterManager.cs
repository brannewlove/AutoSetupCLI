using System.Diagnostics;
using Microsoft.Win32;
using AutoSetupCLI.Configuration;

namespace AutoSetupCLI.Services;

public class PrinterManager
{
    private readonly Logger _logger;
    private readonly ParameterResolver _resolver;

    public PrinterManager(Logger logger, ParameterResolver resolver)
    {
        _logger = logger;
        _resolver = resolver;
    }

    /// <summary>
    /// 복합기/프린터 드라이버 및 포트, 프린터를 무인으로 등록하고 사번을 주입합니다.
    /// </summary>
    public bool InstallPrinter(PrinterDriverConfig config, bool dryRun = false)
    {
        var infPath = _resolver.Resolve(config.InfPath);
        var driverName = _resolver.Resolve(config.DriverName);
        var portName = _resolver.Resolve(config.PortName);
        var printerIp = _resolver.Resolve(config.PrinterIp);
        var printerName = _resolver.Resolve(config.PrinterName);
        var deptCode = _resolver.Resolve(config.DepartmentCode ?? "");

        _logger.Info($"복합기 드라이버 설치 시작: {printerName} (드라이버: {driverName})");

        if (dryRun)
        {
            _logger.Info($"[DRY-RUN] 드라이버 등록: pnputil /add-driver \"{infPath}\" /install");
            _logger.Info($"[DRY-RUN] 포트 생성: {portName} ({printerIp})");
            _logger.Info($"[DRY-RUN] 프린터 추가: {printerName}");
            if (!string.IsNullOrEmpty(deptCode))
            {
                _logger.Info($"[DRY-RUN] 사번 레지스트리 주입: {deptCode}");
            }
            return true;
        }

        // 1. INF 파일 존재 여부 검사
        if (!File.Exists(infPath))
        {
            _logger.Error($"프린터 INF 파일을 찾을 수 없습니다: {infPath}");
            return false;
        }

        // 2. pnputil을 통한 드라이버 스토어 등록
        _logger.Info($"[단계 1/4] 드라이버 등록 중 (pnputil)...");
        var pnpExit = RunProcess("pnputil.exe", $"/add-driver \"{infPath}\" /install");
        if (pnpExit != 0 && pnpExit != 259 && pnpExit != 3010)
        {
            _logger.Warn($"pnputil 드라이버 추가 반환 코드: {pnpExit} (기존 등록 상태일 수 있음)");
        }

        // 3. PowerShell 스크립트를 통한 포트 및 프린터 생성
        _logger.Info($"[단계 2/4] 프린터 포트 및 장치 생성 중 (PowerShell)...");
        var psScript = $@"
$ErrorActionPreference = 'Stop'
# 1. 포트 확인 및 추가
$port = Get-PrinterPort -Name '{portName}' -ErrorAction SilentlyContinue
if (-not $port) {{
    Add-PrinterPort -Name '{portName}' -PrinterHostAddress '{printerIp}'
}}

# 2. 드라이버 등록
Add-PrinterDriver -Name '{driverName}' -ErrorAction SilentlyContinue

# 3. 프린터 장치 생성
$printer = Get-Printer -Name '{printerName}' -ErrorAction SilentlyContinue
if (-not $printer) {{
    Add-Printer -Name '{printerName}' -DriverName '{driverName}' -PortName '{portName}'
}}
";
        var psExit = RunPowerShell(psScript);
        if (psExit != 0)
        {
            _logger.Error($"PowerShell 프린터 추가 실패 (종료 코드: {psExit})");
            return false;
        }

        // 4. 사번(인증 코드) 레지스트리 주입
        if (!string.IsNullOrEmpty(deptCode) && !string.IsNullOrEmpty(config.DepartmentCodeRegistryKey))
        {
            _logger.Info($"[단계 3/4] 복합기 사번/인증코드({deptCode}) 레지스트리 주입 중...");
            var regKeyPath = _resolver.Resolve(config.DepartmentCodeRegistryKey);
            var valName = _resolver.Resolve(config.DepartmentCodeValueName ?? "DepartmentCode");

            try
            {
                InjectDepartmentCodeRegistry(regKeyPath, valName, deptCode);
                _logger.Success($"사번 레지스트리 주입 완료!");
            }
            catch (Exception ex)
            {
                _logger.Warn($"사번 레지스트리 주입 중 경고: {ex.Message}");
            }
        }

        _logger.Success($"✅ 복합기({printerName}) 무인 설치 및 사번 설정 완료");
        return true;
    }

    private void InjectDepartmentCodeRegistry(string regPath, string valueName, string deptCode)
    {
        var parts = regPath.Split('\\', 2);
        if (parts.Length < 2) return;

        var hive = parts[0].ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            _ => RegistryHive.LocalMachine
        };

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var key = baseKey.CreateSubKey(parts[1], writable: true);
        key.SetValue(valueName, deptCode, RegistryValueKind.String);
    }

    private int RunProcess(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(60000);
            return p?.ExitCode ?? -1;
        }
        catch (Exception ex)
        {
            _logger.Error($"프로세스 실행 오류 ({file}): {ex.Message}");
            return -1;
        }
    }

    private int RunPowerShell(string script)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        return RunProcess("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}");
    }
}

