using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FloatQuote;

public sealed class AnimatedText : UserControl
{
    readonly TextBlock _label;
    readonly TranslateTransform _tx = new();

    public AnimatedText(double fontSize, FontWeight weight, Brush color, double? maxWidth = null)
    {
        IsHitTestVisible = false;
        _label = new TextBlock
        {
            Text = "--",
            FontFamily = new FontFamily(Theme.UiFont),
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = color,
            RenderTransform = _tx,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = fontSize + 4,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (maxWidth is not null)
            _label.MaxWidth = maxWidth.Value;
        Content = _label;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public string Text => _label.Text;

    public void SetText(string text, Brush? color = null)
    {
        _label.Text = text;
        if (color is not null)
            _label.Foreground = color;
    }

    public void Reset()
    {
        _tx.BeginAnimation(TranslateTransform.XProperty, null);
        _tx.BeginAnimation(TranslateTransform.YProperty, null);
        BeginAnimation(OpacityProperty, null);
        _tx.X = 0;
        _tx.Y = 0;
        Opacity = 1;
    }

    public void Play(string effect, int direction = 1)
    {
        if (effect == "off")
        {
            Reset();
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (effect == "fade")
        {
            _tx.X = 0;
            _tx.Y = 0;
            var a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease };
            BeginAnimation(OpacityProperty, a);
        }
        else if (effect is "slide_h" or "slide_v")
        {
            var dx = effect == "slide_h" ? direction * 18.0 : 0;
            var dy = effect == "slide_v" ? direction * 14.0 : 0;
            _tx.X = dx;
            _tx.Y = dy;
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
            _tx.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
            _tx.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        }
        else if (effect == "pulse")
        {
            _tx.X = 0;
            _tx.Y = 0;
            var kf = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.35)));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(0.5, KeyTime.FromPercent(0.65)));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(1)));
            BeginAnimation(OpacityProperty, kf);
        }
        else
        {
            Reset();
        }
    }
}
