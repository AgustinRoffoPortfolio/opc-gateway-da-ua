using System.Globalization;
using Gateway.Da;
using TitaniumAS.Opc.Client;
using TitaniumAS.Opc.Client.Common;
using TitaniumAS.Opc.Client.Da;

// Experimento de la Fase 6: mide el bug de FILETIME del SDK de Titanium sobre
// una corrida larga contra el simulador, y verifica que SdkTimestamp.Correct()
// lo elimina. Herramienta desechable, no forma parte del gateway.
//
// Registra por cada lectura el timestamp CRUDO del SDK y el CORREGIDO en la
// misma fila: una sola corrida da los dos escenarios sobre exactamente los
// mismos datos, sin tener que tocar el codigo entre pasadas.
//
// Habla con el SDK directo y no via OpcDaTagSource porque ReadAll() ya devuelve
// el timestamp corregido, y la correccion no es idempotente.

var minutes = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 60;
var outputPath = Path.Combine(
    AppContext.BaseDirectory,
    $"timestamp-probe-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

string[] itemIds =
[
    "Random.Real8", "Random.Real4", "Random.Int4", "Random.Int2", "Random.UInt4",
    "Bucket Brigade.Real8", "Bucket Brigade.Int4", "Bucket Brigade.Real4",
    "Saw-toothed Waves.Real8", "Triangle Waves.Real8"
];

Bootstrap.Initialize();

using var server = new OpcDaServer(UrlBuilder.Build("Matrikon.OPC.Simulation.1"));
server.Connect();

var group = server.AddGroup("ProbeGroup");
group.IsActive = true;
group.UpdateRate = TimeSpan.FromMilliseconds(1000);
group.AddItems([.. itemIds.Select(id => new OpcDaItemDefinition { ItemId = id, IsActive = true })]);

// Cultura invariante al escribir: con decimales en coma el CSV se rompe solo.
using var csv = new StreamWriter(outputPath);
csv.WriteLine("ReadUtc;ItemId;RawUtc;CorrectedUtc;DeltaSeconds;WasCorrected");

Console.WriteLine($"Corriendo {minutes} min sobre {itemIds.Length} tags -> {outputPath}");
Console.WriteLine("Ctrl+C para cortar antes.");

var deadline = DateTime.UtcNow.AddMinutes(minutes);
var reads = 0;
var corrected = 0;

while (DateTime.UtcNow < deadline)
{
    var readUtc = DateTime.UtcNow;

    foreach (var value in group.Read(group.Items, OpcDaDataSource.Cache))
    {
        if (value.Item?.ItemId is not { } itemId || value.Error.Failed) continue;

        var raw = value.Timestamp;
        var fixedUp = SdkTimestamp.Correct(raw);
        var delta = (fixedUp - raw).TotalSeconds;

        reads++;
        if (delta != 0) corrected++;

        csv.WriteLine(string.Join(';', [
            readUtc.ToString("O", CultureInfo.InvariantCulture),
            itemId,
            raw.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            fixedUp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            delta.ToString("F7", CultureInfo.InvariantCulture),
            delta != 0 ? "1" : "0"
        ]));
    }

    csv.Flush();
    Console.Write($"\rLecturas: {reads}  corregidas: {corrected}   ");
    Thread.Sleep(1000);
}

Console.WriteLine($"\nListo. {reads} lecturas, {corrected} corregidas -> {outputPath}");