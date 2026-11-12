namespace GlassLingo.Config
{
    internal static class DisplayPreferences
    {
        public static string Get(AppConfig cfg, string scope) => scope switch
        { "watch" => cfg.WatchDisplayMode, "full" => cfg.FullscreenDisplayMode, _ => cfg.DisplayMode };
        public static void Set(AppConfig cfg, string scope, string value)
        {
            if (value is not ("popup" or "inline" or "sidebar")) return;
            switch (scope) { case "watch": cfg.WatchDisplayMode = value; break; case "full": cfg.FullscreenDisplayMode = value; break; default: cfg.DisplayMode = value; break; }
        }
    }
}
