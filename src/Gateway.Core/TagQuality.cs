namespace Gateway.Core;

/// <summary>Nivel general de la calidad DA (bits 7-6).</summary>
public enum QualityMaster
{
    Bad = 0,
    Uncertain = 64,
    Error = 128,   // reservado por la spec, no deberia aparecer nunca
    Good = 192
}

/// <summary>
/// Causa concreta dentro del nivel (bits 5-2). Los valores son los de la
/// especificacion; el substatus ya implica el master.
/// </summary>
public enum QualitySubstatus
{
    Bad = 0,
    BadConfigurationError = 4,
    BadNotConnected = 8,
    BadDeviceFailure = 12,
    BadSensorFailure = 16,
    BadLastKnown = 20,
    BadCommFailure = 24,
    BadOutOfService = 28,
    BadWaitingForInitialData = 32,
    Uncertain = 64,
    UncertainLastUsableValue = 68,
    UncertainSensorNotAccurate = 80,
    UncertainEngineeringUnitsExceeded = 84,
    UncertainSubNormal = 88,
    Good = 192,
    GoodLocalOverride = 216
}

/// <summary>Si el valor esta pegado a un limite (bits 1-0).</summary>
public enum QualityLimit
{
    NotLimited = 0,
    Low = 1,
    High = 2,
    Constant = 3
}

/// <summary>
/// Calidad de una muestra, con la misma estructura de tres campos que define
/// OPC DA. No se aplana a un enum unico porque los tres campos son
/// independientes: aplanarlos dejaria sin lugar al limit status.
/// Los 8 bits de fabricante se descartan (lo indica la spec, Parte 8 A.3.2.3).
/// </summary>
public readonly record struct TagQuality(
    QualityMaster Master,
    QualitySubstatus Substatus,
    QualityLimit Limit)
{
    /// Calidad buena y sin limites, el caso normal.
    public static readonly TagQuality Good =
        new(QualityMaster.Good, QualitySubstatus.Good, QualityLimit.NotLimited);

    /// El gateway todavia no leyo este tag.
    public static readonly TagQuality WaitingForInitialData =
        new(QualityMaster.Bad, QualitySubstatus.BadWaitingForInitialData, QualityLimit.NotLimited);

    /// Se pidio un tag que la cache no conoce.
    public static readonly TagQuality UnknownTag =
        new(QualityMaster.Bad, QualitySubstatus.BadConfigurationError, QualityLimit.NotLimited);

    /// Llego un valor pero no convierte al DataType declarado en el CSV.
    public static readonly TagQuality ConversionError =
        new(QualityMaster.Bad, QualitySubstatus.BadConfigurationError, QualityLimit.NotLimited);

    /// <summary>El valor sirve para transformar (multiplicador y offset).</summary>
    public bool IsUsable =>
        Master is QualityMaster.Good or QualityMaster.Uncertain;
}