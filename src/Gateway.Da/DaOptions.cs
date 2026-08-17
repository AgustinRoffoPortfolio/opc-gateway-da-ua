namespace Gateway.Da;

/// Configuracion del driver OPC DA, leida de appsettings.json.
public class DaOptions
{
    /// Identificador del servidor DA en el registro de Windows.
    public string ProgId { get; set; } = "Matrikon.OPC.Simulation.1";

    /// Cada cuanto refresca el servidor su cache, y cada cuanto la leemos.
    /// Es el ritmo real de adquisicion, distinto del de publicacion UA.
    public int UpdateRateMs { get; set; } = 1000;

    /// Ciclos sin refresco despues de los cuales un tag se considera viejo y
    /// su calidad se degrada. Se cuenta en ciclos y no en milisegundos porque
    /// el criterio real es "cuantas lecturas nos perdimos", y eso sigue siendo
    /// valido si manana cambia UpdateRateMs.
    /// Tres y no dos: un ciclo perdido puede ser jitter del scheduler o una
    /// lectura DA lenta, y un gateway que parpadea a Uncertain por ruido no
    /// sirve para decidir nada.
    public int StaleAfterCycles { get; set; } = 3;

    /// Espera entre intentos de reconexion cuando el servidor DA no responde.
    /// Mas corto que esto solo agrega ruido al log: si el servidor DA esta
    /// caido, no vuelve en dos segundos.
    public int ReconnectDelayMs { get; set; } = 5000;

    /// Cada cuanto se reintenta dar de alta los items que el servidor DA
    /// rechazo. Un rechazo no siempre es definitivo: si el servidor acaba de
    /// arrancar puede no tener su lista de items lista todavia, y en una planta
    /// se agregan instrumentos con el gateway corriendo. Un ItemID mal escrito
    /// se sigue rechazando siempre y termina igual fuera de servicio.
    /// Treinta segundos: alcanza para resolver un arranque frio en el primer
    /// reintento, y no machaca al servidor legado con altas fallidas.
    public int ItemRetryIntervalMs { get; set; } = 30000;
}