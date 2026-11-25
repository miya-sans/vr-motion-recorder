using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VRMR.App.Services;

namespace VRMR.App.ViewModels
{
    /// <summary>
    ///     The calibration settings editor: how the user's size maps onto the avatar, the spine mode, which trackers drive
    ///     the body, and the rarely changed options. It edits the open take's own settings, or the defaults when no take is
    ///     open (the Settings page shows the same editor for the defaults).
    /// </summary>
    public partial class CalibrationSettingsViewModel : ObservableObject
    {
        const string VrcRecordedSetting = "VRChat: Recorded setting";

        static readonly string[] FixedMeasureModes = ["By height", "By arm span"];
        static readonly string[] FixedSpineModes = ["Lock head", "Lock hips", "Lock both"];

        readonly CalibrationService _calibration;
        readonly Func<BakedClip> _clip;

        readonly bool _defaultsOnly;
        [ObservableProperty] string _advancedSummary = "";
        [ObservableProperty] double _avatarScale;
        [ObservableProperty] bool _avatarScaleChanged;
        [ObservableProperty] string _avatarScaleReadout = "";
        [ObservableProperty] bool _followAvatarScale;
        [ObservableProperty] string _handOffsetTip = "";
        [ObservableProperty] double _handOffsetX;
        [ObservableProperty] double _handOffsetY;
        [ObservableProperty] double _handOffsetZ;
        [ObservableProperty] bool _hangFromHeadset;
        [ObservableProperty] string _headOffsetTip = "";
        [ObservableProperty] double _headOffsetY;
        [ObservableProperty] double _headOffsetZ;

        [ObservableProperty] int _heightModeIndex;

        /// <summary>Shown instead of the number when the value is measured or read from VRChat.</summary>
        [ObservableProperty] string _heightReadout = "";

        [ObservableProperty] double _heightValue;
        [ObservableProperty] int _heightValueDecimals = 2;
        [ObservableProperty] bool _heightValueEditable;
        [ObservableProperty] string _heightValueLabel = "Value";
        [ObservableProperty] string _heightValueTip = "";
        [ObservableProperty] string _heightValueUnit = "m";
        [ObservableProperty] bool _keepFeetPlanted;
        [ObservableProperty] int _measureIndex;
        [ObservableProperty] IReadOnlyList<string> _measureModes = [.. FixedMeasureModes, VrcRecordedSetting];
        [ObservableProperty] bool _proceduralFeet;
        [ObservableProperty] bool _recordingOptionsEnabled = true;
        [ObservableProperty] bool _shoulderTracking;
        [ObservableProperty] int _spineIndex;
        [ObservableProperty] IReadOnlyList<string> _spineModes = [.. FixedSpineModes, VrcRecordedSetting];
        bool _syncing;
        [ObservableProperty] bool _uprightCalibration;
        [ObservableProperty] bool _useChest;
        [ObservableProperty] bool _useElbows;
        [ObservableProperty] bool _useFeet;
        [ObservableProperty] bool _useHips;
        [ObservableProperty] bool _useKnees;
        [ObservableProperty] bool _vrcHands;

        /// <param name="defaultsOnly">Edit the defaults whatever take is open (the Settings page).</param>
        public CalibrationSettingsViewModel(CalibrationService calibration, Func<BakedClip> clip,
            bool defaultsOnly = false)
        {
            _calibration = calibration;
            _clip = clip;
            _defaultsOnly = defaultsOnly;
            if (defaultsOnly) calibration.Changed += Sync;
            Sync();
        }

        public IReadOnlyList<string> HeightModes { get; } = new[]
        {
            "Measure at calibration", "My real height", "My eye height", "Tracking scale", "VRChat's User Real Height"
        };

        /// <summary>The ratio VRChat uses between body height and eye height, for the tooltips.</summary>
        public string EyeRatioText =>
            CalibrationSettings.EyeToHeightRatio.ToString("0.00", CultureInfo.InvariantCulture);

        /// <summary>Reads the shown settings into the controls.</summary>
        public void Sync()
        {
            _syncing = true;
            try
            {
                var c = _defaultsOnly ? _calibration.Defaults : _calibration.Shown;
                var clip = _defaultsOnly ? null : _clip();
                var inv = CultureInfo.InvariantCulture;
                HeightModeIndex = (int)c.Height;
                HeightValueEditable = c.Height != CalHeightMode.Measure && c.Height != CalHeightMode.VRChat;
                HeightValue = c.HeightValue;
                HeightValueDecimals = c.Height == CalHeightMode.Scale ? 3 : 2;
                HeightValueUnit = c.Height == CalHeightMode.Scale ? "" : "m";
                HeightValueLabel = c.Height == CalHeightMode.RealHeight ? "Height" :
                    c.Height == CalHeightMode.EyeHeight ? "Eye height" :
                    c.Height == CalHeightMode.Scale ? "Scale" :
                    c.Height == CalHeightMode.VRChat ? "VRChat says" : "Measured";
                HeightValueTip = c.Height == CalHeightMode.RealHeight
                    ? $"Your height from the floor to the top of your head. The eye height is taken as height × {EyeRatioText}, like VRChat does."
                    : c.Height == CalHeightMode.EyeHeight
                        ? "Your eye height, from the floor to your eyes."
                        : c.Height == CalHeightMode.Scale
                            ? "The tracking scale: how many avatar metres one real metre is."
                            : c.Height == CalHeightMode.VRChat
                                ? $"VRChat's User Real Height, read from its settings and stored with each calibration and take. Eye height = height × {EyeRatioText}."
                                : "The headset's height at the calibration moment, with any head tilt removed.";
                if (c.Height == CalHeightMode.Measure)
                {
                    var res = clip?.Calibration;
                    HeightReadout = res != null ? res.RealEyeHeight.ToString("0.000", inv) + " m eye height" :
                        _defaultsOnly || !RecordingOptionsEnabled ? "measured at calibration" : "after the solve";
                }
                else if (c.Height == CalHeightMode.VRChat)
                {
                    var v = VrcSettings.Read();
                    HeightReadout = v.Found
                        ? v.PlayerHeight.ToString("0.00", inv) + " m (VRChat's setting now)"
                        : "not found: VRChat has not run on this computer";
                }
                else HeightReadout = "";

                var (spine, measure) = _defaultsOnly ? (null, null) : _calibration.InitialVrcSettings();
                string vrcSpine = spine.HasValue ? $"VRChat: {FixedSpineModes[(int)spine.Value]}" : VrcRecordedSetting;
                string vrcMeasure = measure.HasValue
                    ? $"VRChat: {FixedMeasureModes[(int)measure.Value]}"
                    : VrcRecordedSetting;
                if (SpineModes[FixedSpineModes.Length] != vrcSpine) SpineModes = [.. FixedSpineModes, vrcSpine];
                if (MeasureModes[FixedMeasureModes.Length] != vrcMeasure) MeasureModes = [.. FixedMeasureModes, vrcMeasure];
                SpineIndex = c.FollowVrcSpine ? FixedSpineModes.Length : (int)c.Lock;
                MeasureIndex = c.FollowVrcMeasure ? FixedMeasureModes.Length : (int)c.Measure;
                UseHips = c.UseHips;
                UseChest = c.UseChest;
                UseFeet = c.UseFeet;
                UseKnees = c.UseKnees;
                UseElbows = c.UseElbows;
                KeepFeetPlanted = c.KeepFeetPlanted;
                UprightCalibration = c.UprightCalibration;
                HangFromHeadset = c.HangFromHeadset;
                VrcHands = c.VrcHands;
                ShoulderTracking = c.ShoulderTracking;
                ProceduralFeet = c.ProceduralFeet;
                FollowAvatarScale = c.FollowAvatarScale;
                AvatarScale = c.AvatarScale;
                AvatarScaleReadout = "At upload: " + (clip?.UploadedAvatarScale.ToString("0.000", inv) ?? "—");
                var vrcHead = TrackingRig.VrcHeadOffset;
                var vrcHand = FbikAdapter.VrcIndexHandPosLeft;
                var (headY, headZ) = c.VrcHands ? vrcHead : c.HeadOffset ?? vrcHead;
                var hand = c.VrcHands ? vrcHand : c.HandOffset ?? vrcHand;
                HeadOffsetY = OffsetCentimeters(headY);
                HeadOffsetZ = OffsetCentimeters(headZ);
                HandOffsetX = OffsetCentimeters(hand.x);
                HandOffsetY = OffsetCentimeters(hand.y);
                HandOffsetZ = OffsetCentimeters(hand.z);
                const string legacyOffsetTip = " Editing an offset applies the displayed values.";
                HeadOffsetTip = "Headset-local eye target shift, in reference avatar centimeters." +
                                (!c.VrcHands && !c.HeadOffset.HasValue ? legacyOffsetTip : "");
                HandOffsetTip = "Left hand position relative to the controller, in reference avatar centimeters. " +
                                "X is mirrored for the right hand. VRChat's values are for Index controllers." +
                                (!c.VrcHands && !c.HandOffset.HasValue ? legacyOffsetTip : "");
                AvatarScaleChanged = c.AvatarScale > 1e-4f;
                var d = new CalibrationSettings();
                int changed = 0;
                if (c.KeepFeetPlanted != d.KeepFeetPlanted) changed++;
                if (c.UprightCalibration != d.UprightCalibration) changed++;
                if (c.HangFromHeadset != d.HangFromHeadset) changed++;
                if (c.VrcHands != d.VrcHands) changed++;
                if (c.ShoulderTracking != d.ShoulderTracking) changed++;
                if (c.ProceduralFeet != d.ProceduralFeet) changed++;
                if (c.FollowAvatarScale != d.FollowAvatarScale) changed++;
                if (AvatarScaleChanged) changed++;
                if (HeadOffsetY != OffsetCentimeters(vrcHead.Y) || HeadOffsetZ != OffsetCentimeters(vrcHead.Z)) changed++;
                if (HandOffsetX != OffsetCentimeters(vrcHand.x) || HandOffsetY != OffsetCentimeters(vrcHand.y) ||
                    HandOffsetZ != OffsetCentimeters(vrcHand.z)) changed++;
                AdvancedSummary = changed == 0 ? "" : changed + " changed";
            }
            finally { _syncing = false; }
        }

        void Edit(Action<CalibrationSettings> change)
        {
            if (_syncing) return;
            if (_defaultsOnly) _calibration.EditDefaults(change);
            else _calibration.EditSettings(change);
            Sync();
        }

        partial void OnHeightModeIndexChanged(int value)
        {
            Edit(c =>
            {
                c.Height = (CalHeightMode)Math.Max(0, value);
                if (c.Height != CalHeightMode.Measure && c.Height != CalHeightMode.VRChat && c.HeightValue <= 0)
                    c.HeightValue = CalibrationService.SuggestedHeightValue(c.Height);
            });
        }

        partial void OnHeightValueChanged(double value)
        {
            Edit(c => c.HeightValue = (float)Math.Max(0, value));
        }

        partial void OnSpineIndexChanged(int value)
        {
            if (value < 0 || value >= SpineModes.Count) return;
            Edit(c =>
            {
                c.FollowVrcSpine = value == FixedSpineModes.Length;
                if (!c.FollowVrcSpine) c.Lock = (CalLock)value;
            });
        }

        partial void OnMeasureIndexChanged(int value)
        {
            if (value < 0 || value >= MeasureModes.Count) return;
            Edit(c =>
            {
                c.FollowVrcMeasure = value == FixedMeasureModes.Length;
                if (!c.FollowVrcMeasure) c.Measure = (CalMeasure)value;
            });
        }

        partial void OnUseHipsChanged(bool value)
        {
            Edit(c => c.UseHips = value);
        }

        partial void OnUseChestChanged(bool value)
        {
            Edit(c => c.UseChest = value);
        }

        partial void OnUseFeetChanged(bool value)
        {
            Edit(c => c.UseFeet = value);
        }

        partial void OnUseKneesChanged(bool value)
        {
            Edit(c => c.UseKnees = value);
        }

        partial void OnUseElbowsChanged(bool value)
        {
            Edit(c => c.UseElbows = value);
        }

        partial void OnKeepFeetPlantedChanged(bool value)
        {
            Edit(c => c.KeepFeetPlanted = value);
        }

        partial void OnUprightCalibrationChanged(bool value)
        {
            Edit(c => c.UprightCalibration = value);
        }

        partial void OnHangFromHeadsetChanged(bool value)
        {
            Edit(c => c.HangFromHeadset = value);
        }

        partial void OnVrcHandsChanged(bool value)
        {
            Edit(c =>
            {
                c.VrcHands = value;
                if (!value) InitializeOffsets(c);
            });
        }

        partial void OnShoulderTrackingChanged(bool value)
        {
            Edit(c => c.ShoulderTracking = value);
        }

        partial void OnProceduralFeetChanged(bool value)
        {
            Edit(c => c.ProceduralFeet = value);
        }

        partial void OnFollowAvatarScaleChanged(bool value)
        {
            Edit(c => c.FollowAvatarScale = value);
        }

        partial void OnAvatarScaleChanged(double value)
        {
            Edit(c => c.AvatarScale = (float)Math.Max(0, value));
        }

        static double OffsetCentimeters(float metres)
        {
            return Math.Round(metres * 100d, 2);
        }

        static void InitializeOffsets(CalibrationSettings settings)
        {
            settings.HeadOffset ??= TrackingRig.VrcHeadOffset;
            settings.HandOffset ??= FbikAdapter.VrcIndexHandPosLeft;
        }

        void EditOffset(double value, Action<CalibrationSettings, float> change)
        {
            if (_syncing || VrcHands) return;
            float metres = (float)(value / 100);
            if (!float.IsFinite(metres))
            {
                Sync();
                return;
            }

            Edit(c =>
            {
                InitializeOffsets(c);
                change(c, metres);
            });
        }

        partial void OnHeadOffsetYChanged(double value)
        {
            EditOffset(value, static (c, m) => c.HeadOffset = (m, c.HeadOffset.GetValueOrDefault().Z));
        }

        partial void OnHeadOffsetZChanged(double value)
        {
            EditOffset(value, static (c, m) => c.HeadOffset = (c.HeadOffset.GetValueOrDefault().Y, m));
        }

        partial void OnHandOffsetXChanged(double value)
        {
            EditOffset(value, static (c, m) => c.HandOffset = c.HandOffset.GetValueOrDefault() with { x = m });
        }

        partial void OnHandOffsetYChanged(double value)
        {
            EditOffset(value, static (c, m) => c.HandOffset = c.HandOffset.GetValueOrDefault() with { y = m });
        }

        partial void OnHandOffsetZChanged(double value)
        {
            EditOffset(value, static (c, m) => c.HandOffset = c.HandOffset.GetValueOrDefault() with { z = m });
        }

        [RelayCommand]
        void ResetAvatarScale()
        {
            Edit(c => c.AvatarScale = 0);
        }
    }
}
