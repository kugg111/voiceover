using System.Windows;

namespace DiscordClone.Client;

public partial class App : Application
{
    // Change this if your server runs on a different port (see Server/appsettings.json).
    public const string ApiBaseUrl = "http://localhost:5220/";
    public const string HubUrl = "http://localhost:5220/hubs/chat";
}
