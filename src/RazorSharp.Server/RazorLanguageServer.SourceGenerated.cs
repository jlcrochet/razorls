using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Protocol.Messages;
using RazorSharp.Server.Utilities;

namespace RazorSharp.Server;

public partial class RazorLanguageServer
{
    private static bool IsSourceGeneratedPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');
        return normalized.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) >= 0 &&
               normalized.IndexOf("/generated/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool TryUpdateSourceGeneratedIndexForChange(string path, FileChangeType changeType)
    {
        if (!TryParseSourceGeneratedPath(path, out var key, out var isDebug))
        {
            return false;
        }

        var fileExists = File.Exists(path);
        lock (_sourceGeneratedCacheLock)
        {
            if (changeType == FileChangeType.Deleted || !fileExists)
            {
                if (_sourceGeneratedIndex.TryGetValue(key, out var entries))
                {
                    for (var i = entries.Count - 1; i >= 0; i--)
                    {
                        if (entries[i].Path.Equals(path, _uriComparison))
                        {
                            entries.RemoveAt(i);
                        }
                    }

                    if (entries.Count == 0)
                    {
                        _sourceGeneratedIndex.Remove(key);
                    }
                }
            }
            else
            {
                AddOrUpdateSourceGeneratedEntry(_sourceGeneratedIndex, key, path, isDebug);
            }

            if (_sourceGeneratedIndexState == SourceGeneratedIndexState.Uninitialized)
                _sourceGeneratedIndexState = SourceGeneratedIndexState.IncrementalOnly;
        }

        Interlocked.Increment(ref _sourceGeneratedIndexIncrementalUpdates);
        return true;
    }

    private static bool TryParseSourceGeneratedPath(string path, out string key, out bool isDebug)
    {
        key = "";
        isDebug = false;

        var normalized = path.Replace('\\', '/');
        var objIndex = normalized.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase);
        if (objIndex < 0)
        {
            return false;
        }

        var afterObj = normalized[(objIndex + 5)..];
        var segments = afterObj.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var generatedIndex = -1;
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Equals("generated", StringComparison.OrdinalIgnoreCase))
            {
                generatedIndex = i;
                break;
            }
        }

        if (generatedIndex < 0 || segments.Length < generatedIndex + 4)
        {
            return false;
        }

        var config = segments[0];
        isDebug = config.Equals("Debug", StringComparison.OrdinalIgnoreCase);

        var assemblyName = segments[generatedIndex + 1];
        var typeName = segments[generatedIndex + 2];
        var hintName = segments[generatedIndex + 3];

        if (string.IsNullOrEmpty(assemblyName) ||
            string.IsNullOrEmpty(typeName) ||
            string.IsNullOrEmpty(hintName))
        {
            return false;
        }

        key = MakeSourceGeneratedKey(assemblyName, typeName, hintName);
        return true;
    }

    /// <summary>
    /// Transforms roslyn-source-generated:// URIs in location responses to file:// URIs
    /// pointing to the generated files on disk (when EmitCompilerGeneratedFiles is enabled).
    /// This allows editors like Helix that don't support custom URI schemes to navigate to generated code.
    /// </summary>
    private JsonElement TransformSourceGeneratedUris(JsonElement response)
    {
        if (_workspaceRoot == null)
        {
            return response;
        }

        // Response can be: null, Location, Location[], LocationLink[]
        if (response.ValueKind == JsonValueKind.Null)
        {
            return response;
        }

        if (response.ValueKind == JsonValueKind.Array)
        {
            var items = new List<JsonElement>(response.GetArrayLength());
            var anyChanged = false;
            foreach (var item in response.EnumerateArray())
            {
                var newItem = TransformLocationElement(item, out var changed);
                if (changed) anyChanged = true;
                items.Add(changed ? newItem : item);
            }
            if (!anyChanged) return response;
            return JsonSerializer.SerializeToElement(items);
        }

        // Single location
        return TransformLocationElement(response, out _);
    }

    private JsonElement TransformLocationElement(JsonElement element, out bool changed)
    {
        changed = false;

        // Check if this is a Location (has "uri") or LocationLink (has "targetUri")
        if (element.TryGetProperty("uri", out var uriProp))
        {
            var uri = uriProp.GetString();
            if (uri != null && TryMapSourceGeneratedUri(uri, out var filePath))
            {
                changed = true;
                return CloneJsonElementWithReplacedProperty(element, "uri", new Uri(filePath).AbsoluteUri);
            }
        }
        else if (element.TryGetProperty("targetUri", out var targetUriProp))
        {
            var uri = targetUriProp.GetString();
            if (uri != null && TryMapSourceGeneratedUri(uri, out var filePath))
            {
                changed = true;
                return CloneJsonElementWithReplacedProperty(element, "targetUri", new Uri(filePath).AbsoluteUri);
            }
        }

        return element;
    }

    /// <summary>
    /// Clones a JsonElement, replacing a single string property value.
    /// Uses Utf8JsonWriter with pooled buffers to avoid allocations.
    /// </summary>
    private static JsonElement CloneJsonElementWithReplacedProperty(JsonElement element, string propertyName, string newValue)
    {
        using var bufferWriter = new ArrayPoolBufferWriter();
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name == propertyName)
                {
                    writer.WriteString(propertyName, newValue);
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(bufferWriter.WrittenMemory);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Tries to map a roslyn-source-generated:// URI to a file path on disk.
    /// The URI format is: roslyn-source-generated://{projectId}/{hintName}?assemblyName=...&typeName=...&hintName=...
    /// Generated files are typically at: obj/{Configuration}/{TFM}/generated/{assemblyName}/{typeName}/{hintName}
    /// </summary>
    private bool TryMapSourceGeneratedUri(string uri, out string filePath)
    {
        filePath = "";

        if (!uri.StartsWith("roslyn-source-generated://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (_sourceGeneratedCacheLock)
        {
            if (_sourceGeneratedUriCache.TryGetValue(uri, out var cachedPath))
            {
                if (File.Exists(cachedPath))
                {
                    Interlocked.Increment(ref _sourceGeneratedCacheHits);
                    filePath = cachedPath;
                    return true;
                }
                _sourceGeneratedUriCache.Remove(uri);
            }
        }

        try
        {
            var parsed = new Uri(uri);
            var projectId = GetSourceGeneratedProjectId(parsed);

            if (!TryGetQueryValue(parsed.Query, "assemblyName", out var assemblyName) ||
                !TryGetQueryValue(parsed.Query, "typeName", out var typeName) ||
                !TryGetQueryValue(parsed.Query, "hintName", out var hintName) ||
                string.IsNullOrEmpty(assemblyName) ||
                string.IsNullOrEmpty(typeName) ||
                string.IsNullOrEmpty(hintName))
            {
                _logger.LogDebug("Source generated URI missing required query parameters: {Uri}", uri);
                return false;
            }

            var key = MakeSourceGeneratedKey(assemblyName, typeName, hintName);
            if (TryGetSourceGeneratedPath(key, projectId, out var found))
            {
                filePath = found;
                lock (_sourceGeneratedCacheLock)
                {
                    _sourceGeneratedUriCache[uri] = found;
                }
                Interlocked.Increment(ref _sourceGeneratedCacheMisses);
                _logger.LogDebug("Mapped source generated URI {Uri} to {FilePath}", uri, filePath);
                return true;
            }

            Interlocked.Increment(ref _sourceGeneratedCacheMisses);
            _logger.LogDebug("No generated file found for URI: {Uri}", uri);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse source generated URI: {Uri}", uri);
            return false;
        }
    }

    private static bool TryGetQueryValue(string query, string key, out string? value)
    {
        value = null;
        if (string.IsNullOrEmpty(query))
        {
            return false;
        }

        var span = query.AsSpan();
        if (span.Length > 0 && span[0] == '?')
        {
            span = span[1..];
        }

        var keySpan = key.AsSpan();
        while (!span.IsEmpty)
        {
            var ampIndex = span.IndexOf('&');
            var pair = ampIndex >= 0 ? span[..ampIndex] : span;
            span = ampIndex >= 0 ? span[(ampIndex + 1)..] : ReadOnlySpan<char>.Empty;

            if (pair.IsEmpty)
            {
                continue;
            }

            var eqIndex = pair.IndexOf('=');
            if (eqIndex <= 0)
            {
                continue;
            }

            var name = pair[..eqIndex];
            if (!name.SequenceEqual(keySpan))
            {
                continue;
            }

            var rawValue = pair[(eqIndex + 1)..];
            if (rawValue.IndexOfAny('%', '+') < 0)
            {
                value = rawValue.ToString();
                return true;
            }

            value = WebUtility.UrlDecode(rawValue.ToString());
            return true;
        }

        return false;
    }

    private static string MakeSourceGeneratedKey(string assemblyName, string typeName, string hintName)
    {
        var length = assemblyName.Length + 1 + typeName.Length + 1 + hintName.Length;
        return string.Create(length, (assemblyName, typeName, hintName), static (span, state) =>
        {
            var pos = 0;
            state.assemblyName.AsSpan().CopyTo(span);
            pos += state.assemblyName.Length;
            span[pos++] = '\0';
            state.typeName.AsSpan().CopyTo(span[pos..]);
            pos += state.typeName.Length;
            span[pos++] = '\0';
            state.hintName.AsSpan().CopyTo(span[pos..]);
        });
    }

    private static string? GetSourceGeneratedProjectId(Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.Host))
        {
            return uri.Host;
        }

        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var slash = path.IndexOf('/');
        return slash >= 0 ? path[..slash] : path;
    }

    private bool TryGetSourceGeneratedPath(string key, string? projectId, out string filePath)
    {
        filePath = "";
        var now = DateTime.UtcNow;
        var shouldRefresh = false;
        List<SourceGeneratedEntry>? entries = null;

        lock (_sourceGeneratedCacheLock)
        {
            shouldRefresh = _sourceGeneratedIndexState == SourceGeneratedIndexState.Uninitialized ||
                            (_sourceGeneratedIndexState == SourceGeneratedIndexState.FullScan &&
                             (now - _sourceGeneratedIndexLastFullScan) > SourceGeneratedIndexRefreshInterval);

            if (_sourceGeneratedIndex.TryGetValue(key, out entries))
            {
                if (!shouldRefresh && TrySelectSourceGeneratedEntry(key, entries, projectId, out var selected))
                {
                    filePath = selected.Path;
                    return true;
                }

                if (!AnyEntryExists(entries))
                {
                    _sourceGeneratedIndex.Remove(key);
                    shouldRefresh = true;
                }
            }
        }

        if (shouldRefresh)
        {
            RefreshSourceGeneratedIndex();
            lock (_sourceGeneratedCacheLock)
            {
                if (_sourceGeneratedIndex.TryGetValue(key, out entries) &&
                    TrySelectSourceGeneratedEntry(key, entries, projectId, out var selected))
                {
                    filePath = selected.Path;
                    return true;
                }
            }
        }

        return false;
    }

    private bool TrySelectSourceGeneratedEntry(
        string key,
        List<SourceGeneratedEntry> entries,
        string? projectId,
        out SourceGeneratedEntry selected)
    {
        selected = default;

        var hasExisting = false;
        var existingCount = 0;
        SourceGeneratedEntry bestAny = default;

        var hasMatch = false;
        SourceGeneratedEntry bestMatch = default;
        var matchCount = 0;

        foreach (var entry in entries)
        {
            if (!File.Exists(entry.Path))
            {
                continue;
            }

            existingCount++;
            if (!hasExisting || IsBetterSourceGeneratedEntry(entry, bestAny))
            {
                bestAny = entry;
                hasExisting = true;
            }

            if (!string.IsNullOrEmpty(projectId) && entry.Path.Contains(projectId, _uriComparison))
            {
                matchCount++;
                if (!hasMatch || IsBetterSourceGeneratedEntry(entry, bestMatch))
                {
                    bestMatch = entry;
                    hasMatch = true;
                }
            }
        }

        if (!hasExisting)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(projectId))
        {
            if (hasMatch && matchCount == 1)
            {
                selected = bestMatch;
                return true;
            }

            if (existingCount == 1)
            {
                selected = bestAny;
                return true;
            }

            if (matchCount > 1)
            {
                _logger.LogDebug(
                    "Multiple source-generated candidates found for key {Key} and projectId {ProjectId}; skipping mapping.",
                    key,
                    projectId);
                return false;
            }

            if (existingCount > 1)
            {
                _logger.LogDebug(
                    "No source-generated candidates matched projectId {ProjectId} for key {Key}; skipping mapping.",
                    projectId,
                    key);
            }

            return false;
        }

        if (existingCount == 1)
        {
            selected = bestAny;
            return true;
        }

        if (existingCount > 1)
        {
            _logger.LogDebug(
                "Multiple source-generated candidates found for key {Key} without project id; skipping mapping.",
                key);
        }

        return false;
    }

    private static bool AnyEntryExists(List<SourceGeneratedEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (File.Exists(entry.Path))
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<string> EnumerateObjDirectories(string rootPath)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current, "*", SourceGeneratedEnumerateOptions);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error scanning directories under {Path}", current);
                continue;
            }

            foreach (var dir in directories)
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                {
                    yield return dir;
                    continue;
                }

                if (_workspaceManager.ShouldSkipDirectory(rootPath, dir, name))
                {
                    continue;
                }

                pending.Push(dir);
            }
        }
    }

    internal IEnumerable<string> EnumerateObjDirectoriesForTests(string rootPath)
        => EnumerateObjDirectories(rootPath);

    internal void ConfigureExcludedDirectoriesForTests(string[]? overrideDirectories, string[]? additionalDirectories)
        => _workspaceManager.ConfigureExcludedDirectories(overrideDirectories, additionalDirectories);

    private void RefreshSourceGeneratedIndex()
    {
        if (_workspaceRoot == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _sourceGeneratedIndexRefreshInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            Interlocked.Increment(ref _sourceGeneratedIndexRefreshes);
            var newIndex = new Dictionary<string, List<SourceGeneratedEntry>>(StringComparer.OrdinalIgnoreCase);

            var ct = _lifetimeCts.Token;
            foreach (var objDir in EnumerateObjDirectories(_workspaceRoot))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    foreach (var configDir in Directory.EnumerateDirectories(objDir, "*", SourceGeneratedEnumerateOptions))
                    {
                        var configName = Path.GetFileName(configDir);
                        var isDebug = string.Equals(configName, "Debug", StringComparison.OrdinalIgnoreCase);

                        foreach (var tfmDir in Directory.EnumerateDirectories(configDir, "*", SourceGeneratedEnumerateOptions))
                        {
                            var generatedRoot = Path.Combine(tfmDir, "generated");
                            if (!Directory.Exists(generatedRoot))
                            {
                                continue;
                            }

                            foreach (var assemblyDir in Directory.EnumerateDirectories(generatedRoot, "*", SourceGeneratedEnumerateOptions))
                            {
                                var assemblyName = Path.GetFileName(assemblyDir);
                                if (string.IsNullOrEmpty(assemblyName))
                                {
                                    continue;
                                }

                                foreach (var typeDir in Directory.EnumerateDirectories(assemblyDir, "*", SourceGeneratedEnumerateOptions))
                                {
                                    var typeName = Path.GetFileName(typeDir);
                                    if (string.IsNullOrEmpty(typeName))
                                    {
                                        continue;
                                    }

                                    foreach (var file in Directory.EnumerateFiles(typeDir, "*", SourceGeneratedEnumerateOptions))
                                    {
                                        ct.ThrowIfCancellationRequested();
                                        var hintName = Path.GetFileName(file);
                                        if (string.IsNullOrEmpty(hintName))
                                        {
                                            continue;
                                        }

                                        var key = MakeSourceGeneratedKey(assemblyName, typeName, hintName);
                                        AddOrUpdateSourceGeneratedEntry(newIndex, key, file, isDebug);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error scanning generated files under {ObjDir}", objDir);
                }
            }

            var scanTime = DateTime.UtcNow;
            lock (_sourceGeneratedCacheLock)
            {
                _sourceGeneratedIndex.Clear();
                foreach (var kvp in newIndex)
                {
                    _sourceGeneratedIndex[kvp.Key] = kvp.Value;
                }
                _sourceGeneratedIndexState = SourceGeneratedIndexState.FullScan;
                _sourceGeneratedIndexLastFullScan = scanTime;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh source-generated file index");
        }
        finally
        {
            Interlocked.Exchange(ref _sourceGeneratedIndexRefreshInProgress, 0);
        }
    }

    private void AddOrUpdateSourceGeneratedEntry(
        Dictionary<string, List<SourceGeneratedEntry>> index,
        string key,
        string path,
        bool isDebug)
    {
        DateTime lastWriteUtc;
        try
        {
            lastWriteUtc = File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            lastWriteUtc = DateTime.MinValue;
        }

        var candidate = new SourceGeneratedEntry(path, isDebug, lastWriteUtc);
        if (index.TryGetValue(key, out var entries))
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Path.Equals(path, _uriComparison))
                {
                    if (IsBetterSourceGeneratedEntry(candidate, entries[i]))
                    {
                        entries[i] = candidate;
                    }
                    return;
                }
            }

            entries.Add(candidate);
            return;
        }

        index[key] = new List<SourceGeneratedEntry> { candidate };
    }

    private static bool IsBetterSourceGeneratedEntry(SourceGeneratedEntry candidate, SourceGeneratedEntry existing)
    {
        if (candidate.IsDebug != existing.IsDebug)
        {
            return candidate.IsDebug;
        }

        return candidate.LastWriteUtc > existing.LastWriteUtc;
    }
}
