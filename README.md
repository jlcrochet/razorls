# RazorLS

A standalone, IDE-agnostic Language Server Protocol (LSP) server for Razor files (`.razor`, `.cshtml`). Provides language features like completions, hover, go-to-definition, and more for Razor/Blazor development in any LSP-compatible editor.

I made this because I didn't want to wait for Microsoft to release an official solution and because [seblyng/roslyn.nvim](https://github.com/seblyng/roslyn.nvim) only works with Neovim. This project is heavily inspired by seblyng/roslyn.nvim.

By the way, if you're looking for a Tree-sitter parser for Razor files, check out [mine](https://github.com/jlcrochet/tree-sitter-razor).

## Installation

### Pre-built binaries

Download the latest release for your platform from the [Releases](https://github.com/jlcrochet/razorls/releases) page:

| Platform | Download |
|----------|----------|
| Linux x64 | `razorls-linux-x64` |
| Linux ARM64 | `razorls-linux-arm64` |
| macOS x64 (Intel) | `razorls-osx-x64` |
| macOS ARM64 (Apple Silicon) | `razorls-osx-arm64` |
| Windows x64 | `razorls-win-x64.exe` |
| Portable (.NET required) | `razorls-portable.zip` |

The standalone binaries include the .NET runtime (~65MB). If you already have .NET 10+ installed, use the portable version (~2MB) instead:

```bash
# Portable: extract and run with dotnet
unzip razorls-portable.zip -d ~/.local/share/razorls
# Run with: dotnet ~/.local/share/razorls/razorls.dll
```

```bash
# Standalone: make executable and move to PATH
chmod +x razorls-linux-x64
sudo mv razorls-linux-x64 /usr/local/bin/razorls
```

### Building from source

```bash
# Build debug
dotnet build RazorLS.sln

# Build release
dotnet build RazorLS.sln -c Release
```

## Usage

```bash
# Run the server
dotnet run --project src/RazorLS.Server -- [options]

# Or after building
./src/RazorLS.Server/bin/Debug/net10.0/razorls [options]
```

### Options

| Option | Description |
|--------|-------------|
| `-s, --source <path>` | Solution or directory path |
| `-l, --loglevel <level>` | Log level: Trace, Debug, Information, Warning, Error |
| `-v, --verbose` | Set log level to Debug |
| `--logFile <path>` | Write logs to file instead of stderr |
| `-hpid, --hostPID <pid>` | Shutdown when host process exits |
| `--skip-dependency-check` | Skip automatic dependency download |
| `-h, --help` | Show help |
| `--version` | Show version |

The server communicates via LSP over stdin/stdout.

## Editor Integration

### Neovim (with nvim-lspconfig)

If you're using Neovim, you're probably better off using [seblyng/roslyn.nvim](https://github.com/seblyng/roslyn.nvim) instead as it's more mature. That said, here is how you can use this with nvim-lspconfig:

```lua
local lspconfig = require('lspconfig')
local configs = require('lspconfig.configs')

configs.razorls = {
  default_config = {
    cmd = { 'razorls' },  -- assumes razorls is in PATH
    filetypes = { 'razor' },
    root_dir = lspconfig.util.root_pattern('*.sln', '*.csproj'),
  },
}

lspconfig.razorls.setup({})
```

<details>
<summary>Portable version</summary>

```lua
configs.razorls = {
  default_config = {
    cmd = { 'dotnet', '/path/to/razorls.dll' },
    filetypes = { 'razor' },
    root_dir = lspconfig.util.root_pattern('*.sln', '*.csproj'),
  },
}
```

</details>

### Helix

Add to `~/.config/helix/languages.toml`:

```toml
[language-server.razorls]
command = "razorls"

[[language]]
name = "razor"
scope = "source.razor"
file-types = ["razor", "cshtml"]
language-servers = ["razorls"]
roots = ["*.sln", "*.csproj"]
```

<details>
<summary>Portable version</summary>

```toml
[language-server.razorls]
command = "dotnet"
args = ["/path/to/razorls.dll"]

[[language]]
name = "razor"
scope = "source.razor"
file-types = ["razor", "cshtml"]
language-servers = ["razorls"]
roots = ["*.sln", "*.csproj"]
```

</details>

### Other Editors

Configure your editor's LSP client to:
1. Run `razorls`
2. Use `stdio` transport
3. Associate with `.razor` and `.cshtml` files

## Configuration

RazorLS reads configuration from `omnisharp.json` files, compatible with OmniSharp:

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

## Architecture

RazorLS acts as a proxy between your editor and the Roslyn Language Server:

```
Editor (LSP client)
    |
    v (stdin/stdout)
RazorLS
    |
    v
Roslyn Language Server (C#/Razor features)
```

On first run, RazorLS automatically downloads the required Roslyn and Razor extension dependencies from the VS Code C# extension.

## Dependencies Cache

Downloaded dependencies are stored in:
- Linux/macOS: `~/.cache/razorls/`
- Or `$XDG_CACHE_HOME/razorls/`

## License

MIT
