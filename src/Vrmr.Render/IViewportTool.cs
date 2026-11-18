using System.Numerics;
using Avalonia.Input;

namespace VRMR.Render
{
    /// <summary>A pointer event as tools get it.</summary>
    public readonly struct ViewPointer
    {
        /// <summary>Position in the view, in device-independent pixels from the top-left.</summary>
        public readonly Vector2 Position;
        /// <summary>The button that went down or up; None for a move.</summary>
        public readonly MouseButton Button;
        public readonly KeyModifiers Modifiers;
        /// <summary>1 for a click, 2 for a double click (presses only).</summary>
        public readonly int ClickCount;

        public ViewPointer(Vector2 position, MouseButton button, KeyModifiers modifiers, int clickCount = 0)
        {
            Position = position; Button = button; Modifiers = modifiers; ClickCount = clickCount;
        }

        public bool Shift => (Modifiers & KeyModifiers.Shift) != 0;
        public bool Control => (Modifiers & KeyModifiers.Control) != 0;
        public bool Alt => (Modifiers & KeyModifiers.Alt) != 0;
    }

    /// <summary>
    /// An interactive tool in the 3D view (a posing gizmo, the placement handle). It gets pointer and key events before
    /// the camera does and draws over the scene. The view redraws after every event a tool handled; a tool that changes
    /// its picture at another time calls <see cref="ViewportControl.RequestFrame"/>.
    /// </summary>
    public interface IViewportTool
    {
        /// <summary>A button went down. Return true to take the drag: the camera then ignores it and the tool gets the moves and the release.</summary>
        bool PointerPressed(ViewportControl view, in ViewPointer e);

        /// <summary>The pointer moved: while hovering (no button down) and during a drag the tool took.</summary>
        void PointerMoved(ViewportControl view, in ViewPointer e);

        /// <summary>The drag the tool took ended.</summary>
        void PointerReleased(ViewportControl view, in ViewPointer e);

        /// <summary>A key went down while the view has the focus. Return true when the tool used it.</summary>
        bool KeyDown(ViewportControl view, Key key, KeyModifiers modifiers);

        /// <summary>Called for every drawn frame, after the scene and its labels.</summary>
        void Draw(ViewportControl view, OverlayList overlay);
    }
}
