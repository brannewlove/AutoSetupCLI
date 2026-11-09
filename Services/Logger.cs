namespace AutoSetupCLI.Services;

public class Logger
{
    private readonly string? _logFilePath;
    private readonly object _lock = new();

    public string? LogFilePath => _logFilePath;
    public string? LogDirectory => string.IsNullOrEmpty(_logFilePath) ? null : Path.GetDirectoryName(_logFilePath);

    public Logger(string? logFilePath = null)
    {
        _logFilePath = logFilePath;
        if (!string.IsNullOrEmpty(_logFilePath))
        {
            try
            {
                var dir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch
            {
                // 읽기 전용 환경(Windows Sandbox 등)인 경우 임시 디렉터리로 폴백
                try
                {
                    var fallbackDir = Path.Combine(Path.GetTempPath(), "AutoSetupLogs");
                    Directory.CreateDirectory(fallbackDir);
                    _logFilePath = Path.Combine(fallbackDir, Path.GetFileName(_logFilePath));
                }
                catch
                {
                    _logFilePath = null;
                }
            }
        }
    }

    public void Info(string message) => Write(ConsoleColor.Cyan, "[INFO]", message);
    public void Success(string message) => Write(ConsoleColor.Green, "[SUCCESS]", message);
    public void Warn(string message) => Write(ConsoleColor.Yellow, "[WARN]", message);
    public void Error(string message) => Write(ConsoleColor.Red, "[ERROR]", message);
    public void Debug(string message) => Write(ConsoleColor.DarkGray, "[DEBUG]", message);
    public void Step(string title)
    {
        Console.WriteLine();
        Write(ConsoleColor.Magenta, "==>", $" {title}");
    }

    private void Write(ConsoleColor color, string prefix, string message)
    {
        lock (_lock)
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var originalColor = Console.ForegroundColor;
            
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{time}] ");
            
            Console.ForegroundColor = color;
            Console.Write($"{prefix,-9} ");
            
            Console.ForegroundColor = originalColor;
            Console.WriteLine(message);

            if (!string.IsNullOrEmpty(_logFilePath))
            {
                try
                {
                    File.AppendAllText(_logFilePath, $"[{time}] {prefix,-9} {message}{Environment.NewLine}");
                }
                catch { }
            }
        }
    }
}

