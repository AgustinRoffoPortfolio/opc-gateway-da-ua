using System.Net;
using System.Runtime.InteropServices;
using Gateway.Core;
using Gateway.Ua;
using Microsoft.Extensions.Configuration;
using Opc.Ua;
using Opc.Ua.Configuration;
using Serilog;

// El driver OPC DA exige un proceso de 32 bits: esto tiene que fallar
// ruidosamente si algun dia alguien saca el PlatformTarget del csproj.
Console.WriteLine($"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Is64BitProcess: {Environment.Is64BitProcess}");

// Lee appsettings.json desde la carpeta de salida y lo mapea a UaOptions.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var options = configuration.GetSection("Ua").Get<UaOptions>()
    ?? throw new InvalidOperationException("Falta la seccion 'Ua' en appsettings.json");

// Logger de toda la aplicacion.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    // El stack es muy verboso, y firma sus mensajes con el nombre en runtime
    // de la clase que lo hospeda.
    .MinimumLevel.Override("Opc.Ua", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Gateway.Ua.UaServer", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console()
    .CreateLogger();

// El stack OPC UA no tiene logger propio: usa el que le pasemos por aca.
var telemetry = DefaultTelemetry.Create(builder => builder.AddSerilog(Log.Logger));

// Identidad de la aplicacion ante la red OPC UA.
var application = new ApplicationInstance(telemetry)
{
    ApplicationName = options.ApplicationName,
    ApplicationType = ApplicationType.Server
};

// La PKI vive en la raiz del repo, resuelta por ruta absoluta desde la
// ubicacion del ejecutable (no desde el working directory): si "dotnet run"
// y correr el .exe desde bin/ resolvieran carpetas distintas, el servidor
// regeneraria su certificado en cada modo y romperia la confianza ya
// establecida con los clientes.
var pkiRoot = Path.Combine(ConfigPathResolver.ResolveRepoRoot(), options.PkiRoot);

// La API nueva recibe una coleccion porque un servidor puede tener varios
// certificados (RSA, ECC) y ofrecer el que el cliente soporte. Nosotros usamos uno.
var applicationCertificate = new CertificateIdentifier
{
    CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType,
    StoreType = CertificateStoreType.Directory,
    StorePath = Path.Combine(pkiRoot, "own"),
    SubjectName = $"CN={options.ApplicationName}, C=AR, O=Portfolio"
};

// Configuracion armada en codigo, sin archivo XML.
await application.Build(
        applicationUri: $"urn:{Dns.GetHostName()}:OpcGatewayDaUa:Server",
        productUri: "https://github.com/AgustinRoffoPortfolio/opc-gateway-da-ua")
    .AsServer(new[] { options.EndpointUrl })
    .AddUnsecurePolicyNone()          // endpoint sin seguridad, para conectar mientras desarrollamos
    .AddSignAndEncryptPolicies()      // endpoints firmados y cifrados, ya expuestos para la fase de seguridad
    .AddUserTokenPolicy(UserTokenType.Anonymous)
    .AddSecurityConfiguration(
        new CertificateIdentifierCollection { applicationCertificate },
        pkiRoot: pkiRoot)
    .SetAutoAcceptUntrustedCertificates(options.AutoAcceptUntrustedCertificates)
    .CreateAsync();

Log.Information("PKI en {PkiRoot} (auto-aceptar: {Auto})",
    pkiRoot, options.AutoAcceptUntrustedCertificates);

// Crea el certificado propio del servidor la primera vez que corre.
await application.CheckApplicationInstanceCertificatesAsync(silent: true);

// Arbol de tags: sale del CSV, no hardcodeado. Parser deliberadamente
// ingenuo, cualquier fila mal formada tira excepcion y el gateway no arranca.
var tagDefinitions = CsvTagLoader.Load(options.TagsCsvPath);

var server = new UaServer(options, tagDefinitions);
await application.StartAsync(server);

Log.Information("Address space listo: {Tags} tags", server.NodeManager?.TagCount ?? 0);

// Cada ciclo: publicar los valores actuales a los nodos suscriptos.
var interval = TimeSpan.FromMilliseconds(options.UpdateIntervalMs);
using var timer = new Timer(_ =>
{
    try
    {
        server.NodeManager?.UpdateValues();
    }
    catch (Exception ex)
    {
        // Una excepcion sin atrapar dentro de un callback de Timer
        // termina el proceso entero.
        Log.Error(ex, "Fallo el ciclo de actualizacion");
    }
}, null, TimeSpan.Zero, interval);

Log.Information("Servidor OPC UA escuchando en {Endpoint}", options.EndpointUrl);
Log.Information("Ciclo de actualizacion: {IntervalMs} ms", options.UpdateIntervalMs);

// Senaliza el apagado desde Ctrl+C o desde el cierre del proceso (por ejemplo,
// el SCM de un servicio de Windows), nunca desde una tecla en una consola que
// en produccion no va a existir. El shutdown ordenado tiene que correr siempre,
// no solo cuando hay alguien interactuando con la terminal.
var shutdownRequested = new TaskCompletionSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdownRequested.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdownRequested.TrySetResult();

await shutdownRequested.Task;

Log.Information("Deteniendo servidor...");
await application.StopAsync();
Log.Information("Servidor detenido.");
await Log.CloseAndFlushAsync();
