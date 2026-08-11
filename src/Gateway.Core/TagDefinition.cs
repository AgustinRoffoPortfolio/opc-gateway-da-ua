namespace Gateway.Core;

/// Tipos de dato soportados en el CSV de tags. El nombre coincide con la
/// columna DATA_TYPE para que Enum.Parse los reconozca directo.
public enum TagDataType
{
    Double,
    Boolean,
    Int32,
    String
}

/// Una fila del CSV de tags. Multiplier y Offset no se aplican todavia
/// (Fase 2): por ahora solo viajan junto con el resto de la definicion.
public sealed record TagDefinition(
    string OpcUaName,
    string OpcDaName,
    TagDataType DataType,
    double Multiplier,
    double Offset);
