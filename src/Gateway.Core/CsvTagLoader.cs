using System.Globalization;

namespace Gateway.Core;

/// Parsea el CSV de tags fila por fila, sin frenar el archivo entero por una
/// fila mal formada: cada fila que no parsea se acumula como error en vez de
/// tirar excepcion. La validacion que necesita ver el CSV completo (como
/// nombres TAG_NAME_OPC_UA duplicados) es responsabilidad de TagValidator,
/// no de esta clase - esta solo lee filas, no las compara entre si.
internal static class CsvTagLoader
{
    private const char Separator = ';';
    private const char CommentPrefix = '#';
    private const int ExpectedColumns = 11;

    internal static CsvParseResult Parse(string path)
    {
        var rows = File.ReadAllLines(path)
            .Select((line, index) => (Line: line, Number: index + 1))
            .Where(row => row.Line.Length > 0 && row.Line[0] != CommentPrefix)
            .ToArray();

        var parsedRows = new List<ParsedTagRow>();
        var errors = new List<TagLoadError>();

        // La primera fila no comentada es la cabecera, se saltea.
        for (var i = 1; i < rows.Length; i++)
        {
            var (line, lineNumber) = rows[i];
            var fields = line.Split(Separator);
            if (fields.Length != ExpectedColumns)
            {
                errors.Add(new TagLoadError(lineNumber, "",
                    $"'{path}' linea {lineNumber}: tiene {fields.Length} columnas, se esperaban {ExpectedColumns} ('{line}')."));
                continue;
            }

            var opcUaName = fields[0];
            try
            {
                var tag = new TagDefinition(
                    OpcUaName: opcUaName,
                    OpcDaName: fields[1],
                    DataType: Enum.Parse<TagDataType>(fields[2], ignoreCase: true),
                    Multiplier: ParseDouble(fields[3], "MULTIPLICADOR"),
                    Offset: ParseDouble(fields[4], "OFFSET"),
                    EngineeringUnit: fields[5],
                    ScanRateMs: int.Parse(fields[6], CultureInfo.InvariantCulture),
                    Deadband: ParseDouble(fields[7], "DEADBAND"),
                    AccessLevel: Enum.Parse<TagAccessLevel>(fields[8], ignoreCase: true),
                    Description: fields[9],
                    Enabled: bool.Parse(fields[10]));
                parsedRows.Add(new ParsedTagRow(lineNumber, tag));
            }
            catch (Exception ex)
            {
                errors.Add(new TagLoadError(lineNumber, opcUaName,
                    $"'{path}' linea {lineNumber}, tag '{opcUaName}': {ex.Message}"));
            }
        }

        return new CsvParseResult(parsedRows, errors);
    }

    /// Parsea un decimal en cultura invariante SIN permitir separador de miles.
    /// El default de double.Parse incluye NumberStyles.AllowThousands, con lo
    /// que "1,5" (un Excel en es-AR que piso el punto por coma) se leia como
    /// 15 en silencio: el tag cargaba bien y quedaba escalado 10 veces mal.
    private static double ParseDouble(string field, string columnName)
    {
        if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException(
                $"{columnName} '{field}' no es un decimal valido (se espera PUNTO como separador decimal, no coma).");
        }

        return value;
    }
}