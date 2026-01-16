# RazorSharp

A standalone, IDE-agnostic Language Server Protocol (LSP) server for C# and Razor files. Provides language features like completions, hover, go-to-definition, and more for C#, Razor, and Blazor development in any LSP-compatible editor.

I made this because I didn't want to wait for Microsoft to release an official solution and because [seblyng/roslyn.nvim](https://github.com/seblyng/roslyn.nvim) only works with Neovim. This project is heavily inspired by seblyng/roslyn.nvim.

By the way, if you're looking for a Tree-sitter parser for Razor files, check out [mine](https://github.com/jlcrochet/tree-sitter-razor).

## Installation

### Requirements

- **.NET 10+** - Required for both RazorSharp and the Roslyn Language Server dependencies it downloads
- **Node.js** - Optional, for HTML language server support (see [HTML Language Server](#html-language-server))

### Pre-built binaries

Download `razorsharp.zip` from the [Releases](https://github.com/jlcrochet/razorsharp/releases) page. The build is platform-agnostic and works on any OS with .NET installed.

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

### Options

| Option | Description |
|--------|-------------|
| `-s, --source <path>` | Solution or directory path |
| `-l, --loglevel <level>` | Log level: Trace, Debug, Information, Warning, Error |
| `-v, --verbose` | Set log level to Debug |
| `--logFile <path>` | Write logs to file instead of stderr |
| `-hpid, --hostPID <pid>` | Shutdown when host process exits |
| `--download-dependencies` | Download dependencies and exit (does not start server) |
| `--skip-dependency-check` | Skip dependency check on startup |
| `-h, --help` | Show help |
| `--version` | Show version |

The server communicates via LSP over stdin/stdout.

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

[[language]]
name = "razor"
scope = "source.razor"
file-types = ["razor", "cshtml"]
language-servers = ["razorsharp"]
roots = ["*.sln", "*.csproj"]
```

### Other Editors

Configure your editor's LSP client to:
1. Run `dotnet /path/to/razorsharp`
2. Use `stdio` transport
3. Associate with `.cs`, `.razor`, and `.cshtml` files

## Configuration

RazorSharp reads configuration from `omnisharp.json` files, compatible with OmniSharp:

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

### HTML Language Server

RazorSharp uses `vscode-html-language-server` for HTML formatting in Razor files. This requires Node.js to be installed. Install the language server with:

```bash
npm install -g vscode-langservers-extracted
# or
pnpm install -g vscode-langservers-extracted
# or
yarn global add vscode-langservers-extracted
```

The HTML language server is enabled by default. To disable it, configure your editor's LSP `initializationOptions`:

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

Disabling the HTML language server may improve startup times but it will break formatting.

### LSP Initialization Options

RazorSharp supports configuration via LSP `initializationOptions`. In Helix, this is the `config` key in `languages.toml`. These options allow you to enable/disable specific LSP capabilities and customize trigger characters.

#### Available Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `html.enable` | bool | `true` | Enable/disable HTML language server |
| `capabilities.completionProvider.enabled` | bool | `true` | Enable/disable completion |
| `capabilities.completionProvider.triggerCharacters` | string[] | `[".", "<", "@", " ", "(", "\"", "'", "=", "/"]` | Characters that trigger completion (must be subset of default) |
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

#### Example: Disable completion on `(`

To prevent completions from appearing immediately when typing `(` (useful to avoid large completion popups at function arguments):

**Helix:**
```toml
[language-server.razorsharp]
command = "dotnet"
args = ["/path/to/razorsharp.dll"]
config.capabilities.completionProvider.triggerCharacters = [".", "<", "@", " ", "\"", "'", "=", "/"]
```

**Neovim:**
```lua
lspconfig.razorsharp.setup({
  init_options = {
    capabilities = {
      completionProvider = {
        triggerCharacters = { ".", "<", "@", " ", "\"", "'", "=", "/" }
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
