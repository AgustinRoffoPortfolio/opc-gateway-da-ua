using System.Text.Json;
using System.Text.Json.Serialization;
using Gateway.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Web;

/// <summary>
/// Kestrel sirviendo el diagnostico: la foto del gateway y la tabla de tags.
/// </summary>
/// <remarks>
/// No arma el snapshot: lo lee del holder que llena el ciclo de publicacion.
/// Asi la pagina y los nodos UA muestran el mismo objeto, y un F5 no cuesta un
/// recorrido de la cache entera.
///
/// La tabla si se consulta en el momento, porque el snapshot solo tiene
/// agregados y el filtrado depende de parametros que el ciclo no conoce.
/// </remarks>
public sealed class DiagnosticsServer : IAsyncDisposable
{
    // Enums como texto y no como numero: si el JS comparara contra un indice,
    // reordenar el enum romperia la pagina en silencio. camelCase por convencion
    // de JSON, para no escribir propiedades en PascalCase del lado del navegador.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly WebApplication _app;

    public DiagnosticsServer(WebOptions options, SnapshotHolder snapshots, TagCache cache, ILoggerFactory loggerFactory)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // El logging del host ya esta configurado con Serilog: se reusa esa
        // fabrica en vez de que Kestrel monte su propio pipeline y escriba
        // en la consola con otro formato.
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggerFactory);

        builder.WebHost.UseUrls(options.ListenUrl);

        _app = builder.Build();

        // La foto ya armada. Null solo durante los primeros milisegundos del
        // arranque: se responde 503 en vez de un JSON vacio, para que la pagina
        // distinga "todavia no hay dato" de "el gateway dice que todo esta en cero".
        _app.MapGet("/api/diagnostics", (HttpContext http) =>
            snapshots.Current is { } snapshot
                ? Results.Json(snapshot, JsonOptions)
                : Results.Json(new { message = "El gateway todavia no publico su primer ciclo." },
                    JsonOptions, statusCode: StatusCodes.Status503ServiceUnavailable));

        // Filtrado y paginado del lado del servidor: por defecto solo los tags
        // que no estan en Good. Un volcado de 8.000 filas por segundo arrastra
        // el navegador para mostrar algo que nadie mira.
        _app.MapGet("/api/diagnostics/tags", (
            bool? onlyDegraded, string? search, int? offset, int? limit) =>
        {
            var page = TagDiagnosticsQuery.Query(
                cache,
                onlyDegraded ?? true,
                search,
                offset ?? 0,
                limit ?? 100);

            return Results.Json(page, JsonOptions);
        });
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _app.StartAsync(cancellationToken);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}