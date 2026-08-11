namespace Gateway.Core;

/// Localiza un archivo de configuracion subiendo desde la carpeta del
/// ejecutable hasta encontrarlo. Permite que cada proyecto corra desde su
/// propio bin/ contra una configuracion unica en la raiz del repo.
public static class ConfigPathResolver
{
    public static string Resolve(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) return relativePath;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            // El separador de rutas relativas puede venir con "/" y en Windows
            // Path.Combine lo deja mezclado con "\".
            var candidate = Path.GetFullPath(Path.Combine(dir.FullName, relativePath));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"No se encontro '{relativePath}' subiendo desde {AppContext.BaseDirectory}");
    }
}
