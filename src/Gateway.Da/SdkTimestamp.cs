namespace Gateway.Da;

/// <summary>
/// Revierte el error de conversion de FILETIME del SDK de Titanium sobre los
/// timestamps que devuelve. Todo camino de lectura del SDK pasa por aca.
/// </summary>
/// <remarks>
/// El bug: un FILETIME son 64 bits de ticks de 100 ns partidos en dos campos de
/// 32 que en .NET estan declarados como int CON SIGNO. El SDK los recompone con
/// "(high << 32) + low" sin enmascarar el campo bajo, asi que cuando el bit 31
/// de low esta prendido el int se extiende a negativo y el resultado sale
/// exactamente 2^32 ticks abajo: 429,5 s, o sea 7 min 9,5 s. El bit alterna cada
/// ~214,75 s, de ahi que el atraso aparezca y desaparezca solo.
///
/// Upstream lo arreglo en el commit 19ab01b (2021) agregando "& 0xFFFFFFFF", pero
/// ese arreglo nunca se publico en NuGet y ningun paquete disponible lo tiene.
/// Como no podemos tocar la linea, que esta compilada adentro del binario, la
/// deshacemos aca: ellos evitan la resta, nosotros la revertimos.
///
/// La correccion es exacta, no heuristica. Restar 2^32 solo toca los bits >= 32,
/// asi que los 32 bits bajos del valor corrupto son identicos a los del valor
/// real: el bit 31 prendido en la salida del SDK identifica de forma biunivoca
/// al valor corrupto. No hay umbrales ni depende de la antiguedad del dato.
///
/// SOLO vale sobre valores que salieron del SDK, y UNA sola vez. La correccion
/// no es idempotente: como restar 2^32 no toca los 32 bits bajos, el valor ya
/// corregido conserva el bit 31 prendido y una segunda pasada le sumaria otros
/// 7 minutos. Se llama en un unico punto, al construir el TagSample.
/// </remarks>
public static class SdkTimestamp
{
    private const long LowFieldSignBit = 0x80000000L;
    private const long TwoToThe32 = 0x100000000L;

    public static DateTimeOffset Correct(DateTimeOffset sdkTimestamp)
    {
        // Fuera del rango FILETIME (epoca 1601) no puede venir de FromFileTime,
        // y ToFileTime() tiraria excepcion. Un timestamp raro no puede voltear
        // la lectura de toda la tanda.
        if (sdkTimestamp.UtcDateTime.Year < 1601) return sdkTimestamp;

        var ticks = sdkTimestamp.ToFileTime();
        if ((ticks & LowFieldSignBit) == 0) return sdkTimestamp;

        return DateTimeOffset.FromFileTime(ticks + TwoToThe32);
    }
}