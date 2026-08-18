namespace Gateway.Ua;

/// Configuracion del servidor OPC UA, leida de appsettings.json.
public class UaOptions
{
    public string ApplicationName { get; set; } = "OpcGatewayDaUa";
    public string EndpointUrl { get; set; } = "opc.tcp://localhost:4840/GatewayDaUa";
    public string NamespaceUri { get; set; } = "http://opc-gateway-da-ua/";
    public int UpdateIntervalMs { get; set; } = 1000;
    public string TagsCsvPath { get; set; } = "config/tags.example.csv";

    public string PkiRoot { get; set; } = "pki";
    public bool AutoAcceptUntrustedCertificates { get; set; } = true;

    /// Nodos de diagnostico del stack UA (sesiones, suscripciones, contadores).
    /// Externalizado para poder medir su costo en memoria: tienen precio y hay
    /// que poder correr con y sin ellos en la misma sesion. Default true para
    /// no cambiar el comportamiento existente.
    public bool DiagnosticsEnabled { get; set; } = true;
}
