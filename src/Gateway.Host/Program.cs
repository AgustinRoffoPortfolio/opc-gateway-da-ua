using System.Net;
using System.Runtime.InteropServices;
using Gateway.Core;
using Gateway.Ua;
using Microsoft.Extensions.Configuration;
using Opc.Ua;
using Opc.Ua.Configuration;
using Serilog;
using Gateway.Da;
using Gateway.Host;
using Gateway.Web;

// El driver OPC DA exige un proceso de 32 bits: esto tiene que fallar
// ruidosamente si algun dia alguien saca el PlatformTarget del csproj.
Console.WriteLine($"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Is64BitProcess: {Environment.Is64BitProcess}");

// Prueba manual del driver DA aislado: corta antes de levantar el servidor UA.
if (args.Contains("--da-only"))
{
    using var daSource = new OpcDaTagSource("Matrikon.OPC.Simulation.1");
    daSource.Connect();
    Console.WriteLine($"IsConnected: {daSource.IsConnected}");

    var rejected = daSource.AddItems(
        ["Random.Real8", "Random.Int4", "Random.Boolean", "Random.String", "Tag.Que.No.Existe"]);
    Console.WriteLine($"Rechazados: {string.Join(", ", rejected)}");

    // Dos lecturas: la primera cae antes del primer refresco de la cache del
    // servidor, la segunda ya trae datos buenos.
    for (var pass = 1; pass <= 2; pass++)
    {
        Console.WriteLine($"--- Lectura {pass} ---");
        foreach (var (itemId, sample) in daSource.ReadAll())
            Console.WriteLine(
                $"{itemId,-16} {sample.Value,-24} {sample.Quality.Master}/{sample.Quality.Substatus}" +
                $" usable={sample.Quality.IsUsable} src={sample.SourceTimestamp:O}");

        if (pass == 1) Thread.Sleep(2000);
    }

    return;
}

// Lee appsettings.json desde la carpeta de salida y lo mapea a UaOptions.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var options = configuration.GetSection("Ua").Get<UaOptions>()
    ?? throw new InvalidOperationException("Falta la seccion 'Ua' en appsettings.json");

var daOptions = configuration.GetSection("Da").Get<DaOptions>()
    ?? throw new InvalidOperationException("Falta la seccion 'Da' en appsettings.json");

// La web es opcional: si falta la seccion se usan los defaults del record en
// vez de tirar el arranque abajo, porque un gateway sin pagina de diagnostico
// sigue siendo un gateway.
var webOptions = configuration.GetSection("Web").Get<WebOptions>() ?? new WebOptions();

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

// Habilita los nodos de diagnostico del server (ServerDiagnostics). Vienen
// apagados por default en el stack: el address space los expone igual, pero
// no se llenan y EnabledFlag no se deja escribir en runtime. Los necesitamos
// para ver sesiones, suscripciones y contadores del servidor desde un cliente UA.
application.ApplicationConfiguration.ServerConfiguration.DiagnosticsEnabled = true;
Log.Information("Diagnosticos del servidor UA habilitados");

// Crea el certificado propio del servidor la primera vez que corre.
await application.CheckApplicationInstanceCertificatesAsync(silent: true);

// Arbol de tags: sale del CSV, no hardcodeado. Carga parcial (Fase 3): una
// fila invalida no tira el gateway abajo, queda fuera de servicio y se
// reporta en el log.
var tagLoadResult = TagValidator.LoadAndValidate(options.TagsCsvPath);
foreach (var error in tagLoadResult.Errors)
    Log.Warning("Tag invalido, queda fuera de servicio: {Error}", error.Message);

Log.Information("Tags cargados: {Validos} validos, {Invalidos} con error",
    tagLoadResult.Tags.Count, tagLoadResult.Errors.Count);

var tagDefinitions = tagLoadResult.Tags;

// Frontera entre los dos mundos: el driver DA la llena, el node manager la lee.
// La ventana de antiguedad se traduce aca de ciclos a tiempo: el criterio se
// configura en ciclos porque lo que importa es cuantas lecturas nos perdimos,
// pero Gateway.Core no conoce DaOptions y solo recibe una duracion.
var staleAfter = TimeSpan.FromMilliseconds(
    (long)daOptions.UpdateRateMs * daOptions.StaleAfterCycles);

var cache = new TagCache(tagDefinitions, staleAfter);

Log.Information("Degradacion por antiguedad: {Cycles} ciclos ({Ms} ms sin refresco)",
    daOptions.StaleAfterCycles, staleAfter.TotalMilliseconds);

var server = new UaServer(options.NamespaceUri, tagDefinitions, cache);
await application.StartAsync(server);

// El ciclo DA corre en su propio hilo y no en el timer de publicacion: COM
// exige MTA, y una lectura DA lenta no tiene por que frenar la publicacion UA.
// El apartment se fija aca de forma explicita en vez de heredarlo del hilo que
// nos toque, que es como venia funcionando de rebote.
var daShutdown = new CancellationTokenSource();
var acquisition = new DaAcquisitionService(cache, daOptions);
var daThread = new Thread(() => acquisition.Run(daShutdown.Token))
{
    IsBackground = true,
    Name = "OPC DA polling"
};
daThread.SetApartmentState(ApartmentState.MTA);
daThread.Start();

Log.Information("Address space listo: {Tags} tags", server.NodeManager?.TagCount ?? 0);

// Cada ciclo: publicar los valores actuales a los nodos suscriptos.
var interval = TimeSpan.FromMilliseconds(options.UpdateIntervalMs);
// Instante de arranque para el uptime del diagnostico. Se toma aca, con todo
// ya levantado: es el momento en que el gateway empieza a prestar servicio.
var startedUtc = DateTime.UtcNow;

// Ultima foto publicada, para que la capa web la sirva sin volver a armarla.
var snapshots = new SnapshotHolder();

using var timer = new Timer(_ =>
{
    try
    {
        server.NodeManager?.UpdateValues();

        // El snapshot se arma aca y no adentro del node manager porque es el
        // unico punto que ve las dos mitades: el estado del vinculo DA lo tiene
        // el servicio de adquisicion, y Gateway.Ua no puede depender del host.
        if (server.NodeManager is { } nodeManager)
        {
            var snapshot = GatewaySnapshot.Build(
                cache,
                acquisition.GetStatus(),
                nodeManager.GetServerStatus(),
                startedUtc);

            // Un unico Build por ciclo alimenta las dos vistas: los nodos UA y
            // la pagina sirven el mismo objeto, no dos fotos parecidas.
            nodeManager.PublishDiagnostics(snapshot);
            snapshots.Publish(snapshot);
        }
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

// El diagnostico web es accesorio: si no puede levantar (puerto ocupado, por
// ejemplo) se avisa y se sigue. Tumbar el gateway entero por la pagina que
// mira como esta el gateway seria exactamente al reves de lo que se quiere.
DiagnosticsServer? diagnosticsServer = null;

if (webOptions.Enabled)
{
    try
    {
        diagnosticsServer = new DiagnosticsServer(
            webOptions, snapshots, cache,
            new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger));

        await diagnosticsServer.StartAsync();
        Log.Information("Diagnostico web en {Url}", webOptions.ListenUrl);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "No se pudo levantar el diagnostico web; el gateway sigue sin el");
        diagnosticsServer = null;
    }
}
else
{
    Log.Information("Diagnostico web deshabilitado por configuracion");
}

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

// Primero la web: deja de aceptar requests antes de que empiecen a
// desaparecer las piezas que consulta.
if (diagnosticsServer is not null)
    await diagnosticsServer.DisposeAsync();

await daShutdown.CancelAsync();
daThread.Join(TimeSpan.FromSeconds(5));
await application.StopAsync();
Log.Information("Servidor detenido.");
await Log.CloseAndFlushAsync();