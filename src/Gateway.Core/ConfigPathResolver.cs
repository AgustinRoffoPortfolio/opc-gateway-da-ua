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

    /// Ubica la carpeta base contra la que se resuelven las rutas de datos que
    /// el proceso crea al arrancar (por ejemplo, la PKI). A diferencia de
    /// Resolve(), no exige que el destino final exista.
    ///
    /// Corriendo desde el repo devuelve la raiz del repo, para que todos los
    /// proyectos compartan una unica carpeta de datos aunque cada uno corra
    /// desde su propio bin/. En un paquete publicado no hay solucion arriba, y
    /// entonces la carpeta base es la del ejecutable: el paquete es
    /// autocontenido y no depende de nada que este por encima suyo.
    public static string ResolveDataRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, RepoRootMarker))) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }
}
