using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoSetupCLI.Configuration;
using AutoSetupCLI.Services;

namespace AutoSetupCLI;

public class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 1. 명령줄 옵션 파싱
        var options = ParseArguments(args);

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        // 로거 초기화
        var logFile = options.LogPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", $"Install_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        var logger = new Logger(logFile);

        PrintBanner();

        // 2. 대화형 입력 처리 (Silent 모드가 아닐 때 필수 매개변수 확인)
        if (!options.Silent && string.IsNullOrWhiteSpace(options.UserId))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("👉 사번(User ID / 인증코드)을 입력하세요: ");
            Console.ForegroundColor = ConsoleColor.White;
            options.UserId = Console.ReadLine()?.Trim() ?? "";
        }

        // 3. 파라미터 리졸버 초기화
        var resolver = new ParameterResolver();
        if (!string.IsNullOrEmpty(options.UserId)) resolver.SetVariable("UserId", options.UserId);
        if (!string.IsNullOrEmpty(options.PrinterIp)) resolver.SetVariable("PrinterIp", options.PrinterIp);
        if (!string.IsNullOrEmpty(options.Office)) resolver.SetVariable("Office", options.Office);
        if (!string.IsNullOrEmpty(options.ServerIp)) resolver.SetVariable("ServerIp", options.ServerIp);

        foreach (var customVar in options.CustomVariables)
        {
            resolver.SetVariable(customVar.Key, customVar.Value);
        }

        logger.Info($"동적 매개변수 바인딩: UserId='{options.UserId}', PrinterIp='{options.PrinterIp}', Office='{options.Office}'");
        if (options.DryRun)
        {
            logger.Warn("⚡ [DRY-RUN] 시뮬레이션 모드로 실행됩니다 (실제 설치 및 레지스트리 수정 안 됨).");
        }

        // 4. 설정 파일 로드
        var configPath = options.ConfigPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "apps_config.json");
        if (!File.Exists(configPath))
        {
            logger.Error($"설정 파일을 찾을 수 없습니다: {configPath}");
            return 1;
        }

        List<AppConfig>? apps;
        try
        {
            var json = File.ReadAllText(configPath);
            apps = JsonSerializer.Deserialize<List<AppConfig>>(json, JsonOptions);
            if (apps == null || apps.Count == 0)
            {
                logger.Error("설정 파일에 정의된 프로그램이 없습니다.");
                return 1;
            }
        }
        catch (Exception ex)
        {
            logger.Error($"설정 파일(JSON) 파싱 실패: {ex.Message}");
            return 1;
        }

        // 5. 실행 대상 필터링 및 정렬
        var targetApps = apps
            .Where(a => a.Enabled)
            .OrderBy(a => a.Order)
            .ToList();

        if (options.TargetAppKeys.Count > 0)
        {
            targetApps = targetApps
                .Where(a => options.TargetAppKeys.Contains(a.Key, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        logger.Info($"총 {targetApps.Count}개의 프로그램이 순차 실행 대상으로 지정되었습니다.");

        // 6. 엔진 및 서비스 조립
        var detector = new EnvironmentDetector(logger, resolver);
        var issEngine = new IssTemplateEngine(resolver, logger);
        var printerManager = new PrinterManager(logger, resolver);
        var executionEngine = new ExecutionEngine(logger, resolver, detector, issEngine, printerManager);

        var results = new List<InstallResult>();

        // 7. 각 프로그램 순차 무인 실행
        try
        {
            foreach (var app in targetApps)
            {
                var result = await executionEngine.ExecuteAppAsync(app, force: options.Force, dryRun: options.DryRun);
                results.Add(result);
            }
        }
        finally
        {
            issEngine.Cleanup();
        }

        // 8. 최종 결과 요약 출력
        PrintSummary(results, executionEngine.RebootRequired, logger);
        if (!string.IsNullOrEmpty(logger.LogFilePath))
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"📄 상세 실행 및 오류 분석 로그 파일: {logger.LogFilePath}");
            Console.ResetColor();
        }

        // 종료 코드 반환 (재부팅 필요: 3010, 실패 있음: 1, 성공: 0)
        if (results.Any(r => r.Status == "Failed"))
        {
            return 1;
        }
        if (executionEngine.RebootRequired)
        {
            return 3010;
        }

        return 0;
    }

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("==================================================================");
        Console.WriteLine(" 🚀 AutoSetup CLI - Enterprise Unattended Installation Engine");
        Console.WriteLine("    버전: 1.0.0 | 환경감지 · 고급분기 · 파라미터 자동화");
        Console.WriteLine("==================================================================");
        Console.ResetColor();
    }

    private static void PrintHelp()
    {
        PrintBanner();
        Console.WriteLine(@"
[사용법]
  AutoSetupCLI.exe [옵션]

[주요 옵션]
  -u, --user-id <ID>         사번 또는 사용자 ID (매개변수 {UserId}에 바인딩)
  -p, --printer-ip <IP>      복합기/프린터 IP 주소 (기본값: 192.168.1.100)
  -o, --office <Location>    사업장 위치 (Seoul, Busan 등)
  -s, --silent               무인/백그라운드 모드 (사용자 대화형 입력 비활성화)
  -d, --dry-run              시뮬레이션 모드 (실제 실행 없이 환경 감지 및 인자만 확인)
  -f, --force                기설치 감지 무시하고 강제 재설치
  -a, --apps <key1,key2>     특정 프로그램만 선택 실행 (예: --apps fasoo,pcoff)
  -c, --config <Path>        사용자 정의 apps_config.json 파일 경로
  -l, --log <Path>           로그 파일 저장 경로
  -v, --var <Key=Value>      사용자 정의 동적 변수 주입 (예: -v Dept=HR)
  -h, --help                 도움말 출력

[실행 예시]
  # 1. 일반 무인 설치 (WCD, 배치파일, 관리자 콘솔)
  AutoSetupCLI.exe --user-id 20261101 --silent

  # 2. 사전 환경 감지 및 인자 테스트 (Dry-Run)
  AutoSetupCLI.exe --user-id 20261101 --dry-run

  # 3. 특정 프로그램만 강제 재설치
  AutoSetupCLI.exe --apps sindoh --printer-ip 192.168.10.50 --user-id 20261101 --force
");
    }

    private static void PrintSummary(List<InstallResult> results, bool rebootRequired, Logger logger)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("==================================================================");
        Console.WriteLine(" 📊 최종 설치 실행 요약 보고서 (Execution Summary)");
        Console.WriteLine("==================================================================");
        Console.ResetColor();

        Console.WriteLine(string.Format("{0,-26} | {1,-12} | {2,-8} | {3,-7} | {4}", "프로그램 명칭", "상태", "ExitCode", "시간", "비고 / 오류 원인"));
        Console.WriteLine(new string('-', 90));

        foreach (var r in results)
        {
            var statusColor = r.Status switch
            {
                "Installed" => ConsoleColor.Green,
                "Skipped" => ConsoleColor.Cyan,
                "RebootRequired" => ConsoleColor.Yellow,
                _ => ConsoleColor.Red
            };

            Console.Write(string.Format("{0,-26} | ", Truncate(r.Name, 24)));
            Console.ForegroundColor = statusColor;
            Console.Write(string.Format("{0,-12}", r.Status));
            Console.ResetColor();
            Console.WriteLine(string.Format(" | {0,-8} | {1,-6:N1}s | {2}", r.ExitCode?.ToString() ?? "-", r.Duration.TotalSeconds, r.Message));
        }

        Console.WriteLine(new string('-', 90));

        int successCount = results.Count(r => r.Status is "Installed" or "RebootRequired");
        int skippedCount = results.Count(r => r.Status == "Skipped");
        int failedCount = results.Count(r => r.Status == "Failed");

        Console.WriteLine($"총계: 성공 {successCount}개 / 스킵 {skippedCount}개 / 실패 {failedCount}개");
        if (rebootRequired)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("⚠️ 주의: 일부 프로그램 설치 완료를 위해 시스템 재부팅이 필요합니다.");
            Console.ResetColor();
        }
        Console.WriteLine("==================================================================");
    }

    private static string Truncate(string val, int maxLength)
    {
        if (string.IsNullOrEmpty(val)) return "";
        return val.Length <= maxLength ? val : val.Substring(0, maxLength - 2) + "..";
    }

    private static CliOptions ParseArguments(string[] args)
    {
        var options = new CliOptions();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    break;
                case "-s":
                case "--silent":
                    options.Silent = true;
                    break;
                case "-d":
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "-f":
                case "--force":
                    options.Force = true;
                    break;
                case "-u":
                case "--user-id":
                    if (i + 1 < args.Length) options.UserId = args[++i];
                    break;
                case "-p":
                case "--printer-ip":
                    if (i + 1 < args.Length) options.PrinterIp = args[++i];
                    break;
                case "-o":
                case "--office":
                    if (i + 1 < args.Length) options.Office = args[++i];
                    break;
                case "--server-ip":
                    if (i + 1 < args.Length) options.ServerIp = args[++i];
                    break;
                case "-c":
                case "--config":
                    if (i + 1 < args.Length) options.ConfigPath = args[++i];
                    break;
                case "-l":
                case "--log":
                    if (i + 1 < args.Length) options.LogPath = args[++i];
                    break;
                case "-a":
                case "--apps":
                    if (i + 1 < args.Length)
                    {
                        var list = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        options.TargetAppKeys.AddRange(list);
                    }
                    break;
                case "-v":
                case "--var":
                    if (i + 1 < args.Length)
                    {
                        var rawVar = args[++i];
                        var split = rawVar.Split('=', 2);
                        if (split.Length == 2)
                        {
                            options.CustomVariables[split[0].Trim()] = split[1].Trim();
                        }
                    }
                    break;
            }
        }

        return options;
    }
}

public class CliOptions
{
    public bool ShowHelp { get; set; }
    public bool Silent { get; set; }
    public bool DryRun { get; set; }
    public bool Force { get; set; }
    public string UserId { get; set; } = "";
    public string PrinterIp { get; set; } = "192.168.1.100";
    public string Office { get; set; } = "HQ";
    public string ServerIp { get; set; } = "";
    public string? ConfigPath { get; set; }
    public string? LogPath { get; set; }
    public List<string> TargetAppKeys { get; set; } = new();
    public Dictionary<string, string> CustomVariables { get; set; } = new();
}
