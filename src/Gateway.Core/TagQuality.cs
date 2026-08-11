namespace Gateway.Core;

/// <summary>
/// Calidad de una muestra, en el vocabulario propio del gateway.
/// Es el tipo intermedio entre la calidad OPC DA y el StatusCode OPC UA:
/// Gateway.Da traduce DA -> TagQuality, Gateway.Ua traduce TagQuality -> StatusCode.
/// </summary>
public enum TagQuality
{
    // --- Estados que vienen del servidor OPC DA ---
    Good,
    Uncertain,
    Bad,
    NotConnected,
    OutOfService,
    LastUsableValue,

    // --- Estados propios del gateway, sin equivalente en OPC DA ---

    /// El gateway arranco pero todavia no llego la primera lectura de este tag.
    WaitingForInitialData,

    /// Se pidio un tag que la cache no conoce.
    UnknownTag,

    /// Llego un valor del DA pero no convierte al DataType declarado en el CSV.
    ConversionError
}