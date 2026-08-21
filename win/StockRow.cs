using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FloatQuote;

public sealed class StockRow : Border
{
    public const double RowH = 17;

    readonly AnimatedText _name;
    readonly AnimatedText _price;
    readonly AnimatedText _change;
    bool _hovered;
    bool _highlight;

    public string? Code { get; private set; }
    public AnimatedText NameLabel => _name;

    public StockRow()
    {
        Height = RowH;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        CornerRadius = new CornerRadius(3);
        Padding = new Thickness(4, 0, 4, 0);

        _name = new AnimatedText(11, FontWeights.Bold, new SolidColorBrush(Theme.TextMain), 66);
        _price = new AnimatedText(11.5, FontWeights.Bold, new SolidColorBrush(Theme.TextMain));
        _change = new AnimatedText(11, FontWeights.Bold, new SolidColorBrush(Theme.TextMain));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_name, 0);
        Grid.SetColumn(_price, 2);
        Grid.SetColumn(_change, 3);
        _change.Margin = new Thickness(3, 0, 0, 0);
        grid.Children.Add(_name);
        grid.Children.Add(_price);
        grid.Children.Add(_change);
        Child = grid;

        MouseEnter += (_, _) => { _hovered = true; UpdateBg(); };
        MouseLeave += (_, _) => { _hovered = false; UpdateBg(); };
    }

    public void SetHighlight(bool on)
    {
        if (_highlight == on) return;
        _highlight = on;
        UpdateBg();
    }

    public void SetStock(string code, string name, string price, string change, Brush style)
    {
        Code = code;
        _name.SetText(name);
        _price.SetText(price, style);
        _change.SetText(change, style);
    }

    public void Play(string effect)
    {
        _name.Play(effect, -1);
        _price.Play(effect, 1);
        _change.Play(effect, 1);
    }

    public void Clear()
    {
        Code = null;
        _name.SetText("");
        _price.SetText("");
        _change.SetText("");
    }

    void UpdateBg()
    {
        if (_highlight)
            Background = new SolidColorBrush(Theme.HighlightBg);
        else if (_hovered)
            Background = new SolidColorBrush(Theme.HoverBg);
        else
            Background = Brushes.Transparent;
    }
}
