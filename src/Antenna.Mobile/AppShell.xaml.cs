using Antenna.Mobile.Views;

namespace Antenna.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("player", typeof(PlayerPage));
        Routing.RegisterRoute("addplaylist", typeof(AddPlaylistPage));
    }
}
