using Gateway.Da;

namespace Gateway.Tests;

/// <summary>
/// Verifica la correccion del bug de FILETIME del SDK de Titanium.
/// Ver SdkTimestamp y el commit upstream 19ab01b para el detalle del bug.
/// </summary>
/// <remarks>
/// Es aritmetica pura: no toca COM, ni el SDK, ni un servidor DA. El bug en si
/// esta caracterizado aparte en SdkFileTimeConverterTests.
/// </remarks>
public class SdkTimestampTests
{
    private const long TwoToThe32 = 0x100000000L;

    /// <summary>Devuelve un instante cuyo campo bajo del FILETIME tiene el bit 31 en el estado pedido.</summary>
    private static DateTimeOffset WithSignBit(DateTimeOffset seed, bool on)
    {
        var ticks = seed.ToFileTime();
        // El bit 31 alterna cada ~214,75 s: moviendose de a 1 s se encuentra cerca.
        for (var i = 0; i < 512; i++)
        {
            if (((ticks & 0x80000000L) != 0) == on)
                return DateTimeOffset.FromFileTime(ticks);
            ticks += TimeSpan.TicksPerSecond;
        }

        throw new InvalidOperationException("No se encontro un instante con el bit pedido.");
    }

    [Fact]
    public void Correct_ConBit31Prendido_RecuperaElValorReal()
    {
        var real = WithSignBit(DateTimeOffset.UtcNow, on: true);

        // Lo que devolveria el SDK sobre ese instante: 2^32 ticks abajo.
        var corrupto = DateTimeOffset.FromFileTime(real.ToFileTime() - TwoToThe32);

        Assert.Equal(real.UtcDateTime, SdkTimestamp.Correct(corrupto).UtcDateTime);
    }

    [Fact]
    public void Correct_ConBit31Apagado_NoTocaElValor()
    {
        var sano = WithSignBit(DateTimeOffset.UtcNow, on: false);

        Assert.Equal(sano.UtcDateTime, SdkTimestamp.Correct(sano).UtcDateTime);
    }

    [Fact]
    public void Correct_ConDatoLegitimamenteViejo_NoInventaHoras()
    {
        // Un dato de hace horas es viejo de verdad, no corrupto. La correccion
        // mira el bit 31, no la antiguedad, asi que no debe tocarlo.
        var viejo = WithSignBit(DateTimeOffset.UtcNow.AddHours(-6), on: false);

        Assert.Equal(viejo.UtcDateTime, SdkTimestamp.Correct(viejo).UtcDateTime);
    }
}