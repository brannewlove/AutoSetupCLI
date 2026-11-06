namespace AutoSetupCLI.Services;

public class ParameterResolver
{
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);

    public ParameterResolver()
    {
        // 기본 시스템 환경 변수 등록
        _variables["MachineName"] = Environment.MachineName;
        _variables["UserName"] = Environment.UserName;
        _variables["UserDomain"] = Environment.UserDomainName;
        _variables["SystemDrive"] = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        _variables["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        _variables["ProgramFilesX86"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        _variables["BaseDir"] = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        _variables["Date"] = DateTime.Now.ToString("yyyyMMdd");
    }

    public void SetVariable(string key, string value)
    {
        _variables[key] = value;
    }

    public string? GetVariable(string key)
    {
        return _variables.TryGetValue(key, out var val) ? val : null;
    }

    public IReadOnlyDictionary<string, string> GetAllVariables() => _variables;

    /// <summary>
    /// 문자열 내의 {Key} 형태의 템플릿 태그를 등록된 변수 값으로 재귀 치환합니다.
    /// </summary>
    public string Resolve(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var result = input;
        foreach (var kvp in _variables)
        {
            var placeholder = $"{{{kvp.Key}}}";
            if (result.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                result = ReplaceCaseInsensitive(result, placeholder, kvp.Value);
            }
        }
        return result;
    }

    private static string ReplaceCaseInsensitive(string str, string oldValue, string newValue)
    {
        int prevPos = 0;
        var sb = new System.Text.StringBuilder();

        while (true)
        {
            int pos = str.IndexOf(oldValue, prevPos, StringComparison.OrdinalIgnoreCase);
            if (pos < 0)
            {
                sb.Append(str.Substring(prevPos));
                break;
            }

            sb.Append(str.Substring(prevPos, pos - prevPos));
            sb.Append(newValue);
            prevPos = pos + oldValue.Length;
        }

        return sb.ToString();
    }
}

