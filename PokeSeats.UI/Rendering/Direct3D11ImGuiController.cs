using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace PokeSeats.UI.Rendering;

/// <summary>
/// Projects Silk.NET input events into Dear ImGui and owns the Direct3D 11 ImGui context.
/// The window and input context are owned by the application; this type only subscribes to them.
/// </summary>
internal sealed unsafe class Direct3D11ImGuiController : IUiRenderer
{
    private readonly IWindow _window;
    private readonly IInputContext _input;
    private readonly IntPtr _context;
    private readonly Direct3D11Graphics _graphics;

    private IKeyboard? _keyboard;
    private IMouse? _mouse;
    private bool _frameBegun;
    private bool _focused = true;
    private bool _disposed;

    // Characters arrive between frames; ImGui consumes them when the next frame starts.
    private readonly List<char> _pendingChars = new(32);

    private ImGuiMouseCursor _appliedMouseCursor = ImGuiMouseCursor.COUNT;
    private bool _cursorStateKnown;

    // ImGui's platform clipboard callbacks (v1.91 take only the context pointer).
    private readonly GetClipboardTextDelegate _getClipboardText;
    private readonly SetClipboardTextDelegate _setClipboardText;
    private IntPtr _originalGetClipboardTextFn;
    private IntPtr _originalSetClipboardTextFn;
    private bool _clipboardCallbacksInstalled;
    private IntPtr _clipboardTextBuffer;
    private int _clipboardTextCapacity;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetClipboardTextDelegate(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetClipboardTextDelegate(IntPtr context, IntPtr text);

    internal Direct3D11ImGuiController(IWindow window, IInputContext input, Action configureIo)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _getClipboardText = OnGetClipboardText;
        _setClipboardText = OnSetClipboardText;

        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);
        ImGui.StyleColorsDark();

        Direct3D11Graphics? graphics = null;
        try
        {
            ImGuiIOPtr io = ImGui.GetIO();
            configureIo?.Invoke();

            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
            io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;

            // The graphics object builds the device resources and uploads the initial font atlas.
            graphics = new Direct3D11Graphics(window);
            _graphics = graphics;

            // No frame is started here. FontAtlasScaling rebuilds the atlas between frames,
            // so leave it unlocked for the next Build().
            ImFontAtlasPtr atlas = io.Fonts;
            atlas.Locked = false;

            InstallClipboardCallbacks();
            SubscribeInputEvents();
            _window.FocusChanged += OnWindowFocusChanged;

            if (_mouse is null)
                QueueMouseOutside();
            SyncModifiers();
        }
        catch
        {
            UnsubscribeInputEvents();
            _window.FocusChanged -= OnWindowFocusChanged;
            RestoreClipboardCallbacks();
            graphics?.Dispose();
            FreeClipboardBuffer();
            ImGui.DestroyContext(_context);
            throw;
        }
    }

    public void Update(float delta)
    {
        EnsureContext();

        ImGuiIOPtr io = ImGui.GetIO();
        Vector2D<int> windowSize = _window.Size;
        Vector2D<int> framebufferSize = _window.FramebufferSize;

        // ImGui works in window coordinates; the renderer scales scissors/clips through
        // DisplayFramebufferScale, which is where per-monitor DPI shows up.
        io.DisplaySize = new Vector2(windowSize.X, windowSize.Y);
        io.DisplayFramebufferScale = windowSize.X > 0 && windowSize.Y > 0
            ? new Vector2(
                framebufferSize.X / (float)windowSize.X,
                framebufferSize.Y / (float)windowSize.Y)
            : Vector2.One;
        io.DeltaTime = delta > 0f ? delta : 1f / 60f;

        if (_frameBegun)
        {
            // Update was called twice without Render; close the abandoned frame cleanly.
            _frameBegun = false;
            ImGui.Render();
        }

        FlushPendingCharacters();
        SyncModifiers();
        PollMousePosition();

        ImGui.NewFrame();
        _frameBegun = true;
    }

    public void Render()
    {
        if (!_frameBegun)
            return;

        EnsureContext();
        _frameBegun = false;
        ImGui.Render();

        // GetMouseCursor() still holds this frame's request until the next NewFrame().
        UpdateMouseCursor();
        _graphics.Render(ImGui.GetDrawData());
    }

    public void Resize(Vector2D<int> size)
    {
        if (size.X > 0 && size.Y > 0)
            _graphics.Resize(size);
    }

    public void UpdateFontTexture()
    {
        if (_frameBegun)
            throw new InvalidOperationException("The ImGui font atlas can only be updated between frames.");

        EnsureContext();
        _graphics.UpdateFontTexture();
    }

    public IntPtr CreateTexture(ReadOnlySpan<byte> rgba, int width, int height)
    {
        EnsureContext();
        return _graphics.CreateTexture(rgba, width, height);
    }

    public void DeleteTexture(IntPtr texture)
    {
        if (_disposed)
            return;
        EnsureContext();
        _graphics.DeleteTexture(texture);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _window.FocusChanged -= OnWindowFocusChanged;
        UnsubscribeInputEvents();

        EnsureContext();
        if (_frameBegun)
        {
            _frameBegun = false;
            ImGui.EndFrame();
        }

        RestoreClipboardCallbacks();
        _graphics.Dispose();
        ImGui.DestroyContext(_context);
        FreeClipboardBuffer();
    }

    private void EnsureContext()
    {
        if (ImGui.GetCurrentContext() != _context)
            ImGui.SetCurrentContext(_context);
    }

    private void SubscribeInputEvents()
    {
        _input.ConnectionChanged += OnConnectionChanged;

        if (_input.Keyboards.Count > 0)
            AttachKeyboard(_input.Keyboards[0]);
        if (_input.Mice.Count > 0)
            AttachMouse(_input.Mice[0]);
    }

    private void UnsubscribeInputEvents()
    {
        _input.ConnectionChanged -= OnConnectionChanged;
        DetachKeyboard();
        DetachMouse();
    }

    private void OnConnectionChanged(IInputDevice device, bool connected)
    {
        if (device is IKeyboard keyboard)
        {
            if (connected)
            {
                if (_keyboard is null || !_keyboard.IsConnected)
                    AttachKeyboard(keyboard);
            }
            else if (ReferenceEquals(_keyboard, keyboard))
            {
                DetachKeyboard();
                AttachFirstConnectedKeyboard();
            }
        }
        else if (device is IMouse mouse)
        {
            if (connected)
            {
                if (_mouse is null || !_mouse.IsConnected)
                    AttachMouse(mouse);
            }
            else if (ReferenceEquals(_mouse, mouse))
            {
                DetachMouse();
                AttachFirstConnectedMouse();
            }
        }
    }

    private void AttachFirstConnectedKeyboard()
    {
        for (int index = 0; index < _input.Keyboards.Count; index++)
        {
            IKeyboard keyboard = _input.Keyboards[index];
            if (keyboard.IsConnected)
            {
                AttachKeyboard(keyboard);
                return;
            }
        }
    }

    private void AttachFirstConnectedMouse()
    {
        for (int index = 0; index < _input.Mice.Count; index++)
        {
            IMouse mouse = _input.Mice[index];
            if (mouse.IsConnected)
            {
                AttachMouse(mouse);
                return;
            }
        }
    }

    private void AttachKeyboard(IKeyboard keyboard)
    {
        if (ReferenceEquals(_keyboard, keyboard))
            return;

        DetachKeyboard();
        _keyboard = keyboard;
        keyboard.KeyDown += OnKeyDown;
        keyboard.KeyUp += OnKeyUp;
        keyboard.KeyChar += OnKeyChar;
        SyncModifiers();
    }

    private void DetachKeyboard()
    {
        IKeyboard? keyboard = _keyboard;
        if (keyboard is null)
            return;

        keyboard.KeyDown -= OnKeyDown;
        keyboard.KeyUp -= OnKeyUp;
        keyboard.KeyChar -= OnKeyChar;
        _keyboard = null;
    }

    private void AttachMouse(IMouse mouse)
    {
        if (ReferenceEquals(_mouse, mouse))
            return;

        DetachMouse();
        _mouse = mouse;
        mouse.MouseMove += OnMouseMove;
        mouse.MouseDown += OnMouseDown;
        mouse.MouseUp += OnMouseUp;
        mouse.Scroll += OnScroll;
        _cursorStateKnown = false;
        if (mouse.IsConnected)
            QueueMousePosition();
    }

    private void DetachMouse()
    {
        IMouse? mouse = _mouse;
        if (mouse is null)
            return;

        mouse.MouseMove -= OnMouseMove;
        mouse.MouseDown -= OnMouseDown;
        mouse.MouseUp -= OnMouseUp;
        mouse.Scroll -= OnScroll;
        _mouse = null;
    }

    private void OnWindowFocusChanged(bool focused)
    {
        EnsureContext();
        _focused = focused;
        _cursorStateKnown = false;

        ImGuiIOPtr io = ImGui.GetIO();
        io.AddFocusEvent(focused);

        if (focused)
        {
            QueueMousePosition();
            SyncModifiers();
        }
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        SyncModifiers();
        AddKeyEvent(key, scancode, down: true);
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int scancode)
    {
        SyncModifiers();
        AddKeyEvent(key, scancode, down: false);
    }

    private void OnKeyChar(IKeyboard keyboard, char character)
    {
        // Characters arrive between frames; UTF-16 surrogate pairs are recombined on flush.
        if (_pendingChars.Count >= 256)
            _pendingChars.RemoveAt(0);
        _pendingChars.Add(character);
    }

    private void AddKeyEvent(Key key, int scancode, bool down)
    {
        ImGuiKey imguiKey = TranslateKey(key);
        if (imguiKey == ImGuiKey.None)
            return;

        EnsureContext();
        ImGuiIOPtr io = ImGui.GetIO();
        io.AddKeyEvent(imguiKey, down);
        io.SetKeyEventNativeData(imguiKey, (int)key, scancode);
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        EnsureContext();
        ImGui.GetIO().AddMousePosEvent(position.X, position.Y);
    }

    private void OnMouseDown(IMouse mouse, MouseButton button) => AddMouseButton(button, down: true);

    private void OnMouseUp(IMouse mouse, MouseButton button) => AddMouseButton(button, down: false);

    private void AddMouseButton(MouseButton button, bool down)
    {
        int index = TranslateMouseButton(button);
        if (index < 0)
            return;

        SyncModifiers();
        ImGui.GetIO().AddMouseButtonEvent(index, down);
    }

    private void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        EnsureContext();
        ImGui.GetIO().AddMouseWheelEvent(wheel.X, wheel.Y);
    }

    private void FlushPendingCharacters()
    {
        if (_pendingChars.Count == 0)
            return;

        ImGuiIOPtr io = ImGui.GetIO();
        for (int index = 0; index < _pendingChars.Count; index++)
            io.AddInputCharacterUTF16(_pendingChars[index]);

        _pendingChars.Clear();
    }

    private void SyncModifiers()
    {
        EnsureContext();
        IKeyboard? keyboard = _keyboard;
        bool ctrl = false;
        bool shift = false;
        bool alt = false;
        bool super = false;

        if (keyboard is not null && keyboard.IsConnected)
        {
            ctrl = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
            shift = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);
            alt = keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight);
            super = keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight);
        }

        // ImGui 1.91 derives io.KeyCtrl/KeyShift/KeyAlt/KeySuper from these ImGuiMod
        // key states. Left/right physical keys alone do not update the modifier state.
        ImGuiIOPtr io = ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.ModCtrl, ctrl);
        io.AddKeyEvent(ImGuiKey.ModShift, shift);
        io.AddKeyEvent(ImGuiKey.ModAlt, alt);
        io.AddKeyEvent(ImGuiKey.ModSuper, super);
    }

    private void PollMousePosition()
    {
        IMouse? mouse = _mouse;
        if (!_focused || mouse is null || !mouse.IsConnected)
            return;

        // Covers gaps where the input backend does not raise a move event
        // (focus changes, cursor warps). AddMousePosEvent filters duplicates.
        Vector2 position = mouse.Position;
        ImGui.GetIO().AddMousePosEvent(position.X, position.Y);
    }

    private void QueueMousePosition()
    {
        IMouse? mouse = _mouse;
        if (mouse is null)
        {
            QueueMouseOutside();
            return;
        }

        EnsureContext();
        Vector2 position = mouse.Position;
        ImGui.GetIO().AddMousePosEvent(position.X, position.Y);
    }

    private void QueueMouseOutside()
    {
        EnsureContext();
        // ImGui's own Win32 backend uses -FLT_MAX to mean "mouse outside the window".
        ImGui.GetIO().AddMousePosEvent(-float.MaxValue, -float.MaxValue);
    }

    private void UpdateMouseCursor()
    {
        IMouse? mouse = _mouse;
        if (mouse is null || !_focused)
        {
            _cursorStateKnown = false;
            return;
        }

        ImGuiIOPtr io = ImGui.GetIO();
        if ((io.ConfigFlags & ImGuiConfigFlags.NoMouseCursorChange) != 0)
        {
            // The application owns the cursor; re-apply ImGui's choice if this flag is cleared.
            _cursorStateKnown = false;
            return;
        }

        ImGuiMouseCursor cursor = io.MouseDrawCursor ? ImGuiMouseCursor.None : ImGui.GetMouseCursor();
        if (_cursorStateKnown && cursor == _appliedMouseCursor)
            return;

        ApplyMouseCursor(mouse.Cursor, cursor);
        _appliedMouseCursor = cursor;
        _cursorStateKnown = true;
    }

    private static void ApplyMouseCursor(ICursor cursor, ImGuiMouseCursor imguiCursor)
    {
        if (imguiCursor == ImGuiMouseCursor.None)
        {
            if (cursor.CursorMode != CursorMode.Hidden && cursor.IsSupported(CursorMode.Hidden))
                cursor.CursorMode = CursorMode.Hidden;
            return;
        }

        StandardCursor standardCursor = TranslateCursor(imguiCursor);
        if (!cursor.IsSupported(standardCursor))
            standardCursor = cursor.IsSupported(StandardCursor.Default)
                ? StandardCursor.Default
                : StandardCursor.Arrow;

        if (cursor.CursorMode != CursorMode.Normal && cursor.IsSupported(CursorMode.Normal))
            cursor.CursorMode = CursorMode.Normal;
        if (cursor.Type != CursorType.Standard)
            cursor.Type = CursorType.Standard;
        if (cursor.StandardCursor != standardCursor)
            cursor.StandardCursor = standardCursor;
    }

    private void InstallClipboardCallbacks()
    {
        ImGuiPlatformIOPtr platformIo = ImGui.GetPlatformIO();
        _originalGetClipboardTextFn = platformIo.Platform_GetClipboardTextFn;
        _originalSetClipboardTextFn = platformIo.Platform_SetClipboardTextFn;
        platformIo.Platform_GetClipboardTextFn =
            Marshal.GetFunctionPointerForDelegate(_getClipboardText);
        platformIo.Platform_SetClipboardTextFn =
            Marshal.GetFunctionPointerForDelegate(_setClipboardText);
        _clipboardCallbacksInstalled = true;
    }

    private void RestoreClipboardCallbacks()
    {
        if (!_clipboardCallbacksInstalled)
            return;

        EnsureContext();
        ImGuiPlatformIOPtr platformIo = ImGui.GetPlatformIO();
        platformIo.Platform_GetClipboardTextFn = _originalGetClipboardTextFn;
        platformIo.Platform_SetClipboardTextFn = _originalSetClipboardTextFn;
        _clipboardCallbacksInstalled = false;
    }

    private IntPtr OnGetClipboardText(IntPtr context)
    {
        _ = context;
        try
        {
            IKeyboard? keyboard = _keyboard;
            string? text = keyboard is not null && keyboard.IsConnected ? keyboard.ClipboardText : null;
            if (string.IsNullOrEmpty(text))
                return EmptyClipboardText();

            int required = Encoding.UTF8.GetByteCount(text) + 1;
            if (required > _clipboardTextCapacity)
            {
                IntPtr buffer = Marshal.AllocHGlobal(required);
                if (_clipboardTextBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(_clipboardTextBuffer);
                _clipboardTextBuffer = buffer;
                _clipboardTextCapacity = required;
            }

            byte* destination = (byte*)_clipboardTextBuffer;
            int written = Encoding.UTF8.GetBytes(
                text.AsSpan(),
                new Span<byte>(destination, required - 1));
            destination[written] = 0;
            return _clipboardTextBuffer;
        }
        catch
        {
            // Exceptions must not unwind through native ImGui code.
            return EmptyClipboardText();
        }
    }

    private void OnSetClipboardText(IntPtr context, IntPtr text)
    {
        _ = context;
        if (text == IntPtr.Zero)
            return;

        try
        {
            string? value = Marshal.PtrToStringUTF8(text);
            IKeyboard? keyboard = _keyboard;
            if (value is not null && keyboard is not null && keyboard.IsConnected)
                keyboard.ClipboardText = value;
        }
        catch
        {
            // A clipboard failure must not tear down the render loop.
        }
    }

    private IntPtr EmptyClipboardText()
    {
        if (_clipboardTextBuffer == IntPtr.Zero)
        {
            _clipboardTextBuffer = Marshal.AllocHGlobal(1);
            _clipboardTextCapacity = 1;
        }

        *(byte*)_clipboardTextBuffer = 0;
        return _clipboardTextBuffer;
    }

    private void FreeClipboardBuffer()
    {
        if (_clipboardTextBuffer == IntPtr.Zero)
            return;

        Marshal.FreeHGlobal(_clipboardTextBuffer);
        _clipboardTextBuffer = IntPtr.Zero;
        _clipboardTextCapacity = 0;
    }

    private static int TranslateMouseButton(MouseButton button) => button switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        MouseButton.Button4 => 3,
        MouseButton.Button5 => 4,
        _ => -1,
    };

    private static StandardCursor TranslateCursor(ImGuiMouseCursor cursor) => cursor switch
    {
        ImGuiMouseCursor.TextInput => StandardCursor.IBeam,
        ImGuiMouseCursor.ResizeAll => StandardCursor.ResizeAll,
        ImGuiMouseCursor.ResizeNS => StandardCursor.VResize,
        ImGuiMouseCursor.ResizeEW => StandardCursor.HResize,
        ImGuiMouseCursor.ResizeNESW => StandardCursor.NeswResize,
        ImGuiMouseCursor.ResizeNWSE => StandardCursor.NwseResize,
        ImGuiMouseCursor.Hand => StandardCursor.Hand,
        ImGuiMouseCursor.NotAllowed => StandardCursor.NotAllowed,
        _ => StandardCursor.Arrow,
    };

    private static ImGuiKey TranslateKey(Key key) => key switch
    {
        Key.Tab => ImGuiKey.Tab,
        Key.Left => ImGuiKey.LeftArrow,
        Key.Right => ImGuiKey.RightArrow,
        Key.Up => ImGuiKey.UpArrow,
        Key.Down => ImGuiKey.DownArrow,
        Key.PageUp => ImGuiKey.PageUp,
        Key.PageDown => ImGuiKey.PageDown,
        Key.Home => ImGuiKey.Home,
        Key.End => ImGuiKey.End,
        Key.Insert => ImGuiKey.Insert,
        Key.Delete => ImGuiKey.Delete,
        Key.Backspace => ImGuiKey.Backspace,
        Key.Space => ImGuiKey.Space,
        Key.Enter => ImGuiKey.Enter,
        Key.Escape => ImGuiKey.Escape,
        Key.Apostrophe => ImGuiKey.Apostrophe,
        Key.Comma => ImGuiKey.Comma,
        Key.Minus => ImGuiKey.Minus,
        Key.Period => ImGuiKey.Period,
        Key.Slash => ImGuiKey.Slash,
        Key.Semicolon => ImGuiKey.Semicolon,
        Key.Equal => ImGuiKey.Equal,
        Key.LeftBracket => ImGuiKey.LeftBracket,
        Key.BackSlash => ImGuiKey.Backslash,
        Key.RightBracket => ImGuiKey.RightBracket,
        Key.GraveAccent => ImGuiKey.GraveAccent,
        Key.CapsLock => ImGuiKey.CapsLock,
        Key.ScrollLock => ImGuiKey.ScrollLock,
        Key.NumLock => ImGuiKey.NumLock,
        Key.PrintScreen => ImGuiKey.PrintScreen,
        Key.Pause => ImGuiKey.Pause,
        Key.Keypad0 => ImGuiKey.Keypad0,
        Key.Keypad1 => ImGuiKey.Keypad1,
        Key.Keypad2 => ImGuiKey.Keypad2,
        Key.Keypad3 => ImGuiKey.Keypad3,
        Key.Keypad4 => ImGuiKey.Keypad4,
        Key.Keypad5 => ImGuiKey.Keypad5,
        Key.Keypad6 => ImGuiKey.Keypad6,
        Key.Keypad7 => ImGuiKey.Keypad7,
        Key.Keypad8 => ImGuiKey.Keypad8,
        Key.Keypad9 => ImGuiKey.Keypad9,
        Key.KeypadDecimal => ImGuiKey.KeypadDecimal,
        Key.KeypadDivide => ImGuiKey.KeypadDivide,
        Key.KeypadMultiply => ImGuiKey.KeypadMultiply,
        Key.KeypadSubtract => ImGuiKey.KeypadSubtract,
        Key.KeypadAdd => ImGuiKey.KeypadAdd,
        Key.KeypadEnter => ImGuiKey.KeypadEnter,
        Key.KeypadEqual => ImGuiKey.KeypadEqual,
        Key.ShiftLeft => ImGuiKey.LeftShift,
        Key.ControlLeft => ImGuiKey.LeftCtrl,
        Key.AltLeft => ImGuiKey.LeftAlt,
        Key.SuperLeft => ImGuiKey.LeftSuper,
        Key.ShiftRight => ImGuiKey.RightShift,
        Key.ControlRight => ImGuiKey.RightCtrl,
        Key.AltRight => ImGuiKey.RightAlt,
        Key.SuperRight => ImGuiKey.RightSuper,
        Key.Menu => ImGuiKey.Menu,
        Key.Number0 => ImGuiKey._0,
        Key.Number1 => ImGuiKey._1,
        Key.Number2 => ImGuiKey._2,
        Key.Number3 => ImGuiKey._3,
        Key.Number4 => ImGuiKey._4,
        Key.Number5 => ImGuiKey._5,
        Key.Number6 => ImGuiKey._6,
        Key.Number7 => ImGuiKey._7,
        Key.Number8 => ImGuiKey._8,
        Key.Number9 => ImGuiKey._9,
        Key.A => ImGuiKey.A,
        Key.B => ImGuiKey.B,
        Key.C => ImGuiKey.C,
        Key.D => ImGuiKey.D,
        Key.E => ImGuiKey.E,
        Key.F => ImGuiKey.F,
        Key.G => ImGuiKey.G,
        Key.H => ImGuiKey.H,
        Key.I => ImGuiKey.I,
        Key.J => ImGuiKey.J,
        Key.K => ImGuiKey.K,
        Key.L => ImGuiKey.L,
        Key.M => ImGuiKey.M,
        Key.N => ImGuiKey.N,
        Key.O => ImGuiKey.O,
        Key.P => ImGuiKey.P,
        Key.Q => ImGuiKey.Q,
        Key.R => ImGuiKey.R,
        Key.S => ImGuiKey.S,
        Key.T => ImGuiKey.T,
        Key.U => ImGuiKey.U,
        Key.V => ImGuiKey.V,
        Key.W => ImGuiKey.W,
        Key.X => ImGuiKey.X,
        Key.Y => ImGuiKey.Y,
        Key.Z => ImGuiKey.Z,
        Key.F1 => ImGuiKey.F1,
        Key.F2 => ImGuiKey.F2,
        Key.F3 => ImGuiKey.F3,
        Key.F4 => ImGuiKey.F4,
        Key.F5 => ImGuiKey.F5,
        Key.F6 => ImGuiKey.F6,
        Key.F7 => ImGuiKey.F7,
        Key.F8 => ImGuiKey.F8,
        Key.F9 => ImGuiKey.F9,
        Key.F10 => ImGuiKey.F10,
        Key.F11 => ImGuiKey.F11,
        Key.F12 => ImGuiKey.F12,
        Key.F13 => ImGuiKey.F13,
        Key.F14 => ImGuiKey.F14,
        Key.F15 => ImGuiKey.F15,
        Key.F16 => ImGuiKey.F16,
        Key.F17 => ImGuiKey.F17,
        Key.F18 => ImGuiKey.F18,
        Key.F19 => ImGuiKey.F19,
        Key.F20 => ImGuiKey.F20,
        Key.F21 => ImGuiKey.F21,
        Key.F22 => ImGuiKey.F22,
        Key.F23 => ImGuiKey.F23,
        Key.F24 => ImGuiKey.F24,
        _ => ImGuiKey.None,
    };
}
