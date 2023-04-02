using System;
using System.Linq;
using Artemis.Core;
using Artemis.Core.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FluentAvalonia.Core;
using Humanizer;
using Material.Icons;

namespace Artemis.UI.Shared;

/// <summary>
///     Represents a control that can be used to display or edit <see cref="Core.Hotkey" /> instances.
/// </summary>
public partial class HotkeyBox : UserControl
{
    /// <summary>
    ///     Creates a new instance of the <see cref="HotkeyBox" /> class
    /// </summary>
    public HotkeyBox()
    {
        InitializeComponent();
        PropertyChanged += OnPropertyChanged;
        DisplayTextBox.KeyDown += DisplayTextBoxOnKeyDown;
        DisplayTextBox.KeyUp += DisplayTextBoxOnKeyUp;
        UpdateDisplayTextBox();
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == HotkeyProperty)
            UpdateDisplayTextBox();
    }

    private void DisplayTextBoxOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key >= Key.LeftShift && e.Key <= Key.RightAlt)
            return;

        Hotkey ??= new Hotkey();
        Hotkey.Key = (KeyboardKey?) e.Key;
        Hotkey.Modifiers = (KeyboardModifierKey?) e.KeyModifiers;
        UpdateDisplayTextBox();
        HotkeyChanged?.Invoke(this, EventArgs.Empty);

        e.Handled = true;
    }

    private void DisplayTextBoxOnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.None)
            FocusManager.Instance?.Focus(null);

        e.Handled = true;
    }

    private void UpdateDisplayTextBox()
    {
        string? display = null;
        if (Hotkey?.Modifiers != null)
            display = string.Join("+", Enum.GetValues<KeyboardModifierKey>().Skip(1).Where(m => Hotkey.Modifiers.Value.HasFlag(m)).Select(v => v.Humanize()));
        if (Hotkey?.Key != null)
            display = string.IsNullOrEmpty(display) ? Hotkey.Key.ToString() : $"{display}+{Hotkey.Key}";

        DisplayTextBox.Text = display;
        DisplayTextBox.CaretIndex = DisplayTextBox.Text?.Length ?? 0;
    }

    private void Button_OnClick(object? sender, RoutedEventArgs e)
    {
        Hotkey = null;
        FocusManager.Instance?.Focus(null);

        UpdateDisplayTextBox();
    }

    #region Properties

    /// <summary>
    ///     Gets or sets the currently displayed icon as either a <see cref="MaterialIconKind" /> or an <see cref="Uri" />
    ///     pointing to an SVG
    /// </summary>
    public static readonly StyledProperty<Hotkey?> HotkeyProperty = AvaloniaProperty.Register<HotkeyBox, Hotkey?>(nameof(Hotkey), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    ///     Gets or sets the watermark of the hotkey box when it is empty.
    /// </summary>
    public static readonly StyledProperty<string?> WatermarkProperty = AvaloniaProperty.Register<HotkeyBox, string?>(nameof(Watermark));

    /// <summary>
    ///     Gets or sets a boolean indicating whether the watermark should float above the hotkey box when it is not empty.
    /// </summary>
    public static readonly StyledProperty<bool> UseFloatingWatermarkProperty = AvaloniaProperty.Register<HotkeyBox, bool>(nameof(UseFloatingWatermark));

    /// <summary>
    ///     Gets or sets the currently displayed icon as either a <see cref="MaterialIconKind" /> or an <see cref="Uri" />
    ///     pointing to an SVG
    /// </summary>
    public Hotkey? Hotkey
    {
        get => GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    /// <summary>
    ///     Gets or sets the watermark of the hotkey box when it is empty.
    /// </summary>
    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    /// <summary>
    ///     Gets or sets a boolean indicating whether the watermark should float above the hotkey box when it is not empty.
    /// </summary>
    public bool UseFloatingWatermark
    {
        get => GetValue(UseFloatingWatermarkProperty);
        set => SetValue(UseFloatingWatermarkProperty, value);
    }

    #endregion

    #region Events

    /// <summary>
    ///     Occurs when the hotkey changes.
    /// </summary>
    public event TypedEventHandler<HotkeyBox, EventArgs>? HotkeyChanged;

    #endregion
}