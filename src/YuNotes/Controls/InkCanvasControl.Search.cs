using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using YuNotes.Input;
using YuNotes.Models;
using YuNotes.Rendering;
using YuNotes.Tools;

namespace YuNotes.Controls;

public sealed partial class InkCanvasControl
{

    // Search: marks every match, page by page (rects in page coordinates), and
    // emphasizes the current one. Pass null to clear.
    public void SetSearchHighlights(IReadOnlyDictionary<int, List<Windows.Foundation.Rect>>? byPage,
                                    int currentPage = -1, Windows.Foundation.Rect? current = null)
    {
        for (int i = 0; i < _pageCanvases.Count; i++)
        {
            List<Windows.Foundation.Rect>? rects = null;
            byPage?.TryGetValue(i, out rects);
            _pageCanvases[i].SetSearchHighlights(rects, i == currentPage ? current : null);
        }
    }

    // PDF text selection (Hand tool). Lives on the active page; copying delegates there.
    public string? GetSelectedText() => ActivePageCanvas?.GetSelectedText();
    public void ClearTextSelection()
    {
        foreach (var pc in _pageCanvases) pc.ClearTextSelection();
    }
}
