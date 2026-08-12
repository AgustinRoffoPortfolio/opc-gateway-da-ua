using Gateway.Core;
using Gateway.Ua;
using Opc.Ua;

namespace Gateway.Tests;

/// <summary>
/// Verifica la traduccion de calidad DA -> StatusCode UA contra la tabla A.3
/// del Anexo A (normativo) de la especificacion OPC UA Parte 8.
/// </summary>
public class QualityMapperTests
{
    // Las 16 filas de la tabla normativa, transcritas de la spec y no del
    // codigo del mapper: si las dos transcripciones coinciden, es evidencia
    // de que ninguna tiene un error de copiado.
    public static TheoryData<QualitySubstatus, uint> NormativeTable => new()
    {
        { QualitySubstatus.Good,                              StatusCodes.Good },
        { QualitySubstatus.GoodLocalOverride,                 StatusCodes.GoodLocalOverride },
        { QualitySubstatus.Uncertain,                         StatusCodes.Uncertain },
        { QualitySubstatus.UncertainLastUsableValue,          StatusCodes.UncertainLastUsableValue },
        { QualitySubstatus.UncertainSensorNotAccurate,        StatusCodes.UncertainSensorNotAccurate },
        { QualitySubstatus.UncertainEngineeringUnitsExceeded, StatusCodes.UncertainEngineeringUnitsExceeded },
        { QualitySubstatus.UncertainSubNormal,                StatusCodes.UncertainSubNormal },
        { QualitySubstatus.Bad,                               StatusCodes.Bad },
        { QualitySubstatus.BadConfigurationError,             StatusCodes.BadConfigurationError },
        { QualitySubstatus.BadNotConnected,                   StatusCodes.BadNotConnected },
        { QualitySubstatus.BadDeviceFailure,                  StatusCodes.BadDeviceFailure },
        { QualitySubstatus.BadSensorFailure,                  StatusCodes.BadSensorFailure },
        { QualitySubstatus.BadLastKnown,                      StatusCodes.BadOutOfService },
        { QualitySubstatus.BadCommFailure,                    StatusCodes.BadNoCommunication },
        { QualitySubstatus.BadOutOfService,                   StatusCodes.BadOutOfService },
        { QualitySubstatus.BadWaitingForInitialData,          StatusCodes.BadWaitingForInitialData }
    };

    [Theory]
    [MemberData(nameof(NormativeTable))]
    public void Traduce_segun_la_tabla_normativa(QualitySubstatus substatus, uint expected)
    {
        var quality = new TagQuality(QualityMaster.Bad, substatus, QualityLimit.NotLimited);

        Assert.Equal(expected, QualityMapper.ToStatusCode(quality).Code);
    }

    // El test que mas protege: si manana se agrega un substatus al enum y se
    // olvida la fila en el mapper, esto falla nombrando el que falta.
    [Fact]
    public void Ningun_substatus_queda_sin_fila()
    {
        foreach (var substatus in Enum.GetValues<QualitySubstatus>())
        {
            var quality = new TagQuality(QualityMaster.Bad, substatus, QualityLimit.NotLimited);
            var mapped = QualityMapper.ToStatusCode(quality).Code;

            Assert.True(
                NormativeTable.Any(row => (QualitySubstatus)row[0]! == substatus),
                $"Falta la fila de {substatus} en la tabla normativa del test");

            // Solo Bad generico puede mapear a Bad; el resto tiene codigo propio.
            if (substatus != QualitySubstatus.Bad)
                Assert.NotEqual(StatusCodes.Bad, mapped);
        }
    }

    [Fact]
    public void Substatus_desconocido_cae_en_Bad()
    {
        // 99 no existe en la spec: simula un servidor DA que reporta basura.
        var quality = new TagQuality(QualityMaster.Bad, (QualitySubstatus)99, QualityLimit.NotLimited);

        Assert.Equal(StatusCodes.Bad, QualityMapper.ToStatusCode(quality).Code);
    }

    [Theory]
    [InlineData(QualityMaster.Good, true)]
    [InlineData(QualityMaster.Uncertain, true)]
    [InlineData(QualityMaster.Bad, false)]
    [InlineData(QualityMaster.Error, false)]
    public void IsUsable_solo_para_Good_y_Uncertain(QualityMaster master, bool expected)
    {
        var quality = new TagQuality(master, QualitySubstatus.Good, QualityLimit.NotLimited);

        Assert.Equal(expected, quality.IsUsable);
    }
}