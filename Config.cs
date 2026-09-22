#define MOUSE_AND_KEYBOARD_LAYER

using System.Xml.Linq;
using SharpPluginLoader.Core.Configuration;
using SharpPluginLoader.Core.IO;

namespace NewCamera
{
    internal class Config : IConfig
    {
        public String Name => "NewCamera";
        public String Version => "4.1";

        public const float DEFAULT_FOV = 90.0f;
        public const float DEFAULT_NEAR_CLIP = 1.0f;

        public struct Settings
        {
            public const float DEFAULT_SPEED = 5.35f;
            public const float DEFAULT_SPEED_MODIFIER = 0.40f;
            public const float DEFAULT_SENSITIVITY = 0.0425f;
            public const float DEFAULT_ZOOM_SPEED = 0.02f;
            public const float DEFAULT_PITCH_LIMIT = -1.0f;
            public const float MAX_PITCH_LIMIT = 89.95f;
            public const int DEFAULT_DEADZONE = 4750;
            public const float DEFAULT_ALT_NEAR_CLIP = 0.3f;

            public Settings()
            {
                Speed = DEFAULT_SPEED;
                SpeedModifier = DEFAULT_SPEED_MODIFIER;
                Sensitivity = DEFAULT_SENSITIVITY;
                ZoomSpeed = DEFAULT_ZOOM_SPEED;
                PitchLimit = DEFAULT_PITCH_LIMIT;
                StickDeadzone = DEFAULT_DEADZONE;
                AlternateNearClip = DEFAULT_ALT_NEAR_CLIP;
            }

            public float Speed { get; set; }
            public float SpeedModifier { get; set; }
            public float Sensitivity { get; set; }
            public float ZoomSpeed { get; set; }
            public float PitchLimit { get; set; }
            public int StickDeadzone { get; set; }
            public float AlternateNearClip { get; set; }

            public struct Binds
            {
                public Binds()
                {
                    EnableCombo = true;
                    FreeCameraCombo = "RStick,LT";
                    DisableComboButton1UnlessButton2Held = false;
#if MOUSE_AND_KEYBOARD_LAYER
                    EnableMouse = true;
                    MouseSensitivity = 0.0225f;
                    EnableKeyboard = true;
                    KeyboardLookSensitivity = 19750;
#endif
                }

                public bool EnableCombo { get; set; }
                public String FreeCameraCombo { get; set; }
                public bool DisableComboButton1UnlessButton2Held { get; set; }
#if MOUSE_AND_KEYBOARD_LAYER
                public bool EnableMouse { get; set; }
                public float MouseSensitivity { get; set; }
                public bool EnableKeyboard { get; set; }
                public int KeyboardLookSensitivity { get; set; }
#endif
            }
        }

        public struct Preset
        {
            public float FieldOfView { get; set; }
            public float Roll { get; set; }
            public float Forward { get; set; }
            public float Right { get; set; }
            public float Up { get; set; }
            public bool DisableFading { get; set; }
        }

        public static Preset DefaultPreset = new Preset()
        {
            FieldOfView = DEFAULT_FOV,
            Roll = 0.0f,
            Forward = 0.0f,
            Right = 0.0f,
            Up = 0.0f,
            DisableFading = false
        };

        public struct Position
        {
            public float FieldOfView { get; set; }
            public float Roll { get; set; }
            public float PosX { get; set; }
            public float PosY { get; set; }
            public float PosZ { get; set; }
            public float TargetX { get; set; }
            public float TargetY { get; set; }
            public float TargetZ { get; set; }
        }

        // Everything the user has to re-set up each session (Enable Free
        // Camera, the Toggles section, which armor parts are force-hidden,
        // debug near clip, and the slow-motion hotkey speed), so none of it
        // needs to be re-entered by hand every time the game is launched.
        public struct SessionState
        {
            public SessionState()
            {
                EnableFreeCamera = false;
                UnlockInput = false;
                UnlockPlayerMovement = false;
                OrbitPlayer = false;
                IgnoreCameraDirection = false;
                DecoupleMovementFromLook = true;
                DecoupleMovementInvert = true;
                HideHelmet = false;
                HideHair = false;
                HideFace = true;
                HideEyeLens = true;
                DebugNearClip = 1.0f;
                AltNearClipToggled = false;
                CameraFov = DEFAULT_FOV;
                SlowMotionSpeed = 0.1f;
            }

            public bool EnableFreeCamera { get; set; }
            public bool UnlockInput { get; set; }
            public bool UnlockPlayerMovement { get; set; }
            public bool OrbitPlayer { get; set; }
            public bool IgnoreCameraDirection { get; set; }
            public bool DecoupleMovementFromLook { get; set; }
            public bool DecoupleMovementInvert { get; set; }
            public bool HideHelmet { get; set; }
            public bool HideHair { get; set; }
            public bool HideFace { get; set; }
            public bool HideEyeLens { get; set; }
            public float DebugNearClip { get; set; }
            public bool AltNearClipToggled { get; set; }
            public float CameraFov { get; set; }
            public float SlowMotionSpeed { get; set; }
        }

        // A single point on a scripted "gaze curve": at motion progress T
        // (0.0 = motion start, 1.0 = motion end), look Yaw/Pitch/Roll
        // degrees away from the Base Rotation Joint's direction. See
        // OrbitProfile.UseGazeKeyframes below - an alternative to
        // Range/Blend for motions whose raw per-frame joint deviation is
        // too fast/noisy to track directly.

        //ver10
        public struct GazeKeyframe
        {
            public GazeKeyframe()
            {
                T = 0.0f;
                Yaw = 0.0f;
                Pitch = 0.0f;
                Roll = 0.0f;
            }

            public float T { get; set; }
            public float Yaw { get; set; }
            public float Pitch { get; set; }
            public float Roll { get; set; }
        }

        // Gaze Keyframes を「モーションのこの区間の間だけ」効かせるための窓。
        // Start/End はモーション進行度 T (0.0 = 開始, 1.0 = 終了)。この窓の中では
        // Gaze Keyframes が使われ、外では通常の Range/Blend 追従に戻る。
        // Blend は窓の前後に足されるクロスフェード幅 (T 単位) で、0 にすると
        // 切り替わりが瞬間的になる。窓は 1 プロファイルに複数登録でき、
        // 例えば「振りかぶり」と「構え直し」の 2 か所だけ Gaze を効かせ、
        // 間の回転部分は完全追従に任せる、といった使い方ができる。
        // See OrbitProfile.GazeUseRanges/GazeRanges below.
        //ver11
        public struct GazeRange
        {
            public GazeRange()
            {
                Start = 0.0f;
                End = 1.0f;
                Blend = 0.05f;
            }

            public float Start { get; set; }
            public float End { get; set; }
            public float Blend { get; set; }
        }

        // Use Gaze Keyframes の XYZ 版 (Use Gaze Position Keyframes) が使う
        // キーフレーム。Yaw/Pitch/Roll の代わりに、カメラの位置オフセット
        // (Target Y/Right/Forward) を T ごとに指定する。
        public struct GazePositionKeyframe
        {
            public GazePositionKeyframe()
            {
                T = 0.0f;
                Y = 0.0f;
                Right = 0.0f;
                Forward = 0.0f;
            }

            public float T { get; set; }
            public float Y { get; set; }
            public float Right { get; set; }
            public float Forward { get; set; }
        }
        //ver11ここまで

        // Mirrors Plugin.cs's OrbitProfileMode - kept as a separate,
        // identically-ordered enum here (same decoupled-mirroring pattern
        // already used by OrbitProfile/GazeKeyframe below) so Config.cs has
        // no compile-time dependency on Plugin.cs.
        public enum OrbitProfileMode
        {
            FullRotation,     // 旧 A: unclamped, follows raw face-basis rotation exactly.
            BaseOnlyIgnoreX,  // 旧 B (Ignore-X): base-only, hip-projected (X-ignored) position.
            BaseOnly,         // 旧 B: base-only, raw nose position.
            Normal            // 旧 C: clamped/spotted head-tracking follow (attacks).
        }

        // A single motion-category profile - a keyword list, a Mode
        // (FullRotation/BaseOnlyIgnoreX/BaseOnly/Normal), its own Target/
        // Correction/Smoothing/Transition settings, and (for Normal) its own
        // Yaw/Pitch/Roll Range/Blend. OrbitalCameraSettings.Profiles is
        // checked top-to-bottom - the first profile whose Keywords match
        // the current motion is used; matching none of them defaults to
        // "A" (Full Rotation, unclamped). Lets e.g. an "IDLE/WALK/RUN"
        // profile (wide Yaw/Roll, narrow Pitch, Normal mode) and a "spin
        // attacks" profile (narrow Yaw, Normal mode) and a "DASH" profile
        // (BaseOnlyIgnoreX mode) all coexist, each fully independent.
        public struct OrbitProfile
        {
            public OrbitProfile()
            {
                Mode = OrbitProfileMode.Normal;
                Keywords = new List<string>();
                YawRange = 70.0f;
                YawBlend = 25.0f;
                PitchRange = 55.0f;
                PitchBlend = 25.0f;
                RollRange = 25.0f;
                RollBlend = 15.0f;

                //ver10.1
                UseGazeKeyframes = false;
                GazeKeyframes = new List<GazeKeyframe>();
                // GUI のプロファイル見出しにそのまま表示される自由記入のメモ。
                // ツリーを開かなくても何のプロファイルか分かるようにするためのもので、
                // マッチング判定には一切使われない。NewCamera.json に保存される。
                Comment = "";
                WeaponGroup = "";//ver14
                // UseGazeKeyframes をモーション全体ではなく、特定の T 区間の間だけ
                // 有効にする。false = 従来どおりモーション全体で有効。
                GazeUseRanges = false;
                GazeRanges = new List<GazeRange>();
                // Gaze Keyframes / GazeRanges が使う T (0.0-1.0) を、モーション
                // 進行度の代わりに「ActionController+0x760 のサブステート値が
                // 変化してからの経過時間」で代用する。ActionName/motionKey が
                // 変わらないまま上半身側の状態だけ変わる場面 (回復薬を飲む、
                // 弓にビンを装填する等) は、ベースモーションのフレーム進行と
                // 実際の副次モーションの進行が一致しないため、こちらを使う。
                //ver11
                GazeUseSubStateTimer = false;
                // ↑を ON にしたときの正規化用の想定所要時間 (秒)。
                // 経過時間をこの値で割った値 (0-1にクランプ) を T として使う。
                GazeSubStateDuration = 1.0f;
                // Use Gaze Keyframes の XYZ 版。ON にすると、Target Y/Right/Forward
                // を GazePositionKeyframes の T ごとの値で上書きする (T の取得元は
                // 上の GazeUseSubStateTimer 設定を共有)。
                UseGazePositionKeyframes = false;
                GazePositionKeyframes = new List<GazePositionKeyframe>();
                //ver11ここまで

                // Defaults match OrbitalCameraSettings.TargetY/Right/Forward's
                // own default (0/0/0) - i.e. until tuned per-profile, a
                // motion offsets the eye exactly like the top-level default
                // used for every unmatched motion, so existing setups don't
                // shift on upgrade.
                TargetY = 0.0f;
                TargetRight = 0.0f;
                TargetForward = 0.0f;
                // Defaults match OrbitalCameraSettings.ClampBaseCorrectionYaw/
                // Pitch/Roll's own default (180/0/0) - i.e. until tuned
                // per-profile, a motion's baseline facing direction is
                // identical to the top-level default, so existing setups
                // don't shift on upgrade.
                CorrectionYaw = 180.0f;
                CorrectionPitch = 0.0f;
                CorrectionRoll = 0.0f;
                // Matches OrbitalCameraSettings.ClampBaseSmoothing's own
                // default for the same reason.
                ClampBaseSmoothing = 0.5f;
                // 0 = disabled. Freezes the raw joint deviation for this many
                // seconds starting the instant R2 is pressed/released.
                // Only meaningful for Normal-mode profiles.
                ShakeSuppressSeconds = 0.0f;
                // 1.0 = same R-stick response as normal look. Lets e.g.
                // AIM_IDLE's R-stick speed be tuned to actually match the
                // base game's own reticle movement speed, independent of
                // whatever feels right for ordinary free-look. Only
                // meaningful for Normal-mode profiles.
                LookSensitivityMultiplier = 1.0f;
                // When true, this profile uses the game's own native camera
                // pose directly (captured in CalculateCameraHook, see
                // Plugin.cs orbitNativeCameraPosition/Target) instead of
                // this mod's own joint+clamp+R-stick reconstruction -
                // sidesteps drift/mismatch entirely for motions where the
                // native camera is already known-correct (e.g. AIM_IDLE
                // with Free Camera disabled always shows the reticle
                // dead-center). Experimental - verify in-game. Only
                // meaningful for Normal-mode profiles.
                UseNativeAimCamera = false;
                UseNativeAimCameraDelaySeconds = 0.0f;
                DisableLookWhileAiming = false;
                EnableTransitionBlend = true;
                // Matches OrbitalCameraSettings.ProfileTransitionDuration's
                // own default for the same reason.
                ProfileTransitionDuration = 0.2f;
                //ver12 Position/Basis Joint Override(飲みモーション等の手元カメラ用)
                PositionJointOverrideEnable = false;
                PositionJointOverrideUseFace = false;
                PositionJointOverrideJoint = 0;
                //ver12ここまで
                HideSlingerWhileActive = false;//ver13
                DisableFreeCameraWhileActive = false;//ver15
            }

            public OrbitProfileMode Mode { get; set; }
            public List<string> Keywords { get; set; }
            public float YawRange { get; set; }
            public float YawBlend { get; set; }
            public float PitchRange { get; set; }
            public float PitchBlend { get; set; }
            public float RollRange { get; set; }
            public float RollBlend { get; set; }

            // This profile's own baseline yaw/pitch/roll - i.e. what
            // "0 deviation" (dead ahead, forward) actually points toward
            // for this profile, on top of the base joint's own direction.
            // Independent of every other profile's own, and of the
            // top-level default (ClampBaseCorrectionYaw/Pitch/Roll) used
            // only when no profile matches at all. Fixes a fixed angular
            // offset (e.g. a reticle sitting a few degrees left of true
            // center) that a position offset (TargetY/Right/Forward below)
            // can't - that only shifts the eye's position, not which way it
            // points.
            public float CorrectionYaw { get; set; }
            public float CorrectionPitch { get; set; }
            public float CorrectionRoll { get; set; }

            // This profile's own low-pass filter strength on
            // clampBaseRotation - 1.0 = raw/instant, lower = smoother.
            // Independent of every other profile's own, and of the
            // top-level default (ClampBaseSmoothing) used only when no
            // profile matches at all.
            public float ClampBaseSmoothing { get; set; }

            // Per-axis spike cap (deg/s) on how fast the *raw joint's own*
            // deviation (before Range/Blend) is allowed to change frame to
            // frame while this profile is active - separate from, and much
            // more aggressive than, the global "Stabilize Rotation"
            // smoother (which is tuned for ordinary idle-sway noise, not
            // for rejecting a single-frame animation-driven jolt like a
            // bowstring release kick). Frame-to-frame changes faster than
            // this are clipped to this rate instead of passed through, so a
            // momentary jolt gets capped rather than snapping the view; a
            // sustained real head-turn just takes an extra frame or two to
            // catch up, which isn't perceptible. 0 = disabled. Only
            // meaningful for Normal-mode profiles.
            public float ShakeSuppressSeconds { get; set; }
            public float LookSensitivityMultiplier { get; set; }
            public bool UseNativeAimCamera { get; set; }
            public float UseNativeAimCameraDelaySeconds { get; set; }
            public bool DisableLookWhileAiming { get; set; }

            // Whether switching into/out of this profile triggers the
            // fixed-duration Slerp blend, and how long that blend takes for
            // THIS profile specifically.
            public bool EnableTransitionBlend { get; set; }
            public float ProfileTransitionDuration { get; set; }

            // This profile's own Target Y/Right/Forward eye-position
            // offset (see OrbitalCameraSettings.TargetY/Right/Forward for
            // the top-level default used when no profile matches) - lets a
            // motion push the eye forward/back or up/down differently than
            // the default, independent of every other profile's own offset.
            public float TargetY { get; set; }
            public float TargetRight { get; set; }
            public float TargetForward { get; set; }

            // When true, this profile ignores Range/Blend above entirely
            // and instead looks up a Yaw/Pitch/Roll target from
            // GazeKeyframes, keyed by how far the current motion has
            // played (0..1) and Slerp-interpolated. See
            // evaluateGazeKeyframes() in Plugin.cs. Only meaningful for
            // Normal-mode profiles.
            //ver11
            public bool UseGazeKeyframes { get; set; }
            public List<GazeKeyframe> GazeKeyframes { get; set; }
            public string Comment { get; set; }
            public string WeaponGroup { get; set; }//ver14
            public bool GazeUseRanges { get; set; }
            public List<GazeRange> GazeRanges { get; set; }
            public bool GazeUseSubStateTimer { get; set; }
            public float GazeSubStateDuration { get; set; }
            public bool UseGazePositionKeyframes { get; set; }
            public List<GazePositionKeyframe> GazePositionKeyframes { get; set; }
            //ver12
            public bool PositionJointOverrideEnable { get; set; }
            public bool PositionJointOverrideUseFace { get; set; }
            public int PositionJointOverrideJoint { get; set; }
            //ver12ここまで

            // ver13: この動作カテゴリ (Keywords がマッチしている間) だけ、
            // スリンガーを強制非表示にする。狩猟中は「最後のPartをオフ
            // にするとスリンガー全体が消える」現象を利用しており、狩猟中
            // でも確実に効くことを確認済み(Body防具の個別パーツ非表示とは
            // 異なり、こちらは全パーツ非表示なので問題ない)。
            public bool HideSlingerWhileActive { get; set; }

            // ver15: この動作カテゴリ (Keywords がマッチしている間) だけ、
            // Enable Free Camera を強制的にオフにする(=ゲーム本来の
            //三人称視点に戻す)。クラッチクロー・しがみつき中や乗り状態など、
            // 一人称視点だと画面が揺れ過ぎる場面向け。マッチしなくなったら、
            // ユーザーが元々選んでいたEnable Free Cameraの状態に自動で戻す。
            public bool DisableFreeCameraWhileActive { get; set; }
        }
        //ver11ここまで

        // The Orbital Camera's face-lock position/rotation setup, plus the
        // unified motion-category profile system built on top of it.
        public struct OrbitalCameraSettings
        {
            public OrbitalCameraSettings()
            {
                TargetFaceJoint = false;
                TargetJoint = 1;
                SimpleLock = false;
                FaceBasisEnable = false;
                FaceBasisCenterJoint = 2;
                FaceBasisRightEarJoint = 11;
                FaceBasisLeftEarJoint = 12;
                // Fallback/default eye-position offset - used only when no
                // Profiles entry matches the current motion at all
                // (defaults to "A", unclamped).
                TargetY = 0.0f;
                TargetRight = 0.0f;
                TargetForward = 0.0f;
                //ver8 フリールック設定値保存
                Lerp = 0.25f;
                RecenterTapMaxSeconds = 0.3f;//ver9.3
                ReturnToCenterLook = true;
                ReturnToCenterYawMax = 60.0f;
                ReturnToCenterPitchMax = 45.0f;
                ReturnToCenterSpeed = 0.3f;
                RightStickAngleSnapSteps = 100;
                //ver8ここまで
                // Every motion-category profile - Full Rotation ("A"),
                // Base-Only Ignore-X ("B (Ignore-X)"), Base-Only ("B"), and
                // Normal ("C", clamped/spotted) alike - now lives in this one
                // list, checked top-to-bottom, first match wins. See
                // OrbitProfile above.
                Profiles = new List<OrbitProfile>();
                FaceClampEnable = true;
                BaseRotationJoint = -1;
                BaseRotationJointUseFace = false;
                // Fallback/default baseline yaw/pitch/roll - used only when
                // no Profiles entry matches the current motion at all.
                ClampBaseCorrectionYaw = 180.0f;
                ClampBaseCorrectionPitch = 0.0f;
                ClampBaseCorrectionRoll = 0.0f;
                ClampSmoothing = 0.3f;
                // Fallback/default Base Smoothing - used only when no
                // Profiles entry matches the current motion at all.
                ClampBaseSmoothing = 0.5f;
                NeckYawRange = 70.0f;
                NeckYawBlend = 25.0f;
                NeckPitchRange = 55.0f;
                NeckPitchBlend = 25.0f;
                NeckRollRange = 25.0f;
                NeckRollBlend = 15.0f;
                FinalRotationMaxSpeed = 1080.0f;
                FinalPositionMaxSpeed = 3000.0f;
                ForceLevelRoll = true;
                SpotDistance = 500.0f;
                SpotReanchorRate = 0.5f;
                // Fallback/default Profile Switch Blend Time and Enable
                // Transition Blend - used only when no Profiles entry
                // matches the current motion at all.
                ProfileTransitionDuration = 0.2f;
                DefaultEnableTransitionBlend = true;
                // Fixed calibration constant for Ignore Camera Direction's
                // movement-angle math (see CheckMovementHook in Plugin.cs) -
                // compensates a small residual offset between Base Rotation
                // Joint's reconstructed forward and what the game reads as
                // camera-forward for movement. Empirically calibrated;
                // anything in ~179.40-179.55 tested stable over 100m.
                MovementRotation = 179.475f;
                StickSnapAngleDeg = 5.0f;
            }

            public bool TargetFaceJoint { get; set; }
            public int TargetJoint { get; set; }
            public bool SimpleLock { get; set; }
            public bool FaceBasisEnable { get; set; }
            public int FaceBasisCenterJoint { get; set; }
            public int FaceBasisRightEarJoint { get; set; }
            public int FaceBasisLeftEarJoint { get; set; }
            public float TargetY { get; set; }
            public float TargetRight { get; set; }
            public float TargetForward { get; set; }
            public float Lerp { get; set; }
            //ver8 フリールック設定値保存
            public float RecenterTapMaxSeconds { get; set; }//ver9.3
            public bool ReturnToCenterLook { get; set; }
            public float ReturnToCenterYawMax { get; set; }
            public float ReturnToCenterPitchMax { get; set; }
            public float ReturnToCenterSpeed { get; set; }
            public int RightStickAngleSnapSteps { get; set; }
            // ver8ここまで
            public List<OrbitProfile> Profiles { get; set; }
            public bool FaceClampEnable { get; set; }
            public int BaseRotationJoint { get; set; }
            public bool BaseRotationJointUseFace { get; set; }
            public float ClampBaseCorrectionYaw { get; set; }
            public float ClampBaseCorrectionPitch { get; set; }
            public float ClampBaseCorrectionRoll { get; set; }
            public float ClampSmoothing { get; set; }
            public float ClampBaseSmoothing { get; set; }
            public float NeckYawRange { get; set; }
            public float NeckYawBlend { get; set; }
            public float NeckPitchRange { get; set; }
            public float NeckPitchBlend { get; set; }
            public float NeckRollRange { get; set; }
            public float NeckRollBlend { get; set; }
            public float FinalRotationMaxSpeed { get; set; }
            public float FinalPositionMaxSpeed { get; set; }
            public bool ForceLevelRoll { get; set; }
            public float SpotDistance { get; set; }
            public float SpotReanchorRate { get; set; }
            // Fixed duration (seconds) of the Slerp blend triggered the
            // instant Profiles' matched index changes. 0 disables it
            // (falls back to FinalRotationMaxSpeed alone for smoothing out
            // the jump). Fallback only - used when no Profiles entry
            // matches the current motion at all; every profile has its own
            // ProfileTransitionDuration (OrbitProfile.ProfileTransitionDuration
            // above) that takes priority whenever one matches. See
            // Plugin.cs orbitProfileTransitionDuration/
            // orbitCurrentTransitionDuration.
            public float ProfileTransitionDuration { get; set; }
            // Fallback only - used when no Profiles entry matches the
            // current motion at all; every profile has its own
            // EnableTransitionBlend that takes priority whenever one
            // matches.
            public bool DefaultEnableTransitionBlend { get; set; }
            public float MovementRotation { get; set; }
            public float StickSnapAngleDeg { get; set; }
        }

        private static Dictionary<string, Button> buttonMap = new Dictionary<string, Button>
        {
            { "r1", Button.R1 }, { "rb", Button.R1 },
            { "r2", Button.R2 }, { "rt", Button.R2 },
            { "r3", Button.R3 }, { "rstick", Button.R3 },
            { "l1", Button.L1 }, { "lb", Button.L1 },
            { "l2", Button.L2 }, { "lt", Button.L2 },
            { "l3", Button.L3 }, { "lstick", Button.L3 },
            { "a", Button.Cross }, { "cross", Button.Cross },
            { "b", Button.Circle }, { "circle", Button.Circle },
            { "x", Button.Square }, { "square", Button.Square },
            { "y", Button.Triangle }, { "triangle", Button.Triangle },
            { "start", Button.Options }, { "options", Button.Options },
            { "select", Button.Share }, { "share", Button.Share },
            { "up", Button.Up },
            { "down", Button.Down },
            { "left", Button.Left },
            { "right", Button.Right }
        };

        public static Button[]? ParseCombo(String comboString)
        {
            string[] buttonStrings = comboString.Split(",");
            if (buttonStrings.Length == 2)
            {
                string b1 = buttonStrings[0].ToLower();
                string b2 = buttonStrings[1].ToLower();
                if (buttonMap.ContainsKey(b1) && buttonMap.ContainsKey(b2))
                {
                    return [ buttonMap[b1], buttonMap[b2] ];
                }
            }
            return null;
        }

        public static Button? ParseButton(String buttonString)
        {
            string b = buttonString.Trim().ToLower();
            if (buttonMap.ContainsKey(b))
            {
                return buttonMap[b];
            }
            return null;
        }

        public bool DisableMod { get; set; } = false;
        public bool TuningToolInterop = true;
        public bool OverrideViewMode { get; set; } = false;

        public Settings Camera { get; set; } = new Settings();
        public Settings.Binds Binds { get; set; } = new Settings.Binds();

        public bool PerspectiveCameraEnabled { get; set; } = false;
        public Dictionary<String, Preset> Presets { get; set; } = new Dictionary<String, Preset>();
        public String Selected { get; set; } = "";

        public Dictionary<String, Position> Positions { get; set; } = new Dictionary<String, Position>();

        public SessionState Session { get; set; } = new SessionState();
        public OrbitalCameraSettings OrbitalCamera { get; set; } = new OrbitalCameraSettings();
    }
}
