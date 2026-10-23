namespace IDMClone
{
    // Actualizaciones (Ayuda > Comprobar actualizaciones... y la comprobación semanal de IDM). Ver Updater.cs.
    public partial class AppConfig
    {
        // "Comprobar actualizaciones automáticamente cada semana" (ventana de actualizaciones). Activada, como en IDM;
        // en el campo de respaldo para que las configuraciones antiguas (sin la clave) también la tengan.
        bool _autoCheckUpdates = true;
        public bool AutoCheckUpdates { get { return _autoCheckUpdates; } set { _autoCheckUpdates = value; } }

        // De dónde se leen las versiones publicadas: un manifiesto JSON propio o la API de GitHub Releases.
        // Vacío = Updater.DefaultFeedUrl. Solo se cambia a mano en config.json (ver los formatos en Updater.cs).
        public string UpdateFeedUrl { get; set; }

        // Última comprobación que respondió y último fallo (fecha UTC en formato ISO 8601 "o"; null = nunca).
        public string LastUpdateCheck { get; set; }
        public string LastUpdateFailure { get; set; }
    }
}
