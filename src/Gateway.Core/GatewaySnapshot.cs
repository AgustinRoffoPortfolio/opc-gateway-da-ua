namespace Gateway.Core;

/// <summary>Estado del vinculo COM con el servidor DA, visto por el driver.</summary>
public enum LinkState
{
    Disconnected,
    Reconnecting,
    Connected,

    /// El vinculo dice estar vivo pero el ciclo de adquisicion no vuelve. Es el
    /// caso del servidor DA colgado sin morir: COM no falla, simplemente no
    /// contesta. Sin este estado la pagina mostraria "conectado" con el hilo
    /// bloqueado, que es la peor mentira posible en un diagnostico.
    Stalled
}

/// <summary>
/// Interpretacion de los contadores. Es una heuristica, no una medicion: por eso
/// se muestra siempre junto a los numeros que la generaron y nunca se publica
/// como nodo UA (el address space es un contrato, y una opinion no va ahi).
/// </summary>
public enum Diagnosis
{
    Healthy,
    DaLinkDown,
    DaServerStalled,

    /// Los tags mudos nunca entregaron un dato: esos ItemIDs no existieron nunca
    /// del otro lado. Apunta al CSV, no a la red.
    LikelyCsvMismatch,

    /// Los tags mudos si entregaron datos antes. El servidor sigue ahi pero
    /// perdio sus items: el caso del simulador relanzado sin su configuracion.
    DaServerRepopulatedEmpty,

    /// Hay tags mudos pero son una minoria del total. No es una causa global, y
    /// afirmar una mandaria a reiniciar un servidor que esta sano.
    PartialDegradation,

    /// Los dos buckets estan parejos: estan pasando dos cosas a la vez y el
    /// gateway no elige por el operador.
    Indeterminate
}

/// <summary>Lo que el driver DA reporta sobre si mismo. Lo llena Gateway.Da.</summary>
public sealed record DaLinkStatus(
    LinkState State,
    DateTime? LastSuccessfulCycleUtc,
    int ReconnectAttempts,
    string? LastError,
    long ReadCycles,
    long ReadFailures,
    long Connections,
    long Disconnections,
    double LastCycleMs,
    double AvgCycleMs,
    double MaxCycleMs,
    int ConfiguredIntervalMs);

/// <summary>Lo que el server UA reporta sobre sus clientes. Lo llena Gateway.Ua.</summary>
public sealed record UaServerStatus(
    int ConnectedSessions,
    int MonitoredItems);

public sealed record GatewayStatus(
    LinkState LinkState,
    DateTime? LastSuccessfulCycleUtc,
    double? SecondsSinceLastCycle,
    int ReconnectAttempts,
    string? LastError,
    DateTime StartedUtc,
    double UptimeSeconds);

public sealed record GatewayCounters(
    int TotalConfigured,
    int Good,
    int Uncertain,
    int Bad,
    int WaitingForInitialData,
    int SilentNeverAnswered,
    int SilentPreviouslyAnswered,
    long ReadCycles,
    long ReadFailures,
    long DaConnections,
    long DaDisconnections)
{
    /// <summary>Tags que dejaron de contestar, sin contar los que aun no se leyeron.</summary>
    public int SilentTotal => SilentNeverAnswered + SilentPreviouslyAnswered;
}

public sealed record GatewayPerformance(
    double LastCycleMs,
    double AvgCycleMs,
    double MaxCycleMs,
    int ConfiguredIntervalMs,
    int ConnectedUaSessions,
    int MonitoredItems,
    double WorkingSetMb);

/// <summary>
/// Foto del gateway en un instante. Unica fuente para los nodos UA de
/// diagnostico y para la pagina web: si cada vista armara sus propios numeros,
/// terminarian discrepando justo cuando hay un problema.
/// </summary>
public sealed record GatewaySnapshot(
    DateTime TakenUtc,
    GatewayStatus Status,
    GatewayCounters Counters,
    GatewayPerformance Performance,
    Diagnosis Diagnosis)
{
    /// Por debajo de esta fraccion de tags mudos no se afirma una causa global.
    private const double PartialDegradationThreshold = 0.05;

    /// Cuanto tiene que dominar un bucket para atribuirle la causa. No es magia:
    /// dos tags mal tipeados en un CSV de 8.000 no cambian el diagnostico, pero
    /// mitad y mitad si significa que estan pasando dos cosas.
    private const double AttributionThreshold = 0.90;

    /// <summary>
    /// Arma la foto recorriendo la cache por la misma puerta que usa el node
    /// manager (<see cref="TagCache.Get"/>), que degrada al leer. Leer el estado
    /// por otro camino daria una vista que puede contradecir a la del cliente UA.
    /// </summary>
    public static GatewaySnapshot Build(
        TagCache cache,
        DaLinkStatus link,
        UaServerStatus ua,
        DateTime startedUtc)
    {
        var now = DateTime.UtcNow;
        int good = 0, uncertain = 0, bad = 0, waiting = 0, neverAnswered = 0, previouslyAnswered = 0;

        foreach (var uaName in cache.UaNames)
        {
            var state = cache.Get(uaName);

            switch (state.Quality.Master)
            {
                case QualityMaster.Good: good++; break;
                case QualityMaster.Uncertain: uncertain++; break;
                default: bad++; break;
            }

            // Al arranque todos los tags estan en este estado. Contarlos como
            // mudos dispararia un diagnostico de falla en cada inicio.
            if (state.Quality.Substatus == QualitySubstatus.BadWaitingForInitialData)
            {
                waiting++;
                continue;
            }

            // Mudo = no se esta refrescando, que no es lo mismo que tener mala
            // calidad. Un tag que llega Uncertain porque el sensor esta fuera de
            // rango esta contestando bien; contarlo aca diagnosticaria una caida
            // donde solo hay ruido de proceso. Solo cuentan los dos casos en que
            // no llega dato: los Bad (rechazado, no conectado, no convierte) y
            // el Uncertain que la propia cache fabrica por antiguedad.
            var silent = state.Quality.Master is QualityMaster.Bad or QualityMaster.Error
                         || state.Quality.Substatus == QualitySubstatus.UncertainLastUsableValue;

            if (!silent) continue;

            // ScaledValue solo se puebla con una muestra utilizable y ningun
            // camino lo vuelve a null: que no sea null significa que este ItemID
            // contesto alguna vez. Ese es todo el discriminante.
            if (state.ScaledValue is null) neverAnswered++;
            else previouslyAnswered++;
        }

        var counters = new GatewayCounters(
            cache.Count, good, uncertain, bad, waiting,
            neverAnswered, previouslyAnswered,
            link.ReadCycles, link.ReadFailures, link.Connections, link.Disconnections);

        var status = new GatewayStatus(
            link.State,
            link.LastSuccessfulCycleUtc,
            link.LastSuccessfulCycleUtc is { } last ? (now - last).TotalSeconds : null,
            link.ReconnectAttempts,
            link.LastError,
            startedUtc,
            (now - startedUtc).TotalSeconds);

        var performance = new GatewayPerformance(
            link.LastCycleMs, link.AvgCycleMs, link.MaxCycleMs, link.ConfiguredIntervalMs,
            ua.ConnectedSessions, ua.MonitoredItems,
            System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1024d / 1024d);

        return new GatewaySnapshot(now, status, counters, performance, Diagnose(link.State, counters));
    }

    /// <summary>
    /// Primero manda el vinculo: no tiene sentido preguntarse por el CSV cuando
    /// no hay con quien hablar. Recien con el vinculo sano se mira la proporcion
    /// interna del bucket de mudos.
    /// </summary>
    private static Diagnosis Diagnose(LinkState state, GatewayCounters c)
    {
        if (state == LinkState.Stalled) return Diagnosis.DaServerStalled;
        if (state is LinkState.Disconnected or LinkState.Reconnecting) return Diagnosis.DaLinkDown;

        var silent = c.SilentTotal;
        if (silent == 0) return Diagnosis.Healthy;

        if (c.TotalConfigured > 0 &&
            (double)silent / c.TotalConfigured < PartialDegradationThreshold)
            return Diagnosis.PartialDegradation;

        if ((double)c.SilentNeverAnswered / silent >= AttributionThreshold)
            return Diagnosis.LikelyCsvMismatch;

        if ((double)c.SilentPreviouslyAnswered / silent >= AttributionThreshold)
            return Diagnosis.DaServerRepopulatedEmpty;

        return Diagnosis.Indeterminate;
    }
}