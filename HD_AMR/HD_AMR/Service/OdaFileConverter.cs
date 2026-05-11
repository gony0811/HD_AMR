using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD_AMR.Service;

public class OdaFileConverter : IDwgConverter
{
    private readonly DwgConversionOptions _options;
    private readonly ILogger<OdaFileConverter> _logger;
    private readonly Lazy<string?> _resolvedPath;
    private readonly Lazy<IReadOnlyList<string>> _searchedDirectories;

    public OdaFileConverter(IOptions<DwgConversionOptions> options, ILogger<OdaFileConverter> logger)
    {
        _options = options.Value;
        _logger = logger;
        _searchedDirectories = new Lazy<IReadOnlyList<string>>(BuildSearchDirectories);
        _resolvedPath = new Lazy<string?>(ResolveExecutablePath);
    }

    public string? ConfiguredPath => _resolvedPath.Value ?? _options.OdaFileConverterPath;

    public bool IsAvailable => _resolvedPath.Value != null;

    public async Task<ConversionResult> ConvertAsync(string dwgPath, string outputDir, CancellationToken ct = default)
    {
        var exe = _resolvedPath.Value;
        if (exe == null)
        {
            var searched = string.Join(", ", _searchedDirectories.Value);
            var configured = string.IsNullOrWhiteSpace(_options.OdaFileConverterPath) ? "(미설정)" : _options.OdaFileConverterPath;
            return new ConversionResult(false, null,
                $"ODA File Converter를 찾을 수 없습니다. 설정 경로({configured}) 및 자동 감지 후보({searched})에서 ODAFileConverter*.app 매칭 없음.");
        }

        if (!File.Exists(dwgPath))
            return new ConversionResult(false, null, $"입력 파일이 존재하지 않습니다: {dwgPath}");

        Directory.CreateDirectory(outputDir);

        var workRoot = Path.Combine(Path.GetTempPath(), "hdamr_dwg_" + Guid.NewGuid().ToString("N"));
        var workInput = Path.Combine(workRoot, "in");
        var workOutput = Path.Combine(workRoot, "out");
        Directory.CreateDirectory(workInput);
        Directory.CreateDirectory(workOutput);

        try
        {
            var inputName = Path.GetFileName(dwgPath);
            var stagedInput = Path.Combine(workInput, inputName);
            File.Copy(dwgPath, stagedInput, overwrite: true);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(workInput);
            psi.ArgumentList.Add(workOutput);
            psi.ArgumentList.Add(_options.OutputVersion);
            psi.ArgumentList.Add("DXF");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("*.DWG");

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new ConversionResult(false, null, $"변환 타임아웃 ({_options.TimeoutSeconds}초 초과)");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var producedDxf = Directory.EnumerateFiles(workOutput, "*.dxf", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (producedDxf == null)
            {
                _logger.LogWarning("ODA conversion produced no DXF. ExitCode={Code} stderr={Err}", proc.ExitCode, stderr);
                var msg = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return new ConversionResult(false, null, $"DXF 생성 실패 (exit={proc.ExitCode}): {msg.Trim()}");
            }

            var destDxfName = Path.GetFileNameWithoutExtension(dwgPath) + ".dxf";
            var destPath = Path.Combine(outputDir, destDxfName);
            File.Move(producedDxf, destPath, overwrite: true);

            return new ConversionResult(true, destPath, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DWG conversion error");
            return new ConversionResult(false, null, $"변환 중 오류: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { }
        }
    }

    private string? ResolveExecutablePath()
    {
        var configured = _options.OdaFileConverterPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            _logger.LogInformation("ODA File Converter: configured path used ({Path})", configured);
            return configured;
        }

        foreach (var dir in _searchedDirectories.Value)
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> appDirs;
            try
            {
                appDirs = Directory.EnumerateDirectories(dir, "ODAFileConverter*.app");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not enumerate {Dir}", dir);
                continue;
            }

            foreach (var appDir in appDirs)
            {
                var bin = Path.Combine(appDir, "Contents", "MacOS", "ODAFileConverter");
                if (File.Exists(bin))
                {
                    _logger.LogInformation("ODA File Converter: auto-detected at {Path}", bin);
                    return bin;
                }
            }
        }

        return null;
    }

    private IReadOnlyList<string> BuildSearchDirectories()
    {
        var dirs = new List<string>();

        var configured = _options.OdaFileConverterPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var node = configured;
            while (!string.IsNullOrEmpty(node) && !node.EndsWith(".app", StringComparison.Ordinal))
            {
                var parent = Path.GetDirectoryName(node);
                if (string.IsNullOrEmpty(parent) || parent == node) { node = null; break; }
                node = parent;
            }
            if (!string.IsNullOrEmpty(node))
            {
                var bundleParent = Path.GetDirectoryName(node);
                if (!string.IsNullOrEmpty(bundleParent)) dirs.Add(bundleParent);
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            dirs.Add("/Applications");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) dirs.Add(Path.Combine(home, "Applications"));
        }

        return dirs.Distinct(StringComparer.Ordinal).ToList();
    }
}
