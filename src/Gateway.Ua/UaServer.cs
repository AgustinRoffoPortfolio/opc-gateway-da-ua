using Gateway.Core;
using Opc.Ua;
using Opc.Ua.Server;

namespace Gateway.Ua;

/// Servidor estandar del stack, mas nuestro NodeManager registrado.
public class UaServer : StandardServer
{
    private readonly string _namespaceUri;
    private readonly IReadOnlyList<TagDefinition> _tagDefinitions;
    private readonly TagCache _cache;

    public GatewayNodeManager? NodeManager { get; private set; }

    /// Recibe el namespace suelto y no UaOptions entera: es lo unico que usa,
    /// y depender del objeto de configuracion completo ata esta clase a cada
    /// campo que se le agregue despues.
    public UaServer(string namespaceUri, IReadOnlyList<TagDefinition> tagDefinitions, TagCache cache)
    {
        _namespaceUri = namespaceUri;
        _tagDefinitions = tagDefinitions;
        _cache = cache;
    }

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server, ApplicationConfiguration configuration)
    {
        NodeManager = new GatewayNodeManager(server, configuration, _namespaceUri, _tagDefinitions, _cache);
        return new MasterNodeManager(server, configuration, null,
            new INodeManager[] { NodeManager });
    }
}