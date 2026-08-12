using Gateway.Core;

namespace Gateway.Tests;

public class TagCacheTests
{
    private static readonly DateTime T1 = new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 8, 12, 10, 5, 0, DateTimeKind.Utc);

    private static TagDefinition Def(
        TagDataType type = TagDataType.Double,
        double multiplier = 1.0,
        double offset = 0.0) =>
        new("PLANTA_01.MEDICION.PRESION_ENTRADA", "Random.Real8", type, multiplier, offset);

    private static TagCache CacheWith(TagDefinition definition) => new([definition]);

    private static Dictionary<string, TagSample> Sample(object? value, TagQuality quality, DateTime timestamp) =>
        new() { ["Random.Real8"] = new TagSample(value, quality, timestamp) };

    [Fact]
    public void TagSinLeer_QuedaEsperandoDatoInicial()
    {
        var cache = CacheWith(Def());

        var state = cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA");

        Assert.Equal(TagQuality.WaitingForInitialData, state.Quality);
        Assert.Null(state.ScaledValue);
    }

    [Fact]
    public void TagFueraDelCsv_SeDistingueDeUnoSinLeer()
    {
        var cache = CacheWith(Def());

        var state = cache.Get("PLANTA_01.NO.EXISTE");

        Assert.Equal(TagQuality.UnknownTag, state.Quality);
    }

    [Fact]
    public void CalidadBuena_AplicaMultiplicadorYOffset()
    {
        var cache = CacheWith(Def(multiplier: 2.0, offset: 10.0));

        cache.Update(Sample(5.0, TagQuality.Good, T1));

        var state = cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA");
        Assert.Equal(20.0, state.ScaledValue);
        Assert.Equal(T1, state.SourceTimestamp);
    }

    [Fact]
    public void CalidadMala_ConservaValorYTimestampAnteriores()
    {
        var cache = CacheWith(Def(multiplier: 2.0));
        cache.Update(Sample(5.0, TagQuality.Good, T1));

        // Llega una lectura mala con un valor distinto: no debe pisar nada.
        var bad = new TagQuality(QualityMaster.Bad, QualitySubstatus.BadOutOfService, QualityLimit.NotLimited);
        cache.Update(Sample(999.0, bad, T2));

        var state = cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA");
        Assert.Equal(10.0, state.ScaledValue);      // el valor viejo, escalado
        Assert.Equal(T1, state.SourceTimestamp);    // el timestamp viejo
        Assert.Equal(bad, state.Quality);           // pero la calidad nueva
    }

    [Fact]
    public void CalidadUncertain_SeEscalaIgual()
    {
        var cache = CacheWith(Def(multiplier: 2.0));
        var uncertain = new TagQuality(
            QualityMaster.Uncertain, QualitySubstatus.UncertainLastUsableValue, QualityLimit.NotLimited);

        cache.Update(Sample(5.0, uncertain, T1));

        Assert.Equal(10.0, cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").ScaledValue);
    }

    [Fact]
    public void ValorQueNoConvierte_MarcaErrorDeConversion()
    {
        var cache = CacheWith(Def(TagDataType.Boolean));

        cache.Update(Sample("no soy un booleano", TagQuality.Good, T1));

        Assert.Equal(TagQuality.ConversionError, cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").Quality);
    }

    [Fact]
    public void ValorNumericoComoTexto_ParseaConCulturaInvariante()
    {
        var cache = CacheWith(Def(multiplier: 1.0));

        cache.Update(Sample("8009.57", TagQuality.Good, T1));

        Assert.Equal(8009.57, (double)cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").ScaledValue!, 2);
    }

    [Fact]
    public void Int32_RedondeaEnVezDeTruncar()
    {
        var cache = CacheWith(Def(TagDataType.Int32, multiplier: 1.0, offset: 0.6));

        cache.Update(Sample(10.0, TagQuality.Good, T1));

        Assert.Equal(11, cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").ScaledValue);
    }

    [Fact]
    public void MuestraDeUnTagQueNoPedimos_SeIgnora()
    {
        var cache = CacheWith(Def());

        cache.Update(new Dictionary<string, TagSample>
        {
            ["Random.Int4"] = new TagSample(1.0, TagQuality.Good, T1)
        });

        Assert.Equal(1, cache.Count);
    }
}