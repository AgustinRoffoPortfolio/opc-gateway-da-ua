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
                    Multiplier: double.Parse(fields[3], CultureInfo.InvariantCulture),
                    Offset: double.Parse(fields[4], CultureInfo.InvariantCulture),
                    EngineeringUnit: fields[5],
                    ScanRateMs: int.Parse(fields[6], CultureInfo.InvariantCulture),
                    Deadband: double.Parse(fields[7], CultureInfo.InvariantCulture),
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
}