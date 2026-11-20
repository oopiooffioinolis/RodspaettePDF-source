using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Rodspaette.App;

/// <summary>
/// Opening animation, under 2 s, over the whole window on plaice yellow:
/// the eyes fade in, blink once, the body fades in around them, then the yellow fades away.
/// The fish is drawn at exactly the size and place of the start-page logo's fish from the first frame and stays
/// solid while only the yellow fades, so nothing moves or doubles when the start page appears. A click skips it; it is skipped when Windows animations are off.
/// </summary>
public static class Splash
{
    private const double FallbackSize = 220;  // when the start page isn't laid out (a PDF is opening)
    private const double EyeCentreY = 0.156;  // eye line, as a fraction of the layer height

    /// <summary>Returns false when the animation is skipped. <paramref name="done"/> runs once the yellow has gone.</summary>
    public static bool Attach(Window window, Grid root, FrameworkElement startLogo, Action done)
    {
        if (!SystemParameters.ClientAreaAnimation) return false;

        SolidColorBrush yellow = new(Color.FromRgb(0xFF, 0xD1, 0x14));
        Grid overlay = new() { Background = yellow, Cursor = Cursors.Arrow };
        Grid.SetRowSpan(overlay, Math.Max(1, root.RowDefinitions.Count));
        Grid.SetColumnSpan(overlay, Math.Max(1, root.ColumnDefinitions.Count));
        Panel.SetZIndex(overlay, 10000);

        ScaleTransform blink = new(1, 1);
        Image body = Layer("splash-body.png");
        Image eyes = Layer("splash-eyes.png");
        eyes.RenderTransformOrigin = new Point(0.5, EyeCentreY);
        eyes.RenderTransform = blink;
        Grid fish = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Width = FallbackSize, Height = FallbackSize };
        fish.Children.Add(body);
        fish.Children.Add(eyes);
        overlay.Children.Add(fish);

        root.Children.Add(overlay);

        // keep the fish exactly over the start-page logo (same box, same drawing frame) on every layout pass
        void Place(object? s, EventArgs e)
        {
            if (overlay.ActualWidth <= 0) return;
            Rect r;
            if (startLogo.IsVisible && startLogo.ActualWidth > 0)
                r = startLogo.TransformToVisual(overlay).TransformBounds(new Rect(startLogo.RenderSize));
            else
                r = new Rect((overlay.ActualWidth - FallbackSize) / 2, (overlay.ActualHeight - FallbackSize) / 2, FallbackSize, FallbackSize);
            if (Math.Abs(fish.Width - r.Width) > 0.01) { fish.Width = r.Width; fish.Height = r.Height; }
            var m = new Thickness(r.X, r.Y, 0, 0);
            if (fish.Margin != m) fish.Margin = m;

        }
        overlay.LayoutUpdated += Place;

        bool finished = false;
        void Finish()
        {
            if (finished) return;
            finished = true;
            overlay.LayoutUpdated -= Place;
            root.Children.Remove(overlay);
            done();
        }

        void Start()
        {
            // 0.00–0.35 s  eyes fade in
            eyes.BeginAnimation(UIElement.OpacityProperty, Fade(0.00, 0.35));

            // 0.55–0.78 s  one blink
            var b = new DoubleAnimationUsingKeyFrames();
            b.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.55))));
            b.KeyFrames.Add(new EasingDoubleKeyFrame(0.08, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.64)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            b.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.78)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            blink.BeginAnimation(ScaleTransform.ScaleYProperty, b);

            // 0.85–1.45 s  body fades in
            body.BeginAnimation(UIElement.OpacityProperty, Fade(0.85, 0.60));

            // 1.45–1.95 s  only the yellow fades; the fish stays put and solid over the start-page fish
            var fadeYellow = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.50))
            {
                BeginTime = TimeSpan.FromSeconds(1.45),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            fadeYellow.Completed += (_, _) => Finish();
            yellow.BeginAnimation(Brush.OpacityProperty, fadeYellow);
        }

        overlay.MouseLeftButtonDown += (_, e) => { e.Handled = true; Finish(); };

        // start once the first frame is on screen, so no part of the 2 s is spent invisible
        void OnRendered(object? s, EventArgs e) { window.ContentRendered -= OnRendered; Start(); }
        window.ContentRendered += OnRendered;
        return true;
    }

    // ---------------------------------------------------------------- no internet
    // lost-body.png / lost-eyes.png: the logo's fish layers (as splash-body/-eyes, same frame) drawn at 3072 px and cut to
    // the part shown here, x 0.25–0.75 and y 0.08–0.60 of the frame, so the very large head stays sharp.
    private const double CropX = 0.25, CropY = 0.08, CropW = 0.50, CropH = 0.52;
    private const double FishTop = 0.11, FishMiddle = 0.4975;   // the mouth and the middle of the body, as fractions of the frame
    private static bool _lostShowing;

    /// <summary>
    /// Look up was asked for with no internet: the yellow comes back and a very large plaice rises from the bottom (its
    /// top half, up to the middle of its body, fills most of the window), blinks once under "Connection Lost", then
    /// everything fades away like the opening animation. A click skips it. False when Windows animations are off.
    /// </summary>
    public static bool ConnectionLost(Grid root)
    {
        if (!SystemParameters.ClientAreaAnimation) return false;
        if (_lostShowing) return true;
        _lostShowing = true;

        Grid overlay = new() { Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x14)), Cursor = Cursors.Arrow, ClipToBounds = true, Opacity = 0 };
        Grid.SetRowSpan(overlay, Math.Max(1, root.RowDefinitions.Count));
        Grid.SetColumnSpan(overlay, Math.Max(1, root.ColumnDefinitions.Count));
        Panel.SetZIndex(overlay, 10000);

        ScaleTransform blink = new(1, 1);
        TranslateTransform rise = new();
        Image body = Layer("lost-body.png"), eyes = Layer("lost-eyes.png");
        body.Opacity = eyes.Opacity = 1;
        eyes.RenderTransformOrigin = new Point(0.5, (EyeCentreY - CropY) / CropH);
        eyes.RenderTransform = blink;
        Grid fish = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, RenderTransform = rise };
        fish.Children.Add(body);
        fish.Children.Add(eyes);
        TextBlock title = new()
        {
            Text = "Connection Lost",
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3D, 0x2B, 0x00)),   // the dark brown of the fish's eyes
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0,
            RenderTransform = new TranslateTransform(0, -12),
        };
        if (Application.Current.TryFindResource("UiFont") is FontFamily ui) title.FontFamily = ui;
        overlay.Children.Add(fish);
        overlay.Children.Add(title);
        root.Children.Add(overlay);

        double frame = 0;   // the size of the whole (uncut) fish frame, in pixels of the window
        bool started = false, finished = false;

        void Finish()
        {
            if (finished) return;
            finished = true;
            root.Children.Remove(overlay);
            _lostShowing = false;
        }

        void Size(object? sender, SizeChangedEventArgs e)
        {
            double w = overlay.ActualWidth, h = overlay.ActualHeight;
            if (w <= 0 || h <= 0) return;
            // the head, from the mouth to the middle of the body, fills 70% of the height (narrow windows: what fits)
            frame = Math.Min(0.70 * h / (FishMiddle - FishTop), 0.95 * w / 0.445);
            fish.Width = CropW * frame;
            fish.Height = CropH * frame;
            fish.Margin = new Thickness(0, h - (FishMiddle - CropY) * frame, 0, 0);   // the middle of the body on the bottom edge
            double space = h - (FishMiddle - FishTop) * frame;                         // the yellow above the mouth
            title.FontSize = Math.Clamp(space * 0.30, 22, 76);
            title.Margin = new Thickness(0, Math.Max(12, space / 2 - title.FontSize * 0.7), 0, 0);
            if (!started) { started = true; Start(); }
        }

        void Start()
        {
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            // 0.00–0.20 s  the yellow comes back; 1.75–2.25 s  everything fades away, like the end of the opening animation
            var show = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.25) };
            show.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            show.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.20)), ease));
            show.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.75))));
            show.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.25)), ease));
            show.Completed += (_, _) => Finish();
            overlay.BeginAnimation(UIElement.OpacityProperty, show);

            // 0.10–0.85 s  the fish rises from just below the window
            rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation((FishMiddle - CropY) * frame, 0, TimeSpan.FromSeconds(0.75))
            {
                BeginTime = TimeSpan.FromSeconds(0.10),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

            // 0.60–0.90 s  "Connection Lost" settles in above it
            title.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.30)) { BeginTime = TimeSpan.FromSeconds(0.60), EasingFunction = ease });
            ((TranslateTransform)title.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-12, 0, TimeSpan.FromSeconds(0.30)) { BeginTime = TimeSpan.FromSeconds(0.60), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            // 1.10–1.33 s  one blink, as in the opening animation
            var b = new DoubleAnimationUsingKeyFrames();
            b.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.10))));
            b.KeyFrames.Add(new EasingDoubleKeyFrame(0.08, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.19)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            b.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.33)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            blink.BeginAnimation(ScaleTransform.ScaleYProperty, b);
        }

        overlay.SizeChanged += Size;
        overlay.MouseLeftButtonDown += (_, e) => { e.Handled = true; Finish(); };
        return true;
    }

    private static Image Layer(string file)
    {
        var img = new Image
        {
            Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/{file}")),
            Stretch = Stretch.Fill,
            Opacity = 0,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    private static DoubleAnimation Fade(double begin, double duration) => new(0, 1, TimeSpan.FromSeconds(duration))
    {
        BeginTime = TimeSpan.FromSeconds(begin),
        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        FillBehavior = FillBehavior.HoldEnd,
    };
}
