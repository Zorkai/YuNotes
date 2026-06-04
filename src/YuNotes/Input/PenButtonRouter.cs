using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using YuNotes.Models;

namespace YuNotes.Input;

// Maps pen barrel / eraser-tip / top button states to a tool override action.
public sealed class PenButtonRouter
{
    private readonly AppSettings _settings;
    public PenButtonRouter(AppSettings settings) => _settings = settings;

    public PenButtonAction Resolve(PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Pen) return PenButtonAction.None;
        var pt = e.GetCurrentPoint(null);
        var props = pt.Properties;
        if (props.IsEraser) return PenButtonAction.Eraser;
        if (props.IsBarrelButtonPressed) return _settings.BarrelButtonAction;
        return PenButtonAction.None;
    }
}
