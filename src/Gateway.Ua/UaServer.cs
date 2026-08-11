using Opc.Ua;
using Opc.Ua.Server;

namespace Gateway.Ua;

/// Servidor estandar del stack, mas nuestro NodeManager registrado.
public class UaServer : StandardServer
{
    private readonly UaOptions _options;

    public GatewayNodeManager? NodeManager { get; private set; }

    public UaServer(UaOptions options)
    {
        _options = options;
    }

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server, ApplicationConfiguration configuration)
    {
        NodeManager = new GatewayNodeManager(server, configuration, _options.NamespaceUri);
        return new MasterNodeManager(server, configuration, null,
            new INodeManager[] { NodeManager });
    }
}
