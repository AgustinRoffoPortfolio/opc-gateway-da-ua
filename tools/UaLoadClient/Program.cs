using System.Globalization;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

// Cliente de carga: abre N sesiones UA contra el gateway, cada una suscripta a
// los mismos tags, y cuenta notificaciones. Sirve para medir si N clientes
// pidiendo lo mismo multiplican el trabajo contra el servidor DA.

var endpoint = args.Length > 0 ? args[0] : "opc.tcp://localhost:4840/GatewayDaUa";
var clientCount = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 4;
var csvPath = args.Length > 2 ? args[2] : @"C:\Users\agust\Portfolio\scratch\tags-500.csv";
var minutes = args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 5;
const string GatewayNamespace = "http://opc-gateway-da-ua/";

// Nombres de tag del CSV: primera columna, salteando comentarios y header.
var tagNames = File.ReadAllLines(csvPath)
    .Where(l => !l.StartsWith('#') && !l.StartsWith("TAG_NAME_OPC_UA") && l.Contains(';'))
    .Select(l => l.Split(';')[0].Trim())
    .Where(n => n.Length > 0)
    .ToList();
Console.WriteLine($"Tags leidos del CSV: {tagNames.Count}");

// Configuracion minima de cliente: sin PKI propia, acepta el cert del gateway.
var config = new ApplicationConfiguration
{
    ApplicationName = "UaLoadClient",
    ApplicationUri = "urn:localhost:UaLoadClient",
    ApplicationType = ApplicationType.Client,
    SecurityConfiguration = new SecurityConfiguration
    {
        // Almacenes propios de la herramienta, separados del pki/ del gateway:
        // el stack exige que esten declarados aunque no se use seguridad.
        ApplicationCertificate = new CertificateIdentifier
        {
            StoreType = CertificateStoreType.Directory,
            StorePath = "pki-client/own",
            SubjectName = "CN=UaLoadClient"
        },
        TrustedIssuerCertificates = new CertificateTrustList
        {
            StoreType = CertificateStoreType.Directory,
            StorePath = "pki-client/issuers"
        },
        TrustedPeerCertificates = new CertificateTrustList
        {
            StoreType = CertificateStoreType.Directory,
            StorePath = "pki-client/trusted"
        },
        RejectedCertificateStore = new CertificateStoreIdentifier
        {
            StoreType = CertificateStoreType.Directory,
            StorePath = "pki-client/rejected"
        },
        AutoAcceptUntrustedCertificates = true,
        RejectSHA1SignedCertificates = false,
        MinimumCertificateKeySize = 1024
    },
    TransportConfigurations = new TransportConfigurationCollection(),
    TransportQuotas = new TransportQuotas { OperationTimeout = 60000 },
    ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
    TraceConfiguration = new TraceConfiguration()
};
await config.Validate(ApplicationType.Client);
config.CertificateValidator.AutoAcceptUntrustedCertificates = true;

var selected = CoreClientUtils.SelectEndpoint(config, endpoint, useSecurity: false);
var endpointConfig = EndpointConfiguration.Create(config);
var configured = new ConfiguredEndpoint(null, selected, endpointConfig);

var counters = new long[clientCount];
var sessions = new List<ISession>();

for (int i = 0; i < clientCount; i++)
{
    int index = i;
    var session = await Session.Create(config, configured, false,
        $"UaLoadClient-{index + 1}", 60000, null, null);

    // El indice del namespace se resuelve por URI: hardcodear ns=2 se rompe
    // apenas cambie el orden de registro en el servidor.
    var nsIndex = (ushort)session.NamespaceUris.GetIndex(GatewayNamespace);
    if (nsIndex == ushort.MaxValue)
        throw new InvalidOperationException($"No se encontro el namespace {GatewayNamespace}");

    var subscription = new Subscription(session.DefaultSubscription)
    {
        PublishingInterval = 1000,
        PublishingEnabled = true
    };
    session.AddSubscription(subscription);
    subscription.Create();

    var items = tagNames.Select(name => new MonitoredItem(subscription.DefaultItem)
    {
        DisplayName = name,
        StartNodeId = new NodeId(name, nsIndex),
        AttributeId = Attributes.Value,
        SamplingInterval = 1000,
        QueueSize = 1,
        DiscardOldest = true
    }).ToList();

    foreach (var item in items)
        item.Notification += (_, _) => Interlocked.Increment(ref counters[index]);

    subscription.AddItems(items);
    subscription.ApplyChanges();

    sessions.Add(session);
    Console.WriteLine($"Cliente {index + 1}: conectado, ns={nsIndex}, {items.Count} items suscriptos");
}

Console.WriteLine($"\n{clientCount} clientes corriendo durante {minutes} min. Ctrl+C para cortar antes.\n");
await Task.Delay(TimeSpan.FromMinutes(minutes));

Console.WriteLine("\n--- Notificaciones recibidas por cliente ---");
for (int i = 0; i < clientCount; i++)
    Console.WriteLine($"Cliente {i + 1}: {counters[i]:N0}");
Console.WriteLine($"Total: {counters.Sum():N0}");

foreach (var s in sessions)
{
    await s.CloseAsync();
    s.Dispose();
}
Console.WriteLine("Sesiones cerradas.");