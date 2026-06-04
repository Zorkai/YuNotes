using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using YuNotes.Models;

namespace YuNotes.Input;

// Lightweight palm rejection:
//  - Pen events are always accepted.
//  - When a pen has been active in the last `graceMs` ms, touch is rejected.
//  - Optional: reject touch entirely when palm rejection is enabled.
public sealed class PalmRejection
{
    private readonly AppSettings _settings;
    private DateTime _lastPen = DateTime.MinValue;
    public PalmRejection(AppSettings settings) => _settings = settings;

    public bool Accept(PointerRoutedEventArgs e)
    {
        var kind = e.Pointer.PointerDeviceType;
        if (kind == PointerDeviceType.Pen)
        {
            _lastPen = DateTime.UtcNow;
            return true;
        }
        if (kind == PointerDeviceType.Mouse) return true;
        // Touch
        if (!_settings.PalmRejectionEnabled) return true;
        if (_settings.IgnoreTouchWhilePenActive &&
            (DateTime.UtcNow - _lastPen).TotalMilliseconds < _settings.PalmRejectionGraceMs)
            return false;
        return true;
    }
}
