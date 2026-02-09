using System.Text.Json;
using StreamJsonRpc;

namespace RazorSharp.Server;

internal interface IClientCapabilityRegistrar
{
    Task RegisterAsync(object parameters, CancellationToken ct);
}

internal sealed class JsonRpcClientCapabilityRegistrar : IClientCapabilityRegistrar
{
    readonly JsonRpc _rpc;
    readonly string _method;

    public JsonRpcClientCapabilityRegistrar(JsonRpc rpc, string method)
    {
        _rpc = rpc;
        _method = method;
    }

    public Task RegisterAsync(object parameters, CancellationToken ct)
        => _rpc.InvokeWithParameterObjectAsync<JsonElement?>(_method, parameters, ct);
}
