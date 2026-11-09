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
        bool installerExists = File.Exists(installerPath);

        if (dryRun)
        {
            if (installerExists)
                _logger.Success($"[파일 검증] 설치 파일 확인됨: {installerPath}");
            else if (app.Engine != InstallerEngineType.PowerShell)
                _logger.Warn($"[파일 검증] 주의! 설치 파일이 현재 경로에 없습니다: {installerPath}");
        }
        else if (!installerExists && app.Engine != InstallerEngineType.PowerShell)
        {
            _logger.Error($"설치 파일을 찾을 수 없습니다: {installerPath}");
            result.Status = "Failed";
            result.Message = $"파일 없음: {installerPath}";
            result.Duration = sw.Elapsed;
            return result;
        }

        // 5. 엔진별 사일런트 인자, 상세 로그 옵션 및 실행 파일 결정
        string logDir = _logger.LogDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        string engineLogFile = Path.Combine(logDir, $"{app.Key}_{DateTime.Now:yyyyMMdd_HHmmss}.engine.log");

        string arguments = ResolveArguments(app, engineLogFile);
        string execFileName = installerPath;
        string execArguments = arguments;

        if (app.Engine == InstallerEngineType.Msi)
        {
            execFileName = "msiexec.exe";
            // MSI 상세 로그(/L*v)가 인자에 없으면 자동 추가
            string msiLogArg = arguments.Contains("/L", StringComparison.OrdinalIgnoreCase) ? "" : $" /L*v \"{engineLogFile}\"";
            execArguments = $"/i \"{installerPath}\" {arguments}{msiLogArg}";
        }
        else if (app.Engine == InstallerEngineType.PowerShell)
        {
            execFileName = "powershell.exe";
            execArguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{installerPath}\" {arguments}";
        }

        _logger.Info($"실행 파일: {execFileName}");
        _logger.Info($"실행 인자: {execArguments}");

        if (dryRun)
        {
            _logger.Info($"[DRY-RUN] 실행 시뮬레이션 완료");
            result.Status = installerExists ? "Installed" : "Failed";
            result.Message = installerExists ? "Dry-Run 검증 통과 (파일 존재)" : "Dry-Run 경고 (설치파일 없음)";
            result.ExitCode = installerExists ? 0 : -1;
            result.Duration = sw.Elapsed;
            return result;
        }

        // 6. 프로세스 실행 및 모니터링
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = execFileName,
                Arguments = execArguments,
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

            string stdOut = (await outputTask).Trim();
            string stdErr = (await errorTask).Trim();

            int exitCode = proc.ExitCode;
            result.ExitCode = exitCode;

            _logger.Info($"프로세스 종료됨 (종료 코드: {exitCode})");

            if (!string.IsNullOrEmpty(stdOut))
            {
                _logger.Debug($"[{app.Key} 표준출력] {stdOut}");
            }
            if (!string.IsNullOrEmpty(stdErr))
            {
                _logger.Error($"[{app.Key} 오류출력] {stdErr}");
            }

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
                string reason = ExplainExitCode(app.Engine, exitCode);
                _logger.Error($"설치 실패 (종료 코드: {exitCode}) -> 원인 분석: {reason}");
                if (File.Exists(engineLogFile))
                {
                    _logger.Error($"상세 설치 엔진 로그 확인: {engineLogFile}");
                    AnalyzeInstallShieldLog(engineLogFile);
                }
                result.Status = "Failed";
                result.Message = $"실패(Code:{exitCode}) {reason}";
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

    private string ResolveArguments(AppConfig app, string engineLogFile)
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

        string resolved = _resolver.Resolve(rawArgs);

        // 엔진별 자체 로그 파일 생성 옵션 자동 부착 (원인 파악용)
        if (app.Engine == InstallerEngineType.InstallShield && !resolved.Contains("-f2", StringComparison.OrdinalIgnoreCase))
        {
            resolved += $" -f2\"{engineLogFile}\"";
        }
        else if (app.Engine == InstallerEngineType.InnoSetup && !resolved.Contains("/LOG", StringComparison.OrdinalIgnoreCase))
        {
            resolved += $" /LOG=\"{engineLogFile}\"";
        }

        return resolved;
    }

    private void AnalyzeInstallShieldLog(string logPath)
    {
        try
        {
            var lines = File.ReadAllLines(logPath);
            var resultLine = lines.FirstOrDefault(l => l.TrimStart().StartsWith("ResultCode=", StringComparison.OrdinalIgnoreCase));
            if (resultLine != null && int.TryParse(resultLine.Split('=')[1].Trim(), out int isCode))
            {
                string isReason = ExplainExitCode(InstallerEngineType.InstallShield, isCode);
                _logger.Error($"[InstallShield 로그 분석] ResultCode={isCode} ({isReason})");
            }
        }
        catch { }
    }

    private static string ExplainExitCode(InstallerEngineType engine, int code)
    {
        // 공통/MSI/InstallShield 주요 오류 코드 해설
        if (engine == InstallerEngineType.InstallShield)
        {
            return code switch
            {
                0 => "정상 완료",
                -1 => "일반 오류 (General Error)",
                -2 => "잘못된 모드 (Invalid Mode)",
                -3 => "응답 파일(.iss)에 필요한 데이터가 누락되었거나 대화상자 순서가 맞지 않음 (setup.exe /r 로 .iss 재생성 필요)",
                -4 => "디스크 공간 부족",
                -5 => "응답 파일(.iss)이 존재하지 않음 (-f1 경로 확인 필요)",
                -6 => "사용자가 취소함",
                -7 => "로그 파일(.log)을 쓸 수 없음 (-f2 경로 권한/폴더 확인 필요)",
                -8 => "설치 파일 내부 압축 해제 실패",
                -9 => "지원되지 않는 운영체제/타입",
                -12 => "설치 대화상자(Dialog)가 응답 파일(.iss) 순서와 다름 (이미 설치된 상태에서 재실행했거나 버전이 다름)",
                _ => $"알 수 없는 InstallShield 종료 코드 ({code})"
            };
        }

        return code switch
        {
            0 => "정상 완료",
            1 => "잘못된 기능 또는 스크립트/일반 오류",
            2 => "지정된 파일을 찾을 수 없음",
            5 => "액세스 거부됨 (관리자 권한으로 실행했는지 확인 필요)",
            87 => "매개변수(인자)가 잘못됨",
            1602 => "사용자가 설치를 취소함",
            1603 => "설치 중 심각한 오류 발생 (이미 설치되어 있거나, 권한 부족, 또는 선행 조건 미충족)",
            1618 => "다른 설치 프로그램(msiexec)이 이미 실행 중임 (잠시 후 재시도)",
            1619 => "MSI 설치 패키지를 열 수 없음 (경로 또는 파일 손상 확인)",
            1625 => "시스템 정책에 의해 이 설치가 금지됨",
            1638 => "이 제품의 다른 버전이 이미 설치되어 있음",
            3010 => "설치 성공 (시스템 재부팅 필요)",
            _ => $"비정상 종료 코드 ({code})"
        };
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

