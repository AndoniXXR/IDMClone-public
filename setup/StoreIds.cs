namespace IDMClone.Setup
{
    // ID que da cada tienda a la extensión al publicarla (cada tienda asigna el suyo, distinto del de
    // desarrollo). Con un ID puesto, el instalador la registra como "extensión externa": al abrir el
    // navegador se descarga de la tienda y avisa "Se necesita acción" para activarla, como con IDM.
    // Vacío = todavía no publicada en esa tienda (se añade a mano con la guía).
    public static class StoreIds
    {
        public static string ChromeWebStore = "";   // Chrome, Brave (lee la clave de Chrome), Vivaldi
        public static string EdgeAddons = "";       // Microsoft Edge

        public const string ChromeUpdateUrl = "https://clients2.google.com/service/update2/crx";
        public const string EdgeUpdateUrl = "https://edge.microsoft.com/extensionwebstorebase/v1/crx";
        public const string ChromeExternalKey = @"Software\Google\Chrome\Extensions";
        public const string EdgeExternalKey = @"Software\Microsoft\Edge\Extensions";
    }
}
