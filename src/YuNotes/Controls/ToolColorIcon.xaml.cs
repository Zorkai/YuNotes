using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace YuNotes.Controls;

public sealed partial class ToolColorIcon : UserControl
{
    public ToolColorIcon() => InitializeComponent();

    public Color NibColor
    {
        get => _color;
        set { _color = value; NibPath.Fill = new SolidColorBrush(value); }
    }
    private Color _color = Microsoft.UI.Colors.Gray;

    public void SetSize(double size)
    {
        IconViewbox.Width = size;
        IconViewbox.Height = size;
    }
}
