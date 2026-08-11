namespace Gateway.Core;

/// <summary>
/// Foto de un tag en un instante: que valia, con que calidad, y cuando se origino.
/// No es "el valor actual del tag" — eso lo responde la cache combinando muestras.
/// </summary>
/// <param name="Value">
/// Valor crudo. Va como object porque el CSV declara cuatro tipos distintos
/// (Double, Boolean, Int32, String) y no hay un tipo comun mejor.
/// </param>
/// <param name="Quality">Calidad traducida al vocabulario del gateway.</param>
/// <param name="SourceTimestamp">
/// Momento de origen del dato, SIEMPRE en UTC. La normalizacion a UTC se hace
/// en el borde de Gateway.Da; de aca para adentro se asume cumplida.
/// Este valor no se pisa nunca con la hora de lectura.
/// </param>
public readonly record struct TagSample(
    object? Value,
    TagQuality Quality,
    DateTime SourceTimestamp)
{
    /// <summary>
    /// Muestra sin valor, para los estados propios del gateway (tag desconocido,
    /// esperando primer dato, error de conversion). El timestamp es el momento en
    /// que el gateway detecto la situacion, que es lo unico honesto que puede poner.
    /// </summary>
    public static TagSample NoData(TagQuality quality) =>
        new(null, quality, DateTime.UtcNow);
}