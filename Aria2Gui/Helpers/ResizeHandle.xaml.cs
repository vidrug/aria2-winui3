using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Aria2Gui.Helpers;

/// <summary>
/// Draggable column divider with a horizontal-resize cursor. Wraps a Thumb
/// (which is sealed in WinUI 3, so the cursor lives on this UserControl).
/// </summary>
public sealed partial class ResizeHandle : UserControl
{
    /// <summary>Raised during drag with the horizontal delta in pixels.</summary>
    public event EventHandler<double>? Resize;

    public ResizeHandle()
    {
        InitializeComponent();
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }

    private void InnerThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        Resize?.Invoke(this, e.HorizontalChange);
}
