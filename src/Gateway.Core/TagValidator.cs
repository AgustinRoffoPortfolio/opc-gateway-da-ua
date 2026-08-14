namespace Gateway.Core;

/// Punto de entrada publico para cargar el CSV de tags: parsea con
/// CsvTagLoader y aplica las reglas que necesitan ver el archivo completo,
/// no una fila aislada. Por ahora la unica regla de ese tipo es la unicidad
/// de TAG_NAME_OPC_UA; el resto de la validacion de Fase 3 (compatibilidad
/// de tipos, por ejemplo) se suma en pasos siguientes, no en este.
public static class TagValidator
{
    public static TagLoadResult LoadAndValidate(string relativePath)
    {
        var path = ConfigPathResolver.Resolve(relativePath);
        var parsed = CsvTagLoader.Parse(path);

        var errors = new List<TagLoadError>(parsed.Errors);
        var tags = new List<TagDefinition>();
        // Gana la primera aparicion de cada nombre: comportamiento
        // deterministico y facil de explicar ("el primero que aparece en
        // el archivo"), en vez de rechazar ambas filas o quedarse con la
        // ultima.
        var seenNames = new HashSet<string>();

        foreach (var row in parsed.Rows)
        {
            if (!seenNames.Add(row.Tag.OpcUaName))
            {
                errors.Add(new TagLoadError(row.LineNumber, row.Tag.OpcUaName,
                    $"linea {row.LineNumber}: '{row.Tag.OpcUaName}' ya aparecio antes en el archivo, esta fila queda fuera de servicio."));
                continue;
            }

            tags.Add(row.Tag);
        }

        return new TagLoadResult(tags, errors);
    }
}