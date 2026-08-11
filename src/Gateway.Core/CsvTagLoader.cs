using System.Globalization;

namespace Gateway.Core;

/// Lee el arbol de tags desde un CSV de cinco columnas separadas por ";"
/// (TAG_NAME_OPC_UA;TAG_NAME_OPC_DA;DATA_TYPE;MULTIPLICADOR;OFFSET).
/// Deliberadamente ingenuo: cualquier fila mal formada tira excepcion y el
/// gateway no arranca. Nada de acumular errores ni carga parcial, eso es
/// Fase 3.
public static class CsvTagLoader
{
    private const char Separator = ';';
    private const char CommentPrefix = '#';

    public static IReadOnlyList<TagDefinition> Load(string relativePath)
    {
        var path = ConfigPathResolver.Resolve(relativePath);

        // El numero de linea se calcula ANTES de descartar comentarios y
        // vacias: si se calculara despues, apuntaria a la posicion dentro
        // de la lista filtrada en vez de a la linea real del archivo.
        var rows = File.ReadAllLines(path)
            .Select((line, index) => (Line: line, Number: index + 1))
            .Where(row => row.Line.Length > 0 && row.Line[0] != CommentPrefix)
            .ToArray();

        // La primera fila no comentada es la cabecera, se saltea.
        var tags = new List<TagDefinition>();
        for (var i = 1; i < rows.Length; i++)
        {
            var (line, lineNumber) = rows[i];
            var fields = line.Split(Separator);
            if (fields.Length != 5)
            {
                throw new FormatException(
                    $"'{path}' linea {lineNumber}: tiene {fields.Length} columnas, se esperaban 5 ('{line}').");
            }

            var opcUaName = fields[0];
            try
            {
                tags.Add(new TagDefinition(
                    OpcUaName: opcUaName,
                    OpcDaName: fields[1],
                    DataType: Enum.Parse<TagDataType>(fields[2], ignoreCase: true),
                    Multiplier: double.Parse(fields[3], CultureInfo.InvariantCulture),
                    Offset: double.Parse(fields[4], CultureInfo.InvariantCulture)));
            }
            catch (Exception ex)
            {
                throw new FormatException(
                    $"'{path}' linea {lineNumber}, tag '{opcUaName}': {ex.Message}", ex);
            }
        }

        return tags;
    }
}
