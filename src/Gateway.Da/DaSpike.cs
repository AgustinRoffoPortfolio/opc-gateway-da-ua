using System.Runtime.InteropServices;
using TitaniumAS.Opc.Client;
using TitaniumAS.Opc.Client.Common;
using TitaniumAS.Opc.Client.Da;

namespace Gateway.Da;

/// <summary>
/// Spike descartable de Fase 2. Verifica que podemos hablar COM con el servidor
/// OPC DA antes de escribir el driver de verdad. No forma parte del gateway.
/// </summary>
public static class DaSpike
{
    public static void Run(string progId)
    {
        // El apartment queda congelado con la primera llamada COM del proceso.
        // Titanium exige MTA porque llama a CoInitializeSecurity al inicializar.
        var apartment = Thread.CurrentThread.GetApartmentState();
        Console.WriteLine($"ApartmentState: {apartment}");
        if (apartment != ApartmentState.MTA)
            throw new InvalidOperationException(
                "El proceso tiene que correr en MTA. Titanium falla con un error COM ilegible si no.");

        // Configuracion global de COM para el proceso. Va antes que cualquier
        // otra llamada COM: si llega tarde, Windows contesta RPC_E_TOO_LATE.
        Bootstrap.Initialize();
        Console.WriteLine("Bootstrap.Initialize() OK");

        // ProgID -> URL opcda://localhost/... resuelto por COM, no leyendo el registro.
        var url = UrlBuilder.Build(progId);
        Console.WriteLine($"URL: {url}");

        // El using garantiza la liberacion de las referencias COM. Sin esto el
        // proceso del servidor DA puede quedar colgado despues de que salgamos.
        using var server = new OpcDaServer(url);

        server.Connect();
        Console.WriteLine($"IsConnected: {server.IsConnected}");

        var status = server.GetStatus();
        Console.WriteLine($"ServerState: {status.ServerState}");
        Console.WriteLine($"VendorInfo:  {status.VendorInfo}");
        Console.WriteLine($"CurrentTime: {status.CurrentTime:O}");

        // Un Group junta items que se leen juntos. Analogo a agrupar registros
        // contiguos en Modbus, pero el criterio aca es la frecuencia de scan.
        var group = server.AddGroup("SpikeGroup");
        group.IsActive = true;

        // Tags incorporados del simulador Matrikon, uno por cada tipo del CSV.
        var definitions = new[]
        {
            new OpcDaItemDefinition { ItemId = "Random.Real8",  IsActive = true },
            new OpcDaItemDefinition { ItemId = "Random.Int4",   IsActive = true },
            new OpcDaItemDefinition { ItemId = "Random.Boolean",IsActive = true },
            new OpcDaItemDefinition { ItemId = "Random.String", IsActive = true }
        };

        var addResults = group.AddItems(definitions);
        foreach (var r in addResults)
            Console.WriteLine($"AddItem: {r.Error} (Failed: {r.Error.Failed})");
        Console.WriteLine();

        // Device: fuerza lectura al dispositivo. Cache: devuelve lo que el server
        // ya tiene. Para el spike queremos el dato fresco.
        var values = group.Read(group.Items, OpcDaDataSource.Device);

        foreach (var v in values)
        {
            Console.WriteLine($"ItemId:    {v.Item?.ItemId}");
            Console.WriteLine($"  Value:     {v.Value} ({v.Value?.GetType().Name})");
            Console.WriteLine($"  Quality:   {v.Quality}");
            Console.WriteLine($"  Timestamp: {v.Timestamp:O}");
        }
    }
}