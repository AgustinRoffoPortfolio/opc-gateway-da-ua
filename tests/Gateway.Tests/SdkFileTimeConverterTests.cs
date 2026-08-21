using System.Reflection;
using System.Runtime.InteropServices.ComTypes;
using TitaniumAS.Opc.Client.Da;

namespace Gateway.Tests;

/// <summary>
/// Caracteriza el bug de conversion de FILETIME del SDK de Titanium.
/// </summary>
/// <remarks>
/// Un FILETIME son 64 bits de ticks de 100 ns partidos en dos campos de 32.
/// El SDK los recompone sumando el campo bajo, que en
/// System.Runtime.InteropServices.ComTypes.FILETIME esta declarado como int con
/// signo: cuando su bit 31 esta prendido, el valor sale 2^32 ticks abajo, o sea
/// 429,4967296 s (~7 min 10 s). Como ese bit alterna cada ~215 s, el error
/// aparece y desaparece solo, y de ahi el "a veces coincide, a veces no".
///
/// Upstream lo arreglo en el commit 19ab01b (~2021) enmascarando el campo bajo,
/// pero ese arreglo nunca se publico en NuGet: ninguno de los tres paquetes
/// disponibles lo incluye, y el nuestro (.NetCore 1.0.2.1, sep 2018) es anterior.
///
/// ESTOS TESTS AFIRMAN QUE EL BUG ESTA PRESENTE. Si algun dia fallan, no se
/// rompio nada: significa que el binario del SDK quedo arreglado y que el parche
/// de Gateway.Da ya no hace falta.
///
/// No tocan COM: FromFileTime es aritmetica pura, no hay servidor DA de por medio.
/// </remarks>
public class SdkFileTimeConverterTests
{
    /// <summary>Ticks de 100 ns que abarca un DWORD completo. Es el tamano exacto del error.</summary>
    private const long DwordTicks = 4294967296L; // 2^32 -> 429,4967296 s

    private static readonly MethodInfo FromFileTime = ResolveFromFileTime();

    // FileTimeConverter es internal, asi que se llega por reflection sobre el
    // assembly del SDK. OpcDaServer solo se usa para ubicarlo, no se instancia.
    private static MethodInfo ResolveFromFileTime()
    {
        var sdk = typeof(OpcDaServer).Assembly;

        var type = sdk.GetType("TitaniumAS.Opc.Client.Interop.Helpers.FileTimeConverter")
                   ?? Array.Find(sdk.GetTypes(), t => t.Name == "FileTimeConverter")
                   ?? throw new InvalidOperationException(
                       "No se encontro FileTimeConverter en el assembly del SDK.");

        return type.GetMethod("FromFileTime", BindingFlags.Public | BindingFlags.Static)
               ?? throw new InvalidOperationException(
                   "FileTimeConverter existe pero no expone FromFileTime estatico.");
    }

    private static DateTimeOffset Convert(long fileTime)
    {
        var ft = new FILETIME
        {
            dwLowDateTime = unchecked((int)(fileTime & 0xFFFFFFFF)),
            dwHighDateTime = unchecked((int)(fileTime >> 32))
        };

        return (DateTimeOffset)FromFileTime.Invoke(null, new object[] { ft })!;
    }

    /// <summary>
    /// Dos instantes separados por 100 ns, pero a distinto lado del bit 31.
    /// Un conversor sano los devuelve separados por 1 tick.
    /// </summary>
    [Fact]
    public void FromFileTime_CruzandoElBit31_AtrasaElTimestampUnDwordEntero()
    {
        var alto = DateTimeOffset.UtcNow.ToFileTime() & ~0xFFFFFFFFL;

        var sinSigno = alto | 0x7FFFFFFFL; // bit 31 apagado
        var conSigno = alto | 0x80000000L; // bit 31 prendido, 1 tick despues

        var esperadoDespues = Convert(sinSigno);
        var deberiaSerPosterior = Convert(conSigno);

        // Si el SDK estuviera sano esto seria -1 tick. Con el bug, el segundo
        // instante "retrocede" casi 2^32 ticks respecto del primero.
        var deriva = esperadoDespues - deberiaSerPosterior;

        Assert.Equal(DwordTicks - 1, deriva.Ticks);
    }

    /// <summary>
    /// El mismo FILETIME convertido por el framework y por el SDK. La diferencia
    /// es exactamente un DWORD de ticks, que es la firma del error de signo.
    /// </summary>
    [Fact]
    public void FromFileTime_DifiereDelFramework_ExactamenteEnUnDwordDeTicks()
    {
        var conSigno = (DateTimeOffset.UtcNow.ToFileTime() & ~0xFFFFFFFFL) | 0x80000000L;

        var delSdk = Convert(conSigno);
        var delFramework = DateTimeOffset.FromFileTime(conSigno);

        Assert.Equal(DwordTicks, (delFramework - delSdk).Ticks);
    }
}