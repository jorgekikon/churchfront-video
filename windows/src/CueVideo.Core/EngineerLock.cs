using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CueATEM;

namespace CueVideo;

/// <summary>
/// A PIN (4 to 8 digits) that guards the Engineer pages that could disturb the livestream:
/// Stage scan, Routing, Full switcher and Setup. Status stays open. Only a salted PBKDF2 hash
/// is kept, in %APPDATA%\CueVideo\engineer-lock.json (not in config.json, which Claude and
/// people edit). Unlocking lasts until the operator goes back to Operator, or 10 minutes
/// without a click or key in Engineer. After 5 wrong PINs in a row it waits 30 seconds.
/// Forgotten PIN: delete engineer-lock.json (it needs access to this PC's user account).
/// Self-tests use CUEVIDEO_LOCK_FILE, or no lock at all, never the real file.
/// </summary>
public sealed class EngineerLock
{
    public const int MinDigits = 4, MaxDigits = 8, TriesBeforeWait = 5;
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10), Wait = TimeSpan.FromSeconds(30);
    private const int Iterations = 120_000;

    private sealed class Stored { public string Salt { get; set; } = ""; public string Hash { get; set; } = ""; public int Iterations { get; set; } }

    private readonly string? _file;
    private Stored? _pin;
    private DateTime _lastActivity = DateTime.MinValue;
    private bool _open;
    private int _wrong;
    private DateTime _waitUntil = DateTime.MinValue;
    /// <summary>For tests: what "now" is.</summary>
    public Func<DateTime> Clock = () => DateTime.UtcNow;
    /// <summary>Raised (on the main thread) when the PIN is set or removed, or the lock opens or closes.</summary>
    public event Action? Changed;

    public EngineerLock(string? file = null, bool? selfTest = null)
    {
        var test = selfTest ?? Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(k => k.StartsWith("CUEVIDEO_", StringComparison.Ordinal));
        _file = file ?? (Environment.GetEnvironmentVariable("CUEVIDEO_LOCK_FILE") is string f && f.Length > 0 ? f
            : test ? null : Path.Combine(ConfigStore.Folder, "engineer-lock.json"));
        try { if (_file != null && File.Exists(_file)) _pin = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_file), CueJson.Options); } catch { _pin = null; }
        if (_pin != null && (_pin.Hash.Length == 0 || _pin.Salt.Length == 0 || _pin.Iterations <= 0)) _pin = null;
    }

    public static bool IsLockable(EngineerSection s) => s != EngineerSection.Status;

    public bool HasPin => _pin != null;

    /// <summary>Whether the locked pages can be shown now. Always true with no PIN set.</summary>
    public bool IsOpen
    {
        get
        {
            if (_pin == null) return true;
            if (_open && Clock() - _lastActivity > IdleLimit) { _open = false; Changed?.Invoke(); }
            return _open;
        }
    }

    public bool IsLocked(EngineerSection s) => IsLockable(s) && !IsOpen;

    /// <summary>A click or key in Engineer: keeps it open for another 10 minutes.</summary>
    public void Touch() { if (_open && IsOpen) _lastActivity = Clock(); }

    public void Lock() { if (_open) { _open = false; Changed?.Invoke(); } }

    /// <summary>Seconds to wait after too many wrong PINs, or 0.</summary>
    public int WaitSeconds => Math.Max(0, (int)Math.Ceiling((_waitUntil - Clock()).TotalSeconds));

    /// <summary>Tries a PIN. Null when it opened, else why not (wrong, or wait).</summary>
    public string? Unlock(string pin)
    {
        if (_pin == null) { _open = true; _lastActivity = Clock(); return null; }
        if (WaitSeconds > 0) return $"Too many wrong PINs. Try again in {WaitSeconds} seconds.";
        if (Matches(pin))
        {
            _wrong = 0; _open = true; _lastActivity = Clock();
            Changed?.Invoke();
            return null;
        }
        _wrong++;
        if (_wrong >= TriesBeforeWait) { _wrong = 0; _waitUntil = Clock() + Wait; return $"Wrong PIN. Too many tries: wait {(int)Wait.TotalSeconds} seconds."; }
        return "Wrong PIN.";
    }

    public static string? Problem(string pin) =>
        pin.Length < MinDigits || pin.Length > MaxDigits || !pin.All(char.IsAsciiDigit) ? $"The PIN must be {MinDigits} to {MaxDigits} digits." : null;

    /// <summary>Sets a new PIN (only while open). Null when saved, else why not. Leaves it open.</summary>
    public string? SetPin(string pin)
    {
        if (!IsOpen) return "Unlock first.";
        if (Problem(pin) is string p) return p;
        if (_file == null) return "Not saved: this is a self-test.";
        var salt = RandomNumberGenerator.GetBytes(16);
        var s = new Stored { Salt = Convert.ToBase64String(salt), Hash = Convert.ToBase64String(Derive(pin, salt, Iterations)), Iterations = Iterations };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            ConfigStore.WriteAtomic(_file, JsonSerializer.Serialize(s, CueJson.Pretty));
        }
        catch (Exception e) { return $"Couldn't save the PIN: {e.Message}"; }
        _pin = s; _open = true; _lastActivity = Clock();
        Changed?.Invoke();
        return null;
    }

    /// <summary>Removes the PIN (only while open): every page is open to anyone again.</summary>
    public string? RemovePin()
    {
        if (!IsOpen) return "Unlock first.";
        try { if (_file != null && File.Exists(_file)) File.Delete(_file); }
        catch (Exception e) { return $"Couldn't remove the PIN: {e.Message}"; }
        _pin = null; _open = false;
        Changed?.Invoke();
        return null;
    }

    private bool Matches(string pin)
    {
        if (_pin == null) return true;
        try
        {
            var want = Convert.FromBase64String(_pin.Hash);
            return CryptographicOperations.FixedTimeEquals(Derive(pin, Convert.FromBase64String(_pin.Salt), _pin.Iterations), want);
        }
        catch { return false; }
    }

    private static byte[] Derive(string pin, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, 32);
}
