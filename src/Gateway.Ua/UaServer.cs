using Gateway.Core;
using Opc.Ua;
using Opc.Ua.Server;

namespace Gateway.Ua;

/// Servidor estandar del stack, mas nuestro NodeManager registrado.
public class UaServer : StandardServer
{
    private readonly UaOptions _options;
    private readonly IReadOnlyList<TagDefinition> _tagDefinitions;

    public GatewayNodeManager? NodeManager { get; private set; }

    public UaServer(UaOptions options, IReadOnlyList<TagDefinition> tagDefinitions)
    {
        _options = options;
        _tagDefinitions = tagDefinitions;
    }

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server, ApplicationConfiguration configuration)
    {
        NodeManager = new GatewayNodeManager(server, configuration, _options.NamespaceUri, _tagDefinitions);
        return new MasterNodeManager(server, configuration, null,
            new INodeManager[] { NodeManager });
    }
}
