using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EncyPulse.Core;

// ----------------------------------------------------------------------------------------------
// settings.json
// ----------------------------------------------------------------------------------------------

public sealed class Settings
{
    public int Version { get; set; } = 1;
    public string InstallId { get; set; } = Guid.NewGuid().ToString("N");
    public ChannelSettings Channels { get; set; } = new();
    public NotifyDefaults Defaults { get; set; } = new();
    public DiagnosticsSettings Diagnostics { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();

    public IEnumerable<string> EnabledChannels()
    {
        if (Channels.Email.Enabled) yield return ChannelKeys.Email;
        if (Channels.Ntfy.Enabled) yield return ChannelKeys.Ntfy;
        if (Channels.Pushover.Enabled) yield return ChannelKeys.Pushover;
        if (Channels.Relay.Enabled) yield return ChannelKeys.Relay;
    }
}

/// <summary>
/// The personal ntfy topic doubles as the user's access code: whoever knows it can read the
/// messages. Generated once per installation from a cryptographic source, never shared between users.
/// </summary>
public static class TopicCode
{
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789"; // no 0/o/1/l/i to avoid misreading

    public static string New()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return "ency-" + new string(chars);
    }
}

public static class ChannelKeys
{
    public const string Email = "email";
    public const string Ntfy = "ntfy";
    public const string Pushover = "pushover";
    public const string Relay = "relay";

    /// <summary>Channels that land on a phone or watch; these observe quiet hours.</summary>
    public static bool IsPush(string key) => key is Ntfy or Pushover or Relay;
}

public sealed class ChannelSettings
{
    public EmailChannel Email { get; set; } = new();
    public NtfyChannel Ntfy { get; set; } = new();
    public PushoverChannel Pushover { get; set; } = new();
    public RelayChannel Relay { get; set; } = new();
}

public sealed class EmailChannel
{
    public bool Enabled { get; set; }
    public string Address { get; set; } = "";
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string SmtpUser { get; set; } = "";
    /// <summary>Stored DPAPI-protected ("dpapi:..."). Plain text is accepted and re-protected on next save.</summary>
    public string SmtpPassword { get; set; } = "";
    public string From { get; set; } = "";
}

public sealed class NtfyChannel
{
    public bool Enabled { get; set; }
    public string Server { get; set; } = "https://ntfy.sh";
    public string Topic { get; set; } = "";
    public string AccessToken { get; set; } = "";
    /// <summary>Optional: ntfy.sh forwards a copy to this address (the "Email" header).</summary>
    public string EmailForward { get; set; } = "";
}

public sealed class PushoverChannel
{
    public bool Enabled { get; set; }
    public string UserKey { get; set; } = "";
    public string AppToken { get; set; } = "";
}

public sealed class RelayChannel
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public sealed class NotifyDefaults
{
    /// <summary>Entire project: one message when every operation has finished calculating.</summary>
    public bool NotifyProjectCompleted { get; set; } = true;
    /// <summary>Entire project: one message when a simulation of the whole project has finished.</summary>
    public bool NotifySimulationCompleted { get; set; } = true;
    /// <summary>Also report operations that finished without a toolpath.</summary>
    public bool NotifyFailures { get; set; } = true;
    /// <summary>Project-level messages are skipped when the run was shorter than this. 0 = never skip.</summary>
    public int IgnoreShorterThanSec { get; set; } = 0;
    /// <summary>Send only if there was no keyboard/mouse input for this many minutes. 0 = always send.</summary>
    public int OnlyWhenAwayMinutes { get; set; } = 2;
    /// <summary>"HH:mm" local time, or empty. Pushes inside the window are held until it ends.</summary>
    public string QuietHoursFrom { get; set; } = "";
    public string QuietHoursTo { get; set; } = "";
    /// <summary>No calculation activity for this long closes a batch. ENCY pauses a few seconds between operations.</summary>
    public int QuietWindowMs { get; set; } = 4000;
    /// <summary>No change in simulated-operation count for this long ends a simulation (flag-based detector).</summary>
    public int SimulationQuietMs { get; set; } = 2000;
    /// <summary>
    /// No simulation line in ENCY's log for this long ends a simulation run. Interactive simulation
    /// leaves gaps of several seconds between operations, so this is deliberately generous.
    /// </summary>
    public int SimulationSessionQuietSec { get; set; } = 15;
}

public sealed class AppearanceSettings
{
    /// <summary>"auto" follows ENCY's active theme and palette; "light" / "dark" force the built-in ENCY-style palettes.</summary>
    public string Theme { get; set; } = "auto";
    /// <summary>The Overview page is shown first until the user has seen it once.</summary>
    public bool WelcomeShown { get; set; }
}

public sealed class DiagnosticsSettings
{
    public bool DebugLog { get; set; }
    /// <summary>
    /// What RegisterHandler gets as its event list. "empty" (default, the mode the official examples
    /// use and the only one verified to deliver events) lets ENCY call every handler interface the
    /// object implements; "guids" passes interface GUIDs; "names" passes interface names.
    /// </summary>
    public string HandlerEventListMode { get; set; } = "empty";
    /// <summary>Read the operation tree from the background thread at batch close (true), or copy it on ENCY's thread with every event (false).</summary>
    public bool BackgroundSnapshot { get; set; } = true;
    /// <summary>Poll the simulator state while ENCY is in simulation mode.</summary>
    public bool SimulationProbe { get; set; } = true;
    /// <summary>
    /// Detect finished operations by polling the tree for Calculated flag changes (every 2 s, 0.5 s while
    /// simulating). Needed because the UI-triggered calculation did not raise ToolpathCalculated on
    /// ENCY NB 3. Duplicates against real events are removed.
    /// </summary>
    public bool PollForCompletion { get; set; } = true;
    /// <summary>
    /// Follow ENCY's own log file (ICamApiApplication.LogFilePath) for "Start the operation calculation",
    /// "The calculation of the operation is completed" and per-operation simulation lines. The most
    /// reliable signal on ENCY NB 3; English UI texts.
    /// </summary>
    public bool TailEncyLog { get; set; } = true;
    /// <summary>
    /// A simulation run that begins within this many seconds after a project load or a calculation is
    /// ENCY's automatic stock update, not something the user asked for, and is not reported.
    /// </summary>
    public int AutoSimulationGraceSec { get; set; } = 20;
}

// ----------------------------------------------------------------------------------------------
// rules.json
// ----------------------------------------------------------------------------------------------

public sealed class NotifyRules
{
    public Dictionary<string, ProjectRules> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ProjectRules? For(string projectId) => Find(Projects, projectId);

    public OperationRule? For(string projectId, string operationId)
    {
        var p = For(projectId);
        return p == null ? null : Find(p.Operations, operationId);
    }

    public ProjectRules Ensure(string projectId, string filePath)
    {
        var p = Find(Projects, projectId);
        if (p == null) Projects[projectId] = p = new ProjectRules();
        if (!string.IsNullOrEmpty(filePath)) p.FilePath = filePath;
        return p;
    }

    /// <summary>GUID strings compare case-insensitively; JSON deserialization drops the dictionary comparer.</summary>
    internal static TValue? Find<TValue>(Dictionary<string, TValue> dict, string key) where TValue : class
    {
        if (dict.TryGetValue(key, out var v)) return v;
        foreach (var kv in dict)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }
}

public sealed class ProjectRules
{
    public string FilePath { get; set; } = "";
    public Dictionary<string, OperationRule> Operations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Selected operation: which of its completions the user wants to hear about.</summary>
public sealed class OperationRule
{
    /// <summary>"Operation 'X' calculation completed."</summary>
    public bool Calculation { get; set; } = true;
    /// <summary>"Simulation completed for 'X'."</summary>
    public bool Simulation { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Optional channel override; null means every enabled channel.</summary>
    public List<string>? Channels { get; set; }

    public bool IsEmpty => !Calculation && !Simulation;
}

public static class NotifyRulesExtensions
{
    /// <summary>Create, update or remove the rule of one operation. Empty rules are removed.</summary>
    public static void SetOperation(this NotifyRules rules, string projectId, string filePath, string operationId, string name,
        bool? calculation = null, bool? simulation = null)
    {
        var pr = rules.Ensure(projectId, filePath);
        var existing = NotifyRules.Find(pr.Operations, operationId);
        var rule = existing ?? new OperationRule { Calculation = false, Simulation = false };
        if (calculation.HasValue) rule.Calculation = calculation.Value;
        if (simulation.HasValue) rule.Simulation = simulation.Value;
        if (!string.IsNullOrEmpty(name)) rule.Name = name;

        var key = pr.Operations.Keys.FirstOrDefault(k => string.Equals(k, operationId, StringComparison.OrdinalIgnoreCase)) ?? operationId;
        if (rule.IsEmpty) pr.Operations.Remove(key);
        else pr.Operations[key] = rule;
    }
}

// ----------------------------------------------------------------------------------------------
// JSON file store with hot reload
// ----------------------------------------------------------------------------------------------

public sealed class JsonFileStore<T> : IDisposable where T : class, new()
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly Log _log;
    private readonly object _gate = new();
    private T _current;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private DateTimeOffset _ignoreUntil;

    public string Path { get; }
    public T Current => Volatile.Read(ref _current);

    /// <summary>Raised on the watcher thread after an external edit was reloaded.</summary>
    public event Action<T>? Changed;

    public JsonFileStore(string path, Log log, bool watch = true)
    {
        Path = path;
        _log = log;
        _current = LoadOrDefault(out var existed);
        if (!existed) Save(_current);
        if (watch) StartWatcher();
    }

    private T LoadOrDefault(out bool existed)
    {
        existed = File.Exists(Path);
        if (!existed) return new T();
        try
        {
            var json = File.ReadAllText(Path);
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
        }
        catch (Exception ex)
        {
            _log.Error($"could not read {Path}, using defaults", ex);
            return new T();
        }
    }

    public void Save(T value)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var tmp = Path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
                _ignoreUntil = DateTimeOffset.UtcNow.AddSeconds(1.5);
                File.Move(tmp, Path, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.Error($"could not write {Path}", ex);
            }
            Volatile.Write(ref _current, value);
        }
    }

    /// <summary>Copy-on-write update: readers never see a half-mutated object.</summary>
    public T Update(Action<T> mutate)
    {
        lock (_gate)
        {
            var copy = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(Current, JsonOptions), JsonOptions) ?? new T();
            mutate(copy);
            Save(copy);
            return copy;
        }
    }

    public void Reload()
    {
        var fresh = LoadOrDefault(out _);
        Volatile.Write(ref _current, fresh);
        try { Changed?.Invoke(fresh); } catch (Exception ex) { _log.Error("settings Changed handler failed", ex); }
    }

    private void StartWatcher()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(Path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler onChange = (_, _) =>
            {
                if (DateTimeOffset.UtcNow < _ignoreUntil) return;
                _debounce?.Dispose();
                _debounce = new Timer(_ =>
                {
                    _log.Info($"reloading {System.IO.Path.GetFileName(Path)} after external change");
                    Reload();
                }, null, 400, Timeout.Infinite);
            };
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Renamed += (s, e) => onChange(s, e);
        }
        catch (Exception ex)
        {
            _log.Warn($"file watcher unavailable for {Path}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}

// ----------------------------------------------------------------------------------------------
// Secrets: DPAPI (current user) without extra packages
// ----------------------------------------------------------------------------------------------

public static class Secrets
{
    public const string Prefix = "dpapi:";

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static bool IsProtected(string? value) => value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Returns "dpapi:&lt;base64&gt;". Falls back to the plain value if DPAPI is unavailable.</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain) || IsProtected(plain)) return plain;
        if (!OperatingSystem.IsWindows()) return plain;
        var bytes = Encoding.UTF8.GetBytes(plain);
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var input = new DATA_BLOB { cbData = bytes.Length, pbData = handle.AddrOfPinnedObject() };
            if (!CryptProtectData(ref input, "EncyPulse", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output))
                return plain;
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return Prefix + Convert.ToBase64String(result);
            }
            finally { LocalFree(output.pbData); }
        }
        catch { return plain; }
        finally { handle.Free(); }
    }

    /// <summary>Returns the plain value for both protected and unprotected input. Empty string if it cannot be decrypted.</summary>
    public static string Reveal(string stored)
    {
        if (!IsProtected(stored)) return stored ?? "";
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            var bytes = Convert.FromBase64String(stored.Substring(Prefix.Length));
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var input = new DATA_BLOB { cbData = bytes.Length, pbData = handle.AddrOfPinnedObject() };
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output))
                    return "";
                try
                {
                    var result = new byte[output.cbData];
                    Marshal.Copy(output.pbData, result, 0, output.cbData);
                    return Encoding.UTF8.GetString(result);
                }
                finally { LocalFree(output.pbData); }
            }
            finally { handle.Free(); }
        }
        catch { return ""; }
    }
}
