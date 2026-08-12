using Gateway.Core;
using Opc.Ua;

namespace Gateway.Ua;

/// <summary>
/// Traduce la calidad OPC DA al StatusCode de OPC UA.
///
/// La tabla es la A.3 del Anexo A (normativo) de la especificacion OPC UA
/// Parte 8 - Data Access, seccion A.3.2.3. No es un mapeo propio.
/// Ver docs/calidad-da-ua.md para el detalle y las perdidas conocidas.
/// </summary>
public static class QualityMapper
{
    // Tabla explicita y en un solo lugar: es la regla de negocio central del
    // gateway. Repartirla en condicionales la volveria imposible de auditar.
    private static readonly Dictionary<QualitySubstatus, StatusCode> Map = new()
    {
        [QualitySubstatus.Good]                              = StatusCodes.Good,
        [QualitySubstatus.GoodLocalOverride]                 = StatusCodes.GoodLocalOverride,
        [QualitySubstatus.Uncertain]                         = StatusCodes.Uncertain,
        [QualitySubstatus.UncertainLastUsableValue]          = StatusCodes.UncertainLastUsableValue,
        [QualitySubstatus.UncertainSensorNotAccurate]        = StatusCodes.UncertainSensorNotAccurate,
        [QualitySubstatus.UncertainEngineeringUnitsExceeded] = StatusCodes.UncertainEngineeringUnitsExceeded,
        [QualitySubstatus.UncertainSubNormal]                = StatusCodes.UncertainSubNormal,
        [QualitySubstatus.Bad]                               = StatusCodes.Bad,
        [QualitySubstatus.BadConfigurationError]             = StatusCodes.BadConfigurationError,
        [QualitySubstatus.BadNotConnected]                   = StatusCodes.BadNotConnected,
        [QualitySubstatus.BadDeviceFailure]                  = StatusCodes.BadDeviceFailure,
        [QualitySubstatus.BadSensorFailure]                  = StatusCodes.BadSensorFailure,

        // Colapso deliberado de la spec: BadLastKnown y BadOutOfService caen en
        // el mismo StatusCode, asi que la traduccion no es reversible.
        [QualitySubstatus.BadLastKnown]                      = StatusCodes.BadOutOfService,
        [QualitySubstatus.BadOutOfService]                   = StatusCodes.BadOutOfService,

        [QualitySubstatus.BadCommFailure]                    = StatusCodes.BadNoCommunication,
        [QualitySubstatus.BadWaitingForInitialData]          = StatusCodes.BadWaitingForInitialData
    };

    /// <summary>
    /// Traduce una calidad DA. Un substatus desconocido cae en Bad generico:
    /// mejor un conservador de mas que un Good inventado.
    /// </summary>
    public static StatusCode ToStatusCode(TagQuality quality) =>
        Map.TryGetValue(quality.Substatus, out var code)
            ? code
            : StatusCodes.Bad;
}