namespace Gateway.Web;

/// <summary>Configuracion de la capa de diagnostico web.</summary>
/// <param name="ListenUrl">
/// Donde escucha Kestrel. Localhost a proposito: la pagina de diagnostico no
/// tiene autenticacion, asi que exponerla en todas las interfaces regalaria el
/// estado interno del gateway a la red. Que sea configurable permite cambiarlo
/// cuando en la Fase 7 haya con que protegerla.
/// </param>
/// <param name="Enabled">
/// Permite apagar la web sin tocar el resto. Si Kestrel no levanta (un puerto
/// ocupado, por ejemplo), el gateway tiene que poder seguir sirviendo OPC UA:
/// el diagnostico es accesorio y no puede tumbar la funcion principal.
/// </param>
public sealed record WebOptions(
    string ListenUrl = "http://localhost:8080",
    bool Enabled = true);