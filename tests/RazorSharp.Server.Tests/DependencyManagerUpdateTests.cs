using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;

namespace RazorSharp.Server.Tests;

[Collection("Environment")]
public class DependencyManagerUpdateTests
{
    [Fact]
    public void ApplyPendingUpdate_ReturnsFalse_WhenNoPendingVersion()
    {
        var tempRoot = CreateTempDir();
        try
        {
            using var manager = new DependencyManager(CreateLogger(), "1.0.0-test", tempRoot);
            var applied = manager.ApplyPendingUpdateIfAvailable();
            Assert.False(applied);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void ApplyPendingUpdate_ReturnsFalse_WhenUpdateIncomplete()
    {
        var tempRoot = CreateTempDir();
        try
        {
            using var manager = new DependencyManager(CreateLogger(), "1.0.0-test", tempRoot);
            var pendingVersion = "1.2.3+4.5.6";
            WriteVersionInfo(manager.VersionFilePath, pendingVersion);

            var updateRoot = Path.Combine(tempRoot, "updates", pendingVersion, "roslyn");
            Directory.CreateDirectory(updateRoot);
            File.WriteAllText(Path.Combine(updateRoot, "Microsoft.CodeAnalysis.LanguageServer.dll"), string.Empty);

            var applied = manager.ApplyPendingUpdateIfAvailable();
            Assert.False(applied);

            Assert.False(Directory.Exists(manager.RoslynPath));
            Assert.False(Directory.Exists(manager.RazorExtensionPath));

            var info = ReadVersionInfo(manager.VersionFilePath);
            Assert.Null(info.PendingVersion);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void ApplyPendingUpdate_MovesDependencies_AndUpdatesVersion()
    {
        var tempRoot = CreateTempDir();
        try
        {
            using var manager = new DependencyManager(CreateLogger(), "1.0.0-test", tempRoot);
            var pendingVersion = "9.9.9+8.8.8";
            WriteVersionInfo(manager.VersionFilePath, pendingVersion);

            var updateRoot = Path.Combine(tempRoot, "updates", pendingVersion);
            var roslynUpdate = Path.Combine(updateRoot, "roslyn");
            var razorUpdate = Path.Combine(updateRoot, "razorExtension");
            Directory.CreateDirectory(roslynUpdate);
            Directory.CreateDirectory(razorUpdate);

            File.WriteAllText(Path.Combine(roslynUpdate, "Microsoft.CodeAnalysis.LanguageServer.dll"), "new");
            File.WriteAllText(Path.Combine(razorUpdate, "Microsoft.CodeAnalysis.Razor.Compiler.dll"), "new");
            File.WriteAllText(Path.Combine(razorUpdate, "Microsoft.VisualStudioCode.RazorExtension.dll"), "new");

            Directory.CreateDirectory(manager.RoslynPath);
            Directory.CreateDirectory(manager.RazorExtensionPath);
            File.WriteAllText(Path.Combine(manager.RoslynPath, "old.txt"), "old");
            File.WriteAllText(Path.Combine(manager.RazorExtensionPath, "old.txt"), "old");

            var applied = manager.ApplyPendingUpdateIfAvailable();
            Assert.True(applied);

            Assert.True(File.Exists(Path.Combine(manager.RoslynPath, "Microsoft.CodeAnalysis.LanguageServer.dll")));
            Assert.True(File.Exists(Path.Combine(manager.RazorExtensionPath, "Microsoft.CodeAnalysis.Razor.Compiler.dll")));
            Assert.True(File.Exists(Path.Combine(manager.RazorExtensionPath, "Microsoft.VisualStudioCode.RazorExtension.dll")));
            Assert.False(Directory.Exists(updateRoot));

            var updatedInfo = ReadVersionInfo(manager.VersionFilePath);
            Assert.Equal(pendingVersion, updatedInfo.Version);
            Assert.Null(updatedInfo.PendingVersion);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_Skips_WhenCheckedRecently()
    {
        var tempRoot = CreateTempDir();
        try
        {
            using var manager = new DependencyManager(CreateLogger(), "1.0.0-test", tempRoot);
            var lastCheck = DateTime.UtcNow.AddMinutes(-10);
            WriteVersionInfo(manager.VersionFilePath, pendingVersion: null, lastUpdateCheckUtc: lastCheck);

            var result = await manager.CheckForUpdatesAsync(TimeSpan.FromHours(24), CancellationToken.None);

            Assert.Equal(DependencyUpdateStatus.Skipped, result.Status);

            var info = ReadVersionInfo(manager.VersionFilePath);
            Assert.Equal(lastCheck, info.LastUpdateCheckUtc);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_DownloadsAndSetsPendingVersion()
    {
        var tempRoot = CreateTempDir();
        try
        {
            using var manager = new DependencyManager(CreateLogger(), "1.0.0-test", tempRoot);
            var lastCheck = DateTime.UtcNow.AddDays(-2);
            WriteVersionInfo(manager.VersionFilePath, pendingVersion: null, lastUpdateCheckUtc: lastCheck, version: "0.0.1+0.0.1");

            var downloadCalled = false;
            manager.GetLatestRoslynVersionOverride = _ => Task.FromResult<string?>("9.9.9");
            manager.GetLatestExtensionVersionOverride = _ => Task.FromResult<string?>("8.8.8");
            manager.DownloadUpdateOverride = (_, __, ___, ____) =>
            {
                downloadCalled = true;
                return Task.CompletedTask;
            };

            var result = await manager.CheckForUpdatesAsync(TimeSpan.Zero, CancellationToken.None);

            Assert.Equal(DependencyUpdateStatus.UpdateDownloaded, result.Status);
            Assert.Equal("9.9.9+8.8.8", result.TargetVersion);
            Assert.True(downloadCalled);

            var info = ReadVersionInfo(manager.VersionFilePath);
            Assert.Equal("0.0.1+0.0.1", info.Version);
            Assert.Equal("9.9.9+8.8.8", info.PendingVersion);
            Assert.NotNull(info.LastUpdateAttemptUtc);
            Assert.True(info.LastUpdateAttemptUtc > lastCheck);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ThrottlesFailedChecks_AndRetriesOnce()
    {
        var tempRoot = CreateTempDir();
        try
        {
            using var manager = new DependencyManager(CreateLogger(), "1.0.0-test", tempRoot);
            var attempts = 0;
            manager.GetLatestRoslynVersionOverride = _ =>
            {
                attempts++;
                throw new HttpRequestException("fail");
            };

            var before = DateTime.UtcNow;
            var result = await manager.CheckForUpdatesAsync(TimeSpan.Zero, CancellationToken.None);

            Assert.Equal(DependencyUpdateStatus.Failed, result.Status);
            Assert.Equal(2, attempts);

            var info = ReadVersionInfo(manager.VersionFilePath);
            Assert.NotNull(info.LastUpdateCheckUtc);
            Assert.True(info.LastUpdateCheckUtc >= before);

            var skipped = await manager.CheckForUpdatesAsync(TimeSpan.FromHours(1), CancellationToken.None);
            Assert.Equal(DependencyUpdateStatus.Skipped, skipped.Status);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_Skips_WhenPinnedVersionsConfigured()
    {
        var tempRoot = CreateTempDir();
        try
        {
            using var manager = new DependencyManager(CreateLogger(), "1.0.0-test", tempRoot);
            manager.ConfigurePinnedVersions("1.2.3", "4.5.6");

            var roslynCalled = false;
            var extensionCalled = false;
            manager.GetLatestRoslynVersionOverride = _ =>
            {
                roslynCalled = true;
                return Task.FromResult<string?>("9.9.9");
            };
            manager.GetLatestExtensionVersionOverride = _ =>
            {
                extensionCalled = true;
                return Task.FromResult<string?>("8.8.8");
            };

            var result = await manager.CheckForUpdatesAsync(TimeSpan.Zero, CancellationToken.None);

            Assert.Equal(DependencyUpdateStatus.Skipped, result.Status);
            Assert.False(roslynCalled);
            Assert.False(extensionCalled);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public async Task EnsureDependenciesAsync_UsesPinnedVersions_WhenInstalled()
    {
        var tempRoot = CreateTempDir();
        try
        {
            using var manager = new DependencyManager(CreateLogger(), "1.0.0-test", tempRoot);
            manager.ConfigurePinnedVersions("1.2.3", "4.5.6");

            Directory.CreateDirectory(manager.RoslynPath);
            Directory.CreateDirectory(manager.RazorExtensionPath);
            File.WriteAllText(Path.Combine(manager.RoslynPath, "Microsoft.CodeAnalysis.LanguageServer.dll"), "ok");
            File.WriteAllText(Path.Combine(manager.RazorExtensionPath, "Microsoft.CodeAnalysis.Razor.Compiler.dll"), "ok");
            File.WriteAllText(Path.Combine(manager.RazorExtensionPath, "Microsoft.VisualStudioCode.RazorExtension.dll"), "ok");

            WriteVersionInfo(manager.VersionFilePath, pendingVersion: null, version: "1.2.3+4.5.6");

            var result = await manager.EnsureDependenciesAsync(CancellationToken.None);

            Assert.True(result);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public async Task EnsureDependenciesAsync_Succeeds_WhenVersionMissingButDependenciesComplete()
    {
        var tempRoot = CreateTempDir();
        try
        {
            using var manager = new DependencyManager(CreateLogger(), "1.0.0-test", tempRoot);

            Directory.CreateDirectory(manager.RoslynPath);
            Directory.CreateDirectory(manager.RazorExtensionPath);
            File.WriteAllText(Path.Combine(manager.RoslynPath, "Microsoft.CodeAnalysis.LanguageServer.dll"), "ok");
            File.WriteAllText(Path.Combine(manager.RazorExtensionPath, "Microsoft.CodeAnalysis.Razor.Compiler.dll"), "ok");
            File.WriteAllText(Path.Combine(manager.RazorExtensionPath, "Microsoft.VisualStudioCode.RazorExtension.dll"), "ok");

            var result = await manager.EnsureDependenciesAsync(CancellationToken.None);

            Assert.True(result);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public async Task EnsureDependenciesAsync_Fails_WhenIncompleteAndVersionsCannotBeResolved()
    {
        var tempRoot = CreateTempDir();
        try
        {
            using var manager = new DependencyManager(CreateLogger(), "1.0.0-test", tempRoot);
            manager.GetLatestRoslynVersionOverride = _ => Task.FromResult<string?>(null);
            manager.GetLatestExtensionVersionOverride = _ => Task.FromResult<string?>(null);

            var result = await manager.EnsureDependenciesAsync(CancellationToken.None);

            Assert.False(result);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_RetriesAndSucceeds_WhenTransientFailureOccurs()
    {
        var tempRoot = CreateTempDir();
        try
        {
            using var manager = new DependencyManager(CreateLogger(), "1.0.0-test", tempRoot);
            WriteVersionInfo(manager.VersionFilePath, pendingVersion: null, version: "0.0.1+0.0.1");

            var attempts = 0;
            manager.GetLatestRoslynVersionOverride = _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new HttpRequestException("transient");
                }
                return Task.FromResult<string?>("9.9.9");
            };
            manager.GetLatestExtensionVersionOverride = _ => Task.FromResult<string?>("8.8.8");

            var downloadCalled = false;
            manager.DownloadUpdateOverride = (_, __, ___, ____) =>
            {
                downloadCalled = true;
                return Task.CompletedTask;
            };

            var result = await manager.CheckForUpdatesAsync(TimeSpan.Zero, CancellationToken.None);

            Assert.Equal(DependencyUpdateStatus.UpdateDownloaded, result.Status);
            Assert.Equal(2, attempts);
            Assert.True(downloadCalled);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void GetInstalledVersion_BacksUpCorruptVersionFile()
    {
        var tempRoot = CreateTempDir();
        try
        {
            using var manager = new DependencyManager(CreateLogger(), "1.0.0-test", tempRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(manager.VersionFilePath)!);
            File.WriteAllText(manager.VersionFilePath, "not-json");

            var applied = manager.ApplyPendingUpdateIfAvailable();
            Assert.False(applied);

            Assert.False(File.Exists(manager.VersionFilePath));
            var backups = Directory.GetFiles(Path.GetDirectoryName(manager.VersionFilePath)!, "version.json.corrupt-*");
            Assert.NotEmpty(backups);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    static void WriteVersionInfo(string path, string? pendingVersion, DateTime? lastUpdateCheckUtc = null, string? version = null)
    {
        var info = new DependencyVersionInfo
        {
            Version = version ?? "0.0.1+0.0.1",
            PendingVersion = pendingVersion,
            LastUpdateCheckUtc = lastUpdateCheckUtc,
            InstalledAt = DateTime.UtcNow,
            Platform = "test"
        };

        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    static DependencyVersionInfo ReadVersionInfo(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<DependencyVersionInfo>(json) ?? new DependencyVersionInfo();
    }

    static ILogger<DependencyManager> CreateLogger()
    {
        var loggerFactory = LoggerFactory.Create(builder => { });
        return loggerFactory.CreateLogger<DependencyManager>();
    }

    static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "razorsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    static void DeleteTempDir(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
