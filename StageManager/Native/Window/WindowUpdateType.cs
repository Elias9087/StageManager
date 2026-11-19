namespace StageManager.Native.Window
{
    public enum WindowUpdateType
    {
        Show,
        Hide,
        MinimizeStart,
        MinimizeEnd,
        Foreground,
        MoveStart,
        MoveEnd,
        Move,

        /// <summary>
        /// The position or size of a window changed, for every tracked window and every reason: dragging, snapping with the keyboard,
        /// Snap Assist or a snapped neighbour that was resized along with it.
        /// </summary>
        LocationChanged,
    }
}
