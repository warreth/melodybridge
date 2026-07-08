using MudBlazor;

namespace MelodyBridge.Server.Themes;

public class MainTheme : MudTheme
{
    public MainTheme()
    {
        Palette = new PaletteLight()
        {
            Primary = Colors.Blue.Default,
            Secondary = Colors.Green.Accent4,
            AppbarBackground = Colors.Red.Default,
        };

        LayoutProperties = new LayoutProperties()
        {
            DrawerWidthLeft = "260px"
        };
    }
}