using System.Runtime.InteropServices;

namespace WiFiCameraLink;

/// <summary>
/// Centres the text of a single-line text box, or of an editable dropdown, vertically. Windows always draws
/// that text at the top of the box, so when the box is taller than a line this moves the space above and below
/// the line into the box's non-client area and paints it in the box's colour. It also indents the text to line
/// up with the owner-drawn dropdowns.
/// </summary>
sealed class EditCentering : NativeWindow
{
    const int WM_PAINT = 0x000F, WM_SETFONT = 0x0030, WM_NCCALCSIZE = 0x0083, WM_NCPAINT = 0x0085;
    const int EM_SETMARGINS = 0x00D3, EM_GETMARGINS = 0x00D4, EC_LEFTMARGIN = 0x1;
    readonly Control _owner; // the TextBox or ComboBox, for its font and colours
    readonly string _placeholder;
    int _pad;

    EditCentering(Control owner, string placeholder) => (_owner, _placeholder) = (owner, placeholder);

    /// <summary>
    /// WinForms draws a text box's placeholder at the left edge, ignoring the margin, so this takes the
    /// placeholder over and draws it in line with the typed text.
    /// </summary>
    public static void Apply(TextBox box)
    {
        string placeholder = box.PlaceholderText;
        box.PlaceholderText = "";
        // The placeholder only shows while the box is empty and unfocused.
        box.GotFocus += (_, _) => box.Invalidate();
        box.LostFocus += (_, _) => box.Invalidate();
        Attach(box, () => box.Handle, placeholder);
    }

    public static void Apply(ComboBox combo) => Attach(combo, () =>
    {
        var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf<COMBOBOXINFO>() };
        return GetComboBoxInfo(combo.Handle, ref info) ? info.hwndItem : IntPtr.Zero;
    }, "");

    /// <summary>Subclasses the edit window now and again whenever the control recreates its handle.</summary>
    static void Attach(Control owner, Func<IntPtr> edit, string placeholder)
    {
        void Hook()
        {
            IntPtr hwnd = edit();
            if (hwnd == IntPtr.Zero)
                return;
            var centering = new EditCentering(owner, placeholder);
            centering.AssignHandle(hwnd);
            centering.Indent();
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        owner.HandleCreated += (_, _) => Hook();
        owner.EnabledChanged += (_, _) =>
        {
            if (owner.IsHandleCreated && edit() is var hwnd && hwnd != IntPtr.Zero)
                RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_FRAME | RDW_INVALIDATE);
        };
        if (owner.IsHandleCreated)
            Hook();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_NCCALCSIZE)
        {
            // With wParam set, the first of NCCALCSIZE_PARAMS' rectangles is the new client area; either way
            // lParam points at the rectangle to shrink.
            var rect = Marshal.PtrToStructure<RECT>(m.LParam);
            _pad = Math.Max(0, (rect.Bottom - rect.Top - _owner.Font.Height) / 2);
            rect.Top += _pad;
            rect.Bottom -= _pad;
            Marshal.StructureToPtr(rect, m.LParam, false);
        }
        else if (m.Msg == WM_NCPAINT && _pad > 0)
            PaintPadding();
        else if (m.Msg == WM_SETFONT) // a new font resets the margins
            Indent();
        else if (m.Msg == WM_PAINT && _placeholder.Length > 0 && _owner.Text.Length == 0 && !_owner.Focused)
            PaintPlaceholder();
    }

    void PaintPlaceholder()
    {
        int left = (int)SendMessage(Handle, EM_GETMARGINS, IntPtr.Zero, IntPtr.Zero) & 0xFFFF;
        GetClientRect(Handle, out var client);
        using var g = Graphics.FromHwnd(Handle);
        TextRenderer.DrawText(g, _placeholder, _owner.Font, new Rectangle(left, 0, client.Right - left, client.Bottom),
            SystemColors.GrayText, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>
    /// Widens the left margin so typed text starts where the owner-drawn dropdowns start theirs
    /// (MainForm.DrawComboItem's 2 pixels plus TextRenderer's own padding), about 5 pixels further in.
    /// </summary>
    void Indent()
    {
        int left = (int)SendMessage(Handle, EM_GETMARGINS, IntPtr.Zero, IntPtr.Zero) & 0xFFFF;
        left += 5 * _owner.DeviceDpi / 96;
        SendMessage(Handle, EM_SETMARGINS, (IntPtr)EC_LEFTMARGIN, (IntPtr)left);
    }

    void PaintPadding()
    {
        GetWindowRect(Handle, out var window);
        GetClientRect(Handle, out var client);
        var origin = new POINT();
        ClientToScreen(Handle, ref origin);
        int left = origin.X - window.Left, top = origin.Y - window.Top;

        IntPtr dc = GetWindowDC(Handle);
        try
        {
            using var g = Graphics.FromHdc(dc);
            using var brush = new SolidBrush(_owner.Enabled ? _owner.BackColor : SystemColors.Control);
            g.FillRectangle(brush, left, top - _pad, client.Right, _pad);
            g.FillRectangle(brush, left, top + client.Bottom, client.Right, _pad);
        }
        finally
        {
            ReleaseDC(Handle, dc);
        }
    }

    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    const uint RDW_INVALIDATE = 0x1, RDW_FRAME = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct COMBOBOXINFO
    {
        public int cbSize;
        public RECT rcItem, rcButton;
        public int stateButton;
        public IntPtr hwndCombo, hwndItem, hwndList;
    }

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool GetComboBoxInfo(IntPtr hwnd, ref COMBOBOXINFO info);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
