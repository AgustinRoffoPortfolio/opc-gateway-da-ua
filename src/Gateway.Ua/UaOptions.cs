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
}
