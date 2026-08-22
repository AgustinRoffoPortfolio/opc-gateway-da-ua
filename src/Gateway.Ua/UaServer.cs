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
    private readonly UaAuditCounters _audit;

    public GatewayNodeManager? NodeManager { get; private set; }

    /// Recibe el namespace suelto y no UaOptions entera: es lo unico que usa,
    /// y depender del objeto de configuracion completo ata esta clase a cada
    /// campo que se le agregue despues.
    public UaServer(string namespaceUri, IReadOnlyList<TagDefinition> tagDefinitions,
        TagCache cache, UaAuditCounters audit)
    {
        _namespaceUri = namespaceUri;
        _tagDefinitions = tagDefinitions;
        _cache = cache;
        _audit = audit;
    }

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server, ApplicationConfiguration configuration)
    {
        NodeManager = new GatewayNodeManager(server, configuration, _namespaceUri, _tagDefinitions, _cache);
        return new MasterNodeManager(server, configuration, null,
            new INodeManager[] { NodeManager });
    }

    /// <summary>
    /// Engancha la auditoria de sesiones una vez que el servidor esta arriba.
    /// </summary>
    /// <remarks>
    /// Aca y no en el constructor porque el SessionManager no existe hasta que
    /// el stack termina de arrancar. Los eventos de sesion dan las altas y bajas;
    /// los rechazos no pasan por aca y se cuentan aparte (ver ActivateSession).
    /// </remarks>
    protected override void OnServerStarted(IServerInternal server)
    {
        base.OnServerStarted(server);

        server.SessionManager.SessionCreated += (_, _) => _audit.RecordSessionCreated();
        server.SessionManager.SessionClosing += (_, _) => _audit.RecordSessionClosed();
    }

    /// <summary>
    /// Cuenta los intentos rechazados por token de usuario, sin alterar la decision.
    /// </summary>
    /// <remarks>
    /// El stack no expone un evento para esto: el rechazo sale como excepcion
    /// desde adentro de la validacion del token. Envolver el metodo es la unica
    /// forma de verlo sin reimplementar esa validacion, y por eso el catch
    /// relanza siempre: aca se observa, no se decide.
    ///
    /// Es un camino distinto del rechazo por certificado, que ocurre antes, al
    /// abrir el canal seguro, cuando todavia no hay sesion. Un contador unico
    /// para los dos mandaria a revisar la PKI por un cliente que en realidad
    /// esta mandando usuario y contrasena a un server que solo acepta anonimo.
    /// </remarks>
    public override async Task<ActivateSessionResponse> ActivateSessionAsync(
        SecureChannelContext context,
        RequestHeader requestHeader,
        SignatureData clientSignature,
        SignedSoftwareCertificateCollection clientSoftwareCertificates,
        StringCollection localeIds,
        ExtensionObject userIdentityToken,
        SignatureData userTokenSignature,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.ActivateSessionAsync(context, requestHeader, clientSignature,
                clientSoftwareCertificates, localeIds, userIdentityToken, userTokenSignature,
                cancellationToken);
        }
        catch (ServiceResultException ex)
        {
            // No todo lo que falla al activar una sesion es un rechazo de
            // identidad: por aca tambien salen fallas del ciclo de vida del
            // servidor. Medidos los dos casos: BadServerHalted durante el
            // arranque, y BadSessionIdInvalid cuando un cliente reintenta con
            // la sesion de una corrida anterior.
            //
            // Esos no se cuentan. Un contador de intentos rechazados que sube
            // cada vez que se reinicia el gateway con un cliente abierto pierde
            // el unico significado que tenia: cuantas veces alguien no pudo
            // entrar. Se loguean en Debug por si hiciera falta rastrearlos, pero
            // no se publican como numero.
            var isIdentityRejection = ex.StatusCode is
                StatusCodes.BadIdentityTokenInvalid or
                StatusCodes.BadIdentityTokenRejected or
                StatusCodes.BadUserAccessDenied or
                StatusCodes.BadUserSignatureInvalid or
                StatusCodes.BadIdentityChangeNotSupported;

            if (isIdentityRejection)
            {
                _audit.RecordRejection(RejectionCategory.Token,
                    StatusCodes.GetBrowseName(ex.StatusCode));
            }

            throw;
        }
    }
}