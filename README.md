# RazorSharp

A standalone, IDE-agnostic Language Server Protocol (LSP) server for C# and Razor files. Provides language features like completions, hover, go-to-definition, and more for C#, Razor, and Blazor development in any LSP-compatible editor.

RazorSharp is intended to be a **drop-in replacement for OmniSharp** for C# while also supporting `.razor` and `.cshtml` files. It honors OmniSharp configuration conventions for Roslyn settings and adds RazorSharp-specific configuration only where needed.

I made this because I didn't want to wait for Microsoft to release an official solution and because [seblyng/roslyn.nvim](https://github.com/seblyng/roslyn.nvim) only works with Neovim. This project is heavily inspired by seblyng/roslyn.nvim.

By the way, if you're looking for a Tree-sitter parser for Razor files, check out [mine](https://github.com/jlcrochet/tree-sitter-razor).

## Table of Contents

- [Quick Start](#quick-start)
- [Installation](#installation)
- [Usage](#usage)
- [Compatibility](#compatibility)
- [Editor Integration](#editor-integration)
- [Configuration](#configuration)
- [HTML Language Server](#html-language-server)
- [Performance Tips](#performance-tips)
- [Troubleshooting](#troubleshooting)
- [Versioning](#versioning)

## Quick Start

```bash
# Download dependencies (one-time)
dotnet /path/to/razorsharp --download-dependencies

# Run the server
dotnet /path/to/razorsharp.dll
```

Then configure your editor to:
1. Run `dotnet /path/to/razorsharp`
2. Use `stdio` transport
3. Associate with `.cs`, `.razor`, and `.cshtml`

## Installation

### Requirements

- **.NET 10+** - Required for both RazorSharp and the Roslyn Language Server dependencies it downloads
- **64-bit runtime (x64 or arm64)** - 32-bit runtimes are not supported
- **Node.js** - Optional, for HTML language server support (see [HTML Language Server](#html-language-server))

### Pre-built binaries

Download `razorsharp.zip` from the [Releases](https://github.com/jlcrochet/razorsharp/releases) page. The build is platform-agnostic and works on any OS with 64-bit .NET installed.

```bash
# Extract and install
unzip razorsharp.zip -d ~/.local/share/razorsharp
# Run with: dotnet ~/.local/share/razorsharp/razorsharp
```

### Building from source

```bash
# Build debug
dotnet build RazorSharp.sln

# Build release
dotnet build RazorSharp.sln -c Release
```

## Usage

```bash
# Run the server
dotnet run --project src/RazorSharp.Server -- [options]

# Or after building
./src/RazorSharp.Server/bin/Debug/net10.0/razorsharp [options]
```

### First Run

RazorSharp requires the Roslyn Language Server and Razor extension (~100MB) which must be downloaded before first use:

```bash
dotnet /path/to/razorsharp --download-dependencies
```

This only needs to be done once. Dependencies are cached in `~/.cache/razorsharp/` (see [Dependencies Cache](#dependencies-cache)).

If you have an apphost binary (`razorsharp`), you can run it directly instead of `dotnet`.

Downloads use a small retry/backoff for transient network failures.

If auto-updates are enabled (default), RazorSharp will download missing dependencies in the background and start language services automatically. Restart is only required when updating existing dependencies.

### Options

| Option | Description |
|--------|-------------|
| `-s, --source <path>` | Solution or directory path |
| `-l, --loglevel <level>` | Log level: Trace, Debug, Information, Warning, Error |
| `-v, --verbose` | Set log level to Trace |
| `--logFile <path>` | Write logs to file instead of stderr |
| `-hpid, --hostPID <pid>` | Shutdown when host process exits |
| `--download-dependencies` | Download dependencies and exit (does not start server) |
| `--check-updates` | Force a background dependency update check on startup |
| `--no-auto-update` | Disable background dependency auto-updates |
| `--skip-dependency-check` | Skip dependency check on startup |
| `-h, --help` | Show help |
| `--version` | Show version |

The server communicates via LSP over stdin/stdout.
Long-running requests (e.g., hover, code actions) emit workDoneProgress notifications when the client advertises support. The delay is configurable via `initializationOptions.requestProgressDelayMs`.

## Compatibility

RazorSharp aims to be a drop-in replacement for OmniSharp for C# workflows while adding Razor support:

- **Configuration:** Uses `omnisharp.json` in the same locations and precedence as OmniSharp for Roslyn settings.
- **Behavior:** Proxies Roslyn Language Server features to provide C# language services compatible with common OmniSharp clients.
- **Extensions:** Adds Razor/HTML support and other RazorSharp-specific behavior via LSP `initializationOptions`.

### Feature Routing (Summary)

- **C#** features are forwarded to Roslyn.
- **Razor** features come from Roslyn’s Razor extension.
- **HTML formatting** uses `vscode-html-language-server` when available.

## Editor Integration

### Neovim (with nvim-lspconfig)

If you're using Neovim, you're probably better off using [seblyng/roslyn.nvim](https://github.com/seblyng/roslyn.nvim) instead as it's more mature. That said, here is how you can use this with nvim-lspconfig:

```lua
local lspconfig = require('lspconfig')
local configs = require('lspconfig.configs')

configs.razorsharp = {
  default_config = {
    cmd = { 'dotnet', '/path/to/razorsharp.dll' },
    filetypes = { 'cs', 'razor' },
    root_dir = lspconfig.util.root_pattern('*.sln', '*.csproj'),
  },
}

lspconfig.razorsharp.setup({})
```

### Helix

Add to `~/.config/helix/languages.toml`:

```toml
[language-server.razorsharp]
command = "dotnet"
args = ["/path/to/razorsharp.dll"]

[[language]]
name = "c-sharp"
language-servers = ["razorsharp"]
roots = ["obj", "bin", ".git", "global.json"]

[[language]]
name = "razor"
scope = "source.razor"
file-types = ["razor", "cshtml"]
language-servers = ["razorsharp"]
roots = ["obj", "bin", ".git", "global.json"]
```

> **Note:** Helix's `roots` configuration requires exact directory/file names, not glob patterns. Using `obj` or `bin` (created by `dotnet build`) ensures Helix finds the project root correctly.

### Other Editors

Configure your editor's LSP client to:
1. Run `dotnet /path/to/razorsharp.dll`
2. Use `stdio` transport
3. Associate with `.cs`, `.razor`, and `.cshtml` files

## Configuration

RazorSharp uses **two configuration channels** to stay compatible with OmniSharp while exposing RazorSharp-specific options:

- **`omnisharp.json` (Roslyn/OmniSharp settings only)** — forwarded to Roslyn and treated the same way OmniSharp does.
- **LSP `initializationOptions` (RazorSharp settings)** — used only for RazorSharp-specific behavior (HTML LS, workspace discovery, capability toggles, etc.).

See [Compatibility](#compatibility) for the OmniSharp-aligned behavior RazorSharp targets.

### OmniSharp (Roslyn) configuration

RazorSharp reads Roslyn configuration from `omnisharp.json` files, compatible with OmniSharp:

**Locations (in order of precedence):**
1. `~/.omnisharp/omnisharp.json` (global)
2. `<workspace>/omnisharp.json` (local, overrides global)

You can also set `OMNISHARPHOME` environment variable to specify a custom global config directory.

### Example omnisharp.json

```json
{
  "FormattingOptions": {
    "enableEditorConfigSupport": true,
    "useTabs": false,
    "tabSize": 4,
    "indentationSize": 4,
    "newLinesForBracesInTypes": true,
    "newLinesForBracesInMethods": true
  },
  "RoslynExtensionsOptions": {
    "enableAnalyzersSupport": true,
    "enableImportCompletion": true,
    "enableAsyncCompletion": true,
    "analyzeOpenDocumentsOnly": false,
    "inlayHintsOptions": {
      "enableForParameters": true,
      "enableForTypes": true,
      "forImplicitVariableTypes": true,
      "forLambdaParameterTypes": true
    }
  },
  "RenameOptions": {
    "renameOverloads": false,
    "renameInStrings": false,
    "renameInComments": false
  }
}
```

### Supported Configuration Sections

| Section | Description |
|---------|-------------|
| `FormattingOptions` | C# code formatting (spacing, braces, indentation) |
| `RoslynExtensionsOptions` | Analyzers, completion, inlay hints |
| `RoslynExtensionsOptions.inlayHintsOptions` | Parameter and type hint settings |
| `RenameOptions` | Rename refactoring behavior |

## Performance Tips

- Exclude large directories via `workspace.excludeDirectories`.
- Enable file watching in `initializationOptions.workspace`.
- Use `--loglevel Warning` or higher for normal use.

## Troubleshooting

- **Dependencies missing:** run `--download-dependencies` (or allow the background auto-download; language services start automatically once downloads finish).
- **HTML formatting not working:** ensure Node.js and `vscode-langservers-extracted` are installed.
- **Slow startup:** check workspace excludes and file watcher settings.

## Versioning

RazorSharp uses the latest available Roslyn Language Server and Razor extension versions by default. You can pin versions via `initializationOptions.dependencies.pinnedRoslynVersion` and `initializationOptions.dependencies.pinnedExtensionVersion` when you need deterministic tooling; pinning disables auto-update checks (including `--check-updates`). If RazorSharp cannot reach the version feeds, it falls back to the last known versions in `version.json`; if nothing is cached, you'll need to pin versions or restore connectivity. With auto-update enabled, RazorSharp downloads missing dependencies in the background and starts language services automatically. When updating existing dependencies, a restart is required to switch to the new version. Use `--check-updates` to force a background update check even within the interval.


### HTML Language Server

RazorSharp uses `vscode-html-language-server` for HTML formatting in Razor files. This requires Node.js to be installed. Install the language server with:

```bash
npm install -g vscode-langservers-extracted
# or
pnpm install -g vscode-langservers-extracted
# or
yarn global add vscode-langservers-extracted
```

The HTML language server is started lazily when the first Razor file is opened, so it won't affect startup time if you're only working with C# files. To disable it entirely, configure your editor's LSP `initializationOptions`:

#### Helix

```toml
[language-server.razorsharp]
command = "dotnet"
args = ["/path/to/razorsharp.dll"]
config = { html = { enable = false } }
```

#### Neovim

```lua
lspconfig.razorsharp.setup({
  init_options = {
    html = { enable = false }
  }
})
```

Disabling the HTML language server will break formatting in Razor files.

### LSP Initialization Options

RazorSharp supports configuration via LSP `initializationOptions`. In Helix, this is the `config` key in `languages.toml`. These options allow you to enable/disable specific LSP capabilities, configure RazorSharp-specific behavior, and set logging/dependency defaults.

#### Available Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `html.enable` | bool | `true` | Enable/disable HTML language server |
| `fastStart` | bool | `true` | Forward LSP requests before Roslyn initialization completes |
| `fastStartDelayMs` | int | `0` | Delay (ms) before forwarding requests in fast-start mode |
| `capabilities.completionProvider.enabled` | bool | `true` | Enable/disable completion |
| `capabilities.completionProvider.triggerCharacters` | string[] | `[".", "<", "@", "(", "=", "/"]` | Characters that trigger completion |
| `capabilities.hoverProvider` | bool | `true` | Enable/disable hover |
| `capabilities.signatureHelpProvider.enabled` | bool | `true` | Enable/disable signature help |
| `capabilities.signatureHelpProvider.triggerCharacters` | string[] | `["(", ","]` | Characters that trigger signature help |
| `capabilities.signatureHelpProvider.retriggerCharacters` | string[] | `[")"]` | Characters that retrigger signature help |
| `capabilities.definitionProvider` | bool | `true` | Enable/disable go to definition |
| `capabilities.typeDefinitionProvider` | bool | `true` | Enable/disable go to type definition |
| `capabilities.implementationProvider` | bool | `true` | Enable/disable go to implementation |
| `capabilities.referencesProvider` | bool | `true` | Enable/disable find references |
| `capabilities.documentHighlightProvider` | bool | `true` | Enable/disable document highlight |
| `capabilities.documentSymbolProvider` | bool | `true` | Enable/disable document symbols |
| `capabilities.codeActionProvider` | bool | `true` | Enable/disable code actions |
| `capabilities.documentFormattingProvider` | bool | `true` | Enable/disable document formatting |
| `capabilities.documentRangeFormattingProvider` | bool | `true` | Enable/disable range formatting |
| `capabilities.documentOnTypeFormattingProvider.enabled` | bool | `true` | Enable/disable on-type formatting |
| `capabilities.documentOnTypeFormattingProvider.firstTriggerCharacter` | string | `";"` | First trigger character for on-type formatting |
| `capabilities.documentOnTypeFormattingProvider.moreTriggerCharacter` | string[] | `["}", "\n"]` | Additional trigger characters for on-type formatting |
| `capabilities.renameProvider` | bool | `true` | Enable/disable rename |
| `capabilities.foldingRangeProvider` | bool | `true` | Enable/disable folding range |
| `capabilities.workspaceSymbolProvider` | bool | `true` | Enable/disable workspace symbol search |
| `capabilities.semanticTokensProvider.enabled` | bool | `true` | Enable/disable semantic tokens |
| `capabilities.semanticTokensProvider.range` | bool | `true` | Enable/disable range requests |
| `capabilities.semanticTokensProvider.full` | bool | `true` | Enable/disable full document requests |
| `capabilities.inlayHintProvider` | bool | `true` | Enable/disable inlay hints |
| `capabilities.diagnosticProvider.enabled` | bool | `true` | Enable/disable all diagnostics |
| `capabilities.diagnosticProvider.syntax` | bool | `true` | Enable compiler syntax diagnostics (missing braces, etc.) |
| `capabilities.diagnosticProvider.semantic` | bool | `true` | Enable compiler semantic diagnostics (type errors, etc.) |
| `capabilities.diagnosticProvider.analyzerSyntax` | bool | `false` | Enable Roslyn analyzer syntax diagnostics |
| `capabilities.diagnosticProvider.analyzerSemantic` | bool | `false` | Enable Roslyn analyzer semantic diagnostics |
| `workspace.excludeDirectories` | string[] | `[]` | Additional directory names or glob patterns to skip during workspace search (supports `*` and `**`) |
| `workspace.excludeDirectoriesOverride` | string[] | `null` | Replace the default excluded directory list (names or glob patterns) |
| `workspace.enableFileWatching` | bool | `true` | Enable handling `workspace/didChangeWatchedFiles` notifications |
| `workspace.enableFileWatchingRegistration` | bool | `true` | Dynamically register file watchers with the client (when supported) |
| `roslyn.requestTimeoutMs` | int | `10000` | Timeout for Roslyn requests (ms). Set `<= 0` to disable. |
| `roslyn.autoUpdate` | bool | `true` | Enable background dependency auto-updates |
| `roslyn.autoUpdateIntervalHours` | int | `24` | Minimum hours between auto-update checks |
| `logging.level` | string | `Information` | Default log level (CLI `--loglevel` overrides) |
| `logging.file` | string | `null` | Log file path (CLI `--logFile` overrides) |
| `dependencies.skipDependencyCheck` | bool | `false` | Skip dependency presence checks (CLI `--skip-dependency-check` overrides) |
| `requestProgressDelayMs` | int | `1000` | Delay (ms) before showing workDoneProgress for user requests; set `< 0` to disable |
| `dependencies.pinnedRoslynVersion` | string | `null` | Pin Roslyn Language Server version (disables auto-update checks when set) |
| `dependencies.pinnedExtensionVersion` | string | `null` | Pin Razor extension version (disables auto-update checks when set) |

#### Example: Pin versions and set logging defaults

**Helix:**
```toml
[language-server.razorsharp]
command = "dotnet"
args = ["/path/to/razorsharp.dll"]
config.dependencies.pinnedRoslynVersion = "4.10.0"
config.dependencies.pinnedExtensionVersion = "2.3.4"
config.logging.level = "Warning"
config.logging.file = "/tmp/razorsharp.log"
```

**Neovim:**
```lua
lspconfig.razorsharp.setup({
  init_options = {
    dependencies = {
      pinnedRoslynVersion = "4.10.0",
      pinnedExtensionVersion = "2.3.4"
    },
    logging = {
      level = "Warning",
      file = "/tmp/razorsharp.log"
    }
  }
})
```

#### Example: Fast-start with a delay

**Helix:**
```toml
[language-server.razorsharp]
command = "dotnet"
args = ["/path/to/razorsharp.dll"]
config.fastStart = true
config.fastStartDelayMs = 1000
```

**Neovim:**
```lua
lspconfig.razorsharp.setup({
  init_options = {
    fastStart = true,
    fastStartDelayMs = 1000
  }
})
```

#### Example: Disable completion on `(`

To prevent completions from appearing immediately when typing `(` (useful to avoid large completion popups at function arguments):

**Helix:**
```toml
[language-server.razorsharp]
command = "dotnet"
args = ["/path/to/razorsharp.dll"]
config.capabilities.completionProvider.triggerCharacters = [".", "<", "@", "=", "/"]
```

**Neovim:**
```lua
lspconfig.razorsharp.setup({
  init_options = {
    capabilities = {
      completionProvider = {
        triggerCharacters = { ".", "<", "@", "=", "/" }
      }
    }
  }
})
```

#### Example: Disable inlay hints and semantic tokens

**Helix:**
```toml
[language-server.razorsharp]
command = "dotnet"
args = ["/path/to/razorsharp.dll"]
config.capabilities.inlayHintProvider = false
config.capabilities.semanticTokensProvider.enabled = false
```

**Neovim:**
```lua
lspconfig.razorsharp.setup({
  init_options = {
    capabilities = {
      inlayHintProvider = false,
      semanticTokensProvider = { enabled = false }
    }
  }
})
```

#### Example: Enable Roslyn analyzer diagnostics

By default, only compiler diagnostics (syntax and semantic) are enabled. To also get diagnostics from Roslyn analyzers:

**Helix:**
```toml
[language-server.razorsharp]
command = "dotnet"
args = ["/path/to/razorsharp.dll"]
config.capabilities.diagnosticProvider.analyzerSyntax = true
config.capabilities.diagnosticProvider.analyzerSemantic = true
```

**Neovim:**
```lua
lspconfig.razorsharp.setup({
  init_options = {
    capabilities = {
      diagnosticProvider = {
        analyzerSyntax = true,
        analyzerSemantic = true
      }
    }
  }
})
```

Note: Enabling analyzer diagnostics may impact performance on large codebases.

#### Example: Customize workspace directory exclusions

By default, RazorSharp skips `bin`, `obj`, `.git`, `.vs`, and `node_modules` during workspace search. You can extend or override that list (names or glob patterns):

**Helix:**
```toml
[language-server.razorsharp]
command = "dotnet"
args = ["/path/to/razorsharp.dll"]
config.workspace.excludeDirectories = ["dist", "out", "**/packages/**"]
```

**Neovim:**
```lua
lspconfig.razorsharp.setup({
  init_options = {
    workspace = {
      excludeDirectories = { "dist", "out", "**/packages/**" }
    }
  }
})
```

To override the defaults entirely:

**Helix:**
```toml
[language-server.razorsharp]
command = "dotnet"
args = ["/path/to/razorsharp.dll"]
config.workspace.excludeDirectoriesOverride = ["obj"]
```

**Neovim:**
```lua
lspconfig.razorsharp.setup({
  init_options = {
    workspace = {
      excludeDirectoriesOverride = { "obj" }
    }
  }
})
```

#### Example: Increase Roslyn request timeout

**Helix:**
```toml
[language-server.razorsharp]
command = "dotnet"
args = ["/path/to/razorsharp.dll"]
config.roslyn.requestTimeoutMs = 20000
```

**Neovim:**
```lua
lspconfig.razorsharp.setup({
  init_options = {
    roslyn = {
      requestTimeoutMs = 20000
    }
  }
})
```

#### Example: Disable dynamic file watcher registration

If your editor already configures static file watchers, you can avoid duplicate notifications:

**Helix:**
```toml
[language-server.razorsharp]
command = "dotnet"
args = ["/path/to/razorsharp.dll"]
config.workspace.enableFileWatchingRegistration = false
```

**Neovim:**
```lua
lspconfig.razorsharp.setup({
  init_options = {
    workspace = {
      enableFileWatchingRegistration = false
    }
  }
})
```

By default, RazorSharp registers watchers (scoped to the workspace root when possible) for:

- `**/*.sln`, `**/*.slnf`, `**/*.slnx`
- `**/*.csproj`, `**/*.csproj.user`
- `**/*.props`, `**/*.targets`, `**/Directory.Build.props`, `**/Directory.Build.targets`
- `**/*.cs`, `**/*.razor`, `**/*.razor.cs`, `**/*.cshtml`
- `**/.editorconfig`
- `**/global.json`
- `**/omnisharp.json`
- `**/obj/**/generated/**`

## Architecture

RazorSharp acts as a proxy between your editor and the Roslyn Language Server:

```
Editor (LSP client)
    |
    v (stdin/stdout)
RazorSharp
    |
    +---> Roslyn Language Server (C#/Razor features)
    |
    +---> HTML Language Server (HTML formatting, optional)
```

## Dependencies Cache

Downloaded dependencies are stored in:
- Linux/macOS: `~/.cache/razorsharp/` (or `$XDG_CACHE_HOME/razorsharp/`)
- Windows: `%LOCALAPPDATA%\razorsharp\`

## License

MIT
