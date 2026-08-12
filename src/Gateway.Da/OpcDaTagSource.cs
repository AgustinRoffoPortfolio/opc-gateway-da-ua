using TitaniumAS.Opc.Client;
using TitaniumAS.Opc.Client.Common;
using TitaniumAS.Opc.Client.Da;
using Gateway.Core;

namespace Gateway.Da;

/// <summary>
/// Driver de lectura contra un servidor OPC DA. Traduce el vocabulario del SDK
/// al del gateway: ningun tipo de Titanium cruza este borde.
/// </summary>
/// <remarks>
/// No tiene reloj propio a proposito. Expone ReadAll() y el ritmo lo decide
/// quien lo hospeda; el desacople entre lectura DA y publicacion UA es trabajo
/// de la cache, y dos relojes serian dos duenos del mismo problema.
/// </remarks>
public sealed class OpcDaTagSource : IDisposable
{
    private readonly string _progId;
    private OpcDaServer? _server;
    private OpcDaGroup? _group;
    private bool _disposed;

    public OpcDaTagSource(string progId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(progId);
        _progId = progId;
    }

    /// <summary>El servidor DA respondio la ultima vez que se lo consulto.</summary>
    public bool IsConnected => _server?.IsConnected ?? false;

    /// <summary>
    /// Conecta al servidor DA y crea el grupo de lectura.
    /// </summary>
    /// <remarks>
    /// El apartment COM del hilo queda congelado con la primera llamada COM del
    /// proceso, asi que esto no lo puede arreglar el driver: solo verificarlo y
    /// fallar con un mensaje legible. La responsabilidad es del host.
    /// </remarks>
    public void Connect(string groupName = "GatewayGroup", int updateRateMs = 1000)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var apartment = Thread.CurrentThread.GetApartmentState();
        if (apartment != ApartmentState.MTA)
            throw new InvalidOperationException(
                $"El hilo esta en {apartment} y COM exige MTA. Titanium falla con un error COM ilegible si no.");

        // Configuracion global de COM del proceso. Va antes que cualquier otra
        // llamada COM: si llega tarde, Windows contesta RPC_E_TOO_LATE.
        Bootstrap.Initialize();

        // ProgID -> URL opcda://localhost/... resuelto por COM, no leyendo el registro.
        var url = UrlBuilder.Build(_progId);

        _server = new OpcDaServer(url);
        _server.Connect();

        _group = _server.AddGroup(groupName);
        _group.IsActive = true;

        // Leyendo con Cache, la frescura del dato la decide cada cuanto el
        // servidor refresca su propia cache, no cada cuanto llamamos a ReadAll().
        // Sin esto el ritmo real de adquisicion queda en el default del servidor.
        _group.UpdateRate = TimeSpan.FromMilliseconds(updateRateMs);
    }

    /// <summary>
    /// Da de alta los items a leer. Devuelve los ItemIDs que el servidor rechazo,
    /// para que el llamador los marque fuera de servicio en vez de perderlos.
    /// </summary>
    public IReadOnlyList<string> AddItems(IEnumerable<string> itemIds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_group is null)
            throw new InvalidOperationException("Hay que llamar a Connect() antes de agregar items.");

        var ids = itemIds.ToArray();
        if (ids.Length == 0) return [];

        var definitions = ids
            .Select(id => new OpcDaItemDefinition { ItemId = id, IsActive = true })
            .ToArray();

        var results = _group.AddItems(definitions);

        // El alta es por item: que uno falle no invalida los demas. Un ItemID mal
        // escrito en el CSV no puede dejar sin datos al resto de la planta.
        var rejected = new List<string>();
        for (var i = 0; i < results.Length; i++)
            if (results[i].Error.Failed)
                rejected.Add(ids[i]);

        return rejected;
    }

/// <summary>
    /// Lee todos los items dados de alta y devuelve una muestra por cada uno,
    /// indexada por ItemID.
    /// </summary>
    /// <remarks>
    /// Se lee con Device y no con Cache. Cache parecia mejor en teoria (deberia
    /// dar el timestamp de cuando el servidor refresco cada item), pero medido
    /// contra Matrikon devuelve el valor actual acompanado del timestamp del
    /// refresco anterior: el valor y su hora quedan desincronizados, con hasta
    /// varios minutos de diferencia. Device fuerza la lectura al dispositivo y
    /// estampa el instante real, aunque toda la tanda comparta ese instante.
    /// Un timestamp por item pero falso es peor que uno compartido y verdadero.
    /// </remarks>
    public IReadOnlyDictionary<string, TagSample> ReadAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_group is null)
            throw new InvalidOperationException("Hay que llamar a Connect() antes de leer.");

        var samples = new Dictionary<string, TagSample>();
        if (_group.Items.Count == 0) return samples;

        var values = _group.Read(_group.Items, OpcDaDataSource.Device);

        foreach (var value in values)
        {
            var itemId = value.Item?.ItemId;
            if (itemId is null) continue;

            // Error a nivel item: el servidor no pudo darnos ni siquiera una
            // calidad. No se inventa una: se marca sin dato y el log dira por que.
            if (value.Error.Failed)
            {
                samples[itemId] = TagSample.NoData(TagQuality.WaitingForInitialData);
                continue;
            }

            // UtcDateTime y no ToUniversalTime(): el SDK devuelve DateTimeOffset
            // con offset local (-03:00). Sin normalizar aca, dos veces por ano
            // aparecen saltos de una hora imposibles de rastrear despues.
            samples[itemId] = new TagSample(
                value.Value,
                Translate(value.Quality),
                value.Timestamp.UtcDateTime);
        }

        return samples;
    }

    /// <summary>
    /// Traduce la calidad del SDK a la del gateway.
    /// </summary>
    /// <remarks>
    /// Va con switch explicito y no con cast numerico aunque hoy los valores
    /// coincidan: un cast sigue compilando si el SDK renumera un enum en una
    /// version futura, y falla en silencio sobre el dato. Esto falla ruidoso.
    /// </remarks>
    private static TagQuality Translate(OpcDaQuality quality)
    {
        var master = quality.Master switch
        {
            OpcDaQualityMaster.Bad => QualityMaster.Bad,
            OpcDaQualityMaster.Uncertain => QualityMaster.Uncertain,
            OpcDaQualityMaster.Good => QualityMaster.Good,
            OpcDaQualityMaster.Error => QualityMaster.Error,
            _ => QualityMaster.Bad
        };

        var substatus = quality.Status switch
        {
            OpcDaQualityStatus.Bad => QualitySubstatus.Bad,
            OpcDaQualityStatus.BadConfigurationError => QualitySubstatus.BadConfigurationError,
            OpcDaQualityStatus.BadNotConnected => QualitySubstatus.BadNotConnected,
            OpcDaQualityStatus.BadDeviceFailure => QualitySubstatus.BadDeviceFailure,
            OpcDaQualityStatus.BadSensorFailure => QualitySubstatus.BadSensorFailure,
            OpcDaQualityStatus.BadLastKnown => QualitySubstatus.BadLastKnown,
            OpcDaQualityStatus.BadCommFailure => QualitySubstatus.BadCommFailure,
            OpcDaQualityStatus.BadOutOfService => QualitySubstatus.BadOutOfService,
            OpcDaQualityStatus.BadWaitingForInitialData => QualitySubstatus.BadWaitingForInitialData,
            OpcDaQualityStatus.Uncertain => QualitySubstatus.Uncertain,
            OpcDaQualityStatus.UncertainLastUsableValue => QualitySubstatus.UncertainLastUsableValue,
            OpcDaQualityStatus.UncertainSensorNotAccurate => QualitySubstatus.UncertainSensorNotAccurate,
            OpcDaQualityStatus.UncertainEngineeringUnitsExceeded => QualitySubstatus.UncertainEngineeringUnitsExceeded,
            OpcDaQualityStatus.UncertainSubNormal => QualitySubstatus.UncertainSubNormal,
            OpcDaQualityStatus.Good => QualitySubstatus.Good,
            OpcDaQualityStatus.GoodLocalOverride => QualitySubstatus.GoodLocalOverride,
            _ => QualitySubstatus.Bad
        };

        var limit = quality.Limit switch
        {
            OpcDaQualityLimit.NotLimited => QualityLimit.NotLimited,
            OpcDaQualityLimit.LowLimited => QualityLimit.Low,
            OpcDaQualityLimit.HighLimited => QualityLimit.High,
            OpcDaQualityLimit.Constant => QualityLimit.Constant,
            _ => QualityLimit.NotLimited
        };

        return new TagQuality(master, substatus, limit);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // OpcDaServer libera las referencias COM del grupo y los items. Sin esto
        // el proceso del servidor DA puede quedar vivo despues de que salgamos.
        _server?.Dispose();
        _server = null;
        _group = null;
    }
}