using System.Text;

namespace AutoSetupCLI.Services;

public class IssTemplateEngine
{
    private readonly ParameterResolver _resolver;
    private readonly Logger _logger;
    private readonly List<string> _tempFiles = new();

    public IssTemplateEngine(ParameterResolver resolver, Logger logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// .iss 템플릿 파일의 {변수}를 실제 값으로 치환하여 임시 응답 파일을 생성합니다.
    /// </summary>
    public string GenerateResponseFile(string templatePath)
    {
        var resolvedPath = _resolver.Resolve(templatePath);
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"InstallShield 응답 템플릿 파일을 찾을 수 없습니다: {resolvedPath}");
        }

        var content = File.ReadAllText(resolvedPath, Encoding.Default);
        var resolvedContent = _resolver.Resolve(content);

        var tempPath = Path.Combine(Path.GetTempPath(), $"AutoSetup_{Guid.NewGuid():N}.iss");
        File.WriteAllText(tempPath, resolvedContent, Encoding.Default);
        _tempFiles.Add(tempPath);

        _logger.Debug($"InstallShield 응답 파일 동적 생성 완료: {tempPath}");
        return tempPath;
    }

    public void Cleanup()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch { }
        }
        _tempFiles.Clear();
    }
}

