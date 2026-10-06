namespace Geomonitoreo.API.Configuration;

/// <summary>
/// Nombres de las políticas del limitador. Viven aquí porque los usan tanto el arranque, que las
/// define, como los controladores que las exigen.
/// </summary>
public static class PoliticasLimite
{
    /// <summary>Lecturas de la consola: flota, detalle, recorridos, geocercas.</summary>
    public const string General = "general";

    /// <summary>
    /// Acciones sobre los equipos (pedir la ubicación). Despiertan el teléfono y gastan batería:
    /// un botón apretado en bucle no debe convertirse en una ráfaga contra la flota.
    /// </summary>
    public const string Acciones = "acciones";
}
