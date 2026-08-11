namespace Gateway.Core;

/// Localiza rutas de la raiz del repo subiendo desde la carpeta del
/// ejecutable. Permite que cada proyecto corra desde su propio bin/ contra
/// una configuracion unica en la raiz, sin depender del working directory.
public static class ConfigPathResolver
{
    // Marca la raiz del repo. A diferencia de una carpeta de datos, el archivo
    // de solucion siempre existe, asi que sirve de ancla aunque el destino
    // final que se quiera construir (por ejemplo, la carpeta de PKI) todavia no.
    private const string RepoRootMarker = "Gateway.slnx";

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

    /// Ubica la raiz del repo subiendo desde la carpeta del ejecutable hasta
    /// encontrar el archivo de solucion. A diferencia de Resolve(), no exige
    /// que el destino final exista: sirve para construir rutas absolutas a
    /// carpetas que el proceso crea la primera vez que corre.
    public static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, RepoRootMarker))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"No se encontro '{RepoRootMarker}' subiendo desde {AppContext.BaseDirectory}");
    }
}
