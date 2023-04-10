using System;
using Artemis.UI.Shared.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;

namespace Artemis.UI.DefaultTypes.PropertyInput;

public partial class SKSizePropertyInputView : ReactiveUserControl<SKSizePropertyInputViewModel>
{
    public SKSizePropertyInputView()
    {
        InitializeComponent();
    }


    private void DraggableNumberBox_OnDragStarted(DraggableNumberBox sender, EventArgs args)
    {
        ViewModel?.StartPreview();
    }

    private void DraggableNumberBox_OnDragFinished(DraggableNumberBox sender, EventArgs args)
    {
        ViewModel?.ApplyPreview();
    }
}