using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace VRMR.App.Services
{
    /// <summary>
    ///     Small things the window remembers between runs (which sections are open, panel sizes, the theme, whether the
    ///     first-run guide was seen), in <c>ui-state.json</c> next to the settings. Not part of the engine's settings.
    /// </summary>
    public sealed class UiStateStore : IDisposable
    {
        readonly string _file;
        readonly Debouncer _save;
        readonly Dictionary<string, object> _values = new();
        readonly object _writeLock = new();

        public UiStateStore(string file, IUiDispatcher ui)
        {
            _file = file;
            _save = new Debouncer(ui, TimeSpan.FromMilliseconds(500), () =>
            {
                string json = Serialize();
                Task.Run(() => Write(json));
            });
            try
            {
                if (File.Exists(file))
                {
                    var d = Json.ParseObject(File.ReadAllText(file, Encoding.UTF8));
                    if (d != null)
                        foreach (var kv in d)
                            _values[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex) { AppLog.Warn("UI state: " + ex.Message); }
        }

        public void Dispose()
        {
            Flush();
        }

        /// <summary>Raised when a value changed (the key).</summary>
        public event Action<string> Changed;

        public bool GetBool(string key, bool fallback)
        {
            return _values.TryGetValue(key, out object v) && v is bool b ? b : fallback;
        }

        public double GetNumber(string key, double fallback)
        {
            return _values.TryGetValue(key, out object v)
                ? v is double d ? d : v is long l ? l : v is int i ? i : fallback
                : fallback;
        }

        public string GetString(string key, string fallback)
        {
            return _values.TryGetValue(key, out object v) && v is string s ? s : fallback;
        }

        public void Set(string key, bool value)
        {
            SetCore(key, value);
        }

        public void Set(string key, double value)
        {
            SetCore(key, value);
        }

        public void Set(string key, string value)
        {
            SetCore(key, value ?? "");
        }

        void SetCore(string key, object value)
        {
            if (_values.TryGetValue(key, out object old) && Equals(old, value)) return;
            _values[key] = value;
            _save.Trigger();
            Changed?.Invoke(key);
        }

        string Serialize()
        {
            return Json.Serialize(new Dictionary<string, object>(_values), true);
        }

        void Write(string json)
        {
            try
            {
                lock (_writeLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_file));
                    File.WriteAllText(_file, json, Encoding.UTF8);
                }
            }
            catch (Exception ex) { AppLog.Warn("UI state save: " + ex.Message); }
        }

        /// <summary>Writes pending changes now (on exit).</summary>
        public void Flush()
        {
            if (!_save.Pending) return;
            _save.Cancel();
            Write(Serialize());
        }
    }
}
