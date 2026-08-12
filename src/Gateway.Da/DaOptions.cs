namespace Gateway.Da;

/// Configuracion del driver OPC DA, leida de appsettings.json.
public class DaOptions
{
    /// Identificador del servidor DA en el registro de Windows.
    public string ProgId { get; set; } = "Matrikon.OPC.Simulation.1";

    /// Cada cuanto refresca el servidor su cache, y cada cuanto la leemos.
    /// Es el ritmo real de adquisicion, distinto del de publicacion UA.
    public int UpdateRateMs { get; set; } = 1000;
}