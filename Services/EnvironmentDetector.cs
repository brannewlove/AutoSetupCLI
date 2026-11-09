using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using Microsoft.Win32;
using AutoSetupCLI.Configuration;

namespace AutoSetupCLI.Services;

public class EnvironmentDetector
{
    private readonly Logger _logger;
    private readonly ParameterResolver _resolver;

    public EnvironmentDetector(Logger logger, ParameterResolver resolver)
    {
        _logger = logger;
        _resolver = resolver;
    }

    /// <summary>
    /// 감지 규칙을 평가하여 조건 일치 여부를 반환합니다.
    /// </summary>
    public bool EvaluateRule(DetectionRule rule)
    {
        bool matched = false;

        switch (rule.Type)
        {
            case RuleType.Registry:
                matched = CheckRegistry(rule);
                break;
            case RuleType.File:
                matched = CheckFile(rule);
                break;
            case RuleType.Service:
                matched = CheckService(rule);
                break;
            case RuleType.Process:
                matched = CheckProcess(rule);
                break;
            case RuleType.Command:
                matched = CheckCommand(rule);
                break;
        }

        bool finalResult = rule.Invert ? !matched : matched;
        _logger.Debug($"[감지 상세] Type={rule.Type}, Operator={rule.Operator}, Invert={rule.Invert} => 원본결과={matched}, 최종판정={finalResult}");
        return finalResult;
    }

    private bool CheckRegistry(DetectionRule rule)
    {
        if (string.IsNullOrEmpty(rule.RegistryKey)) return false;

        var fullPath = _resolver.Resolve(rule.RegistryKey);
        var (hive, subKey) = ParseRegistryPath(fullPath);
        if (hive == null || string.IsNullOrEmpty(subKey)) return false;

        // 64비트 및 32비트 레지스트리 뷰 모두 확인
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

        foreach (var view in views)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive.Value, view);
                using var key = baseKey.OpenSubKey(subKey);
                if (key == null) continue;

                if (string.IsNullOrEmpty(rule.ValueName))
                {
                    // 키 자체의 존재 여부
                    _logger.Debug($"[레지스트리 감지됨] 경로='{fullPath}' ({view})");
                    return true;
                }

                var valObj = key.GetValue(rule.ValueName);
                if (valObj == null) continue;

                var actualVal = valObj.ToString() ?? "";
                var expected = _resolver.Resolve(rule.ExpectedValue ?? "");
                _logger.Debug($"[레지스트리 값 확인] 경로='{fullPath}\\{rule.ValueName}' ({view}) => 실제값='{actualVal}', 기대값='{expected}'");

                switch (rule.Operator)
                {
                    case RuleOperator.Exists:
                        return true;
                    case RuleOperator.Equals:
                        if (actualVal.Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
                        break;
                    case RuleOperator.Contains:
                        if (actualVal.Contains(expected, StringComparison.OrdinalIgnoreCase)) return true;
                        break;
                    case RuleOperator.VersionGreaterOrEqual:
                        if (Version.TryParse(actualVal, out var actualVer) &&
                            Version.TryParse(expected, out var expVer))
                        {
                            if (actualVer >= expVer) return true;
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"레지스트리 감지 중 오류 ({subKey}): {ex.Message}");
            }
        }

        return false;
    }

    private bool CheckFile(DetectionRule rule)
    {
        if (string.IsNullOrEmpty(rule.FilePath)) return false;
        var path = _resolver.Resolve(rule.FilePath);

        if (!File.Exists(path)) return false;

        if (rule.Operator == RuleOperator.Exists) return true;

        if (rule.Operator == RuleOperator.VersionGreaterOrEqual && !string.IsNullOrEmpty(rule.ExpectedValue))
        {
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(path);
                if (Version.TryParse(fvi.FileVersion, out var actualVer) &&
                    Version.TryParse(_resolver.Resolve(rule.ExpectedValue), out var expVer))
                {
                    return actualVer >= expVer;
                }
            }
            catch { }
        }

        return true;
    }

    private bool CheckService(DetectionRule rule)
    {
        if (string.IsNullOrEmpty(rule.ServiceName)) return false;
        var svcName = _resolver.Resolve(rule.ServiceName);

        try
        {
            using var sc = new ServiceController(svcName);
            if (rule.Operator == RuleOperator.Exists)
            {
                // 서비스가 존재하면 Status 접근 시 예외가 발생하지 않음
                _ = sc.Status;
                return true;
            }
            if (rule.Operator == RuleOperator.Equals)
            {
                var expected = _resolver.Resolve(rule.ExpectedValue ?? "Running");
                return sc.Status.ToString().Equals(expected, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private bool CheckProcess(DetectionRule rule)
    {
        if (string.IsNullOrEmpty(rule.ProcessName)) return false;
        var procName = _resolver.Resolve(rule.ProcessName);
        if (procName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            procName = Path.GetFileNameWithoutExtension(procName);
        }

        var procs = Process.GetProcessesByName(procName);
        return procs.Length > 0;
    }

    private bool CheckCommand(DetectionRule rule)
    {
        // Custom exit-code command check
        if (string.IsNullOrEmpty(rule.FilePath)) return false;
        try
        {
            var cmd = _resolver.Resolve(rule.FilePath);
            var psi = new ProcessStartInfo("cmd.exe", $"/c {cmd}")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (RegistryHive? hive, string subKey) ParseRegistryPath(string path)
    {
        var parts = path.Split('\\', 2);
        if (parts.Length < 2) return (null, path);

        var hiveStr = parts[0].ToUpperInvariant();
        var subKey = parts[1];

        RegistryHive? hive = hiveStr switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            _ => null
        };

        return (hive, subKey);
    }
}

