using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace QuickAccess;

public sealed class HotKeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private readonly System.Windows.Window _window;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _disposed;

    public event Action<int>? HotKeyPressed;

    public string LastError { get; private set; } = "";

    public HotKeyManager(System.Windows.Window window) => _window = window;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const int ID_MAIN = 9001;
    public const int ID_SECONDARY = 9002;

    public record HotkeySlot(int Id, uint Modifiers, uint Vk);

    public bool Init(IEnumerable<HotkeySlot> slots)
    {
        var helper = new WindowInteropHelper(_window);
        helper.EnsureHandle();
        _handle = helper.Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(HwndHook);

        var results = new List<string>();
        bool anyOk = false;
        foreach (var slot in slots)
        {
            bool ok = RegisterHotKey(_handle, slot.Id, slot.Modifiers, slot.Vk);
            int err = ok ? 0 : Marshal.GetLastWin32Error();
            if (ok) anyOk = true;
            results.Add($"{Format(slot.Modifiers, (int)slot.Vk)}: {(ok ? "OK" : $"FAIL err={err}")}");
        }

        LastError = string.Join(", ", results);
        DebugLog.Write($"HotKey Init handle={_handle}: {LastError}");
        return anyOk;
    }

    public static bool TryParse(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "alt":
                case "menu": modifiers |= MOD_ALT; break;
                case "ctrl":
                case "control": modifiers |= MOD_CONTROL; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win":
                case "windows": modifiers |= MOD_WIN; break;
                default: return false;
            }
        }
        string keyPart = parts[^1];
        System.Windows.Input.Key key;
        if (keyPart.Length == 1)
        {
            char c = char.ToUpperInvariant(keyPart[0]);
            if (c is >= 'A' and <= 'Z') key = System.Windows.Input.Key.A + (c - 'A');
            else if (c is >= '0' and <= '9') key = System.Windows.Input.Key.D0 + (c - '0');
            else return false;
        }
        else if (!Enum.TryParse(keyPart, true, out key)) return false;
        int code = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        if (code == 0) return false;
        vk = (uint)code;
        return true;
    }

    public static string Format(uint modifiers, int vk)
    {
        var parts = new List<string>();
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
        string name = System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk).ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name.Substring(1);
        parts.Add(name);
        return string.Join("+", parts);
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == ID_MAIN || id == ID_SECONDARY)
            {
                DebugLog.Write($"HotKey pressed id={id}");
                HotKeyPressed?.Invoke(id);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_handle != IntPtr.Zero)
            {
                UnregisterHotKey(_handle, ID_MAIN);
                UnregisterHotKey(_handle, ID_SECONDARY);
            }
            if (_source != null) _source.RemoveHook(HwndHook);
        }
        catch { }
    }
}

public static class DebugLog
{
    private static readonly string Path =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuickAccess", "debug.log");

    public static void Write(string msg)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            System.IO.File.AppendAllText(Path,
                $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public static string ReadTail(int maxChars = 2000)
    {
        try
        {
            if (!System.IO.File.Exists(Path)) return "(лог пуст)";
            var t = System.IO.File.ReadAllText(Path);
            return t.Length <= maxChars ? t : "..." + t[^maxChars..];
        }
        catch (Exception ex) { return ex.Message; }
    }
}
