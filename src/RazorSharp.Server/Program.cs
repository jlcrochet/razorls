using System.Reflection;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Server;

static string GetVersion() =>
    Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
    ?? "[unknown version]";


// Parse command line arguments
var logLevel = LogLevel.Information;
var logLevelSpecifiedByCli = false;
string? logFile = null;
var logFileSpecifiedByCli = false;
string? solutionPath = null;
int? hostPid = null;
bool skipDependencyCheck = false;
var skipDependencyCheckSpecifiedByCli = false;
bool downloadDependenciesOnly = false;
bool autoUpdateEnabled = true;
bool forceUpdateCheck = false;
var warnings = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    var arg = args[i];
    var nextArg = i + 1 < args.Length ? args[i + 1] : null;

    switch (arg)
    {
        case "-l" or "--loglevel":
            if (nextArg == null || nextArg.StartsWith('-'))
            {
                warnings.Add($"Option '{arg}' requires a value (Trace, Debug, Information, Warning, Error).");
            }
            else if (!Enum.TryParse<LogLevel>(args[++i], true, out var level))
            {
                warnings.Add($"Invalid log level '{args[i]}'. Valid values: Trace, Debug, Information, Warning, Error.");
            }
            else
            {
                logLevel = level;
                logLevelSpecifiedByCli = true;
            }
            break;
        case "-v" or "--verbose":
            logLevel = LogLevel.Trace;
            logLevelSpecifiedByCli = true;
            break;
        case "--logFile":
            if (nextArg == null || nextArg.StartsWith('-'))
            {
                warnings.Add($"Option '{arg}' requires a file path.");
            }
            else
            {
                logFile = args[++i];
                logFileSpecifiedByCli = true;
            }
            break;
        case "-s" or "--source":
            if (nextArg == null || nextArg.StartsWith('-'))
            {
                warnings.Add($"Option '{arg}' requires a path.");
            }
            else
            {
                solutionPath = args[++i];
            }
            break;
        case "-hpid" or "--hostPID":
            if (nextArg == null || nextArg.StartsWith('-'))
            {
                warnings.Add($"Option '{arg}' requires a process ID.");
            }
            else if (!int.TryParse(args[++i], out var pid))
            {
                warnings.Add($"Invalid process ID '{args[i]}'. Must be an integer.");
            }
            else
            {
                hostPid = pid;
            }
            break;
        case "--skip-dependency-check":
            skipDependencyCheck = true;
            skipDependencyCheckSpecifiedByCli = true;
            break;
        case "--check-updates":
            forceUpdateCheck = true;
            break;
        case "--no-auto-update":
            autoUpdateEnabled = false;
            break;
        case "--download-dependencies":
            downloadDependenciesOnly = true;
            break;
        case "-?" or "-h" or "--help":
            PrintHelp();
            return 0;
        case "--version":
            Console.WriteLine($"RazorSharp {GetVersion()}");
            return 0;
        default:
            warnings.Add($"Unknown option '{arg}'. Use --help to see available options.");
            break;
    }
}

// Print any warnings (never write to stdout; stdout is reserved for LSP).
var warningOutput = Console.Error;
foreach (var warning in warnings)
{
    warningOutput.WriteLine($"Warning: {warning}");
}

// Handle --download-dependencies mode
if (downloadDependenciesOnly)
{
    return await DownloadDependenciesAsync();
}


// Configure logging
var logLevelSwitch = new LoggingLevelSwitch(logLevel);
var logFileSwitch = new LogFileSwitch(logFile);
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Trace);
    builder.AddProvider(new SwitchingLoggerProvider(logLevelSwitch, logFileSwitch));
});

using var depManager = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), GetVersion());
depManager.ApplyPendingUpdateIfAvailable();

// Run the server
var server = new RazorLanguageServer(loggerFactory, depManager);
server.SetLogLevel(logLevel);
server.SetLoggingOptions(logLevelSwitch, logFileSwitch, logLevelSpecifiedByCli, logFileSpecifiedByCli);
server.SetAutoUpdateEnabledFromCli(autoUpdateEnabled);
server.SetForceUpdateCheck(forceUpdateCheck);
server.SetSkipDependencyCheck(skipDependencyCheck, skipDependencyCheckSpecifiedByCli);
if (solutionPath != null)
{
    server.SetSolutionPath(solutionPath);
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Monitor host process - shutdown when parent dies
if (hostPid.HasValue)
{
    _ = Task.Run(async () =>
    {
        try
        {
            using var hostProcess = System.Diagnostics.Process.GetProcessById(hostPid.Value);
            await hostProcess.WaitForExitAsync(cts.Token);
            cts.Cancel();
        }
        catch
        {
            // Host process already dead or inaccessible - shutdown
            cts.Cancel();
        }
    });
}

try
{
    await server.RunAsync(cts.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal error: {ex}");
    return 1;
}
finally
{
    await server.DisposeAsync();
}

static void PrintHelp()
{
    Console.WriteLine("""
        RazorSharp - Razor Language Server

        Usage: razorsharp [options]

        Options:
          -s|--source <path>       Solution or directory for RazorSharp to point at
          -l|--loglevel <level>    Set log level (Trace, Debug, Information, Warning, Error)
          -v|--verbose             Set log level to Trace
          --logFile <path>         Write logs to file instead of stderr
          -hpid|--hostPID <pid>    Host process ID (shutdown when host exits)
          --download-dependencies  Download dependencies and exit (does not start server)
          --check-updates          Force a background dependency update check on startup
          --no-auto-update         Disable background dependency auto-updates
          --skip-dependency-check  Skip dependency check on startup
          -?|-h|--help             Show this help message
          --version                Show version information

        The server communicates via Language Server Protocol over stdin/stdout.

        Before first use, you must download dependencies:
          razorsharp --download-dependencies
        """);
}

static async Task<int> DownloadDependenciesAsync()
{
    Console.WriteLine("RazorSharp - Downloading dependencies...");
    Console.WriteLine();

    // Create a minimal logger that doesn't output anything (we'll use the progress callback instead)
    using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
    var logger = loggerFactory.CreateLogger<DependencyManager>();

    using var depManager = new DependencyManager(logger, GetVersion());

    // Use progress bar if output is a TTY, otherwise use simple line output
    var progressBar = !Console.IsOutputRedirected ? new ProgressBar() : null;

    try
    {
        var success = await depManager.EnsureDependenciesAsync(
            CancellationToken.None,
            message =>
            {
                if (progressBar != null)
                {
                    progressBar.Update(message);
                }
                else
                {
                    Console.WriteLine(message);
                }
            });

        progressBar?.Complete();
        Console.WriteLine();
        if (success)
        {
            Console.WriteLine($"Dependencies installed to: {depManager.BasePath}");
            return 0;
        }
        else
        {
            Console.Error.WriteLine("Failed to download dependencies.");
            return 1;
        }
    }
    catch (Exception ex)
    {
        progressBar?.Complete();
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

/// <summary>
/// Simple progress bar for TTY output.
/// </summary>
internal class ProgressBar
{
    int _lastPercent = -1;
    int _lastOutputLength;
    const int BarWidth = 30;

    readonly char[] _bar = new char[BarWidth];
    readonly char[] _padding;

    public ProgressBar()
    {
        int width;
        try { width = Console.WindowWidth; }
        catch (IOException) { width = 120; }  // Console not available (e.g., redirected output)
        catch (PlatformNotSupportedException) { width = 120; }
        _padding = new char[width];
        Array.Fill(_padding, ' ');
    }

    public void Update(string message)
    {
        // Try to extract percentage from messages like "Downloading Roslyn... 50%"
        if (message.StartsWith("Downloading ") &&
            message[^1] == '%' &&
            char.IsAsciiDigit(message[^2]))
        {
            // Scan backwards to find start of number
            var numStart = message.Length - 2;
            while (numStart > 0 && char.IsAsciiDigit(message[numStart - 1]))
            {
                numStart--;
            }

            if (!int.TryParse(message.AsSpan(numStart, message.Length - 1 - numStart), out var percent))
            {
                ClearLine();
                Console.WriteLine(message);
                _lastPercent = -1;
                return;
            }
            percent = Math.Clamp(percent, 0, 100);
            var label = message.AsSpan(0, numStart).TrimEnd();
            RenderProgressBar(label, percent);
            _lastPercent = percent;
        }
        else
        {
            ClearLine();
            Console.WriteLine(message);
            _lastPercent = -1;
        }
    }

    public void Complete()
    {
        if (_lastPercent >= 0)
        {
            // Clear the progress bar line
            ClearLine();
        }
    }

    private void RenderProgressBar(ReadOnlySpan<char> label, int percent)
    {
        var filled = (int)(percent / 100.0 * BarWidth);
        if (filled > BarWidth) filled = BarWidth;
        if (filled < 0) filled = 0;

        _bar.AsSpan(0, filled).Fill('█');
        _bar.AsSpan(filled).Fill('░');

        // Truncate label if too long
        int consoleWidth;
        try { consoleWidth = Console.WindowWidth; }
        catch (IOException) { consoleWidth = _padding.Length; }
        catch (PlatformNotSupportedException) { consoleWidth = _padding.Length; }
        var maxLabelWidth = Math.Max(10, consoleWidth - BarWidth - 10);
        var labelSpan = label.Length <= maxLabelWidth
            ? label
            : label[0..(maxLabelWidth - 3)];
        var needsEllipsis = label.Length > maxLabelWidth;

        Console.Write('\r');
        Console.Write(labelSpan);
        if (needsEllipsis) Console.Write("...");
        Console.Write(' ');
        Console.Write(_bar);
        Console.Write(' ');
        Console.Write(percent.ToString().PadLeft(3));
        Console.Write('%');

        // Calculate current output length and clear any leftover chars
        var outputLength = 1 + labelSpan.Length + (needsEllipsis ? 3 : 0) + 1 + BarWidth + 1 + 4;
        var toClear = Math.Max(0, _lastOutputLength - outputLength);
        if (toClear > 0 && toClear <= _padding.Length)
        {
            Console.Write(_padding.AsSpan(0, toClear));
        }
        _lastOutputLength = outputLength;
    }

    private static void ClearLine()
    {
        Console.Write("\r\x1b[K");
    }
}
