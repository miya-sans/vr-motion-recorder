using System;
using System.Collections.Generic;

namespace VRMR.App.Services
{
    /// <summary>Sends avatar parameters somewhere (VRChat's OSC input). Tests use their own.</summary>
    public interface IParameterSender : IDisposable
    {
        void Send(string name, ParamType type, float value);
    }

    /// <summary>
    ///     While a take plays, sends its recorded face parameters to VRChat so the replay shows on the avatar in game.
    ///     Off unless the user switches it on; values are only sent when they changed.
    /// </summary>
    public sealed class FaceReplayService : IDisposable
    {
        readonly Func<IParameterSender> _connect;
        readonly Dictionary<string, float> _sent = new();
        bool _enabled;
        IParameterSender _sender;

        /// <param name="connect">
        ///     Opens the sender when replay is switched on; null when sending is not available (no live
        ///     sources).
        /// </param>
        public FaceReplayService(Func<IParameterSender> connect) { _connect = connect; }

        /// <summary>False when the app runs without live sources: nothing can be sent.</summary>
        public bool Available => _connect != null;

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (value && !Available) return;
                if (_enabled == value) return;
                _enabled = value;
                if (!value)
                {
                    _sender?.Dispose();
                    _sender = null;
                }

                _sent.Clear();
            }
        }

        public void Dispose()
        {
            _sender?.Dispose();
            _sender = null;
        }

        /// <summary>Playback (re)started: send every value again.</summary>
        public void Restart()
        {
            _sent.Clear();
        }

        /// <summary>Sends the face parameters of a take at a source time (shifted by the take's face offset).</summary>
        public void Send(Recording take, double time)
        {
            if (!_enabled || take == null) return;
            if (_sender == null) _sender = _connect();
            double offset = take.Edits.FaceOffset;
            foreach (var p in take.Params)
            {
                if (!IsFaceParameter(p.Name)) continue;
                float v = p.Type == ParamType.Float ? p.Sample(time - offset) : p.SampleStep(time - offset);
                if (_sent.TryGetValue(p.Name, out float prev) && Math.Abs(prev - v) < 1e-4f) continue;
                _sent[p.Name] = v;
                _sender.Send(p.Name, p.Type, v);
            }
        }

        /// <summary>Face tracking parameters by their name prefix.</summary>
        public static bool IsFaceParameter(string name)
        {
            return name.StartsWith("FT/", StringComparison.Ordinal) || name.StartsWith("v2/", StringComparison.Ordinal);
        }
    }

    /// <summary>The sender for VRChat's OSC input port on this computer.</summary>
    public sealed class OscParameterSender : IParameterSender
    {
        readonly OscSender _osc;
        public OscParameterSender(int port) { _osc = new OscSender("127.0.0.1", port); }

        public void Send(string name, ParamType type, float value)
        {
            _osc.SendParam(name, type, value);
        }

        public void Dispose()
        {
            _osc.Dispose();
        }
    }
}
