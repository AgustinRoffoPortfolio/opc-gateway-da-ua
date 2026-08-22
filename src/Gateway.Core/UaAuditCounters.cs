namespace Gateway.Core;

/// <summary>En que etapa del establecimiento de la conexion se rechazo.</summary>
/// <remarks>
/// El certificado del cliente se valida al abrir el canal seguro, antes de que
/// exista sesion; el token de usuario recien al crearla. Son dos caminos
/// distintos del stack, y meterlos en un solo contador borraria justamente la
/// diferencia que un operador necesita para saber que arreglar.
/// </remarks>
public enum RejectionCategory
{
    Certificate,
    Token,
    Other
}

/// <summary>Foto de la auditoria de conexiones UA en un instante.</summary>
public sealed record UaAuditSnapshot(
    long SessionsCreated,
    long SessionsClosed,
    long RejectedByCertificate,
    long RejectedByToken,
    long RejectedOther,
    string? LastRejectionReason,
    DateTime? LastRejectionUtc,
    IReadOnlyDictionary<string, long> RejectionsByReason)
{
    public long RejectedTotal => RejectedByCertificate + RejectedByToken + RejectedOther;

    /// Estado inicial, para que quien lea antes del primer evento no reciba null.
    public static UaAuditSnapshot Empty { get; } =
        new(0, 0, 0, 0, 0, null, null, new Dictionary<string, long>());
}

/// <summary>
/// Acumulador de eventos de conexion UA: sesiones abiertas, cerradas e intentos
/// rechazados, agrupados por motivo.
/// </summary>
/// <remarks>
/// Existe porque un rechazo correcto no es una falla del gateway: es un evento
/// contable. Hoy el stack lo reporta con la misma severidad que un error real
/// (un solo rechazo de certificado son doce lineas ERR, repetidas en cada
/// reintento del cliente), asi que el log no sirve para responder "cuantas
/// veces paso y por que".
///
/// No hay deduplicacion a proposito: un cliente que reintenta cada cinco
/// segundos suma un intento cada cinco segundos. Ese numero creciendo es
/// exactamente la senal de que hay alguien afuera insistiendo mal configurado.
///
/// Sin dependencias del stack UA: recibe categoria y motivo ya traducidos a
/// texto desde Gateway.Ua, que es la unica capa que conoce los StatusCode.
/// </remarks>
public sealed class UaAuditCounters
{
    private long _sessionsCreated;
    private long _sessionsClosed;
    private long _rejectedByCertificate;
    private long _rejectedByToken;
    private long _rejectedOther;

    // El desglose por motivo y el ultimo rechazo se tocan juntos, asi que van
    // bajo un lock en vez de Interlocked: son eventos raros, no el camino
    // caliente. Los contadores agregados si son atomicos porque los lee el
    // timer de publicacion una vez por segundo.
    private readonly object _lock = new();
    private readonly Dictionary<string, long> _byReason = new(StringComparer.Ordinal);
    private string? _lastReason;
    private DateTime? _lastUtc;

    public void RecordSessionCreated() => Interlocked.Increment(ref _sessionsCreated);

    public void RecordSessionClosed() => Interlocked.Increment(ref _sessionsClosed);

    /// <param name="reason">
    /// Nombre simbolico del StatusCode (BadCertificateUntrusted, etc). Se usa
    /// como clave de agrupacion, asi que tiene que ser estable: nunca el mensaje
    /// de error, que cambia con la version del stack y con el idioma.
    /// </param>
    public void RecordRejection(RejectionCategory category, string reason)
    {
        switch (category)
        {
            case RejectionCategory.Certificate:
                Interlocked.Increment(ref _rejectedByCertificate);
                break;
            case RejectionCategory.Token:
                Interlocked.Increment(ref _rejectedByToken);
                break;
            default:
                Interlocked.Increment(ref _rejectedOther);
                break;
        }

        lock (_lock)
        {
            _byReason[reason] = _byReason.GetValueOrDefault(reason) + 1;
            _lastReason = reason;
            _lastUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Copia inmutable del estado actual. El diccionario se clona: si se
    /// devolviera el interno, el ciclo de publicacion podria estar recorriendolo
    /// mientras llega un rechazo nuevo.
    /// </summary>
    public UaAuditSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new UaAuditSnapshot(
                Interlocked.Read(ref _sessionsCreated),
                Interlocked.Read(ref _sessionsClosed),
                Interlocked.Read(ref _rejectedByCertificate),
                Interlocked.Read(ref _rejectedByToken),
                Interlocked.Read(ref _rejectedOther),
                _lastReason,
                _lastUtc,
                new Dictionary<string, long>(_byReason, StringComparer.Ordinal));
        }
    }
}