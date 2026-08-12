using System.Collections.Concurrent;

namespace Gateway.Core;

/// <summary>
/// Estado actual de un tag: lo que la cache responde cuando el lado UA pregunta.
/// </summary>
/// <param name="ScaledValue">
/// Valor ya transformado y convertido al DataType del CSV. Puede ser un valor
/// viejo: si la ultima lectura fue mala se conserva el anterior, con el
/// StatusCode malo y el timestamp original. Un dato congelado que se sabe
/// congelado es mas util que un vacio.
/// </param>
/// <param name="SourceTimestamp">
/// Momento de origen del valor que se esta devolviendo, no de la ultima lectura.
/// Si el valor es viejo, este timestamp tambien lo es: es lo que deja ver que
/// el tag esta congelado.
/// </param>
public readonly record struct TagState(
    object? ScaledValue,
    TagQuality Quality,
    DateTime SourceTimestamp);

/// <summary>
/// Frontera entre el mundo DA y el mundo UA. El driver empuja muestras a su
/// ritmo, el node manager pide estado al suyo, y ninguno de los dos conoce la
/// frecuencia del otro.
/// </summary>
/// <remarks>
/// Es la razon por la que diez clientes UA preguntando lo mismo no se traducen
/// en diez lecturas al servidor legado.
/// </remarks>
public sealed class TagCache
{
    // Indexado por nombre DA porque es la clave con la que llegan las muestras.
    // Es una lista y no una definicion sola: un mismo item DA puede alimentar
    // varios nodos UA con transformaciones distintas (el mismo caudal en m3/h
    // y en l/s, por ejemplo). La relacion es uno a muchos.
    private readonly Dictionary<string, List<TagDefinition>> _definitionsByDaName;

    // ConcurrentDictionary porque el hilo que lee DA y el que publica UA son
    // distintos: uno escribe mientras el otro lee, sin lock explicito.
    private readonly ConcurrentDictionary<string, TagState> _stateByUaName = new();

    public TagCache(IEnumerable<TagDefinition> definitions)
    {
        _definitionsByDaName = definitions
            .GroupBy(d => d.OpcDaName)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Todo tag arranca declarado pero sin dato. Sin esto, un cliente UA que
        // conecta antes de la primera lectura recibiria "tag desconocido", que
        // es una causa distinta y mandaria a buscar el problema al lugar equivocado.
        foreach (var definition in _definitionsByDaName.Values.SelectMany(list => list))
            _stateByUaName[definition.OpcUaName] =
                new TagState(null, TagQuality.WaitingForInitialData, DateTime.UtcNow);
    }

    public int Count => _stateByUaName.Count;

    /// <summary>Nombres UA de todos los tags configurados.</summary>
    public IEnumerable<string> UaNames => _stateByUaName.Keys;

    /// <summary>ItemIDs a pedirle al servidor DA.</summary>
    public IEnumerable<string> DaNames => _definitionsByDaName.Keys;

    /// <summary>
    /// Estado actual de un tag. Un tag que no esta en el CSV no es un error de
    /// lectura sino de configuracion, y se distingue como tal.
    /// </summary>
    public TagState Get(string uaName) =>
        _stateByUaName.TryGetValue(uaName, out var state)
            ? state
            : new TagState(null, TagQuality.UnknownTag, DateTime.UtcNow);

    /// <summary>
    /// Incorpora una tanda de muestras del driver DA.
    /// </summary>
    public void Update(IReadOnlyDictionary<string, TagSample> samples)
    {
        foreach (var (daName, sample) in samples)
        {
            if (!_definitionsByDaName.TryGetValue(daName, out var definitions))
                continue;   // el servidor DA mando algo que no pedimos

            // Una muestra puede alimentar varios nodos UA, cada uno con su
            // propia transformacion.
            foreach (var definition in definitions)
                _stateByUaName[definition.OpcUaName] =
                    Apply(definition, sample, _stateByUaName.GetValueOrDefault(definition.OpcUaName));
        }
    }

    /// <summary>
    /// Calcula el nuevo estado de un tag a partir de una muestra y del estado previo.
    /// </summary>
    private static TagState Apply(TagDefinition definition, TagSample sample, TagState previous)
    {
        // Calidad no utilizable: se conserva el valor anterior con su timestamp
        // original, y se pega la calidad nueva. Escalar sobre una lectura mala
        // produce un numero con apariencia de valido, que es peor que no publicar.
        if (!sample.Quality.IsUsable)
            return new TagState(previous.ScaledValue, sample.Quality, previous.SourceTimestamp);

        if (!TryScale(definition, sample.Value, out var scaled))
            return new TagState(previous.ScaledValue, TagQuality.ConversionError, previous.SourceTimestamp);

        return new TagState(scaled, sample.Quality, sample.SourceTimestamp);
    }

    /// <summary>
    /// Aplica multiplicador y offset y convierte al tipo declarado en el CSV.
    /// </summary>
    /// <remarks>
    /// La transformacion es numerica, asi que Boolean y String pasan derecho:
    /// multiplicar un texto no significa nada. Si el CSV declara un tipo que el
    /// valor DA no puede tomar, es error de configuracion y se marca como tal.
    /// </remarks>
    private static bool TryScale(TagDefinition definition, object? raw, out object? scaled)
    {
        scaled = null;
        if (raw is null) return false;

        switch (definition.DataType)
        {
            case TagDataType.String:
                scaled = raw.ToString();
                return true;

            case TagDataType.Boolean:
                if (raw is bool b) { scaled = b; return true; }
                return false;

            case TagDataType.Double:
            case TagDataType.Int32:
                // InvariantCulture: el valor puede llegar como texto con punto
                // decimal, y la maquina esta en es-AR (coma). Sin esto, "8009.57"
                // no parsea o parsea mal.
                if (!TryToDouble(raw, out var numeric)) return false;

                var value = numeric * definition.Multiplier + definition.Offset;

                if (definition.DataType == TagDataType.Double)
                {
                    scaled = value;
                    return true;
                }

                // Fuera de rango no es un redondeo: es un tipo mal declarado.
                if (value is < int.MinValue or > int.MaxValue) return false;
                scaled = (int)Math.Round(value);
                return true;

            default:
                return false;
        }
    }

    private static bool TryToDouble(object raw, out double value)
    {
        switch (raw)
        {
            case double d: value = d; return true;
            case float f: value = f; return true;
            case int i: value = i; return true;
            case short s: value = s; return true;
            case long l: value = l; return true;
            case string text:
                return double.TryParse(
                    text,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value);
            default:
                value = 0;
                return false;
        }
    }
}