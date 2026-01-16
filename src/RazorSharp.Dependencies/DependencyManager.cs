using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RazorSharp.Dependencies;

/// <summary>
/// Manages downloading and caching of Roslyn and Razor dependencies.
/// </summary>
public class DependencyManager
{
    readonly ILogger<DependencyManager> _logger;
    readonly string _basePath;
    readonly HttpClient _httpClient;

    // Roslyn Language Server version from Crashdummyy/roslynLanguageServer
    // This provides platform-specific builds that actually work
    const string RoslynVersion = "5.4.0-2.26065.8";

    // VS Code C# extension version - used for Razor extension only
    const string ExtensionVersion = "2.111.2";

    public DependencyManager(ILogger<DependencyManager> logger, string? basePath = null)
    {
        _logger = logger;
        _basePath = basePath ?? GetDefaultBasePath();
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", $"RazorSharp/{VersionHelper.GetAssemblyVersion()}");
    }

    public string BasePath => _basePath;
    public string RoslynPath => Path.Combine(_basePath, "roslyn");
    public string RazorExtensionPath => Path.Combine(_basePath, "razorExtension");
    public string VersionFilePath => Path.Combine(_basePath, "version.json");

    /// <summary>
    /// Gets the path to the Roslyn language server DLL (requires dotnet to run).
    /// </summary>
    public string RoslynServerDllPath =>
        Path.Combine(RoslynPath, "Microsoft.CodeAnalysis.LanguageServer.dll");

    /// <summary>
    /// Gets the path to the Razor source generator DLL.
    /// </summary>
    public string RazorSourceGeneratorPath =>
        Path.Combine(RazorExtensionPath, "Microsoft.CodeAnalysis.Razor.Compiler.dll");

    /// <summary>
    /// Gets the path to the Razor design-time targets.
    /// </summary>
    public string RazorDesignTimePath =>
        Path.Combine(RazorExtensionPath, "Targets", "Microsoft.NET.Sdk.Razor.DesignTime.targets");

    /// <summary>
    /// Gets the path to the Razor extension DLL.
    /// </summary>
    public string RazorExtensionDllPath =>
        Path.Combine(RazorExtensionPath, "Microsoft.VisualStudioCode.RazorExtension.dll");

    /// <summary>
    /// Ensures all dependencies are downloaded and ready.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="onProgress">Optional callback for progress updates</param>
    public async Task<bool> EnsureDependenciesAsync(CancellationToken cancellationToken = default, Action<string>? onProgress = null)
    {
        try
        {
            Directory.CreateDirectory(_basePath);

            var installedVersion = GetInstalledVersion();
            var expectedVersion = $"{RoslynVersion}+{ExtensionVersion}";

            if (installedVersion?.Version == expectedVersion && AreDependenciesComplete())
            {
                _logger.LogInformation("Dependencies are up to date (Roslyn {RoslynVersion}, Extension {ExtensionVersion})",
                    RoslynVersion, ExtensionVersion);
                onProgress?.Invoke("Dependencies are up to date");
                return true;
            }

            // Download Roslyn Language Server (platform-specific)
            _logger.LogInformation("Downloading Roslyn Language Server (version {Version})...", RoslynVersion);
            onProgress?.Invoke($"Downloading Roslyn Language Server ({RoslynVersion})...");
            await DownloadRoslynLanguageServerAsync(cancellationToken, onProgress);

            // Download Razor extension from VS Code C# extension
            _logger.LogInformation("Downloading Razor extension (version {Version})...", ExtensionVersion);
            onProgress?.Invoke($"Downloading Razor extension ({ExtensionVersion})...");
            await DownloadRazorExtensionAsync(cancellationToken, onProgress);

            SaveVersionInfo(expectedVersion);

            _logger.LogInformation("Dependencies downloaded successfully");
            onProgress?.Invoke("Dependencies downloaded successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure dependencies");
            return false;
        }
    }

    /// <summary>
    /// Checks if all required dependency files exist.
    /// </summary>
    public bool AreDependenciesComplete()
    {
        var complete = File.Exists(RoslynServerDllPath)
            && File.Exists(RazorSourceGeneratorPath)
            && File.Exists(RazorExtensionDllPath);

        // Design-time targets may not exist in all versions, so just warn
        if (complete && !File.Exists(RazorDesignTimePath))
        {
            _logger.LogWarning("Razor design-time targets not found at {Path}", RazorDesignTimePath);
        }

        return complete;
    }

    private async Task DownloadRoslynLanguageServerAsync(CancellationToken cancellationToken, Action<string>? onProgress = null)
    {
        var platform = GetRoslynPlatform();
        var url = $"https://github.com/Crashdummyy/roslynLanguageServer/releases/download/{RoslynVersion}/microsoft.codeanalysis.languageserver.{platform}.zip";

        _logger.LogInformation("Downloading Roslyn from {Url}", url);

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"roslyn-{Guid.NewGuid()}.zip");
        try
        {
            await DownloadFileAsync(url, tempZipPath, cancellationToken,
                percent => onProgress?.Invoke($"Downloading Roslyn... {percent}%"));

            onProgress?.Invoke("Extracting Roslyn...");

            if (Directory.Exists(RoslynPath))
            {
                Directory.Delete(RoslynPath, recursive: true);
            }
            Directory.CreateDirectory(RoslynPath);

            // Extract to roslyn directory
            await Task.Run(() => ZipFile.ExtractToDirectory(tempZipPath, RoslynPath), cancellationToken);

            _logger.LogInformation("Extracted Roslyn language server to {Path}", RoslynPath);
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                File.Delete(tempZipPath);
            }
        }
    }

    private async Task DownloadRazorExtensionAsync(CancellationToken cancellationToken, Action<string>? onProgress = null)
    {
        var extensionUrl = GetExtensionDownloadUrl();

        _logger.LogInformation("Downloading C# extension from {Url}", extensionUrl);

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"csharp-extension-{Guid.NewGuid()}.vsix");
        try
        {
            await DownloadFileAsync(extensionUrl, tempZipPath, cancellationToken,
                percent => onProgress?.Invoke($"Downloading Razor extension... {percent}%"));

            onProgress?.Invoke("Extracting Razor extension...");
            await ExtractRazorExtensionAsync(tempZipPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                File.Delete(tempZipPath);
            }
        }
    }

    private async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken, Action<int>? onProgress = null)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        int lastLoggedPercent = -10; // Start at -10 so first 0% gets logged

        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;

            if (totalBytes.HasValue)
            {
                var percent = (int)(totalRead * 100 / totalBytes.Value);
                // Only log/report every 10% to avoid spamming
                if (percent >= lastLoggedPercent + 10)
                {
                    _logger.LogDebug("Download progress: {Percent}%", percent);
                    onProgress?.Invoke(percent);
                    lastLoggedPercent = percent;
                }
            }
        }
    }

    private async Task ExtractRazorExtensionAsync(string zipPath, CancellationToken cancellationToken)
    {
        var tempExtractPath = Path.Combine(Path.GetTempPath(), $"csharp-extension-extract-{Guid.NewGuid()}");
        try
        {
            _logger.LogInformation("Extracting Razor extension...");

            // VSIX is just a ZIP file
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tempExtractPath), cancellationToken);

            // Find and copy Razor extension
            var razorSource = Path.Combine(tempExtractPath, "extension", ".razorExtension");
            if (Directory.Exists(razorSource))
            {
                if (Directory.Exists(RazorExtensionPath))
                {
                    Directory.Delete(RazorExtensionPath, recursive: true);
                }
                CopyDirectory(razorSource, RazorExtensionPath);
                _logger.LogInformation("Extracted Razor extension to {Path}", RazorExtensionPath);
            }
            else
            {
                throw new InvalidOperationException($"Razor extension not found in VSIX at {razorSource}");
            }
        }
        finally
        {
            if (Directory.Exists(tempExtractPath))
            {
                Directory.Delete(tempExtractPath, recursive: true);
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destDir = Path.Combine(destinationDir, Path.GetFileName(dir));
            CopyDirectory(dir, destDir);
        }
    }

    private static string GetRoslynPlatform()
    {
        // Determine platform for Roslyn Language Server download
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "win-arm64",
                _ => "win-x64"
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "osx-arm64",
                _ => "osx-x64"
            };
        }
        else // Linux
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "linux-arm64",
                _ => "linux-x64"
            };
        }
    }

    private static string GetExtensionDownloadUrl()
    {
        // Download from VS Code marketplace using the vsassets URL
        // This gets the universal (platform-neutral) package for the Razor extension
        return $"https://ms-dotnettools.gallery.vsassets.io/_apis/public/gallery/publisher/ms-dotnettools/extension/csharp/{ExtensionVersion}/assetbyname/Microsoft.VisualStudio.Services.VSIXPackage";
    }

    private static string GetDefaultBasePath()
    {
        // Use XDG_CACHE_HOME if set (Linux/macOS)
        var cacheDir = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrEmpty(cacheDir))
        {
            return Path.Combine(cacheDir, "razorsharp");
        }

        // On Windows, use LocalApplicationData
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "razorsharp");
        }

        // Linux/macOS fallback: ~/.cache
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".cache", "razorsharp");
    }

    private DependencyVersionInfo? GetInstalledVersion()
    {
        if (!File.Exists(VersionFilePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(VersionFilePath);
            return JsonSerializer.Deserialize<DependencyVersionInfo>(json);
        }
        catch
        {
            return null;
        }
    }

    private void SaveVersionInfo(string version)
    {
        var info = new DependencyVersionInfo
        {
            Version = version,
            InstalledAt = DateTime.UtcNow,
            Platform = RuntimeInformation.RuntimeIdentifier
        };

        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(VersionFilePath, json);
    }
}

public class DependencyVersionInfo
{
    public string? Version { get; set; }
    public DateTime InstalledAt { get; set; }
    public string? Platform { get; set; }
}
