using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Buffers;
using System.Threading;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Utilities;

namespace RazorSharp.Dependencies;

/// <summary>
/// Manages downloading and caching of Roslyn and Razor dependencies.
/// </summary>
public class DependencyManager : IDisposable
{
    readonly ILogger<DependencyManager> _logger;
    readonly string _basePath;
    readonly StringComparison _dependencyPathComparison;
    readonly HttpClient _httpClient;
    bool _disposed;
    static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(10);
    static readonly TimeSpan VersionInfoStaleThreshold = TimeSpan.FromDays(30);
    static readonly TimeSpan DependencyLockTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan DependencyLockRetryDelay = TimeSpan.FromMilliseconds(200);
    static readonly JsonSerializerOptions VersionInfoJsonOptions = new() { WriteIndented = true };
    static readonly string ExtensionQueryPayload = JsonSerializer.Serialize(new
    {
        filters = new[]
        {
            new
            {
                criteria = new[]
                {
                    new { filterType = 7, value = "ms-dotnettools.csharp" }
                }
            }
        },
        flags = 1 // IncludeVersions
    });
    const int MaxDownloadRetries = 3;
    const int MaxUpdateCheckRetries = 2;
    const int UpdateRetryBaseDelayMs = 250;
    const int MaxZipEntries = 20000;
    const long MaxTotalUncompressedBytes = 1L * 1024 * 1024 * 1024;
    const long MaxSingleEntryUncompressedBytes = 256L * 1024 * 1024;
    long _downloadRetryCount;

    const string UpdateRootDirectoryName = "updates";
    const string DependencyLockFileName = ".dependency.lock";
    internal Func<CancellationToken, Task<string?>>? GetLatestRoslynVersionOverride;
    internal Func<CancellationToken, Task<string?>>? GetLatestExtensionVersionOverride;
    internal Func<string, string, string, CancellationToken, Task>? DownloadUpdateOverride;
    internal Func<CancellationToken, Action<string>?, Task<bool>>? EnsureDependenciesOverride;
    string? _pinnedRoslynVersion;
    string? _pinnedExtensionVersion;

    public DependencyManager(ILogger<DependencyManager> logger, string version, string? basePath = null)
    {
        _logger = logger;
        _basePath = basePath ?? GetDefaultBasePath();
        _dependencyPathComparison = FileSystemCaseSensitivity.IsCaseInsensitiveForPath(_basePath)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", $"RazorSharp/{version}");
        _httpClient.Timeout = DefaultDownloadTimeout;

        var architecture = RuntimeInformation.ProcessArchitecture;
        if (architecture is Architecture.X86 or Architecture.Arm)
        {
            _logger.LogError(
                "RazorSharp does not support 32-bit runtimes (arch: {Architecture}). Use x64 or arm64.",
                architecture);
        }
    }

    public void ConfigurePinnedVersions(string? roslynVersion, string? extensionVersion)
    {
        _pinnedRoslynVersion = string.IsNullOrWhiteSpace(roslynVersion) ? null : roslynVersion;
        _pinnedExtensionVersion = string.IsNullOrWhiteSpace(extensionVersion) ? null : extensionVersion;
    }

    public bool HasPinnedVersions =>
        !string.IsNullOrWhiteSpace(_pinnedRoslynVersion)
        || !string.IsNullOrWhiteSpace(_pinnedExtensionVersion);

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
            if (EnsureDependenciesOverride != null)
            {
                return await EnsureDependenciesOverride(cancellationToken, onProgress);
            }

            await using var dependencyLock = await TryAcquireDependencyLockAsync(DependencyLockTimeout, cancellationToken);
            if (dependencyLock == null)
            {
                _logger.LogWarning("Timed out waiting for dependency lock; skipping dependency ensure.");
                onProgress?.Invoke("Timed out waiting for dependency lock");
                return false;
            }

            Directory.CreateDirectory(_basePath);

            var installedVersion = GetInstalledVersion();
            if (!HasPinnedVersions &&
                AreDependenciesComplete() &&
                string.IsNullOrWhiteSpace(installedVersion?.Version))
            {
                _logger.LogWarning("Dependencies appear complete but version info is missing; skipping version resolution.");
                onProgress?.Invoke("Dependencies present; version info missing");
                return true;
            }

            var targetVersions = await ResolveTargetVersionsAsync(installedVersion, cancellationToken);
            if (targetVersions == null)
            {
                if (!HasPinnedVersions && AreDependenciesComplete())
                {
                    _logger.LogWarning("Dependencies appear complete but versions could not be resolved; skipping download.");
                    onProgress?.Invoke("Dependencies present; skipping download");
                    return true;
                }

                _logger.LogError("Unable to resolve dependency versions. Configure pinned versions or check network access.");
                onProgress?.Invoke("Failed to resolve dependency versions");
                return false;
            }

            var (targetRoslynVersion, targetExtensionVersion) = targetVersions.Value;
            var expectedVersion = BuildCombinedVersion(targetRoslynVersion, targetExtensionVersion);

            if (installedVersion?.Version == expectedVersion && AreDependenciesComplete())
            {
                _logger.LogInformation("Dependencies are up to date (Roslyn {RoslynVersion}, Extension {ExtensionVersion})",
                    targetRoslynVersion, targetExtensionVersion);
                onProgress?.Invoke("Dependencies are up to date");
                return true;
            }

            // Download Roslyn Language Server (platform-specific)
            _logger.LogInformation("Downloading Roslyn Language Server (version {Version})...", targetRoslynVersion);
            onProgress?.Invoke($"Downloading Roslyn Language Server ({targetRoslynVersion})...");
            await DownloadRoslynLanguageServerAsync(targetRoslynVersion, RoslynPath, cancellationToken, onProgress);

            // Download Razor extension from VS Code C# extension
            _logger.LogInformation("Downloading Razor extension (version {Version})...", targetExtensionVersion);
            onProgress?.Invoke($"Downloading Razor extension ({targetExtensionVersion})...");
            await DownloadRazorExtensionAsync(targetExtensionVersion, RazorExtensionPath, cancellationToken, onProgress);

            SaveVersionInfo(expectedVersion, targetRoslynVersion, targetExtensionVersion);

            _logger.LogInformation("Dependencies downloaded successfully");
            if (_downloadRetryCount > 0)
            {
                _logger.LogInformation("Dependency downloads retried {RetryCount} time(s)", _downloadRetryCount);
            }
            onProgress?.Invoke("Dependencies downloaded successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure dependencies");
            return false;
        }
    }

    private async Task<(string RoslynVersion, string ExtensionVersion)?> ResolveTargetVersionsAsync(
        DependencyVersionInfo? info,
        CancellationToken cancellationToken)
    {
        string? roslynVersion = _pinnedRoslynVersion;
        string? extensionVersion = _pinnedExtensionVersion;

        if (string.IsNullOrWhiteSpace(roslynVersion))
        {
            try
            {
                if (GetLatestRoslynVersionOverride != null)
                {
                    roslynVersion = await GetLatestRoslynVersionOverride(cancellationToken);
                    if (info != null && !string.IsNullOrWhiteSpace(roslynVersion))
                    {
                        info.LastKnownRoslynVersion = roslynVersion;
                    }
                }
                else
                {
                    var result = await GetLatestRoslynVersionAsync(null, null, cancellationToken);
                    if (result.Success && !string.IsNullOrWhiteSpace(result.Version))
                    {
                        roslynVersion = result.Version;
                        if (info != null)
                        {
                            info.LastKnownRoslynVersion = result.Version;
                            if (!string.IsNullOrWhiteSpace(result.ETag))
                            {
                                info.RoslynReleasesETag = result.ETag;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve latest Roslyn version");
            }
        }

        if (string.IsNullOrWhiteSpace(roslynVersion))
        {
            roslynVersion = info?.LastKnownRoslynVersion;
            if (!string.IsNullOrWhiteSpace(roslynVersion))
            {
                _logger.LogWarning("Using cached Roslyn version {Version} because latest resolution failed", roslynVersion);
            }
        }

        if (string.IsNullOrWhiteSpace(extensionVersion))
        {
            try
            {
                if (GetLatestExtensionVersionOverride != null)
                {
                    extensionVersion = await GetLatestExtensionVersionOverride(cancellationToken);
                    if (info != null && !string.IsNullOrWhiteSpace(extensionVersion))
                    {
                        info.LastKnownExtensionVersion = extensionVersion;
                    }
                }
                else
                {
                    var result = await GetLatestExtensionVersionAsync(null, null, cancellationToken);
                    if (result.Success && !string.IsNullOrWhiteSpace(result.Version))
                    {
                        extensionVersion = result.Version;
                        if (info != null)
                        {
                            info.LastKnownExtensionVersion = result.Version;
                            if (!string.IsNullOrWhiteSpace(result.ETag))
                            {
                                info.ExtensionVersionsETag = result.ETag;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve latest extension version");
            }
        }

        if (string.IsNullOrWhiteSpace(extensionVersion))
        {
            extensionVersion = info?.LastKnownExtensionVersion;
            if (!string.IsNullOrWhiteSpace(extensionVersion))
            {
                _logger.LogWarning("Using cached extension version {Version} because latest resolution failed", extensionVersion);
            }
        }

        if (string.IsNullOrWhiteSpace(roslynVersion) || string.IsNullOrWhiteSpace(extensionVersion))
        {
            return null;
        }

        if (_pinnedRoslynVersion != null || _pinnedExtensionVersion != null)
        {
            _logger.LogInformation(
                "Using pinned dependency versions (Roslyn {RoslynVersion}, Extension {ExtensionVersion})",
                roslynVersion,
                extensionVersion);
        }
        else
        {
            _logger.LogInformation("Using latest dependency versions (Roslyn {RoslynVersion}, Extension {ExtensionVersion})",
                roslynVersion, extensionVersion);
        }

        return (roslynVersion, extensionVersion);
    }

    /// <summary>
    /// Checks if all required dependency files exist.
    /// </summary>
    public bool AreDependenciesComplete()
    {
        return AreDependenciesCompleteAt(RoslynPath, RazorExtensionPath, logDesignTimeMissing: true);
    }

    public bool ApplyPendingUpdateIfAvailable()
    {
        using var dependencyLock = TryAcquireDependencyLock();
        if (dependencyLock == null)
        {
            _logger.LogWarning("Dependency lock unavailable; skipping pending update apply.");
            return false;
        }

        var info = GetInstalledVersion();
        if (string.IsNullOrWhiteSpace(info?.PendingVersion))
        {
            return false;
        }

        var pendingVersion = info.PendingVersion!;
        var updateRoot = GetUpdateRoot(pendingVersion);
        var pendingRoslynPath = Path.Combine(updateRoot, "roslyn");
        var pendingRazorPath = Path.Combine(updateRoot, "razorExtension");

        if (!AreDependenciesCompleteAt(pendingRoslynPath, pendingRazorPath, logDesignTimeMissing: false))
        {
            _logger.LogWarning("Pending dependency update {Version} is incomplete; keeping existing dependencies.", pendingVersion);
            return false;
        }

        try
        {
            ReplaceDirectory(pendingRoslynPath, RoslynPath);
            ReplaceDirectory(pendingRazorPath, RazorExtensionPath);

            info.Version = pendingVersion;
            info.PendingVersion = null;
            info.InstalledAt = DateTime.UtcNow;
            info.Platform = RuntimeInformation.RuntimeIdentifier;
            WriteVersionInfo(info);

            TryDeleteDirectory(updateRoot);

            _logger.LogInformation("Applied pending dependency update {Version}", pendingVersion);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply pending dependency update {Version}", pendingVersion);
            return false;
        }
    }

    public async Task<DependencyUpdateResult> CheckForUpdatesAsync(TimeSpan minInterval, CancellationToken cancellationToken = default)
    {
        if (minInterval < TimeSpan.Zero)
        {
            minInterval = TimeSpan.Zero;
        }

        if (HasPinnedVersions)
        {
            _logger.LogInformation("Skipping dependency update check because pinned versions are configured.");
            var pinnedInfo = GetInstalledVersion();
            return new DependencyUpdateResult(DependencyUpdateStatus.Skipped, pinnedInfo?.Version, pinnedInfo?.PendingVersion);
        }

        await using var dependencyLock = await TryAcquireDependencyLockAsync(DependencyLockTimeout, cancellationToken);
        if (dependencyLock == null)
        {
            _logger.LogWarning("Timed out waiting for dependency lock; skipping update check.");
            var lockedInfo = GetInstalledVersion();
            return new DependencyUpdateResult(DependencyUpdateStatus.Skipped, lockedInfo?.Version, lockedInfo?.PendingVersion);
        }

        var info = GetInstalledVersion();

        var now = DateTime.UtcNow;
        var lastSuccess = info?.LastUpdateSuccessUtc ?? info?.LastUpdateCheckUtc;
        if (lastSuccess != null && now - lastSuccess.Value < minInterval)
        {
            _logger.LogDebug("Skipping dependency update check (last success at {LastCheck})", lastSuccess);
            return new DependencyUpdateResult(DependencyUpdateStatus.Skipped, info?.Version, info?.PendingVersion);
        }

        info ??= new DependencyVersionInfo();
        info.LastUpdateAttemptUtc = now;
        info.LastUpdateCheckUtc = now;
        WriteVersionInfo(info);

        string? latestRoslyn = _pinnedRoslynVersion;
        string? latestExtension = _pinnedExtensionVersion;
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                if (latestRoslyn == null)
                {
                    if (GetLatestRoslynVersionOverride != null)
                    {
                        latestRoslyn = await GetLatestRoslynVersionOverride(cancellationToken);
                    }
                    else
                    {
                        var roslynResult = await GetLatestRoslynVersionAsync(
                            info.RoslynReleasesETag,
                            info.LastKnownRoslynVersion,
                            cancellationToken);
                        if (!roslynResult.Success)
                        {
                            throw new InvalidOperationException("Failed to resolve latest Roslyn version.");
                        }

                        latestRoslyn = roslynResult.Version;
                        info.RoslynReleasesETag = roslynResult.ETag ?? info.RoslynReleasesETag;
                        info.LastKnownRoslynVersion = roslynResult.Version ?? info.LastKnownRoslynVersion;
                    }
                }

                if (latestExtension == null)
                {
                    if (GetLatestExtensionVersionOverride != null)
                    {
                        latestExtension = await GetLatestExtensionVersionOverride(cancellationToken);
                    }
                    else
                    {
                        var extensionResult = await GetLatestExtensionVersionAsync(
                            info.ExtensionVersionsETag,
                            info.LastKnownExtensionVersion,
                            cancellationToken);
                        if (!extensionResult.Success)
                        {
                            throw new InvalidOperationException("Failed to resolve latest extension version.");
                        }

                        latestExtension = extensionResult.Version;
                        info.ExtensionVersionsETag = extensionResult.ETag ?? info.ExtensionVersionsETag;
                        info.LastKnownExtensionVersion = extensionResult.Version ?? info.LastKnownExtensionVersion;
                    }
                }

                break;
            }
            catch (Exception ex) when (IsTransientUpdateFailure(ex, cancellationToken) && attempt < MaxUpdateCheckRetries)
            {
                latestRoslyn = _pinnedRoslynVersion;
                latestExtension = _pinnedExtensionVersion;
                var delay = TimeSpan.FromMilliseconds(UpdateRetryBaseDelayMs * attempt);
                _logger.LogWarning(ex, "Failed to check for dependency updates, retrying in {Delay} (attempt {Attempt}/{MaxAttempts})",
                    delay,
                    attempt,
                    MaxUpdateCheckRetries);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check for dependency updates");
                return new DependencyUpdateResult(DependencyUpdateStatus.Failed, info.Version, info.PendingVersion);
            }
        }

        if (string.IsNullOrWhiteSpace(latestRoslyn) || string.IsNullOrWhiteSpace(latestExtension))
        {
            _logger.LogWarning("Failed to resolve latest dependency versions");
            return new DependencyUpdateResult(DependencyUpdateStatus.Failed, info.Version, info.PendingVersion);
        }

        if (_pinnedRoslynVersion != null)
        {
            info.LastKnownRoslynVersion = _pinnedRoslynVersion;
        }
        if (_pinnedExtensionVersion != null)
        {
            info.LastKnownExtensionVersion = _pinnedExtensionVersion;
        }

        var latestCombined = BuildCombinedVersion(latestRoslyn, latestExtension);
        if (string.Equals(info.PendingVersion, latestCombined, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Dependency update {Version} already downloaded; restart to use it.", latestCombined);
            info.LastUpdateSuccessUtc = now;
            WriteVersionInfo(info);
            return new DependencyUpdateResult(DependencyUpdateStatus.UpdateAlreadyPending, info.Version, latestCombined);
        }

        var currentVersion = info.Version;

        if (!string.IsNullOrWhiteSpace(currentVersion) &&
            string.Equals(currentVersion, latestCombined, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Dependencies already up to date ({Version})", latestCombined);
            info.LastUpdateSuccessUtc = now;
            WriteVersionInfo(info);
            return new DependencyUpdateResult(DependencyUpdateStatus.NoUpdate, currentVersion, latestCombined);
        }

        try
        {
            if (DownloadUpdateOverride != null)
            {
                await DownloadUpdateOverride(latestRoslyn, latestExtension, latestCombined, cancellationToken);
            }
            else
            {
                await DownloadUpdateAsync(latestRoslyn, latestExtension, latestCombined, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download dependency update {Version}", latestCombined);
            return new DependencyUpdateResult(DependencyUpdateStatus.Failed, currentVersion, latestCombined);
        }

        info.PendingVersion = latestCombined;
        info.LastUpdateSuccessUtc = now;
        WriteVersionInfo(info);

        _logger.LogInformation("Downloaded dependency update {Version}; restart to apply.", latestCombined);
        return new DependencyUpdateResult(DependencyUpdateStatus.UpdateDownloaded, currentVersion, latestCombined);
    }

    private bool AreDependenciesCompleteAt(string roslynPath, string razorExtensionPath, bool logDesignTimeMissing)
    {
        var complete = File.Exists(GetRoslynServerDllPath(roslynPath))
            && File.Exists(GetRazorSourceGeneratorPath(razorExtensionPath))
            && File.Exists(GetRazorExtensionDllPath(razorExtensionPath));

        if (complete && logDesignTimeMissing && !File.Exists(GetRazorDesignTimePath(razorExtensionPath)))
        {
            _logger.LogWarning("Razor design-time targets not found at {Path}", GetRazorDesignTimePath(razorExtensionPath));
        }

        return complete;
    }

    private async Task DownloadUpdateAsync(string roslynVersion, string extensionVersion, string combinedVersion, CancellationToken cancellationToken)
    {
        var updateRoot = GetUpdateRoot(combinedVersion);
        TryDeleteDirectory(updateRoot);
        Directory.CreateDirectory(updateRoot);

        var roslynTarget = Path.Combine(updateRoot, "roslyn");
        var extensionTarget = Path.Combine(updateRoot, "razorExtension");

        _logger.LogInformation("Downloading dependency update {Version}", combinedVersion);
        await DownloadRoslynLanguageServerAsync(roslynVersion, roslynTarget, cancellationToken);
        await DownloadRazorExtensionAsync(extensionVersion, extensionTarget, cancellationToken);

        if (!AreDependenciesCompleteAt(roslynTarget, extensionTarget, logDesignTimeMissing: false))
        {
            throw new InvalidOperationException($"Downloaded update {combinedVersion} is incomplete.");
        }
    }

    private async Task DownloadRoslynLanguageServerAsync(string version, string destinationPath, CancellationToken cancellationToken, Action<string>? onProgress = null)
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        if (architecture is Architecture.X86 or Architecture.Arm)
        {
            _logger.LogError(
                "Roslyn language server dependencies do not support 32-bit runtimes (arch: {Architecture}). Use x64 or arm64.",
                architecture);
            throw new NotSupportedException("32-bit runtimes are not supported for Roslyn language server dependencies.");
        }

        var platform = GetRoslynPlatform(architecture);
        var url = $"https://github.com/Crashdummyy/roslynLanguageServer/releases/download/{version}/microsoft.codeanalysis.languageserver.{platform}.zip";

        _logger.LogInformation("Downloading Roslyn from {Url}", url);

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"roslyn-{Guid.NewGuid()}.zip");
        try
        {
            await DownloadFileAsync(url, tempZipPath, cancellationToken,
                percent => onProgress?.Invoke($"Downloading Roslyn... {percent}%"));

            onProgress?.Invoke("Extracting Roslyn...");

            if (Directory.Exists(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }
            Directory.CreateDirectory(destinationPath);

            // Extract to roslyn directory
            await Task.Run(() => ExtractZipToDirectorySafeInternal(
                tempZipPath,
                destinationPath,
                _dependencyPathComparison,
                MaxZipEntries,
                MaxTotalUncompressedBytes,
                MaxSingleEntryUncompressedBytes), cancellationToken);

            _logger.LogInformation("Extracted Roslyn language server to {Path}", destinationPath);
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                File.Delete(tempZipPath);
            }
        }
    }

    private async Task DownloadRazorExtensionAsync(string version, string destinationPath, CancellationToken cancellationToken, Action<string>? onProgress = null)
    {
        var extensionUrl = GetExtensionDownloadUrl(version);

        _logger.LogInformation("Downloading C# extension from {Url}", extensionUrl);

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"csharp-extension-{Guid.NewGuid()}.vsix");
        try
        {
            await DownloadFileAsync(extensionUrl, tempZipPath, cancellationToken,
                percent => onProgress?.Invoke($"Downloading Razor extension... {percent}%"));

            onProgress?.Invoke("Extracting Razor extension...");
            await ExtractRazorExtensionAsync(tempZipPath, destinationPath, cancellationToken);
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
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;
                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(destinationPath, new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    BufferSize = 81920
                });

                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    long totalRead = 0;
                    int bytesRead;
                    int lastLoggedPercent = -10; // Start at -10 so first 0% gets logged

                    while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        totalRead += bytesRead;

                        if (totalBytes.HasValue && totalBytes.Value > 0)
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

                    return;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (Exception ex) when (IsTransientDownloadFailure(ex, cancellationToken) && attempt < MaxDownloadRetries)
            {
                Interlocked.Increment(ref _downloadRetryCount);
                TryDeleteFile(destinationPath);
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                _logger.LogWarning(ex, "Download failed, retrying in {Delay} (attempt {Attempt}/{MaxAttempts})", delay, attempt, MaxDownloadRetries);
                await Task.Delay(delay, cancellationToken);
            }
            catch
            {
                TryDeleteFile(destinationPath);
                throw;
            }
        }
    }

    private static bool IsTransientDownloadFailure(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return ex is HttpRequestException || ex is IOException || ex is TaskCanceledException;
    }

    private static bool IsTransientUpdateFailure(Exception ex, CancellationToken cancellationToken)
        => IsTransientDownloadFailure(ex, cancellationToken);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }


    private async Task ExtractRazorExtensionAsync(string zipPath, string destinationPath, CancellationToken cancellationToken)
    {
        var tempExtractPath = Path.Combine(Path.GetTempPath(), $"csharp-extension-extract-{Guid.NewGuid()}");
        try
        {
            _logger.LogInformation("Extracting Razor extension...");

            // VSIX is just a ZIP file
            await Task.Run(() => ExtractZipToDirectorySafeInternal(
                zipPath,
                tempExtractPath,
                _dependencyPathComparison,
                MaxZipEntries,
                MaxTotalUncompressedBytes,
                MaxSingleEntryUncompressedBytes), cancellationToken);

            // Find and copy Razor extension
            var razorSource = Path.Combine(tempExtractPath, "extension", ".razorExtension");
            if (Directory.Exists(razorSource))
            {
                if (Directory.Exists(destinationPath))
                {
                    Directory.Delete(destinationPath, recursive: true);
                }
                CopyDirectory(razorSource, destinationPath);
                _logger.LogInformation("Extracted Razor extension to {Path}", destinationPath);
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

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var destFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            var destDir = Path.Combine(destinationDir, Path.GetFileName(dir));
            CopyDirectory(dir, destDir);
        }
    }

    internal static void ExtractZipToDirectorySafe(
        string zipPath,
        string destinationPath,
        int maxEntries = MaxZipEntries,
        long maxTotalBytes = MaxTotalUncompressedBytes,
        long maxEntryBytes = MaxSingleEntryUncompressedBytes)
    {
        var comparison = FileSystemCaseSensitivity.IsCaseInsensitiveForPath(destinationPath)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        ExtractZipToDirectorySafeInternal(
            zipPath,
            destinationPath,
            comparison,
            maxEntries,
            maxTotalBytes,
            maxEntryBytes);
    }

    private static void ExtractZipToDirectorySafeInternal(
        string zipPath,
        string destinationPath,
        StringComparison comparison,
        int maxEntries,
        long maxTotalBytes,
        long maxEntryBytes)
    {
        Directory.CreateDirectory(destinationPath);

        var destinationFullPath = Path.GetFullPath(destinationPath);
        if (!destinationFullPath.EndsWith(Path.DirectorySeparatorChar))
        {
            destinationFullPath += Path.DirectorySeparatorChar;
        }

        using var archive = ZipFile.OpenRead(zipPath);
        var entryCount = 0;
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            entryCount++;
            if (entryCount > maxEntries)
            {
                throw new InvalidOperationException($"Zip entry count exceeds limit ({maxEntries}).");
            }

            var entryLength = entry.Length;
            if (entryLength > maxEntryBytes)
            {
                throw new InvalidOperationException($"Zip entry exceeds max uncompressed size ({maxEntryBytes} bytes).");
            }

            totalBytes += entryLength;
            if (totalBytes > maxTotalBytes)
            {
                throw new InvalidOperationException($"Zip uncompressed size exceeds limit ({maxTotalBytes} bytes).");
            }

            if (string.IsNullOrEmpty(entry.FullName))
            {
                continue;
            }

            var entryPath = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrEmpty(entryPath))
            {
                continue;
            }

            var destinationFilePath = Path.GetFullPath(Path.Combine(destinationPath, entryPath));
            if (!destinationFilePath.StartsWith(destinationFullPath, comparison))
            {
                throw new InvalidOperationException($"Zip entry is outside destination: {entry.FullName}");
            }

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.Name.Length == 0)
            {
                Directory.CreateDirectory(destinationFilePath);
                continue;
            }

            var destinationDirectory = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            entry.ExtractToFile(destinationFilePath, overwrite: true);
        }
    }

    private static string GetRoslynPlatform(Architecture architecture)
    {
        // Determine platform for Roslyn Language Server download
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return architecture switch
            {
                Architecture.Arm64 => "win-arm64",
                _ => "win-x64"
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return architecture switch
            {
                Architecture.Arm64 => "osx-arm64",
                _ => "osx-x64"
            };
        }
        else // Linux
        {
            return architecture switch
            {
                Architecture.Arm64 => "linux-arm64",
                _ => "linux-x64"
            };
        }
    }

    private static string GetExtensionDownloadUrl(string version)
    {
        // Download from VS Code marketplace using the vsassets URL
        // This gets the universal (platform-neutral) package for the Razor extension
        return $"https://ms-dotnettools.gallery.vsassets.io/_apis/public/gallery/publisher/ms-dotnettools/extension/csharp/{version}/assetbyname/Microsoft.VisualStudio.Services.VSIXPackage";
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
            var info = JsonSerializer.Deserialize<DependencyVersionInfo>(json);
            if (info == null)
            {
                return null;
            }

            if (NormalizeVersionInfo(info))
            {
                WriteVersionInfo(info);
            }

            return info;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse dependency version file at {Path}", VersionFilePath);
            TryBackupCorruptVersionFile();
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read dependency version file at {Path}", VersionFilePath);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied reading dependency version file at {Path}", VersionFilePath);
            return null;
        }
    }

    private void SaveVersionInfo(string version, string? roslynVersion, string? extensionVersion)
    {
        var info = GetInstalledVersion() ?? new DependencyVersionInfo();
        info.Version = version;
        info.PendingVersion = null;
        info.InstalledAt = DateTime.UtcNow;
        info.Platform = RuntimeInformation.RuntimeIdentifier;
        if (!string.IsNullOrWhiteSpace(roslynVersion))
        {
            info.LastKnownRoslynVersion = roslynVersion;
        }
        if (!string.IsNullOrWhiteSpace(extensionVersion))
        {
            info.LastKnownExtensionVersion = extensionVersion;
        }
        WriteVersionInfo(info);
    }

    private void WriteVersionInfo(DependencyVersionInfo info)
    {
            try
            {
                Directory.CreateDirectory(_basePath);
                var json = JsonSerializer.Serialize(info, VersionInfoJsonOptions);
                var directory = Path.GetDirectoryName(VersionFilePath);
                if (string.IsNullOrEmpty(directory))
                {
                    return;
                }

            var tempPath = Path.Combine(directory, $"version.json.tmp-{Guid.NewGuid():N}");
            File.WriteAllText(tempPath, json);

            if (File.Exists(VersionFilePath))
            {
                File.Replace(tempPath, VersionFilePath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, VersionFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write dependency version file at {Path}", VersionFilePath);
            try
            {
                var directory = Path.GetDirectoryName(VersionFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    foreach (var tempFile in Directory.EnumerateFiles(directory, "version.json.tmp-*"))
                    {
                        TryDeleteFile(tempFile);
                    }
                }
            }
            catch
            {
                // Ignore cleanup failures
            }
        }
    }

    private string GetUpdateRoot(string version)
    {
        return Path.Combine(_basePath, UpdateRootDirectoryName, version);
    }

    private static string BuildCombinedVersion(string roslynVersion, string extensionVersion)
    {
        return $"{roslynVersion}+{extensionVersion}";
    }

    private static string GetRoslynServerDllPath(string roslynPath)
    {
        return Path.Combine(roslynPath, "Microsoft.CodeAnalysis.LanguageServer.dll");
    }

    private static string GetRazorSourceGeneratorPath(string razorExtensionPath)
    {
        return Path.Combine(razorExtensionPath, "Microsoft.CodeAnalysis.Razor.Compiler.dll");
    }

    private static string GetRazorExtensionDllPath(string razorExtensionPath)
    {
        return Path.Combine(razorExtensionPath, "Microsoft.VisualStudioCode.RazorExtension.dll");
    }

    private static string GetRazorDesignTimePath(string razorExtensionPath)
    {
        return Path.Combine(razorExtensionPath, "Targets", "Microsoft.NET.Sdk.Razor.DesignTime.targets");
    }

    private static void ReplaceDirectory(string sourceDir, string destinationDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");
        }

        var backupDir = destinationDir + ".bak-" + Guid.NewGuid().ToString("N");
        try
        {
            if (Directory.Exists(destinationDir))
            {
                Directory.Move(destinationDir, backupDir);
            }

            Directory.Move(sourceDir, destinationDir);
        }
        catch
        {
            if (Directory.Exists(backupDir) && !Directory.Exists(destinationDir))
            {
                try
                {
                    Directory.Move(backupDir, destinationDir);
                }
                catch
                {
                    // Ignore rollback failures
                }
            }
            throw;
        }
        finally
        {
            TryDeleteDirectory(backupDir);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private bool NormalizeVersionInfo(DependencyVersionInfo info)
    {
        var updated = false;
        var now = DateTime.UtcNow;

        if (info.InstalledAt == default || info.InstalledAt > now.AddDays(1))
        {
            info.InstalledAt = now;
            updated = true;
        }

        if (info.LastUpdateAttemptUtc.HasValue && info.LastUpdateAttemptUtc.Value > now.AddDays(1))
        {
            info.LastUpdateAttemptUtc = null;
            updated = true;
        }

        if (info.LastUpdateSuccessUtc.HasValue && info.LastUpdateSuccessUtc.Value > now.AddDays(1))
        {
            info.LastUpdateSuccessUtc = null;
            updated = true;
        }

        if (info.LastUpdateCheckUtc.HasValue && info.LastUpdateCheckUtc.Value > now.AddDays(1))
        {
            info.LastUpdateCheckUtc = null;
            updated = true;
        }

        var baseline = info.LastUpdateSuccessUtc ?? info.LastUpdateCheckUtc ?? info.InstalledAt;
        if (baseline < now - VersionInfoStaleThreshold)
        {
            if (!string.IsNullOrWhiteSpace(info.RoslynReleasesETag) ||
                !string.IsNullOrWhiteSpace(info.ExtensionVersionsETag) ||
                !string.IsNullOrWhiteSpace(info.LastKnownRoslynVersion) ||
                !string.IsNullOrWhiteSpace(info.LastKnownExtensionVersion))
            {
                info.RoslynReleasesETag = null;
                info.ExtensionVersionsETag = null;
                info.LastKnownRoslynVersion = null;
                info.LastKnownExtensionVersion = null;
                updated = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(info.PendingVersion))
        {
            var updateRoot = GetUpdateRoot(info.PendingVersion);
            var pendingRoslynPath = Path.Combine(updateRoot, "roslyn");
            var pendingRazorPath = Path.Combine(updateRoot, "razorExtension");

            if (!AreDependenciesCompleteAt(pendingRoslynPath, pendingRazorPath, logDesignTimeMissing: false))
            {
                info.PendingVersion = null;
                updated = true;
            }
        }

        return updated;
    }

    private void TryBackupCorruptVersionFile()
    {
        try
        {
            if (!File.Exists(VersionFilePath))
            {
                return;
            }

            var directory = Path.GetDirectoryName(VersionFilePath);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            var backupPath = Path.Combine(
                directory,
                $"version.json.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}");
            File.Move(VersionFilePath, backupPath, overwrite: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private DependencyLock? TryAcquireDependencyLock()
    {
        try
        {
            Directory.CreateDirectory(_basePath);
            var lockPath = Path.Combine(_basePath, DependencyLockFileName);
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new DependencyLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<DependencyLock?> TryAcquireDependencyLockAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(_basePath);
                var lockPath = Path.Combine(_basePath, DependencyLockFileName);
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new DependencyLock(stream);
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return null;
                }

                await Task.Delay(DependencyLockRetryDelay, cancellationToken);
            }
            catch (UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return null;
                }

                await Task.Delay(DependencyLockRetryDelay, cancellationToken);
            }
        }
    }

    private async Task<VersionFetchResult> GetLatestRoslynVersionAsync(string? etag, string? cachedVersion, CancellationToken cancellationToken)
    {
        const string url = "https://api.github.com/repos/Crashdummyy/roslynLanguageServer/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            if (!string.IsNullOrWhiteSpace(cachedVersion))
            {
                return VersionFetchResult.CreateFromCache(cachedVersion, etag);
            }
            _logger.LogWarning("Received 304 Not Modified but no cached Roslyn version available.");
            return VersionFetchResult.Failed;
        }

        if (IsRateLimited(response))
        {
            _logger.LogWarning("GitHub API rate limit exceeded while checking Roslyn version.");
            return VersionFetchResult.Failed;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (doc.RootElement.TryGetProperty("tag_name", out var tag))
        {
            var value = tag.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return VersionFetchResult.CreateSuccess(value.TrimStart('v', 'V'), GetResponseEtag(response));
            }
        }

        return VersionFetchResult.Failed;
    }

    private async Task<VersionFetchResult> GetLatestExtensionVersionAsync(string? etag, string? cachedVersion, CancellationToken cancellationToken)
    {
        const string url = "https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery?api-version=3.0-preview.1";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(ExtensionQueryPayload, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            if (!string.IsNullOrWhiteSpace(cachedVersion))
            {
                return VersionFetchResult.CreateFromCache(cachedVersion, etag);
            }
            // 304 without cached version - can't use this response
            _logger.LogWarning("Extension API returned 304 NotModified but no cached version available");
            return VersionFetchResult.Failed;
        }

        if (IsRateLimited(response))
        {
            _logger.LogWarning("Marketplace API rate limit exceeded while checking extension version.");
            return VersionFetchResult.Failed;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Marketplace API endpoint returned 404 while checking extension version.");
            return VersionFetchResult.Failed;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Marketplace API returned {StatusCode} while checking extension version.", response.StatusCode);
            return VersionFetchResult.Failed;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (doc.RootElement.TryGetProperty("results", out var results) &&
            results.ValueKind == JsonValueKind.Array &&
            results.GetArrayLength() > 0 &&
            results[0].TryGetProperty("extensions", out var extensions) &&
            extensions.ValueKind == JsonValueKind.Array)
        {
            Version? best = null;
            string? bestRaw = null;

            foreach (var extension in extensions.EnumerateArray())
            {
                if (!extension.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var version in versions.EnumerateArray())
                {
                    if (!version.TryGetProperty("version", out var versionProp))
                    {
                        continue;
                    }

                    var value = versionProp.GetString();
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (Version.TryParse(value, out var parsed))
                    {
                        if (best == null || parsed > best)
                        {
                            best = parsed;
                            bestRaw = value;
                        }
                    }
                    else if (best == null && bestRaw == null)
                    {
                        bestRaw = value;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(bestRaw))
            {
                return VersionFetchResult.CreateSuccess(bestRaw, GetResponseEtag(response));
            }
        }

        return VersionFetchResult.Failed;
    }

    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden &&
            response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues))
        {
            foreach (var value in remainingValues)
            {
                if (string.Equals(value, "0", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? GetResponseEtag(HttpResponseMessage response)
    {
        if (response.Headers.ETag != null)
        {
            return response.Headers.ETag.Tag;
        }

        if (response.Headers.TryGetValues("ETag", out var values))
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _httpClient.Dispose();
    }
}

public class DependencyVersionInfo
{
    public string? Version { get; set; }
    public DateTime InstalledAt { get; set; }
    public string? Platform { get; set; }
    public string? PendingVersion { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
    public DateTime? LastUpdateAttemptUtc { get; set; }
    public DateTime? LastUpdateSuccessUtc { get; set; }
    public string? RoslynReleasesETag { get; set; }
    public string? ExtensionVersionsETag { get; set; }
    public string? LastKnownRoslynVersion { get; set; }
    public string? LastKnownExtensionVersion { get; set; }
}

public enum DependencyUpdateStatus
{
    Skipped,
    NoUpdate,
    UpdateAlreadyPending,
    UpdateDownloaded,
    Failed
}

public readonly record struct DependencyUpdateResult(
    DependencyUpdateStatus Status,
    string? CurrentVersion,
    string? TargetVersion);

readonly record struct VersionFetchResult(string? Version, string? ETag, bool Success, bool FromCache)
{
    public static VersionFetchResult Failed => new(null, null, Success: false, FromCache: false);
    public static VersionFetchResult CreateSuccess(string version, string? etag) => new(version, etag, Success: true, FromCache: false);
    public static VersionFetchResult CreateFromCache(string version, string? etag) => new(version, etag, Success: true, FromCache: true);
}

sealed class DependencyLock : IDisposable, IAsyncDisposable
{
    readonly FileStream _stream;

    public DependencyLock(FileStream stream)
    {
        _stream = stream;
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
