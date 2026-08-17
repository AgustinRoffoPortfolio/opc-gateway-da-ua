namespace Gateway.Core;

/// <summary>
/// Guarda la ultima foto armada por el ciclo de publicacion, para que las
/// vistas la consuman sin volver a construirla.
/// </summary>
/// <remarks>
/// Existe porque <see cref="GatewaySnapshot.Build"/> no es gratis: recorre la
/// cache entera y consulta el proceso. Si cada request HTTP armara su propia
/// foto, apretar F5 sobre 8.000 tags costaria un recorrido completo, y diez
/// clientes costarian diez.
///
/// El otro motivo es de correctitud, y pesa mas: la pagina y los nodos UA de
/// diagnostico sirven exactamente el mismo objeto. Si cada una armara el suyo
/// terminarian discrepando justo cuando hay un problema y alguien las esta
/// comparando.
///
/// Sin lock: un GatewaySnapshot es inmutable, asi que el lector ve la foto
/// vieja entera o la nueva entera, nunca una mezcla. La palabra clave volatile
/// no esta por la atomicidad de la escritura (una referencia en x86 ya lo es)
/// sino por la visibilidad: garantiza que el hilo que lee no siga viendo una
/// copia cacheada despues de que el timer publico una foto nueva.
/// </remarks>
public sealed class SnapshotHolder
{
    private volatile GatewaySnapshot? _current;

    /// <summary>
    /// Ultima foto publicada, o null si todavia no corrio el primer ciclo.
    /// Es null solo durante los primeros milisegundos del arranque, y quien la
    /// consuma tiene que contemplar ese caso en vez de asumir que siempre hay dato.
    /// </summary>
    public GatewaySnapshot? Current => _current;

    public void Publish(GatewaySnapshot snapshot) => _current = snapshot;
}