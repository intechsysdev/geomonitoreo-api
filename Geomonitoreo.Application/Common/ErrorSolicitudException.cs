namespace Geomonitoreo.Application.Common;

/// <summary>
/// El cliente mandó algo que no se puede procesar (firma ilegible, cédula vacía…).
/// El API la traduce a un 400 con el mensaje tal cual, que es lo que el formulario muestra.
/// </summary>
public class ErrorSolicitudException(string mensaje) : Exception(mensaje);
