using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics.Effects;
using Windows.UI;

namespace YuNotes.Controls;

/// <summary>
/// "Liquid glass" backdrop layer — a native port of the liquid-glass-react look
/// (blur + saturation + edge refraction + specular rim) built on the compositor.
///
/// Place as the first child of the panel that should look like glass, behind the
/// real content, with <c>CornerRadius</c> matching the host pill and
/// <c>Background</c> set to the normal (non-glass) brush. While
/// <see cref="IsGlassActive"/> is false the control just shows that Background,
/// so the host keeps today's acrylic look. When activated it swaps the
/// Background for a SpriteVisual whose effect brush samples the live backdrop:
///
///   backdrop → GaussianBlur → Saturation ┬→ tint overlay            (glass body)
///                                        └→ Transform2D zoom → edge-masked  (refraction)
///   + Win2D-drawn rim/sheen surface, all alpha-masked to the rounded rect.
///
/// The whole graph lives on the compositor thread: after setup there is no
/// per-frame CPU work, and the GPU cost is in the same class as the in-app
/// AcrylicBrush it replaces (one blur of a panel-sized region). The three mask
/// surfaces are tiny and redrawn only on resize / theme / DPI changes.
/// True per-pixel displacement maps (the React lib's SVG feDisplacementMap)
/// aren't in the composition-supported effect set, so the edge refraction uses
/// the same approximation the lib's "standard" map encodes: edge pixels sample
/// toward the center (a magnifying rim), masked to a band along the border.
/// </summary>
public sealed partial class LiquidGlassBackdrop : Grid
{
    // Tuning — logical px unless noted. Derived from liquid-glass-react defaults,
    // toned down for legibility of toolbar icons over ink.
    private const float BlurRadius = 13f;        // acrylic uses ~30; glass is clearer
    private const float SaturationBoost = 1.35f;
    // Dark theme: darken the sampled backdrop (in stops) so the glass stays
    // anchored dark even when floating over a white page, instead of flipping
    // to silver. Light theme keeps the unmodified backdrop.
    private const float DarkBackdropExposure = -1.8f;
    private const float RefractionZoom = 1.22f;  // edge band magnification
    private const float EdgeBandWidth = 15f;     // refraction band along the rim
    private const float RimStrokeWidth = 1.5f;

    private bool _active;
    private Brush? _inactiveBackground;
    private Vector2 _lastSourceOffset = new(float.NaN);

    private CanvasDevice? _device;
    private CompositionGraphicsDevice? _gfx;
    private SpriteVisual? _visual;
    private CompositionEffectBrush? _brush;
    private CompositionVisualSurface? _visualSurface;  // live capture feeding the refraction
    private CompositionDrawingSurface? _edgeSurface;   // refraction band alpha
    private CompositionDrawingSurface? _rimSurface;    // specular border + sheen

    private Vector2 _builtSize;
    private float _builtScale;
    private ElementTheme _builtTheme;
    private XamlRoot? _root;

    public LiquidGlassBackdrop()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, __) => Rebuild();
        ActualThemeChanged += (_, __) => Rebuild();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Element whose live rendering feeds the magnified edge-refraction band
    /// (typically the canvas the glass floats over). Backdrop brushes cannot be
    /// transformed by the compositor, so true refraction needs a
    /// CompositionVisualSurface capture of the content behind the panel. When
    /// null the edge band falls back to an untransformed, clearer backdrop.
    /// Must not be an ancestor of this control.
    /// </summary>
    public UIElement? RefractionSource { get; set; }

    /// <summary>Switches between the glass effect and the plain Background brush.</summary>
    public bool IsGlassActive
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (value)
            {
                // Keep the normal background visible until the effect is live so
                // the toolbar never flashes transparent.
                _inactiveBackground ??= Background;
                Rebuild();
            }
            else
            {
                ReleaseVisual();
                Background = _inactiveBackground;
            }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _root = XamlRoot;
        if (_root is not null) _root.Changed += OnRootChanged;   // DPI / monitor moves
        LayoutUpdated += OnLayoutUpdated;                        // tracks toolbar drags
        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_root is not null) { _root.Changed -= OnRootChanged; _root = null; }
        LayoutUpdated -= OnLayoutUpdated;
        // Loaded/Unloaded can arrive out of order; only tear down when truly unloaded.
        if (!IsLoaded) ReleaseVisual();
    }

    private void OnLayoutUpdated(object? sender, object e) => UpdateRefractionOffset();

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Rebuild();

    private void Rebuild()
    {
        if (!_active) return;
        float w = (float)ActualWidth, h = (float)ActualHeight;
        if (w < 4 || h < 4 || XamlRoot is null || !IsLoaded) return;

        float scale = (float)XamlRoot.RasterizationScale;
        var theme = ActualTheme;
        var size = new Vector2(w, h);
        if (_visual is not null && size == _builtSize && scale == _builtScale && theme == _builtTheme)
            return;

        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            EnsureDevice(compositor);
            DrawSurfaces(w, h, scale, theme);
            BuildBrush(compositor, w, h, theme);

            _visual ??= compositor.CreateSpriteVisual();
            _visual.Brush = _brush;
            _visual.Size = size;
            // Rounded-corner clip (a 5th mask parameter would exceed the
            // compositor's 4-source-parameter limit on effect graphs).
            var clipGeometry = compositor.CreateRoundedRectangleGeometry();
            clipGeometry.Size = size;
            float radius = MathF.Min((float)CornerRadius.TopLeft, MathF.Min(w, h) / 2f);
            clipGeometry.CornerRadius = new Vector2(radius, radius);
            _visual.Clip = compositor.CreateGeometricClip(clipGeometry);
            ElementCompositionPreview.SetElementChildVisual(this, _visual);

            _builtSize = size;
            _builtScale = scale;
            _builtTheme = theme;
            Background = null;   // effect is live; stop double-painting acrylic under it
        }
        catch (Exception ex)
        {
            // A composition/Win2D failure must never take the toolbar with it —
            // fall back to the plain background brush and stay there until the
            // next size/theme change retries.
            App.LogError(ex, "LiquidGlassBackdrop rebuild failed");
            ReleaseVisual();
            Background = _inactiveBackground;
        }
    }

    private void EnsureDevice(Compositor compositor)
    {
        if (_device is not null && _gfx is not null) return;
        _device = CanvasDevice.GetSharedDevice();
        _device.DeviceLost += OnDeviceLost;
        _gfx = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _device);
    }

    private void OnDeviceLost(CanvasDevice sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            sender.DeviceLost -= OnDeviceLost;
            DisposeSurfaces();
            _gfx?.Dispose(); _gfx = null;
            _device = null;
            _builtSize = default;   // force a full rebuild
            Rebuild();
        });
    }

    // ── Mask / rim surfaces (drawn once per size·scale·theme) ───────────────

    private void DrawSurfaces(float w, float h, float scale, ElementTheme theme)
    {
        bool dark = theme == ElementTheme.Dark;
        int pw = Math.Max(1, (int)MathF.Round(w * scale));
        int ph = Math.Max(1, (int)MathF.Round(h * scale));
        float r = MathF.Min((float)CornerRadius.TopLeft * scale, MathF.Min(pw, ph) / 2f);

        DisposeSurfaces();
        _edgeSurface = CreateSurface(pw, ph);
        _rimSurface = CreateSurface(pw, ph);

        // Refraction band: opaque at the rim, fading to 0 inland. Fill the pill,
        // then carve out a blurred interior so the falloff is smooth.
        float band = MathF.Min(EdgeBandWidth * scale, MathF.Min(pw, ph) / 3f);
        using (var ds = CanvasComposition.CreateDrawingSession(_edgeSurface))
        {
            ds.Clear(Colors.Transparent);
            byte edgeAlpha = dark ? (byte)190 : (byte)210;
            ds.FillRoundedRectangle(new Rect(0, 0, pw, ph), r, r, Color.FromArgb(edgeAlpha, 255, 255, 255));

            using var interior = new CanvasCommandList(ds);
            using (var ids = interior.CreateDrawingSession())
            {
                ids.FillRoundedRectangle(
                    new Rect(band, band, Math.Max(1, pw - 2 * band), Math.Max(1, ph - 2 * band)),
                    MathF.Max(1, r - band), MathF.Max(1, r - band), Colors.White);
            }
            using var blurredInterior = new GaussianBlurEffect { Source = interior, BlurAmount = band / 2.2f };
            ds.DrawImage(blurredInterior, 0, 0, new Rect(0, 0, pw, ph), 1f,
                         CanvasImageInterpolation.Linear, CanvasComposite.DestinationOut);
        }

        // Specular rim (gradient hairline, bright at the top) + soft top sheen —
        // the two "border layers" of the React component collapsed into one texture.
        using (var ds = CanvasComposition.CreateDrawingSession(_rimSurface))
        {
            ds.Clear(Colors.Transparent);
            float sw = RimStrokeWidth * scale;
            var rimRect = new Rect(sw / 2, sw / 2, pw - sw, ph - sw);
            float rimRadius = MathF.Max(1, r - sw / 2);
            byte hi = dark ? (byte)60 : (byte)110;
            byte mid = dark ? (byte)14 : (byte)28;
            byte lo = dark ? (byte)40 : (byte)70;
            var rimStops = new CanvasGradientStop[]
            {
                new() { Position = 0.00f, Color = Color.FromArgb(hi, 255, 255, 255) },
                new() { Position = 0.35f, Color = Color.FromArgb(mid, 255, 255, 255) },
                new() { Position = 0.70f, Color = Color.FromArgb(mid, 255, 255, 255) },
                new() { Position = 1.00f, Color = Color.FromArgb(lo, 255, 255, 255) },
            };
            using (var rimBrush = new CanvasLinearGradientBrush(ds, rimStops)
            { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(0, ph) })
            {
                ds.DrawRoundedRectangle(rimRect, rimRadius, rimRadius, rimBrush, sw);
            }

            var sheenStops = new CanvasGradientStop[]
            {
                new() { Position = 0f, Color = Color.FromArgb(dark ? (byte)10 : (byte)16, 255, 255, 255) },
                new() { Position = 1f, Color = Color.FromArgb(0, 255, 255, 255) },
            };
            using var sheenBrush = new CanvasLinearGradientBrush(ds, sheenStops)
            { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(0, ph * 0.5f) };
            ds.FillRoundedRectangle(new Rect(0, 0, pw, ph), r, r, sheenBrush);
        }
    }

    private CompositionDrawingSurface CreateSurface(int pw, int ph) =>
        _gfx!.CreateDrawingSurface(new Size(pw, ph),
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);

    // ── Effect graph ─────────────────────────────────────────────────────────

    private void BuildBrush(Compositor compositor, float w, float h, ElementTheme theme)
    {
        bool dark = theme == ElementTheme.Dark;
        Color tint = dark
            ? Color.FromArgb(0xB4, 0x1B, 0x1F, 0x28)
            : Color.FromArgb(0x78, 0xE9, 0xEB, 0xF2);

        // CreateEffectFactory only accepts tree-shaped graphs (no node reuse), so
        // the body and the refraction branch each get their own blur chain; both
        // bind to the same backdrop brush instance below.
        IGraphicsEffectSource bodyBackdrop = new GaussianBlurEffect
        {
            Source = new CompositionEffectSourceParameter("backdrop"),
            BlurAmount = BlurRadius,
            BorderMode = EffectBorderMode.Hard,
        };
        if (dark) bodyBackdrop = new ExposureEffect { Source = bodyBackdrop, Exposure = DarkBackdropExposure };

        var tinted = new CompositeEffect
        {
            Mode = CanvasComposite.SourceOver,
            Sources =
            {
                new SaturationEffect { Saturation = SaturationBoost, Source = bodyBackdrop },
                new ColorSourceEffect { Color = tint },
            },
        };

        // Edge refraction: the rim shows the content behind magnified toward the
        // panel center, masked to a band along the border. Less blur than the
        // body — a real glass rim bends light but stays clearer than the slab.
        // The magnification lives on the brush bound to "backdrop2" (a scaled
        // visual-surface brush), since the compositor rejects any Transform2D
        // over a backdrop source.
        IGraphicsEffectSource edgeBackdrop = new GaussianBlurEffect
        {
            Source = new CompositionEffectSourceParameter("backdrop2"),
            BlurAmount = BlurRadius * 0.45f,
            BorderMode = EffectBorderMode.Hard,
        };
        if (dark) edgeBackdrop = new ExposureEffect { Source = edgeBackdrop, Exposure = DarkBackdropExposure };

        var refracted = new SaturationEffect
        {
            Saturation = SaturationBoost,
            Source = edgeBackdrop,
        };
        var edge = new AlphaMaskEffect
        {
            Source = refracted,
            AlphaMask = new CompositionEffectSourceParameter("edge"),
        };

        // Corner rounding comes from the visual's geometric clip, not the graph —
        // the compositor allows at most four source parameters per graph.
        var glass = new CompositeEffect
        {
            Mode = CanvasComposite.SourceOver,
            Sources = { tinted, edge, new CompositionEffectSourceParameter("rim") },
        };

        var factory = compositor.CreateEffectFactory(glass);
        _brush?.Dispose();
        _brush = factory.CreateBrush();
        _brush.SetSourceParameter("backdrop", compositor.CreateBackdropBrush());
        _brush.SetSourceParameter("backdrop2", CreateRefractionBrush(compositor, w, h));
        _brush.SetSourceParameter("edge", CreateMaskBrush(compositor, _edgeSurface!));
        _brush.SetSourceParameter("rim", CreateMaskBrush(compositor, _rimSurface!));
    }

    /// <summary>
    /// Brush feeding the magnified edge band: a live visual-surface capture of
    /// <see cref="RefractionSource"/> aligned with this panel, zoomed toward the
    /// center. Without a source, an unzoomed backdrop (the band then just shows
    /// the backdrop with less blur — still glassy, no lens bend).
    /// </summary>
    private CompositionBrush CreateRefractionBrush(Compositor compositor, float w, float h)
    {
        _visualSurface?.Dispose(); _visualSurface = null;
        if (RefractionSource is null) return compositor.CreateBackdropBrush();

        _visualSurface = compositor.CreateVisualSurface();
        _visualSurface.SourceVisual = ElementCompositionPreview.GetElementVisual(RefractionSource);
        _visualSurface.SourceSize = new Vector2(w, h);
        _lastSourceOffset = new Vector2(float.NaN);
        UpdateRefractionOffset();

        var brush = compositor.CreateSurfaceBrush(_visualSurface);
        brush.Stretch = CompositionStretch.None;   // 1:1, centered by default ratios
        brush.CenterPoint = new Vector2(w / 2f, h / 2f);
        brush.Scale = new Vector2(RefractionZoom);
        return brush;
    }

    /// <summary>
    /// Keeps the captured region aligned with the panel as it is dragged or
    /// re-docked. Called from LayoutUpdated; the transform compare makes the
    /// no-change case (e.g. layout passes caused by unrelated elements) cheap.
    /// </summary>
    private void UpdateRefractionOffset()
    {
        if (_visualSurface is null || RefractionSource is null || !IsLoaded) return;
        var origin = TransformToVisual(RefractionSource).TransformPoint(new Point(0, 0));
        var offset = new Vector2((float)origin.X, (float)origin.Y);
        if (offset == _lastSourceOffset) return;
        _lastSourceOffset = offset;
        _visualSurface.SourceOffset = offset;
    }

    private static CompositionSurfaceBrush CreateMaskBrush(Compositor compositor, CompositionDrawingSurface surface)
    {
        var brush = compositor.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.Fill;
        return brush;
    }

    // ── Teardown ─────────────────────────────────────────────────────────────

    private void ReleaseVisual()
    {
        ElementCompositionPreview.SetElementChildVisual(this, null);
        _visual?.Dispose(); _visual = null;
        _brush?.Dispose(); _brush = null;
        _visualSurface?.Dispose(); _visualSurface = null;
        DisposeSurfaces();
        _builtSize = default;
    }

    private void DisposeSurfaces()
    {
        _edgeSurface?.Dispose(); _edgeSurface = null;
        _rimSurface?.Dispose(); _rimSurface = null;
    }
}
