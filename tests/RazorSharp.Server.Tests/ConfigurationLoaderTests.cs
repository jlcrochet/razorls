using System;
using System.IO;
using Microsoft.Extensions.Logging;
using RazorSharp.Server.Configuration;

namespace RazorSharp.Server.Tests;

[Collection("Environment")]
public class ConfigurationLoaderTests
{
    [Fact]
    public void Reload_MergesGlobalAndLocalConfigs_WithOverrides()
    {
        var globalHome = CreateTempDir();
        var workspaceRoot = CreateTempDir();

        var oldHome = Environment.GetEnvironmentVariable("OMNISHARPHOME");
        try
        {
            Environment.SetEnvironmentVariable("OMNISHARPHOME", globalHome);

            WriteConfig(Path.Combine(globalHome, "omnisharp.json"), """
                {
                  // global config
                  "FormattingOptions": {
                    "enableEditorConfigSupport": false,
                    "tabSize": 2,
                  },
                  "RoslynExtensionsOptions": {
                    "enableAnalyzersSupport": true,
                    "inlayHintsOptions": {
                      "enableForParameters": true,
                    },
                  },
                  "RenameOptions": {
                    "renameOverloads": true
                  },
                  "csharp": {
                    "suppressDotnetRestoreNotification": true
                  }
                }
                """);

            WriteConfig(Path.Combine(workspaceRoot, "omnisharp.json"), """
                {
                  "FormattingOptions": {
                    "enableEditorConfigSupport": true
                  },
                  "RoslynExtensionsOptions": {
                    "enableAnalyzersSupport": false
                  },
                  "csharp": {
                    "maxProjectFileCountForDiagnosticAnalysis": 123
                  }
                }
                """);

            var loader = new ConfigurationLoader(CreateLogger());
            loader.SetWorkspaceRoot(workspaceRoot);

            var config = loader.Configuration;
            Assert.NotNull(config.FormattingOptions);
            Assert.NotNull(config.RoslynExtensionsOptions);
            Assert.NotNull(config.RoslynExtensionsOptions!.InlayHintsOptions);
            Assert.NotNull(config.RenameOptions);
            Assert.NotNull(config.CSharp);

            Assert.True(config.FormattingOptions!.EnableEditorConfigSupport);
            Assert.Equal(2, config.FormattingOptions.TabSize);
            Assert.False(config.RoslynExtensionsOptions!.EnableAnalyzersSupport);
            Assert.True(config.RoslynExtensionsOptions.InlayHintsOptions!.EnableForParameters);
            Assert.True(config.RenameOptions!.RenameOverloads);
            Assert.Equal(123, config.CSharp!.MaxProjectFileCountForDiagnosticAnalysis);
            Assert.True(config.CSharp.SuppressDotnetRestoreNotification);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNISHARPHOME", oldHome);
            DeleteTempDir(globalHome);
            DeleteTempDir(workspaceRoot);
        }
    }

    [Fact]
    public void GetConfigurationValue_ReturnsExpectedValues()
    {
        var globalHome = CreateTempDir();
        var workspaceRoot = CreateTempDir();

        var oldHome = Environment.GetEnvironmentVariable("OMNISHARPHOME");
        try
        {
            Environment.SetEnvironmentVariable("OMNISHARPHOME", globalHome);

            WriteConfig(Path.Combine(globalHome, "omnisharp.json"), """
                {
                  "FormattingOptions": {
                    "enableEditorConfigSupport": true
                  },
                  "RoslynExtensionsOptions": {
                    "inlayHintsOptions": {
                      "enableForParameters": true
                    }
                  }
                }
                """);

            var loader = new ConfigurationLoader(CreateLogger());
            loader.SetWorkspaceRoot(workspaceRoot);

            Assert.Same(loader.Configuration.FormattingOptions, loader.GetConfigurationValue("FormattingOptions"));
            Assert.Same(loader.Configuration.FormattingOptions, loader.GetConfigurationValue("omnisharp.formattingOptions"));
            Assert.Equal(true, loader.GetConfigurationValue("omnisharp.enableEditorConfigSupport"));
            Assert.Equal(true, loader.GetConfigurationValue("csharp.inlayHints.parameters.enabled"));
            Assert.Null(loader.GetConfigurationValue("does.not.exist"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNISHARPHOME", oldHome);
            DeleteTempDir(globalHome);
            DeleteTempDir(workspaceRoot);
        }
    }

    [Fact]
    public void Reload_IgnoresInvalidLocalConfig()
    {
        var globalHome = CreateTempDir();
        var workspaceRoot = CreateTempDir();

        var oldHome = Environment.GetEnvironmentVariable("OMNISHARPHOME");
        try
        {
            Environment.SetEnvironmentVariable("OMNISHARPHOME", globalHome);

            WriteConfig(Path.Combine(globalHome, "omnisharp.json"), """
                {
                  "FormattingOptions": {
                    "enableEditorConfigSupport": true
                  }
                }
                """);

            // Invalid JSON
            WriteConfig(Path.Combine(workspaceRoot, "omnisharp.json"), "{ invalid");

            var loader = new ConfigurationLoader(CreateLogger());
            loader.SetWorkspaceRoot(workspaceRoot);

            Assert.True(loader.Configuration.FormattingOptions?.EnableEditorConfigSupport);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNISHARPHOME", oldHome);
            DeleteTempDir(globalHome);
            DeleteTempDir(workspaceRoot);
        }
    }

    private static ILogger<ConfigurationLoader> CreateLogger()
    {
        var loggerFactory = LoggerFactory.Create(builder => { });
        return loggerFactory.CreateLogger<ConfigurationLoader>();
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "razorsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDir(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void WriteConfig(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
