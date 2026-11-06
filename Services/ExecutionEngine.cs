using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using Microsoft.Win32;
using AutoSetupCLI.Configuration;

namespace AutoSetupCLI.Services;

public class InstallResult
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "Unknown"; // Installed, Skipped, Failed, RebootRequired
    public int? ExitCode { get; set; }
    public TimeSpan Duration { get; set; }
    public string Message { get; set; } = "";
}

public class ExecutionEngine
{
    private readonly Logger _logger;
    private readonly ParameterResolver _resolver;
    private readonly EnvironmentDetector _detector;
    private readonly IssTemplateEngine _issEngine;
    private readonly PrinterManager _printerManager;

    public bool RebootRequired { get; private set; } = false;

    public ExecutionEngine(
        Logger logger, 
        ParameterResolver resolver, 
        EnvironmentDetector detector, 
        IssTemplateEngine issEngine, 
        PrinterManager printerManager)
    {
        _logger = logger;
        _resolver = resolver;
        _detector = detector;
        _issEngine = issEngine;
        _printerManager = printerManager;
    }

    public async Task<InstallResult> ExecuteAppAsync(AppConfig app, bool force = false, bool dryRun = false)
    {
        var sw = Stopwatch.StartNew();
        var result = new InstallResult
        {
            Key = app.Key,
            Name = app.Name
        };

        _logger.Step($"[{app.Name}] 처리 시작 (엔진: {app.Engine})");

        // 1. 환경 상태 감지 (기설치 여부 및 스마트 분기)
        if (app.Detection != null && !force)
        {
            _logger.Info("환경 및 기설치 상태 감지 중...");
            bool isDetected = _detector.EvaluateRule(app.Detection);
            if (isDetected)
            {
                _logger.Success($"이미 설치되어 있거나 조건이 충족되었습니다 (스킵).");
                result.Status = "Skipped";
                result.Message = "이미 설치됨 (조건 충족)";
                result.Duration = sw.Elapsed;
                return result;
            }
        }

        // 2. Pre-Install Hooks 실행
        if (app.PreInstallHooks.Count > 0)
        {
            _logger.Info($"설치 전 작업({app.PreInstallHooks.Count}개) 수행 중...");
            foreach (var hook in app.PreInstallHooks)
            {
                ExecuteHook(hook, dryRun);
            }
        }

        // 3. 프린터 드라이버 전용 처리
        if (app.Engine == InstallerEngineType.PrinterDriver && app.PrinterConfig != null)
        {
            bool success = _printerManager.InstallPrinter(app.PrinterConfig, dryRun);
            sw.Stop();
            result.Status = success ? "Installed" : "Failed";
            result.Message = success ? "프린터 드라이버 설치 완료" : "드라이버 설치 실패";
            result.Duration = sw.Elapsed;
            return result;
        }

        // 4. 설치 파일 및 인자 검증
        var installerPath = _resolver.Resolve(app.InstallerPath);
        if (!dryRun && !File.Exists(installerPath) && app.Engine != InstallerEngineType.PowerShell)
        {
            _logger.Error($"설치 파일을 찾을 수 없습니다: {installerPath}");
            result.Status = "Failed";
            result.Message = $"파일 없음: {installerPath}";
            result.Duration = sw.Elapsed;
            return result;
        }

        // 5. 엔진별 사일런트 인자 결정
        string arguments = ResolveArguments(app);

        _logger.Info($"실행 파일: {installerPath}");
        _logger.Info($"실행 인자: {arguments}");

        if (dryRun)
        {
            _logger.Info($"[DRY-RUN] 실행 시뮬레이션 완료");
            result.Status = "Installed";
            result.Message = "Dry-Run 시뮬레이션 성공";
            result.ExitCode = 0;
            result.Duration = sw.Elapsed;
            return result;
        }

        // 6. 프로세스 실행 및 모니터링
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            // 비동기로 출력 스트림 읽기
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(app.TimeoutSeconds));
            await proc.WaitForExitAsync(cts.Token);

            int exitCode = proc.ExitCode;
            result.ExitCode = exitCode;

            _logger.Info($"프로세스 종료됨 (종료 코드: {exitCode})");

            // 7. 종료 코드 판정 및 분기 대응
            bool isSuccess = app.SuccessExitCodes.Contains(exitCode);
            bool isReboot = app.RebootExitCodes.Contains(exitCode);

            if (isReboot)
            {
                RebootRequired = true;
                _logger.Warn($"재부팅이 필요한 상태입니다 (종료 코드: {exitCode})");
            }

            if (isSuccess)
            {
                _logger.Success($"'{app.Name}' 설치 성공!");
                result.Status = isReboot ? "RebootRequired" : "Installed";
                result.Message = isReboot ? "설치 완료 (재부팅 필요)" : "정상 설치 완료";

                // 8. Post-Install Hooks 실행 (사번 주입, 레지스트리 수정, 서비스 시작 등)
                if (app.PostInstallHooks.Count > 0)
                {
                    _logger.Info($"설치 후 작업({app.PostInstallHooks.Count}개) 수행 중...");
                    foreach (var hook in app.PostInstallHooks)
                    {
                        ExecuteHook(hook, dryRun);
                    }
                }
            }
            else
            {
                _logger.Error($"설치 실패 (종료 코드: {exitCode})");
                result.Status = "Failed";
                result.Message = $"실패 ExitCode: {exitCode}";
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Error($"설치 시간 초과 ({app.TimeoutSeconds}초)");
            result.Status = "Failed";
            result.Message = "설치 시간 초과(Timeout)";
        }
        catch (Exception ex)
        {
            _logger.Error($"설치 실행 중 예외: {ex.Message}");
            result.Status = "Failed";
            result.Message = ex.Message;
        }

        sw.Stop();
        result.Duration = sw.Elapsed;
        return result;
    }

    private string ResolveArguments(AppConfig app)
    {
        string rawArgs = app.SilentArguments;

        // InstallShield 응답 파일(.iss) 치환 처리
        if (app.Engine == InstallerEngineType.InstallShield && !string.IsNullOrEmpty(app.IssTemplatePath))
        {
            var tempIss = _issEngine.GenerateResponseFile(app.IssTemplatePath);
            _resolver.SetVariable("IssFilePath", tempIss);
            if (string.IsNullOrEmpty(rawArgs))
            {
                rawArgs = $"-s -f1\"{tempIss}\"";
            }
        }
        else if (string.IsNullOrEmpty(rawArgs))
        {
            // 기본 권장 사일런트 인자 자동 할당
            rawArgs = app.Engine switch
            {
                InstallerEngineType.Msi => "/qn /norestart",
                InstallerEngineType.InnoSetup => "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                InstallerEngineType.Nsis => "/S",
                InstallerEngineType.InstallShield => "-s",
                _ => ""
            };
        }

        return _resolver.Resolve(rawArgs);
    }

    private void ExecuteHook(HookAction hook, bool dryRun)
    {
        var target = _resolver.Resolve(hook.Target);
        var val = _resolver.Resolve(hook.Value ?? "");
        var valName = _resolver.Resolve(hook.ValueName ?? "");

        _logger.Info($"[Hook] {hook.Description} ({hook.Type})");

        if (dryRun)
        {
            _logger.Info($"[DRY-RUN Hook] Type={hook.Type}, Target={target}, ValName={valName}, Val={val}");
            return;
        }

        try
        {
            switch (hook.Type)
            {
                case HookType.RegistrySet:
                    SetRegistryValue(target, valName, val, hook.ValueKind);
                    break;
                case HookType.RegistryDelete:
                    DeleteRegistryValue(target, valName);
                    break;
                case HookType.FileWrite:
                    File.WriteAllText(target, val);
                    break;
                case HookType.ServiceControl:
                    ControlService(target, hook.ServiceAction ?? "Start");
                    break;
                case HookType.RunCommand:
                    RunShellCommand(target);
                    break;
            }
        }
        catch (Exception ex)
        {
            if (hook.IgnoreErrors)
            {
                _logger.Warn($"Hook 오류 무시됨 ({hook.Description}): {ex.Message}");
            }
            else
            {
                throw;
            }
        }
    }

    private static void SetRegistryValue(string path, string valueName, string value, string? kind)
    {
        var parts = path.Split('\\', 2);
        if (parts.Length < 2) return;

        var hive = parts[0].ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            _ => RegistryHive.LocalMachine
        };

        var regKind = kind?.ToUpperInvariant() switch
        {
            "DWORD" => RegistryValueKind.DWord,
            "QWORD" => RegistryValueKind.QWord,
            "BINARY" => RegistryValueKind.Binary,
            _ => RegistryValueKind.String
        };

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var subKey = baseKey.CreateSubKey(parts[1], writable: true);

        object finalValue = regKind == RegistryValueKind.DWord ? int.Parse(value) : value;
        subKey.SetValue(valueName, finalValue, regKind);
    }

    private static void DeleteRegistryValue(string path, string valueName)
    {
        var parts = path.Split('\\', 2);
        if (parts.Length < 2) return;

        var hive = parts[0].ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            _ => RegistryHive.LocalMachine
        };

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var subKey = baseKey.OpenSubKey(parts[1], writable: true);
        subKey?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private static void ControlService(string serviceName, string action)
    {
        using var sc = new ServiceController(serviceName);
        if (action.Equals("Stop", StringComparison.OrdinalIgnoreCase) && sc.Status == ServiceControllerStatus.Running)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        else if (action.Equals("Start", StringComparison.OrdinalIgnoreCase) && sc.Status == ServiceControllerStatus.Stopped)
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
    }

    private static void RunShellCommand(string cmd)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c {cmd}")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(30000);
    }
}

