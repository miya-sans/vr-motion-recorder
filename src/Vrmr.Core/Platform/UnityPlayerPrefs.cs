using System;
using System.Collections.Generic;

namespace VRMR
{
    /// <summary>
    ///     Reads the settings a Unity program keeps in the registry (PlayerPrefs under HKCU\Software\&lt;company&gt;\&lt;
    ///     product&gt;).
    ///     Unity appends "_h" and a hash to every name, and stores floats as 8-byte doubles in values typed as DWORD.
    ///     Read-only.
    /// </summary>
    public sealed class UnityPlayerPrefs
    {
        readonly string _key;
        readonly IMachine _machine;
        readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

        /// <summary>The settings of one Unity program, e.g. company "VRChat", product "VRChat".</summary>
        public UnityPlayerPrefs(IMachine machine, string company, string product)
        {
            _machine = machine;
            _key = @"Software\" + company + @"\" + product;
            foreach (string name in machine.RegistryValueNames(RegistryRoot.CurrentUser, _key))
            {
                int h = name.LastIndexOf("_h", StringComparison.Ordinal);
                _names[h > 0 ? name.Substring(0, h) : name] = name;
            }
        }

        /// <summary>Whether the program has stored any settings (it has run for this user).</summary>
        public bool Found => _names.Count > 0;

        /// <summary>Whether a setting is stored.</summary>
        public bool Has(string name)
        {
            return _names.ContainsKey(name);
        }

        /// <summary>An integer setting, or null when it is not stored.</summary>
        public int? Int(string name)
        {
            return _names.TryGetValue(name, out string full)
                ? (int)Number(_machine.RegistryValue(RegistryRoot.CurrentUser, _key, full), false)
                : null;
        }

        /// <summary>A float setting, or null when it is not stored.</summary>
        public float? Float(string name)
        {
            return _names.TryGetValue(name, out string full)
                ? (float)Number(_machine.RegistryValue(RegistryRoot.CurrentUser, _key, full), true)
                : null;
        }

        /// <summary>A raw registry value as a number: floats are 8-byte doubles (read as Int64), ints plain DWORDs.</summary>
        public static double Number(object value, bool isFloat)
        {
            switch (value)
            {
                case long l: return isFloat ? BitConverter.Int64BitsToDouble(l) : l;
                case int i: return isFloat ? BitConverter.ToSingle(BitConverter.GetBytes(i), 0) : i;
                case double d: return d;
                case float f: return f;
                case byte[] b when b.Length >= 8: return BitConverter.ToDouble(b, 0);
                case byte[] b
                    when b.Length >= 4: return isFloat ? BitConverter.ToSingle(b, 0) : BitConverter.ToInt32(b, 0);
                default: return 0;
            }
        }
    }
}
