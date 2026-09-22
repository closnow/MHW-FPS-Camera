//#define ENABLE_ASSERTS
//#define HOOK_ORDER_ASSERTS
//#define LOG_DEBUG_MESSAGES
#define MOUSE_AND_KEYBOARD_LAYER
//#define QUARANTINED_FEATURES

using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
#if ENABLE_ASSERTS
using System.Diagnostics;
#endif

using ImGuiNET;
using SharpPluginLoader.Core;
using SharpPluginLoader.Core.MtTypes;
using SharpPluginLoader.Core.Entities;
using SharpPluginLoader.Core.IO;
using SharpPluginLoader.Core.View;
using SharpPluginLoader.Core.Memory;
using SharpPluginLoader.Core.Actions;
using SharpPluginLoader.Core.Components;
using SharpPluginLoader.Core.Configuration;
using System.IO;//Cのログ

// Tentative List of Actions:
//  - Toggle Free Camera
//  - Unlock Player Movement
//  - Lock Camera Vertical Movement
//  - Apply Speed Modifier
//  - Unlock Input
//  - Toggle UI
//  - Toggle Depth of Field
//  - Toggle Motion Blur
//  - Toggle Reduced Near Clip
//  - Translate Camera
//  - Roll Camera
//  - Reset Roll
//  - Zoom
//  - Reset Zoom
//  - Teleport Player to Camera
//  - Teleport Camera to Player
//  - Crawl
//  - Sit in Hot Springs
//  - Adjust SSAO/General A/B Graphical Tweaks
//  - Freeze Game
//  - Exit Free Camera
//  - Open Gestures Menu
//  - Open Poses Menu

// @TODO:
//  - Cleanup math.
//  - pCamera refactor.
//  - Attempt to document all test cases I can think of.
//  - Centralize all bindings to get a better idea of how to structure custom binds.
//  - Pass on ImGui flicker.
//  - Simplify setting ImGui width.
//  - WASD for ignore camera direction.
//  - Make audio like waterfall follow camera position instead of player position.
//  - Audio high/lowpass possible?
//  - Save toggle state to config file.
//   - Include Hide Weapon.
//  - Hunter effects:
//   - Sweating
//   - Steamy
//   - On Fire
//   - Dung Pod?
//   - Other Monster Inflicted Debuffs
//  - Camera "Gear Shift".
//  - Option to not adjust camera position on camera change.
//   - Better calculation for positioning camera behind the player.
//  - Underwater camera crashes in The Rotten Vale.
//  - Make left stick input a curve like Wilds.
//   - Deadzone? MonsterHunterWorld.exe+5831DB - comiss xmm1,[MonsterHunterWorld.exe+503C438]
//  - Ability to lock camera to a the player/a joint.
//  - Simplify and document input handling/blocking logic.
//   - Find a way to block other inputs while button1 is down (and blocked).
//  - More multiple controller testing (3 controllers, Steam Input, Windows).
//  - Thoroughly test "Disable Mod".
//  - Improve AOB scans.
//  - Better name for unknownPtr.

// Known Issues:
//  - Camera up not respecting collision can badly bug out culling.
//  - Camera pitch wrap around while in the tent will sometimes flicker at the point of wrapping.
//   - Likely due to the unpredictable position of SetCameraTentHook() in the chain of hooks. Updating
//     free camera state within SetCameraTentHook() is not a solution because it comes out laggy.
//  - With Passive mode enabled out in the field, if you go into a tent, change equipment and quickly leave
//    the tent the game freezes/uses the wrong camera.

// Steam proton launch command:
//  env WINEDLLOVERRIDES="ucrtbase,dinput8=n,b" %command%
//  export CMD=$(echo -n "%command%" | sed "s/\/MonsterHunterWorld.exe/\/SPLLauncher.exe/") && WINEDLLOVERRIDES="winmm,dinput8=n,b" eval proton-env $CMD

// Test cases:
//  Offset Perspective:
//   - For all cases roll should an extra consideration.
//   Constant FOV:
//    - In quest book/board GUI (uses player camera).
//   previousCameraAnimState = 8 (FOV Only):
//    - Push through roots.
//   previousCameraAnimState = 5 (FOV Only):
//    - Zoom into quest board or The Handler's book.
//    - Open map animation.
//    - Entering/Leaving tent.
//   previousCameraAnimState = 4 (FOV Only):
//    - Quest depart/return.
//    - Traveling on the lift in Astera.
//   previousCameraAnimState = 2 (Apply offset):
//    - Point camera towards quest board.
//    - Crawl under object.
//   Player camera is the target of a transition:
//    - Leaving dialoge with NPC (The Handler, The Smithy, pub lasses, etc).
//    - Getting up from hot spring.
//    - Getting up from canteen.
//    - Getting up from cart.
//    - Seasonal gathering hub cutscene.
//   Directly moves player camera:
//    - Look at monster.
//    - Scoutflies point in a direction.
//    - Mount monster.
//    - Dive.
//
//  Free Camera:
//   - Toggle rapidly and there should be no visual jump.
//   - Story cutscenes.
//   - Canteen cutscene/animation.
//   - Inside tent.
//    - Equipment select inside tent.
//
//  Disable Character Fade:
//   - NPCs that spawn invisible and fade-in have to actually fade-in.
//    - A night time in Astera, there will eventually be 3 hunters eating at the table next to the handler.
//      If this is broken, two of them will be invisible.
//
//  Player Wetness:
//   - Entering Change Equipment menu.
//   - Changing zones.

namespace NewCamera
{
    public static class TuningToolInterop
    {
        public static string PluginName = "World Tuning Tool";
        public static dynamic? Instance = null;

        public static bool InitFromLoadedInstance()
        {
#if QUARANTINED_FEATURES
            //Instance = IPlugin.GetInstance(PluginName);
#endif
            return Instance != null;
        }

        public static int AddOverride(string name, Vector4 v4, int i1)
        {
            if (Instance != null)
            {
                return Instance.AddOverride(name, v4, i1);
            }
            return 0;
        }

        public static void RemoveOverride(int id)
        {
            if (Instance != null)
            {
                Instance.RemoveOverride(id);
            }
        }
    }

    public unsafe class Plugin : IPlugin
    {
        public string Name => "New Camera";
        public string Author => "Akon City Software";

        private const float DEFAULT_FOV = Config.DEFAULT_FOV;
        private const float DEFAULT_NEAR_CLIP = Config.DEFAULT_NEAR_CLIP;

        private static void Assert(bool condition)
        {
#if ENABLE_ASSERTS
            Trace.Assert(condition);
#endif
        }

        private static nint lton(long l) { unchecked { return (nint)l; } }

        private static byte ByteFlag(bool f) { return f ? (byte)0x1 : (byte)0x0; }

        private bool disableMod = false;

        private static readonly MtObject sMain = SingletonManager.GetSingleton("sMhMain")!;
        private bool freezeGame = false;
        private bool decoupleDtFromGameTime = false;
        private int advanceFrame = 0;
        private int forceOffMotionBlurOverride = 0;

        private float cameraSpeed;
        private float cameraSpeedModifier;
        private float cameraSensitivity;
        private float cameraZoomSpeed;
        private float cameraPitchLimit;
        private int stickDeadzone;
        private float alternateNearClip;

        private float cameraFov = DEFAULT_FOV;
        private float cameraForward = 0.0f;
        private float cameraRight = 0.0f;
        private float cameraUp = 0.0f;
        private Vector3 cameraOffset;
        // Debug snapshot of the CheckMovementHook / IgnoreCameraDirection math,
        // captured each time that branch runs, for on-screen inspection.
        private float dbgCmDist = 0.0f;
        private float dbgCmMovementAngleDeg = 0.0f;
        private Vector3 dbgCmCameraPosition;
        private Vector3 dbgCmTargetBefore;
        private Vector3 dbgCmTargetAfter;
        private float cameraYaw = 0.0f;
        private float cameraPitch = 0.0f;
        private float cameraRoll = 0.0f;
        private bool disableFading = false;
        private int cameraWrapState = 0;

        private bool enableFreeCamera = false;
        private bool freeCamera = false;
        private bool freeCameraFromViewMode = false;
        private bool freeCameraFallback = false;
        private bool freeCameraNoMenu = false;
        private Camera? vCamera = null;
        // Captured from vCamera.Position/Target in CalculateCameraHook,
        // right after calculateCameraHook.Original() runs (the native game
        // camera computation for this frame) but before updateFreeCamera()
        // below overwrites vCamera with this mod's own reconstruction -
        // i.e. this is the game's own camera, including its own native
        // aim-camera behavior while aiming (see !freeCamera branch just
        // below, which already reads this same pre-overwrite state when
        // Free Camera is off). Used by OrbitProfile.UseNativeAimCamera
        // to let a specific motion (e.g. AIM_IDLE) use this directly instead
        // of this mod's own joint+clamp+R-stick reconstruction. One frame
        // of lag versus the OnUpdate() call that consumes it (captured in
        // CalculateCameraHook, read next in OnUpdate) - same tolerance
        // already relied on elsewhere in this file for cross-hook data.
        private Vector3 orbitNativeCameraPosition = Vector3.Zero;
        private Vector3 orbitNativeCameraTarget = Vector3.Zero;
        private bool orbitNativeCameraValid = false;
        // Counts down from OrbitProfile.UseNativeAimCameraDelaySeconds
        // starting the instant L2 is pressed - see its use near the L2
        // aim-state handling above, and the native-camera override below.
        private float orbitNativeAimCameraDelayTimer = 0.0f;
        // The value orbitNativeAimCameraDelayTimer was (re)started at -
        // i.e. OrbitProfile.UseNativeAimCameraDelaySeconds captured at
        // the moment the countdown began. Needed to turn the countdown into
        // a 0..1 blend progress (elapsed/total) for the pitch pre-blend
        // below; the timer alone only tells us time *remaining*.
        private float orbitNativeAimCameraDelayTotal = 0.0f;
        private int vCameraViewportIndex = -1;
        private float? restoreFov = null;
        private float? restoreRoll = null;
        private float? restoreNearClip = null;
        private Vector3 cameraPosition;
        private Vector3 cameraTarget;
        private Vector3 cameraFrame;
        private bool unlockMovementHeld = false;
        private bool unlockMovementToggled = false;
        private bool unlockMovementPause = false;
        private bool playerMovementLocked => !(unlockMovementHeld || unlockMovementToggled) || unlockMovementPause;
        private bool lockVerticalToggled = false;

        private NativeFunction<nint, int, int, nint> getViewParamOffset;

        private bool orbitPlayer = false;
        private bool orbitIgnoreCamera = false;
        // Independent of orbitIgnoreCamera above (that mechanism - the
        // vCamera.Target override in CheckMovementHook - is left untouched
        // but is NOT used here per user report that it doesn't work).
        // Rewritten from scratch: rotates the raw left-stick axes directly
        // in WritePadInputHook so the body keeps moving relative to the
        // stable A/B/C base rotation even while R-stick free-look
        // (cameraYaw/cameraPitch) has turned the view away from it. See
        // orbitCachedCameraForwardYawDeg/orbitCachedStableForwardYawDeg,
        // cached once per frame right after "rotation" is finalized.
        private bool orbitDecoupleMovementFromLook = true;
        private float orbitCachedCameraForwardYawDeg = 0.0f;
        private float orbitCachedStableForwardYawDeg = 0.0f;
        private bool orbitCachedForwardYawValid = false;
        // Flip this if, in-game, holding a look angle makes movement pull
        // *further* off-axis instead of going straight - that means the
        // correction below is being applied backwards for this build's
        // stick/yaw handedness.
        private bool orbitDecoupleMovementInvert = true;
        private float orbitMovementRotation = 179.475f;
        private float orbitStickSnapAngleDeg = 5.0f;
        private float orbitDistance = 350.0f;
        private float orbitY = 150.0f;
        private float orbitRight = 0.0f;
        private float orbitForward = 0.0f;
        // orbitY/orbitRight/orbitForward above now serve specifically as the
        // "default (unmatched motion)" eye-position offset - i.e. what's used
        // whenever no OrbitProfile (see OrbitProfile.TargetY/Right/Forward
        // below) matches the current motion at all (falls back to "A"
        // behavior, unclamped). Every profile - regardless of its Mode
        // (Full Rotation / Base-Only / Base-Only Ignore-X / Normal) - carries
        // its own independent Target Y/Right/Forward instead of sharing one
        // global triple per mode. Selected each frame by
        // getActiveOrbitTargetOffset().
        private float orbitLerp = 0.25f;
        private int orbitJoint = -1;
        private bool orbitTargetFaceJoint = false;
        private bool orbitSimpleLock = true;
        private int orbitSimpleRotationJoint = 1;
        private bool orbitSimpleRotationJointUseFace = false;
        private bool orbitFaceBasisEnable = true;
        private int orbitFaceBasisCenterJoint = 2;
        private int orbitFaceBasisRightEarJoint = 11;
        private int orbitFaceBasisLeftEarJoint = 12;
        private Vector3 orbitFaceBasisPrevUp = Vector3.Zero;
        private bool orbitFaceBasisPrevUpInit = false;
        // Tracks which action/motion the anti-flip "up" memory above was
        // last built during, so it can be reset at motion boundaries - see
        // the reset check right before the face-basis block below.
        private string orbitFaceBasisLastActionKey = "";
        // FPS/head-lock fix: setCameraRoll() used to always force camera.Up to a
        // world-space vector derived only from the manual free-look roll input
        // (cameraRoll), completely independent of the face-joint-derived
        // "rotation" used for the look-at Target. That mismatch is what made a
        // front-flip look like a "roll to correct itself" instead of a clean
        // pitch: the Target direction would swing through a full loop while Up
        // stayed pinned near world-up, forcing the engine's internal basis
        // reconstruction to twist the view to reconcile the two. When active,
        // these let setCameraRoll() instead take Up from the *same* rotation
        // quaternion that produced the Target direction, so pitch/roll/yaw stay
        // physically consistent through full rotations (dodge rolls, spins).
        private bool orbitFaceLockedUpActive = false;
        private Vector3 orbitFaceLockedUpRaw = Vector3.UnitY;
        private Vector3 orbitFaceLockedUpSmoothed = Vector3.UnitY;
        private int orbitRotationJoint = 1;
        private bool orbitRotationJointUseFace = false;
        private float orbitRotationJointYawOffset = 0.0f;
        private float orbitRotationJointPitchOffset = 0.0f;
        private float orbitRotationJointRollOffset = 0.0f;
        private int orbitBaseRotationJoint = -1;
        private bool orbitBaseRotationJointUseFace = false;
        // Per-axis "human neck range": below this angle of deviation (relative to
        // the base joint), that axis stays locked to the base joint. Beyond it,
        // blends toward the dynamic joint over the "Blend" width, capped at
        // "Max Follow" degrees (so continuous multi-rotation spins - e.g. hammer
        // spin attacks - don't spin the camera indefinitely like a ballerina;
        // it saturates instead).
        private float orbitNeckYawRange = 70.0f;
        private float orbitNeckYawBlend = 25.0f;
        private float orbitNeckYawMaxFollow = 110.0f;
        private float orbitNeckPitchRange = 55.0f;
        private float orbitNeckPitchBlend = 25.0f;
        private float orbitNeckPitchMaxFollow = 130.0f;
        private float orbitNeckRollRange = 25.0f;
        private float orbitNeckRollBlend = 15.0f;
        private float orbitNeckRollMaxFollow = 60.0f;
        private float orbitLastRelYaw = 0.0f;
        private float orbitLastRelPitch = 0.0f;
        private float orbitLastRelRoll = 0.0f;
        // Simple Lock "neck clamp": reuses the Base Rotation Joint / Neck
        // Range / Blend / Max Follow fields above (same as the non-Simple-Lock
        // Rotation Joint blend), but applied to the face-basis rotation, and
        // skipped entirely during "full-body rotation" motions (see keyword
        // list) so those still track the raw, unclamped face-basis rotation.
        private bool orbitFaceClampEnable = true;
        // player.Rotation's own forward/up axis convention doesn't necessarily
        // match the face-basis rotation's convention. Rather than guess it in
        // code, these let it be dialed in live: while standing still facing
        // normally, adjust these until the Deviation readout below reads ~0
        // on all three axes.
        private float orbitClampBaseCorrectionYaw = 180.0f;
        private float orbitClampBaseCorrectionPitch = 0.0f;
        private float orbitClampBaseCorrectionRoll = 0.0f;
        // Fallback/default baseline yaw/pitch/roll - used only when no
        // OrbitProfile matches the current motion at all (default "A").
        // Every profile carries its own independent CorrectionYaw/Pitch/Roll
        // (see OrbitProfile.CorrectionYaw/Pitch/Roll below), selected each
        // frame by getActiveOrbitBaseCorrection() whenever one matches.
        // R2-press/release-triggered shake suppression state for
        // OrbitProfile.ShakeSuppressSeconds - see its use at the "C"
        // raw-deviation assignment below.
        private float orbitShakeSuppressYaw = 0.0f;
        private float orbitShakeSuppressPitch = 0.0f;
        private float orbitShakeSuppressRoll = 0.0f;
        private float orbitShakeSuppressTimer = 0.0f;
        private bool orbitShakeSuppressInit = false;
        // Low-pass filter on the raw per-axis deviation, applied before the
        // range/blend clamp - smooths out high-frequency wobble (e.g. natural
        // walk-cycle micro-twist) that would otherwise repeatedly cross the
        // clamp range/blend boundary and flicker. 1.0 = no smoothing (raw,
        // instant); lower = smoother but slower to react to real look-around.
        private float orbitClampSmoothing = 0.3f;
        private float orbitClampSmoothedYaw = 0.0f;
        private float orbitClampSmoothedPitch = 0.0f;
        private float orbitClampSmoothedRoll = 0.0f;
        private bool orbitClampSmoothedInit = false;
        // Separate low-pass filter on clampBaseRotation itself (not the
        // deviation from it) - filters out small, real jitter in the
        // player's own heading (e.g. analog-stick noise reflected into
        // movement direction/player.Rotation) that would otherwise pass
        // straight through into B/C as visible camera shake, since B/C both
        // track this rotation directly. 1.0 = no smoothing (raw, instant).
        // Fallback/default value - used only when no OrbitProfile matches
        // the current motion at all (default "A"). Every profile carries its
        // own independent ClampBaseSmoothing (see OrbitProfile.ClampBaseSmoothing
        // below), selected each frame by getActiveClampBaseSmoothing()
        // whenever one matches.
        private float orbitClampBaseSmoothing = 0.5f;
        private Quaternion orbitClampBaseRotationSmoothed = Quaternion.Identity;
        private bool orbitClampBaseSmoothedInit = false;
        // Classification: every motion is checked against orbitProfiles
        // (see below) top-to-bottom, first match wins. Each profile declares
        // its own Mode - Full Rotation ("A"), Base-Only Ignore-X ("B
        // (Ignore-X)"), Base-Only ("B"), or Normal ("C", clamped/spotted
        // follow) - and its own keyword list, Target/Correction/Smoothing,
        // and (for Normal) Yaw/Pitch/Roll Range/Blend, so e.g. a narrow-Yaw
        // "spin attacks" profile and a wide-Yaw/narrow-Pitch "IDLE/WALK/RUN"
        // profile can coexist with entirely independent settings instead of
        // sharing one clamp per mode. Anything matching no profile at all
        // defaults to "A" (unclamped, follows raw motion exactly), using the
        // top-level fallback fields above (orbitY/orbitRight/orbitForward,
        // orbitClampBaseCorrectionYaw/Pitch/Roll, orbitClampBaseSmoothing).
        // See findMatchingProfileIndex() and its use in the classification
        // block, and spotAxisDeg()'s call site further down.
        private bool orbitLastIsBaseOnly = false;
        private bool orbitLastBaseOnlyIgnoreX = false;
        // A single point on a scripted "gaze curve": at motion progress T
        // (0.0 = motion start, 1.0 = motion end), look Yaw/Pitch/Roll
        // degrees away from the Base Rotation Joint's direction. Used by
        // OrbitProfile.GazeKeyframes below in place of the raw Range/Blend
        // joint-deviation follow, for motions where the raw per-frame joint
        // data is too noisy/violent to look at directly (see
        // evaluateGazeKeyframes() and its call site in the C branch).

        //ver11
        private class OrbitGazeKeyframe
        {
            public float T = 0.0f;
            public float Yaw = 0.0f;
            public float Pitch = 0.0f;
            public float Roll = 0.0f;
        }

        // Gaze Keyframes を「モーションのこの区間の間だけ」効かせるための窓。
        // Start/End はモーション進行度 T (0.0 = 開始, 1.0 = 終了)。この窓の中では
        // Gaze Keyframes が使われ、外では通常の Range/Blend 追従に戻る。
        // Blend は窓の前後に足されるクロスフェード幅 (T 単位)。
        // Mirrors Config.GazeRange (Config.cs)。
        // See evaluateGazeRangeWeight() below.
        private class OrbitGazeRange
        {
            public float Start = 0.0f;
            public float End = 1.0f;
            public float Blend = 0.05f;
        }

        // Use Gaze Keyframes の XYZ 版が使うキーフレーム。Yaw/Pitch/Roll では
        // なく、カメラの位置オフセット (Target Y/Right/Forward) を T ごとに
        // 指定する。Mirrors Config.GazePositionKeyframe (Config.cs)。
        private class OrbitGazePositionKeyframe
        {
            public float T = 0.0f;
            public float Y = 0.0f;
            public float Right = 0.0f;
            public float Forward = 0.0f;
        }
        //ver11ここまで

        // Which of the four previously-hardcoded behaviors a given
        // OrbitProfile represents. Any profile, in any mode, can now be
        // matched to specific motions via its own Keywords list - "A"/"B"/"B
        // (Ignore-X)" are no longer single global catch-alls, they're just
        // another Mode a profile can declare.
        public enum OrbitProfileMode
        {
            FullRotation,     // 旧 A: unclamped, follows raw face-basis rotation exactly.
            BaseOnlyIgnoreX,  // 旧 B (Ignore-X): base-only, hip-projected (X-ignored) position.
            BaseOnly,         // 旧 B: base-only, raw nose position.
            Normal            // 旧 C: clamped/spotted head-tracking follow (attacks).
        }

        private class OrbitProfile
        {
            public OrbitProfileMode Mode = OrbitProfileMode.Normal;
            public List<string> Keywords = new List<string>();
            public string KeywordInput = "";

            // --- Common settings (apply regardless of Mode) ---

            // This profile's own eye-position offset - see
            // getActiveOrbitTargetOffset() for how it's selected each frame.
            // Independent of every other profile's own, and of the
            // top-level fallback (orbitY/orbitRight/orbitForward) used only
            // when no profile matches at all.
            public float TargetY = 0.0f;
            public float TargetRight = 0.0f;
            public float TargetForward = 0.0f;

            // This profile's own baseline yaw/pitch/roll (what "0
            // deviation" points toward) - see getActiveOrbitBaseCorrection()
            // for how it's selected each frame. Fixes a fixed angular offset
            // (e.g. reticle sitting a few degrees off true center) that
            // TargetY/Right/Forward above can't, since that only moves the
            // eye's position, not which way it points. Independent of every
            // other profile's own, and of the top-level fallback
            // (orbitClampBaseCorrectionYaw/Pitch/Roll) used only when no
            // profile matches at all.
            public float CorrectionYaw = 180.0f;
            public float CorrectionPitch = 0.0f;
            public float CorrectionRoll = 0.0f;

            // This profile's own low-pass filter strength on clampBaseRotation
            // (see getActiveClampBaseSmoothing() and its use at the clamp
            // block) - 1.0 = raw/instant, lower = smoother. Independent of
            // every other profile's own, and of the top-level fallback
            // (orbitClampBaseSmoothing) used only when no profile matches.
            public float ClampBaseSmoothing = 0.5f;

            // Whether switching into/out of this profile triggers the
            // fixed-duration Slerp blend below, and how long that blend
            // takes for THIS profile specifically (see
            // getActiveProfileTransitionDuration() and
            // orbitCurrentTransitionDuration, captured the instant a switch
            // into this profile is detected).
            public bool EnableTransitionBlend = true;
            public float ProfileTransitionDuration = 0.2f;

            // --- Normal ("C") mode-only settings below - only meaningful
            // (and only shown in the GUI) while Mode == Normal, since they
            // rely on the clamp/spotting math that Full Rotation bypasses
            // and Base-Only pins to zero regardless. ---

            public float YawRange = 70.0f;
            public float YawBlend = 25.0f;
            public float PitchRange = 55.0f;
            public float PitchBlend = 25.0f;
            public float RollRange = 25.0f;
            public float RollBlend = 15.0f;

            // Seconds to freeze the raw joint deviation for, starting the
            // instant R2 is pressed or released while this profile is
            // active (the two moments that actually cause an animation
            // kick - nocking, then firing) - see its use at the "C"
            // raw-deviation assignment below. 0 = disabled. Deliberately
            // NOT a continuous rate cap (tried that as MaxJointSpeed: it
            // necessarily slowed down legitimate fast R-stick tracking too,
            // and recovering from a held-back spike still added lag to
            // ordinary aiming) - this instead holds still for a fixed
            // window then snaps straight back to instant tracking.
            public float ShakeSuppressSeconds = 0.0f;

            // Scales Rx/Ry (right-stick input) while this profile is
            // active, so R-stick response can be tuned to actually match
            // this motion's own on-screen aim speed (e.g. AIM_IDLE),
            // independent of ordinary free-look sensitivity. 1.0 = no
            // change.
            public float LookSensitivityMultiplier = 1.0f;

            // See orbitNativeCameraPosition/Target declaration near vCamera
            // above. Experimental.
            public bool UseNativeAimCamera = false;

            // Delays switching over to the native camera by this many
            // seconds after L2 is first pressed - see
            // orbitNativeAimCameraDelayTimer and its use near the L2
            // aim-state handling. 0 = switch instantly (may show the base
            // game's own ~0.25s "swing to player facing" transition, since
            // the native camera isn't actively driven while Free Camera is
            // on and needs to catch up from wherever it last was).
            public float UseNativeAimCameraDelaySeconds = 0.0f;

            // While L2 is held and this profile is active, suppresses this
            // mod's own R-stick-driven camera offset entirely (see its use
            // near the top of OnUpdate, right after the one-time L2-press
            // center reset) - the camera stays put instead of trying to
            // visually track the aim, since this mod's own R-stick response
            // curve doesn't match the base game's own aim response to the
            // same stick input and the two visibly drift apart otherwise.
            // The reticle keeps moving normally regardless (native, reads
            // the raw stick directly) - only this mod's *camera* stops
            // trying to follow it.
            public bool DisableLookWhileAiming = false;

            // When true, this profile ignores the raw joint deviation
            // (Yaw/Pitch/RollRange/Blend above) entirely and instead looks
            // up a Yaw/Pitch/Roll target from GazeKeyframes, keyed by how
            // far the current motion has played (0..1), interpolated with
            // Slerp + smoothstep. See evaluateGazeKeyframes() below.
            // Mirrors Config.OrbitProfile.UseGazeKeyframes/GazeKeyframes
            // (Config.cs) - travels through the normal Config/ConfigManager
            // round-trip into NewCamera.json along with everything else,
            // same as every other field on this class.

            //ver10.1
            public bool UseGazeKeyframes = false;
            public List<OrbitGazeKeyframe> GazeKeyframes = new List<OrbitGazeKeyframe>();

            // GUI のプロファイル見出しにそのまま表示される自由記入のメモ。
            // マッチング判定には一切使われない。Config.OrbitProfile.Comment を
            // 経由して NewCamera.json に保存される。
            public string Comment = "";
            //ver14
            public string WeaponGroup = "";

            // Weapon Group 入力欄の一時バッファ。入力中はここだけが変わり、
            // "Set Group" ボタンを押した瞬間だけ WeaponGroup へ確定する。
            // これにより、1文字入力するたびに並び替えが走ることはなくなる。
            // 保存対象ではない (NewCamera.json には入らない)。
            public string WeaponGroupInput = "";

            // GUI 上でこのプロファイルを一意に識別するための番号。ImGui の
            // ウィジェット ID を「リスト上の位置」ではなくプロファイル自体に
            // 結び付けるために使う。並び替えでウィジェットの中身が別の
            // プロファイルのものに入れ替わる誤爆を防ぐためのもので、
            // 保存対象ではない。
            private static int nextUid = 1;
            public int Uid = 30000 + (nextUid++);
            //ver14ここまで
            // UseGazeKeyframes をモーション全体ではなく、GazeRanges で指定した
            // T 区間の間だけ有効にする。区間の外では従来どおり Range/Blend による
            // 追従に戻り、各区間の Blend 幅でクロスフェードされるので境目で
            // 飛ばない。GazeRanges が空のときは制限なし (全体で有効) 扱い。
            public bool GazeUseRanges = false;
            public List<OrbitGazeRange> GazeRanges = new List<OrbitGazeRange>();

            // ON にすると、Gaze Keyframes / GazeRanges が使う T を、モーション
            // 進行度 (getMotionProgress) の代わりに「orbitActionSubState が
            // 変化してからの経過時間 ÷ GazeSubStateDuration」で代用する。
            // ActionName/motionKey が変わらないまま上半身側の状態だけが変わる
            // 場面 (#<数値> キーワードでマッチさせるようなケース) 専用。
            //ver11
            public bool GazeUseSubStateTimer = false;
            // ↑を ON にしたときの正規化用の想定所要時間 (秒)。実測して調整する。
            public float GazeSubStateDuration = 1.0f;

            // Use Gaze Keyframes の XYZ 版。ON にすると、Target Y/Right/Forward を
            // GazePositionKeyframes の T ごとの値で上書きする。T の取得元
            // (モーションフレーム / サブステート経過時間) と Limit Gaze To
            // Ranges (GazeUseRanges/GazeRanges) は Rotation 側と共通で使う。
            public bool UseGazePositionKeyframes = false;
            public List<OrbitGazePositionKeyframe> GazePositionKeyframes = new List<OrbitGazePositionKeyframe>();
            //ver12 Position/Basis Jointをプロファイル単位で上書きする。
            // ON時は、このプロファイルがマッチしている間、eye位置
            // (TargetY/Right/Forwardの基準点)と、Simple Lock+Face Basis
            // 使用時の顔基準("nosePos")の両方に、グローバルのUse Face
            // Joints/Target Jointの代わりにここで指定したジョイントを使う。
            // 例: 飲みモーション専用プロファイルで、鼻の代わりにビンを
            // 持つ手のジョイントを指定すると、視点がその手元に寄った
            // 「手元カメラ」になり、鼻基準では毎回変わっていた瓶と視点の
            // ズレが目立たなくなる。
            public bool PositionJointOverrideEnable = false;
            public bool PositionJointOverrideUseFace = false;
            public int PositionJointOverrideJoint = 0;
            //ver12ここまで

            // ver13: このプロファイルがマッチしている間、スリンガーを
            // 強制非表示にする (Armor.Slinger, 全パーツ非表示)。狩猟中も
            // 「最後のPartをオフにするとスリンガー全体が消える」現象を
            // 利用して hideArmorPartAllLods/showArmorPartAllLods で毎フレーム
            // 強制する。マッチしなくなったら自動で再表示する。
            public bool HideSlingerWhileActive = false;

            // ver15: このプロファイルがマッチしている間、Enable Free Camera を
            // 強制的にオフにする。クラッチクロー・しがみつき中や乗り状態など、
            // 一人称視点だと画面が揺れ過ぎる場面向け。マッチしなくなったら、
            // ユーザーが元々選んでいたEnable Free Cameraの状態に自動で戻る。
            public bool DisableFreeCameraWhileActive = false;
        }
        private List<OrbitProfile> orbitProfiles = new List<OrbitProfile>();
        //ver11
        // 直近フレームの Gaze ブレンド率 (0 = Range/Blend 追従のみ、
        // 1 = Gaze Keyframes のみ)。GUI の確認用。
        private float orbitLastGazeWeight = 0.0f;
        //ver10.1ここまで

        //* ver2変更
        private int orbitLastProfileIndex = -1;

        // ver13: 現在マッチしているプロファイルがスリンガーの強制
        // 非表示を要求しているか。毎フレーム更新され、
        // collectArmorParts(Armor.Slinger) が参照する。
        private bool orbitSlingerHiddenByProfile = false;

        // ver15: 現在マッチしているプロファイルによってEnable Free Cameraが
        // 強制的にオフにされている最中かどうか。trueの間、
        // orbitFreeCameraUserWanted に「ユーザー本来の希望値」を退避しておき、
        // プロファイルがマッチしなくなった瞬間にそれを復元する。
        // Free Camera が無効の間はこのMODのカメラ計算(updateFreeCamera)
        // 自体が呼ばれずモーションを拾えなくなるため、この判定は
        // updateFreeCamera() の中ではなく OnUpdate() の中で、Free Camera の
        // 有効/無効に関わらず毎フレーム独立して行う
        // (updateForcedFreeCameraDisableByProfile() 参照)。
        private bool orbitFreeCameraForcedOffByProfile = false;
        private bool orbitFreeCameraUserWanted = false;

        // ver15: enableFreeCamera を false にすると disableFreeCamera() が
        // 呼ばれ、Unlock Input・FOVまでリセットされてしまう(F7の裏側と同じ)。
        // そのため、F7の手動トグルと全く同じ「オフにする直前の値を退避し、
        // 再度オンに戻すときに復元する」処理を、自動強制オフの場合にも
        // 同様に行う必要がある。
        private bool orbitFreeCameraSavedUnlockInput = false;
        private float orbitFreeCameraSavedFov = 90.0f;
        private bool orbitFreeCameraHasSavedFov = false;

        // --- ここから 追加・修正する変数群 ---
        private int orbitPrevProfileId = -1;
        private bool orbitPrevProfileIdInit = false;
        private bool orbitProfileTransitionActive = false;
        private float orbitProfileTransitionTimer = 0.0f;
        private Quaternion orbitProfileTransitionStartRotation = Quaternion.Identity;

        // Positionブレンド用
        private float orbitPrevTargetY = 0.0f;
        private float orbitPrevTargetRight = 0.0f;
        private float orbitPrevTargetForward = 0.0f;

        private float orbitProfileTransitionStartTargetY = 0.0f;
        private float orbitProfileTransitionStartTargetRight = 0.0f;
        private float orbitProfileTransitionStartTargetForward = 0.0f;

        // Fallback/default Enable Transition Blend - used only when no
        // OrbitProfile matches the current motion at all (default "A").
        // Every profile carries its own independent EnableTransitionBlend
        // (see OrbitProfile.EnableTransitionBlend above), selected each
        // frame by getActiveEnableTransitionBlend() whenever one matches.
        private bool orbitDefaultEnableTransitionBlend = true;
        // --- ここまで ---

        // Fallback/default Profile Switch Blend Time, in seconds - used only
        // when no OrbitProfile matches the current motion at all (default
        // "A"). Every profile carries its own independent
        // ProfileTransitionDuration (see OrbitProfile.ProfileTransitionDuration
        // above). Fixed regardless of how big the jump is (unlike
        // orbitFinalRotationMaxSpeed below, which takes longer for bigger
        // jumps). 0 disables this feature (falls back to relying on
        // orbitFinalRotationMaxSpeed alone).
        private float orbitProfileTransitionDuration = 0.2f;
        // Whichever of the above two (a matched profile's own
        // ProfileTransitionDuration, or this default) was actually captured
        // the instant the current transition began - see the classification
        // block below and its use at the transition-timer block further
        // down. Using a captured snapshot (rather than re-reading whichever
        // duration happens to be "active" every frame) means an in-progress
        // blend always finishes in the time it started with, even if the
        // person tweaks a duration slider mid-blend.
        private float orbitCurrentTransitionDuration = 0.2f;
        //*/

        // Cache of clampBaseRotation from the clamp block above, so the
        // position override further below (same frame) can decompose the
        // nose-to-hip offset into local right/up/forward without
        // recomputing it.
        private Quaternion orbitClampBaseRotationCache = Quaternion.Identity;
        // Real ballet spotting fixes on one point in the room, not a
        // direction that keeps re-measuring from wherever the dancer
        // currently stands. This captures a fixed world-space point - B's
        // forward direction, projected out to orbitSpotDistance - the
        // instant "C" begins, then for as long as C continues, the
        // reference direction is recomputed each frame as "current position
        // to that fixed point" (so it still accounts for the camera moving,
        // unlike just reusing a single frozen direction).
        private float orbitSpotDistance = 500.0f;
        // How fast (fraction per second) the spot point drifts to re-match
        // the live base direction while C continues - 0 disables (spot
        // point stays perfectly fixed for the whole motion, original
        // behavior). Keep this well below "one rotation's worth" of speed
        // so it doesn't interfere with spotting itself.
        private float orbitSpotReanchorRate = 0.5f;
        private Vector3 orbitSpotPoint = Vector3.Zero;
        private bool orbitSpotPointValid = false;
        private bool orbitWasInC = false;
        // Final safety net: caps how fast the *rendered* camera rotation can
        // visibly change per frame, regardless of what produced the jump
        // (switching between A/B/C, pressing the recenter button, etc). Set
        // high enough that legitimate fast in-game rotations (a full-body
        // spin attack, category A) still track at effectively full speed;
        // this only smooths out otherwise-instant snaps.
        private float orbitFinalRotationMaxSpeed = 1080.0f;
        private Quaternion orbitFinalRotationPrev = Quaternion.Identity;
        private bool orbitFinalRotationPrevInit = false;
        // Same idea as orbitFinalRotationMaxSpeed above, but for position -
        // caps how fast the camera can visibly move per frame, smoothing
        // out the jump when eyeAnchor's source changes discontinuously
        // (e.g. B-Ignore-X's hip-projected position vs. A/C/B-no-ignore's
        // raw nose position), independent of the Lerp setting above (which
        // the person may want at 1.0/instant for normal tracking).
        private float orbitFinalPositionMaxSpeed = 3000.0f;
        private Vector3 orbitFinalPositionPrev = Vector3.Zero;
        private bool orbitFinalPositionPrevInit = false;
        // When on: re-levels B/C's rotation so Up stays as close to true
        // world-up as possible (removing roll around the forward axis
        // entirely), regardless of what the computed roll deviation was.
        // Does NOT apply to A (full-body rotation) - a motion that
        // genuinely rolls sideways (e.g. a lateral roll dodge) should keep
        // rolling if it's registered under A; only register it under C if
        // it's fine for the camera to remove that roll.
        private bool orbitForceLevelRoll = true;
        private string orbitLastActionName = "";
        private bool orbitLastIsFullRotation = false;
        private Button? orbitRecenterButton = Button.L1;
        private string typedRecenterButton = "L1";

        //ver9.3
        // -1 = ボタンを押していない。0以上 = 押してからの経過秒数(現実時間)。
        // フレームカウント方式(旧orbitRecenterPressFrame)だとフレームレートが
        // 変わるたびにTap Max Framesを調整し直す必要があったため、時間ベースに
        // 変更した。
        private float orbitRecenterHeldSec = -1.0f;
        private float orbitRecenterTapMaxSeconds = 0.3f;
        // このReturningがL1リセット由来かどうか。L1由来ならPitchも0へイーズ
        // させ、Max Yaw超過由来(Keepから)ならPitchはそのまま保持する
        // (仕様ドキュメント16項)。
        private bool orbitReturningResetPitch = false;
        //ver9.3ここまで
        
        // Return-to-Center Look: while enabled, the right stick's tilt
        // (angle + magnitude) is mapped directly to a yaw/pitch offset from
        // forward every frame, instead of being integrated over time like
        // the normal free-look. Letting go of the stick (back to neutral)
        // therefore snaps/springs the view back to forward on its own,
        // without needing to detect *why* the view needs to be
        // recentered (NPC talk, quest board, etc.) - it's just always true
        // whenever the stick isn't held.
        private bool orbitReturnToCenterLook = true;
        private float orbitReturnToCenterYawMax = 60.0f;
        private float orbitReturnToCenterPitchMax = 45.0f;
        // How fast (in effective "fraction per second") the view springs
        // back to center once the stick returns to neutral. Higher = snappier.
        //ver8 angleスナップ対応フィールド追加
        private float orbitReturnToCenterSpeed = 0.3f;
        // Angle-snap applied to PadRx/PadRy in updateFreeCamera() (right
        // stick only). 12 = clock-hour granularity (30deg steps). 0 disables
        // snapping (full continuous resolution, the old behavior).

        private int orbitRightStickAngleSnapSteps = 100;
        private int orbitFrameCounter = 0;
        //ver8ここまで
        //ver9 右スティックのKeep/Catch-upステートマシン用
        // (Manual: 手動操作中 / Keep: 離した瞬間のワールド方向を保持 / Follow: 通常追従)
        private enum OrbitLookState { Follow, Manual, Keep, Returning }//ver9
        private OrbitLookState orbitLookState = OrbitLookState.Follow;
        // Keep中に保持し続けるワールドYawの目標値と、その有効フラグ。
        private float orbitKeepWorldYawDeg = 0.0f;
        private bool orbitKeepValid = false;
        // 「cameraYawを乗算する直前のrotation」のワールドYaw(=プロファイルA/B/C
        // どれでもcameraYawが実際に足し込まれる直前の基準方向)。updateFreeCamera()
        // 内、cameraYaw乗算の直前2箇所(SimpleLock/ターゲットジョイント経路と、
        // 通常の回転ジョイント経路)でキャッシュする。orbitCachedStableForwardYawDeg
        // (player.Rotation基準の"体幹"の向き)とは別物で、こちらはプロファイル
        // ごとの実際の合成対象そのもの。前フレームの値を使う(1フレーム遅れ)のは、
        // cameraYawを決める時点ではこのフレームのrotationがまだ計算されていない
        // ため。前回のクローズドループ補正(自分の出力を読み返す)が発振した反省で、
        // これはcameraYawより"前"の値なのでフィードバックループにならない。
        private float orbitCachedBaseYawDeg = 0.0f;
        private bool orbitCachedBaseYawValid = false;
        // true: このベース方向とcameraYawの間にclampFlip(X軸180度)が挟まっており、
        // cameraYawの効き方が符号反転する経路(プロファイルA/SimpleLock)。
        // false: 通常の足し算の経路(プロファイルB/C)。
        private bool orbitCachedBaseYawFlipped = false;
        // orbitCachedStableForwardYawDeg(Decouple Movement From Look用に平滑化
        // 済み)とは別に、平滑化前の"今まさに向いている"体の正面Yaw。Keepの
        // Catch-up判定は、素早い旋回中でも遅延なく検出したいのでこちらを使う。
        private float orbitCachedRawStableForwardYawDeg = 0.0f;
        private bool orbitCachedRawStableForwardYawValid = false;
        // Catch-up判定のしきい値(度)。「キャラクターの現在の安定した体の正面」
        // と「保持しているワールド方向」の差がこれを下回ったらFollowへ復帰する。
        private float orbitKeepCatchUpEpsilonDeg = 1.0f;
        // Max Yaw超過判定に使う余裕分(度)。orbitReturnToCenterYawMaxちょうどで
        // 判定すると、走行から停止した直後の数秒など一時的にMax Yaw付近を
        // 通過しただけで意図せずReturning(センタリング)してしまうため、
        // 実際にMax Yawを大きく超えたときだけ発動するよう少し広くしてある。
        private float orbitReturnToCenterExceedMarginDeg = 5.0f;
        // Catch-up判定の「素通り」対策。狭いepsilon窓を1フレームでまたいで
        // しまうことがあるため、前フレームのoffsetFromNeutralを保持しておき、
        // 符号が反転した(=ちょうど0をまたいで通過した)ことも合わせて検知する。
        private float orbitKeepPrevOffsetDeg = 0.0f;
        private bool orbitKeepPrevOffsetValid = false;
        //ver9ここまで
        private bool orbitStabilize = true;

        private bool orbitLevelRoll = true;
        private float orbitStabilizeMinSpeed = 90.0f;
        private float orbitStabilizeMaxSpeed = 500.0f;
        private float orbitStabilizeMinAlpha = 0.06f;
        private Quaternion orbitStableRotation = Quaternion.Identity;
        private bool orbitStableInit = false;
        private Quaternion orbitPrevRawRotation = Quaternion.Identity;
        private bool orbitPrevRawInit = false;
        private float orbitLastAngularSpeed = 0.0f;
        private bool blockRightStickLookDuringL1 = false;//L1押下中カメラロック

        private bool enableOffsetPerspective = false;
        private bool offsetPerspective = false;
        private Player? lastPlayer = null;
        private Camera? pCamera = null;
        private int pCameraViewportIndex = -1;
        private bool applyPerspective = false;
        private float previousFov = -1.0f;
        private int previousCameraAnimState = 0;
        private bool ignoreAnimState = false;

        private bool enableCombo = false;
        private Button[]? freeCameraCombo = null;
        private bool disableComboButton1 = false;
        private bool comboButton1Down = false;

        private static readonly MtObject sMhController = SingletonManager.GetSingleton("sMhSteamController")!;
        private nint primaryPad = 0x0;
        private int PadLx, PadLy;
        private int PadRx, PadRy;

        // 追加: Enable Free Camera のオン/オフで Unlock Input と FOV を復元するための保存領域
        private bool savedUnlockInputOnDisable = false;
        private float savedEnableFreeCameraCameraFov = 90.0f;//DEFAULT_FOV
        private bool savedEnableFreeCameraHasSavedFov = false;

        // USLASH→DOGE_Rバグのログ取り
        private delegate nint ActionRequestDelegate(nint player, int requestId);
        private Hook<ActionRequestDelegate>? actionRequestHook;

        private uint lastPadDown = 0u;
        private uint? prevPadDown = null;
        private bool unlockInputToggled = false;
        private bool unlockInputForMenu = false;
        private bool unlockInputHideMenu = false;
        private bool buttonWasDown(Button button)
        {
            return (lastPadDown & (uint)button) == (uint)button;
        }
        private bool buttonWasPressed(Button button)
        {
            return (lastPadDown & (uint)button) == (uint)button && (prevPadDown & (uint)button) != (uint)button;
        }
        private bool buttonWasReleased(Button button)
        {
            return (lastPadDown & (uint)button) != (uint)button && (prevPadDown & (uint)button) == (uint)button;
        }

#if MOUSE_AND_KEYBOARD_LAYER
        private static readonly MtObject sMhMouse = SingletonManager.GetSingleton("sMhMouse")!;
        private static readonly MtObject sMhKeyboard = SingletonManager.GetSingleton("sMhKeyboard")!;
        private bool mouseEnabled = false;
        private float mouseSensitivity;
        private bool keyboardEnabled = false;
        private int keyboardLookValue;
#endif
        // Debug > Viewports > Viewport #0's Near Clip - persisted and
        // (re)applied once per session as soon as vCamera becomes valid,
        // since camera.NearClip itself is live engine state, not something
        // that's naturally remembered between launches.
        private float debugNearClip = DEFAULT_NEAR_CLIP;
        private bool debugNearClipApplied = false;
        // "Alternate Near Clip" checkbox state - previously purely guessed
        // from camera.NearClip == alternateNearClip (no independent
        // backing state), which could show checked just from Debug Near
        // Clip coincidentally matching alternateNearClip's saved value.
        // Explicit and persisted now, default off.
        private bool orbitAltNearClipToggled = false;
        private bool orbitAltNearClipApplied = false;
        // Set once, right after loading a saved cameraFov from Config -
        // makes the *next* setupFreeCamera() call use that saved value
        // instead of adopting whatever FOV the camera happens to have at
        // that moment (which is what normally avoids a visible "pop" when
        // manually toggling free camera mid-session, but would otherwise
        // clobber a saved FOV on the very first activation).
        private bool cameraFovPreloaded = false;
        // Keyboard-only "Toggle Slow Motion" hotkey (Key.F9, see
        // OnUpdate) for quickly checking motions frame-by-frame without
        // digging into World > Game Speed each time.
        private bool slowMotionActive = false;
        private float slowMotionSpeed = 0.1f;

        // --- ここから追加 ---ver7
        private long f9PressStartTime = 0;
        private bool f9IsFastSpeed = false;
        private bool f9HoldTriggered = false;
        private const long F9_LONG_PRESS_MS = 400; // 0.4秒（400ミリ秒）
        // --- ここまで ---

        private float plusRight = 0.0f;
        private float plusForward = 0.0f;

        private bool sortMonsters = true;
        private bool sortAnimals = true;

        private string modelFilter = "";
        private bool modelFilterIsRegex = false;
        private Regex? modelRegex = null;
        private bool modelOnlyAddressLod0 = false;

        private delegate void ProcessCameraDelegate(nint cameraPointer);
        private Hook<ProcessCameraDelegate>? setCameraHook;
        private Hook<ProcessCameraDelegate>? calculateCameraHook;
        private Hook<ProcessCameraDelegate>? checkCameraHook;
        private delegate void ViewModeDelegate(nint viewModeObject);
        private Hook<ViewModeDelegate>? startViewModeHook;
        private bool overrideViewMode = false;
        /*
        private NativeAction<nint> startViewMode;
        private NativeAction<nint> stopViewMode;
        private NativeAction<nint> processViewMode;
        private Patch viewMode1;
        private Patch viewMode1_1;
        private Patch viewMode2;
        private Patch viewMode3;
        private Patch viewMode4;
        private nint psuedoViewModeObject;
        */
        private NativeAction<nint, nint, nint> setupGestureMenu;
        private NativeAction<nint> showGestureMenu;

        private delegate void SetCameraTentDelegate(nint unknownPtr);
        private Hook<SetCameraTentDelegate>? setCameraTentHook;

        private delegate void SetCameraCutsceneDelegate(nint unknownPtr);
        private Hook<SetCameraCutsceneDelegate>? setCameraCutsceneHook;

        /*
        private delegate void CalculateViewDelegate(nint unknownPtr);
        private Hook<CalculateViewDelegate>? calculateViewHook;
        */

        private delegate void WritePadInputDelegate(nint unknownPtr, nint unknownPtr2, nint unknownPtr3);
        private Hook<WritePadInputDelegate>? writePadInputHook;

        private delegate float CheckMovementDelegate(int stickValue, float alwaysZero);
        private Hook<CheckMovementDelegate>? checkMovementHook;

        private delegate void CollisionCheckDelegate(nint unknownPtr, nint unknownPtr2);
        private Hook<CollisionCheckDelegate>? collisionCheckHook;

        private delegate void CameraEffectDelegate(nint unknownPtr, nint unknownPtr2, nint unknownPtr3);
        private Hook<CameraEffectDelegate>? cameraEffectHook;

        private Patch forceMinimapFollowsCamera;

        private bool uiToggled = false;
        private Patch jmpOverUi;

        private static readonly MtObject sMhScene = SingletonManager.GetSingleton("sMhScene")!;
        private bool disableCharacterFade = false;
        private Patch noopCharacterFade;

        private int disableNearDofOverride = 0;

        private bool enableUnderwaterCamera = false;
        private Patch underwaterCamera1;
        private Patch underwaterCamera2;
        private Patch underwaterCamera3;
        private Patch underwaterCamera4;
        private Patch underwaterCamera5;
        private bool disableDofCoc = false;
        private Patch nullifyDofCoc;
        private bool cameraIsUnderwater = false;
        private delegate void UnderwaterCheck(nint unknownPtr);
        private Hook<UnderwaterCheck>? underwaterCheckHook;

        private void underwaterCameraEnable()
        {
            underwaterCamera1.Enable();
            underwaterCamera2.Enable();
            underwaterCamera3.Enable();
            underwaterCamera4.Enable();
            underwaterCamera5.Enable();
        }

        private void underwaterCameraDisable()
        {
            underwaterCamera1.Disable();
            underwaterCamera2.Disable();
            underwaterCamera3.Disable();
            underwaterCamera4.Disable();
            underwaterCamera5.Disable();
        }

        private static readonly MtObject sPlayer = SingletonManager.GetSingleton("sPlayer")!;
        private int saveSlotIndex = -1;
        private NativeFunction<nint, nint> getSaveSlotGuiAddr;
        private NativeFunction<nint, nint> getPlayerSettingsAddr;
        private nint getPlayerSettings()
        {
            nint rcx = MemoryUtil.Read<nint>(0x145013950);
            if (saveSlotIndex >= 0)
            {
                // MonsterHunterWorld.exe+ADC442 - imul rdx,rcx,0026CC00
                nint rax = saveSlotIndex * 0x26CC00;
                rax += MemoryUtil.Read<nint>(rcx + 0xA8);
                return rax;
            }
            // MonsterHunterWorld.exe+1B8DBB0 - movsxd  rax,dword ptr [rcx+000000A0]
            return getPlayerSettingsAddr.Invoke(rcx);
        }
        private bool hideWeapon = false;
        private bool hideKnife = false;
        private bool checkKnifeHidden = false;

        private static class Armor
        {
            public const byte Body = 0;
            public const byte Helmet = 1;
            public const byte Arm = 2;
            public const byte Waist = 3;
            public const byte Leg = 4;
            public const byte Weapon = 5;
            public const byte Slinger = 6;
            public const byte Hair = 7;
            public const byte Face = 8;
            public const byte EyeLens = 9;
        }

        private static class Joint
        {
            public const byte Root = 0x0;
            public const byte Root2 = 0xD;
            public const byte LowerBack = 0x1;
            public const byte UpperBack = 0x2;
            public const byte LowerNeck = 0x3;
            public const byte LowerNeckNull = 0xFE;
            public const byte UpperNeck = 0x4;
            public const byte LeftCollarbone = 0x5;
            public const byte LeftShoulderNull = 0x46;
            public const byte LeftShoulder = 0x6;
            public const byte LeftElbow = 0x7;
            public const byte LeftWrist = 0x8;
            public const byte LeftWristNull = 0x1E;
            public const byte LeftThumbLow = 0x1F;
            public const byte LeftThumbMid = 0x20;
            public const byte LeftThumbHigh = 0x21;
            public const byte LeftIndexLow = 0x22;
            public const byte LeftIndexMid = 0x23;
            public const byte LeftIndexHigh = 0x24;
            public const byte LeftMiddleLow = 0x25;
            public const byte LeftMiddleMid = 0x26;
            public const byte LeftMiddleHigh = 0x27;
            public const byte LeftPalm = 0x28;
            public const byte LeftRingLow = 0x29;
            public const byte LeftRingMid = 0x2A;
            public const byte LeftRingHigh = 0x2B;
            public const byte LeftPinkyLow = 0x2C;
            public const byte LeftPinkyMid = 0x2D;
            public const byte LeftPinkyHigh = 0x2E;
            public const byte LeftArmUnk1 = 0x51;
            public const byte LeftArmUnk2 = 0x65;
            public const byte LeftArmUnk3 = 0x66;
            public const byte LeftArmUnk4 = 0x67;
            public const byte LeftArmUnk5 = 0x68;
            public const byte LeftArmUnk6 = 0x47;
            public const byte LeftArmUnk7 = 0x50;
            public const byte RightCollarbone = 0x9;
            public const byte RightShoulder = 0xA;
            public const byte RightElbow = 0xB;
            public const byte RightWrist = 0xC;
            public const byte RightWristNull = 0x2F;
            public const byte RightThumbLow = 0x30;
            public const byte RightThumbMid = 0x31;
            public const byte RightThumbHigh = 0x32;
            public const byte RightIndexLow = 0x33;
            public const byte RightIndexMid = 0x34;
            public const byte RightIndexHigh = 0x35;
            public const byte RightMiddleLow = 0x36;
            public const byte RightMiddleMid = 0x37;
            public const byte RightMiddleHigh = 0x38;
            public const byte RightPalm = 0x39;
            public const byte RightRingLow = 0x3A;
            public const byte RightRingMid = 0x3B;
            public const byte RightRingHigh = 0x3C;
            public const byte RightPinkyLow = 0x3D;
            public const byte RightPinkyMid = 0x3E;
            public const byte RightPinkyHigh = 0x3F;
            public const byte RightArmUnk1 = 0x53;
            public const byte RightArmUnk2 = 0x49;
            public const byte RightArmUnk3 = 0x52;
            public const byte RightArmUnk4 = 0x48;
            public const byte Unknown1 = 0xF7;
            public const byte Unknown2 = 0xF8;
            public const byte LeftHip = 0xE;
            public const byte LeftKnee = 0xF;
            public const byte LeftAnkle = 0x10;
            public const byte LeftToes = 0x11;
            public const byte LeftAnkleNull = 0x54;
            public const byte LeftKneeNull = 0x4B;
            public const byte LeftHipNull = 0x4A;
            public const byte Unknown3 = 0x40;
            public const byte Unknown4 = 0x42;
            public const byte RightHip = 0x12;
            public const byte RightKnee = 0x13;
            public const byte RightAnkle = 0x14;
            public const byte RightToes = 0x15;
            public const byte RightAnkleNull = 0x55;
            public const byte RightKneeNull = 0x4D;
            public const byte RightHipNull = 0x4C;
            public const byte Unknown5 = 0x43;
            public const byte Unknown6 = 0x45;
            public const byte Unknown7 = 0xF9;
            public const byte Unknown8 = 0xFA;
            public const byte Unknown9 = 0xFB;
            public const byte Unknown10 = 0xFC;
            public const byte Unknown11 = 0xFD;
        }

        private nint[] playerArmor = new nint[10];
        // Whether each Armor.* category should stay force-hidden even after
        // re-equipping (e.g. changing helmets) - re-applied every time
        // collectArmorParts sees that category's part.
        private bool[] stickyHideArmor = new bool[10];
        private nint[] playerJoints = new nint[0xFF];

        private delegate void UpdateJoints(nint obj);
        private Hook<UpdateJoints>? updateJointsHook;
        private List<nint> bodyJoints = new List<nint>();
        private List<nint> ignoredJoints = new List<nint>();
        private Dictionary<nint, List<Vector3>> bodyOffsets = new Dictionary<nint, List<Vector3>>();

        private List<nint> faceJoints = new List<nint>();
        private Dictionary<nint, List<Vector3>> faceOffsets = new Dictionary<nint, List<Vector3>>();

#if QUARANTINED_FEATURES
        private delegate void UpdateIK(nint superOfChild, nint joint, nint obj);
        private Hook<UpdateIK>? updateIKHook;
        private Dictionary<nint, List<Vector3>> ikOffsets = new Dictionary<nint, List<Vector3>>();
        private List<nint> ikJoints = new List<nint>();
#endif
        private bool ikForcedOff = false;
        private Patch forceDisableIK;

        private List<nint> jiggleBones = new List<nint>();
        private bool attachToChest = false;
        private bool swapChestSides = false;
        private float chestMoveScale = 0.25f;
        private nint chestBone1 = 0x0;
        private nint chestBone2 = 0x0;

        /*
        private delegate void ChangeEquipment(nint unknownPtr, int unknownInt);
        private Hook<ChangeEquipment>? changeEquipmentHook;
        private NativeFunction<nint, int, nint> newEquipmentPointer;
        private int lastEquipedId = 0;
        */

        private bool disableCollision = false;
        private bool disableExtraCollision = false;
        private bool disableGravity = false;
        private bool disableExtraGravity = false;
        private Patch disableXCollision;
        private Patch disableYCollision;
        private Patch disableSecondaryYCollision;
        private Patch disableZCollision;
        private Patch disableExtraYCollision;
        private Patch disableGravityYUpdate;
        private Patch disableGravityXZUpdate;
        private Patch disableGravityEval;

        private void disableCollisionEnable()
        {
            disableXCollision.Enable();
            disableYCollision.Enable();
            disableSecondaryYCollision.Enable();
            disableZCollision.Enable();
        }

        private void disableCollisionDisable()
        {
            disableXCollision.Disable();
            disableYCollision.Disable();
            disableSecondaryYCollision.Disable();
            disableZCollision.Disable();
        }

        private void disableExtraCollisionEnable()
        {
            disableExtraYCollision.Enable();
        }

        private void disableExtraCollisionDisable()
        {
            disableExtraYCollision.Disable();
        }

        private void disableGravityEnable()
        {
            disableGravityYUpdate.Enable();
        }

        private void disableGravityDisable()
        {
            disableGravityYUpdate.Disable();
        }

        private void disableExtraGravityEnable()
        {
            disableGravityEval.Enable();
        }

        private void disableExtraGravityDisable()
        {
            disableGravityEval.Disable();
        }

        private enum ZoneState : int
        {
            Unknown = 0,
            Hub = 1,
            Combat = 2
        }

#if QUARANTINED_FEATURES
        private ZoneState forceZoneState = ZoneState.Unknown;
        private ZoneState lastZoneState = ZoneState.Unknown;
        private NativeAction<nint, int> setZoneState;
        private NativeAction<nint> setPlayerController1;
        private NativeAction<nint> setPlayerController5;
        private bool zoneStateManualInvoke = false;
        private bool forcePassiveInCombatZone = false;
        private Patch zoneStateForcePassive1;
        private Patch zoneStateForcePassive2;
        private bool forceCombatInPassiveZone = false;
        private Patch zoneStateForceCombat1;
        private Patch zoneStateForceCombat2;
#endif
        private delegate void ZoneStateDelegate(nint player, int flags);
        private Hook<ZoneStateDelegate>? setZoneStateHook;

        private bool allowHotSpringsAnywhere = false;
        private Patch jmpOverHotSpringsEval;

        private bool disableHotSpringsSteam = false;
        private Patch jmpOverHotSpringsSteam;

        private bool enableCrawl = false;
        private NativeAction<nint, nint> procEnvironmentCollision;
        private nint psuedoObject1;
        private nint psuedoObject2;

        private float playerOpacityOverride = 1.0f;
        private delegate void RefreshEntityParams(nint entity, nint unknownPtr2);
        private Hook<RefreshEntityParams>? refreshEntityParamsHook;

        private bool overridePlayerWetness = false;
        private Patch noopPlayerWetnessUpdate;
        private Patch jmpOverChangeEquipmentWetnessUpdate_1;
        private Patch jmpOverChangeEquipmentWetnessUpdate_2;
        private float wholeBodyWetness = 0.0f;

        private void overridePlayerWetnessEnable()
        {
            noopPlayerWetnessUpdate.Enable();
            jmpOverChangeEquipmentWetnessUpdate_1.Enable();
            jmpOverChangeEquipmentWetnessUpdate_2.Enable();
        }

        private void overridePlayerWetnessDisable()
        {
            noopPlayerWetnessUpdate.Disable();
            jmpOverChangeEquipmentWetnessUpdate_1.Disable();
            jmpOverChangeEquipmentWetnessUpdate_2.Disable();
        }

        private void writePlayerWholeBodyWetness(nint wetnessAddr)
        {
            MemoryUtil.GetRef<float>(wetnessAddr) = wholeBodyWetness;
            MemoryUtil.GetRef<float>(wetnessAddr + 0x28) = wholeBodyWetness;
            MemoryUtil.GetRef<float>(wetnessAddr + 0x50) = wholeBodyWetness;
            MemoryUtil.GetRef<float>(wetnessAddr + 0x78) = wholeBodyWetness;
        }

        private static readonly MtObject sOtomo = SingletonManager.GetSingleton("sOtomo")!;

        // Gui strings.
        private string typedCombo = "";
        private string typedPresetName = "";
        private string typedPositionName = "";
        private string selectedPositionName = "";

#if HOOK_ORDER_ASSERTS
        private int hookOrder = 0;
        private int frameTick = 0;
#endif

        private void debugLog(string message)
        {
#if LOG_DEBUG_MESSAGES
            Log.Info(message);
#else
            Log.Debug(message);
#endif
        }

        private Config loadConfig()
        {
            Config config = ConfigManager.GetConfig<Config>(this);

            // --- 追加ここから: ファイルから直接JSONを読み込んでキャッシュを強制上書きする ---
            try
            {
                // DLLと同じディレクトリにある NewCamera.json を探す
                string pluginDir = Path.GetDirectoryName(this.GetType().Assembly.Location) ?? "";
                string jsonPath = Path.Combine(pluginDir, "NewCamera.json");

                // プラグインフォルダ構成の違いに備えたフォールバック（念のため）
                if (!File.Exists(jsonPath))
                {
                    jsonPath = Path.Combine(AppContext.BaseDirectory, "nativePC", "plugins", "CSharp", "NewCamera", "NewCamera.json");
                }
                if (!File.Exists(jsonPath))
                {
                    jsonPath = Path.Combine(AppContext.BaseDirectory, "nativePC", "plugins", "CSharp", "NewCamera.json");
                }

                // ファイルが存在すれば強制的に読み込む
                if (File.Exists(jsonPath))
                {
                    string jsonString = File.ReadAllText(jsonPath);
                    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    Config? fileConfig = System.Text.Json.JsonSerializer.Deserialize<Config>(jsonString, options);

                    if (fileConfig != null)
                    {
                        // 取得した最新のファイル内容で、SPLが握っているキャッシュ(config)を上書き更新する
                        config.DisableMod = fileConfig.DisableMod;
                        config.TuningToolInterop = fileConfig.TuningToolInterop;
                        config.OverrideViewMode = fileConfig.OverrideViewMode;
                        config.Camera = fileConfig.Camera;
                        config.Binds = fileConfig.Binds;
                        config.PerspectiveCameraEnabled = fileConfig.PerspectiveCameraEnabled;
                        config.Presets = fileConfig.Presets;
                        config.Selected = fileConfig.Selected;
                        config.Positions = fileConfig.Positions;
                        config.Session = fileConfig.Session;
                        config.OrbitalCamera = fileConfig.OrbitalCamera;
                    }
                }
            }
            catch (Exception ex)
            {
                debugLog($"Config reload failed: {ex.Message}");
            }
            // --- 追加ここまで ---

            disableMod = config.DisableMod;

            overrideViewMode = config.OverrideViewMode;

            Config.Settings.Binds binds = config.Binds;
            enableCombo = binds.EnableCombo;
            freeCameraCombo = Config.ParseCombo(binds.FreeCameraCombo);
            typedCombo = binds.FreeCameraCombo.Replace(",", "+");
            disableComboButton1 = binds.DisableComboButton1UnlessButton2Held;
#if MOUSE_AND_KEYBOARD_LAYER
            mouseEnabled = binds.EnableMouse;
            mouseSensitivity = binds.MouseSensitivity;
            keyboardEnabled = binds.EnableKeyboard;
            keyboardLookValue = binds.KeyboardLookSensitivity;
#endif

            Config.Settings camera = config.Camera;
            cameraSpeed = camera.Speed;
            cameraSpeedModifier = camera.SpeedModifier;
            cameraSensitivity = camera.Sensitivity;
            cameraZoomSpeed = camera.ZoomSpeed;
            cameraPitchLimit = camera.PitchLimit;
            if (cameraPitchLimit != -1.0f)
            {
                cameraPitchLimit = Math.Clamp(cameraPitchLimit, 0.0f, Config.Settings.MAX_PITCH_LIMIT);
            }
            stickDeadzone = camera.StickDeadzone;
            alternateNearClip = camera.AlternateNearClip;

            enableOffsetPerspective = config.PerspectiveCameraEnabled;

            // 設定を読み込んだ直後にメモリ上の古い設定で上書き保存している。削除またはコメントアウト
            //ConfigManager.SaveConfig<Config>(this);

            return config;
        }

        public PluginData Initialize()
        {
            PluginData data = new PluginData();

            Animal.Initialize();

            getViewParamOffset = new NativeFunction<nint, int, int, nint>(0x14136E6C0);

#if QUARANTINED_FEATURES
            // MonsterHunterWorld.exe+20354F7 - call MonsterHunterWorld.exe+1F73850
            setZoneState = new NativeAction<nint, int>(0x142035020);
            setPlayerController1 = new NativeAction<nint>(0x141F73850);
            setPlayerController5 = new NativeAction<nint>(0x14118DDC0);
            zoneStateForcePassive1 = new Patch(lton(0x140256A3F) + 0x6, [0x1]);
            zoneStateForcePassive2 = new Patch(lton(0x141AC2865) + 0x6, [0x1]);
            zoneStateForceCombat1 = new Patch(lton(0x141AC2938) + 0x6, [0x0]);
            zoneStateForceCombat2 = new Patch(lton(0x141AC28EC) + 0x6, [0x0]);
#endif
            setZoneStateHook = Hook.Create<ZoneStateDelegate>(0x142035020, SetZoneStateHook); // nint, int

            procEnvironmentCollision = new NativeAction<nint, nint>(0x141F737D0);
            psuedoObject1 = (nint)NativeMemory.AllocZeroed(0x7C);
            MemoryUtil.WriteBytes(psuedoObject1 + 0x30, [0x03]);
            psuedoObject2 = (nint)NativeMemory.AllocZeroed(0x7C);
            MemoryUtil.WriteBytes(psuedoObject2 + 0x30, [0x08]);

            // Asserts based on version 15.23.00.
            // @TODO: Handle addr not found.

            // @TODO: Pick camera functions out of the upper function (they happen in immediate succession).
            // Place where we can check camera state early.
            nint addr = PatternScanner.FindFirst(Pattern.FromString("40 53 48 81 EC 80 00 00 00 8B 81 B0 17 00 00 48 8B D9 85 C0 ?? ??"));
            Assert(addr == 0x141FA11A0); // nint
            setCameraHook = Hook.Create<ProcessCameraDelegate>(addr, SetCameraHook);

            // Place where we can adjust the camera position.
            addr = PatternScanner.FindFirst(Pattern.FromString("48 8B C4 55 41 57 48 81 EC D8 00 00 00 44 0F 29 40 B8 45 33 FF ?? ?? ?? ?? ?? ?? ?? ?? ?? 48 8B E9"));
            Assert(addr == 0x141FA5380); // nint
            calculateCameraHook = Hook.Create<ProcessCameraDelegate>(addr, CalculateCameraHook);

            checkCameraHook = Hook.Create<ProcessCameraDelegate>(0x141FA2130, CheckCameraHook); // nint

            startViewModeHook = Hook.Create<ViewModeDelegate>(0x1405842E0, StartViewModeHook); // nint
            setupGestureMenu = new NativeAction<nint, nint, nint>(0x141E12AF0);
            showGestureMenu = new NativeAction<nint>(0x141FB79B0);
            /*
            // @TODO: View mode collision check here
            //  MonsterHunterWorld.exe+23268DF - call MonsterHunterWorld.exe+2326B10
            //  can be overridden by the player collision function here
            //  MonsterHunterWorld.exe+23268DF - call MonsterHunterWorld.exe+23269F0
            //  to evaluate differences.
            //  Other TODOs for View Mode interop.
            //   - Don't colide with water.
            //   - Disable box around player collision.
            //   - View mode fade on close to player.
            //   - Weird LOD differences when in view mode.
            startViewMode = new NativeAction<nint>(0x1405842E0);
            stopViewMode = new NativeAction<nint>(0x1405825A0);
            processViewMode = new NativeAction<nint>(0x140582820);
            viewMode1 = new Patch((nint)0x141ADFAB9, [0x90, 0x90]); // Check input.
            viewMode1.Enable();
            // rax = 0x73 -> 0x43
            //viewMode2 = new Patch((nint)0x1405843F8, [0x48, 0x8B, 0x43, 0x10, 0x90]);
            //viewMode2.Enable();
            viewMode3 = new Patch((nint)0x1405844ED, [0x90, 0x90, 0x90, 0x90, 0x90]); // Screen flash related.
            viewMode3.Enable();
            //viewMode2 = new Patch((nint)0x1405844CB, [0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90]); // Disables view mode from starting.
            //viewMode2.Enable();
            //
            //viewMode1 = new Patch((nint)0x140582578, [0xEB]);
            ////viewMode1.Enable();
            //viewMode1_1 = new Patch((nint)0x140582F13, [0xEB]);
            ////viewMode1_1.Enable();
            //viewMode2 = new Patch((nint)0x1405831A5, [0x90, 0x90, 0x90, 0x90]);
            ////viewMode2.Enable();
            //viewMode3 = new Patch((nint)0x1405831A5 + 0xC, [0x90, 0x90, 0x90, 0x90, 0x90]);
            ////viewMode3.Enable();
            //viewMode4 = new Patch((nint)0x1405831A5 + 0xC + 0x11, [0x90, 0x90, 0x90, 0x90, 0x90]);
            ////viewMode4.Enable();
            psuedoViewModeObject = (nint)NativeMemory.AllocZeroed(240);
            MemoryUtil.WriteBytes(psuedoViewModeObject, [0x28, 0x23, 0xFB, 0x42, 0x01, 0x00, 0x00, 0x00]);
            */

            // Very special case for inside tent.
            addr = PatternScanner.FindFirst(Pattern.FromString("48 89 5C 24 10 48 89 6C 24 18 48 89 7C 24 20 41 56 48 83 EC 60 48 8B D9 0F 57 DB ?? ?? ?? ?? ?? ?? ?? 0F 57 D2 33 D2"));
            Assert(addr == 0x142106450); // nint
            setCameraTentHook = Hook.Create<SetCameraTentDelegate>(addr, SetCameraTentHook);

            addr = PatternScanner.FindFirst(Pattern.FromString("48 8B C4 48 89 58 10 48 89 70 18 55 57 41 54 41 56 41 57 48 8D 6C 24 80 48 81 EC 80 01 00 00 0F 29 70 C8 48 8B F9 0F 29 78 B8 0F 57 C9 44 0F 29 40 A8 0F 57 F6 44 0F 29 48 98 44 0F 29 50 88"));
            Assert(addr == 0x141FB12D0); // nint
            setCameraCutsceneHook = Hook.Create<SetCameraCutsceneDelegate>(addr, SetCameraCutsceneHook);

            /*
            addr = PatternScanner.FindFirst(Pattern.FromString("48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 48 89 7C 24 20 41 54 41 56 41 57 48 81 EC 90 00 00 00 ?? ?? ?? ?? ?? ?? ?? 48 8B F9"));
            Assert(addr == 0X14228EB60); // nint
            calculateViewHook = Hook.Create<CalculateViewDelegate>(addr, CalculateViewHook);
            */

            addr = PatternScanner.FindFirst(Pattern.FromString("48 89 5C 24 08 57 44 8B 9A 60 01 00 00 48 8B DA 41 0F BF 40 08 4D 8B D0 89 82 80 01 00 00 BF 00 10 00 00 41 0F BF 40 0A 89 82 84 01 00 00 41 0F BF 40 0C 89 82 78 01 00 00 41 0F BF 40 0E 89 82 7C 01 00 00"));
            Assert(addr == 0x1422A1280); // nint, nint, nint
            writePadInputHook = Hook.Create<WritePadInputDelegate>(addr, WritePadInputHook);

            // Place where we can change the analog stick value the game uses for player movement.
            addr = PatternScanner.FindFirst(Pattern.FromString("66 0F 6E C1 0F 5B C0 85 C9 78 11"));
            Assert(addr == 0x142107CB0); // int, float
            checkMovementHook = Hook.Create<CheckMovementDelegate>(addr, CheckMovementHook);

            // DODGE_Rバグのログ取り
            addr = PatternScanner.FindFirst(Pattern.FromString("48 8B C4 48 89 48 08 41 55 41 56 48 81 EC 18 01 00 00 48 83 B9 80 00 00 00 00"));
            Assert(addr == 0x142254AA0);
            actionRequestHook = Hook.Create<ActionRequestDelegate>(addr, ActionRequestHook);

            collisionCheckHook = Hook.Create<CollisionCheckDelegate>(0x1411C4E50, CollisionCheckHook); // nint, nint

            cameraEffectHook = Hook.Create<CameraEffectDelegate>(0x141AB6AE0, CameraEffectHook); // nint, nint, nint

            addr = PatternScanner.FindFirst(Pattern.FromString("84 C0 0F 84 ?? ?? ?? ?? 48 8B 57 28 45 33 C0 48 8B CE E8 ?? ?? ?? ?? 48 8B 97 E0 01 00 00 41 B0 01 48 8B CE E8 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B D8"));
            Assert(addr == 0x141E55D9E);
            forceMinimapFollowsCamera = new Patch(addr + 0x3, [0xE9]); // je -> jmp.

            addr = PatternScanner.FindFirst(Pattern.FromString("48 89 5C 24 18 48 89 6C 24 20 57 48 83 EC 20 48 8B 59 68 48 8B FA 48 8B E9 48 85 DB 0F 84 ?? ?? ?? ?? 4C 89 74 24 38 4C 8B B3 80 00 00 00 4D 85 F6"));
            Assert(addr == 0x14234DD60);
            jmpOverUi = new Patch(addr + 0x1C, [0xE9, 0x65, 0x01, 0x00, 0x00, 0x90]);

            addr = PatternScanner.FindFirst(Pattern.FromString("CC 48 89 5C 24 18 57 48 83 EC 30 48 89 74 24 48 48 8B D9 E8 ?? ?? ?? ?? 48 8B 8B B0 0D 00 00 8B 81 54 89 00 00 C1 E8 0D A8 01"));
            Assert(addr == 0x1411A6A9F);
            noopCharacterFade = new Patch(addr + 0x13, [0x90, 0x90, 0x90, 0x90, 0x90]); // call MonsterHunterWorld.exe+11A6D50.
            if (disableCharacterFade && !disableMod)
            {
                noopCharacterFade.Enable();
            }

            underwaterCamera1 = new Patch(lton(0x1412AD54D), [0x90, 0x90, 0x90, 0x90, 0x90, 0x90]); // Nop "Is diving" check.
            underwaterCamera2 = new Patch(lton(0x1412AD571), [0xB1, 0x01, 0x28, 0xC1, 0x90, 0x90, 0x90]); // Make cl the inverse of al.
            underwaterCamera3 = new Patch(lton(0x141FA5A7E), [0x90, 0x90]); // Nop check.
            underwaterCamera4 = new Patch(lton(0x141FA5BA0), [0x40, 0x84, 0xF6, 0x90, 0x74]); // jae -> je.
            underwaterCamera5 = new Patch(lton(0x141FA5A74), [0x0F, 0x57, 0xF6, 0x31, 0xD2, 0x48, 0x8D, 0x4D, 0x80, 0xE8, 0x9E, 0x41, 0x38, 0xFE, 0xC1, 0xE8, 0x14, 0x66, 0x25, 0xD8, 0x03, 0xFF, 0xC8, 0x66, 0x3D, 0x48, 0x00, 0x77]);
            nullifyDofCoc = new Patch(lton(0x1424236DD), [0x0F, 0x57, 0xF6, 0x90, 0x90, 0x90, 0x90]);
            underwaterCheckHook = Hook.Create<UnderwaterCheck>(0x1412AD500, UnderwaterCheckHook); // nint

            getSaveSlotGuiAddr = new NativeFunction<nint, nint>(0x141AC0B60);
            getPlayerSettingsAddr = new NativeFunction<nint, nint>(0x141B8DBB0);

            forceDisableIK = new Patch(lton(0x14031FD40), [0xC6, 0x81, 0x81, 0x01, 0x00, 0x00, 0x00, 0xC3]);

            // Alt: 0x141FBC7A0 (model - 0x958)
            updateJointsHook = Hook.Create<UpdateJoints>(0x14223BA30, UpdateJointsHook);

#if QUARANTINED_FEATURES
            updateIKHook = Hook.Create<UpdateIK>(0x142472C00, UpdateIKHook);
#endif

            /*
            changeEquipmentHook = Hook.Create<ChangeEquipment>(0x141DDEF80, ChangeEquipmentHook); // nint, int
            newEquipmentPointer = new NativeFunction<nint, int, nint>(0x141DE56A0);
            */

            // Player + ECCC = How far underwater is the player.
            jmpOverHotSpringsEval = new Patch(lton(0x1417C1B03), [0xEB]);
            jmpOverHotSpringsSteam = new Patch(lton(0x14203A494), [0xE9, 0x8D, 0x00, 0x00, 0x00, 0x90]);

            refreshEntityParamsHook = Hook.Create<RefreshEntityParams>(0x141F605F0, RefreshEntityParamsHook); // nint, nint

            noopPlayerWetnessUpdate = new Patch(lton(0x14203E893), [0x90, 0x90, 0x90, 0x90, 0x90]);
            jmpOverChangeEquipmentWetnessUpdate_1 = new Patch(lton(0x14203E310), [0xEB]); // je -> jmp.
            jmpOverChangeEquipmentWetnessUpdate_2 = new Patch(lton(0x14203E397), [0xE9, 0xB9, 0x00, 0x00, 0x00, 0x90]); // je -> jmp.

            // Noop collision checks.
            addr = PatternScanner.FindFirst(Pattern.FromString("F3 0F 11 06 F3 0F 10 48 04 F3 0F 58 4E 04 F3 0F 11 4E 04 F3 0F 10 40 08 F3 0F 58 46 08 F3 0F 11 46 08 44 8B AF E0 0B 00 00"));
            Assert(addr == 0x141C001B5);
            disableXCollision = new Patch(addr, [0x90, 0x90, 0x90, 0x90]);
            Assert(addr + 0xE == 0x141C001C3);
            disableYCollision = new Patch(addr + 0xE, [0x90, 0x90, 0x90, 0x90, 0x90]);
            Assert(addr + 0x1D == 0x141C001D2);
            disableZCollision = new Patch(addr + 0x1D, [0x90, 0x90, 0x90, 0x90, 0x90]);

            addr = PatternScanner.FindFirst(Pattern.FromString("F3 0F 11 46 04 F3 0F 10 46 08 F3 0F 5C C2 F3 0F 11 46 08 ?? ?? F3 0F"));
            Assert(addr == 0x141C000D2);
            disableSecondaryYCollision = new Patch(addr, [0x90, 0x90, 0x90, 0x90, 0x90]);

            addr = PatternScanner.FindFirst(Pattern.FromString("F3 0F 11 46 04 F3 0F 10 58 08 F3 0F 58 5E 08 F3 0F 11 5E 08 44 8B 87 D0 0B 00 00 41 F6 C0 03"));
            Assert(addr == 0x141BFFF90);
            disableExtraYCollision = new Patch(addr, [0x90, 0x90, 0x90, 0x90, 0x90]);

            addr = PatternScanner.FindFirst(Pattern.FromString("F3 0F 11 83 80 00 00 00 F3 0F 11 8B 84 00 00 00 F3 0F 11 93 88 00 00 00 89 B3 8C 00 00 00 8B 83 A4 01 00 00 C1 E8 05 44 0F 29 A4 24 D0 01 00 00 44 0F 29 B4 24 B0 01 00 00 A8 01"));
            Assert(addr + 0x8 == 0x1413259ED);
            disableGravityYUpdate = new Patch(addr + 0x8, [
                0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90,
            ]);
            disableGravityXZUpdate = new Patch(addr, [
                0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90,
                0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, // Will already be applied.
                0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90
            ]);

            addr = PatternScanner.FindFirst(Pattern.FromString("F3 0F 11 97 54 15 00 00 F3 0F 11 8F 58 15 00 00 F3 0F 10 47 68 F3 0F 5E D8 44 89 B7 6C 15 00 00 F3 0F 5E D0 F3 0F 5E C8 F3 0F 11 9F 60 15 00 00 F3 0F 11 97 64 15 00 00 F3 0F 11 8F 68 15 00 00"));
            Assert(addr + 0x40 == 0x141BFFF75);
            disableGravityEval = new Patch(addr + 0x40, [0x90, 0x90, 0x90, 0x90, 0x90]); // call MonsterHunterWorld.exe+1325810.

            return data;
        }

        // メソッド名を Dispose から OnUnload に変更します
        public void OnUnload()
        {
            // --- Hookの解除 ---
            // Hook<T> はクラス(参照型)なので ?.Dispose() でOKです
            setZoneStateHook?.Dispose();
            setCameraHook?.Dispose();
            calculateCameraHook?.Dispose();
            checkCameraHook?.Dispose();
            startViewModeHook?.Dispose();
            setCameraTentHook?.Dispose();
            setCameraCutsceneHook?.Dispose();
            writePadInputHook?.Dispose();
            checkMovementHook?.Dispose();
            actionRequestHook?.Dispose();
            collisionCheckHook?.Dispose();
            cameraEffectHook?.Dispose();
            underwaterCheckHook?.Dispose();
            updateJointsHook?.Dispose();
            refreshEntityParamsHook?.Dispose();

            // --- Patchの無効化 ---
            // Patch は構造体(struct)なので null にならず、?. は使えません。
            // ? を外し、そのまま .Disable() を呼ぶだけで元のバイト列に復元されます。
            forceMinimapFollowsCamera.Disable();
            noopCharacterFade.Disable();
            jmpOverUi.Disable();

            underwaterCamera1.Disable();
            underwaterCamera2.Disable();
            underwaterCamera3.Disable();
            underwaterCamera4.Disable();
            underwaterCamera5.Disable();
            nullifyDofCoc.Disable();

            forceDisableIK.Disable();
            jmpOverHotSpringsEval.Disable();
            jmpOverHotSpringsSteam.Disable();

            disableXCollision.Disable();
            disableYCollision.Disable();
            disableSecondaryYCollision.Disable();
            disableZCollision.Disable();
            disableExtraYCollision.Disable();
            disableGravityYUpdate.Disable();
            disableGravityXZUpdate.Disable();
            disableGravityEval.Disable();

            noopPlayerWetnessUpdate.Disable();
            jmpOverChangeEquipmentWetnessUpdate_1.Disable();
            jmpOverChangeEquipmentWetnessUpdate_2.Disable();

            // --- 確保したネイティブメモリの解放 ---
            if (psuedoObject1 != 0)
            {
                NativeMemory.Free((void*)psuedoObject1);
                psuedoObject1 = 0;
            }
            if (psuedoObject2 != 0)
            {
                NativeMemory.Free((void*)psuedoObject2);
                psuedoObject2 = 0;
            }
        }

        // This doesn't include decals or monsters.
        private bool getViewportFadeObjects(int viewportIndex)
        {
            Viewport vp = CameraSystem.GetViewport(viewportIndex);
            return MemoryUtil.Read<byte>(vp.Instance + 0x21) == 0x1;
        }

        private void setViewportFadeObjects(int viewportIndex, bool enable)
        {
            Viewport vp = CameraSystem.GetViewport(viewportIndex);
            MemoryUtil.GetRef<byte>(vp.Instance + 0x21) = ByteFlag(enable);
        }

        private static bool getPassthroughEnabled()
        {
            return MemoryUtil.Read<byte>(sMhScene.Instance + 0xE9A0) == 0x1;
        }

        private static void setPassthroughEnabled(bool enable)
        {
            MemoryUtil.GetRef<byte>(sMhScene.Instance + 0xE9A0) = ByteFlag(enable);
        }

        private void setDisableCharacterFade(bool disable)
        {
            if (disable && !disableCharacterFade)
            {
                noopCharacterFade.Enable();
                disableCharacterFade = true;
            }
            else if (!disable && disableCharacterFade)
            {
                noopCharacterFade.Disable();
                disableCharacterFade = false;
            }
        }

        private void setDisableFading(bool disable)
        {
            setPassthroughEnabled(!disable);
            setDisableCharacterFade(disable);
        }

        private void setPerspectivePreset(Config.Preset preset)
        {
            if (restoreFov != null)
            {
                restoreFov = preset.FieldOfView;
            }
            else
            {
                cameraFov = preset.FieldOfView;
            }
            if (restoreRoll != null)
            {
                restoreRoll = preset.Roll;
            }
            else
            {
                cameraRoll = preset.Roll;
            }
            orbitDistance += cameraForward - preset.Forward;
            cameraForward = preset.Forward;
            cameraRight = preset.Right;
            cameraUp = preset.Up;
            disableFading = preset.DisableFading;
            if (!freeCamera)
            {
                setDisableFading(disableFading);
            }
        }

        public void OnLoad()
        {
            loadSessionAndOrbitalSettings();
            // "Disable Fading of Objects/Monsters" default ON - passthrough
            // disabled means fading is disabled.
            setPassthroughEnabled(false);
            Config config = loadConfig();
            if (enableOffsetPerspective && config.Selected != "")
            {
                setPerspectivePreset(config.Presets[config.Selected]);
            }
#if QUARANTINED_FEATURES
            if (config.TuningToolInterop)
            {
                if (TuningToolInterop.InitFromLoadedInstance())
                {
                    Log.Info($"Successfully loaded '{TuningToolInterop.PluginName}' instance");
                }
                else
                {
                    Log.Warn($"Couldn't find loaded '{TuningToolInterop.PluginName}' instance, features will be missing");
                }
            }
#endif
        }

        private void toggleUi()
        {
            uiToggled = !uiToggled;
            if (uiToggled)
            {
                jmpOverUi.Enable();
            }
            else
            {
                jmpOverUi.Disable();
            }
            if (unlockInputForMenu)
            {
                unlockInputHideMenu = uiToggled;
            }
        }

        private Player? getPlayerFromSaveSlot()
        {
            nint guiAddr = getSaveSlotGuiAddr.Invoke(MemoryUtil.Read<nint>(0x1451C42B8));
            if (guiAddr == 0x0)
            {
                saveSlotIndex = -1;
                return null;
            }
            // MonsterHunterWorld.exe+ADC42F - movsxd  rcx,dword ptr [rax+00000334]
            saveSlotIndex = MemoryUtil.Read<int>(guiAddr + 0x334);
            if (saveSlotIndex == -1)
            {
                return null;
            }
            // MonsterHunterWorld.exe+1B42047 - add r8,00000740
            nint offset = (sPlayer.Instance + 0x40) + (0x740 * saveSlotIndex);
            if (offset == 0x0 || MemoryUtil.Read<nint>(offset) != 0x1433FE588)
            {
                return null;
            }
            nint playerAddr = MemoryUtil.Read<nint>(offset + 0x18);
            if (playerAddr == 0x0)
            {
                return null;
            }
            return new Player(playerAddr);
        }

        private Player? getPlayer() { return Player.MainPlayer; }
        private Player? getPlayerWithFallback()
        {
            Player? player = getPlayer();
            if (player == null)
            {
                player = getPlayerFromSaveSlot();
            }
            return player;
        }

        private Player? checkPlayerChange()
        {
            Player? player = getPlayer();
            if (player != lastPlayer)
            {
                pCamera = null;
                pCameraViewportIndex = -1;
                lastPlayer = player;
            }
            return player;
        }

        private int getVisibleCamera()
        {
            for (int i = 0; i < 8; i++)
            {
                Viewport vp = CameraSystem.GetViewport(i);
                // A viewport can be visible with a null camera.
                if (vp.Visible && vp.Camera != null)
                {
                    return i;
                }
            }
            return -1;
        }

        private void checkCurrentVisibleCamera(Player? player)
        {
            int prevIndex = vCameraViewportIndex;
            vCameraViewportIndex = getVisibleCamera();
            if (vCameraViewportIndex >= 0)
            {
                Camera camera = CameraSystem.GetViewport(vCameraViewportIndex).Camera!;
                // Evaluate potential free camera target change.
                if (freeCamera)
                {
                    if (vCamera != camera)
                    {
                        if (player != null)
                        {
                            // Try to position the camera behind the player.
                            cameraPosition = player.Position;
                            cameraPosition.X -= player.Forward.X * 250.0f;
                            cameraPosition.Y += 200.0f;
                            cameraPosition.Z -= player.Forward.Z * 250.0f;
                            cameraTarget = player.Position;
                            cameraTarget.Y += 150.0f;
                        }
                        else
                        {
                            cameraPosition = camera.Position;
                            cameraTarget = camera.Target;
                        }
                        Quaternion forward = Quaternion.Normalize(getForward(cameraPosition, cameraTarget));
                        cameraYaw = Single.RadiansToDegrees(MathF.Atan2(forward.Z, forward.X));
                        cameraPitch = Single.RadiansToDegrees(MathF.Asin(forward.Y));
                    }
                }
                vCamera = camera;
                if (!debugNearClipApplied)
                {
                    vCamera.NearClip = debugNearClip;
                    debugNearClipApplied = true;
                }

                // --- NearClip を常に 1.0f に固定する ---
                // ここで確実に 1.0f にする（ゲームのどこかが上書きしても
                // フレーム内の早い段階で強制する）
                vCamera.NearClip = 1.0f;
                debugNearClipApplied = true;
                // ----------------------------------------

                if (!orbitAltNearClipApplied)
                {
                    if (orbitAltNearClipToggled)
                    {
                        setNearClip(vCamera, true);
                    }
                    orbitAltNearClipApplied = true;
                }
                // Assume that after a player is set the visible camera is the player camera.
                if (pCamera == null && player != null)
                {
                    pCamera = vCamera;
                    pCameraViewportIndex = vCameraViewportIndex;
                }
            }
            else
            {
                vCamera = null;
                pCamera = null;
                pCameraViewportIndex = -1;
            }
        }

        // Filters the raw target rotation for the joint-following orbit camera,
        // based on how fast it's actually rotating (degrees/second), not a
        // fixed per-frame angle. At low angular speed (standing, walking - the
        // neck-sway "noise" range) we smooth heavily and gradually cancel roll
        // so the view doesn't sit tilted. At high angular speed (dodge rolls,
        // weapon spin attacks) we track the raw rotation almost immediately and
        // leave roll untouched, so those still look fully dynamic. In between,
        // both the smoothing amount and the roll-cancel amount fade linearly
        // with speed, so there's no sudden "pop" as you cross a threshold.
        // allowLevelRoll: the "Re-level Roll When Slow" behavior below was
        // designed for the non-SimpleLock Rotation Joint mode, where the
        // rotation isn't a direct face reconstruction and some artificial
        // leveling is desirable. Pass false when smoothing "A" (SimpleLock
        // full-rotation) so its roll always matches the actual face
        // orientation exactly, regardless of the "Re-level Roll When Slow"
        // checkbox - only the jitter-smoothing part of this function should
        // apply to A, never this leveling.
        private Quaternion stabilizeOrbitRotation(Quaternion targetRotation, float deltaTime, bool allowLevelRoll = true)
        {
            if (!orbitStableInit || !orbitPrevRawInit)
            {
                orbitStableRotation = targetRotation;
                orbitPrevRawRotation = targetRotation;
                orbitStableInit = true;
                orbitPrevRawInit = true;
                return orbitStableRotation;
            }

            // True angular speed of the *raw* signal, independent of our own
            // smoothing lag. deltaTime here is expressed as "60fps-frames"
            // (1.0 == one frame at 60fps), so elapsedSeconds = deltaTime / 60.
            float rawDot = Math.Clamp(MathF.Abs(Quaternion.Dot(orbitPrevRawRotation, targetRotation)), 0.0f, 1.0f);
            float rawAngleDeg = Single.RadiansToDegrees(2.0f * MathF.Acos(rawDot));
            float elapsedSeconds = MathF.Max(deltaTime, 0.0001f) / 60.0f;
            float angularSpeedDegPerSec = rawAngleDeg / elapsedSeconds;
            orbitLastAngularSpeed = angularSpeedDegPerSec;
            orbitPrevRawRotation = targetRotation;

            float speedRange = MathF.Max(orbitStabilizeMaxSpeed - orbitStabilizeMinSpeed, 0.0001f);
            float speedFrac = Math.Clamp((angularSpeedDegPerSec - orbitStabilizeMinSpeed) / speedRange, 0.0f, 1.0f);

            // Smoothing strength: minAlpha (heavy smoothing) at/below minSpeed,
            // ramping up to 1.0 (no smoothing, instant tracking) at/above maxSpeed.
            float alpha = Math.Clamp(orbitStabilizeMinAlpha + (1.0f - orbitStabilizeMinAlpha) * speedFrac, 0.0f, 1.0f);

            Quaternion blendTarget = targetRotation;
            if (orbitLevelRoll && allowLevelRoll)
            {
                // How much to cancel roll: full cancel at low speed, none at high speed.
                float levelAmount = 1.0f - speedFrac;
                if (levelAmount > 0.0f)
                {
                    Vector3 targetForward = Vector3.Transform(new Vector3(0.0f, 0.0f, 1.0f), targetRotation);
                    if (targetForward.LengthSquared() > 1e-8f)
                    {
                        Quaternion leveledTarget = lookRotation(targetForward, new Vector3(0.0f, 1.0f, 0.0f));
                        blendTarget = Quaternion.Slerp(targetRotation, leveledTarget, levelAmount);
                    }
                }
            }

            orbitStableRotation = Quaternion.Slerp(orbitStableRotation, blendTarget, alpha);
            return orbitStableRotation;
        }

        // Normalizes v, falling back to a normalized fallback if v is (near)
        // zero-length - guards the Up-vector lerp above against the rare case
        // where two consecutive frames' Up vectors are near-exactly opposite.
        private static Vector3 safeNormalize(Vector3 v, Vector3 fallback)
        {
            float lenSq = v.LengthSquared();
            if (lenSq > 1e-8f)
            {
                return v / MathF.Sqrt(lenSq);
            }
            Vector3 fb = fallback;
            float fbLenSq = fb.LengthSquared();
            return fbLenSq > 1e-8f ? fb / MathF.Sqrt(fbLenSq) : Vector3.UnitY;
        }

        // Builds a rotation from a forward direction and a preferred up vector
        // (used to re-level roll while the orbit camera is "stable", see above).
        private static Quaternion lookRotation(Vector3 forward, Vector3 up)
        {
            forward = Vector3.Normalize(forward);
            Vector3 right = Vector3.Cross(up, forward);
            if (right.LengthSquared() < 1e-6f)
            {
                right = Vector3.Cross(new Vector3(0.0f, 0.0f, 1.0f), forward);
            }
            right = Vector3.Normalize(right);
            Vector3 correctedUp = Vector3.Cross(forward, right);

            Matrix4x4 m = new Matrix4x4(
                right.X, right.Y, right.Z, 0.0f,
                correctedUp.X, correctedUp.Y, correctedUp.Z, 0.0f,
                forward.X, forward.Y, forward.Z, 0.0f,
                0.0f, 0.0f, 0.0f, 1.0f);

            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
        }

        // Decomposes a rotation into yaw (around Y), pitch (around X), and roll
        // (around Z) degrees, matching the same axis convention used by this
        // file's Quaternion.CreateFromYawPitchRoll calls. Used to check how far
        // the dynamic rotation joint deviates from the base joint on each axis
        // independently, since a sideways roll is mostly "roll", a forward
        // somersault is mostly "pitch", and plain turning is mostly "yaw".
        // Returns the same action name shown in the Animation UI (e.g. the
        // "SPIN_ATTACK3" part of "WP_04::SPIN_ATTACK3"), or null if unavailable.
        // Persistence for everything the user previously had to re-set up
        // each session (Enable Free Camera, Toggles, force-hidden armor
        // parts, debug near clip, slow-motion speed) plus the Orbital
        // Camera's position/rotation setup and the A/B/C motion-category
        // clamp system, all via the plugin's normal Config/ConfigManager.
        private void loadSessionAndOrbitalSettings()
        {
            Config config = loadConfig();
            Config.SessionState session = config.Session;
            enableFreeCamera = session.EnableFreeCamera;
            unlockInputToggled = session.UnlockInput;
            unlockMovementToggled = session.UnlockPlayerMovement;
            orbitPlayer = session.OrbitPlayer;
            orbitIgnoreCamera = session.IgnoreCameraDirection;
            orbitDecoupleMovementFromLook = session.DecoupleMovementFromLook;
            orbitDecoupleMovementInvert = session.DecoupleMovementInvert;
            stickyHideArmor[Armor.Helmet] = session.HideHelmet;
            stickyHideArmor[Armor.Hair] = session.HideHair;
            stickyHideArmor[Armor.Face] = session.HideFace;
            stickyHideArmor[Armor.EyeLens] = session.HideEyeLens;
            //debugNearClip = session.DebugNearClip;
            debugNearClip = 1.0f;// Saved セッション値に関係なく NearClip を常に 1.0f に固定する
            orbitAltNearClipToggled = session.AltNearClipToggled;
            orbitAltNearClipApplied = false;
            debugNearClipApplied = false;
            cameraFov = session.CameraFov;
            cameraFovPreloaded = true;
            slowMotionSpeed = session.SlowMotionSpeed;

            Config.OrbitalCameraSettings o = config.OrbitalCamera;
            orbitTargetFaceJoint = o.TargetFaceJoint;
            orbitJoint = o.TargetJoint;
            orbitSimpleLock = o.SimpleLock;
            orbitFaceBasisEnable = o.FaceBasisEnable;
            orbitFaceBasisCenterJoint = o.FaceBasisCenterJoint;
            orbitFaceBasisRightEarJoint = o.FaceBasisRightEarJoint;
            orbitFaceBasisLeftEarJoint = o.FaceBasisLeftEarJoint;
            orbitMovementRotation = o.MovementRotation;
            orbitStickSnapAngleDeg = o.StickSnapAngleDeg;
            orbitY = o.TargetY;
            orbitRight = o.TargetRight;
            orbitForward = o.TargetForward;
            //ver8 load
            orbitLerp = o.Lerp;
            orbitRecenterTapMaxSeconds = o.RecenterTapMaxSeconds;//ver9.3
            orbitReturnToCenterLook = o.ReturnToCenterLook;
            orbitReturnToCenterYawMax = o.ReturnToCenterYawMax;
            orbitReturnToCenterPitchMax = o.ReturnToCenterPitchMax;
            orbitReturnToCenterSpeed = o.ReturnToCenterSpeed;
            orbitRightStickAngleSnapSteps = o.RightStickAngleSnapSteps;
            //ver8ここまで
            orbitProfiles = new List<OrbitProfile>();
            if (o.Profiles != null)
            {
                foreach (Config.OrbitProfile savedProfile in o.Profiles)
                {
                    //ver10
                    List<OrbitGazeKeyframe> gazeKeyframes = new List<OrbitGazeKeyframe>();
                    if (savedProfile.GazeKeyframes != null)
                    {
                        foreach (Config.GazeKeyframe savedKeyframe in savedProfile.GazeKeyframes)
                        {
                            gazeKeyframes.Add(new OrbitGazeKeyframe
                            {
                                T = savedKeyframe.T,
                                Yaw = savedKeyframe.Yaw,
                                Pitch = savedKeyframe.Pitch,
                                Roll = savedKeyframe.Roll,
                            });
                        }
                    }
                    //ver11
                    // 旧バージョンの NewCamera.json には GazeRanges が存在しない
                    // ので、null のときは空リストのまま (= 制限なし) にしておく。
                    List<OrbitGazeRange> gazeRanges = new List<OrbitGazeRange>();
                    if (savedProfile.GazeRanges != null)
                    {
                        foreach (Config.GazeRange savedRange in savedProfile.GazeRanges)
                        {
                            gazeRanges.Add(new OrbitGazeRange
                            {
                                Start = savedRange.Start,
                                End = savedRange.End,
                                Blend = savedRange.Blend,
                            });
                        }
                    }
                    // Use Gaze Keyframes の XYZ 版。旧バージョンの NewCamera.json
                    // には存在しないので、null のときは空リストのままにしておく。
                    List<OrbitGazePositionKeyframe> gazePositionKeyframes = new List<OrbitGazePositionKeyframe>();
                    if (savedProfile.GazePositionKeyframes != null)
                    {
                        foreach (Config.GazePositionKeyframe savedPositionKeyframe in savedProfile.GazePositionKeyframes)
                        {
                            gazePositionKeyframes.Add(new OrbitGazePositionKeyframe
                            {
                                T = savedPositionKeyframe.T,
                                Y = savedPositionKeyframe.Y,
                                Right = savedPositionKeyframe.Right,
                                Forward = savedPositionKeyframe.Forward,
                            });
                        }
                    }
                    orbitProfiles.Add(new OrbitProfile
                    {
                        Mode = (OrbitProfileMode)(int)savedProfile.Mode,
                        Keywords = savedProfile.Keywords != null ? new List<string>(savedProfile.Keywords) : new List<string>(),
                        Comment = savedProfile.Comment ?? "",
                        //ver14
                        WeaponGroup = savedProfile.WeaponGroup ?? "",
                        WeaponGroupInput = savedProfile.WeaponGroup ?? "",
                        //ver14ここまで
                        GazeUseRanges = savedProfile.GazeUseRanges,
                        GazeRanges = gazeRanges,
                        // 旧バージョンの NewCamera.json には存在しないフィールドなので、
                        // 0 (未設定) だった場合は既定の 1.0 秒にフォールバックする。
                        GazeUseSubStateTimer = savedProfile.GazeUseSubStateTimer,
                        GazeSubStateDuration = savedProfile.GazeSubStateDuration > 0.0f ? savedProfile.GazeSubStateDuration : 1.0f,
                        UseGazePositionKeyframes = savedProfile.UseGazePositionKeyframes,
                        GazePositionKeyframes = gazePositionKeyframes,
                        //ver11ここまで

                        YawRange = savedProfile.YawRange,
                        YawBlend = savedProfile.YawBlend,
                        PitchRange = savedProfile.PitchRange,
                        PitchBlend = savedProfile.PitchBlend,
                        RollRange = savedProfile.RollRange,
                        RollBlend = savedProfile.RollBlend,
                        TargetY = savedProfile.TargetY,
                        TargetRight = savedProfile.TargetRight,
                        TargetForward = savedProfile.TargetForward,
                        CorrectionYaw = savedProfile.CorrectionYaw,
                        CorrectionPitch = savedProfile.CorrectionPitch,
                        CorrectionRoll = savedProfile.CorrectionRoll,
                        ClampBaseSmoothing = savedProfile.ClampBaseSmoothing,
                        ShakeSuppressSeconds = savedProfile.ShakeSuppressSeconds,
                        LookSensitivityMultiplier = savedProfile.LookSensitivityMultiplier,
                        UseNativeAimCamera = savedProfile.UseNativeAimCamera,
                        UseNativeAimCameraDelaySeconds = savedProfile.UseNativeAimCameraDelaySeconds,
                        DisableLookWhileAiming = savedProfile.DisableLookWhileAiming,
                        UseGazeKeyframes = savedProfile.UseGazeKeyframes,
                        GazeKeyframes = gazeKeyframes,
                        EnableTransitionBlend = savedProfile.EnableTransitionBlend,
                        ProfileTransitionDuration = savedProfile.ProfileTransitionDuration,
                        //ver12
                        PositionJointOverrideEnable = savedProfile.PositionJointOverrideEnable,
                        PositionJointOverrideUseFace = savedProfile.PositionJointOverrideUseFace,
                        PositionJointOverrideJoint = savedProfile.PositionJointOverrideJoint,
                        //ver12ここまで
                        HideSlingerWhileActive = savedProfile.HideSlingerWhileActive,//ver13
                        DisableFreeCameraWhileActive = savedProfile.DisableFreeCameraWhileActive,//ver15
                    });
                }
            }
            orbitFaceClampEnable = o.FaceClampEnable;
            orbitBaseRotationJoint = o.BaseRotationJoint;
            orbitBaseRotationJointUseFace = o.BaseRotationJointUseFace;
            orbitClampBaseCorrectionYaw = o.ClampBaseCorrectionYaw;
            orbitClampBaseCorrectionPitch = o.ClampBaseCorrectionPitch;
            orbitClampBaseCorrectionRoll = o.ClampBaseCorrectionRoll;
            orbitClampSmoothing = o.ClampSmoothing;
            orbitClampBaseSmoothing = o.ClampBaseSmoothing;
            orbitNeckYawRange = o.NeckYawRange;
            orbitNeckYawBlend = o.NeckYawBlend;
            orbitNeckPitchRange = o.NeckPitchRange;
            orbitNeckPitchBlend = o.NeckPitchBlend;
            orbitNeckRollRange = o.NeckRollRange;
            orbitNeckRollBlend = o.NeckRollBlend;
            orbitFinalRotationMaxSpeed = o.FinalRotationMaxSpeed;
            orbitFinalPositionMaxSpeed = o.FinalPositionMaxSpeed;
            orbitForceLevelRoll = o.ForceLevelRoll;
            orbitSpotDistance = o.SpotDistance;
            orbitSpotReanchorRate = o.SpotReanchorRate;
            orbitProfileTransitionDuration = o.ProfileTransitionDuration;
            orbitDefaultEnableTransitionBlend = o.DefaultEnableTransitionBlend;
        }

        private void saveSessionAndOrbitalSettings()
        {
            Config config = ConfigManager.GetConfig<Config>(this);
            config.Session = new Config.SessionState
            {
                EnableFreeCamera = enableFreeCamera,
                UnlockInput = unlockInputToggled,
                UnlockPlayerMovement = unlockMovementToggled,
                OrbitPlayer = orbitPlayer,
                IgnoreCameraDirection = orbitIgnoreCamera,
                DecoupleMovementFromLook = orbitDecoupleMovementFromLook,
                DecoupleMovementInvert = orbitDecoupleMovementInvert,
                HideHelmet = stickyHideArmor[Armor.Helmet],
                HideHair = stickyHideArmor[Armor.Hair],
                HideFace = stickyHideArmor[Armor.Face],
                HideEyeLens = stickyHideArmor[Armor.EyeLens],
                DebugNearClip = debugNearClip,
                AltNearClipToggled = orbitAltNearClipToggled,
                CameraFov = cameraFov,
                SlowMotionSpeed = slowMotionSpeed,
            };
            config.OrbitalCamera = new Config.OrbitalCameraSettings
            {
                TargetFaceJoint = orbitTargetFaceJoint,
                TargetJoint = orbitJoint,
                SimpleLock = orbitSimpleLock,
                FaceBasisEnable = orbitFaceBasisEnable,
                FaceBasisCenterJoint = orbitFaceBasisCenterJoint,
                FaceBasisRightEarJoint = orbitFaceBasisRightEarJoint,
                FaceBasisLeftEarJoint = orbitFaceBasisLeftEarJoint,
                MovementRotation = orbitMovementRotation,
                StickSnapAngleDeg = orbitStickSnapAngleDeg,
                TargetY = orbitY,
                TargetRight = orbitRight,
                TargetForward = orbitForward,
                //ver8 save
                Lerp = orbitLerp,
                RecenterTapMaxSeconds = orbitRecenterTapMaxSeconds,//ver9.3
                ReturnToCenterLook = orbitReturnToCenterLook,
                ReturnToCenterYawMax = orbitReturnToCenterYawMax,
                ReturnToCenterPitchMax = orbitReturnToCenterPitchMax,
                ReturnToCenterSpeed = orbitReturnToCenterSpeed,
                RightStickAngleSnapSteps = orbitRightStickAngleSnapSteps,
                //ver8ここまで

                //ver10.1
                Profiles = orbitProfiles.ConvertAll(p => new Config.OrbitProfile
                {
                    Mode = (Config.OrbitProfileMode)(int)p.Mode,
                    Keywords = new List<string>(p.Keywords),
                    Comment = p.Comment ?? "",
                    WeaponGroup = p.WeaponGroup ?? "",//ver14
                    GazeUseRanges = p.GazeUseRanges,
                    GazeRanges = p.GazeRanges.ConvertAll(r => new Config.GazeRange
                    {
                        Start = r.Start,
                        End = r.End,
                        Blend = r.Blend,
                    }),
                    //ver11
                    GazeUseSubStateTimer = p.GazeUseSubStateTimer,
                    GazeSubStateDuration = p.GazeSubStateDuration,
                    UseGazePositionKeyframes = p.UseGazePositionKeyframes,
                    GazePositionKeyframes = p.GazePositionKeyframes.ConvertAll(k => new Config.GazePositionKeyframe
                    {
                        T = k.T,
                        Y = k.Y,
                        Right = k.Right,
                        Forward = k.Forward,
                    }),
                    YawRange = p.YawRange,
                    //ver11ここまで

                    YawBlend = p.YawBlend,
                    PitchRange = p.PitchRange,
                    PitchBlend = p.PitchBlend,
                    RollRange = p.RollRange,
                    RollBlend = p.RollBlend,
                    TargetY = p.TargetY,
                    TargetRight = p.TargetRight,
                    TargetForward = p.TargetForward,
                    CorrectionYaw = p.CorrectionYaw,
                    CorrectionPitch = p.CorrectionPitch,
                    CorrectionRoll = p.CorrectionRoll,
                    ClampBaseSmoothing = p.ClampBaseSmoothing,
                    ShakeSuppressSeconds = p.ShakeSuppressSeconds,
                    LookSensitivityMultiplier = p.LookSensitivityMultiplier,
                    UseNativeAimCamera = p.UseNativeAimCamera,
                    UseNativeAimCameraDelaySeconds = p.UseNativeAimCameraDelaySeconds,
                    DisableLookWhileAiming = p.DisableLookWhileAiming,
                    UseGazeKeyframes = p.UseGazeKeyframes,
                    GazeKeyframes = p.GazeKeyframes.ConvertAll(k => new Config.GazeKeyframe
                    {
                        T = k.T,
                        Yaw = k.Yaw,
                        Pitch = k.Pitch,
                        Roll = k.Roll,
                    }),
                    EnableTransitionBlend = p.EnableTransitionBlend,
                    ProfileTransitionDuration = p.ProfileTransitionDuration,
                    //ver12
                    PositionJointOverrideEnable = p.PositionJointOverrideEnable,
                    PositionJointOverrideUseFace = p.PositionJointOverrideUseFace,
                    PositionJointOverrideJoint = p.PositionJointOverrideJoint,
                    //ver12ここまで
                    HideSlingerWhileActive = p.HideSlingerWhileActive,//ver13
                    DisableFreeCameraWhileActive = p.DisableFreeCameraWhileActive,//ver15
                }),
                FaceClampEnable = orbitFaceClampEnable,
                BaseRotationJoint = orbitBaseRotationJoint,
                BaseRotationJointUseFace = orbitBaseRotationJointUseFace,
                ClampBaseCorrectionYaw = orbitClampBaseCorrectionYaw,
                ClampBaseCorrectionPitch = orbitClampBaseCorrectionPitch,
                ClampBaseCorrectionRoll = orbitClampBaseCorrectionRoll,
                ClampSmoothing = orbitClampSmoothing,
                ClampBaseSmoothing = orbitClampBaseSmoothing,
                NeckYawRange = orbitNeckYawRange,
                NeckYawBlend = orbitNeckYawBlend,
                NeckPitchRange = orbitNeckPitchRange,
                NeckPitchBlend = orbitNeckPitchBlend,
                NeckRollRange = orbitNeckRollRange,
                NeckRollBlend = orbitNeckRollBlend,
                FinalRotationMaxSpeed = orbitFinalRotationMaxSpeed,
                FinalPositionMaxSpeed = orbitFinalPositionMaxSpeed,
                ForceLevelRoll = orbitForceLevelRoll,
                SpotDistance = orbitSpotDistance,
                SpotReanchorRate = orbitSpotReanchorRate,
                ProfileTransitionDuration = orbitProfileTransitionDuration,
                DefaultEnableTransitionBlend = orbitDefaultEnableTransitionBlend,
            };
            ConfigManager.SaveConfig<Config>(this);
        }

        private string? getCurrentActionName(Player player)
        {            
            ActionController actionController = player.ActionController;
            ActionInfo currentActionInfo = actionController.CurrentAction;
            if (currentActionInfo.ActionSet < 0 || currentActionInfo.ActionSet > 3)
            {
                return null;
            }
            ActionList actionList = actionController.GetActionList(currentActionInfo.ActionSet);
            if (currentActionInfo.ActionId < 0 || currentActionInfo.ActionId >= actionList.Count)
            {
                return null;
            }
            return actionList[currentActionInfo.ActionId].Name;
        }

        //ver10
        // "{Lmt}.{Id}" (e.g. "12.156"), matching exactly what's shown in
        // Player > Animation > Current. Some distinct motions share the same
        // action Name (e.g. a toast vs. drinking, or DODGE across different
        // weapons) - this lets a keyword pin an exact motion instead, when
        // the Name alone is ambiguous.
        private static string getCurrentMotionKey(Player player)
        {
            AnimationId currentAnimation = player.CurrentAnimation;
            return $"{currentAnimation.Lmt}.{currentAnimation.Id}";
        }

        //10.1
        // ActionController + 0x760 (4 bytes) の現在値。
        // モーション名 (ActionName) も motionKey も変わらないまま、上半身側の
        // 細かい状態 (回復薬を飲む=8 / しまう=9、弓のビン装填=14 / 解除=15 など)
        // だけが変化するケースを、キーワード側で "#8" のように指定して
        // 区別できるようにするためのもの。
        // 毎フレーム、プロファイル判定の直前に更新される (下の
        // orbitFaceClampEnable ブロック参照)。読めなかった場合は -1。
        private static int orbitActionSubState = -1;

        // orbitActionSubState が直前フレームから変化してからの経過秒数。
        // 値が変化した瞬間に 0 にリセットされ、以後は deltaTime を加算し続ける。
        // GazeUseSubStateTimer が ON のプロファイルで、Gaze Keyframes /
        // GazeRanges の T の代わりに使われる (getSubStateProgress() 参照)。
        private static float orbitSubStateElapsedSec = 0.0f;

        private static int getCurrentActionSubState(Player player)
        {
            nint actionControllerInstance = player.ActionController.Instance;
            if (actionControllerInstance == 0)
            {
                return -1;
            }
            return MemoryUtil.Read<int>(actionControllerInstance + 0x760);
        }
        //ver10.1ここまで

        // Exact match only (not substring) against either the full action
        // name or just the part after the last "::" - so a short keyword
        // like "VSLASH" matches an action named "WP_00::VSLASH" but does NOT
        // also match "WP_00::STRONG_VSLASH" (a substring match would). Also
        // accepts an exact match against motionKey ("{Lmt}.{Id}") for
        // disambiguating motions that share the same action Name.
        private static string actionNameSuffix(string actionName)
        {
            int idx = actionName.LastIndexOf("::", StringComparison.Ordinal);
            return idx >= 0 ? actionName[(idx + 2)..] : actionName;
        }
        //ver10
        private static bool matchesKeywordList(string? actionName, string motionKey, List<string> keywords)
        {
            string? suffix = string.IsNullOrEmpty(actionName) ? null : actionNameSuffix(actionName);
            foreach (string rawKeyword in keywords)
            {
                /* 異なる文字列でも数字が同じ場合がある
                if (keyword.Length == 0)
                {
                    continue;
                }
                */

                if (string.IsNullOrEmpty(rawKeyword))
                {
                    continue;
                }

                string keyword = rawKeyword.Trim();
                if (keyword.Length == 0)
                {
                    continue;
                }

                // 追加フォーマット: 末尾の "#<数値>" = ActionController + 0x760 の値。
                //   "#8"                     … サブステートが 8 のときだけ一致 (モーション名は不問)
                //   "Common::IDLE#8"         … アクション名が一致し、かつサブステートが 8
                //   "IDLE#8"                 … suffix 一致 + サブステート 8
                //   "WP_11::IDLE@12.156#14"  … アクション名 + motionKey + サブステート
                // これにより、モーション名も motionKey も同じまま値だけ変わる
                // 「回復薬を飲む(8)」「取り出してしまう(9)」「ビン装填(14)」
                // 「ビン解除(15)」などをプロファイル側で完全に分離できる。
                int hashIndex = keyword.LastIndexOf('#');
                if (hashIndex >= 0)
                {
                    string subStateText = keyword[(hashIndex + 1)..].Trim();
                    if (!int.TryParse(subStateText, out int requiredSubState))
                    {
                        // "#" の後ろが数値でないキーワードは無効として無視する
                        continue;
                    }
                    if (requiredSubState != orbitActionSubState)
                    {
                        continue;
                    }
                    keyword = keyword[..hashIndex].Trim();
                    if (keyword.Length == 0)
                    {
                        // "#8" のように数値だけ指定された場合はここで一致確定
                        return true;
                    }
                }

                // フォーマット: "ActionName@Lmt.Id" または "Suffix@Lmt.Id"
                // 例: "WP_00::VSLASH@12.156" または "VSLASH@12.156"
                if (keyword.Contains('@'))
                {
                    string[] parts = keyword.Split(new char[] { '@' }, 2);
                    string namePart = parts[0];
                    string keyPart = parts[1];

                    if (string.Equals(keyPart, motionKey, StringComparison.OrdinalIgnoreCase))
                    {
                        if (actionName != null &&
                            (string.Equals(namePart, actionName, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(namePart, suffix, StringComparison.OrdinalIgnoreCase)))
                        {
                            return true;
                        }
                    }
                    continue;
                }

                // 既存のマッチ: motionKey と完全一致
                if (string.Equals(keyword, motionKey, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // 既存のマッチ: actionName またはその suffix と一致
                if (actionName != null &&
                    (string.Equals(keyword, actionName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(keyword, suffix, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            return false;
        }
        //ver10ここまで

        // Lerps between two angles (degrees) taking the shortest path, so e.g.
        // lerping from 179 to -179 moves through +-180 (a 2-degree step)
        // rather than sweeping back down through 0 (a 358-degree step).
        private static float lerpAngleDeg(float a, float b, float t)
        {
            float delta = b - a;
            delta -= 360.0f * MathF.Floor((delta + 180.0f) / 360.0f);
            return a + delta * t;
        }
        // (b - a)の符号付き最短角度差(度)、[-180, 180]にラップ。ver9の
        // Keep/Catch-upステートマシンで、キャラクターが1フレームでどれだけ
        // 回転したか、および保持中のオフセットがどれだけ残っているかを測るのに使う。
        private static float deltaAngleDeg(float a, float b)
        {
            float delta = b - a;
            delta -= 360.0f * MathF.Floor((delta + 180.0f) / 360.0f);
            return delta;
        }

        // orbitClampSmoothing / orbitClampBaseSmoothing は「1フレームにつき
        // この割合だけ寄せる」という前提でQuaternion.Slerp/lerpAngleDegに
        // 直接渡されており、deltaTimeが一切考慮されていなかった。そのため
        // 120FPSでは30FPSの4倍の頻度でこの補間が走り、実時間では目標に
        // 追いつくのが大幅に速くなる。これがWritePadInputHookの
        // Decouple Movement From Look補正の参照先(orbitCachedStableForwardYawDeg)
        // を経由して、「フレームレートが高いほど少ないスティック傾きで
        // 最大旋回に達する」という体感差として現れていた。
        // deltaTimeSeconds == 1 / orbitClampReferenceFps のときは alpha を
        // そのまま返すので、現在の0.3/0.5がしっくり来ているフレームレートの
        // 挙動はそのまま維持され、それ以外のフレームレートがそこに合わせて
        // スケールされる。
        private float orbitClampReferenceFps = 0.4f;// 左スティックの旋回感度
        private float frameRateIndependentAlpha(float alpha, float deltaTimeSeconds)
        {
            alpha = Math.Clamp(alpha, 0.0f, 1.0f);
            if (alpha <= 0.0f) return 0.0f;
            if (alpha >= 1.0f) return 1.0f;
            float dt = MathF.Max(deltaTimeSeconds, 0.0f);
            return 1.0f - MathF.Pow(1.0f - alpha, dt * orbitClampReferenceFps);
        }
        // Extracts yaw/pitch directly from a forward direction (not a full
        // quaternion) - always well-defined for any non-degenerate forward,
        // unlike a combined 3-axis Euler decomposition which can become
        // unstable (gimbal lock) when pitch nears +-90 degrees.
        private static void extractYawPitchDegFromForward(Vector3 forward, out float yawDeg, out float pitchDeg)
        {
            forward = Vector3.Normalize(forward);
            float horizontalLen = MathF.Sqrt(forward.X * forward.X + forward.Z * forward.Z);
            yawDeg = Single.RadiansToDegrees(MathF.Atan2(forward.X, forward.Z));
            pitchDeg = Single.RadiansToDegrees(MathF.Atan2(-forward.Y, horizontalLen));
        }

        // Roll is measured as the twist around "relative"'s own forward axis,
        // relative to what "up" would be with zero roll at the given
        // yaw/pitch (usually the *raw*, un-clamped yaw/pitch from
        // extractYawPitchDegFromForward above). This is independent of the
        // yaw/pitch gimbal entirely, since it never shares an axis with them.
        private static float extractRollDegAroundForward(Quaternion relative, float yawDeg, float pitchDeg)
        {
            Quaternion yawPitchOnly = Quaternion.CreateFromYawPitchRoll(
                Single.DegreesToRadians(yawDeg), Single.DegreesToRadians(pitchDeg), 0.0f);
            Vector3 expectedForward = Vector3.Transform(new Vector3(0.0f, 0.0f, 1.0f), yawPitchOnly);
            Vector3 expectedUp = Vector3.Transform(new Vector3(0.0f, 1.0f, 0.0f), yawPitchOnly);
            Vector3 actualUp = Vector3.Transform(new Vector3(0.0f, 1.0f, 0.0f), relative);

            Vector3 actualUpProjected = actualUp - expectedForward * Vector3.Dot(actualUp, expectedForward);
            if (actualUpProjected.LengthSquared() < 1e-8f)
            {
                return 0.0f;
            }
            actualUpProjected = Vector3.Normalize(actualUpProjected);

            float cosRoll = Math.Clamp(Vector3.Dot(expectedUp, actualUpProjected), -1.0f, 1.0f);
            float sinRoll = Vector3.Dot(Vector3.Cross(expectedUp, actualUpProjected), expectedForward);
            return Single.RadiansToDegrees(MathF.Atan2(sinRoll, cosRoll));
        }

        // Returns the index of the first OrbitProfile (any Mode) whose
        // keyword list matches, or -1 if none match (which classifies the
        // motion as "A"/Full Rotation by default). Checked top-to-bottom so
        // an earlier, more specific profile (e.g. a named list of spin
        // attacks) can take priority over a later, broader one (e.g.
        // IDLE/WALK/RUN).
        private int findMatchingProfileIndex(string? actionName, string motionKey)
        {
            for (int profileIndex = 0; profileIndex < orbitProfiles.Count; profileIndex++)
            {
                if (matchesKeywordList(actionName, motionKey, orbitProfiles[profileIndex].Keywords))
                {
                    return profileIndex;
                }
            }
            return -1;
        }

        // ver15: 現在マッチしているプロファイルの DisableFreeCameraWhileActive
        // に応じて Enable Free Camera を強制オフ/復元する。
        // updateFreeCamera() 内の通常のプロファイル判定 (orbitLastProfileIndex
        // まわり) は Free Camera が有効な間しか呼ばれない
        // (OnUpdate() 内の "if (freeCamera) { updateFreeCamera(...); }" 参照)
        // ため、そちらに置くと「強制オフにした瞬間、以後モーションの変化を
        // 一切拾えなくなり、二度と元に戻らない」という不具合になる。
        // そのため、この判定だけは OnUpdate() から Free Camera の有効/無効に
        // 関わらず毎フレーム呼び出す。
        private void updateForcedFreeCameraDisableByProfile(Player? player)
        {
            bool wantsFreeCameraForcedOff = false;
            if (orbitFaceClampEnable && player != null)
            {
                // "#<数値>" キーワード(例: "#8")はモーション名を一切見ず、
                // orbitActionSubState の値だけで即マッチする仕様。この値は
                // 本来 updateFreeCamera() 内でのみ毎フレーム更新されるが、
                // それは Free Camera が有効な間しか呼ばれないため、無効化中は
                // 更新されないまま古い値に固定されてしまう。放置すると、
                // 無効化中に無関係な "#8"(飲料) 等のプロファイルへ誤ってマッチ
                // し続ける可能性があるため、ここでも毎フレーム最新値に更新する。
                orbitActionSubState = getCurrentActionSubState(player);

                string? currentActionName = getCurrentActionName(player);
                string currentMotionKey = getCurrentMotionKey(player);
                int profileIndex = findMatchingProfileIndex(currentActionName, currentMotionKey);
                wantsFreeCameraForcedOff = profileIndex >= 0
                    && profileIndex < orbitProfiles.Count
                    && orbitProfiles[profileIndex].DisableFreeCameraWhileActive;
            }

            if (wantsFreeCameraForcedOff && !orbitFreeCameraForcedOffByProfile)
            {
                // 強制オフを開始する瞬間: 現在のEnable Free Camera値を
                // 「ユーザー本来の希望値」として退避してからオフにする。
                orbitFreeCameraUserWanted = enableFreeCamera;
                orbitFreeCameraForcedOffByProfile = true;

                if (enableFreeCamera)
                {
                    // enableFreeCamera を false にすると disableFreeCamera() が
                    // 呼ばれ、Unlock Input・FOV までリセットされる(F7の裏側と
                    // 同じ処理)。F7の手動トグルと同様に、オフにする直前の
                    // Unlock Input / FOV をここで退避しておく。
                    orbitFreeCameraSavedUnlockInput = unlockInputToggled;
                    orbitFreeCameraSavedFov = 90.0f;//cameraFov
                    orbitFreeCameraHasSavedFov = true;
                }

                enableFreeCamera = false;
            }
            else if (!wantsFreeCameraForcedOff && orbitFreeCameraForcedOffByProfile)
            {
                // 強制オフが終わった瞬間: 退避しておいたユーザー本来の
                // 希望値に復元する。
                orbitFreeCameraForcedOffByProfile = false;
                enableFreeCamera = orbitFreeCameraUserWanted;

                if (enableFreeCamera)
                {
                    // F7の手動トグルと同様に、退避しておいたUnlock Input / FOV
                    // を復元する。これを行わないと、強制オフ中に
                    // disableFreeCamera() がリセットしたUnlock Inputが
                    // オフのまま戻ってしまう。
                    if (orbitFreeCameraHasSavedFov)
                    {
                        unlockInputToggled = orbitFreeCameraSavedUnlockInput;
                        cameraFov = orbitFreeCameraSavedFov;
                        orbitFreeCameraHasSavedFov = false;
                    }
                    else
                    {
                        unlockInputToggled = true;
                    }
                }
            }
        }

        private static void extractYawPitchRollDeg(Quaternion q, out float yawDeg, out float pitchDeg, out float rollDeg)
        {
            float x = q.X, y = q.Y, z = q.Z, w = q.W;

            float sinPitch = 2.0f * (w * x - y * z);
            sinPitch = Math.Clamp(sinPitch, -1.0f, 1.0f);
            float pitch = MathF.Asin(sinPitch);

            float yawSin = 2.0f * (w * y + z * x);
            float yawCos = 1.0f - 2.0f * (x * x + y * y);
            float yaw = MathF.Atan2(yawSin, yawCos);

            float rollSin = 2.0f * (w * z + x * y);
            float rollCos = 1.0f - 2.0f * (y * y + z * z);
            float roll = MathF.Atan2(rollSin, rollCos);

            yawDeg = Single.RadiansToDegrees(yaw);
            pitchDeg = Single.RadiansToDegrees(pitch);
            rollDeg = Single.RadiansToDegrees(roll);
        }

        // For a single axis: below "range" degrees of deviation, returns 0 (stay
        // locked to the base joint on this axis). Beyond that, smoothly blends
        // toward the raw deviation over "blendWidth" degrees, capping the amount
        // actually followed at "maxFollow" degrees so continuous multi-rotation
        // spins (e.g. hammer spin attacks) don't spin the camera indefinitely -
        // the followed angle saturates instead, similar to how a real person
        // "spots" during a spin rather than rotating their head at the same
        // continuous rate as their body.
        // Ballet-"spotting"-style follow, used for C (attack motions) in the
        // Simple Lock clamp: within `range` (a comfortable eye-reach), pinned
        // to true forward (0 deviation) - same low end as blendAxisDeg. But
        // beyond `range`, this tracks the raw deviation *exactly*, with no
        // ceiling - unlike blendAxisDeg's `maxFollow` cap, which makes the
        // camera visibly stop following partway through a large rotation
        // (e.g. a full-body horizontal spin attack) once deviation exceeds
        // maxFollow. This instead keeps following all the way through, then
        // naturally settles back at forward once the motion's own deviation
        // shrinks back under `range` again.
        private static float spotAxisDeg(float deviationDeg, float range, float blendWidth)
        {
            float absDeviation = MathF.Abs(deviationDeg);
            float blendStart = MathF.Max(range, 0.0f);
            float blendEnd = blendStart + MathF.Max(blendWidth, 0.0001f);
            float t = Math.Clamp((absDeviation - blendStart) / (blendEnd - blendStart), 0.0f, 1.0f);
            t = t * t * (3.0f - 2.0f * t); // smoothstep
            return MathF.Sign(deviationDeg) * absDeviation * t;
        }

        // Selects which Target Y/Right/Forward triple (the eye-position
        // offset applied in the main orbit-camera update, see its call
        // site) applies to the current frame. Every OrbitProfile - whatever
        // its Mode - carries its own independent Target Y/Right/Forward now,
        // so this simply uses whichever profile findMatchingProfileIndex()
        // matched this frame (orbitLastProfileIndex), falling back to the
        // top-level "default" values (orbitY/orbitRight/orbitForward) only
        // when nothing matched at all (or Face Clamp is off, so
        // classification didn't run).
        private (float y, float right, float forward) getActiveOrbitTargetOffset()
        {
            if (!orbitFaceClampEnable)
            {
                return (orbitY, orbitRight, orbitForward);
            }
            if (orbitLastProfileIndex >= 0 && orbitLastProfileIndex < orbitProfiles.Count)
            {
                OrbitProfile activeProfile = orbitProfiles[orbitLastProfileIndex];
                return (activeProfile.TargetY, activeProfile.TargetRight, activeProfile.TargetForward);
            }
            // No profile matched this motion - default "A" behavior.
            return (orbitY, orbitRight, orbitForward);
        }

        // Same idea as getActiveOrbitTargetOffset() above, but for this
        // profile's own Base Smoothing (low-pass filter on clampBaseRotation
        // - see its use at the clamp block). Falls back to the top-level
        // default (orbitClampBaseSmoothing) only when no profile matched.
        private float getActiveClampBaseSmoothing()
        {
            if (orbitFaceClampEnable && orbitLastProfileIndex >= 0 && orbitLastProfileIndex < orbitProfiles.Count)
            {
                return orbitProfiles[orbitLastProfileIndex].ClampBaseSmoothing;
            }
            return orbitClampBaseSmoothing;
        }

        private bool getActiveEnableTransitionBlend()
        {
            if (orbitFaceClampEnable && orbitLastProfileIndex >= 0 && orbitLastProfileIndex < orbitProfiles.Count)
            {
                return orbitProfiles[orbitLastProfileIndex].EnableTransitionBlend;
            }
            return orbitDefaultEnableTransitionBlend;
        }

        // This profile's own Profile Switch Blend Time - only actually read
        // once, at the instant a switch INTO this profile is detected (see
        // the classification block below), and cached into
        // orbitCurrentTransitionDuration for the duration of that one blend.
        private float getActiveProfileTransitionDuration()
        {
            if (orbitFaceClampEnable && orbitLastProfileIndex >= 0 && orbitLastProfileIndex < orbitProfiles.Count)
            {
                return orbitProfiles[orbitLastProfileIndex].ProfileTransitionDuration;
            }
            return orbitProfileTransitionDuration;
        }

        // Selects which baseline Yaw/Pitch/Roll (what "0 deviation" points
        // toward) applies to the current frame - same profile selection as
        // getActiveOrbitTargetOffset() above, just for the angular baseline
        // instead of the position offset. Read at the clampBaseRotation
        // correction-multiply site.
        private (float yaw, float pitch, float roll) getActiveOrbitBaseCorrection()
        {
            if (!orbitFaceClampEnable)
            {
                return (orbitClampBaseCorrectionYaw, orbitClampBaseCorrectionPitch, orbitClampBaseCorrectionRoll);
            }
            if (orbitLastProfileIndex >= 0 && orbitLastProfileIndex < orbitProfiles.Count)
            {
                OrbitProfile activeProfile = orbitProfiles[orbitLastProfileIndex];
                return (activeProfile.CorrectionYaw, activeProfile.CorrectionPitch, activeProfile.CorrectionRoll);
            }
            return (orbitClampBaseCorrectionYaw, orbitClampBaseCorrectionPitch, orbitClampBaseCorrectionRoll);
        }

        // Rx/Ry (right-stick input) multiplier for the currently-active
        // Normal ("C") profile, so a motion's own look sensitivity (e.g.
        // AIM_IDLE, to match the base game's own reticle speed) can differ
        // from ordinary free-look. Deliberately still restricted to Normal-
        // mode profiles specifically (not Base-Only/Base-Only-Ignore-X/Full
        // Rotation) - this multiplier only makes sense alongside the
        // clamped/spotted follow math those other modes don't run. Uses
        // *last* frame's classification (orbitLastProfileIndex is only
        // refreshed later this same frame, in the SimpleLock face-clamp
        // block below) - one frame of lag, same tolerance already relied on
        // elsewhere (e.g. the movement/look decoupling correction above).
        // 1.0 (no change) outside of a Normal profile, or if that profile
        // doesn't override it.
        private float getActiveLookSensitivityMultiplier()
        {
            if (orbitFaceClampEnable && !orbitLastIsBaseOnly && !orbitLastIsFullRotation
                && orbitLastProfileIndex >= 0 && orbitLastProfileIndex < orbitProfiles.Count
                && orbitProfiles[orbitLastProfileIndex].Mode == OrbitProfileMode.Normal)
            {
                return orbitProfiles[orbitLastProfileIndex].LookSensitivityMultiplier;
            }
            return 1.0f;
        }

        private static float blendAxisDeg(float deviationDeg, float range, float blendWidth, float maxFollow)
        {
            float absDeviation = MathF.Abs(deviationDeg);
            float blendStart = MathF.Max(range, 0.0f);
            float blendEnd = blendStart + MathF.Max(blendWidth, 0.0001f);
            float t = Math.Clamp((absDeviation - blendStart) / (blendEnd - blendStart), 0.0f, 1.0f);
            t = t * t * (3.0f - 2.0f * t); // smoothstep
            float followedMagnitude = MathF.Min(absDeviation, MathF.Max(maxFollow, 0.0f)) * t;
            return MathF.Sign(deviationDeg) * followedMagnitude;
        }

        // How far the current motion has played, 0.0 (just started) to 1.0
        // (about to end/loop). This is the same value shown by the "Frame"
        // slider in the Animation debug panel (CurrentFrame/MaxFrame) - it
        // comes from the animation's own playback position, not from any
        // joint's rotation, so it carries none of the per-frame skeletal
        // noise that makes the raw deviation signal jittery. Also unaffected
        // by hitstop/timescale changes, since it's frame-based, not
        // real-time-based.

        //10.1
        private static float getMotionProgress(Player player)
        {
            AnimationLayerComponent? layer = player.AnimationLayer;
            if (layer == null || layer.MaxFrame <= 0.0001f)
            {
                return 0.0f;
            }
            return Math.Clamp(layer.CurrentFrame / layer.MaxFrame, 0.0f, 1.0f);
        }

        // getMotionProgress() の代替。ベースモーションのフレーム進行ではなく、
        // orbitActionSubState (ActionController+0x760) が最後に変化してからの
        // 経過秒数を assumedDurationSec で正規化して 0.0-1.0 の T として返す。
        // ActionName/motionKey が変わらないまま上半身側の状態だけが変わる
        // 場面 (#<数値> でマッチさせるプロファイル) で、Gaze Keyframes /
        // GazeRanges の T をそのサブステート自体の経過に同期させたいときに使う。
        private static float getSubStateProgress(float assumedDurationSec)
        {
            float duration = MathF.Max(assumedDurationSec, 0.01f);
            return Math.Clamp(orbitSubStateElapsedSec / duration, 0.0f, 1.0f);
        }
        //10.1ここまで

        // Ballet-choreography-style alternative to spotAxisDeg(): instead of
        // following the live (and possibly noisy/violent) joint deviation,
        // look up a Yaw/Pitch/Roll target from a short, hand-authored list
        // of (motion progress, angle) keyframes and Slerp between the two
        // surrounding ones, eased with smoothstep. Before the first
        // keyframe's T and after the last one's T, the endpoint value is
        // held. Composed as a single Quaternion (not three independently
        // lerped floats) so a keyframe pair with both Yaw and Pitch set
        // sweeps through a single diagonal arc instead of a Yaw-then-Pitch
        // "dog-leg", and so Slerp can pick the shorter rotational path.
        private static Quaternion evaluateGazeKeyframes(List<OrbitGazeKeyframe> keyframes, float t)
        {
            if (keyframes == null || keyframes.Count == 0)
            {
                return Quaternion.Identity;
            }
            if (keyframes.Count == 1 || t <= keyframes[0].T)
            {
                OrbitGazeKeyframe only = keyframes[0];
                return Quaternion.CreateFromYawPitchRoll(
                    Single.DegreesToRadians(only.Yaw),
                    Single.DegreesToRadians(only.Pitch),
                    Single.DegreesToRadians(only.Roll));
            }
            for (int keyframeIndex = 0; keyframeIndex < keyframes.Count - 1; keyframeIndex++)
            {
                OrbitGazeKeyframe segmentStart = keyframes[keyframeIndex];
                OrbitGazeKeyframe segmentEnd = keyframes[keyframeIndex + 1];
                if (t <= segmentEnd.T)
                {
                    float span = MathF.Max(segmentEnd.T - segmentStart.T, 0.0001f);
                    float local = Math.Clamp((t - segmentStart.T) / span, 0.0f, 1.0f);
                    local = local * local * (3.0f - 2.0f * local); // smoothstep
                    Quaternion segmentStartRotation = Quaternion.CreateFromYawPitchRoll(
                        Single.DegreesToRadians(segmentStart.Yaw),
                        Single.DegreesToRadians(segmentStart.Pitch),
                        Single.DegreesToRadians(segmentStart.Roll));
                    Quaternion segmentEndRotation = Quaternion.CreateFromYawPitchRoll(
                        Single.DegreesToRadians(segmentEnd.Yaw),
                        Single.DegreesToRadians(segmentEnd.Pitch),
                        Single.DegreesToRadians(segmentEnd.Roll));
                    return Quaternion.Slerp(segmentStartRotation, segmentEndRotation, local);
                }
            }

            //ver11
            OrbitGazeKeyframe last = keyframes[^1];
            return Quaternion.CreateFromYawPitchRoll(
                Single.DegreesToRadians(last.Yaw),
                Single.DegreesToRadians(last.Pitch),
                Single.DegreesToRadians(last.Roll));
        }

        // 現在のモーション進行度 t において、Gaze Keyframes をどれだけの割合で
        // 使うかを 0..1 で返す。窓の中なら 1 (Gaze のみ)、窓から Blend 以上
        // 離れていれば 0 (通常の Range/Blend 追従のみ)、その間は smoothstep で
        // なめらかに橋渡しする。窓が複数ある場合はいちばん強いものを採用する。
        // 窓がひとつも登録されていないときは 1 (＝制限なし＝従来どおり
        // モーション全体で Gaze を使う) を返すので、チェックを入れただけで
        // 何も設定していない状態でも挙動が壊れない。
        private static float evaluateGazeRangeWeight(List<OrbitGazeRange> ranges, float t)
        {
            if (ranges == null || ranges.Count == 0)
            {
                return 1.0f;
            }
            float bestWeight = 0.0f;
            foreach (OrbitGazeRange range in ranges)
            {
                float rangeStart = MathF.Min(range.Start, range.End);
                float rangeEnd = MathF.Max(range.Start, range.End);
                float rangeBlend = MathF.Max(range.Blend, 0.0f);

                float weight;
                if (t >= rangeStart && t <= rangeEnd)
                {
                    weight = 1.0f;
                }
                else if (rangeBlend <= 0.0001f)
                {
                    weight = 0.0f;
                }
                else if (t < rangeStart)
                {
                    weight = Math.Clamp((t - (rangeStart - rangeBlend)) / rangeBlend, 0.0f, 1.0f);
                }
                else
                {
                    weight = Math.Clamp(((rangeEnd + rangeBlend) - t) / rangeBlend, 0.0f, 1.0f);
                }
                weight = weight * weight * (3.0f - 2.0f * weight); // smoothstep

                if (weight > bestWeight)
                {
                    bestWeight = weight;
                }
            }
            return bestWeight;
        }

        // evaluateGazeKeyframes() のXYZ版。Quaternion Slerpの代わりに、
        // Y/Right/Forward をそれぞれ独立に線形補間する (回転と違って軸ごとの
        // 補間で問題ない)。イージングは同じ smoothstep。
        private static (float y, float right, float forward) evaluateGazePositionKeyframes(List<OrbitGazePositionKeyframe> keyframes, float t)
        {
            if (keyframes == null || keyframes.Count == 0)
            {
                return (0.0f, 0.0f, 0.0f);
            }
            if (keyframes.Count == 1 || t <= keyframes[0].T)
            {
                OrbitGazePositionKeyframe only = keyframes[0];
                return (only.Y, only.Right, only.Forward);
            }
            for (int keyframeIndex = 0; keyframeIndex < keyframes.Count - 1; keyframeIndex++)
            {
                OrbitGazePositionKeyframe segmentStart = keyframes[keyframeIndex];
                OrbitGazePositionKeyframe segmentEnd = keyframes[keyframeIndex + 1];
                if (t <= segmentEnd.T)
                {
                    float span = MathF.Max(segmentEnd.T - segmentStart.T, 0.0001f);
                    float local = Math.Clamp((t - segmentStart.T) / span, 0.0f, 1.0f);
                    local = local * local * (3.0f - 2.0f * local); // smoothstep
                    float y = segmentStart.Y + (segmentEnd.Y - segmentStart.Y) * local;
                    float rightOffset = segmentStart.Right + (segmentEnd.Right - segmentStart.Right) * local;
                    float forwardOffset = segmentStart.Forward + (segmentEnd.Forward - segmentStart.Forward) * local;
                    return (y, rightOffset, forwardOffset);
                }
            }
            OrbitGazePositionKeyframe last = keyframes[^1];
            return (last.Y, last.Right, last.Forward);
        }
        //ver11ここまで

        private void checkCameraAnimState()
        {
            if (pCamera != null)
            {
                previousCameraAnimState = MemoryUtil.Read<int>(pCamera.Instance + 0x240);
                if (previousCameraAnimState == 5)
                {
                    ActionInfo currentActionInfo = lastPlayer!.ActionController.CurrentAction;
                    // 1:314 = Wingdrake landing on area enter.
                    // 1:319 = Disoriented wingdrake landing next to monster.
                    if (currentActionInfo.ActionSet == 1 && (currentActionInfo.ActionId == 314 || currentActionInfo.ActionId == 319))
                    {
                        // Temporarily disable evaluation of cameraAnimState != 5.
                        ignoreAnimState = true;
                    }
                }
                else if (ignoreAnimState)
                {
                    // Once cameraAnimState is no longer 5, resume default logic.
                    ignoreAnimState = false;
                }
            }
            else
            {
                previousCameraAnimState = 0;
            }
        }

        private void resetStatePerFrame()
        {
            primaryPad = 0x0;

            // Zero pad input in case no controllers are connected.
            PadLx = 0; PadLy = 0;
            PadRx = 0; PadRy = 0;

            cameraOffset = Vector3.Zero;
        }

        // Cのモーション中のカメラ挙動をログに出力するための設定
        private bool enableCMotionLogging = false; // 必要に応じて false にして無効化
        private string cMotionLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NewCamera", "C_motion_log.csv");

        // ここにログしたい「特定のモーション」を入れる。
        // 形式:
        //  - 完全な ActionName（例: "WP_00::VSLASH"）
        //  - suffix（例: "VSLASH"）
        //  - motionKey（例: "12.156" または "12.156" と一致するように "12.156" を入れる）
        //  - またはアクションと motionKey を組合せた "WP_00::VSLASH@12.156"（getCurrentMotionKey と併用して厳密マッチ可能）
        private List<string> cMotionLogKeywords = new List<string>
        {
            // 例：以下を実際にログしたいモーションに置き換えてください
            // "VSLASH",
            // "WP_00::VSLASH@12.156",
            // "SPIN_ATTACK3"
            //"USLASH"
        };
        private void logCMotionIfMatched(Player? player)
        {
            if (!enableCMotionLogging || player == null)
            {
                return;
            }

            string? actionName = getCurrentActionName(player);
            string motionKey = getCurrentMotionKey(player);

            int profileIndex = findMatchingProfileIndex(actionName, motionKey);
            if (profileIndex < 0 || orbitProfiles[profileIndex].Mode != OrbitProfileMode.Normal)
            {
                return;
            }

            if (cMotionLogKeywords != null && cMotionLogKeywords.Count > 0)
            {
                if (!matchesKeywordList(actionName, motionKey, cMotionLogKeywords))
                {
                    return;
                }
            }

            // 1) Deviation (Y/P/R) の取得
            // これが GUI の DEBUG の Deviation に対応する値です
            float devYaw = orbitClampSmoothedYaw;
            float devPitch = orbitClampSmoothedPitch;
            float devRoll = orbitClampSmoothedRoll;

            // 2) カメラ由来の回転からの YPR（GUI と一致させたい値）
            float camYaw = 0f, camPitch = 0f, camRoll = 0f;
            if (vCamera != null)
            {
                Vector3 forwardVec = vCamera.Target - vCamera.Position;
                if (forwardVec.LengthSquared() > 1e-8f)
                {
                    forwardVec = Vector3.Normalize(forwardVec);
                    Quaternion camRot = lookRotation(forwardVec, vCamera.Up);
                    extractYawPitchRollDeg(camRot, out camYaw, out camPitch, out camRoll);
                }
            }

            // CSVの1行を作成（末尾に \n を忘れないように追加）
            // 形式: 日時, アクション名, モーションキー, プロファイルインデックス, devYaw, devPitch, devRoll, camYaw, camPitch, camRoll
            string line = $"{DateTime.Now:O},{actionName ?? ""},{motionKey},{profileIndex}," +
                          $"{devYaw:F3},{devPitch:F3},{devRoll:F3}," +
                          $"{camYaw:F3},{camPitch:F3},{camRoll:F3}\n";

            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NewCamera");
                Directory.CreateDirectory(dir);
                string full = Path.Combine(dir, "C_motion_log.csv");
                File.AppendAllText(full, line);
            }
            catch (Exception ex)
            {
            }
        }

        public void OnUpdate(float deltaTime)
        {
            if (disableMod)
            {
                return;
            }

#if HOOK_ORDER_ASSERTS
            Assert(hookOrder == 0 || hookOrder == 4);
            hookOrder = 0;
            if (frameTick == int.MaxValue) frameTick = 0;
            else frameTick++;
            //debugLog($"OnUpdate() @ {frameTick}");
#endif

#if MOUSE_AND_KEYBOARD_LAYER
            if (keyboardEnabled)
            {
                if (Input.IsPressed(Key.NumPad0))
                {
                    enableFreeCamera = !enableFreeCamera;
                }

                // 追加: F7 で Enable Free Camera を切り替え、
                // オフにする時は Unlock Input と FOV を保存、再びオンにする時は復元する
                if (Input.IsPressed(Key.F7))
                {
                    if (enableFreeCamera)
                    {
                        // オフにする直前の状態を保存
                        savedUnlockInputOnDisable = unlockInputToggled;
                        savedEnableFreeCameraCameraFov = 90.0f;//cameraFov
                        savedEnableFreeCameraHasSavedFov = true;
                        enableFreeCamera = false;
                    }
                    else
                    {
                        // 再度オンにする -> 保存値があれば復元、なければ Unlock Input をオンにする
                        enableFreeCamera = true;
                        if (savedEnableFreeCameraHasSavedFov)
                        {
                            unlockInputToggled = savedUnlockInputOnDisable;
                            cameraFov = savedEnableFreeCameraCameraFov;
                            savedEnableFreeCameraHasSavedFov = false;
                        }
                        else
                        {
                            unlockInputToggled = true;
                        }
                    }
                }
                //ここまで

                if (Input.IsPressed(Key.NumPadPeriod))
                {
                    toggleUi();
                }
                if (Input.IsPressed(Key.NumPadSlash))
                {
                    if (disableNearDofOverride != 0)
                    {
                        TuningToolInterop.RemoveOverride(disableNearDofOverride);
                        disableNearDofOverride = 0;
                    }
                    else
                    {
                        disableNearDofOverride = TuningToolInterop.AddOverride("Near Enable", new Vector4(), 0);
                    }
                }
                ref float gameSpeedRef = ref MemoryUtil.GetRef<float>(sMain.Instance + 0xA4);
                if (Input.IsPressed(Key.F9))
                {
                    f9PressStartTime = Environment.TickCount64;
                    f9HoldTriggered = false;
                }
                if (Input.IsDown(Key.F9))
                {
                    // 現実時間で400ミリ秒以上長押しされたら即座に10倍速へ
                    if (!f9HoldTriggered && f9PressStartTime > 0 && (Environment.TickCount64 - f9PressStartTime >= F9_LONG_PRESS_MS))
                    {
                        f9IsFastSpeed = true;
                        slowMotionActive = false;
                        gameSpeedRef = 10.0f;
                        f9HoldTriggered = true;
                    }
                }
                if (Input.IsReleased(Key.F9))
                {
                    // 長押しトリガーが引かれずに離された場合（＝短押し）
                    if (!f9HoldTriggered)
                    {
                        if (f9IsFastSpeed)
                        {
                            // 10倍速のとき短押し -> 0.1倍(スロー)へ
                            f9IsFastSpeed = false;
                            slowMotionActive = true;
                            gameSpeedRef = slowMotionSpeed;
                        }
                        else
                        {
                            // 通常のトグル（0.1倍 ⇔ 1.0倍）
                            slowMotionActive = !slowMotionActive;
                            gameSpeedRef = slowMotionActive ? slowMotionSpeed : 1.0f;
                        }
                    }
                    f9PressStartTime = 0;
                    f9HoldTriggered = false;
                }
            }
#endif

            Player? player = null;

            if (freeCameraFallback)
            {
                player = checkPlayerChange();
                checkCurrentVisibleCamera(player); // Ensure vCamera is valid.
                if (vCamera != null)
                {
                    if (!freeCamera)
                    {
                        Quaternion forward = Quaternion.Normalize(getForward(vCamera.Position, vCamera.Target));
                        cameraYaw = Single.RadiansToDegrees(MathF.Atan2(forward.Z, forward.X));
                        cameraPitch = Single.RadiansToDegrees(MathF.Asin(forward.Y));
                        if (enableFreeCamera)
                        {
                            setupFreeCamera(vCamera, vCameraViewportIndex);
                        }
                    }
                    if (freeCamera)
                    {
                        updateFreeCamera(vCamera);
                        setCameraRoll(vCamera);
                        if (!enableFreeCamera)
                        {
                            disableFreeCamera();
                        }
                    }
                }
            }
            freeCameraFallback = true;
            freeCameraNoMenu = false;

            ref float gameSpeed = ref MemoryUtil.GetRef<float>(sMain.Instance + 0xA4);
            if (freezeGame && gameSpeed != 0.0f)
            {
                gameSpeed = 0.0f;
            }
            else if (freezeGame && advanceFrame > 0)
            {
                gameSpeed = 1.0f;
                advanceFrame--;
            }

            player = getPlayerWithFallback();

            logCMotionIfMatched(player);// OnUpdate 内で player = getPlayerWithFallback(); の直後に呼び出す。Cのモーション中のカメラ挙動をログに出力するための設定

            // ver15: Free Camera が無効(=updateFreeCamera()が呼ばれない)間も
            // モーションの変化を検知できるよう、ここで毎フレーム独立して
            // 呼び出す。理由は updateForcedFreeCameraDisableByProfile() 側の
            // コメント参照。
            updateForcedFreeCameraDisableByProfile(player);

            if (player != null)
            {
                // @TODO: Comment this.
                if (hideWeapon)
                {
                    nint baseAddr = MemoryUtil.Read<nint>(player.Instance + 0x76B0);
                    if (baseAddr != 0x0)
                    {
                        MemoryUtil.WriteBytes(baseAddr + 0x1A98, [0x1]);
                        int weaponParts = MemoryUtil.Read<int>(baseAddr + 0x2250);
                        for (int i = 0; i < weaponParts; i++)
                        {
                            nint partsAddr = MemoryUtil.Read<nint>(baseAddr + ((i + 0x16B) * 24));
                            if (partsAddr != 0x0)
                            {
                                MemoryUtil.WriteBytes(partsAddr + 0x1A98, [0x1]);
                            }
                        }
                    }
                }
                /*
                if (hideKnife)
                {
                    // Emulate what Carving and Flourish emote do at
                    // MonsterHunterWorld.exe+1F68832 - bts eax,06
                    // if set, skips
                    // MonsterHunterWorld.exe+203A0D2 - bts ecx,01
                    MemoryUtil.GetRef<int>(player.Instance + 0x8938) |= (1 << 6);
                    MemoryUtil.GetRef<int>(player.Instance + 0x8968) |= (1 << 6);
                }
                */
            }

            resetStatePerFrame();
        }

        private static Quaternion getForward(Vector3 pos, Vector3 target)
        {
            return new Quaternion(target.X - pos.X, target.Y - pos.Y, target.Z - pos.Z, 0.0f);
        }

        private static Quaternion getRight(Quaternion q)
        {
            return q * Quaternion.CreateFromYawPitchRoll(MathF.PI, 0.0f, 0.0f);
        }

        private void setCameraRoll(Camera camera)
        {
            // Face-locked orbit camera (Simple Lock / Use Positional Face Basis):
            // Up comes from the exact same rotation that produced the look-at
            // Target direction (see updateFreeCamera), instead of the
            // world-relative cameraRoll math below. This is what keeps a
            // front-flip a clean pitch instead of a self-correcting roll.
            if (orbitFaceLockedUpActive && camera == vCamera)
            {
                camera.Up = orbitFaceLockedUpSmoothed;
                return;
            }

            // Don't test our luck with precision weirdness if we know we can be exact.
            if (cameraRoll == 0.0f)
            {
                camera.Up = new Vector3(0.0f, 1.0f, 0.0f);
            }
            else if (cameraRoll == 180.0f)
            {
                camera.Up = new Vector3(0.0f, -1.0f, 0.0f);
            }
            else
            {
                // yaw + 90.0 = point right.
                camera.Up.X = MathF.Sin(Single.DegreesToRadians(cameraRoll)) * MathF.Cos(Single.DegreesToRadians(cameraPitch)) * MathF.Cos(Single.DegreesToRadians(cameraYaw + 90.0f));
                camera.Up.Y = MathF.Cos(Single.DegreesToRadians(cameraRoll));
                camera.Up.Z = MathF.Sin(Single.DegreesToRadians(cameraRoll)) * MathF.Cos(Single.DegreesToRadians(cameraPitch)) * MathF.Sin(Single.DegreesToRadians(cameraYaw + 90.0f));
            }
        }

        private bool guessAltNearClipSet(Camera camera)
        {
            // Try to guard against persisting an adjusted near clip on the player camera after a free camera target change.
            return (camera.NearClip == alternateNearClip) || (camera == pCamera && camera.NearClip != DEFAULT_NEAR_CLIP);
        }

        private void setNearClip(Camera camera, bool alt)
        {
            if (alt)
            {
                if (restoreNearClip == null)
                {
                    restoreNearClip = camera.NearClip;
                }
                camera.NearClip = alternateNearClip;
            }
            else
            {
                if (restoreNearClip == null)
                {
                    camera.NearClip = DEFAULT_NEAR_CLIP;
                }
                else
                {
                    if (camera == pCamera && restoreNearClip != DEFAULT_NEAR_CLIP)
                    {
                        restoreNearClip = DEFAULT_NEAR_CLIP;
                    }
                    camera.NearClip = (float)restoreNearClip;
                    restoreNearClip = null;
                }
            }
        }

        private void setPerspective(Camera camera)
        {
            Quaternion forward = getForward(camera.Position, camera.Target);
            Quaternion right = getRight(forward);
            forward = Quaternion.Normalize(forward);
            right = Quaternion.Normalize(right);
            Vector3 pos = camera.Position;
            forward *= cameraForward;
            cameraOffset.X = forward.X;
            cameraOffset.Y = forward.Y;
            cameraOffset.Z = forward.Z;
            cameraOffset.X += right.X * cameraRight;
            cameraOffset.Z += right.Z * cameraRight;
            pos += cameraOffset;
            camera.Position = pos;
            if (cameraFov != DEFAULT_FOV)
            {
                camera.FieldOfView = Math.Clamp((cameraFov * previousFov) / DEFAULT_FOV, 1.0f, 179.0f);
            }
        }

        private void setTentBasePos(Vector3 pos, Vector3 target)
        {
            // @TODO: This could be picked out of the code.
            nint baseAddr = MemoryUtil.Read<nint>(0x145011F58);     // Default:
            MemoryUtil.GetRef<Vector3>(baseAddr + 0x3E20) = pos;    // 0.0, -19850.0, 270.0
            MemoryUtil.GetRef<Vector3>(baseAddr + 0x3E30) = target; // 0.0, -19830.0, 0.0
        }

        private void setupFreeCamera(Camera camera, int viewportIndex)
        {
            freeCamera = true;
            cameraPosition = camera.Position;
            cameraTarget = camera.Target;
            restoreFov = cameraFov;
            restoreRoll = cameraRoll;
            restoreNearClip = camera.NearClip;
            // The value of cameraFov is effectively a new default and scales accordingly
            // based on the in-game FOV. Take the calculated FOV here because that's what is
            // actually shown, which avoids a jump when toggling free camera. This is also
            // why we don't enable free camera until after applying a offset on this update.
            if (cameraFovPreloaded)
            {
                // Use the saved FOV instead, just this once.
                cameraFovPreloaded = false;
            }
            else
            {
                cameraFov = camera.FieldOfView;
            }

            // --- 強制 FOV を 90 にする ---
            // FreeCamera を有効にしたとき、常に 90.0f を使う
            cameraFov = 90.0f;
            camera.FieldOfView = 90.0f;
            // ---------------------------------

            setDisableFading(true);
            forceMinimapFollowsCamera.Enable();
            /*
            //MemoryUtil.GetRef<nint>(psuedoViewModeObject + 0x10) = vCamera.Instance;
            Player? player = getPlayer();
            if (player != null)
            {
                MemoryUtil.GetRef<nint>(psuedoViewModeObject + 0x118) = player.Instance;
            }
            //startViewMode.Invoke(psuedoViewModeObject);
            */
        }

        private void disableFreeCamera()
        {
            freeCamera = false;
            freeCameraFromViewMode = false;
            unlockInputToggled = false;
            unlockInputForMenu = false;
            unlockInputHideMenu = false;
            if (restoreFov != null)
            {
                cameraFov = (float)restoreFov;
                restoreFov = null;
            }
            if (restoreRoll != null)
            {
                cameraRoll = (float)restoreRoll;
                restoreRoll = null;
            }
            if (restoreNearClip != null && vCamera != null)
            {
                vCamera.NearClip = (float)restoreNearClip;
                restoreNearClip = null;
            }
            cameraWrapState = 0;
            setTentBasePos(new Vector3(0.0f, -19850.0f, 270.0f), new Vector3(0.0f, -19830.0f, 0.0f));
            if (enableOffsetPerspective)
            {
                setDisableFading(disableFading);
            }
            else
            {
                setDisableFading(false);
            }
            forceMinimapFollowsCamera.Disable();
            //stopViewMode.Invoke(psuedoViewModeObject);
        }

        private float adjustedZoomSpeed(float deltaTime)
        {
            return cameraZoomSpeed * deltaTime * cameraFov;
        }

        private void updateFreeCamera(Camera camera)
        {
            // Dedicated frame counter for this file's own use (e.g. tap-vs-hold
            // timing below). frameTick, elsewhere in this file, only exists under
            // #if HOOK_ORDER_ASSERTS and isn't reliably available.
            if (orbitFrameCounter == int.MaxValue) orbitFrameCounter = 0;
            else orbitFrameCounter++;

            float deltaTime;
            if (decoupleDtFromGameTime)
            {
                deltaTime = 60.0f / MemoryUtil.Read<float>(sMain.Instance + 0x68);
            }
            else
            {
                deltaTime = MemoryUtil.Read<float>(sMain.Instance + 0x94);
            }

            // Disabled: "Hold LT (+ LB)" Lock Vertical/Speed-Modifier bind, per
            // user request (misfire prevention). Was:
            // buttonWasDown(Button.L2) || lockVerticalToggled
            bool lockVerticalAndModifySpeed = false;
            float adjustedSpeed = cameraSpeed * deltaTime * (lockVerticalAndModifySpeed ? cameraSpeedModifier : 1.0f);
            bool lockCameraLook = false;
            //ver8 WritePadInputHook() 内、12時方向angleでスナップと同じ考え方を角度全周に一般化して追加します。
            if (Math.Abs(PadLy) < stickDeadzone) PadLy = 0;
            if (Math.Abs(PadLx) < stickDeadzone) PadLx = 0;
            if (Math.Abs(PadRx) < stickDeadzone) PadRx = 0;
            if (Math.Abs(PadRy) < stickDeadzone) PadRy = 0;

            // Coarsen the right stick's *angle* to a fixed number of "clock"
            // steps (12 steps = 30deg increments, like an analog clock's
            // hour hand) instead of its full continuous resolution.
            // Magnitude (how far it's pushed) is preserved exactly; only the
            // angle is rounded to the nearest step. Same idea as the left-
            // stick 12-o'clock snap in WritePadInputHook, generalized to N
            // steps and applied here to the right stick instead, before
            // Return-to-Center Look (or ordinary free-look) ever sees it.
            if (orbitRightStickAngleSnapSteps > 0 && (PadRx != 0 || PadRy != 0))
            {
                float magnitude = MathF.Sqrt((float)PadRx * PadRx + (float)PadRy * PadRy);
                float angleDeg = Single.RadiansToDegrees(MathF.Atan2(PadRx, PadRy));
                float stepDeg = 360.0f / orbitRightStickAngleSnapSteps;
                float snappedAngleDeg = MathF.Round(angleDeg / stepDeg) * stepDeg;
                float snappedAngleRad = Single.DegreesToRadians(snappedAngleDeg);
                PadRx = (int)Math.Clamp(magnitude * MathF.Sin(snappedAngleRad), -32768.0f, 32767.0f);
                PadRy = (int)Math.Clamp(magnitude * MathF.Cos(snappedAngleRad), -32768.0f, 32767.0f);
            }
            //ver8ここまで

            cameraFrame = Vector3.Zero;

            Player? player = lastPlayer;
            if (player == null)
            {
                player = getPlayerFromSaveSlot();
            }

            // PadLx/y is read in WritePadInputHook().
            if (buttonWasDown(Button.L2) && buttonWasDown(Button.R2)) // Left stick zoom.
            {
                if (player != null && attachToChest && chestBone1 != 0x0 && chestBone2 != 0x0)
                {
                    Quaternion rotation = new Quaternion(player.Rotation.X, player.Rotation.Y, player.Rotation.Z, player.Rotation.W);
                    Vector3 up = Vector3.Transform(new Vector3(0.0f, 1.0f, 0.0f), rotation);
                    Vector3 right = Vector3.Transform(new Vector3(1.0f, 0.0f, 0.0f), rotation);
                    ref Vector3 jiggle1 = ref MemoryUtil.GetRef<Vector3>((swapChestSides ? chestBone2 : chestBone1) + 0xD0);
                    ref Vector3 jiggle2 = ref MemoryUtil.GetRef<Vector3>((swapChestSides ? chestBone1 : chestBone2) + 0xD0);
                    float scale = (Int16.MaxValue / adjustedSpeed / chestMoveScale);
                    jiggle1 -= (right * (PadLx / scale)) + (up * (PadLy / scale));
                    jiggle2 -= (right * (PadRx / scale)) + (up * (PadRy / scale));
                    lockCameraLook = true;
                }
                else
                {
                    // Disabled: "Hold LT+RT: Zoom with Left Stick" bind, per user request.
                    //float Ly = PadLy / (Int16.MaxValue / adjustedZoomSpeed(deltaTime));
                    //cameraFov = Math.Clamp(cameraFov - Ly, 1.0f, 179.0f);
                }
            }
            else if (playerMovementLocked || orbitPlayer) // Move camera.
            {
#if MOUSE_AND_KEYBOARD_LAYER
                if (keyboardEnabled)
                {
                    if (Input.IsDown(Key.Up)) PadLy += Int16.MaxValue;
                    if (Input.IsDown(Key.Down)) PadLy -= Int16.MaxValue;
                    if (Input.IsDown(Key.Left)) PadLx -= Int16.MaxValue;
                    if (Input.IsDown(Key.Right)) PadLx += Int16.MaxValue;
                }
#endif
                float Ly = PadLy / (Int16.MaxValue / adjustedSpeed);
                float Lx = PadLx / (Int16.MaxValue / adjustedSpeed);

                if (orbitPlayer)
                {
                    if (playerMovementLocked)
                    {
                        if (lockVerticalAndModifySpeed)
                        {
                            orbitRight += Lx;
                        }
                        else
                        {
                            orbitDistance -= Ly;
                            if (orbitDistance < 3.00f)
                            {
                                orbitDistance = 3.00f;
                            }
                        }
                        if (plusRight != 0.0f)
                        {
                            orbitMovementRotation += plusRight;
                        }
                    }
                    else
                    {
                        // Movement Rotation now doubles as a fixed calibration
                        // constant for Ignore Camera Direction's movement-angle
                        // math (see CheckMovementHook) - it compensates for a
                        // small residual offset between the Base Rotation
                        // Joint's reconstructed "stable forward" and what the
                        // game actually reads as camera-forward for movement.
                        // It used to auto-accumulate from the movement stick's
                        // raw Lx every frame during normal gameplay (originally
                        // for a circle-strafe-style manual turn), but that
                        // silently drifted the calibrated value away from
                        // straight over time from ordinary play - even a
                        // deadzone gate didn't fully prevent it. Left as a
                        // pure, stable, user-set value during normal gameplay;
                        // only the locked/camera-adjustment path above still
                        // adjusts it deliberately (via plusRight).
                    }
                    if (orbitMovementRotation >= 180.0f)
                    {
                        orbitMovementRotation -= 360.0f;
                    }
                    else if (orbitMovementRotation < -180.0f)
                    {
                        orbitMovementRotation += 360.0f;
                    }
                }
                else
                {
                    Quaternion forward = getForward(cameraPosition, cameraTarget);
                    if (cameraWrapState == 1)
                    {
                        // Invert diagonal directions when upside down for movement consistency.
                        forward.X = -forward.X;
                        forward.Z = -forward.Z;
                    }
                    if (lockVerticalAndModifySpeed)
                    {
                        forward.Y = 0.0f;
                    }
                    Quaternion right = forward;
                    right.Y = 0.0f;
                    right = getRight(right);
                    forward = Quaternion.Normalize(forward);
                    right = Quaternion.Normalize(right);
                    cameraFrame.X += right.X * Lx;
                    cameraFrame.Z += right.Z * Lx;
                    cameraFrame.X += forward.X * Ly;
                    cameraFrame.Y += forward.Y * Ly;
                    cameraFrame.Z += forward.Z * Ly;
                    if (plusForward != 0.0f)
                    {
                        forward *= plusForward * deltaTime;
                        cameraFrame.X += forward.X;
                        cameraFrame.Y += forward.Y;
                        cameraFrame.Z += forward.Z;
                    }
                }
            }

            // Disabled: everything in this block is a documented gamepad Bind
            // (Select+X, RB+D-Pad Right/Up, D-Pad Up/Down/Left/Right) that the
            // user asked to turn off entirely to prevent accidental misfires.
            if (false && !unlockInputToggled && !comboButton1Down)
            {
                if (buttonWasDown(Button.Share))
                {
                    if (buttonWasPressed(Button.Square))
                    {
                        setNearClip(camera, !guessAltNearClipSet(camera));
                    }
                }
                if (buttonWasDown(Button.R1))
                {
                    if (buttonWasPressed(Button.Right))
                    {
                        cameraRoll = (restoreRoll != null) ? (float)restoreRoll : 0.0f;
                    }
                    if (buttonWasPressed(Button.Up))
                    {
                        if (restoreFov != null)
                        {
                            cameraFov = Math.Clamp((float)restoreFov * (previousFov / DEFAULT_FOV), 1.0f, 179.0f);
                        }
                        else
                        {
                            cameraFov = DEFAULT_FOV;
                        }
                    }
                }
                else if (!unlockInputForMenu || unlockInputHideMenu)
                {
                    if (buttonWasDown(Button.Up))
                    {
                        cameraFrame.Y += adjustedSpeed / 2.0f;
                    }
                    if (buttonWasDown(Button.Down))
                    {
                        cameraFrame.Y -= adjustedSpeed / 2.0f;
                    }
                    if (buttonWasDown(Button.Left))
                    {
                        cameraRoll -= adjustedSpeed / 4.0f;
                    }
                    if (buttonWasDown(Button.Right))
                    {
                        cameraRoll += adjustedSpeed / 4.0f;
                    }
                }
            }

#if MOUSE_AND_KEYBOARD_LAYER
            if (keyboardEnabled)
            {
                if (Input.IsDown(Key.NumPadMinus))
                {
                    cameraFov = Math.Clamp(cameraFov - adjustedZoomSpeed(deltaTime), 1.0f, 179.0f);
                }
                if (Input.IsDown(Key.NumPadPlus))
                {
                    cameraFov = Math.Clamp(cameraFov + adjustedZoomSpeed(deltaTime), 1.0f, 179.0f);
                }
                if (Input.IsPressed(Key.NumPadEnter))
                {
                    if (restoreFov != null)
                    {
                        cameraFov = Math.Clamp((float)restoreFov * (previousFov / DEFAULT_FOV), 1.0f, 179.0f);
                    }
                    else
                    {
                        cameraFov = DEFAULT_FOV;
                    }
                }
                if (Input.IsPressed(Key.NumPadStar))
                {
                    cameraRoll = (restoreRoll != null) ? (float)restoreRoll : 0.0f;
                }
                if (Input.IsDown(Key.NumPad7))
                {
                    cameraFrame.Y += adjustedSpeed / 2.0f;
                }
                if (Input.IsDown(Key.NumPad1))
                {
                    cameraFrame.Y -= adjustedSpeed / 2.0f;
                }
                if (Input.IsDown(Key.NumPad9))
                {
                    cameraRoll += adjustedSpeed / 4.0f;
                }
                if (Input.IsDown(Key.NumPad3))
                {
                    cameraRoll -= adjustedSpeed / 4.0f;
                }
            }
#endif

            // Camera look. PadRx/y is read in WritePadInputHook().
            // 変更前: if (!lockCameraLook), L1押下中カメラロック
            // L2 aiming (slinger/bow, reticle up): reset the view straight
            // back to forward exactly once, on the frame L2 is first
            // pressed, so aiming always starts dead-centered instead of
            // carrying over whatever free-look offset was left from
            // casually glancing around beforehand. This is a one-time
            // reset, NOT a continuous lock - R-stick keeps controlling aim
            // normally for as long as L2 (and L2+R2, drawing) stays held -
            // UNLESS the currently-matched "C" profile opts out via
            // DisableLookWhileAiming (see below).
            if (orbitPlayer && buttonWasPressed(Button.L2))
            {
                cameraYaw = 180.0f;
                cameraPitch = 0.0f;
            }
            // OrbitProfile.DisableLookWhileAiming: while L2 is held
            // and this profile is active, this mod's own R-stick-driven
            // camera offset (Return-to-Center Look or plain accumulation,
            // whichever's active below) is suppressed entirely - the camera
            // just stays at the centered pose set above instead of trying
            // to track the stick itself. The reticle still moves normally,
            // since the base game reads the raw stick directly for aim,
            // independent of this mod's camera. The point: this mod's own
            // R-stick response (its own acceleration curve, return-to-
            // center speed, etc.) has a different feel/speed than the base
            // game's own aim response to the *same* stick input, so running
            // both off the same input visibly drifts the camera away from
            // where the reticle actually is over time. Suppressing this
            // mod's side removes the mismatch - the camera simply doesn't
            // try to visually track the aim for this profile.
            bool suppressLookForAimingProfile = orbitPlayer && buttonWasDown(Button.L2)
                && orbitLastProfileIndex >= 0 && orbitLastProfileIndex < orbitProfiles.Count
                && orbitProfiles[orbitLastProfileIndex].Mode == OrbitProfileMode.Normal
                && orbitProfiles[orbitLastProfileIndex].DisableLookWhileAiming;

            // OrbitProfile.UseNativeAimCameraDelaySeconds: the native
            // camera (see orbitNativeCameraPosition/Target, and its use
            // further below) isn't actively driven while Free Camera is on,
            // so it's just sitting wherever it last was - possibly nowhere
            // near the player's actual facing. The base game's own "aim
            // direction: player facing" setting resolves this by animating
            // the native camera from that stale pose to the player's actual
            // facing over a short transition (~0.25s) the instant L2 is
            // pressed - invisible normally (Free Camera hides the native
            // camera entirely), but if this mod switches to using it
            // immediately on L2 press, that whole swing becomes visible.
            // This timer delays the switch-over until the transition has
            // almost certainly finished, so by the time this mod starts
            // reading it, it's already settled on the correct direction and
            // the switch-over is invisible. 0 = no delay (switch instantly).
            if (buttonWasPressed(Button.L2))
            {
                float delaySeconds = (orbitLastProfileIndex >= 0 && orbitLastProfileIndex < orbitProfiles.Count
                    && orbitProfiles[orbitLastProfileIndex].Mode == OrbitProfileMode.Normal)
                    ? orbitProfiles[orbitLastProfileIndex].UseNativeAimCameraDelaySeconds
                    : 0.0f;
                orbitNativeAimCameraDelayTimer = delaySeconds;
                orbitNativeAimCameraDelayTotal = delaySeconds;
            }
            if (buttonWasDown(Button.L2) && orbitNativeAimCameraDelayTimer > 0.0f)
            {
                orbitNativeAimCameraDelayTimer -= deltaTime / 60.0f;
            }

            if (!suppressLookForAimingProfile && !lockCameraLook && !blockRightStickLookDuringL1)
            {
#if MOUSE_AND_KEYBOARD_LAYER
                if (keyboardEnabled)
                {
                    if (Input.IsDown(Key.NumPad8)) PadRy += keyboardLookValue;
                    if (Input.IsDown(Key.NumPad2)) PadRy -= keyboardLookValue;
                    if (Input.IsDown(Key.NumPad4)) PadRx -= keyboardLookValue;
                    if (Input.IsDown(Key.NumPad6)) PadRx += keyboardLookValue;
                }
#endif
                float adjustedSensitivity = cameraSensitivity * deltaTime * cameraFov;
                // Lets e.g. AIM_IDLE's R-stick response be tuned separately
                // from ordinary free-look (OrbitProfile.
                // LookSensitivityMultiplier) - applied uniformly below to
                // whichever of the two look modes (Return-to-Center or
                // plain accumulation) is actually active.
                float lookSensitivityMultiplier = getActiveLookSensitivityMultiplier();
                float Rx = PadRx / (Int16.MaxValue / adjustedSensitivity) * lookSensitivityMultiplier;
                float Ry = PadRy / (Int16.MaxValue / adjustedSensitivity) * lookSensitivityMultiplier;
                // Mouse contribution kept separate from the stick's Rx/Ry so
                // Return-to-Center Look (below) can apply auto-recentering
                // to the stick only, while still letting the mouse nudge
                // the view additively on top for KBM users.
                float mouseRx = 0.0f, mouseRy = 0.0f;
#if MOUSE_AND_KEYBOARD_LAYER
                if (mouseEnabled && !playerInMenu())
                {
                    mouseRx = MemoryUtil.GetRef<int>(sMhMouse.Instance + 0xFC) * mouseSensitivity;
                    mouseRy = -MemoryUtil.GetRef<int>(sMhMouse.Instance + 0x100) * mouseSensitivity;
                    Rx += mouseRx;
                    Ry += mouseRy;
                    ref int scrollY = ref MemoryUtil.GetRef<int>(sMhMouse.Instance + 0x17C);
                    if (orbitPlayer)
                    {
                        orbitDistance -= scrollY * cameraZoomSpeed * 2.0f;
                        if (orbitDistance < 0.01f)
                        {
                            orbitDistance = 0.01f;
                        }
                    }
                    else
                    {
                        cameraFov = Math.Clamp(cameraFov - scrollY * cameraZoomSpeed, 1.0f, 179.0f);
                    }
                }
#endif
                //ver9 右スティックYawのManual -> Keep -> Returning -> Followステートマシン
                // (旧ver8の「スティック位置=目標角度、常にイーズで追従」方式を置き換え)
                //
                // Follow: 手動オフセットなし。cameraYawを毎フレーム180に固定する。
                //
                // Manual: 右スティック操作中。オフセットは速度ベースで加算し、
                // ±orbitReturnToCenterYawMax(Max Yaw)でハードクランプする。
                //
                // Keep: 離した瞬間の実際のカメラワールドYaw
                // (orbitCachedCameraForwardYawDeg)を保持目標にする。以後は毎フレーム、
                // フィードフォワードでcameraYawを直接計算する。キャラクターの現在の
                // 正面(orbitCachedRawStableForwardYawDeg)が保持方向に近づいたら
                // Followへ(Catch-up & Lock)、逆にMax Yawを超えて離れたら
                // Returningへ移行する。
                //
                // Returning: cameraYawを毎フレーム180(=その時点でのキャラクターの
                // 現在の正面。180自体が動くベースを指すので毎フレーム取り直す
                // 必要がない)へorbitReturnToCenterSpeedでイーズさせる。この間は
                // 右スティック入力を無視する(仕様ドキュメント10項)。
                //
                // 未実装(次のステップで対応予定): L1リセットとの統合
                // (仕様ドキュメント14項)。L1は引き続き別ロジック(即座に180/0へ
                // スナップ)のままで、このReturningとは別経路。
                if (orbitPlayer && orbitReturnToCenterLook)
                {
                    bool rightStickActive = (PadRx != 0 || PadRy != 0);

                    // Returning中は右スティック入力を無視する。
                    if (rightStickActive && orbitLookState != OrbitLookState.Returning)
                    {
                        orbitLookState = OrbitLookState.Manual;
                    }
                    else if (!rightStickActive && orbitLookState == OrbitLookState.Manual)
                    {
                        // 今フレームでスティックがニュートラルに戻った=離した瞬間。
                        orbitLookState = OrbitLookState.Keep;
                        orbitKeepWorldYawDeg = orbitCachedCameraForwardYawDeg;
                        orbitKeepValid = orbitCachedForwardYawValid;
                        orbitKeepPrevOffsetValid = false;
                    }

                    if (orbitLookState == OrbitLookState.Manual)
                    {
                        float offsetYaw = deltaAngleDeg(180.0f, cameraYaw);
                        offsetYaw += Rx;
                        offsetYaw = Math.Clamp(offsetYaw, -orbitReturnToCenterYawMax, orbitReturnToCenterYawMax);
                        cameraYaw = 180.0f + offsetYaw;
                    }
                    else if (orbitLookState == OrbitLookState.Keep)
                    {
                        if (orbitKeepValid && orbitCachedBaseYawValid)
                        {
                            // フィードフォワード: 前フレームのベース方向から見て
                            // 保持目標に到達するcameraYawを直接計算する。
                            cameraYaw = orbitCachedBaseYawFlipped
                                ? deltaAngleDeg(orbitKeepWorldYawDeg, orbitCachedBaseYawDeg)
                                : deltaAngleDeg(orbitCachedBaseYawDeg, orbitKeepWorldYawDeg);

                            // 上で今まさに計算したcameraYaw自身が180(オフセット
                            // ゼロ)からどれだけズレているかで判定する。
                            // orbitCachedRawStableForwardYawDeg(体幹基準)のような
                            // "別の信号"と突き合わせると、プロファイルAの顔基準
                            // ベースとの間にわずかなズレが生じることがあり、
                            // Catch-up成立の瞬間にcameraYawへ残差が乗ったまま
                            // 処理してしまい、「旋回方向と逆に一瞬戻ってから追いつく」
                            // 違和感の原因になっていた。判定と実際の値を同じ信号に
                            // することでこの残差を原理的になくす。
                            float offsetFromNeutral = deltaAngleDeg(180.0f, cameraYaw);

                            // 素通り対策: 狭いepsilon窓を1フレームでまたいで
                            // 通過してしまうことがあるため、前フレームとの間で
                            // 符号が反転した(=ちょうど0をまたいで通過した)ことも
                            // Catch-upとみなす。
                            bool crossedZero = orbitKeepPrevOffsetValid
                                && Math.Sign(offsetFromNeutral) != 0
                                && Math.Sign(orbitKeepPrevOffsetDeg) != 0
                                && Math.Sign(offsetFromNeutral) != Math.Sign(orbitKeepPrevOffsetDeg);

                            if (MathF.Abs(offsetFromNeutral) < orbitKeepCatchUpEpsilonDeg || crossedZero)
                            {
                                // Catch-up & Lock: ちょうど保持方向にいる、または
                                // またいで通過した。すでに180近傍なのでReturningを
                                // 経由せずそのままFollowへ切り替える(見た目上の
                                // 動きは発生しない)。
                                orbitLookState = OrbitLookState.Follow;
                                cameraYaw = 180.0f;
                            }
                            //ver9.3
                            else if (MathF.Abs(offsetFromNeutral) > orbitReturnToCenterYawMax + orbitReturnToCenterExceedMarginDeg)
                            {
                                // Max Yaw超過(マージン込み): 保持を解除して
                                // キャラクター正面へスムーズに戻す。Pitchはそのまま
                                // 保持するので(仕様ドキュメント16項)resetPitchは
                                // falseのまま(L1由来のtrueが残っていないよう明示)。
                                orbitLookState = OrbitLookState.Returning;
                                orbitReturningResetPitch = false;
                            }
                            //ver9.3ここまで
                            orbitKeepPrevOffsetDeg = offsetFromNeutral;
                            orbitKeepPrevOffsetValid = true;
                        }
                    }
                    //ver9.3
                    else if (orbitLookState == OrbitLookState.Returning)
                    {
                        float t = Math.Clamp(orbitReturnToCenterSpeed * deltaTime, 0.0f, 1.0f);
                        cameraYaw = lerpAngleDeg(cameraYaw, 180.0f, t);
                        float remainingYaw = deltaAngleDeg(180.0f, cameraYaw);
                        bool yawSettled = MathF.Abs(remainingYaw) < orbitKeepCatchUpEpsilonDeg;

                        // L1リセット由来のReturningのときだけ、Pitchも同じ速度で
                        // 0(水平)へイーズさせる(仕様ドキュメント14項)。Max Yaw
                        // 超過由来のReturningではPitchはそのまま保持する(16項)。
                        bool pitchSettled = true;
                        if (orbitReturningResetPitch)
                        {
                            cameraPitch = lerpAngleDeg(cameraPitch, 0.0f, t);
                            pitchSettled = MathF.Abs(cameraPitch) < orbitKeepCatchUpEpsilonDeg;
                        }

                        if (yawSettled && pitchSettled)
                        {
                            orbitLookState = OrbitLookState.Follow;
                            cameraYaw = 180.0f;
                            if (orbitReturningResetPitch)
                            {
                                cameraPitch = 0.0f;
                            }
                            orbitReturningResetPitch = false;
                        }
                    }
                    //ver9.3ここまで
                    else // Follow
                    {
                        cameraYaw = 180.0f;
                    }

                    cameraYaw += mouseRx;

                    // Pitch: Manual中は従来通り速度ベースで加算。Returning中も
                    // 右スティック入力自体を無視するのでここも対象外にする。
                    if (rightStickActive && orbitLookState != OrbitLookState.Returning)
                    {
                        cameraPitch += Ry;
                    }
                    cameraPitch += mouseRy;
                    cameraPitch = Math.Clamp(cameraPitch, -orbitReturnToCenterPitchMax, orbitReturnToCenterPitchMax);
                }
                //ver9ここまで
                else
                {
                    cameraYaw += Rx;
                    if (plusRight != 0.0f && !orbitPlayer)
                    {
                        cameraYaw += plusRight * deltaTime;
                    }
                    cameraPitch += Ry;
                }
            }
            if (cameraYaw >= 180.0f)
            {
                cameraYaw -= 360.0f;
            }
            else if (cameraYaw < -180.0f)
            {
                cameraYaw += 360.0f;
            }
            float pitchLimit = cameraPitchLimit;
            if (orbitPlayer && pitchLimit == -1.0f)
            {
                pitchLimit = Config.Settings.MAX_PITCH_LIMIT;
            }
            if (pitchLimit >= 0.0f)
            {
                cameraPitch = Math.Clamp(cameraPitch, -pitchLimit, pitchLimit);
            }
            else
            {
                if (cameraPitch >= 90.0f && cameraWrapState == 0)
                {
                    cameraRoll += 180.0f;
                    cameraWrapState = 1;
                }
                else if (cameraPitch < -90.0f && cameraWrapState == 0)
                {
                    cameraPitch += 360.0f;
                    cameraRoll += 180.0f;
                    cameraWrapState = 1;
                }
                else if (cameraPitch < 90.0f && cameraWrapState == 1)
                {
                    cameraRoll -= 180.0f;
                    cameraWrapState = 0;
                }
                else if (cameraPitch >= 270.0f && cameraWrapState == 1)
                {
                    cameraPitch -= 360.0f;
                    cameraRoll -= 180.0f;
                    cameraWrapState = 0;
                }
            }
            if (cameraRoll < 0.0f)
            {
                cameraRoll += 360.0f;
            }
            else if (cameraRoll >= 360.0f)
            {
                cameraRoll -= 360.0f;
            }
            //ver9.3
            // The base game's own "recenter camera" button doesn't reach the game's
            // camera logic while this free/orbit camera is driving the view, so we
            // re-implement it here. To match the base game (a short tap recenters;
            // a long hold opens the item wheel instead), we only trigger on
            // *release*, and only if it was held for less than
            // orbitRecenterTapMaxSeconds - a long hold is left alone so it doesn't
            // interfere with the item wheel.
            //ver9 フレーム数ではなく現実時間(秒)でタップ判定する。フレーム数
            // だとフレームレートが変わると同じ長さの時間を表さなくなるため
            // (120FPSは60FPSの半分の時間しか経たない)、deltaTimeを積算した
            // 経過秒数で判定するように変更した。
            if (orbitPlayer && orbitRecenterButton != null)
            {
                if (buttonWasPressed(orbitRecenterButton.Value))
                {
                    orbitRecenterHeldSec = 0.0f;
                }
                else if (orbitRecenterHeldSec >= 0.0f)
                {
                    // このコードベースのdeltaTimeは実秒ではなく60FPS基準の
                    // フレーム換算値(60FPSで約1.0、120FPSで約0.5)。実秒に
                    // 変換してから積算する(elapsedSeconds = deltaTime / 60.0f
                    // という他箇所と同じ変換)。これを忘れていたため、1秒あたり
                    // フレームレート分(例:70FPSなら約70)が積み上がってしまい、
                    // しきい値0.3をどんなに短いタップでも一瞬で超えていた。
                    orbitRecenterHeldSec += deltaTime / 60.0f;
                }

                if (buttonWasReleased(orbitRecenterButton.Value))
                {
                    if (orbitRecenterHeldSec >= 0.0f && orbitRecenterHeldSec <= orbitRecenterTapMaxSeconds)
                    {
                        // 即座に180/0へスナップするのではなく、Returningへ渡して
                        // orbitReturnToCenterSpeedでcameraYaw(現在のキャラクター
                        // 正面へ)とcameraPitch(水平0度へ)をイーズさせる
                        // (仕様ドキュメント14項)。
                        orbitLookState = OrbitLookState.Returning;
                        orbitReturningResetPitch = true;
                        cameraWrapState = 0;
                        // The A/B/C deviation smoothing (see the Simple Lock
                        // clamp block) is a low-pass filter - it lags behind
                        // by design. Without this, a still-decaying residual
                        // from a just-finished "C" (attack) motion would keep
                        // the view slightly off-center even right after
                        // recentering. Forcing a resync snaps it to the
                        // current real deviation instead of the stale one.
                        orbitClampSmoothedInit = false;
                        orbitKeepValid = false;
                    }
                    orbitRecenterHeldSec = -1.0f;
                }
            }
            //ver9.3ここまで
            if (orbitPlayer && player != null)
            {
                Quaternion rotation;
                Vector3 target;
                Quaternion offsetBasis = Quaternion.Identity;
                //ver12 プロファイル単位でPosition/Basis Jointを上書きする
                // (瓶を口に運ぶ際に鼻ではなく手を基準にする、等)。
                // orbitLastProfileIndexはこの時点では「前フレームの分類」
                // (このtarget計算自体がプロファイル判定より前に行われるため)。
                // 切り替わる瞬間の1フレームだけ旧設定が残るだけなので、数秒
                // 続くモーションでは体感できない(orbitNativeCameraPosition/
                // Targetなど、このファイルの他箇所でも同じ許容をしている)。
                OrbitProfile? orbitPositionOverrideProfile = (orbitLastProfileIndex >= 0 && orbitLastProfileIndex < orbitProfiles.Count
                    && orbitProfiles[orbitLastProfileIndex].PositionJointOverrideEnable)
                    ? orbitProfiles[orbitLastProfileIndex]
                    : null;
                List<nint> orbitJointList = orbitPositionOverrideProfile != null
                    ? (orbitPositionOverrideProfile.PositionJointOverrideUseFace ? faceJoints : bodyJoints)
                    : (orbitTargetFaceJoint ? faceJoints : bodyJoints);
                int orbitActiveJoint = orbitPositionOverrideProfile?.PositionJointOverrideJoint ?? orbitJoint;
                bool targetJoint = orbitActiveJoint >= 0 && orbitActiveJoint < orbitJointList.Count;
                //ver12ここまで
                if (targetJoint)
                {
                    nint jointAddr = orbitJointList[orbitActiveJoint];
                    target = MemoryUtil.GetRef<Vector3>(jointAddr + 0x50);
                    // Used only to place Target Y / Target Right / Distance offsets
                    // relative to the *position* joint's own natural orientation -
                    // kept separate from "rotation" (the final look direction) so
                    // that when rotation comes from a different joint (Simple
                    // Rotation Joint), the offset placement doesn't drift as that
                    // other joint rotates independently (e.g. the hip swaying while
                    // walking) instead of staying anchored to the position joint.
                    offsetBasis = new Quaternion(player.Rotation.X, player.Rotation.Y, player.Rotation.Z, player.Rotation.W)
                        * MemoryUtil.GetRef<Quaternion>(jointAddr + 0x70);

                    if (orbitSimpleLock)
                    {
                        // Simplest possible mode: position comes from Target Joint.
                        // Rotation is built directly from the *positions* of three
                        // face joints - nose, a center/back reference, and the two
                        // ears - rather than from any joint's own rotation data.
                        // This sidesteps the hip/body rotation entirely (no more
                        // "waist rotation" bleeding into the view) and stays purely
                        // driven by where the face itself actually is, which still
                        // correctly reflects rolls/flips since the head physically
                        // moves through space during those animations.
                        rotation = new Quaternion(player.Rotation.X, player.Rotation.Y, player.Rotation.Z, player.Rotation.W);
                        bool builtFaceBasis = false;

                        // A large, sudden rotation crammed into very few
                        // frames (e.g. RESPAWN_CAT_KART - carried away on a
                        // stretcher, thrown, tumbles) can legitimately flip
                        // faceUp's sign frame-to-frame. The anti-flip
                        // correction below can't tell that apart from real
                        // position noise, so it locks onto the wrong
                        // hemisphere - and since nothing previously reset
                        // it, that wrong sign could persist even after the
                        // motion ended. Resetting whenever the action/motion
                        // changes re-anchors fresh on each new motion's
                        // first frame instead of carrying that bias across
                        // motion boundaries.
                        string faceBasisActionKey = $"{getCurrentActionName(player)}|{getCurrentMotionKey(player)}";
                        if (orbitFaceBasisLastActionKey != faceBasisActionKey)
                        {
                            orbitFaceBasisPrevUpInit = false;
                            orbitFaceBasisLastActionKey = faceBasisActionKey;
                        }

                        if (orbitFaceBasisEnable
                            && orbitFaceBasisCenterJoint >= 0 && orbitFaceBasisCenterJoint < faceJoints.Count
                            && orbitFaceBasisRightEarJoint >= 0 && orbitFaceBasisRightEarJoint < faceJoints.Count
                            && orbitFaceBasisLeftEarJoint >= 0 && orbitFaceBasisLeftEarJoint < faceJoints.Count)
                        {
                            Vector3 nosePos = target; // raw joint position, not yet offset
                            Vector3 centerPos = MemoryUtil.GetRef<Vector3>(faceJoints[orbitFaceBasisCenterJoint] + 0x50);
                            Vector3 rightEarPos = MemoryUtil.GetRef<Vector3>(faceJoints[orbitFaceBasisRightEarJoint] + 0x50);
                            Vector3 leftEarPos = MemoryUtil.GetRef<Vector3>(faceJoints[orbitFaceBasisLeftEarJoint] + 0x50);

                            Vector3 faceForward = nosePos - centerPos;
                            Vector3 earRight = rightEarPos - leftEarPos;
                            if (faceForward.LengthSquared() > 1e-8f && earRight.LengthSquared() > 1e-8f)
                            {
                                faceForward = Vector3.Normalize(faceForward);
                                float earRightLen = earRight.Length();
                                Vector3 faceUpRaw = Vector3.Cross(faceForward, earRight);
                                if (faceUpRaw.LengthSquared() > 1e-8f)
                                {
                                    Vector3 faceUp = Vector3.Normalize(faceUpRaw);
                                    // Reconstructing "up" fresh every frame from raw
                                    // positions has no memory of which way was "up" a
                                    // moment ago. Near certain angles (e.g. mid-roll,
                                    // where forward and the ear line briefly get close
                                    // to parallel) small position noise can make this
                                    // flip to the opposite side, which looks like the
                                    // view suddenly turning upside-down. sinAngle below
                                    // (angle between faceForward and earRight,
                                    // independent of head size) measures how close to
                                    // that degenerate, parallel configuration this
                                    // frame is. Only in that narrow zone do we hold
                                    // onto the previous frame's sign as a noise filter.
                                    // Away from it the geometry is well-conditioned, so
                                    // a sign disagreement is far more likely to be a
                                    // real, fast reorientation of the head - e.g. being
                                    // flung around on RESPAWN_CAT_KART, dismounting a
                                    // wyvern, or sitting down to eat - and forcing the
                                    // old sign in that case is exactly what leaves the
                                    // view stuck upside-down for the rest of the
                                    // motion, since every later well-conditioned frame
                                    // would otherwise keep getting flipped back to
                                    // match the one bad frame that started it.
                                    float sinAngle = faceUpRaw.Length() / earRightLen;
                                    bool nearDegenerate = sinAngle < 0.2f;
                                    if (nearDegenerate && orbitFaceBasisPrevUpInit && Vector3.Dot(faceUp, orbitFaceBasisPrevUp) < 0.0f)
                                    {
                                        faceUp = -faceUp;
                                    }
                                    orbitFaceBasisPrevUp = faceUp;
                                    orbitFaceBasisPrevUpInit = true;
                                    rotation = lookRotation(faceForward, faceUp);
                                    builtFaceBasis = true;
                                }
                            }
                        }
                        if (!builtFaceBasis)
                        {
                            // Fallback: Simple Rotation Joint's own rotation data.
                            List<nint> simpleRotJointList = orbitSimpleRotationJointUseFace ? faceJoints : bodyJoints;
                            bool hasSimpleRotJoint = orbitSimpleRotationJoint >= 0 && orbitSimpleRotationJoint < simpleRotJointList.Count;
                            if (hasSimpleRotJoint)
                            {
                                nint simpleRotJointAddr = simpleRotJointList[orbitSimpleRotationJoint];
                                rotation *= MemoryUtil.GetRef<Quaternion>(simpleRotJointAddr + 0x70);
                            }
                            else
                            {
                                rotation *= MemoryUtil.GetRef<Quaternion>(jointAddr + 0x70);
                            }
                        }

                        // "Full-body rotation" motions (spin attacks, front-flip
                        // dodges - see the keyword list) keep the raw face-basis
                        // rotation as-is, unclamped, so they still track the
                        // motion exactly like before this feature existed.
                        // Everything else ("Normal": standing, walking, regular
                        // attacks) gets pulled back toward Base Rotation Joint's
                        // direction whenever it strays further than a human
                        // neck plausibly could - this is what cancels out
                        // head-tracking (looking at items etc.) while standing
                        // or moving normally. Reuses the exact same base+blend
                        // math as the "Rotation Joint" mode below.
                        if (orbitFaceClampEnable)
                        {
                            //ver10.1
                            string? currentActionName = getCurrentActionName(player);
                            string currentMotionKey = getCurrentMotionKey(player);
                            // findMatchingProfileIndex() -> matchesKeywordList() が
                            // "#<数値>" キーワードの判定に使うので、必ずその手前で更新する。
                            int previousActionSubState = orbitActionSubState;
                            orbitActionSubState = getCurrentActionSubState(player);
                            if (orbitActionSubState != previousActionSubState)
                            {
                                // サブステートが切り替わった瞬間。ここを 0 の基準点として、
                                // 経過時間の計測をやり直す。
                                orbitSubStateElapsedSec = 0.0f;
                            }
                            else
                            {
                                // deltaTime は「60fpsでの何フレーム分か」で渡ってくるので、
                                // 他の秒基準タイマーと同じく /60 して秒に変換する。
                                orbitSubStateElapsedSec += deltaTime / 60.0f;
                            }
                            orbitLastActionName = $"{currentActionName} [{currentMotionKey}]";
                            //ver10.1ここまで

                            // Single unified lookup, top-to-bottom, first
                            // match wins, across every profile regardless of
                            // its Mode - replaces the old fixed B(Ignore-X)
                            // -> B -> C priority order with whatever order
                            // the person has actually arranged them in.
                            // Matching none of them at all defaults to "A"
                            // (Full Rotation, unclamped).
                            orbitLastProfileIndex = findMatchingProfileIndex(currentActionName, currentMotionKey);
                            OrbitProfileMode currentMode = orbitLastProfileIndex >= 0
                                ? orbitProfiles[orbitLastProfileIndex].Mode
                                : OrbitProfileMode.FullRotation;

                            // ver13: マッチしたプロファイルがスリンガーの強制非表示を
                            // 要求しているかを毎フレーム更新する。実際の適用は
                            // collectArmorParts (Armor.Slinger) 側で行う - 次に
                            // refreshArmorState() が呼ばれたときに反映されるので、
                            // 最大1フレームの遅延はあるが体感できない。
                            orbitSlingerHiddenByProfile = orbitLastProfileIndex >= 0
                                && orbitLastProfileIndex < orbitProfiles.Count
                                && orbitProfiles[orbitLastProfileIndex].HideSlingerWhileActive;

                            orbitLastBaseOnlyIgnoreX = currentMode == OrbitProfileMode.BaseOnlyIgnoreX;
                            orbitLastIsBaseOnly = orbitLastBaseOnlyIgnoreX || currentMode == OrbitProfileMode.BaseOnly;
                            orbitLastIsFullRotation = currentMode == OrbitProfileMode.FullRotation;

                            // orbitLastProfileIndex itself is already a
                            // stable per-motion identity (-1 = "no profile
                            // matched" is its own single shared state, same
                            // as before) - no separate id encoding needed.
                            int currentProfileId = orbitLastProfileIndex;

                            if (!orbitPrevProfileIdInit)
                            {
                                orbitPrevProfileId = currentProfileId;
                                orbitPrevProfileIdInit = true;
                            }
                            else if (currentProfileId != orbitPrevProfileId)
                            {
                                if (getActiveEnableTransitionBlend())
                                {
                                    orbitProfileTransitionActive = true;
                                    orbitProfileTransitionTimer = 0.0f;
                                    // Snapshot which duration this specific
                                    // blend uses, so it always finishes in
                                    // the time it started with (see
                                    // orbitCurrentTransitionDuration above).
                                    orbitCurrentTransitionDuration = getActiveProfileTransitionDuration();
                                    orbitProfileTransitionStartRotation = orbitFinalRotationPrevInit ? orbitFinalRotationPrev : rotation;
                                    // Positionブレンドの初期値を前のフレームで実際に適用された位置オフセットに設定
                                    orbitProfileTransitionStartTargetY = orbitPrevTargetY;
                                    orbitProfileTransitionStartTargetRight = orbitPrevTargetRight;
                                    orbitProfileTransitionStartTargetForward = orbitPrevTargetForward;
                                }
                                else
                                {
                                    // ブレンドOFFの場合は強制的に遷移フラグを落とし、即時切り替えを適用する
                                    orbitProfileTransitionActive = false;
                                }

                                if (buttonWasDown(Button.L2) && orbitLastProfileIndex >= 0
                                    && orbitProfiles[orbitLastProfileIndex].Mode == OrbitProfileMode.Normal)
                                {
                                    var nextProfile = orbitProfiles[orbitLastProfileIndex];
                                    if (nextProfile.UseNativeAimCamera)
                                    {
                                        orbitNativeAimCameraDelayTimer = nextProfile.UseNativeAimCameraDelaySeconds;
                                        orbitNativeAimCameraDelayTotal = nextProfile.UseNativeAimCameraDelaySeconds;
                                    }
                                }

                                orbitPrevProfileId = currentProfileId;
                            }
                            
                            // Computed and cached unconditionally (even
                            // during "A") - orbitClampBaseRotationCache is
                            // also used for movement-relative direction
                            // (Ignore Camera Direction), which needs to stay
                            // accurate *during* a dodge specifically, not
                            // just outside of it.
                            List<nint> clampBaseJointList = orbitBaseRotationJointUseFace ? faceJoints : bodyJoints;
                            bool hasClampBaseJoint = orbitBaseRotationJoint >= 0 && orbitBaseRotationJoint < clampBaseJointList.Count;
                            Quaternion clampBaseRotation = new Quaternion(player.Rotation.X, player.Rotation.Y, player.Rotation.Z, player.Rotation.W);
                            if (hasClampBaseJoint)
                            {
                                nint clampBaseJointAddr = clampBaseJointList[orbitBaseRotationJoint];
                                clampBaseRotation *= MemoryUtil.GetRef<Quaternion>(clampBaseJointAddr + 0x70);
                            }
                            // NOTE (history): Correction Yaw/Pitch/Roll used
                            // to be applied as a separate post-multiply after
                            // this whole if-block (to avoid it cancelling
                            // itself out for full-tracking "C" profiles - see
                            // old comment below this one, kept for context).
                            // That fixed full-tracking "C" but broke "A"
                            // (which had never had correction applied to it
                            // at all - applying the same value uniformly
                            // introduced a spurious 180 yaw flip, since "A"'s
                            // raw joint rotation uses a different native
                            // convention than clampBaseRotation's
                            // player.Rotation-based one and was never
                            // supposed to need this correction) and broke
                            // non-full-tracking "C" profiles like the
                            // walk/run/idle one (pitch inversion via
                            // clampFlip non-commutativity - the post-multiply
                            // position relative to clampFlip isn't
                            // equivalent to baking it in here except at
                            // exactly 180/0/0). Reverted back to baking it in
                            // here, selected per-state
                            // (getActiveOrbitBaseCorrection()) - this is
                            // exactly what was confirmed working for "B"/"B
                            // (Ignore-X)" before the post-multiply attempt,
                            // and "A" never used this value anyway (skips
                            // this whole block), so it's unaffected either
                            // way. The known remaining limitation: this still
                            // self-cancels for any full-tracking (Range竕・)
                            // "C" profile - use
                            // OrbitProfile.UseNativeAimCamera for those
                            // instead (bypasses this entire reconstruction).
                            var (activeCorrectionYaw, activeCorrectionPitch, activeCorrectionRoll) = getActiveOrbitBaseCorrection();
                            clampBaseRotation *= Quaternion.CreateFromYawPitchRoll(
                                Single.DegreesToRadians(activeCorrectionYaw),
                                Single.DegreesToRadians(activeCorrectionPitch),
                                Single.DegreesToRadians(activeCorrectionRoll));

                            //ver9 Keepのcatch-up判定用に、平滑化(Slerp)される前の
                            // "今まさに向いている"正面Yawをキャッシュしておく。
                            // 素早い旋回(走行での急旋回等)が続くと、下のSlerp平滑化
                            // は本当の現在向きより遅れ続けてしまい、その遅れた値を
                            // catch-up判定に使うと「何周かしないと追いついた判定に
                            // ならない」原因になるため、判定にはこちらを使う。
                            {
                                Vector3 orbitRawStableForward = Vector3.Transform(new Vector3(0.0f, 0.0f, 1.0f), clampBaseRotation);
                                if (orbitRawStableForward.X * orbitRawStableForward.X + orbitRawStableForward.Z * orbitRawStableForward.Z > 0.0001f)
                                {
                                    orbitCachedRawStableForwardYawDeg = Single.RadiansToDegrees(MathF.Atan2(orbitRawStableForward.X, orbitRawStableForward.Z));
                                    orbitCachedRawStableForwardYawValid = true;
                                }
                            }
                            //ver9ここまで

                            float activeClampBaseSmoothing = getActiveClampBaseSmoothing();
                            if (!orbitClampBaseSmoothedInit)
                            {
                                orbitClampBaseRotationSmoothed = clampBaseRotation;
                                orbitClampBaseSmoothedInit = true;
                            }
                            else if (activeClampBaseSmoothing < 1.0f)
                            {
                                // Slerp takes the shorter path automatically
                                // (handles quaternion double-cover), same
                                // as the final-rotation limiter below.
                                // フレームレート非依存化: この値はAプロファイル中でも
                                // 無条件に計算され、WritePadInputHookのDecouple
                                // Movement From Look補正が直接参照するため、
                                // ここのalphaが未補正だと設定のON/OFFに関係なく
                                // 左スティックの旋回感がFPSごとに変わっていた。
                                float baseAlpha = frameRateIndependentAlpha(activeClampBaseSmoothing, deltaTime);
                                orbitClampBaseRotationSmoothed = Quaternion.Slerp(orbitClampBaseRotationSmoothed, clampBaseRotation, baseAlpha);
                            }
                            else
                            {
                                orbitClampBaseRotationSmoothed = clampBaseRotation;
                            }
                            clampBaseRotation = orbitClampBaseRotationSmoothed;
                            orbitClampBaseRotationCache = clampBaseRotation;

                            if (!orbitLastIsFullRotation)
                            {
                                bool orbitInC = !orbitLastIsBaseOnly; // reached here means !orbitLastIsFullRotation already
                                if (orbitInC)
                                {
                                    if (!orbitWasInC || !orbitSpotPointValid)
                                    {
                                        Vector3 spotForward = Vector3.Transform(new Vector3(0.0f, 0.0f, 1.0f), clampBaseRotation);
                                        orbitSpotPoint = target + spotForward * orbitSpotDistance;
                                        orbitSpotPointValid = true;
                                    }
                                    else if (orbitSpotReanchorRate > 0.0f)
                                    {
                                        // Slowly nudge the spot point toward
                                        // where it would be if recaptured
                                        // fresh from the *current* live base
                                        // direction - slow enough not to
                                        // disrupt spotting during any single
                                        // rotation, but fast enough that a
                                        // long attack's net drift (e.g. root
                                        // motion repositioning the character
                                        // slightly) doesn't build up into a
                                        // noticeable correction once C ends
                                        // and hands off to B's live reference.
                                        Vector3 freshSpotForward = Vector3.Transform(new Vector3(0.0f, 0.0f, 1.0f), clampBaseRotation);
                                        Vector3 freshSpotPoint = target + freshSpotForward * orbitSpotDistance;
                                        float reanchorT = Math.Clamp(orbitSpotReanchorRate * deltaTime, 0.0f, 1.0f);
                                        orbitSpotPoint = Vector3.Lerp(orbitSpotPoint, freshSpotPoint, reanchorT);
                                    }
                                }
                                else
                                {
                                    orbitSpotPointValid = false;
                                }
                                orbitWasInC = orbitInC;

                                // While in C: look toward the fixed spot point
                                // captured above (a point in the room, like
                                // real ballet spotting), instead of B's own
                                // live forward direction - this direction is
                                // still recomputed every frame from the
                                // *current* position, so it accounts for the
                                // camera moving during the attack, but the
                                // point itself doesn't drift with the motion.
                                Quaternion clampReferenceRotation = clampBaseRotation;
                                if (orbitInC && orbitSpotPointValid)
                                {
                                    Vector3 dirToSpot = orbitSpotPoint - target;
                                    if (dirToSpot.LengthSquared() > 1.0f)
                                    {
                                        Vector3 upRef = Vector3.Transform(new Vector3(0.0f, 1.0f, 0.0f), clampBaseRotation);
                                        clampReferenceRotation = lookRotation(dirToSpot, upRef);
                                    }
                                }

                                // The raw face-basis "rotation" here uses a different
                                // forward/up sign convention than clampBaseRotation
                                // (player.Rotation) - it only gets corrected to match
                                // right before Target/Up are read out, further below.
                                // Apply that same correction to a throwaway copy here
                                // so the clamp math compares apples to apples, then
                                // undo it on the result (the flip is its own inverse)
                                // so "rotation" stays in its original convention for
                                // the offset multiply / Target+Up sampling that follow.
                                Quaternion clampFlip = Quaternion.CreateFromAxisAngle(new Vector3(1.0f, 0.0f, 0.0f), MathF.PI);
                                Quaternion rotationForClamp = rotation * clampFlip;

                                // "B" (Base-Only) motions: target deviation is
                                // pinned to zero - the face-basis rotation
                                // (head-tracking, item/NPC look-at, etc.) is
                                // ignored entirely, not just clamped. This still
                                // flows through the same smoothing below so
                                // switching in/out of B doesn't snap instantly.
                                float clampRelYaw, clampRelPitch, clampRelRoll;
                                if (orbitLastIsBaseOnly)
                                {
                                    clampRelYaw = 0.0f;
                                    clampRelPitch = 0.0f;
                                    clampRelRoll = 0.0f;
                                }
                                else
                                {
                                    Quaternion clampRelative = Quaternion.Inverse(clampReferenceRotation) * rotationForClamp;
                                    Vector3 clampRelForward = Vector3.Transform(new Vector3(0.0f, 0.0f, 1.0f), clampRelative);
                                    extractYawPitchDegFromForward(clampRelForward, out clampRelYaw, out clampRelPitch);
                                    clampRelRoll = extractRollDegAroundForward(clampRelative, clampRelYaw, clampRelPitch);
                                }

                                if (!orbitClampSmoothedInit)
                                {
                                    orbitClampSmoothedYaw = clampRelYaw;
                                    orbitClampSmoothedPitch = clampRelPitch;
                                    orbitClampSmoothedRoll = clampRelRoll;
                                    orbitClampSmoothedInit = true;
                                }
                                else if (orbitLastIsBaseOnly && orbitClampSmoothing < 1.0f)
                                {
                                    // B's target is always exactly 0 (a fixed
                                    // point) - smoothing here just avoids an
                                    // abrupt snap and converges fine over a
                                    // few frames, no lag problem.
                                    // フレームレート非依存化: orbitClampBaseSmoothingと同じ理由。
                                    float clampAlpha = frameRateIndependentAlpha(orbitClampSmoothing, deltaTime);
                                    orbitClampSmoothedYaw = lerpAngleDeg(orbitClampSmoothedYaw, clampRelYaw, clampAlpha);
                                    orbitClampSmoothedPitch = lerpAngleDeg(orbitClampSmoothedPitch, clampRelPitch, clampAlpha);
                                    orbitClampSmoothedRoll = lerpAngleDeg(orbitClampSmoothedRoll, clampRelRoll, clampAlpha);
                                }
                                else
                                {
                                    // C: the raw deviation is a genuinely
                                    // fast-moving target during an attack -
                                    // any fixed-fraction smoothing here lags
                                    // behind proportionally to how fast it's
                                    // changing, so it can never catch back up
                                    // to "within range" during a fast spin,
                                    // breaking the spotting behavior entirely.
                                    // Use it raw/instant instead.
                                    orbitClampSmoothedYaw = clampRelYaw;
                                    orbitClampSmoothedPitch = clampRelPitch;
                                    orbitClampSmoothedRoll = clampRelRoll;

                                    // Optional per-profile shake suppression
                                    // (see OrbitProfile.
                                    // ShakeSuppressSeconds) - deliberately NOT
                                    // a rate cap (tried that: it necessarily
                                    // slows down *legitimate* fast R-stick
                                    // tracking too since it can't tell "fast
                                    // because of a real jolt" from "fast
                                    // because you moved the stick fast", and
                                    // recovering from a held-back spike still
                                    // means catching up gradually, adding lag
                                    // to normal aim). Instead: freeze the
                                    // joint-driven deviation at exactly
                                    // whatever it was the instant R2 is
                                    // pressed or released (the two moments
                                    // that actually cause the animation kick -
                                    // nocking, then firing), hold it there for
                                    // a short fixed window, then snap straight
                                    // back to instant tracking - no catch-up,
                                    // no added lag to ordinary aiming. The
                                    // right stick's own contribution
                                    // (cameraYaw/cameraPitch) is untouched
                                    // throughout - only the *joint's own*
                                    // motion is suppressed.
                                    float suppressSeconds = (orbitLastProfileIndex >= 0 && orbitLastProfileIndex < orbitProfiles.Count
                                        && orbitProfiles[orbitLastProfileIndex].Mode == OrbitProfileMode.Normal)
                                        ? orbitProfiles[orbitLastProfileIndex].ShakeSuppressSeconds
                                        : 0.0f;
                                    if (suppressSeconds > 0.0f)
                                    {
                                        if (buttonWasPressed(Button.R2) || buttonWasReleased(Button.R2))
                                        {
                                            orbitShakeSuppressTimer = suppressSeconds;
                                            // Freeze at the *previous* frame's
                                            // value (before this frame's kick
                                            // can have moved it), not this
                                            // frame's raw value.
                                            orbitShakeSuppressYaw = orbitShakeSuppressInit ? orbitShakeSuppressYaw : clampRelYaw;
                                            orbitShakeSuppressPitch = orbitShakeSuppressInit ? orbitShakeSuppressPitch : clampRelPitch;
                                            orbitShakeSuppressRoll = orbitShakeSuppressInit ? orbitShakeSuppressRoll : clampRelRoll;
                                        }
                                        if (orbitShakeSuppressTimer > 0.0f)
                                        {
                                            orbitClampSmoothedYaw = orbitShakeSuppressYaw;
                                            orbitClampSmoothedPitch = orbitShakeSuppressPitch;
                                            orbitClampSmoothedRoll = orbitShakeSuppressRoll;
                                            orbitShakeSuppressTimer -= deltaTime / 60.0f;
                                        }
                                        else
                                        {
                                            // Not currently suppressing - keep
                                            // the freeze buffer fresh so the
                                            // *next* press/release event
                                            // freezes at a value from just
                                            // before it, not a stale one from
                                            // several seconds ago.
                                            orbitShakeSuppressYaw = clampRelYaw;
                                            orbitShakeSuppressPitch = clampRelPitch;
                                            orbitShakeSuppressRoll = clampRelRoll;
                                        }
                                        orbitShakeSuppressInit = true;
                                    }
                                    else
                                    {
                                        orbitShakeSuppressInit = false;
                                        orbitShakeSuppressTimer = 0.0f;
                                    }
                                }

                                // "B"/"B (Ignore-X)" have no profile here
                                // (deviation is pinned to 0 above regardless,
                                // so these values don't matter there) - "C"
                                // uses whichever Normal-mode profile's
                                // keyword list matched this motion. Falls
                                // back to the shared Neck*/Range/Blend fields
                                // (used by the non-SimpleLock Rotation Joint
                                // mode) only if no profile matched, which
                                // shouldn't normally happen since orbitInC
                                // already implies a Normal-mode profile matched.

                                //ver10
                                OrbitProfile? matchedProfile = (orbitLastProfileIndex >= 0 && orbitLastProfileIndex < orbitProfiles.Count
                                    && orbitProfiles[orbitLastProfileIndex].Mode == OrbitProfileMode.Normal)
                                    ? orbitProfiles[orbitLastProfileIndex]
                                    : null;

                                // Gaze Keyframes は、モーション全体ではなく
                                // GazeRanges で指定した T 区間の間だけ有効にできる
                                // (Limit Gaze To Ranges)。区間の外では従来どおりの
                                // Range/Blend 追従に戻り、区間の境目は各区間の
                                // Blend 幅で Slerp クロスフェードされるので飛ばない。
                                // 例: 何回転もする攻撃で、振りかぶりと構え直しの
                                // 2 区間だけ Gaze で揺れを抑え、間の回転部分は
                                // そのまま完全追従に任せる、という使い分けができる。

                                //10.1
                                bool gazeAvailable = matchedProfile != null
                                    && matchedProfile.UseGazeKeyframes
                                    && matchedProfile.GazeKeyframes.Count > 0;
                                float gazeWeight = 0.0f;
                                Quaternion gazeRelative = Quaternion.Identity;
                                if (gazeAvailable)
                                {
                                    // GazeUseSubStateTimer が ON のプロファイルは、モーション
                                    // フレームではなくサブステート経過時間を T として使う
                                    // (getCurrentActionSubState / orbitSubStateElapsedSec 参照)。
                                    float motionProgress = matchedProfile!.GazeUseSubStateTimer
                                        ? getSubStateProgress(matchedProfile.GazeSubStateDuration)
                                        : getMotionProgress(player);
                                    gazeRelative = evaluateGazeKeyframes(matchedProfile.GazeKeyframes, motionProgress);
                                    gazeWeight = matchedProfile.GazeUseRanges
                                        ? evaluateGazeRangeWeight(matchedProfile.GazeRanges, motionProgress)
                                        : 1.0f;
                                }
                                orbitLastGazeWeight = gazeWeight;
                                //10.1ここまで

                                Quaternion clampBlendedRelative;
                                if (gazeAvailable && gazeWeight >= 0.9999f)
                                {
                                    // Gaze-keyframe path: the raw per-frame
                                    // deviation computed above
                                    // (orbitClampSmoothedYaw/Pitch/Roll) is not
                                    // used at all here - only how far the
                                    // motion has played matters, so a fast or
                                    // noisy weapon swing can never leak into
                                    // the camera. See evaluateGazeKeyframes().
                                    clampBlendedRelative = gazeRelative;

                                    // Keep the Deviation debug readout showing
                                    // something meaningful in this mode too.
                                    extractYawPitchRollDeg(clampBlendedRelative, out orbitLastRelYaw, out orbitLastRelPitch, out orbitLastRelRoll);
                                }
                                else
                                {
                                    float cYawRange = orbitNeckYawRange, cYawBlend = orbitNeckYawBlend;
                                    float cPitchRange = orbitNeckPitchRange, cPitchBlend = orbitNeckPitchBlend;
                                    float cRollRange = orbitNeckRollRange, cRollBlend = orbitNeckRollBlend;
                                    if (matchedProfile != null)
                                    {
                                        cYawRange = matchedProfile.YawRange;
                                        cYawBlend = matchedProfile.YawBlend;
                                        cPitchRange = matchedProfile.PitchRange;
                                        cPitchBlend = matchedProfile.PitchBlend;
                                        cRollRange = matchedProfile.RollRange;
                                        cRollBlend = matchedProfile.RollBlend;
                                    }
                                    float clampBlendedYaw = spotAxisDeg(orbitClampSmoothedYaw, cYawRange, cYawBlend);
                                    float clampBlendedPitch = spotAxisDeg(orbitClampSmoothedPitch, cPitchRange, cPitchBlend);
                                    float clampBlendedRoll = spotAxisDeg(orbitClampSmoothedRoll, cRollRange, cRollBlend);

                                    orbitLastRelYaw = orbitClampSmoothedYaw;
                                    orbitLastRelPitch = orbitClampSmoothedPitch;
                                    orbitLastRelRoll = orbitClampSmoothedRoll;

                                    Quaternion normalRelative = Quaternion.CreateFromYawPitchRoll(
                                        Single.DegreesToRadians(clampBlendedYaw),
                                        Single.DegreesToRadians(clampBlendedPitch),
                                        Single.DegreesToRadians(clampBlendedRoll));

                                    if (gazeAvailable && gazeWeight > 0.0001f)
                                    {
                                        // 区間の出入り口: 通常追従と Gaze を
                                        // 混ぜている最中。
                                        clampBlendedRelative = Quaternion.Slerp(normalRelative, gazeRelative, gazeWeight);
                                        extractYawPitchRollDeg(clampBlendedRelative, out orbitLastRelYaw, out orbitLastRelPitch, out orbitLastRelRoll);
                                    }
                                    else
                                    {
                                        clampBlendedRelative = normalRelative;
                                    }
                                }
                                rotation = (clampReferenceRotation * clampBlendedRelative) * clampFlip;
                                //ver10ここまで

                                if (orbitForceLevelRoll)
                                {
                                    // Re-level: keep the same forward direction
                                    // this rotation would produce, but rebuild
                                    // Up to be as close to true world-up as
                                    // possible - removing roll around the
                                    // forward axis entirely, regardless of what
                                    // clampBlendedRoll came out to.
                                    Vector3 levelForward = Vector3.Transform(new Vector3(0.0f, 0.0f, -1.0f), rotation);
                                    rotation = lookRotation(levelForward, new Vector3(0.0f, 1.0f, 0.0f)) * clampFlip;
                                }
                            }
                        }

                        // "A" (full-body rotation, clamp bypassed - or Face
                        // Clamp disabled entirely, which behaves like A
                        // everywhere) still gets the same speed-adaptive
                        // stabilization used by the non-SimpleLock Rotation
                        // Joint path below: raw joint *positions* carry small
                        // per-frame skeletal noise (idle sway, secondary
                        // motion) with nothing else filtering it out, which
                        // otherwise reads as constant fine shake. At low
                        // angular speed this smooths heavily; at high angular
                        // speed (dodge rolls, spin attacks) it tracks almost
                        // instantly, so legitimate fast motion isn't dulled.
                        bool orbitInFullRotationPath = !orbitFaceClampEnable || orbitLastIsFullRotation;
                        if (orbitInFullRotationPath && orbitStabilize)
                        {
                            // allowLevelRoll: false - "A" must keep the face's
                            // actual roll exactly as reconstructed. The
                            // "Re-level Roll When Slow" checkbox only applies
                            // to the non-SimpleLock call below.
                            rotation = stabilizeOrbitRotation(rotation, deltaTime, allowLevelRoll: false);
                        }
                        else
                        {
                            orbitStableInit = false;
                            orbitPrevRawInit = false;
                        }

                        //ver9 cameraYawを乗算する直前の"素のベース方向"をキャッシュ
                        // (Keepのフィードフォワード計算で使う。cameraYawより前の値
                        // なのでフィードバックループにならない)
                        // このすぐ下でclampFlip(X軸周り180度)がrotationに乗っており、
                        // これがcameraYawの効き方の符号を反転させる
                        // (world = base - cameraYaw。通常のworld = cameraYaw + base
                        // ではない)。orbitCachedBaseYawFlippedでそれをKeep側に伝える。
                        {
                            Vector3 orbitBaseForwardForKeep = Vector3.Transform(new Vector3(0.0f, 0.0f, 1.0f), rotation);
                            if (orbitBaseForwardForKeep.X * orbitBaseForwardForKeep.X + orbitBaseForwardForKeep.Z * orbitBaseForwardForKeep.Z > 0.0001f)
                            {
                                orbitCachedBaseYawDeg = Single.RadiansToDegrees(MathF.Atan2(orbitBaseForwardForKeep.X, orbitBaseForwardForKeep.Z));
                                orbitCachedBaseYawValid = true;
                                orbitCachedBaseYawFlipped = true;
                            }
                        }
                        //ver9ここまで

                        rotation *= Quaternion.CreateFromYawPitchRoll(
                            Single.DegreesToRadians(cameraYaw),
                            Single.DegreesToRadians(-cameraPitch),
                            Single.DegreesToRadians(cameraRoll));
                    }
                    else
                    {

                        // Rotation can come from a *different* joint than position. Some
                        // joints (e.g. face/head joints) don't carry the full-body swing
                        // used during dodge rolls or weapon spin attacks, while a body
                        // joint like the chest/spine does. This lets you take eye/head
                        // position from a face joint while still rotating like the body.
                        List<nint> rotJointList = orbitRotationJointUseFace ? faceJoints : bodyJoints;
                        bool hasRotationJoint = orbitRotationJoint >= 0 && orbitRotationJoint < rotJointList.Count;

                        // The "base" joint is a stable always-faces-forward reference
                        // (e.g. Body Joint 0). The camera normally locks onto this
                        // direction, like a head/eyes tracking straight ahead. The
                        // dynamic rotation joint (e.g. Body Joint 1) tracks rolls/spin
                        // attacks. We only follow it, per-axis, once that axis's
                        // deviation exceeds what a human neck could plausibly do on its
                        // own (orbitNeckYawRange / orbitNeckPitchRange / orbitNeckRollRange)
                        // - i.e. only when the base joint's "always forward" direction is
                        // no longer physically believable given how the body has spun.
                        // Below range we stay locked to the base joint on that axis;
                        // above it, we blend toward the dynamic joint's deviation over
                        // the matching *Blend width, capped by the matching *MaxFollow.
                        List<nint> baseJointList = orbitBaseRotationJointUseFace ? faceJoints : bodyJoints;
                        bool hasBaseJoint = orbitBaseRotationJoint >= 0 && orbitBaseRotationJoint < baseJointList.Count;

                        Quaternion baseRotation = new Quaternion(player.Rotation.X, player.Rotation.Y, player.Rotation.Z, player.Rotation.W);
                        if (hasBaseJoint)
                        {
                            nint baseJointAddr = baseJointList[orbitBaseRotationJoint];
                            baseRotation *= MemoryUtil.GetRef<Quaternion>(baseJointAddr + 0x70);
                        }

                        rotation = baseRotation;
                        if (hasRotationJoint)
                        {
                            nint rotJointAddr = rotJointList[orbitRotationJoint];
                            Quaternion dynamicWorldRotation = new Quaternion(player.Rotation.X, player.Rotation.Y, player.Rotation.Z, player.Rotation.W)
                                * MemoryUtil.GetRef<Quaternion>(rotJointAddr + 0x70);

                            // How the dynamic joint is oriented *relative to the base joint*,
                            // decomposed per-axis (yaw/pitch/roll) so each axis can have its
                            // own "human neck range" - e.g. a sideways barrel roll is mostly
                            // a ROLL deviation (small human range) while a forward somersault
                            // is mostly PITCH (larger human range), and pure turning is YAW.
                            Quaternion relative = Quaternion.Inverse(baseRotation) * dynamicWorldRotation;
                            extractYawPitchRollDeg(relative, out float relYaw, out float relPitch, out float relRoll);

                            float blendedYaw = blendAxisDeg(relYaw, orbitNeckYawRange, orbitNeckYawBlend, orbitNeckYawMaxFollow);
                            float blendedPitch = blendAxisDeg(relPitch, orbitNeckPitchRange, orbitNeckPitchBlend, orbitNeckPitchMaxFollow);
                            float blendedRoll = blendAxisDeg(relRoll, orbitNeckRollRange, orbitNeckRollBlend, orbitNeckRollMaxFollow);

                            orbitLastRelYaw = relYaw;
                            orbitLastRelPitch = relPitch;
                            orbitLastRelRoll = relRoll;

                            Quaternion blendedRelative = Quaternion.CreateFromYawPitchRoll(
                                Single.DegreesToRadians(blendedYaw),
                                Single.DegreesToRadians(blendedPitch),
                                Single.DegreesToRadians(blendedRoll));
                            rotation = baseRotation * blendedRelative;

                            // Corrects for a joint's rest-pose axes not lining up with the
                            // character's true forward direction (e.g. if Root/Body Joint 0
                            // ends up facing a fixed amount off from forward).
                            if (orbitRotationJointYawOffset != 0.0f || orbitRotationJointPitchOffset != 0.0f || orbitRotationJointRollOffset != 0.0f)
                            {
                                rotation *= Quaternion.CreateFromYawPitchRoll(
                                    Single.DegreesToRadians(orbitRotationJointYawOffset),
                                    Single.DegreesToRadians(orbitRotationJointPitchOffset),
                                    Single.DegreesToRadians(orbitRotationJointRollOffset));
                            }
                        }
                        //ver9 cameraYawを乗算する直前の"素のベース方向"をキャッシュ
                        // (プロファイルB/C経路。こちらはclampFlipのような反転が
                        // 挟まらないので、cameraYawの効き方は通常の足し算のまま)
                        {
                            Vector3 orbitBaseForwardForKeep = Vector3.Transform(new Vector3(0.0f, 0.0f, 1.0f), rotation);
                            if (orbitBaseForwardForKeep.X * orbitBaseForwardForKeep.X + orbitBaseForwardForKeep.Z * orbitBaseForwardForKeep.Z > 0.0001f)
                            {
                                orbitCachedBaseYawDeg = Single.RadiansToDegrees(MathF.Atan2(orbitBaseForwardForKeep.X, orbitBaseForwardForKeep.Z));
                                orbitCachedBaseYawValid = true;
                                orbitCachedBaseYawFlipped = false;
                            }
                        }
                        //ver9ここまで

                        // Manual "recenter" input snaps the free-look offset back to zero,
                        // replacing the base game's recenter button which this free camera
                        // otherwise bypasses (see orbitRecenterButton handling below).
                        rotation *= Quaternion.CreateFromYawPitchRoll(
                            Single.DegreesToRadians(cameraYaw),
                            Single.DegreesToRadians(-cameraPitch),
                            Single.DegreesToRadians(cameraRoll));

                        // The joint's own local rotation is also what drives legitimate large
                        // rotations (dodge rolls, greatsword/hammer spin attacks), so we can't
                        // just discard it. Instead, only filter out small-amplitude, high
                        // frequency noise (neck sway while walking, idle-stance skew) while
                        // still tracking large/fast rotations at full speed.
                        if (orbitStabilize)
                        {
                            rotation = stabilizeOrbitRotation(rotation, deltaTime);
                        }
                        else
                        {
                            orbitStableInit = false;
                            orbitPrevRawInit = false;
                        }
                    }
                }
                else
                {
                    target = player.Position;
                    rotation = new Quaternion(player.Rotation.X, player.Rotation.Y, player.Rotation.Z, player.Rotation.W);
                    orbitStableInit = false;
                    orbitPrevRawInit = false;
                    orbitFaceBasisPrevUpInit = false;
                }

                // Dedicated fixed-duration blend for the moment
                // orbitLastProfileIndex changes (see detection
                // above) - runs before the general speed cap below so a
                // profile switch always resolves in exactly
                // orbitCurrentTransitionDuration (the entered profile's own
                // Profile Switch Blend Time, snapshotted when the switch was
                // detected - see its assignment above) regardless of how big
                // the YPR jump is, rather than "longer for bigger jumps" like
                // the speed cap. The speed cap still runs afterward as a
                // second safety net for jumps from other causes (recenter,
                // etc.), same as before.
                //* ver追加
                float transitionAlpha = 1.0f;
                bool isTransitioning = false;
                if (orbitProfileTransitionActive)
                {
                    orbitProfileTransitionTimer += deltaTime;
                    if (orbitCurrentTransitionDuration <= 0.0001f || orbitProfileTransitionTimer >= orbitCurrentTransitionDuration)
                    {
                        orbitProfileTransitionActive = false;
                    }
                    else
                    {
                        isTransitioning = true;
                        transitionAlpha = Math.Clamp(orbitProfileTransitionTimer / orbitCurrentTransitionDuration, 0.0f, 1.0f);
                        transitionAlpha = transitionAlpha * transitionAlpha * (3.0f - 2.0f * transitionAlpha); // smoothstep
                        rotation = Quaternion.Slerp(orbitProfileTransitionStartRotation, rotation, transitionAlpha);
                    }
                }
                //*
                // See orbitFinalRotationMaxSpeed above - smooths out any
                // otherwise-instant jump in "rotation" (switching between
                // A/B/C, pressing recenter, etc.) without adding noticeable
                // lag to legitimate fast rotations.
                if (!orbitFinalRotationPrevInit)
                {
                    orbitFinalRotationPrev = rotation;
                    orbitFinalRotationPrevInit = true;
                }
                else if (orbitFinalRotationMaxSpeed > 0.0f)
                {
                    float rotDot = Math.Clamp(MathF.Abs(Quaternion.Dot(orbitFinalRotationPrev, rotation)), -1.0f, 1.0f);
                    float rotAngleDeg = Single.RadiansToDegrees(2.0f * MathF.Acos(rotDot));
                    float maxStepDeg = orbitFinalRotationMaxSpeed * deltaTime;
                    if (rotAngleDeg > maxStepDeg && rotAngleDeg > 0.0001f)
                    {
                        rotation = Quaternion.Slerp(orbitFinalRotationPrev, rotation, maxStepDeg / rotAngleDeg);
                    }
                    orbitFinalRotationPrev = rotation;
                }
                else
                {
                    orbitFinalRotationPrev = rotation;
                }

                // Cache this frame's final camera-forward yaw and the stable
                // body-forward yaw (same base-rotation cache used for the
                // clamp/spot logic above), so WritePadInputHook can, later
                // this same frame, counter-rotate the raw left-stick vector
                // by the gap between them. yaw here = atan2(forward.X,
                // forward.Z), matching how Quaternion.CreateFromYawPitchRoll
                // actually maps yaw to a forward vector in this runtime -
                // Transform(UnitZ, CreateFromYawPitchRoll(yaw,0,0)) works out
                // to (sin(yaw), ~0, cos(yaw)), not (cos(yaw), ~0, sin(yaw)).
                Vector3 finalCameraForward = Vector3.Transform(new Vector3(0.0f, 0.0f, 1.0f), rotation);
                if (finalCameraForward.X * finalCameraForward.X + finalCameraForward.Z * finalCameraForward.Z > 0.0001f)
                {
                    orbitCachedCameraForwardYawDeg = Single.RadiansToDegrees(MathF.Atan2(finalCameraForward.X, finalCameraForward.Z));
                    Vector3 stableBodyForward = Vector3.Transform(new Vector3(0.0f, 0.0f, 1.0f), orbitClampBaseRotationCache);
                    if (stableBodyForward.X * stableBodyForward.X + stableBodyForward.Z * stableBodyForward.Z > 0.0001f)
                    {
                        orbitCachedStableForwardYawDeg = Single.RadiansToDegrees(MathF.Atan2(stableBodyForward.X, stableBodyForward.Z));
                        orbitCachedForwardYawValid = true;
                    }
                }

                Quaternion offsetRotation = targetJoint ? offsetBasis : rotation;
                Vector3 up = Vector3.Transform(new Vector3(0.0f, 1.0f, 0.0f), offsetRotation);
                Vector3 right = Vector3.Transform(new Vector3(1.0f, 0.0f, 0.0f), offsetRotation);
                Vector3 forwardOffsetAxis = Vector3.Transform(new Vector3(0.0f, 0.0f, 1.0f), offsetRotation);
                orbitY += cameraFrame.Y;
                //ver11
                var (activeTargetY, activeTargetRight, activeTargetForward) = getActiveOrbitTargetOffset();
                //* ver2追加
                // XYZ Position ブレンド適用
                if (isTransitioning)
                {
                    activeTargetY = orbitProfileTransitionStartTargetY + (activeTargetY - orbitProfileTransitionStartTargetY) * transitionAlpha;
                    activeTargetRight = orbitProfileTransitionStartTargetRight + (activeTargetRight - orbitProfileTransitionStartTargetRight) * transitionAlpha;
                    activeTargetForward = orbitProfileTransitionStartTargetForward + (activeTargetForward - orbitProfileTransitionStartTargetForward) * transitionAlpha;
                }

                // 次フレームの参照用に、実際に適用されたオフセットを保存
                orbitPrevTargetY = activeTargetY;
                orbitPrevTargetRight = activeTargetRight;
                orbitPrevTargetForward = activeTargetForward;
                //*/

                // Use Gaze Keyframes の XYZ 版 (Use Gaze Position Keyframes)。
                // Target Y/Right/Forward を、Rotation側のGaze Keyframesと同じ T
                // (motionProgress、または GazeUseSubStateTimer が ON ならサブ
                // ステート経過時間) に沿って上書きする。GazeUseRanges が ON の
                // 場合は Rotation 側と共通の区間・Blend 設定で通常値との
                // クロスフェードにも対応する。
                if (orbitFaceClampEnable && orbitLastProfileIndex >= 0 && orbitLastProfileIndex < orbitProfiles.Count)
                {
                    OrbitProfile gazePositionProfile = orbitProfiles[orbitLastProfileIndex];
                    bool gazePositionAvailable = gazePositionProfile.Mode == OrbitProfileMode.Normal
                        && gazePositionProfile.UseGazePositionKeyframes
                        && gazePositionProfile.GazePositionKeyframes.Count > 0;
                    if (gazePositionAvailable)
                    {
                        float gazePositionProgress = gazePositionProfile.GazeUseSubStateTimer
                            ? getSubStateProgress(gazePositionProfile.GazeSubStateDuration)
                            : getMotionProgress(player);
                        var (gazeY, gazeRight, gazeForward) = evaluateGazePositionKeyframes(gazePositionProfile.GazePositionKeyframes, gazePositionProgress);
                        float gazePositionWeight = gazePositionProfile.GazeUseRanges
                            ? evaluateGazeRangeWeight(gazePositionProfile.GazeRanges, gazePositionProgress)
                            : 1.0f;
                        activeTargetY += (gazeY - activeTargetY) * gazePositionWeight;
                        activeTargetRight += (gazeRight - activeTargetRight) * gazePositionWeight;
                        activeTargetForward += (gazeForward - activeTargetForward) * gazePositionWeight;
                    }
                }

                target += activeTargetY * up;
                target.X += activeTargetRight * right.X;
                target.Z += activeTargetRight * right.Z;
                target += activeTargetForward * forwardOffsetAxis;
                //ver11ここまで
                Vector3 position;
                float dist = orbitDistance;
                if (targetJoint)
                {
                    // IMPORTANT: the camera is set via Position + Target further down
                    // (a look-at style camera), so the vector from position to target
                    // *is* the actual viewing direction. The eye location itself
                    // (position) must stay exactly anchored to the joint (+ Target
                    // Y/Right offset above) regardless of aim direction - so instead
                    // of moving the eye backward along "rotation" (which would drag
                    // the eye away from the joint whenever rotation differs from the
                    // joint's own orientation, e.g. a hip-driven Simple Rotation
                    // Joint), we keep the eye fixed and push the *look-at point*
                    // forward from it along "rotation" instead.
                    Vector3 eyeAnchor = target;
                    if (orbitFaceClampEnable && orbitLastBaseOnlyIgnoreX && bodyJoints.Count > 0)
                    {
                        // The nose (Face Joint) position still shifts with
                        // head-tracking even once its *rotation* is locked out
                        // by "B" (see the Simple Lock clamp block above) - but
                        // that shift is specifically a left/right (local
                        // "right" axis) thing, since head-tracking is turning
                        // the head to look at something to the side. Forward
                        // lean while walking/running is a genuine, wanted
                        // up/forward shift, not head-tracking - so decompose
                        // the nose's offset from the hip into local
                        // right/up/forward and keep only up+forward.
                        Vector3 hipWorldPos = MemoryUtil.GetRef<Vector3>(bodyJoints[0] + 0x50);
                        Vector3 noseOffsetFromHip = target - hipWorldPos;
                        Vector3 baseUp = Vector3.Transform(new Vector3(0.0f, 1.0f, 0.0f), orbitClampBaseRotationCache);
                        Vector3 baseForward = Vector3.Transform(new Vector3(0.0f, 0.0f, 1.0f), orbitClampBaseRotationCache);
                        float upComponent = Vector3.Dot(noseOffsetFromHip, baseUp);
                        float forwardComponent = Vector3.Dot(noseOffsetFromHip, baseForward);
                        eyeAnchor = hipWorldPos + baseUp * upComponent + baseForward * forwardComponent;
                    }
                    Vector3 forward = Vector3.Transform(new Vector3(0.0f, 0.0f, -1.0f), rotation);
                    position = eyeAnchor;
                    target = eyeAnchor + forward * dist;

                    // Derive Up from this exact same "rotation" quaternion that
                    // produced "forward" above, so Target/Up stay physically
                    // consistent through a full pitch (front flip) instead of
                    // Up being pinned to world-up by setCameraRoll() while
                    // forward loops through a somersault - see setCameraRoll().
                    orbitFaceLockedUpRaw = Vector3.Transform(new Vector3(0.0f, -1.0f, 0.0f), rotation);
                    orbitFaceLockedUpActive = true;
                }
                else
                {
                    position.X = target.X - dist * MathF.Cos(Single.DegreesToRadians(cameraPitch)) * MathF.Cos(Single.DegreesToRadians(cameraYaw));
                    position.Y = target.Y - dist * MathF.Sin(Single.DegreesToRadians(cameraPitch));
                    position.Z = target.Z - dist * MathF.Cos(Single.DegreesToRadians(cameraPitch)) * MathF.Sin(Single.DegreesToRadians(cameraYaw));
                    orbitFaceLockedUpActive = false;
                }
                if (orbitLerp != 1.0f)
                {
                    cameraTarget = Vector3.Lerp(cameraTarget, target, orbitLerp);
                    cameraPosition = Vector3.Lerp(cameraPosition, position, orbitLerp);
                    // Match the same linear-blend smoothing used for
                    // Position/Target above, so Up doesn't snap independently
                    // of the (smoothed) forward direction it needs to match.
                    orbitFaceLockedUpSmoothed = orbitFaceLockedUpActive
                        ? safeNormalize(Vector3.Lerp(orbitFaceLockedUpSmoothed, orbitFaceLockedUpRaw, orbitLerp), orbitFaceLockedUpRaw)
                        : orbitFaceLockedUpRaw;
                }
                else
                {
                    cameraTarget = target;
                    cameraPosition = position;
                    orbitFaceLockedUpSmoothed = orbitFaceLockedUpRaw;
                }
            }
            else
            {
                cameraPosition += cameraFrame;
                // 700.0 is the same value the game uses for the player camera. The in-game view mode
                // uses 1.0 like I did here before, which is really bad for precision.
                float dist = 700.0f - cameraForward;
                cameraTarget.X = cameraPosition.X + dist * MathF.Cos(Single.DegreesToRadians(cameraPitch)) * MathF.Cos(Single.DegreesToRadians(cameraYaw));
                cameraTarget.Y = cameraPosition.Y + dist * MathF.Sin(Single.DegreesToRadians(cameraPitch));
                cameraTarget.Z = cameraPosition.Z + dist * MathF.Cos(Single.DegreesToRadians(cameraPitch)) * MathF.Sin(Single.DegreesToRadians(cameraYaw));
                orbitFaceLockedUpActive = false;
            }

            // See orbitFinalPositionMaxSpeed above - smooths out an
            // otherwise-instant jump in cameraPosition (eyeAnchor's source
            // changing between raw nose / hip-projected, A/B/C switching,
            // etc.), independent of orbitLerp (which may be set to 1.0/
            // instant for normal responsiveness).
            if (!orbitFinalPositionPrevInit)
            {
                orbitFinalPositionPrev = cameraPosition;
                orbitFinalPositionPrevInit = true;
            }
            else if (orbitFinalPositionMaxSpeed > 0.0f)
            {
                Vector3 positionDelta = cameraPosition - orbitFinalPositionPrev;
                float positionDistance = positionDelta.Length();
                float maxStepDistance = orbitFinalPositionMaxSpeed * deltaTime;
                if (positionDistance > maxStepDistance && positionDistance > 0.0001f)
                {
                    Vector3 clampedPosition = orbitFinalPositionPrev + positionDelta * (maxStepDistance / positionDistance);
                    cameraTarget += clampedPosition - cameraPosition;
                    cameraPosition = clampedPosition;
                }
                orbitFinalPositionPrev = cameraPosition;
            }
            else
            {
                orbitFinalPositionPrev = cameraPosition;
            }

            // Experimental: while OrbitProfile.UseNativeAimCameraDelaySeconds
            // is still counting down (see orbitNativeAimCameraDelayTimer/Total
            // and the L2 handling above), pre-blend this mod's own reconstructed
            // camera's *pitch* toward the native camera's pitch, so it already
            // matches by the time the hard switch-over just below kicks in.
            //
            // Why only pitch: the delay exists to wait out the native camera's
            // own ~0.25s swing from wherever it was stale-parked to the
            // player's actual facing (see the comment on
            // OrbitProfile.UseNativeAimCameraDelaySeconds near the L2
            // handling above). With the base game's "Aim Direction" set to
            // "Player Facing", that swing settles on the player's yaw - which
            // is also what this mod's own reconstruction already tracks, so
            // yaw already agrees on both sides and never pops. The pitch
            // portion of "Player Facing" is left at whatever the native
            // camera's pitch happened to be, which has no reason to match
            // this mod's own pitch, so that's the only component that jumps
            // without this blend. Roll isn't touched here for the same reason
            // it isn't touched by the switch-over below: this only ever
            // adjusts Target relative to the already-computed (stable)
            // cameraPosition, and roll isn't encoded in that vector.
            if (orbitFaceClampEnable && orbitNativeCameraValid
                && orbitLastProfileIndex >= 0 && orbitLastProfileIndex < orbitProfiles.Count
                && orbitProfiles[orbitLastProfileIndex].Mode == OrbitProfileMode.Normal
                && orbitProfiles[orbitLastProfileIndex].UseNativeAimCamera
                && buttonWasDown(Button.L2) && orbitNativeAimCameraDelayTimer > 0.0f)
            {
                Vector3 nativeDir = orbitNativeCameraTarget - orbitNativeCameraPosition;
                if (nativeDir.LengthSquared() > 0.0001f)
                {
                    nativeDir = Vector3.Normalize(nativeDir);

                    Vector3 ownDirVec = cameraTarget - cameraPosition;
                    float ownDist = ownDirVec.Length();
                    if (ownDist < 0.01f)
                    {
                        ownDist = 0.01f;
                    }
                    Vector3 ownDir = ownDirVec / ownDist;

                    // Same yaw/pitch convention used to build cameraTarget
                    // from cameraYaw/cameraPitch elsewhere in this file:
                    // pitch = asin(forward.Y), yaw = atan2(forward.Z, forward.X).
                    float ownYawRad = MathF.Atan2(ownDir.Z, ownDir.X);
                    float ownPitchDeg = Single.RadiansToDegrees(MathF.Asin(Math.Clamp(ownDir.Y, -1.0f, 1.0f)));
                    float nativePitchDeg = Single.RadiansToDegrees(MathF.Asin(Math.Clamp(nativeDir.Y, -1.0f, 1.0f)));

                    // 0 at the instant the countdown (re)started, 1 the
                    // instant it reaches zero - i.e. exactly when the hard
                    // switch-over below takes over, so the two meet with a
                    // matching pitch instead of a visible pop.
                    float progress = orbitNativeAimCameraDelayTotal > 0.0001f
                        ? Math.Clamp(1.0f - (orbitNativeAimCameraDelayTimer / orbitNativeAimCameraDelayTotal), 0.0f, 1.0f)
                        : 1.0f;

                    float blendedPitchDeg = ownPitchDeg + (nativePitchDeg - ownPitchDeg) * progress;
                    float blendedPitchRad = Single.DegreesToRadians(blendedPitchDeg);
                    float horizontalScale = MathF.Cos(blendedPitchRad);

                    Vector3 blendedDir = new Vector3(
                        MathF.Cos(ownYawRad) * horizontalScale,
                        MathF.Sin(blendedPitchRad),
                        MathF.Sin(ownYawRad) * horizontalScale);

                    cameraTarget = cameraPosition + blendedDir * ownDist;
                }
            }

            // Experimental: let a specific "C" profile (e.g. AIM_IDLE) use
            // the game's own native camera's *direction* (only) instead of
            // this mod's own joint+clamp+R-stick reconstruction - see
            // OrbitProfile.UseNativeAimCamera and
            // orbitNativeCameraPosition/Target above. Deliberately kept as
            // the very last step, after every other adjustment above.
            //
            // NOTE: originally this replaced position AND direction
            // wholesale, but the native camera's *position* gets pulled
            // around independently for reasons unrelated to aim (terrain/
            // wall avoidance etc.), so it doesn't reliably stay anchored at
            // the nose the way this mod's own reconstruction does - using it
            // caused a large, unwanted position drift away from the body.
            // Using only the *direction* (Target - Position, normalized)
            // avoids that: it's re-aimed from this mod's own already-computed
            // (stable) cameraPosition instead of replacing that position too.
            if (orbitFaceClampEnable && orbitNativeCameraValid
                && orbitLastProfileIndex >= 0 && orbitLastProfileIndex < orbitProfiles.Count
                && orbitProfiles[orbitLastProfileIndex].Mode == OrbitProfileMode.Normal
                && orbitProfiles[orbitLastProfileIndex].UseNativeAimCamera
                && buttonWasDown(Button.L2) && orbitNativeAimCameraDelayTimer <= 0.0f)
            {
                Vector3 nativeDir = orbitNativeCameraTarget - orbitNativeCameraPosition;
                if (nativeDir.LengthSquared() > 0.0001f)
                {
                    float ownDist = Vector3.Distance(cameraPosition, cameraTarget);
                    if (ownDist < 0.01f)
                    {
                        ownDist = 0.01f;
                    }
                    cameraTarget = cameraPosition + Vector3.Normalize(nativeDir) * ownDist;
                }
            }

            camera.Position = cameraPosition;
            camera.Target = cameraTarget;
            camera.FieldOfView = cameraFov;
        }

        private void SetCameraHook(nint cameraPointer)
        {
            if (disableMod)
            {
                setCameraHook!.Original(cameraPointer);
                return;
            }

#if HOOK_ORDER_ASSERTS
            //debugLog($"SetCameraHook(0x{cameraPointer:X}) @ {frameTick}");
            Assert(hookOrder == 0);
            hookOrder = 1;
#endif

            Player? player = checkPlayerChange();
            checkCurrentVisibleCamera(player);
            checkCameraAnimState();

            // CalculateCameraHook() is within this function.
            setCameraHook!.Original(cameraPointer);

#if HOOK_ORDER_ASSERTS
            Assert(hookOrder == 3);
            hookOrder = 4;
#endif

            // --- フレーム中に別箇所で NearClip が書かれてしまう可能性に対応 ---
            if (vCamera != null)
            {
                vCamera.NearClip = 1.0f;
            }

            if (applyPerspective)
            {
                setCameraRoll(pCamera!);
            }

            if (freeCamera && vCamera != null)
            {
                setCameraRoll(vCamera);
                if (!enableFreeCamera)
                {
                    // Disable free camera only after applying all possible
                    // offsets for this frame. This is to hopefully avoid any
                    // visual jumps.
                    disableFreeCamera();
                }
            }
        }

        private static bool assumeInQuestBoard(float fov)
        {
            return fov == 20.000038f || fov == 20.017189f || fov == 20.00004f;
        }

        // If camera.Move = false, this hook won't run.
        private void CalculateCameraHook(nint cameraPointer)
        {
            if (disableMod)
            {
                calculateCameraHook!.Original(cameraPointer);
                return;
            }

#if HOOK_ORDER_ASSERTS
            //debugLog($"CalculateCameraHook(0x{cameraPointer:X}) @ {frameTick}");
            Assert(hookOrder == 2);
            hookOrder = 3;
#endif

            if (enableOffsetPerspective && !offsetPerspective)
            {
                offsetPerspective = true;
            }
            else if (!enableOffsetPerspective && offsetPerspective)
            {
                offsetPerspective = false;
            }

            applyPerspective = offsetPerspective && !freeCamera && pCamera != null;
            if (applyPerspective)
            {
                previousFov = pCamera!.FieldOfView; // Pre-offset FOV value.
            }
            bool perspectiveFovOnly = false;
            if (applyPerspective)
            {
                applyPerspective &= !assumeInQuestBoard(previousFov);
                perspectiveFovOnly = applyPerspective;
                applyPerspective &= (previousCameraAnimState != 5 || ignoreAnimState) && previousCameraAnimState != 4 && previousCameraAnimState != 8;
            }

            if (applyPerspective)
            {
                // Applying Y offset here keeps crosshair UI centered, but affects the angle of the camera.
                pCamera!.Position.Y += cameraUp;
            }

            if (perspectiveFovOnly && cameraFov != DEFAULT_FOV)
            {
                pCamera!.FieldOfView = Math.Clamp((cameraFov * previousFov) / DEFAULT_FOV, 1.0f, 179.0f);
            }

            calculateCameraHook!.Original(cameraPointer);

            if (vCamera != null)
            {
                // See orbitNativeCameraPosition/Target declaration above -
                // must capture before setupFreeCamera()/updateFreeCamera()
                // below can overwrite vCamera with this mod's own camera.
                orbitNativeCameraPosition = vCamera.Position;
                orbitNativeCameraTarget = vCamera.Target;
                orbitNativeCameraValid = true;
            }

            // Setting the perspective after calculateCameraHook.Original() allows the offset to not
            // negatively affect the right stick camera movement. If set earlier, camera movement
            // would be too snappy and incorrectly smoothed. If set later, it may not be
            // considered by lower-level functions like culling.
            if (applyPerspective)
            {
                setPerspective(pCamera!);
            }

            if (vCamera != null)
            {
                if (!freeCamera)
                {
                    // We need pitch and yaw to apply cameraRoll and avoid a jump if applyPerspective = false.
                    // @TODO: These values could just be read from the in-game camera.
                    Quaternion forward = Quaternion.Normalize(getForward(vCamera.Position, vCamera.Target));
                    cameraYaw = Single.RadiansToDegrees(MathF.Atan2(forward.Z, forward.X));
                    cameraPitch = Single.RadiansToDegrees(MathF.Asin(forward.Y));
                    if (enableFreeCamera && getPlayer() != null)
                    {
                        setupFreeCamera(vCamera, vCameraViewportIndex);
                    }
                }
                // No else because we want to updateFreeCamera() if freeCamera was enabled above.
                if (freeCamera)
                {
                    updateFreeCamera(vCamera);
                }
            }

            freeCameraFallback = false;
        }

        private void CheckCameraHook(nint cameraPointer)
        {
            if (disableMod)
            {
                checkCameraHook!.Original(cameraPointer);
                return;
            }

#if HOOK_ORDER_ASSERTS
            //debugLog($"CheckCameraHook(0x{cameraPointer:X}) @ {frameTick}");
            Assert(hookOrder == 1);
            hookOrder = 2;
#endif

            /*
            if (vCamera != null && freeCamera)
            {
                updateFreeCamera(vCamera);

                MemoryUtil.GetRef<Vector3>(psuedoViewModeObject + 0xE0) = cameraFrame;

                processViewMode.Invoke(psuedoViewModeObject);

                cameraPosition = MemoryUtil.GetRef<Vector3>(psuedoViewModeObject + 0x90);
            }
            */

            if (!freeCamera)
            {
                checkCameraHook!.Original(cameraPointer);
            }
        }

        private void StartViewModeHook(nint viewModeObject)
        {
            if (disableMod || !overrideViewMode)
            {
                startViewModeHook!.Original(viewModeObject);
                return;
            }

#if HOOK_ORDER_ASSERTS
            //debugLog($"StartViewMode(0x{viewModeObject:X}) @ {frameTick}");
#endif

            if (vCamera != null)
            {
                enableFreeCamera = true;
                freeCameraFromViewMode = true;
            }
        }

        private void SetCameraTentHook(nint unknownPtr)
        {
            if (disableMod)
            {
                setCameraTentHook!.Original(unknownPtr);
                return;
            }

#if HOOK_ORDER_ASSERTS
            // The order of this hook between SetCameraHook() and CalculateCameraHook() is inconsistent.
            //debugLog($"SetCameraTentHook(0x{unknownPtr:X}) @ {frameTick}");
#endif

            if (freeCamera && vCamera != null)
            {
                setTentBasePos(cameraPosition, cameraTarget);
            }

            setCameraTentHook!.Original(unknownPtr);

            if (freeCamera && vCamera != null)
            {
                vCamera.Position = cameraPosition;
                vCamera.Target = cameraTarget;
                vCamera.FieldOfView = cameraFov;

                // NearClip を再適用（テント内等で上書きされる対策）
                vCamera.NearClip = 1.0f;

                setCameraRoll(vCamera);
            }

            freeCameraFallback = false;
        }

        private void SetCameraCutsceneHook(nint unknownPtr)
        {
            if (disableMod)
            {
                setCameraCutsceneHook!.Original(unknownPtr);
                return;
            }

#if HOOK_ORDER_ASSERTS
            //debugLog($"SetCameraCutsceneHook(0x{unknownPtr:X}) @ {frameTick}");
#endif

            setCameraCutsceneHook!.Original(unknownPtr);

            if (freeCamera && vCamera != null)
            {
                vCamera.Position = cameraPosition;
                vCamera.Target = cameraTarget;
                vCamera.FieldOfView = cameraFov;
                setCameraRoll(vCamera);
            }

            freeCameraFallback = false;
            freeCameraNoMenu = true;
        }

        /*
        private void CalculateViewHook(nint unknownPtr)
        {
            calculateViewHook!.Original(unknownPtr);

            if (disableMod)
            {
                return;
            }

            if (vCameraViewportIndex >= 0 && cameraRoll != 0.0f)
            {
                Viewport vp = CameraSystem.GetViewport(vCameraViewportIndex);
                vp.ViewMatrix *= Matrix4x4.CreateRotationZ(Single.DegreesToRadians(cameraRoll));
            }
        }
        */

        private void toggleFreezeGame(float resumeSpeed = 1.0f)
        {
            freezeGame = !freezeGame;
            if (freezeGame)
            {
                decoupleDtFromGameTime = true;
                if (forceOffMotionBlurOverride == 0)
                {
                    forceOffMotionBlurOverride = TuningToolInterop.AddOverride("Shutter Speed", new Vector4(0.0f), 0);
                }
            }
            else
            {
                decoupleDtFromGameTime = false;
                if (forceOffMotionBlurOverride != 0)
                {
                    TuningToolInterop.RemoveOverride(forceOffMotionBlurOverride);
                    forceOffMotionBlurOverride = 0;
                }
            }
            MemoryUtil.GetRef<float>(sMain.Instance + 0xA4) = freezeGame ? 0.0f : resumeSpeed;
        }

        private bool playerInMenu()
        {
            // This doesn't align with in-game mouse controls. This can be observed when closing the pause
            // menu where there is a brief moment the camera moves -> freezes again -> then starts normally.
            return !freeCameraNoMenu && MemoryUtil.GetRef<byte>(Gui.SingletonInstance.Instance + 0x147A8) == 0x1;
        }

        // This can undoubtedly be simplified.
        private void WritePadInputHook(nint unknownPtr, nint unknownPtr2, nint unknownPtr3)
        {
            // Assume the primary pad always comes first.
            if (primaryPad == 0x0)
            {
                primaryPad = unknownPtr2;
            }

            if (disableMod || unknownPtr2 != primaryPad)
            {
                writePadInputHook!.Original(unknownPtr, unknownPtr2, unknownPtr3);
                return;
            }

#if HOOK_ORDER_ASSERTS
            //debugLog($"WritePadInputHook(0x{unknownPtr:X}, 0x{unknownPtr2:X}, 0x{unknownPtr3:X}) @ {frameTick}");
#endif

            Button b1 = 0u, b2 = 0u;
            uint b1u = 0u;
            if (enableCombo && freeCameraCombo != null)
            {
                b1 = freeCameraCombo[0];
                b2 = freeCameraCombo[1];
                b1u = (uint)b1;
                if (disableComboButton1 && comboButton1Down)
                {
                    MemoryUtil.GetRef<uint>(sMhController.Instance + 0x198) |= b1u;
                }
            }

            writePadInputHook!.Original(unknownPtr, unknownPtr2, unknownPtr3);

            uint PadDown = MemoryUtil.Read<uint>(sMhController.Instance + 0x198);
            uint PadRel = MemoryUtil.Read<uint>(sMhController.Instance + 0x1A4);
            prevPadDown = (prevPadDown == null) ? PadDown : lastPadDown;
            lastPadDown = PadDown;

            PadLx = MemoryUtil.Read<int>(sMhController.Instance + 0x1B8);
            PadLy = MemoryUtil.Read<int>(sMhController.Instance + 0x1BC);
            PadRx = MemoryUtil.Read<int>(sMhController.Instance + 0x1B0);
            PadRy = MemoryUtil.Read<int>(sMhController.Instance + 0x1B4);

            // Snap left-stick angle to straight-up (12 o'clock) when within
            // +/-30deg (11 o'clock to 1 o'clock). Written back to the actual
            // pad memory, not just the local PadLx copy, so the game's own
            // native movement/turn logic sees the snap too.
            if (PadLx != 0 || PadLy != 0)
            {
                float stickAngleDeg = Single.RadiansToDegrees(MathF.Atan2(PadLx, PadLy));
                if (stickAngleDeg >= -orbitStickSnapAngleDeg && stickAngleDeg <= orbitStickSnapAngleDeg)
                {
                    PadLx = 0;
                    MemoryUtil.GetRef<int>(sMhController.Instance + 0x1B8) = 0;
                }
            }

            blockRightStickLookDuringL1 = enableFreeCamera && buttonWasDown(Button.L1);//L1押下中カメラロック

            if (enableCombo && freeCameraCombo != null)
            {
                if (disableComboButton1)
                {
                    if (comboButton1Down || enableFreeCamera || !buttonWasDown(b2))
                    {
                        if ((PadDown & b1u) == b1u)
                        {
                            comboButton1Down = true;
                        }
                        MemoryUtil.GetRef<uint>(sMhController.Instance + 0x198) &= ~b1u;
                    }

                    if (comboButton1Down)
                    {
                        if (((PadRel & b1u) == b1u))
                        {
                            comboButton1Down = false;
                        }
                        MemoryUtil.GetRef<uint>(sMhController.Instance + 0x1A0) &= ~b1u;
                        MemoryUtil.GetRef<uint>(sMhController.Instance + 0x1A4) &= ~b1u;
                        MemoryUtil.GetRef<uint>(sMhController.Instance + 0x1A8) &= ~b1u;
                    }
                }
            }

            if ((comboButton1Down || (!disableComboButton1 && buttonWasDown(b1))))
            {
                // Disabled: all Button1-combo binds turned off per user request
                // (misfire prevention). Use the ImGui checkboxes/UI instead.
                //if (buttonWasPressed(b2))
                //{
                //    enableFreeCamera = !enableFreeCamera;
                //}
                //if (buttonWasPressed(Button.Share))
                //{
                //    toggleUi();
                //}
                // Disabled: this collided with L1 being used elsewhere (Orbital
                // Camera recenter, in-game Guard, etc.), silently flipping
                // "Unlock Input" during normal play. Toggle it from the
                // checkbox in the menu instead.
                //if (buttonWasPressed(Button.L1))
                //{
                //    unlockInputToggled = !unlockInputToggled;
                //}
                int presetSelect = 0; // Disabled (was: Button1 + D-Pad Up/Down).
                if (presetSelect != 0)
                {
                    Config config = ConfigManager.GetConfig<Config>(this);
                    Dictionary<string, Config.Preset>.KeyCollection presetKeys = config.Presets.Keys;
                    if (presetKeys.Count > 0)
                    {
                        if (config.Selected != "")
                        {
                            int selectedIndex;
                            for (selectedIndex = 0; selectedIndex < presetKeys.Count; selectedIndex++)
                            {
                                string presetKey = presetKeys.ElementAt(selectedIndex);
                                if (presetKey == config.Selected)
                                {
                                    break;
                                }
                            }
                            selectedIndex += presetSelect;
                            if (selectedIndex >= presetKeys.Count)
                            {
                                selectedIndex -= presetKeys.Count;
                            }
                            else if (selectedIndex < 0)
                            {
                                selectedIndex += presetKeys.Count;
                            }
                            config.Selected = presetKeys.ElementAt(selectedIndex);
                        }
                        else
                        {
                            config.Selected = presetKeys.ElementAt((presetSelect == 1) ? 0 : presetKeys.Count - 1);
                        }
                        setPerspectivePreset(config.Presets[config.Selected]);
                        ConfigManager.SaveConfig<Config>(this);
                    }
                }
            }

            if (unlockMovementHeld && buttonWasReleased(Button.R2))
            {
                unlockMovementHeld = false;
                unlockMovementPause = false;
            }

            bool blockInput = comboButton1Down || (enableFreeCamera && !unlockInputToggled);

            if (blockInput)
            {
                uint Mask = 0u;
                uint StartSelectMask = (uint)Button.Options | (uint)Button.Share;
                uint DPadMask = (uint)Button.Up | (uint)Button.Down | (uint)Button.Left | (uint)Button.Right;
                uint BumperMask = (uint)Button.L1 | (uint)Button.R1;
                uint StickMask = (uint)Button.LsUp | (uint)Button.LsDown | (uint)Button.LsLeft | (uint)Button.LsRight |
                    (uint)Button.RsUp | (uint)Button.RsDown | (uint)Button.RsLeft | (uint)Button.RsRight;
                // @TODO: This is broken if you mix keyboard/mouse and controller input.
                if (unlockInputForMenu)
                {
                    // Idealy we wouldn't block Y or X here but triggering Rearrange or Edit Shoutout in
                    // the gesture menu can desync our primitive "still in menu" handling when B is pressed.
                    uint FaceButtonMask = (uint)Button.Triangle | (uint)Button.Square;
                    Mask = FaceButtonMask | StartSelectMask;
                    if (unlockInputHideMenu)
                    {
                        Mask |= (uint)Button.Circle; // Still allow A/Cross.
                        Mask |= DPadMask | BumperMask | StickMask;
                    }
                    else if (buttonWasReleased(Button.Circle))
                    {
                        unlockInputForMenu = false;
                        unlockInputHideMenu = false;
                    }
                }
                else
                {
                    uint FaceButtonMask = (uint)Button.Cross | (uint)Button.Circle | (uint)Button.Square | (uint)Button.Triangle;
                    Mask = FaceButtonMask | StartSelectMask | DPadMask | BumperMask | StickMask;
                }
                if (enableCombo && freeCameraCombo != null)
                {
                    Mask &= ~(b1u | (uint)b2);
                }
                MemoryUtil.GetRef<uint>(sMhController.Instance + 0x198) &= ~Mask;
                MemoryUtil.GetRef<uint>(sMhController.Instance + 0x1A0) &= ~Mask;
                MemoryUtil.GetRef<uint>(sMhController.Instance + 0x1A8) &= ~Mask;
                MemoryUtil.GetRef<uint>(sMhController.Instance + 0x1AC) &= ~Mask;
                MemoryUtil.GetRef<int>(sMhController.Instance + 0x1C0) = 0; // Left trigger.
                MemoryUtil.GetRef<int>(sMhController.Instance + 0x1C1) = 0; // Right trigger.
                MemoryUtil.GetRef<int>(sMhController.Instance + 0x1B0) = 0; // Rx.
                MemoryUtil.GetRef<int>(sMhController.Instance + 0x1B4) = 0; // Ry.
            }

            if (enableFreeCamera)
            {
                if (playerMovementLocked)
                {
                    MemoryUtil.GetRef<int>(sMhController.Instance + 0x1B8) = 0; // Lx.
                    MemoryUtil.GetRef<int>(sMhController.Instance + 0x1BC) = 0; // Ly.
                }

                if (orbitPlayer)
                {
                    // Keep L-stick movement locked to the stable A/B/C body
                    // direction even while R-stick free-look has turned the
                    // view away from it. Rotates the raw stick vector itself
                    // (not the camera object) by the gap between the actual
                    // camera-forward and the stable body-forward, so the
                    // game's own camera-relative movement math - reading the
                    // real, head-turned camera - ends up moving the body
                    // exactly as if the camera were still centered. This is
                    // independent of "Ignore Camera Direction"
                    // (orbitIgnoreCamera / CheckMovementHook) above, which is
                    // left untouched but unused here.
                    // Suspended while aiming (L2 held, slinger/bow): while
                    // actively aiming, you want ordinary camera-relative
                    // strafe - moving relative to where you're looking/aiming,
                    // not locked to body-only facing - same as vanilla and
                    // same as this mod without Decouple enabled. Keeping the
                    // correction active here was very likely why "shoot while
                    // running" (Common::IDLE) regressed: it was fighting the
                    // aim-direction camera instead of just letting movement
                    // follow it like normal.
                    if (orbitDecoupleMovementFromLook && orbitCachedForwardYawValid && !playerMovementLocked && !buttonWasDown(Button.L2))
                    {
                        float correctionDeg = orbitCachedStableForwardYawDeg - orbitCachedCameraForwardYawDeg;
                        if (orbitDecoupleMovementInvert)
                        {
                            correctionDeg = -correctionDeg;
                        }
                        if (MathF.Abs(correctionDeg) > 0.01f)
                        {
                            float correctionRad = Single.DegreesToRadians(correctionDeg);
                            float cosC = MathF.Cos(correctionRad);
                            float sinC = MathF.Sin(correctionRad);
                            float rawLx = PadLx;
                            float rawLy = PadLy;
                            // Same rotation form as the yaw convention above
                            // (Z acts as the "cos axis", X as the "sin
                            // axis") applied directly to the stick's own
                            // (Lx, Ly) pair - a pure rotation of that pair,
                            // so it's correct regardless of what "Ly
                            // positive" happens to mean on this controller.
                            float correctedLy = rawLy * cosC - rawLx * sinC;
                            float correctedLx = rawLy * sinC + rawLx * cosC;
                            MemoryUtil.GetRef<int>(sMhController.Instance + 0x1B8) = (int)Math.Clamp(correctedLx, -32768.0f, 32767.0f);
                            MemoryUtil.GetRef<int>(sMhController.Instance + 0x1BC) = (int)Math.Clamp(correctedLy, -32768.0f, 32767.0f);
                        }
                    }

                    if (plusRight != 0.0f && !playerMovementLocked && !orbitIgnoreCamera)
                    {
                        int Lx = (int)(Int16.MaxValue * Math.Clamp(plusRight, -1.0f, 1.0f));
                        MemoryUtil.GetRef<int>(sMhController.Instance + 0x1B8) = Lx;
                    }
                    if (plusForward != 0.0f)
                    {
                        int Ly = (int)(Int16.MaxValue * Math.Clamp(plusForward, -1.0f, 1.0f));
                        MemoryUtil.GetRef<int>(sMhController.Instance + 0x1BC) = Ly;
                    }
                }

                if (!unlockMovementHeld && buttonWasPressed(Button.R2))
                {
                    unlockMovementHeld = true;
                }

                // Removed: this used to force playerMovementLocked (zeroing
                // Lx/Ly in WritePadInputHook, see above) while L2 was held
                // during unlockMovementHeld, to protect the old "Hold LT+RT:
                // Zoom with Left Stick" bind (see the fully-disabled
                // cameraFov block a few hundred lines up) from also moving
                // the player. That zoom bind is dead code now, but this
                // guard was still firing on every real L2(aim)+R2(draw) bow
                // shot, silently zeroing left-stick movement mid-draw for no
                // remaining reason - removed rather than left in as a
                // trap. If a future "zoom with left stick" rework wants this
                // back, it should re-add its own pause, not share this one.

                // Disabled: "Hold LT + Press LB" toggle, per user request.
                //if (buttonWasDown(Button.L2) && buttonWasPressed(Button.L1))
                //{
                //    lockVerticalToggled = !lockVerticalToggled;
                //}

                if (blockInput)
                {
                    // Disabled: "Hold RB + D-Pad Down" (teleport) and
                    // "Hold RB + D-Pad Left" (toggle freeze game) binds,
                    // per user request.
                    //if (buttonWasDown(Button.R1))
                    //{
                    //    if (buttonWasPressed(Button.Down))
                    //    {
                    //        Player? player = getPlayer();
                    //        if (player != null)
                    //        {
                    //            // Y - 150 to approximately align the players head with the camera.
                    //            player.Position = new Vector3(cameraPosition.X, cameraPosition.Y - 150.0f, cameraPosition.Z);
                    //        }
                    //    }
                    //
                    //    if (buttonWasPressed(Button.Left))
                    //    {
                    //        toggleFreezeGame();
                    //    }
                    //}

                    if (buttonWasDown(Button.Share))
                    {
                        if (buttonWasPressed(Button.Cross))
                        {
                        }
                        if (buttonWasPressed(Button.Circle))
                        {
                            if (disableNearDofOverride != 0)
                            {
                                TuningToolInterop.RemoveOverride(disableNearDofOverride);
                                disableNearDofOverride = 0;
                            }
                            else
                            {
                                disableNearDofOverride = TuningToolInterop.AddOverride("Near Enable", new Vector4(), 0);
                            }
                        }
                    }
                    else if (freeCameraFromViewMode && !unlockInputForMenu && buttonWasPressed(Button.Circle))
                    {
                        enableFreeCamera = false;
                    }

                    if (buttonWasPressed(Button.Options) && (plusRight != 0.0f || plusForward != 0.0f))
                    {
                        if (buttonWasDown(Button.Share))
                        {
                            plusRight = 0.0f;
                            plusForward = 0.0f;
                        }
                        else
                        {
                            if (plusRight != 0.0f)
                            {
                                plusRight = -plusRight;
                            }
                            if (plusForward != 0.0f)
                            {
                                plusForward = -plusForward;
                            }
                        }
                    }
                    // Disabled: "Press Y" (gestures menu) and "Hold Select +
                    // Press Y" (poses menu) binds, per user request.
                    //if (!unlockInputForMenu && buttonWasPressed(Button.Triangle))
                    //{
                    //    Player? player = getPlayer();
                    //    if (player != null)
                    //    {
                    //        nint baseAddr = MemoryUtil.Read<nint>(0x1451C4640);
                    //        // MonsterHunterWorld.exe+1ADA26A - mov rax,[rbx+00014018]
                    //        nint gesturesAddr = MemoryUtil.Read<nint>(baseAddr + 0x14018);
                    //        if (buttonWasDown(Button.Share)) // MonsterHunterWorld.exe+1EC8000
                    //        {
                    //            setupGestureMenu.Invoke(gesturesAddr, 0x0, 0x1); // Poses.
                    //        }
                    //        else // MonsterHunterWorld.exe+1EC7D10
                    //        {
                    //            setupGestureMenu.Invoke(gesturesAddr, 0x0, 0x0); // Gestures.
                    //        }
                    //        showGestureMenu.Invoke(gesturesAddr);
                    //        unlockInputForMenu = true;
                    //    }
                    //}
                }
            }
        }

        private float CheckMovementHook(int stickValue, float alwaysZero)
        {
            if (disableMod)
            {
                return checkMovementHook!.Original(stickValue, alwaysZero);
            }

#if HOOK_ORDER_ASSERTS
            //debugLog($"CheckMovementHook({stickValue:X}, {alwaysZero}) @ {frameTick}");
#endif

            if (freeCamera)
            {
                if (orbitPlayer && orbitIgnoreCamera && vCamera != null)
                {
                    // Control the camera's influence on player movement separately.
                    float dist = 700.0f - cameraForward;
                    // Prefer the stable "true forward" reference already
                    // computed for B/C (orbitClampBaseRotationCache) over a
                    // fixed manual angle - this tracks the character's real
                    // facing (so dodge direction matches the stick), while
                    // still being immune to head-tracking jitter (unlike
                    // using the raw FPS camera direction directly).
                    float movementAngleDeg = orbitMovementRotation;
                    if (orbitFaceClampEnable)
                    {
                        Vector3 stableForward = Vector3.Transform(new Vector3(0.0f, 0.0f, 1.0f), orbitClampBaseRotationCache);
                        if (stableForward.LengthSquared() > 0.0001f)
                        {
                            movementAngleDeg = Single.RadiansToDegrees(MathF.Atan2(stableForward.Z, stableForward.X)) + orbitMovementRotation;
                        }
                    }
                    dbgCmDist = dist;
                    dbgCmMovementAngleDeg = movementAngleDeg;
                    dbgCmCameraPosition = cameraPosition;
                    dbgCmTargetBefore = vCamera.Target;
                    vCamera.Target.X = cameraPosition.X + dist * MathF.Cos(Single.DegreesToRadians(movementAngleDeg));
                    vCamera.Target.Y = cameraPosition.Y;
                    vCamera.Target.Z = cameraPosition.Z + dist * MathF.Sin(Single.DegreesToRadians(movementAngleDeg));
                    dbgCmTargetAfter = vCamera.Target;
                }
                if (playerMovementLocked && (plusRight == 0.0f && plusForward == 0.0f))
                {
                    stickValue = 0;
                }
            }

            return checkMovementHook!.Original(stickValue, alwaysZero);
        }
        //DODGE_Rのログ取り
        private nint ActionRequestHook(nint player, int requestId)
        {
            Player? p = getPlayer();
            nint myActionController = p != null ? p.ActionController.Instance : 0; // ← 実際のプロパティ名に置き換え

            if (myActionController != 0 && player == myActionController)
            {
                string motionKey = getCurrentMotionKey(p!);
                float f24 = MemoryUtil.Read<float>(player + 0x24);
                float f4368 = MemoryUtil.Read<float>(player + 0x4368);
                float f436C = MemoryUtil.Read<float>(player + 0x436C);
                float f4370 = MemoryUtil.Read<float>(player + 0x4370);
                float f4374 = MemoryUtil.Read<float>(player + 0x4374);
                float f437C = MemoryUtil.Read<float>(player + 0x437C);
                int i4380 = MemoryUtil.Read<int>(player + 0x4380);

                /*
                debugLog($"[ActionRequest] reqId={requestId} @ {motionKey} | ptr=0x{player:X} " +
                         $"f24={f24} f4368={f4368} f436C={f436C} f4370={f4370} " +
                         $"f4374={f4374} f437C={f437C} i4380={i4380}");
                //*/
            }

            return actionRequestHook!.Original(player, requestId);
        }

        private void CollisionCheckHook(nint unknownPtr, nint unknownPtr2)
        {
            if (disableMod)
            {
                collisionCheckHook!.Original(unknownPtr, unknownPtr2);
                return;
            }

#if HOOK_ORDER_ASSERTS
            //debugLog($"CollisionCheckHook(0x{unknownPtr:X}, 0x{unknownPtr2:X}) @ {frameTick}");
#endif

            Player? player = null;
            if (enableCrawl && (player = getPlayer()) != null)
            {
                nint controlsAddr = MemoryUtil.Read<nint>(player.Instance + 0x12608);
                bool combatControls = false;
                if (controlsAddr != 0x0)
                {
                    combatControls = MemoryUtil.Read<byte>(controlsAddr + 0xB18) == 0x80;
                }
                if (combatControls)
                {
                    procEnvironmentCollision.Invoke(player.Instance, psuedoObject1);
                    procEnvironmentCollision.Invoke(player.Instance, psuedoObject2);
                }
                else
                {
                    enableCrawl = false;
                }
            }

            collisionCheckHook!.Original(unknownPtr, unknownPtr2);
        }

        private void SetZoneStateHook(nint player, int flags)
        {
            if (disableMod)
            {
                setZoneStateHook!.Original(player, flags);
                return;
            }

#if HOOK_ORDER_ASSERTS
            //debugLog($"SetZoneStateHook(0x{player:X}, 0x{flags:X}) @ {frameTick}");
#endif

#if QUARANTINED_FEATURES
            nint zoneStateAddr = MemoryUtil.Read<nint>(0x1451C42B8);
            ref byte zoneState = ref MemoryUtil.GetRef<byte>(zoneStateAddr + 0xD2EA);
            if (!zoneStateManualInvoke)
            {
                lastZoneState = (zoneState == 0x1) ? ZoneState.Hub : ZoneState.Combat;
            }
            else
            {
                zoneStateManualInvoke = false;
            }

            ZoneState overrideZoneState = (forceZoneState == ZoneState.Unknown) ? lastZoneState : forceZoneState;
            if (overrideZoneState != ZoneState.Unknown)
            {
                zoneState = ByteFlag(overrideZoneState == ZoneState.Hub);
            }
#endif

            float headWet = 0.0f, bodyWet = 0.0f, waistWet = 0.0f, legsWet = 0.0f;
            nint wetnessAddr = player + 0x13BD0;
            if (overridePlayerWetness)
            {
                headWet = MemoryUtil.Read<float>(wetnessAddr);
                bodyWet = MemoryUtil.Read<float>(wetnessAddr + 0x28);
                waistWet = MemoryUtil.Read<float>(wetnessAddr + 0x50);
                legsWet = MemoryUtil.Read<float>(wetnessAddr + 0x78);
            }

            setZoneStateHook!.Original(player, flags);

            if (overridePlayerWetness)
            {
                MemoryUtil.GetRef<float>(wetnessAddr) = headWet;
                MemoryUtil.GetRef<float>(wetnessAddr + 0x28) = bodyWet;
                MemoryUtil.GetRef<float>(wetnessAddr + 0x50) = waistWet;
                MemoryUtil.GetRef<float>(wetnessAddr + 0x78) = legsWet;
            }
        }

        private void RefreshEntityParamsHook(nint entity, nint unknownPtr2)
        {
            refreshEntityParamsHook!.Original(entity, unknownPtr2);

            if (disableMod)
            {
                return;
            }

            Player? player = getPlayerWithFallback();
            if (player != null && entity == player && playerOpacityOverride != 1.0f)
            {
                MemoryUtil.GetRef<float>(player.Instance + 0x78E0) = playerOpacityOverride;
            }
        }

        private static string extractPartName(string fullString)
        {
            string partName = "";
            if (fullString.StartsWith("pl\\f_equip") || fullString.StartsWith("pl\\m_equip"))
            {
                partName = fullString.Split('\\')[3];
            }
            else if (fullString.StartsWith("wp\\"))
            {
                partName = fullString.Split('\\')[2];
            }
            else if (fullString.StartsWith("pl\\hair"))
            {
                partName = fullString.Split('\\')[2];
            }
            else if (fullString.StartsWith("pl\\f_face") || fullString.StartsWith("pl\\m_face"))
            {
                partName = fullString.Split('\\')[4].Substring(2);
            }
            return partName;
        }

        private void collectArmorParts(nint baseAddr, nint addr)
        {
            int part;
            string fullString = Marshal.PtrToStringAnsi(addr + 0xC)!;
            string partName = extractPartName(fullString);
            if (partName == "body") part = Armor.Body;
            else if (partName == "helm") part = Armor.Helmet;
            else if (partName == "arm") part = Armor.Arm;
            else if (partName == "wst") part = Armor.Waist;
            else if (partName == "leg") part = Armor.Leg;
            else if (partName.StartsWith("slg")) part = Armor.Slinger;
            else if (fullString.StartsWith("wp\\")) part = Armor.Weapon;
            else if (partName.StartsWith("hair")) part = Armor.Hair;
            else if (partName == "face000") part = Armor.Face;
            else if (partName == "face000_eyelens") part = Armor.EyeLens;
            else return;
            bool firstTimeThisSlot = playerArmor[part] == 0x0;
            playerArmor[part] = baseAddr;
            // Face/EyeLens ("face000"/"face000_eyelens") are preset-name
            // model resources, not per-character-instanced equipment like
            // Helmet/Hair - continuously re-applying a hide here can affect
            // *other* characters (NPCs, other hunters) who happen to share
            // the same face preset. Sticky (persists across re-equips) is
            // limited to true per-instance parts; Face/EyeLens instead only
            // get hidden once per session (first collection), whether from
            // clicking "Hide All" or from the "default hidden" setting.
            if (part == Armor.Face || part == Armor.EyeLens)
            {
                // ▼ NPCが消えてしまう原因なのでコメントアウトする
                /*
                if (stickyHideArmor[part] && firstTimeThisSlot)
                {
                    hideArmorPartAllLods(addr);
                }
                //*/
            }
            //ver13
            else if (part == Armor.Slinger)
            {
                // ver13: 手動のHide All(stickyHideArmor)に加えて、現在
                // マッチしているプロファイルのHideSlingerWhileActiveでも
                // 強制非表示にする。どちらでもなければ自動で再表示する。
                if (stickyHideArmor[part] || orbitSlingerHiddenByProfile)
                {
                    hideArmorPartAllLods(addr);
                }
                else
                {
                    showArmorPartAllLods(addr);
                }
            }
            else if (stickyHideArmor[part])
            {
                hideArmorPartAllLods(addr);
            }
            //ver13ここまで
            int jointCount = MemoryUtil.Read<int>(baseAddr + 0x4A0);
            nint jointAddr = MemoryUtil.Read<nint>(baseAddr + 0x4A8);
            for (int i = 0; i < jointCount; i++)
            {
                nint childAddr = MemoryUtil.Read<nint>(jointAddr + 0x8);
                if (childAddr != 0x0 && MemoryUtil.Read<nint>(childAddr) == 0x142F21A90)
                {
                    childAddr = MemoryUtil.Read<nint>(childAddr + 0x8);
                    if (childAddr != 0x0)
                    {
                        nint childVtable = MemoryUtil.Read<nint>(childAddr);
                        if (childVtable == 0x1435195B8) // Jiggle Bone Container.
                        {
                            if (part == Armor.Body)
                            {
                                ignoredJoints.Add(jointAddr);
                            }
                        }
                        else if (childVtable == 0x143524BD8)
                        {
                            childAddr = MemoryUtil.Read<nint>(childAddr + 0x188);
                            if (childAddr != 0x0 && MemoryUtil.Read<nint>(childAddr) == 0x143519530)
                            {
                                nint jiggleData = MemoryUtil.Read<nint>(MemoryUtil.Read<nint>(childAddr + 0xA0));
                                if (MemoryUtil.Read<nint>(jiggleData) == 0x143519508)
                                {
                                    if (!jiggleBones.Contains(jiggleData))
                                    {
                                        jiggleBones.Add(jiggleData);
                                        if (part == Armor.Body)
                                        {
                                            // Guess that chest bones are the last jiggle bones on the body.
                                            if (chestBone1 == 0x0)
                                            {
                                                chestBone1 = jiggleData;
                                            }
                                            else
                                            {
                                                if (chestBone2 != 0x0)
                                                {
                                                    chestBone2 = chestBone1;
                                                    chestBone1 = jiggleData;
                                                }
                                                else
                                                {
                                                    chestBone2 = jiggleData;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            if (part == Armor.Body)
                            {
                                ignoredJoints.Add(jointAddr);
                            }
                        }
#if QUARANTINED_FEATURES
                        else if (childVtable == 0x143249170)
                        {
                            childAddr = MemoryUtil.Read<nint>(childAddr + 0x188);
                            if (childAddr != 0x0 && MemoryUtil.Read<nint>(childAddr) == 0x1432492F0)
                            {
                                ikJoints.Add(jointAddr);
                            }
                        }
#endif
                    }
                }
                if (part == Armor.Body)
                {
                    bodyJoints.Add(jointAddr);
                }
                else if (part == Armor.Face)
                {
                    faceJoints.Add(jointAddr);
                }
                jointAddr += 0xC0;
            }
            if (part == Armor.Body)
            {
                for (int i = 0; i < bodyJoints.Count; i++)
                {
                    jointAddr = bodyJoints[i];
                    if (ignoredJoints.Contains(jointAddr))
                    {
                        continue;
                    }
                    byte id = MemoryUtil.Read<byte>(jointAddr + 0xA0);
                    Assert(playerJoints[id] == 0x0);
                    playerJoints[id] = jointAddr;
                }
                foreach (nint joint in bodyOffsets.Where(e => !bodyJoints.Contains(e.Key)).Select(e => e.Key).ToList())
                {
                    bodyOffsets.Remove(joint);
                }
#if QUARANTINED_FEATURES
                foreach (nint joint in ikOffsets.Where(e => !bodyJoints.Contains(e.Key)).Select(e => e.Key).ToList())
                {
                    ikOffsets.Remove(joint);
                }
#endif
            }
        }

        private void drawJoint(nint jointAddr, Dictionary<nint, List<Vector3>> offsets)
        {
            if (jointAddr == 0x0) return;
            ImGui.PushID(jointAddr);
            ImGui.DragFloat4("Position", ref MemoryUtil.GetRef<Vector4>(jointAddr + 0x50), 0.005f);
            bool ikJoint = false;
#if QUARANTINED_FEATURES
            if (ikJoints.Contains(jointAddr))
            {
                if (!ikOffsets.ContainsKey(jointAddr))
                {
                    if (ImGui.Button("Add IK Offset"))
                    {
                        ikOffsets.Add(jointAddr, new List<Vector3>()
                        {
                            new Vector3()
                        });
                    }
                }
                else
                {
                    bool doRemove = ImGui.Button("X");
                    ImGui.SameLine();
                    Vector3 v = ikOffsets[jointAddr][0];
                    if (ImGui.DragFloat3($"Offset", ref v, 0.025f))
                    {
                        ikOffsets[jointAddr][0] = v;
                    }
                    if (doRemove) ikOffsets.Remove(jointAddr);
                }
                //ikJoint = true;
            }
#endif
            ImGui.DragFloat4("Rotation", ref MemoryUtil.GetRef<Vector4>(jointAddr + 0x70), 0.005f);
            if (ikJoint)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f);
            }
            bool hasOffset = offsets.ContainsKey(jointAddr);
            if (!hasOffset)
            {
                if (ImGui.Button("Add Offset"))
                {
                    offsets.Add(jointAddr, new List<Vector3>()
                    {
                        new Vector3()
                    });
                }
                if (ikJoint)
                {
                    ImGui.SameLine();
                }
            }
            if (ikJoint)
            {
                ImGui.Text("(Enforced by Inverse Kinematics)");
                ImGui.PopStyleVar();
            }
            if (hasOffset)
            {
                bool doRemove = ImGui.Button("X");
                ImGui.SameLine();
                if (ImGui.Button("Reset"))
                {
                    offsets[jointAddr][0] = new Vector3();
                }
                ImGui.SameLine();
                Vector3 v = offsets[jointAddr][0];
                if (ImGui.DragFloat3($"Offset", ref v, 0.005f))
                {
                    offsets[jointAddr][0] = v;
                }
                if (doRemove) offsets.Remove(jointAddr);
            }
            ImGui.PopID();
        }

        private void drawJoint(nint jointAddr)
        {
            drawJoint(jointAddr, bodyOffsets);
        }

        private void drawJointLeaf(ref List<nint> joints, int i, Dictionary<nint, List<Vector3>> offsets)
        {
            if (i >= joints.Count) return;
            nint jointAddr = joints[i];
            byte id = MemoryUtil.Read<byte>(jointAddr + 0xA0);
            ImGui.PushID(jointAddr);
            if (ImGui.CollapsingHeader($"Joint #{i} (0x{jointAddr:X}, id: 0x{id:X})"))
            {
                drawJoint(jointAddr, offsets);
            }
            ImGui.PopID();
            drawJointLeaf(ref joints, i + 1, offsets);
        }

        private void drawJoints(float width)
        {
            ImGui.PushItemWidth(width * 0.75f);
            ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.Framed;
            bool expanded;
            if (ImGui.TreeNodeEx("Root", flags))
            {
                drawJoint(playerJoints[Joint.Root2]);
                ImGui.TreePop();
            }
            ImGui.PushID("Neck");
            if (ImGui.TreeNodeEx("Neck", flags))
            {
                if (ImGui.TreeNodeEx("Upper", flags))
                {
                    drawJoint(playerJoints[Joint.UpperNeck]);
                    ImGui.TreePop();
                }
                if (ImGui.TreeNodeEx("Lower", flags))
                {
                    drawJoint(playerJoints[Joint.LowerNeck]);
                    ImGui.TreePop();
                }
                ImGui.TreePop();
            }
            ImGui.PopID(); // Neck.
            ImGui.PushID("Back");
            if (ImGui.TreeNodeEx("Back", flags))
            {
                if (ImGui.TreeNodeEx("Upper", flags))
                {
                    drawJoint(playerJoints[Joint.UpperBack]);
                    ImGui.TreePop();
                }
                if (ImGui.TreeNodeEx("Lower", flags))
                {
                    drawJoint(playerJoints[Joint.LowerBack]);
                    ImGui.TreePop();
                }
                ImGui.TreePop();
            }
            ImGui.PopID(); // Back.
            ImGui.PushID("Right Arm");
            if (ImGui.TreeNodeEx("Right Arm", flags))
            {
                if (ImGui.TreeNodeEx("Collarbone", flags))
                {
                    drawJoint(playerJoints[Joint.RightCollarbone]);
                    ImGui.TreePop();
                }
                if (ImGui.TreeNodeEx("Shoulder", flags))
                {
                    drawJoint(playerJoints[Joint.RightShoulder]);
                    ImGui.TreePop();
                }
                if (ImGui.TreeNodeEx("Elbow", flags))
                {
                    drawJoint(playerJoints[Joint.RightElbow]);
                    ImGui.TreePop();
                }
                if (ImGui.TreeNodeEx("Wrist", flags))
                {
                    drawJoint(playerJoints[Joint.RightWrist]);
                    ImGui.TreePop();
                }
                ImGui.TreePop();
            }
            ImGui.PopID(); // Right Arm.
            ImGui.PushID("Right Hand");
            if (ImGui.TreeNodeEx("Right Hand", flags))
            {
                ImGui.PushID("Thumb");
                if (ImGui.TreeNodeEx("Thumb", flags))
                {
                    if (ImGui.TreeNodeEx("Base", flags))
                    {
                        drawJoint(playerJoints[Joint.RightThumbLow]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Middle", flags))
                    {
                        drawJoint(playerJoints[Joint.RightThumbMid]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Tip", flags))
                    {
                        drawJoint(playerJoints[Joint.RightThumbHigh]);
                        ImGui.TreePop();
                    }
                    ImGui.TreePop();
                }
                ImGui.PopID();
                ImGui.PushID("Index");
                if (ImGui.TreeNodeEx("Index", flags))
                {
                    if (ImGui.TreeNodeEx("Base", flags))
                    {
                        drawJoint(playerJoints[Joint.RightIndexLow]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Middle", flags))
                    {
                        drawJoint(playerJoints[Joint.RightIndexMid]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Tip", flags))
                    {
                        drawJoint(playerJoints[Joint.RightIndexHigh]);
                        ImGui.TreePop();
                    }
                    ImGui.TreePop();
                }
                ImGui.PopID();
                ImGui.PushID("Middle");
                if (ImGui.TreeNodeEx("Middle", flags))
                {
                    if (ImGui.TreeNodeEx("Base", flags))
                    {
                        drawJoint(playerJoints[Joint.RightMiddleLow]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Middle", flags))
                    {
                        drawJoint(playerJoints[Joint.RightMiddleMid]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Tip", flags))
                    {
                        drawJoint(playerJoints[Joint.RightMiddleHigh]);
                        ImGui.TreePop();
                    }
                    ImGui.TreePop();
                }
                ImGui.PopID();
                ImGui.PushID("Ring");
                if (ImGui.TreeNodeEx("Ring", flags))
                {
                    if (ImGui.TreeNodeEx("Base", flags))
                    {
                        drawJoint(playerJoints[Joint.RightRingLow]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Middle", flags))
                    {
                        drawJoint(playerJoints[Joint.RightRingMid]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Tip", flags))
                    {
                        drawJoint(playerJoints[Joint.RightRingHigh]);
                        ImGui.TreePop();
                    }
                    ImGui.TreePop();
                }
                ImGui.PopID();
                ImGui.PushID("Pinky");
                if (ImGui.TreeNodeEx("Pinky", flags))
                {
                    if (ImGui.TreeNodeEx("Base", flags))
                    {
                        drawJoint(playerJoints[Joint.RightPinkyLow]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Middle", flags))
                    {
                        drawJoint(playerJoints[Joint.RightPinkyMid]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Tip", flags))
                    {
                        drawJoint(playerJoints[Joint.RightPinkyHigh]);
                        ImGui.TreePop();
                    }
                    ImGui.TreePop();
                }
                ImGui.PopID();
                expanded = ImGui.TreeNodeEx("Palm", flags);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Outer Palm/Pinky+Ring");
                    ImGui.EndTooltip();
                }
                if (expanded)
                {
                    drawJoint(playerJoints[Joint.RightPalm]);
                    ImGui.TreePop();
                }
                ImGui.TreePop();
            }
            ImGui.PopID(); // Right Hand.
            ImGui.PushID("Right Leg");
            if (ImGui.TreeNodeEx("Right Leg", flags))
            {
                if (ImGui.TreeNodeEx("Hip", flags))
                {
                    drawJoint(playerJoints[Joint.RightHip]);
                    ImGui.TreePop();
                }
                if (ImGui.TreeNodeEx("Knee", flags))
                {
                    drawJoint(playerJoints[Joint.RightKnee]);
                    ImGui.TreePop();
                }
                if (ImGui.TreeNodeEx("Ankle", flags))
                {
                    drawJoint(playerJoints[Joint.RightAnkle]);
                    ImGui.TreePop();
                }
                if (ImGui.TreeNodeEx("Toes", flags))
                {
                    drawJoint(playerJoints[Joint.RightToes]);
                    ImGui.TreePop();
                }
                ImGui.TreePop();
            }
            ImGui.PopID(); // Right Leg.
            ImGui.PushID("Left Arm");
            if (ImGui.TreeNodeEx("Left Arm", flags))
            {
                if (ImGui.TreeNodeEx("Collarbone", flags))
                {
                    drawJoint(playerJoints[Joint.LeftCollarbone]);
                    ImGui.TreePop();
                }
                if (ImGui.TreeNodeEx("Shoulder", flags))
                {
                    drawJoint(playerJoints[Joint.LeftShoulder]);
                    ImGui.TreePop();
                }
                if (ImGui.TreeNodeEx("Elbow", flags))
                {
                    drawJoint(playerJoints[Joint.LeftElbow]);
                    ImGui.TreePop();
                }
                if (ImGui.TreeNodeEx("Wrist", flags))
                {
                    drawJoint(playerJoints[Joint.LeftWrist]);
                    ImGui.TreePop();
                }
                ImGui.TreePop();
            }
            ImGui.PopID(); // Left Arm.
            ImGui.PushID("Left Hand");
            if (ImGui.TreeNodeEx("Left Hand", flags))
            {
                ImGui.PushID("Thumb");
                if (ImGui.TreeNodeEx("Thumb", flags))
                {
                    if (ImGui.TreeNodeEx("Base", flags))
                    {
                        drawJoint(playerJoints[Joint.LeftThumbLow]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Middle", flags))
                    {
                        drawJoint(playerJoints[Joint.LeftThumbMid]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Tip", flags))
                    {
                        drawJoint(playerJoints[Joint.LeftThumbHigh]);
                        ImGui.TreePop();
                    }
                    ImGui.TreePop();
                }
                ImGui.PopID();
                ImGui.PushID("Index");
                if (ImGui.TreeNodeEx("Index", flags))
                {
                    if (ImGui.TreeNodeEx("Base", flags))
                    {
                        drawJoint(playerJoints[Joint.LeftIndexLow]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Middle", flags))
                    {
                        drawJoint(playerJoints[Joint.LeftIndexMid]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Tip", flags))
                    {
                        drawJoint(playerJoints[Joint.LeftIndexHigh]);
                        ImGui.TreePop();
                    }
                    ImGui.TreePop();
                }
                ImGui.PopID();
                ImGui.PushID("Middle");
                if (ImGui.TreeNodeEx("Middle", flags))
                {
                    if (ImGui.TreeNodeEx("Base", flags))
                    {
                        drawJoint(playerJoints[Joint.LeftMiddleLow]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Middle", flags))
                    {
                        drawJoint(playerJoints[Joint.LeftMiddleMid]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Tip", flags))
                    {
                        drawJoint(playerJoints[Joint.LeftMiddleHigh]);
                        ImGui.TreePop();
                    }
                    ImGui.TreePop();
                }
                ImGui.PopID();
                ImGui.PushID("Ring");
                if (ImGui.TreeNodeEx("Ring", flags))
                {
                    if (ImGui.TreeNodeEx("Base", flags))
                    {
                        drawJoint(playerJoints[Joint.LeftRingLow]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Middle", flags))
                    {
                        drawJoint(playerJoints[Joint.LeftRingMid]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Tip", flags))
                    {
                        drawJoint(playerJoints[Joint.LeftRingHigh]);
                        ImGui.TreePop();
                    }
                    ImGui.TreePop();
                }
                ImGui.PopID();
                ImGui.PushID("Pinky");
                if (ImGui.TreeNodeEx("Pinky", flags))
                {
                    if (ImGui.TreeNodeEx("Base", flags))
                    {
                        drawJoint(playerJoints[Joint.LeftPinkyLow]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Middle", flags))
                    {
                        drawJoint(playerJoints[Joint.LeftPinkyMid]);
                        ImGui.TreePop();
                    }
                    if (ImGui.TreeNodeEx("Tip", flags))
                    {
                        drawJoint(playerJoints[Joint.LeftPinkyHigh]);
                        ImGui.TreePop();
                    }
                    ImGui.TreePop();
                }
                ImGui.PopID();
                expanded = ImGui.TreeNodeEx("Palm", flags);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Outer Palm/Pinky+Ring");
                    ImGui.EndTooltip();
                }
                if (expanded)
                {
                    drawJoint(playerJoints[Joint.LeftPalm]);
                    ImGui.TreePop();
                }
                ImGui.TreePop();
            }
            ImGui.PopID(); // Left Hand.
            ImGui.PushID("Left Leg");
            if (ImGui.TreeNodeEx("Left Leg", flags))
            {
                if (ImGui.TreeNodeEx("Hip", flags))
                {
                    drawJoint(playerJoints[Joint.LeftHip]);
                    ImGui.TreePop();
                }
                if (ImGui.TreeNodeEx("Knee", flags))
                {
                    drawJoint(playerJoints[Joint.LeftKnee]);
                    ImGui.TreePop();
                }
                if (ImGui.TreeNodeEx("Ankle", flags))
                {
                    drawJoint(playerJoints[Joint.LeftAnkle]);
                    ImGui.TreePop();
                }
                if (ImGui.TreeNodeEx("Toes", flags))
                {
                    drawJoint(playerJoints[Joint.LeftToes]);
                    ImGui.TreePop();
                }
                ImGui.TreePop();
            }
            ImGui.PopID(); // Left Leg.
            ImGui.Separator();
            ImGui.Checkbox("Attach to Chest", ref attachToChest);
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text("While in Free Camera, hold LT + RT and move your joysticks (armor has to have jiggle physics).");
                ImGui.EndTooltip();
            }
            if (!attachToChest)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f);
            }
            ImGui.SameLine();
            ImGui.Checkbox("Swap Sides", ref swapChestSides);
            ImGui.SetNextItemWidth(width * 0.35f);
            ImGui.DragFloat("Strength", ref chestMoveScale, 0.001f);
            if (!attachToChest)
            {
                ImGui.PopStyleVar();
            }
            if (ImGui.CollapsingHeader("Jiggle Bones"))
            {
                for (int i = 0; i < jiggleBones.Count; i++)
                {
                    nint jiggleData = jiggleBones[i];
                    ImGui.PushID(jiggleData);
                    if (ImGui.CollapsingHeader($"Jiggle Bone #{i} (0x{jiggleData:X})"))
                    {
                        ImGui.DragFloat3("Position", ref MemoryUtil.GetRef<Vector3>(jiggleData + 0xD0), 0.05f);
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("Drag this position around to influence the bone.");
                            ImGui.EndTooltip();
                        }
                    }
                    ImGui.PopID();
                }
            }
            ImGui.Separator();
            if (ImGui.CollapsingHeader("Body Joints"))
            {
                drawJointLeaf(ref bodyJoints, 0, bodyOffsets);
            }
            if (ImGui.CollapsingHeader("Face Joints"))
            {
                drawJointLeaf(ref faceJoints, 0, faceOffsets);
            }
            ImGui.PopItemWidth();
        }

        // Standalone version of the "Hide All" logic below, usable without
        // any ImGui context - called from collectArmorParts to re-apply a
        // sticky hide after equipment changes (e.g. swapping helmets), which
        // otherwise reset a freshly-equipped part back to visible.
        private void hideArmorPartAllLods(nint partsAddr)
        {
            if (partsAddr == 0x0)
            {
                return;
            }
            nint baseLod = MemoryUtil.Read<nint>(partsAddr + 0xC8);
            nint baseOffset = MemoryUtil.Read<nint>(partsAddr + 0x408 + (0x8 * 0x1));
            int numParts = MemoryUtil.Read<int>(partsAddr + 0x548 + (0x4 * 0x1));
            int lodCount = MemoryUtil.Read<int>(partsAddr + 0xD0);
            const int lodStructSize = 0x50;
            for (int i = 0; i < numParts; i++)
            {
                int lodOffset = MemoryUtil.Read<int>(baseOffset + (i * 0x4));
                nint lod0 = baseLod + lodOffset * lodStructSize;
                ushort lod0Visible = MemoryUtil.GetRef<ushort>(lod0);
                if (lod0Visible == 0)
                {
                    continue; // already hidden
                }
                int lastLod = (i < numParts - 1) ? MemoryUtil.Read<int>(baseOffset + ((i + 1) * 0x4)) : lodCount;
                int partLodCount = lastLod - lodOffset;
                for (int j = 0; j < partLodCount; j++)
                {
                    nint lod = lod0 + (j * lodStructSize);
                    ref ushort lodVisible = ref MemoryUtil.GetRef<ushort>(lod);
                    ref ushort padSub = ref MemoryUtil.GetRef<ushort>(lod + 0xA);
                    if (lodVisible != 0)
                    {
                        (lodVisible, padSub) = (padSub, lodVisible);
                    }
                    if (modelOnlyAddressLod0)
                    {
                        break;
                    }
                }
            }
        }
        //ver13
        // Mirror image of hideArmorPartAllLods - forces every part back to
        // visible. Used (ver13) to automatically un-hide the Slinger the
        // moment a HideSlingerWhileActive profile stops matching.
        private void showArmorPartAllLods(nint partsAddr)
        {
            if (partsAddr == 0x0)
            {
                return;
            }
            nint baseLod = MemoryUtil.Read<nint>(partsAddr + 0xC8);
            nint baseOffset = MemoryUtil.Read<nint>(partsAddr + 0x408 + (0x8 * 0x1));
            int numParts = MemoryUtil.Read<int>(partsAddr + 0x548 + (0x4 * 0x1));
            int lodCount = MemoryUtil.Read<int>(partsAddr + 0xD0);
            const int lodStructSize = 0x50;
            for (int i = 0; i < numParts; i++)
            {
                int lodOffset = MemoryUtil.Read<int>(baseOffset + (i * 0x4));
                nint lod0 = baseLod + lodOffset * lodStructSize;
                ushort lod0Visible = MemoryUtil.GetRef<ushort>(lod0);
                if (lod0Visible != 0)
                {
                    continue; // already visible
                }
                int lastLod = (i < numParts - 1) ? MemoryUtil.Read<int>(baseOffset + ((i + 1) * 0x4)) : lodCount;
                int partLodCount = lastLod - lodOffset;
                for (int j = 0; j < partLodCount; j++)
                {
                    nint lod = lod0 + (j * lodStructSize);
                    ref ushort lodVisible = ref MemoryUtil.GetRef<ushort>(lod);
                    ref ushort padSub = ref MemoryUtil.GetRef<ushort>(lod + 0xA);
                    if (lodVisible == 0)
                    {
                        (lodVisible, padSub) = (padSub, lodVisible);
                    }
                    if (modelOnlyAddressLod0)
                    {
                        break;
                    }
                }
            }
        }
        //ver13ここまで
        private void drawModelParts(nint armorAddr, nint partsAddr = 0x0, int armorCategory = -1)
        {
            if (partsAddr == 0x0)
            {
                if (armorAddr == 0x0 || (partsAddr = MemoryUtil.Read<nint>(armorAddr + 0x2A0)) == 0x0)
                {
                    return;
                }
            }
            ImGui.PushID(partsAddr);
            string fullString = Marshal.PtrToStringAnsi(partsAddr + 0xC)!;
            if (armorAddr != 0x0)
            {
                string partName = extractPartName(fullString);
                ImGui.Text($"{partName} (0x{armorAddr:X}, {fullString} @ 0x{partsAddr:X}):");
            }
            else
            {
                ImGui.Text($"{fullString} @ 0x{partsAddr:X}:");
            }
            // MonsterHunterWorld.exe+222E651 - mov rax,[r8+000000C8]
            nint baseLod = MemoryUtil.Read<nint>(partsAddr + 0xC8);
            nint baseOffset = MemoryUtil.Read<nint>(partsAddr + 0x408 + (0x8 * 0x1));
            int numParts = MemoryUtil.Read<int>(partsAddr + 0x548 + (0x4 * 0x1));
            // Setting lodCount to 0 is probably the best way to completely hide a model.
            int lodCount = MemoryUtil.Read<int>(partsAddr + 0xD0);
            const int lodStructSize = 0x50;
            const int partsPerLine = 10;
            bool allOn = ImGui.Button("Show All");
            ImGui.SameLine();
            bool allOff = ImGui.Button("Hide All");
            if (armorCategory >= 0)
            {
                ImGui.SameLine();
                bool sticky = stickyHideArmor[armorCategory];
                ImGui.Text(sticky ? "(stays hidden across equipment changes)" : "");
                if (allOn)
                {
                    stickyHideArmor[armorCategory] = false;
                }
                if (allOff)
                {
                    stickyHideArmor[armorCategory] = true;
                }
            }
            for (int i = 0; i < numParts; i++)
            {
                ref int lodOffset = ref MemoryUtil.GetRef<int>(baseOffset + (i * 0x4));
                nint lod0 = baseLod + lodOffset * lodStructSize;
                bool isVisible = MemoryUtil.GetRef<ushort>(lod0) != 0;
                bool togglePart = ImGui.Checkbox($"##Part #{i}", ref isVisible);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text($"Part #{i} (0x{lod0:X})");
                    ImGui.EndTooltip();
                }
                int lastLod = (i < numParts - 1) ? MemoryUtil.Read<int>(baseOffset + ((i + 1) * 0x4)) : lodCount;
                int partLodCount = lastLod - lodOffset;
                // This is not a "proper" way to hide a part. It abuses the fact that if the lower
                // 16 bits (lodVisible) of these 4 bytes are 0, a check to skip rendering it will
                // always pass. Also that +0xA appears to be unused/padding.
                // MonsterHunterWorld.exe+222FCE3 - mov eax,[r8]
                ushort lod0Visible = MemoryUtil.GetRef<ushort>(lod0);
                for (int j = 0; j < partLodCount; j++)
                {
                    if (togglePart || (lod0Visible != 0 && allOff) || (lod0Visible == 0 && allOn))
                    {
                        nint lod = lod0 + (j * lodStructSize);
                        ref ushort lodVisible = ref MemoryUtil.GetRef<ushort>(lod);
                        ref ushort padSub = ref MemoryUtil.GetRef<ushort>(lod + 0xA);
                        if (lod0Visible != 0) Assert(padSub == 0);
                        // Check against LOD0 to prevent an inconsistent state.
                        if ((lodVisible != 0) == (lod0Visible != 0))
                        {
                            (lodVisible, padSub) = (padSub, lodVisible);
                        }
                    }
                    if (modelOnlyAddressLod0) break;
                }
                if (i != numParts - 1 && i % partsPerLine != partsPerLine - 1)
                {
                    ImGui.SameLine();
                }
            }
            ImGui.PopID();
        }

        private void dropArmorState()
        {
            Array.Clear(playerArmor, 0, playerArmor.Length);
            Array.Clear(playerJoints, 0, playerJoints.Length);
            bodyJoints.Clear();
            ignoredJoints.Clear();
            faceJoints.Clear();
#if QUARANTINED_FEATURES
            ikJoints.Clear();
#endif
            jiggleBones.Clear();
            chestBone1 = 0x0;
            chestBone2 = 0x0;
        }

        private void dropJointOffsets()
        {
            bodyOffsets.Clear();
            faceOffsets.Clear();
#if QUARANTINED_FEATURES
            ikOffsets.Clear();
#endif
        }

        private bool collectArmorAndJoints(Player player)
        {
            nint bodyArmorParts = MemoryUtil.Read<nint>(player.Instance + 0x2A0);
            if (bodyArmorParts == 0x0)
            {
                return false;
            }
            // MonsterHunterWorld.exe+203E8A2 - mov rax,[rbx+000126C8]
            nint combineArmorList = MemoryUtil.Read<nint>(player.Instance + 0x126C8);
            if (combineArmorList == 0x0)
            {
                return false;
            }
            collectArmorParts(player.Instance, bodyArmorParts);
            nint weapon = MemoryUtil.Read<nint>(player.Instance + 0x76B0);
            if (weapon != 0x0)
            {
                nint weaponParts = MemoryUtil.Read<nint>(weapon + 0x2A0);
                if (weaponParts != 0x0)
                {
                    collectArmorParts(weapon, weaponParts);
                }
            }
            nint slinger = MemoryUtil.Read<nint>(player.Instance + 0x8918);
            if (slinger != 0x0)
            {
                nint slingerParts = MemoryUtil.Read<nint>(slinger + 0x2A0);
                if (slingerParts != 0x0)
                {
                    collectArmorParts(slinger, slingerParts);
                }
            }
            combineArmorList = MemoryUtil.Read<nint>(combineArmorList + 0x70);
            nint next = combineArmorList;
            while (next != 0x0)
            {
                nint armorAddr = next;
                next = MemoryUtil.Read<nint>(armorAddr + 0x30);
                nint vTable = MemoryUtil.Read<nint>(armorAddr);
                // uWeaponParts: 0x1434D2400, uWeaponEmblem: 0x1434D1D58
                if (vTable == 0x1434A7560) // uPlCombineArmor
                {
                    nint partsAddr = 0x0;
                    nint childAddr = MemoryUtil.Read<nint>(armorAddr + 0x548);
                    if (childAddr != 0x0)
                    {
                        if (MemoryUtil.Read<nint>(childAddr) == 0x143451520) // Face.
                        {
                            nint ownerAddr = MemoryUtil.Read<nint>(childAddr + 0x998);
                            if (ownerAddr != player.Instance)
                            {
                                continue;
                            }
                            if (playerArmor[Armor.Face] == 0x0)
                            {
                                partsAddr = MemoryUtil.Read<nint>(childAddr + 0x2A0);
                                if (partsAddr != 0x0)
                                {
                                    collectArmorParts(childAddr, partsAddr);
                                }
                                childAddr = MemoryUtil.Read<nint>(childAddr + 0x9C8);
                                if (childAddr != 0x0 && MemoryUtil.Read<nint>(childAddr) == 0x143505E98) // Eye lens.
                                {
                                    partsAddr = MemoryUtil.Read<nint>(childAddr + 0x2A0);
                                    if (partsAddr != 0x0)
                                    {
                                        collectArmorParts(childAddr, partsAddr);
                                    }
                                }
                            }
                        }
                        else if (childAddr != player.Instance)
                        {
                            continue;
                        }
                    }
                    partsAddr = MemoryUtil.Read<nint>(armorAddr + 0x2A0);
                    if (partsAddr != 0x0)
                    {
                        collectArmorParts(armorAddr, partsAddr);
                    }
                }
            }
            return true;
        }

        private void refreshArmorState(Player player)
        {
            dropArmorState();
            if (!collectArmorAndJoints(player))
            {
                dropJointOffsets();
            }
        }

        private bool usingArmorState()
        {
            return (bodyOffsets.Count > 0 || faceOffsets.Count > 0) || (hideKnife || checkKnifeHidden) || (enableFreeCamera && orbitPlayer && orbitJoint >= 0);
        }

        private void UpdateJointsHook(nint model)
        {
            if (!disableMod && usingArmorState())
            {
                Player? player = getPlayerWithFallback();
                if (player != null && model == player.Instance)
                {
                    refreshArmorState(player);
                    foreach ((nint addr, List<Vector3> offsets) in bodyOffsets)
                    {
                        MemoryUtil.GetRef<Quaternion>(addr + 0x70) *= Quaternion.CreateFromYawPitchRoll(offsets[0].X, offsets[0].Y, offsets[0].Z);
                    }
                    if (playerArmor[Armor.Waist] != 0x0)
                    {
                        MemoryUtil.GetRef<byte>(playerArmor[Armor.Waist] + 0x2D0) = hideKnife ? (byte)0xFD : (byte)0xFF;
                        checkKnifeHidden = false;
                    }
                }
                else if (player != null && model == playerArmor[Armor.Face])
                {
                    refreshArmorState(player);

                    // ▼ ここから追加：プレイヤーの顔とアイレンズの非表示もしくは透明度を 0.0f にして完全透明にする
                    if (stickyHideArmor[Armor.Face])
                    {
                        MemoryUtil.GetRef<float>(playerArmor[Armor.Face] + 0x314) = 0.0f;
                    }
                    if (stickyHideArmor[Armor.EyeLens] && playerArmor[Armor.EyeLens] != 0x0)
                    {
                        MemoryUtil.GetRef<float>(playerArmor[Armor.EyeLens] + 0x314) = 0.0f;
                    }
                    // ▲ ここまで追加

                    foreach ((nint addr, List<Vector3> offsets) in faceOffsets)
                    {
                        MemoryUtil.GetRef<Quaternion>(addr + 0x70) *= Quaternion.CreateFromYawPitchRoll(offsets[0].X, offsets[0].Y, offsets[0].Z);
                    }
                }
            }
            updateJointsHook!.Original(model);
        }

#if QUARANTINED_FEATURES
        private void UpdateIKHook(nint superOfChild, nint joint, nint obj)
        {
            if (!disableMod && ikOffsets.ContainsKey(joint))
            {
                Vector3 offset = ikOffsets[joint][0];
                MemoryUtil.GetRef<Vector3>(superOfChild + 0x1380) += offset;
            }
            updateIKHook!.Original(superOfChild, joint, obj);
        }
#endif

        /*
        private void ChangeEquipmentHook(nint unknownPtr, int unknownInt)
        {
            nint rax = MemoryUtil.Read<nint>(unknownPtr + 0x2968);
            nint r9 = MemoryUtil.Read<nint>(unknownPtr + 0x2950);
            int esi = MemoryUtil.Read<int>(rax + 0x240);
            esi *= MemoryUtil.Read<int>(r9 + 0x1D8);
            esi += unknownInt;
            nint newEquipment = newEquipmentPointer.Invoke(unknownPtr, esi);
            lastEquipedId = MemoryUtil.Read<int>(newEquipment + 0x24);
            changeEquipmentHook!.Original(unknownPtr, unknownInt);
        }
        */

        private void UnderwaterCheckHook(nint unknownPtr)
        {
            underwaterCheckHook!.Original(unknownPtr);

            if (disableMod || !enableUnderwaterCamera)
            {
                return;
            }

#if HOOK_ORDER_ASSERTS
            //debugLog($"UnderwaterCheckHook(0x{unknownPtr:X}) @ {frameTick}");
#endif

            cameraIsUnderwater = MemoryUtil.Read<byte>(unknownPtr + 0x1E83) == 0x1;
        }

        private void CameraEffectHook(nint unknownPtr, nint unknownPtr2, nint unknownPtr3)
        {
            if (disableMod || !enableUnderwaterCamera)
            {
                cameraEffectHook!.Original(unknownPtr, unknownPtr2, unknownPtr3);
                return;
            }

#if HOOK_ORDER_ASSERTS
            //debugLog($"CameraEffectHook(0x{unknownPtr:X}, 0x{unknownPtr2:X}, 0x{unknownPtr3:X}) @ {frameTick}");
#endif

            byte[]? underwaterValues = null;
            nint targetAddr = 0x0;

            nint rax = MemoryUtil.Read<nint>(unknownPtr + 0x550);
            int index1 = MemoryUtil.Read<int>(unknownPtr + 0xA50);
            rax = MemoryUtil.Read<nint>(rax + (index1 * 0x8));
            if (rax != 0x0)
            {
                rax = MemoryUtil.Read<nint>(rax + 0x188);
                if (rax != 0x0)
                {
                    targetAddr = rax + 0x140;
                }
            }

            if (cameraIsUnderwater && !disableDofCoc)
            {
                nullifyDofCoc.Enable();
                disableDofCoc = true;
            }
            else if (!cameraIsUnderwater && disableDofCoc)
            {
                nullifyDofCoc.Disable();
                disableDofCoc = false;
            }

            if (cameraIsUnderwater)
            {
                underwaterValues = MemoryUtil.ReadArray<byte>(targetAddr + 0x8, 120);
                // Straight copy-paste from The Great Forest.
                MemoryUtil.WriteBytes(targetAddr + 0x8, [0x01, 0x01, 0x00, 0x00, 0x14, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0xBF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x6F, 0x12, 0x03, 0x3B, 0x01, 0x00, 0x00, 0x00, 0xCE, 0xCC, 0xCC, 0x3E, 0x01, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x80, 0x3F, 0x8F, 0xC2, 0xF5, 0x3C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x7A, 0x44, 0x01, 0x00, 0x00, 0x00, 0x00, 0x60, 0xEA, 0x46, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC8, 0x42]);
            }

            cameraEffectHook!.Original(unknownPtr, unknownPtr2, unknownPtr3);

            if (underwaterValues != null)
            {
                MemoryUtil.WriteBytes(targetAddr + 0x8, underwaterValues);
            }
        }

        private void drawViewportInfo(int i, float width, Config config, bool debug)
        {
            ImGui.PushItemWidth(width * 0.75f);
            Viewport vp = CameraSystem.GetViewport(i);
            if (debug)
            {
                ImGui.Text($"Address: 0x{vp.Instance:X}");
                ImGui.Text($"Visible: {vp.Visible}");
                ImGui.Text($"Region: {vp.Region.Width}x{vp.Region.Height} ({vp.Region.X},{vp.Region.Y})");
            }
            else
            {
                ImGui.Text($"Resolution: {vp.Region.Width}x{vp.Region.Height}");
            }
            if (debug && vp.Visible)
            {
                bool fadeObjects = getViewportFadeObjects(i);
                if (ImGui.Checkbox("Fade Objects When Close to Camera", ref fadeObjects))
                {
                    setViewportFadeObjects(i, fadeObjects);
                }
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("This is a viewport flag which is not used by the \"Disable Fading\" option. It doesn't apply to decals or monsters.");
                    ImGui.EndTooltip();
                }
            }
            if (vp.Camera != null)
            {
                Camera camera = vp.Camera;
                bool usedByFreeCamera = freeCamera && enableFreeCamera && camera == vCamera;
                if (debug) ImGui.Text($"Camera Pointer: 0x{camera.Instance:X}");
                if (ImGui.DragFloat("Field of View", ref camera.FieldOfView, 0.1f))
                {
                    if (usedByFreeCamera)
                    {
                        cameraFov = camera.FieldOfView;
                    }
                }
                if (enableOffsetPerspective && camera == pCamera)
                {
                    ImGui.Text($"Internal FOV: {previousFov}");
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("What the FOV would be if not set by us.");
                        ImGui.EndTooltip();
                    }
                }
                if (debug) ImGui.InputFloat("Aspect Ratio", ref camera.AspectRatio);
                if (ImGui.InputFloat("Near Clip", ref camera.NearClip) && usedByFreeCamera)
                {
                    debugNearClip = camera.NearClip;
                }
                ImGui.InputFloat("Far Clip", ref camera.FarClip);
                if (usedByFreeCamera)
                {
                    ImGui.DragFloat3("Position", ref cameraPosition, 0.425f);
                }
                else
                {
                    ImGui.DragFloat3("Position", ref camera.Position, 0.425f);
                }
                ImGui.DragFloat3("Target", ref camera.Target, 0.425f);
                ImGui.DragFloat3("Up", ref camera.Up, 0.425f);
                ImGui.PushItemWidth(width * 0.3f);
                ImGui.InputText("##Position Name", ref typedPositionName, 99);
                ImGui.SameLine();
                if (ImGui.Button("Save"))
                {
                    string name = typedPositionName;
                    if (name != "")
                    {
                        Config.Position pos = new Config.Position();
                        pos.FieldOfView = camera.FieldOfView;
                        pos.Roll = cameraRoll;
                        pos.PosX = camera.Position.X;
                        pos.PosY = camera.Position.Y;
                        pos.PosZ = camera.Position.Z;
                        pos.TargetX = camera.Target.X;
                        pos.TargetY = camera.Target.Y;
                        pos.TargetZ = camera.Target.Z;
                        if (config.Positions.ContainsKey(name))
                        {
                            config.Positions[name] = pos;
                        }
                        else
                        {
                            config.Positions.Add(name, pos);
                        }
                        ConfigManager.SaveConfig<Config>(this);
                    }
                }
                ImGui.SameLine();
                if (ImGui.BeginCombo("##Positions", selectedPositionName))
                {
                    Dictionary<string, Config.Position>.KeyCollection positionKeys = config.Positions.Keys;
                    for (int j = 0; j < positionKeys.Count + 1; j++)
                    {
                        if (j == 0)
                        {
                            if (ImGui.Selectable("(None)"))
                            {
                                selectedPositionName = "";
                            }
                            continue;
                        }
                        string positionKey = positionKeys.ElementAt(j - 1);
                        if (ImGui.Selectable(positionKey))
                        {
                            Config.Position pos = config.Positions[positionKey];
                            selectedPositionName = positionKey;
                            camera.FieldOfView = pos.FieldOfView;
                            cameraRoll = pos.Roll;
                            cameraPosition = new Vector3(pos.PosX, pos.PosY, pos.PosZ);
                            cameraTarget = new Vector3(pos.TargetX, pos.TargetY, pos.TargetZ);
                            Quaternion forward = Quaternion.Normalize(getForward(cameraPosition, cameraTarget));
                            cameraYaw = Single.RadiansToDegrees(MathF.Atan2(forward.Z, forward.X));
                            cameraPitch = Single.RadiansToDegrees(MathF.Asin(forward.Y));
                            camera.Position = cameraPosition;
                            camera.Target = cameraTarget;
                        }
                    }
                    ImGui.EndCombo();
                }
                ImGui.PopItemWidth();
                ImGui.SameLine();
                if (ImGui.Button("Delete"))
                {
                    if (selectedPositionName != "")
                    {
                        config.Positions.Remove(selectedPositionName);
                        selectedPositionName = "";
                        ConfigManager.SaveConfig<Config>(this);
                    }
                }
                if (debug)
                {
                    bool move = camera.Move;
                    if (ImGui.Checkbox("Move", ref move))
                    {
                        camera.Move = move;
                    }
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("Uncheck this to allow manually overriding the camera position.\nIf your camera is frozen, make sure this is checked.");
                        ImGui.EndTooltip();
                    }
                    ImGui.SameLine();
                    bool fix = camera.Fix;
                    if (ImGui.Checkbox("Fix", ref fix))
                    {
                        camera.Fix = fix;
                    }
                }
                if (camera == pCamera)
                {
                    Player? player = checkPlayerChange();
                    if (!debug && player != null)
                    {
                        ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(0.0f, 6.0f));
                        ImGui.Text("Camera Distance");
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("To save this setting, save your game.");
                            ImGui.EndTooltip();
                        }
                        ImGui.SetCursorPos(ImGui.GetCursorPos() - new Vector2(0.0f, 6.0f));
                        ref byte settingB = ref MemoryUtil.GetRef<byte>(getPlayerSettings() + 0x1403FD);
                        int settingI = settingB;
                        ImGui.SameLine();
                        ImGui.SetCursorPos(ImGui.GetCursorPos() - new Vector2(0.0f, 6.0f));
                        if (ImGui.RadioButton("Close", ref settingI, 0))
                        {
                            settingB = (byte)settingI;
                        }
                        ImGui.SameLine();
                        ImGui.SetCursorPos(ImGui.GetCursorPos() - new Vector2(0.0f, 6.0f));
                        if (ImGui.RadioButton("Default", ref settingI, 1))
                        {
                            settingB = (byte)settingI;
                        }
                        ImGui.SameLine();
                        ImGui.SetCursorPos(ImGui.GetCursorPos() - new Vector2(0.0f, 6.0f));
                        if (ImGui.RadioButton("Far", ref settingI, 2))
                        {
                            settingB = (byte)settingI;
                        }
                        // MonsterHunterWorld.exe+12A9AEC - mov rcx,[rdi+00001E90]
                        // MonsterHunterWorld.exe+11A9AB9 - add rcx,00001410
                        // MonsterHunterWorld.exe+12A9AE0 - mov rcx,[MonsterHunterWorld.exe+5013950]
                        nint playerCameraAddr = MemoryUtil.Read<nint>(player.Instance + 0x14F8);
                        if (playerCameraAddr != 0x0)
                        {
                            playerCameraAddr = MemoryUtil.Read<nint>(playerCameraAddr + 0x1410 + 0x1E90);
                        }
                        if (playerCameraAddr != 0x0)
                        {
                            ImGui.Text("Offset");
                            nint viewParamOffset = getViewParamOffset.Invoke(playerCameraAddr + 0x1380, 0, 0xC9);
                            if (viewParamOffset != 0x0)
                            {
                                ImGui.DragFloat3("Close", ref MemoryUtil.GetRef<Vector3>(viewParamOffset + 0x20), 0.1f);
                            }
                            viewParamOffset = getViewParamOffset.Invoke(playerCameraAddr + 0x1380, 0, 0xCA);
                            if (viewParamOffset != 0x0)
                            {
                                ImGui.DragFloat3("Far", ref MemoryUtil.GetRef<Vector3>(viewParamOffset + 0x20), 0.1f);
                            }
                        }
                    }
                }
                if (camera == vCamera)
                {
                    if (!usedByFreeCamera)
                    {
                        ImGui.PushItemFlag(ImGuiItemFlags.Disabled, true);
                    }
                    ImGui.Text("Angle");
                    // If our camera hooks aren't being run, these values won't be updated.
                    ImGui.DragFloat("Yaw", ref cameraYaw, 0.2f);
                    ImGui.DragFloat("Pitch", ref cameraPitch, 0.2f);
                    ImGui.DragFloat("Roll", ref cameraRoll, 0.2f);
                    if (!usedByFreeCamera)
                    {
                        ImGui.PopItemFlag();
                    }
                    // Try to grey out offsets when they probably have no effect.
                    if ((camera == pCamera && !usedByFreeCamera) && camera.Move)
                    {
                        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f);
                    }
                    ImGui.Text("Move");
                    float offsetForward = 0.0f;
                    if (ImGui.DragFloat("Forward", ref offsetForward, 0.425f))
                    {
                        Quaternion forward = getForward(camera.Position, camera.Target);
                        forward = Quaternion.Normalize(forward);
                        camera.Position.X += forward.X * offsetForward;
                        camera.Position.Y += forward.Y * offsetForward;
                        camera.Position.Z += forward.Z * offsetForward;
                        if (usedByFreeCamera) cameraPosition = camera.Position;
                    }
                    float offsetRight = 0.0f;
                    if (ImGui.DragFloat("Right", ref offsetRight, 0.425f))
                    {
                        Quaternion forward = getForward(camera.Position, camera.Target);
                        Quaternion right = getRight(forward);
                        forward = Quaternion.Normalize(forward);
                        right = Quaternion.Normalize(right);
                        camera.Position.X += right.X * offsetRight;
                        camera.Position.Z += right.Z * offsetRight;
                        if (usedByFreeCamera) cameraPosition = camera.Position;
                    }
                    float offsetUp = 0.0f;
                    if (ImGui.DragFloat("Up", ref offsetUp, 0.425f))
                    {
                        camera.Position.Y += offsetUp;
                        if (usedByFreeCamera) cameraPosition = camera.Position;
                    }
                    if (camera == pCamera && !(freeCamera || enableFreeCamera))
                    {
                        ImGui.PopStyleVar();
                    }
                }
            }
            ImGui.PopItemWidth();
        }

        private void drawAnimationInfo(Entity entity, float width, bool debug)
        {
            ActionController actionController = entity.ActionController;
            ActionInfo currentActionInfo = actionController.CurrentAction;
            SharpPluginLoader.Core.Actions.Action? currentAction = null;
            if (currentActionInfo.ActionSet >= 0 && currentActionInfo.ActionSet <= 3)
            {
                ActionList actionList = actionController.GetActionList(currentActionInfo.ActionSet);
                if (currentActionInfo.ActionId >= 0 && currentActionInfo.ActionId < actionList.Count)
                {
                    currentAction = actionList[currentActionInfo.ActionId];
                }
            }
            if (debug)
            {
                ImGui.Text($"ActionController: 0x{actionController.Instance:X}");
                ImGui.Text($" Current: {currentAction} {currentActionInfo}");
                if (currentAction != null)
                {

                    ImGui.Text($" Active Time: {currentAction.ActiveTime}");
                    ImGui.Text($" Delta Sec: {currentAction.DeltaSec}");
                }
            }
            AnimationId currentAnimation = entity.CurrentAnimation;
            AnimationLayerComponent? animationLayer = entity.AnimationLayer;
            if (animationLayer != null)
            {
                ImGui.Text(debug ? $"AnimationLayer: 0x{animationLayer.Instance:X}" : "Animation");
                if (debug)
                {
                    ImGui.Text($" Current: {currentAnimation}");
                    ImGui.Text($" Frame: {animationLayer.CurrentFrame:0.000}/{animationLayer.MaxFrame:0.000}");
                    return;
                }
                else
                {
                    ImGui.Text($" Current: {currentAnimation.Lmt}.{currentAnimation.Id}, {(currentAction != null ? currentAction.Name : "None")}");
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("{Lmt}.{Id}, {Action}");
                        ImGui.EndTooltip();
                    }
                    ImGui.SetNextItemWidth(width * 0.8f);
                    ImGui.SliderFloat("Frame", ref animationLayer.CurrentFrame, 0.0f, animationLayer.MaxFrame, "%.3f");
                }
                bool animationPaused = animationLayer.Paused;
                if (ImGui.Checkbox("Paused", ref animationPaused))
                {
                    animationLayer.Paused = animationPaused;
                }
#if QUARANTINED_FEATURES
                ImGui.SameLine();
                float? lockedSpeed = animationLayer.LockedSpeed;
                float speed = lockedSpeed ?? animationLayer.Speed;
                bool animationLocked = lockedSpeed != null;
                if (ImGui.Checkbox("Set", ref animationLocked))
                {
                    if (animationLocked)
                    {
                        animationLayer.LockSpeed(speed);
                    }
                    else
                    {
                        animationLayer.UnlockSpeed();
                    }
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(width * 0.35f);
                if (ImGui.DragFloat($"Speed", ref speed, 0.01f, 0.0f, 0.0f, "%.3f"))
                {
                    if (animationLocked) animationLayer.LockSpeed(speed);
                }
#endif
            }
        }

        private void disableAllCollisionHooks()
        {
            if (disableExtraGravity)
            {
                disableExtraGravity = false;
                disableExtraGravityDisable();
            }
            if (disableGravity)
            {
                disableGravity = false;
                disableGravityDisable();
            }
            if (disableExtraCollision)
            {
                disableExtraCollision = false;
                disableExtraCollisionDisable();
            }
            if (disableCollision)
            {
                disableCollision = false;
                disableCollisionDisable();
            }
        }

        private void tryToDisableMod(Player? player)
        {
            disableAllCollisionHooks();
            if (player != null)
            {
                player.Rotation.X = 0.0f;
                player.Rotation.Z = 0.0f;
            }
            if (freeCamera)
            {
                disableFreeCamera();
            }
            for (int i = 0; i < 8; i++)
            {
                Viewport vp = CameraSystem.GetViewport(i);
                if (vp.Camera != null)
                {
                    vp.Camera.Move = true;
                }
                setViewportFadeObjects(i, true);
            }
            if (enableOffsetPerspective && disableFading)
            {
                setPassthroughEnabled(true);
            }
            if (disableCharacterFade)
            {
                noopCharacterFade.Disable();
            }
            if (enableUnderwaterCamera)
            {
                underwaterCameraDisable();
            }
            if (allowHotSpringsAnywhere)
            {
                jmpOverHotSpringsEval.Disable();
            }
            if (disableHotSpringsSteam)
            {
                jmpOverHotSpringsSteam.Disable();
            }
            if (ikForcedOff)
            {
                forceDisableIK.Disable();
            }
        }

        private void tryToEnableMod()
        {
            if (enableOffsetPerspective && disableFading)
            {
                setPassthroughEnabled(false);
            }
            if (disableCharacterFade)
            {
                noopCharacterFade.Enable();
            }
            if (enableUnderwaterCamera)
            {
                underwaterCameraEnable();
            }
            if (allowHotSpringsAnywhere)
            {
                jmpOverHotSpringsEval.Enable();
            }
            if (disableHotSpringsSteam)
            {
                jmpOverHotSpringsSteam.Enable();
            }
            if (ikForcedOff)
            {
                forceDisableIK.Enable();
            }
        }

        private void drawPlayerInfo(Player player, float width)
        {
            nint playerSettings = getPlayerSettings();

            ImGui.Text($"{Marshal.PtrToStringAnsi(playerSettings + 0x50)}");

            ImGui.Checkbox("Hide Weapon", ref hideWeapon);
            ImGui.SameLine();
            if (ImGui.Checkbox("Hide Knife", ref hideKnife))
            {
                checkKnifeHidden = true;
            }
            ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(0.0f, 6.0f));
            ImGui.Text("Head Armor Display");
            // MonsterHunterWorld.exe+11A2967 - cmp byte ptr [rax+001403E1],01
            ref byte showHeadArmor = ref MemoryUtil.GetRef<byte>(playerSettings + 0x1403E1);
            int showHeadArmorInt = showHeadArmor;
            ImGui.SameLine();
            ImGui.SetCursorPos(ImGui.GetCursorPos() - new Vector2(0.0f, 6.0f));
            if (ImGui.RadioButton("Show", ref showHeadArmorInt, 0))
            {
                showHeadArmor = (byte)showHeadArmorInt;
            }
            ImGui.SameLine();
            ImGui.SetCursorPos(ImGui.GetCursorPos() - new Vector2(0.0f, 6.0f));
            if (ImGui.RadioButton("Hide", ref showHeadArmorInt, 1))
            {
                showHeadArmor = (byte)showHeadArmorInt;
            }
            ImGui.SameLine();
            ImGui.SetCursorPos(ImGui.GetCursorPos() - new Vector2(0.0f, 6.0f));
            if (ImGui.RadioButton("Hide in Cutscenes", ref showHeadArmorInt, 2))
            {
                showHeadArmor = (byte)showHeadArmorInt;
            }

            ImGui.Separator();



            bool armorStateRefreshed = false;

            ImGui.PushID("Joints");
            if (ImGui.CollapsingHeader("Joints"))
            {
                if (!armorStateRefreshed)
                {
                    refreshArmorState(player);
                    armorStateRefreshed = true;
                }
                ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(0.0f, 6.0f));
#if QUARANTINED_FEATURES
                ImGui.Text($"Joint Offsets: {bodyOffsets.Count + faceOffsets.Count}, IK Offsets: {ikOffsets.Count}");
#else
                ImGui.Text($"Joint Offsets: {bodyOffsets.Count + faceOffsets.Count}");
#endif
                ImGui.SameLine();
                ImGui.SetCursorPos(ImGui.GetCursorPos() - new Vector2(0.0f, 6.0f));
                if (ImGui.Button("Clear"))
                {
                    dropJointOffsets();
                }
                ImGui.SameLine();
                /*
                ref byte ikDisabledB = ref MemoryUtil.GetRef<byte>(MemoryUtil.Read<nint>(player.Instance + 0x990) + 0x5C);
                bool ikDisabled = ikDisabledB == 0x0;
                if (ImGui.Checkbox("Disable IK", ref ikDisabled))
                {
                    ikDisabledB = ByteFlag(!ikDisabled);
                }
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Mostly disable inverse kinematics on your player. To rotate ankles, you need to use Force Disable IK.");
                    ImGui.EndTooltip();
                }
                ImGui.SameLine();
                */
                ImGui.SetCursorPos(ImGui.GetCursorPos() - new Vector2(0.0f, 6.0f));
                if (ImGui.Checkbox("Disable IK", ref ikForcedOff))
                {
                    if (ikForcedOff)
                    {
                        forceDisableIK.Enable();
                    }
                    else
                    {
                        forceDisableIK.Disable();
                    }
                }
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Forcefully disable inverse kinematics.");
                    ImGui.EndTooltip();
                }
                drawJoints(width);
                ImGui.Separator();
            }
            ImGui.PopID();

            ImGui.PushID("Parts");
            if (ImGui.CollapsingHeader("Armor Parts"))
            {
                if (!armorStateRefreshed)
                {
                    refreshArmorState(player);
                    armorStateRefreshed = true;
                }
                bool expaned = ImGui.CollapsingHeader("Helmet");
                ImGui.SameLine();
                ImGui.Text($"(id: {MemoryUtil.Read<int>(player.Instance + 0x1373C):X})");
                if (expaned)
                {
                    drawModelParts(playerArmor[Armor.Helmet], 0x0, Armor.Helmet);
                    ImGui.Separator();
                }
                expaned = ImGui.CollapsingHeader("Body");
                ImGui.SameLine();
                ImGui.Text($"(id: {MemoryUtil.Read<int>(player.Instance + 0x13740):X})");
                if (expaned)
                {
                    drawModelParts(playerArmor[Armor.Body]);
                    ImGui.Separator();
                }
                expaned = ImGui.CollapsingHeader("Arms");
                ImGui.SameLine();
                ImGui.Text($"(id: {MemoryUtil.Read<int>(player.Instance + 0x13744):X})");
                if (expaned)
                {
                    drawModelParts(playerArmor[Armor.Arm]);
                    ImGui.Separator();
                }
                expaned = ImGui.CollapsingHeader("Waist");
                ImGui.SameLine();
                ImGui.Text($"(id: {MemoryUtil.Read<int>(player.Instance + 0x13748):X})");
                if (expaned)
                {
                    drawModelParts(playerArmor[Armor.Waist]);
                    ImGui.Separator();
                }
                expaned = ImGui.CollapsingHeader("Legs");
                ImGui.SameLine();
                ImGui.Text($"(id: {MemoryUtil.Read<int>(player.Instance + 0x1374C):X})");
                if (expaned)
                {
                    drawModelParts(playerArmor[Armor.Leg]);
                    ImGui.Separator();
                }
                /*
                expaned = ImGui.CollapsingHeader("Weapon");
                if (expaned)
                {
                    drawModelParts(playerArmor[Armor.Weapon]);
                    ImGui.Separator();
                }
                */
                expaned = ImGui.CollapsingHeader("Slinger");
                ImGui.SameLine();
                ImGui.Text($"(id: {MemoryUtil.Read<int>(player.Instance + 0x13D8C):X}_{MemoryUtil.Read<int>(player.Instance + 0x13D90):X})");
                if (expaned)
                {
                    drawModelParts(playerArmor[Armor.Slinger]);
                    ImGui.Separator();
                }
                if (ImGui.CollapsingHeader("Hair"))
                {
                    drawModelParts(playerArmor[Armor.Hair], 0x0, Armor.Hair);
                    ImGui.Separator();
                }
                if (ImGui.CollapsingHeader("Face"))
                {
                    drawModelParts(playerArmor[Armor.Face], 0x0, Armor.Face);
                    ImGui.Separator();
                }
                if (ImGui.CollapsingHeader("Eye Lens"))
                {
                    drawModelParts(playerArmor[Armor.EyeLens], 0x0, Armor.EyeLens);
                }
                ImGui.Separator();
            }
            ImGui.PopID();

            nint controlsAddr = MemoryUtil.Read<nint>(player.Instance + 0x12608);
            nint zoneStateAddr = MemoryUtil.Read<nint>(0x1451C42B8);
            ZoneState zoneState = ZoneState.Unknown;
            if (controlsAddr != 0x0 && zoneStateAddr != 0x0)
            {
                ref byte passiveFlag = ref MemoryUtil.GetRef<byte>(player.Instance + 0x7626);
                ref byte passiveFlag2 = ref MemoryUtil.GetRef<byte>(MemoryUtil.Read<nint>(player.Instance + 0x7D20) + 0x9B8);
                bool passive = passiveFlag == 0x1 && passiveFlag2 == 0x1;
                if (ImGui.Checkbox("Passive", ref passive))
                {
                    passiveFlag = ByteFlag(passive);
                    passiveFlag2 = ByteFlag(passive);
                }
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Passive: Calm camera and your hunter looks neutral/smiles.\nCombat: Intense camera and your hunter looks angry.\nIf you have Passive set and Combat Controls on, or vice versa, expect glitchy behavior.");
                    ImGui.EndTooltip();
                }
                bool combatControls = MemoryUtil.Read<byte>(controlsAddr + 0xB18) == 0x80;
                zoneState = combatControls ? ZoneState.Combat : ZoneState.Hub;
#if !QUARANTINED_FEATURES
                ImGui.Text($"Current Zone State: {((MemoryUtil.GetRef<byte>(zoneStateAddr + 0xD2EA) == 0x1) ? "Hub" : "Combat")}");
#else
                if (ImGui.Checkbox("Combat Controls", ref combatControls))
                {
                    passiveFlag = ByteFlag(!combatControls);
                    passiveFlag2 = ByteFlag(!combatControls);
                    setPlayerController1.Invoke(player.Instance);
                    setPlayerController5.Invoke(controlsAddr);
                }
                int zoneStateInt = (int)forceZoneState;
                ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(0.0f, 6.0f));
                ImGui.Text($"Force Zone State (Current: {((MemoryUtil.GetRef<byte>(zoneStateAddr + 0xD2EA) == 0x1) ? "Hub" : "Combat")})");
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("This will apply when moving to a different area.");
                    ImGui.EndTooltip();
                }
                ImGui.SameLine();
                ImGui.SetCursorPos(ImGui.GetCursorPos() - new Vector2(0.0f, 6.0f));
                ImGui.RadioButton("Off", ref zoneStateInt, 0);
                ImGui.SameLine();
                ImGui.SetCursorPos(ImGui.GetCursorPos() - new Vector2(0.0f, 6.0f));
                ImGui.RadioButton("Hub", ref zoneStateInt, 1);
                ImGui.SameLine();
                ImGui.SetCursorPos(ImGui.GetCursorPos() - new Vector2(0.0f, 6.0f));
                ImGui.RadioButton("Combat", ref zoneStateInt, 2);
                forceZoneState = (ZoneState)zoneStateInt;
                if (ImGui.Button("Run Change Zone State"))
                {
                    zoneStateManualInvoke = true;
                    setZoneState.Invoke(player.Instance, 0x000106C0); // or 0x00010780.
                }
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("You can use this to apply the value of Force Zone State.\nIf you're in a map and Force Zone State is set to Off or Combat, this will send you back to camp.");
                    ImGui.EndTooltip();
                }
                if (ImGui.Checkbox("Force Passive in Combat Zones", ref forcePassiveInCombatZone))
                {
                    if (forcePassiveInCombatZone)
                    {
                        zoneStateForcePassive1.Enable();
                        zoneStateForcePassive2.Enable();
                    }
                    else
                    {
                        zoneStateForcePassive1.Disable();
                        zoneStateForcePassive2.Disable();
                    }
                }
                if (ImGui.Checkbox("Force Combat in Passive Zones", ref forceCombatInPassiveZone))
                {
                    if (forceCombatInPassiveZone)
                    {
                        zoneStateForceCombat1.Enable();
                        zoneStateForceCombat2.Enable();
                    }
                    else
                    {
                        zoneStateForceCombat1.Disable();
                        zoneStateForceCombat2.Disable();
                    }
                }
#endif
            }

            if (zoneState != ZoneState.Combat)
            {
                ImGui.PushItemFlag(ImGuiItemFlags.Disabled, true);
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f);
            }
            if (ImGui.Checkbox("Force Crawl", ref enableCrawl))
            {
                if (enableCrawl)
                {
                    MemoryUtil.GetRef<float>(psuedoObject2 + 0x70) = player.Position.X + player.Forward.X;
                    MemoryUtil.GetRef<float>(psuedoObject2 + 0x74) = player.Position.Y + player.Forward.Y;
                    MemoryUtil.GetRef<float>(psuedoObject2 + 0x78) = player.Position.Z + player.Forward.Z;

                    Quaternion playerRotation = new Quaternion(player.Forward.X, player.Forward.Y, player.Forward.Z, 0.0f);
                    Quaternion objectRotation = getRight(playerRotation);

                    MemoryUtil.GetRef<float>(psuedoObject2 + 0x40) = objectRotation.X;
                    MemoryUtil.GetRef<float>(psuedoObject2 + 0x44) = objectRotation.Y;
                    MemoryUtil.GetRef<float>(psuedoObject2 + 0x48) = objectRotation.Z;
                    MemoryUtil.GetRef<float>(psuedoObject2 + 0x4C) = objectRotation.W;

                    MemoryUtil.GetRef<float>(psuedoObject2 + 0x50) = 0.0f;
                    MemoryUtil.GetRef<float>(psuedoObject2 + 0x54) = 1.0f;
                    MemoryUtil.GetRef<float>(psuedoObject2 + 0x58) = 0.0f;
                    MemoryUtil.GetRef<float>(psuedoObject2 + 0x5C) = 0.0f;

                    MemoryUtil.GetRef<float>(psuedoObject2 + 0x60) = playerRotation.X;
                    MemoryUtil.GetRef<float>(psuedoObject2 + 0x64) = playerRotation.Y;
                    MemoryUtil.GetRef<float>(psuedoObject2 + 0x68) = playerRotation.Z;
                    MemoryUtil.GetRef<float>(psuedoObject2 + 0x6C) = playerRotation.W;
                }
            }
            if (zoneState != ZoneState.Combat)
            {
                ImGui.PopItemFlag();
                ImGui.PopStyleVar();
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("You have to be out in a map to use this.");
                    ImGui.EndTooltip();
                }
            }
            else
            {
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Simulate crawling under an object in the direction your character was facing when enabled.");
                    ImGui.EndTooltip();
                }
            }

            if (zoneState != ZoneState.Hub)
            {
                ImGui.PushItemFlag(ImGuiItemFlags.Disabled, true);
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f);
            }
            if (ImGui.Button("Sit in Hot Springs"))
            {
                // Run sit in hot springs action (top of function).
                // MonsterHunterWorld.exe+17601F0 - mov [rsp+08],rbx
                MemoryUtil.WriteBytes(player.ActionController.Instance + 0xC0, [0x65, 0x00, 0x00, 0x00]);
                MemoryUtil.WriteBytes(player.ActionController.Instance + 0xBC, [0x01, 0x00, 0x00, 0x00]);
            }
            if (zoneState != ZoneState.Hub)
            {
                ImGui.PopItemFlag();
                ImGui.PopStyleVar();
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("You have to be in a hub area to use this.");
                    ImGui.EndTooltip();
                }
            }

            if (ImGui.Checkbox("Allow Hot Springs Anywhere", ref allowHotSpringsAnywhere))
            {
                if (allowHotSpringsAnywhere)
                {
                    jmpOverHotSpringsEval.Enable();
                }
                else
                {
                    jmpOverHotSpringsEval.Disable();
                }
            }
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text("Disable check for being about halfway submerged in water to stay sitting.");
                ImGui.EndTooltip();
            }

            if (ImGui.Checkbox("Disable Hot Springs Steam", ref disableHotSpringsSteam))
            {
                if (disableHotSpringsSteam)
                {
                    jmpOverHotSpringsSteam.Enable();
                }
                else
                {
                    jmpOverHotSpringsSteam.Disable();
                }
            }
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text("It will take a couple of seconds to fade away.");
                ImGui.EndTooltip();
            }

            ref float playerOpacity = ref MemoryUtil.GetRef<float>(player.Instance + 0x78E0);
            ImGui.SetNextItemWidth(width * 0.325f);
            if (ImGui.DragFloat("Opacity", ref playerOpacity, 0.01f, 0.0f, Single.MaxValue))
            {
                playerOpacityOverride = playerOpacity;
            }

            ImGui.PushID("Wetness");
            if (ImGui.CollapsingHeader("Wetness"))
            {
                nint wetnessAddr = player.Instance + 0x13BD0;
                ImGui.PushItemWidth(width * 0.15f);
                ImGui.DragFloat("Head", ref MemoryUtil.GetRef<float>(wetnessAddr), 0.005f);
                ImGui.SameLine();
                ImGui.DragFloat("Body", ref MemoryUtil.GetRef<float>(wetnessAddr + 0x28), 0.005f);
                ImGui.DragFloat("Waist", ref MemoryUtil.GetRef<float>(wetnessAddr + 0x50), 0.005f);
                ImGui.SameLine();
                ImGui.DragFloat("Legs", ref MemoryUtil.GetRef<float>(wetnessAddr + 0x78), 0.005f);
                ImGui.PopItemWidth();
                ImGui.SetNextItemWidth(width * 0.35f);
                if (ImGui.DragFloat("Whole Body", ref wholeBodyWetness, 0.005f))
                {
                    if (overridePlayerWetness)
                    {
                        writePlayerWholeBodyWetness(wetnessAddr);
                    }
                }
                if (ImGui.Button("Reset"))
                {
                    wholeBodyWetness = 0.0f;
                    writePlayerWholeBodyWetness(wetnessAddr);
                }
                ImGui.SameLine();
                if (ImGui.Checkbox("Override Player Wetness", ref overridePlayerWetness))
                {
                    if (overridePlayerWetness)
                    {
                        overridePlayerWetnessEnable();
                        writePlayerWholeBodyWetness(wetnessAddr);
                    }
                    else
                    {
                        overridePlayerWetnessDisable();
                    }
                }
                ImGui.Separator();
            }
            ImGui.PopID();

            ImGui.PushItemWidth(width * 0.75f);
            ImGui.DragFloat3("Position", ref player.Position, 0.5f);
            Vector3 forward = new Vector3(player.Forward.X, player.Forward.Y, player.Forward.Z);
            ImGui.DragFloat3("Forward", ref forward, 0.0f);
            Vector4 rotation = new Vector4(player.Rotation.X, player.Rotation.Y, player.Rotation.Z, player.Rotation.W);
            ImGui.SliderFloat4("Rotation", ref rotation, -1.0f, 1.0f);
            Vector3 rotationOffset = new Vector3();
            if (ImGui.DragFloat3("Rotation Offset", ref rotationOffset, 0.0025f))
            {
                Quaternion qRotation = new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
                qRotation *= Quaternion.CreateFromYawPitchRoll(rotationOffset.X, rotationOffset.Y, rotationOffset.Z);
                player.Rotation = new MtQuaternion(qRotation.X, qRotation.Y, qRotation.Z, qRotation.W);
            }
            ImGui.PopItemWidth();
            if (ImGui.Button("Reset"))
            {
                player.Rotation.X = 0.0f;
                player.Rotation.Z = 0.0f;
            }
            if (ImGui.Checkbox("Disable Collision", ref disableCollision))
            {
                if (disableCollision)
                {
                    disableCollisionEnable();
                }
                else
                {
                    if (disableExtraGravity)
                    {
                        disableExtraGravity = false;
                        disableExtraGravityDisable();
                    }
                    if (disableGravity)
                    {
                        disableGravity = false;
                        disableGravityDisable();
                    }
                    if (disableExtraCollision)
                    {
                        disableExtraCollision = false;
                        disableExtraCollisionDisable();
                    }
                    disableCollisionDisable();
                }
            }
            if (!disableCollision)
            {
                ImGui.PushItemFlag(ImGuiItemFlags.Disabled, true);
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f);
            }
            if (ImGui.Checkbox("Disable Another Y Collision", ref disableExtraCollision))
            {
                if (disableExtraCollision)
                {
                    disableExtraCollisionEnable();
                }
                else
                {
                    if (disableExtraGravity)
                    {
                        disableExtraGravity = false;
                        disableExtraGravityDisable();
                    }
                    if (disableGravity)
                    {
                        disableGravity = false;
                        disableGravityDisable();
                    }
                    disableExtraCollisionDisable();
                }
            }
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text("This will make your legs goofy, but is needed to get on top of some things.");
                ImGui.EndTooltip();
            }
            if (!disableCollision)
            {
                ImGui.PopItemFlag();
                ImGui.PopStyleVar();
            }
            if (!disableExtraCollision)
            {
                ImGui.PushItemFlag(ImGuiItemFlags.Disabled, true);
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f);
            }
            if (ImGui.Checkbox("Disable Y Gravity", ref disableGravity))
            {
                if (disableGravity)
                {
                    disableGravityEnable();
                }
                else
                {
                    if (disableExtraGravity)
                    {
                        disableExtraGravity = false;
                        disableExtraGravityDisable();
                    }
                    disableGravityDisable();
                }
            }
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text("Avoid falling when going too high (Y axis).");
                ImGui.EndTooltip();
            }
            if (!disableExtraCollision)
            {
                ImGui.PopItemFlag();
                ImGui.PopStyleVar();
            }
            if (!disableGravity)
            {
                ImGui.PushItemFlag(ImGuiItemFlags.Disabled, true);
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f);
            }
            if (ImGui.Checkbox("Disable More Gravity", ref disableExtraGravity))
            {
                if (disableExtraGravity)
                {
                    disableExtraGravityEnable();
                }
                else
                {
                    disableExtraGravityDisable();
                }
            }
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text("This can stop you from falling when going out of bounds and/or rotating.");
                ImGui.EndTooltip();
            }
            if (!disableGravity)
            {
                ImGui.PopItemFlag();
                ImGui.PopStyleVar();
            }
            drawAnimationInfo(player, width, false);
        }

        public void OnImGuiRender()
        {
#if HOOK_ORDER_ASSERTS
            //debugLog($"OnImGuiRender() @ {frameTick}");
#endif

            float width = ImGui.GetWindowWidth();
            width /= width / 600.0f;

            Config config = ConfigManager.GetConfig<Config>(this);

            // If we use vCamera or pCamera here, we need to ensure they're not invalid.
            Player? player = checkPlayerChange();
            checkCurrentVisibleCamera(player);

            if (ImGui.Checkbox("Disable Mod", ref disableMod))
            {
                if (disableMod)
                {
                    tryToDisableMod(player);
                }
                else
                {
                    tryToEnableMod();
                }
                config.DisableMod = disableMod;
                ConfigManager.SaveConfig<Config>(this);
            }
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text("Try to disable as much as possible.");
                ImGui.EndTooltip();
            }
            if (disableMod)
            {
                return;
            }

            ImGui.Separator();

            if (ImGui.Checkbox("Enable Perspective Camera", ref enableOffsetPerspective))
            {
                config.PerspectiveCameraEnabled = enableOffsetPerspective;
                if (enableOffsetPerspective)
                {
                    if (config.Selected != "")
                    {
                        setPerspectivePreset(config.Presets[config.Selected]);
                    }
                }
                else if (!freeCamera && disableFading)
                {
                    setDisableFading(false);
                }
                ConfigManager.SaveConfig<Config>(this);
            }
            if (enableOffsetPerspective)
            {
                ImGui.PushID("Perspective");
                ImGui.PushItemWidth(width * 0.575f);
                ImGui.DragFloat("Field of View", ref cameraFov, 0.4f, 1.0f, 179.0f);
                ImGui.DragFloat("Roll", ref cameraRoll, 0.25f);
                if (freeCamera)
                {
                    ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f);
                }
                ImGui.DragFloat("Forward", ref cameraForward, 0.25f);
                ImGui.DragFloat("Right", ref cameraRight, 0.25f);
                ImGui.DragFloat("Up", ref cameraUp, 0.025f);
                ImGui.PopItemWidth();
                if (ImGui.Checkbox("Disable Fading", ref disableFading))
                {
                    setDisableFading(disableFading);
                }
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Disable transparency fading when the camera is close to something.\nApplies to objects, monsters, players, palicos and NPCs.\nEnabled by default when entering free camera.");
                    ImGui.EndTooltip();
                }
                if (freeCamera)
                {
                    ImGui.PopStyleVar();
                }
                if (ImGui.Button("Default"))
                {
                    setPerspectivePreset(Config.DefaultPreset);
                    config.Selected = "";
                    ConfigManager.SaveConfig<Config>(this);
                }
                ImGui.SameLine();
                ImGui.PushItemWidth(width * 0.2f);
                ImGui.InputText("##Preset Name", ref typedPresetName, 99);
                ImGui.SameLine();
                if (ImGui.Button("Save"))
                {
                    string name = typedPresetName;
                    if (name != "")
                    {
                        Config.Preset preset = new Config.Preset()
                        {
                            FieldOfView = cameraFov,
                            Roll = cameraRoll,
                            Forward = cameraForward,
                            Right = cameraRight,
                            Up = cameraUp,
                            DisableFading = disableFading
                        };
                        if (config.Presets.ContainsKey(name))
                        {
                            config.Presets[name] = preset;
                        }
                        else
                        {
                            config.Presets.Add(name, preset);
                        }
                        config.Selected = name;
                        ConfigManager.SaveConfig<Config>(this);
                    }
                }
                ImGui.SameLine();
                if (ImGui.BeginCombo("##Preset", config.Selected))
                {
                    Dictionary<string, Config.Preset>.KeyCollection presetKeys = config.Presets.Keys;
                    for (int i = 0; i < presetKeys.Count; i++)
                    {
                        string presetKey = presetKeys.ElementAt(i);
                        bool isSelected = presetKey == config.Selected;
                        if (ImGui.Selectable(presetKey, isSelected))
                        {
                            config.Selected = presetKey;
                            setPerspectivePreset(config.Presets[config.Selected]);
                            ConfigManager.SaveConfig<Config>(this);
                        }
                        if (isSelected) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
                ImGui.PopItemWidth();
                ImGui.SameLine();
                if (ImGui.Button("Delete"))
                {
                    if (config.Selected != "")
                    {
                        config.Presets.Remove(config.Selected);
                        config.Selected = "";
                        ConfigManager.SaveConfig<Config>(this);
                    }
                }
                ImGui.PopID();
            }

            ImGui.Separator();

            ImGui.Checkbox("Enable Free Camera", ref enableFreeCamera);
            ImGui.PushID("Settings");
            ImGui.PushItemWidth(width * 0.575f);
            ImGui.DragFloat("Camera Speed", ref cameraSpeed, 0.01f, 0.0f, 0.0f, "%.4f");
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text("Movement speed in free camera.");
                ImGui.EndTooltip();
            }
            ImGui.DragFloat("Speed Modifier", ref cameraSpeedModifier, 0.01f, 0.0f, 0.0f, "%.4f");
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text("Value to multiply speed by when LT is held.");
                ImGui.EndTooltip();
            }
            ImGui.DragFloat("Camera Sensitivity", ref cameraSensitivity, 0.001f, 0.0f, 0.0f, "%.4f");
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text("Look sensitivity in free camera.");
                ImGui.EndTooltip();
            }
            ImGui.DragFloat("Zoom Speed", ref cameraZoomSpeed, 0.01f, 0.0f, 0.0f, "%.4f");
            if (ImGui.InputFloat("Pitch Limit", ref cameraPitchLimit, 0.0f, 0.0f, null, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                if (cameraPitchLimit != -1.0f)
                {
                    cameraPitchLimit = Math.Clamp(cameraPitchLimit, 0.0f, Config.Settings.MAX_PITCH_LIMIT);
                }
            }
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text($"-1.0 = Wrap around (go upside down), Max: {Config.Settings.MAX_PITCH_LIMIT:0.00}.");
                ImGui.EndTooltip();
            }
            ImGui.DragInt("Stick Deadzone", ref stickDeadzone, 5, 0);
            ImGui.PopItemWidth();
            /*
            if (ImGui.Button("Default"))
            {
                cameraSpeed = Config.Settings.DEFAULT_SPEED;
                cameraSpeedModifier = Config.Settings.DEFAULT_SPEED_MODIFIER;
                cameraSensitivity = Config.Settings.DEFAULT_SENSITIVITY;
                cameraZoomSpeed = Config.Settings.DEFAULT_ZOOM_SPEED;
                cameraPitchLimit = Config.Settings.DEFAULT_PITCH_LIMIT;
                stickDeadzone = Config.Settings.DEFAULT_DEADZONE;
                alternateNearClip = Config.Settings.DEFAULT_ALT_NEAR_CLIP;
            }
            ImGui.SameLine();
            */
            if (ImGui.Button("Reset"))
            {
                Config.Settings camera = config.Camera;
                cameraSpeed = camera.Speed;
                cameraSpeedModifier = camera.SpeedModifier;
                cameraSensitivity = camera.Sensitivity;
                cameraZoomSpeed = camera.ZoomSpeed;
                cameraPitchLimit = camera.PitchLimit;
                stickDeadzone = camera.StickDeadzone;
                alternateNearClip = camera.AlternateNearClip;
            }
            ImGui.SameLine();
            if (ImGui.Button("Save"))
            {
                Config.Settings camera = config.Camera;
                camera.Speed = cameraSpeed;
                camera.SpeedModifier = cameraSpeedModifier;
                camera.Sensitivity = cameraSensitivity;
                camera.ZoomSpeed = cameraZoomSpeed;
                camera.PitchLimit = cameraPitchLimit;
                camera.StickDeadzone = stickDeadzone;
                config.Camera = camera;
                ConfigManager.SaveConfig<Config>(this);
            }
            ImGui.PopID();

            ImGui.Separator();

            ImGui.PushID("Toggles");
            ImGui.Text("Toggles");

            bool uiWasToggled = uiToggled;
            if (ImGui.Checkbox("Disable UI", ref uiWasToggled))
            {
                toggleUi();
            }
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text("The scoutfly marker on menus will still be visible.");
                ImGui.EndTooltip();
            }
            ImGui.SameLine();
            if (!freeCamera || freeCameraFallback)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f);
            }
            ImGui.Checkbox("Unlock Input", ref unlockInputToggled);
            ImGui.Checkbox("Unlock Player Movement", ref unlockMovementToggled);
            ImGui.Checkbox("Lock Vertical Movement and Apply Speed Modifier", ref lockVerticalToggled);
            if (!freeCamera || freeCameraFallback)
            {
                ImGui.PopStyleVar();
            }
            if (!freeCamera)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f);
            }
            ImGui.Checkbox("Orbit Player", ref orbitPlayer);
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text("Use left stick up/down to move the camera forward/back and d-pad up/down to set the Y target.");
                ImGui.EndTooltip();
            }
            if (!freeCamera)
            {
                ImGui.PopStyleVar();
            }
            ImGui.SameLine();
            if (!freeCamera || freeCameraFallback)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f);
            }
            ImGui.Checkbox("Ignore Camera Direction", ref orbitIgnoreCamera);
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text("Left stick right/left will gradually shift your forward direction.");
                ImGui.EndTooltip();
            }
            ImGui.SameLine();
            ImGui.Checkbox("Decouple Movement From Look", ref orbitDecoupleMovementFromLook);
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text("Rewritten from scratch, independent of \"Ignore Camera Direction\"\nabove: rotates the raw left-stick input so it keeps moving the\nbody relative to the stable A/B/C base direction even while\nR-stick free-look has turned the view away from it. If holding a\nlook angle makes movement pull *further* off-axis instead of\ngoing straight, check \"Invert\" below.");
                ImGui.EndTooltip();
            }
            ImGui.SameLine();
            ImGui.Checkbox("Invert##DecoupleMovement", ref orbitDecoupleMovementInvert);
            if (!freeCamera || freeCameraFallback)
            {
                ImGui.PopStyleVar();
            }
            if (vCamera != null)
            {
                ImGui.PushItemWidth(width * 0.12f);
                bool altNearClipSet = orbitAltNearClipToggled;
                if (ImGui.Checkbox("Alternate Near Clip", ref altNearClipSet))
                {
                    orbitAltNearClipToggled = altNearClipSet;
                    setNearClip(vCamera, altNearClipSet);
                }
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Changing the near clip will break the rendering of shadows and various effects.\nTemporarily reducing the near clip can allow you to move the camera closer to things without clipping.");
                    ImGui.EndTooltip();
                }
                ImGui.SameLine();
                if (ImGui.DragFloat("##Alternate Near Clip", ref alternateNearClip, 0.05f, 0.001f, 0.0f))
                {
                    if (altNearClipSet)
                    {
                        vCamera.NearClip = alternateNearClip;
                    }
                }
                ImGui.PopItemWidth();
                ImGui.SameLine();
                if (ImGui.Button("Save"))
                {
                    Config.Settings camera = config.Camera;
                    camera.AlternateNearClip = alternateNearClip;
                    config.Camera = camera;
                    ConfigManager.SaveConfig<Config>(this);
                }
            }

            ImGui.Separator();

            ImGui.Text("When Close to the Camera");
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text("Set Disable Fading in the camera preset above for these to persist.");
                ImGui.EndTooltip();
            }
            bool disableFadingObjects = !getPassthroughEnabled();
            if (ImGui.Checkbox("Disable Fading of Objects/Monsters", ref disableFadingObjects))
            {
                setPassthroughEnabled(!disableFadingObjects);
            }
            if (ImGui.Checkbox("Disable Fading of Player/Palico/NPCs", ref disableCharacterFade))
            {
                if (disableCharacterFade)
                {
                    noopCharacterFade.Enable();
                }
                else
                {
                    noopCharacterFade.Disable();
                }
            }
            ImGui.PopID();

            ImGui.Separator();

            ImGui.PushID("Binds");
            if (ImGui.CollapsingHeader("Binds"))
            {
                if (ImGui.Checkbox("Toggle Free Camera", ref enableCombo))
                {
                    Config.Settings.Binds binds = config.Binds;
                    binds.EnableCombo = enableCombo;
                    config.Binds = binds;
                    ConfigManager.SaveConfig<Config>(this);
                }
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Hold the first button (Button1) then press the second (Button2).");
                    ImGui.EndTooltip();
                }
                ImGui.SameLine();
                if (freeCameraCombo == null)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, 0xFF0000FF);
                }
                ImGui.SetNextItemWidth(width * 0.15f);
                ImGui.InputText("##Toggle Free Camera", ref typedCombo, 12);
                if (freeCameraCombo == null)
                {
                    ImGui.PopStyleColor();
                }
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Form: RT+LB, R3+L2, A+LStick, Up+RT, Cross+Triangle, etc.");
                    ImGui.EndTooltip();
                }
                ImGui.SameLine();
                if (ImGui.Button("Save"))
                {
                    string comboString = typedCombo.Replace("+", ",");
                    freeCameraCombo = Config.ParseCombo(comboString);
                    if (freeCameraCombo != null)
                    {
                        Config.Settings.Binds binds = config.Binds;
                        binds.FreeCameraCombo = comboString;
                        config.Binds = binds;
                        ConfigManager.SaveConfig<Config>(this);
                    }
                }
                ImGui.SameLine();
                if (ImGui.Checkbox("Disable Button1 Unless Button2 is Held", ref disableComboButton1))
                {
                    comboButton1Down = false;
                    Config.Settings.Binds binds = config.Binds;
                    binds.DisableComboButton1UnlessButton2Held = disableComboButton1;
                    config.Binds = binds;
                    ConfigManager.SaveConfig<Config>(this);
                }
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("For example, if bound to RStick+LT, right stick presses will ignored by the game unless you're holding the left trigger.\nIn that case, it can let you avoid switching the targeted monster when toggling free camera.");
                    ImGui.EndTooltip();
                }

                ImGui.Text("Global");
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Button1 refers to the first button of the Toggle Free Camera bind.");
                    ImGui.EndTooltip();
                }
                if (ImGui.BeginTable("Global Binds", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Hold Button1 + Press Select");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Toggle UI");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Hold Button1 + Press D-Pad Up/Down");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Select Perspective Camera Preset");

                    ImGui.EndTable();
                }

                ImGui.Text("While in Free Camera | ( ) = Toggle");
                if (ImGui.BeginTable("Binds", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Hold Button1 + Press LB");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Toggle Unlock Input");
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("Attempts to apply to every controller input except left stick player movement.");
                        ImGui.EndTooltip();
                    }

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Hold RT (+ Press LB)");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Unlock Player Movement and Freeze Camera Movement");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Hold LT (+ Press LB)");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Lock Vertical Camera Movement and Apply Speed Modifier");

                    ImGui.EndTable();
                }

                ImGui.Text("Disabled While Input to the Game is Unlocked");
                if (ImGui.BeginTable("More Binds", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("D-Pad Up/Down");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Translate Camera Up/Down");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("D-Pad Left/Right");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Roll Camera");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Hold LT + RT");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Zoom with Left Stick Up/Down");
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("If you have Depth of Field enabled in Advanced Graphics Settings, this will also move the focal point.");
                        ImGui.EndTooltip();
                    }

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Press Y");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Open Gestures Menu");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Hold Select");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(" + Press Y");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Open Poses Menu");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(" + Press X");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Toggle Alternate Near Clip");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Hold RB");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(" + Press D-Pad Right");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Reset Roll");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(" + Press D-Pad Up");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Reset Zoom");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(" + Press D-Pad Left");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Toggle Freeze Game");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(" + Press D-Pad Down");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Teleport Player to Camera Position");

                    ImGui.EndTable();
                }

                ImGui.Separator();
            }
            ImGui.PopID();

#if MOUSE_AND_KEYBOARD_LAYER
            ImGui.PushID("Mouse/Keyboard");
            if (ImGui.CollapsingHeader("Mouse & Keyboard"))
            {
                ImGui.PushItemWidth(width * 0.2f);
                if (ImGui.Checkbox("Enable Mouse", ref mouseEnabled))
                {
                    Config.Settings.Binds binds = config.Binds;
                    binds.EnableMouse = mouseEnabled;
                    config.Binds = binds;
                    ConfigManager.SaveConfig<Config>(this);
                }
                ImGui.SameLine();
                if (ImGui.DragFloat("Mouse Sensitivity", ref mouseSensitivity, 0.001f, 0.0f, 0.0f, "%.4f"))
                {
                    Config.Settings.Binds binds = config.Binds;
                    binds.MouseSensitivity = mouseSensitivity;
                    config.Binds = binds;
                    ConfigManager.SaveConfig<Config>(this);
                }
                if (ImGui.Checkbox("Enable Keyboard", ref keyboardEnabled))
                {
                    Config.Settings.Binds binds = config.Binds;
                    binds.EnableKeyboard = keyboardEnabled;
                    config.Binds = binds;
                    ConfigManager.SaveConfig<Config>(this);
                }
                ImGui.SameLine();
                if (ImGui.DragInt("Keyboard Look Sensitivity", ref keyboardLookValue, 20, 0, Int16.MaxValue))
                {
                    Config.Settings.Binds binds = config.Binds;
                    binds.KeyboardLookSensitivity = keyboardLookValue;
                    config.Binds = binds;
                    ConfigManager.SaveConfig<Config>(this);
                }
                ImGui.PopItemWidth();
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Value that gets sent as a simulated joystick movement. Range: [0, 32767].");
                    ImGui.EndTooltip();
                }
                if (ImGui.BeginTable("Keyboard Binds", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Num 0");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Toggle Free Camera");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Arrow Up/Down/Left/Right");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Move Camera");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Num 8/2/4/6");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Look");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Num 7/1");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Translate Up/Down");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Num 9/3");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Roll Camera");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Num *");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Reset Roll");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Num -/+");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Zoom");

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Num .");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Toggle UI");

                    /*
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Num /");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text("Toggle Depth of Field");
                    */

                    ImGui.EndTable();
                }

                ImGui.Separator();
            }
            ImGui.PopID();
#endif

            ImGui.PushID("Viewport");
            if (ImGui.CollapsingHeader("Viewport") && vCameraViewportIndex >= 0)
            {
                drawViewportInfo(vCameraViewportIndex, width, config, false);
                ImGui.Separator();
            }
            ImGui.PopID();

            if (player == null)
            {
                player = getPlayerFromSaveSlot();
            }
            else
            {
                saveSlotIndex = -1;
            }

            ImGui.PushID("Player");
            if (ImGui.CollapsingHeader("Player"))
            {
                if (player != null)
                {
                    drawPlayerInfo(player, width);
                    ImGui.Separator();
                }
            }
            ImGui.PopID();

            ImGui.PushID("World");
            if (ImGui.CollapsingHeader("World"))
            {
                nint timeAddr = MemoryUtil.Read<nint>(sMain.Instance + 0xAF878);
                if (timeAddr != 0x0)
                {
                    float gameTime = MemoryUtil.Read<float>(timeAddr + 0x38);
                    ImGui.SetNextItemWidth(width * 0.75f);
                    if (ImGui.SliderFloat("Time of Day", ref gameTime, 0.0f, 24.0f))
                    {
                        MemoryUtil.GetRef<float>(timeAddr + 0x38) = gameTime;
                    }
                }

                ref float gameSpeed = ref MemoryUtil.GetRef<float>(sMain.Instance + 0xA4);
                ImGui.SetNextItemWidth(width * 0.125f);
                if (ImGui.DragFloat("Game Speed", ref gameSpeed, 0.005f, 0.0f, Single.MaxValue))
                {
                    if (freezeGame && gameSpeed != 0.0f)
                    {
                        toggleFreezeGame(gameSpeed);
                    }
                    else if (!freezeGame && gameSpeed == 0.0f)
                    {
                        toggleFreezeGame();
                    }
                }
                ImGui.SameLine();
                if (ImGui.Button("Toggle Freeze Game"))
                {
                    toggleFreezeGame();
                }
                ImGui.SameLine();
                if (ImGui.Button("Advance Frame"))
                {
                    advanceFrame++;
                }
                ImGui.Checkbox("Decouple Camera Speed from Game Speed", ref decoupleDtFromGameTime);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Enable this to move in free camera at a normal speed when game speed is not 1.0.");
                    ImGui.EndTooltip();
                }

                if (ImGui.Checkbox("Override View Mode", ref overrideViewMode))
                {
                    config.OverrideViewMode = overrideViewMode;
                    ConfigManager.SaveConfig<Config>(this);
                }
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Replace in-game View Mode with the free camera from this mod.");
                    ImGui.EndTooltip();
                }

                if (ImGui.Checkbox("Underwater Camera", ref enableUnderwaterCamera))
                {
                    if (enableUnderwaterCamera)
                    {
                        underwaterCameraEnable();
                    }
                    else
                    {
                        underwaterCameraDisable();
                    }
                }
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Enable the screen filter from diving but whenever your camera goes underwater.\nWARNING: This is experimental, unfinished and known to crash in at least The Rotten Vale.\nIf you want to see the effect you can try it in the Seliana Gathering Hub, the Ancient Forest or your Private Suite.");
                    ImGui.EndTooltip();
                }

                ImGui.Separator();
            }
            ImGui.PopID();

            ImGui.PushID("Debug");
            if (ImGui.CollapsingHeader("DEBUG"))
            {
                if (vCamera != null || pCamera != null)
                {
                    ImGui.Text("Camera");
                    //ver9 L1リセットの切り分け用デバッグ表示。原因が特定できたら
                    // 削除して構いません。このコードベースのdeltaTimeは、実は実時間の秒数ではなく「60FPS基準のフレーム換算値」でした
                    //ImGui.Text($"orbitLookState: {orbitLookState} | orbitRecenterHeldSec: {orbitRecenterHeldSec:0.000} | L1 down: {buttonWasDown(Button.L1)}");
                    //ver9ここまで
                    if (vCamera != null)
                    {
                        ImGui.Text($"Visible Camera: 0x{vCamera.Instance:X}");
                        ImGui.Text($"freeCameraFallback: {freeCameraFallback}");
                        //ImGui.Text($"viewModeObject: 0x{psuedoViewModeObject:X}");
                    }
                    if (pCamera != null)
                    {
                        ImGui.Text($"Player Camera: 0x{pCamera.Instance:X}");
                        ImGui.Text($"Applying Offset: {applyPerspective}");
                        ImGui.Text($"previousCameraAnimState: {previousCameraAnimState}");
                        ImGui.PushItemFlag(ImGuiItemFlags.Disabled, true);
                        ImGui.DragFloat3("Offset", ref cameraOffset);
                        ImGui.PopItemFlag();
                    }
                    ImGui.Separator();
                }

                ImGui.PushID("Viewports");
                ImGui.Text("Viewports");
                for (int i = 0; i < 8; i++)
                {
                    ImGui.PushID($"Viewport{i}");
                    bool viewportExpanded = ImGui.CollapsingHeader($"Viewport #{i}");
                    ImGui.SameLine();
                    ImGui.Text($"({((i == vCameraViewportIndex) ? "visible" : "inactive")})");
                    if (viewportExpanded)
                    {
                        drawViewportInfo(i, width, config, true);
                    }
                    ImGui.PopID();
                }
                ImGui.PopID();

                ImGui.Separator();

                ImGui.PushID("Player");
                ImGui.Text("Player");
                ImGui.Text($"sMhPlayer: 0x{sPlayer.Instance:X}");
                if (player != null)
                {
                    ImGui.Text($"Address: 0x{player.Instance:X}");
                    ImGui.Text($"Settings: 0x{getPlayerSettings():X}");

                    drawAnimationInfo(player, width, true);

                    bool move = player.Move;
                    if (ImGui.Checkbox("Move", ref move))
                    {
                        player.Move = move;
                    }
                    ImGui.SameLine();
                    bool fix = player.Fix;
                    if (ImGui.Checkbox("Fix", ref fix))
                    {
                        player.Fix = fix;
                    }

                    ImGui.Separator();
                }
                ImGui.PopID();

                ImGui.PushID("Palico");
                ImGui.Text("Palico/Otomo");
                ImGui.Text($"sOtomo: 0x{sOtomo.Instance:X}");
                ImGui.PopID();

                ImGui.Separator();

                if (ImGui.CollapsingHeader($"Monsters"))
                {
                    Monster[] monsters = Monster.GetAllMonsters();
                    ImGui.Text($"Count: {monsters.Length}");
                    ImGui.Checkbox("Sort by distance from camera", ref sortMonsters);
                    if (sortMonsters && vCamera != null)
                    {
                        Array.Sort(monsters, delegate(Monster x, Monster y)
                        {
                            float distX = Vector3.Distance(vCamera.Position, x.Position);
                            float distY = Vector3.Distance(vCamera.Position, y.Position);
                            if (distX < distY) return 1;
                            else if (distX > distY) return -1;
                            return 0;
                        });
                    }
                    for (int i = monsters.Length - 1; i >= 0; i--)
                    {
                        Monster monster = monsters[i];
                        ImGui.PushID(monster.Instance);
                        ImGui.Text($"{monster.Name}:");
                        ImGui.Text($" Address: 0x{monster.Instance:X}");
                        ImGui.DragFloat3("Position", ref monster.Position, 0.5f);
                        drawAnimationInfo(monster, width, true);
                        ImGui.PopID();
                        ImGui.Separator();
                    }
                }

                if (ImGui.CollapsingHeader($"Animals"))
                {
                    Animal[] animals = Animal.GetAllAnimals();
                    ImGui.Text($"Count: {animals.Length}");
                    ImGui.Checkbox("Sort by distance from camera", ref sortAnimals);
                    if (sortAnimals && vCamera != null)
                    {
                        Array.Sort(animals, delegate(Animal x, Animal y)
                        {
                            float distX = Vector3.Distance(vCamera.Position, x.Position);
                            float distY = Vector3.Distance(vCamera.Position, y.Position);
                            if (distX < distY) return 1;
                            else if (distX > distY) return -1;
                            return 0;
                        });
                    }
                    for (int i = animals.Length - 1; i >= 0; i--)
                    {
                        Animal animal = animals[i];
                        ImGui.PushID(animal.Instance);
                        ImGui.Text($"{animal.Id:X} ({animal.OtherId:X}):");
                        ImGui.Text($" Address: 0x{animal.Instance:X}");
                        ImGui.DragFloat3("Position", ref animal.Position, 0.5f);
                        // Freeze: +0x1AFC = 0xFFFFFFFC
                        if (ImGui.Button("Remove"))
                        {
                            MemoryUtil.GetRef<uint>(animal.Instance + 0x928) = 0xFFFFFFFF;
                        }
                        ImGui.PopID();
                        ImGui.Separator();
                    }
                }

                ImGui.Separator();

                ImGui.Checkbox("Only Toggle LOD 0", ref modelOnlyAddressLod0);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text($"This can be used to check if a given model is showing LOD 0 or not.\nApplies when a part is toggled, meaning you should probably toggle all parts on before enabling this.");
                    ImGui.EndTooltip();
                }
                if (ImGui.CollapsingHeader("Loaded Models") && player != null)
                {
                    ImGui.Text("Filter");
                    bool updateRegex = false;
                    ImGui.SetNextItemWidth(width * 0.5f);
                    bool invalidRegex = modelFilterIsRegex && modelFilter != "" && modelRegex == null;
                    if (invalidRegex)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, 0xFF0000FF);
                    }
                    if (ImGui.InputText("##Filter", ref modelFilter, 256))
                    {
                        if (modelFilterIsRegex)
                        {
                            updateRegex = true;
                        }
                    }
                    if (invalidRegex)
                    {
                        ImGui.PopStyleColor();
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Clear"))
                    {
                        modelFilter = "";
                        if (modelFilterIsRegex)
                        {
                            updateRegex = true;
                        }
                    }
                    ImGui.SameLine();
                    if (ImGui.Checkbox("Regex", ref modelFilterIsRegex))
                    {
                        if (modelFilterIsRegex)
                        {
                            updateRegex = true;
                        }
                    }
                    if (updateRegex)
                    {
                        if (String.IsNullOrEmpty(modelFilter))
                        {
                            modelRegex = null;
                        }
                        else
                        {
                            try
                            {
                                modelRegex = new Regex(modelFilter);
                            }
                            catch (ArgumentException)
                            {
                                modelRegex = null;
                            }
                        }
                    }
                    nint bodyArmorParts = MemoryUtil.Read<nint>(player.Instance + 0x2A0);
                    if (bodyArmorParts != 0x0)
                    {
                        nint next = bodyArmorParts - 0x40;
                        nint head = next;
                        nint prev = MemoryUtil.Read<nint>(next + 0x10);
                        for (;next != 0x0;)
                        {
                            ImGui.PushID(next);
                            nint partsAddr = next + 0x40;
                            nint vTable = MemoryUtil.Read<nint>(partsAddr);
                            if (vTable == 0x143507A18)
                            {
                                string fullString = Marshal.PtrToStringAnsi(partsAddr + 0xC)!;
                                bool filterMatch = false;
                                if (modelFilterIsRegex)
                                {
                                    filterMatch = modelRegex == null || modelRegex.Match(fullString).Success;
                                }
                                else
                                {
                                    filterMatch = String.IsNullOrEmpty(modelFilter) || fullString.StartsWith(modelFilter);
                                }
                                if (filterMatch)
                                {
                                    drawModelParts(0x0, partsAddr);
                                }
                            }
                            ImGui.PopID();
                            if (head == prev)
                            {
                                next = MemoryUtil.Read<nint>(next + 0x10);
                            }
                            else
                            {
                                next = MemoryUtil.Read<nint>(next + 0x18);
                                if (next == 0x0)
                                {
                                    head = prev;
                                    next = prev;
                                }
                            }
                        }
                    }
                }

                ImGui.Separator();

#if MOUSE_AND_KEYBOARD_LAYER
                ImGui.PushID("Mouse/Keyboard");
                ImGui.Text("Mouse");
                ImGui.Text($"Address: 0x{sMhMouse.Instance:X}");
                ImGui.Text("Keyboard");
                ImGui.Text($"Address: 0x{sMhKeyboard.Instance:X}");
                ImGui.PopID();
#endif

                ImGui.PushID("Pad");
                ImGui.Text("Pad");
                ImGui.Text($"Address: 0x{sMhController.Instance:X}");
                int Rx = MemoryUtil.Read<int>(sMhController.Instance + 0x1B0);
                int Ry = MemoryUtil.Read<int>(sMhController.Instance + 0x1B4);
                int Lx = MemoryUtil.Read<int>(sMhController.Instance + 0x1B8);
                int Ly = MemoryUtil.Read<int>(sMhController.Instance + 0x1BC);
                byte PadRz = MemoryUtil.Read<byte>(sMhController.Instance + 0x1C0);
                byte PadLz = MemoryUtil.Read<byte>(sMhController.Instance + 0x1C1);
                ImGui.Text("Left Stick:");
                ImGui.Text($" X: {Lx} ({PadLx})");
                ImGui.Text($" Y: {Ly} ({PadLy})");
                ImGui.Text($" LT: {PadLz}");
                ImGui.Text("Decouple Movement From Look (debug):");
                ImGui.Text($" Enabled: {orbitDecoupleMovementFromLook}  Invert: {orbitDecoupleMovementInvert}  Valid: {orbitCachedForwardYawValid}");
                ImGui.Text($" Camera Yaw: {orbitCachedCameraForwardYawDeg:F1}  Stable Yaw: {orbitCachedStableForwardYawDeg:F1}");
                {
                    float dbgCorrection = orbitCachedStableForwardYawDeg - orbitCachedCameraForwardYawDeg;
                    if (orbitDecoupleMovementInvert) dbgCorrection = -dbgCorrection;
                    ImGui.Text($" Correction Applied: {dbgCorrection:F1} deg");
                }
                ImGui.Text("Ignore Camera Direction (debug, only updates while ON):");
                ImGui.Text($" cameraForward: {cameraForward:F1}  dist(700-cf): {dbgCmDist:F1}");
                ImGui.Text($" movementAngleDeg: {dbgCmMovementAngleDeg:F1}  StableYaw: {orbitCachedStableForwardYawDeg:F1}");
                ImGui.Text($" CamPos: {dbgCmCameraPosition.X:F1},{dbgCmCameraPosition.Y:F1},{dbgCmCameraPosition.Z:F1}");
                ImGui.Text($" Target before: {dbgCmTargetBefore.X:F1},{dbgCmTargetBefore.Y:F1},{dbgCmTargetBefore.Z:F1}");
                ImGui.Text($" Target after:  {dbgCmTargetAfter.X:F1},{dbgCmTargetAfter.Y:F1},{dbgCmTargetAfter.Z:F1}");
                ImGui.Text("Right Stick:");
                ImGui.Text($" X: {Rx} ({PadRx})");
                ImGui.Text($" Y: {Ry} ({PadRy})");
                ImGui.Text($" RT: {PadRz}");
                ImGui.PushItemWidth(width * 0.125f);
                ImGui.DragFloat("+Right", ref plusRight, 0.01f);
                ImGui.SameLine();
                ImGui.DragFloat("+Forward", ref plusForward, 0.01f);
                ImGui.PopItemWidth();
                ImGui.SameLine();
                if (ImGui.Button("Reset"))
                {
                    plusRight = 0.0f;
                    plusForward = 0.0f;
                }
                ImGui.SameLine();
                if (ImGui.Button("Flip"))
                {
                    if (plusRight != 0.0f)
                    {
                        plusRight = -plusRight;
                    }
                    if (plusForward != 0.0f)
                    {
                        plusForward = -plusForward;
                    }
                }
                ImGui.PopID();

                ImGui.Separator();

                ImGui.PushID("Orbit");
                ImGui.Text("Orbital Camera");
                ImGui.PushItemWidth(width * 0.15f);
                ImGui.DragFloat("Distance", ref orbitDistance, 1.0f);
                ImGui.SameLine();
                ImGui.DragFloat("Target Y (A)", ref orbitY, 1.0f);
                ImGui.SameLine();
                ImGui.DragFloat("Target Right (A)", ref orbitRight, 1.0f);
                ImGui.SameLine();
                ImGui.DragFloat("Target Forward (A)", ref orbitForward, 1.0f);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Positive pushes the eye position forward (away from the body,\ntoward where the character is looking) - try a small positive\nvalue if arms/weapon clip into view. This triple only applies\nwhile \"A\" (full-body rotation / clamp bypassed, or Face Clamp\ndisabled entirely) is active - see \"B\"/\"B (Ignore-X)\"'s own\nTarget Y/Right/Forward below the keyword lists, and each \"C\"\nprofile's own further down, for their independent offsets.");
                    ImGui.EndTooltip();
                }
                ImGui.DragFloat("Lerp", ref orbitLerp, 0.01f, 0.0f, 1.0f);
                ImGui.Checkbox("Ignore Camera Direction", ref orbitIgnoreCamera);
                ImGui.DragFloat("Movement Rotation", ref orbitMovementRotation, 1.0f);
                ImGui.DragFloat("Stick Snap Angle (deg)", ref orbitStickSnapAngleDeg, 0.5f, 0.0f, 89.0f);
                if (ImGui.InputInt("Target Joint", ref orbitJoint))
                {
                    orbitFaceBasisPrevUpInit = false;
                    orbitClampSmoothedInit = false;
                }
                if (ImGui.Checkbox("Use Face Joints", ref orbitTargetFaceJoint))
                {
                    orbitFaceBasisPrevUpInit = false;
                    orbitClampSmoothedInit = false;
                }
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Off: \"Target Joint\" indexes the numbers shown under\nPlayer > Joints > Body Joints (Joint #N).\nOn: it instead indexes Player > Joints > Face Joints,\nwhich includes the actual head/eye-level bones - usually\na better fit for a head-locked FPS camera position.");
                    ImGui.EndTooltip();
                }
                ImGui.Checkbox("Simple Lock (position from Target Joint, rotation from below)", ref orbitSimpleLock);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("When on: the camera's position comes directly from \"Target\nJoint\" above, and its rotation comes directly from \"Simple\nRotation Joint\" below - raw, with nothing else involved (no\nbase joint, no neck-range blending, no stabilizer). Use this if\na Face Joint's own rotation doesn't carry roll/spin animation\nthe way a Body Joint's does. Turn off to use the more advanced\noptions below instead.");
                    ImGui.EndTooltip();
                }
                if (orbitSimpleLock)
                {
                    if (ImGui.Checkbox("Use Positional Face Basis", ref orbitFaceBasisEnable))
                    {
                        orbitFaceBasisPrevUpInit = false;
                        orbitClampSmoothedInit = false;
                    }
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("Builds the camera's rotation purely from face joint *positions*\n(no joint rotation data, so no hip/body rotation bleeds in):\nforward = nose - Center Joint, and the ear-to-ear line fixes\nroll/up. Falls back to Simple Rotation Joint below if these\njoints aren't valid or nearly overlap.");
                        ImGui.EndTooltip();
                    }
                    if (orbitFaceBasisEnable)
                    {
                        if (ImGui.InputInt("Center Joint (Face)", ref orbitFaceBasisCenterJoint))
                        {
                            orbitFaceBasisPrevUpInit = false;
                            orbitClampSmoothedInit = false;
                        }
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("Face Joint index for a point behind/center of the face (e.g.\nback of the mouth/throat). The nose-to-here direction becomes\n\"forward\".");
                            ImGui.EndTooltip();
                        }
                        if (ImGui.InputInt("Right Ear Joint (Face)", ref orbitFaceBasisRightEarJoint))
                        {
                            orbitFaceBasisPrevUpInit = false;
                            orbitClampSmoothedInit = false;
                        }
                        if (ImGui.InputInt("Left Ear Joint (Face)", ref orbitFaceBasisLeftEarJoint))
                        {
                            orbitFaceBasisPrevUpInit = false;
                            orbitClampSmoothedInit = false;
                        }
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("Face Joint indices for the right/left ear. The line between\nthem fixes which way is \"up\" (and therefore roll), so barrel\nrolls tilt the camera correctly.");
                            ImGui.EndTooltip();
                        }
                    }
                    ImGui.InputInt("Simple Rotation Joint", ref orbitSimpleRotationJoint);
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("Fallback rotation source, used only if Positional Face Basis is\noff or its joints are invalid/degenerate. Taken directly (no\nblending/clamping) from this joint's own rotation. Defaults to\nBody Joint 1. Set to -1 to use Target Joint's own rotation.");
                        ImGui.EndTooltip();
                    }
                    ImGui.Checkbox("Simple Rotation Uses Face Joints", ref orbitSimpleRotationJointUseFace);
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("Whether \"Simple Rotation Joint\" above indexes Body Joints\n(off) or Face Joints (on).");
                        ImGui.EndTooltip();
                    }

                    ImGui.Separator();
                    ImGui.Checkbox("Clamp to Human Neck Range (ignore item/head tracking)", ref orbitFaceClampEnable);
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("While on: outside of \"full-body rotation\" motions (see list\nbelow), the camera is pulled back toward Base Rotation Joint's\ndirection whenever it strays further than a human neck could\nplausibly turn - this cancels out head-tracking (looking at\nitems, etc.) while standing/walking/attacking normally.");
                        ImGui.EndTooltip();
                    }
                    if (orbitFaceClampEnable)
                    {
                        ImGui.InputInt("Base Rotation Joint##SimpleLock", ref orbitBaseRotationJoint);
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("Stable \"always faces forward\" reference (e.g. Body Joint 0).\nSet to -1 to use the player's raw body rotation (player.Rotation)\ninstead of a joint.");
                            ImGui.EndTooltip();
                        }
                        ImGui.Checkbox("Base Uses Face Joints##SimpleLock", ref orbitBaseRotationJointUseFace);
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("Whether \"Base Rotation Joint\" above indexes Body Joints (off)\nor Face Joints (on).");
                            ImGui.EndTooltip();
                        }
                        ImGui.DragFloat("Base Correction Yaw (deg)##SimpleLock", ref orbitClampBaseCorrectionYaw, 1.0f, -180.0f, 180.0f);
                        ImGui.DragFloat("Base Correction Pitch (deg)##SimpleLock", ref orbitClampBaseCorrectionPitch, 1.0f, -180.0f, 180.0f);
                        ImGui.DragFloat("Base Correction Roll (deg)##SimpleLock", ref orbitClampBaseCorrectionRoll, 1.0f, -180.0f, 180.0f);
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("Base Rotation Joint / player.Rotation may use a different\nforward/up convention than the face-basis rotation. Stand still\nfacing normally and adjust these three until Deviation below\nreads ~0 on all axes.\n\nThese three fields are the fallback used only when a motion\nmatches none of the profiles below at all (defaults to \"A\").\nEvery profile below - any Mode, including an explicit Full\nRotation profile - has its own independent Correction Yaw/Pitch/\nRoll that takes priority whenever it matches, so tune a profile's\nown fields instead if this doesn't seem to be taking effect.\n\nIMPORTANT: whichever Correction actually applies (this fallback,\nor a matched profile's own) still feeds the shared movement/\nlook-decoupling reference (\"Decouple Movement From Look\" above)\nat all times, even during Full Rotation motions (which never use\nit for their OWN rendered view - only for that shared reference) -\nso if movement direction feels wrong, check here first regardless\nof which mode is active.");
                            ImGui.EndTooltip();
                        }
                        ImGui.DragFloat("Spot Distance##SimpleLock", ref orbitSpotDistance, 10.0f, 1.0f, 5000.0f);
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("\"C\" (attack) motions spot a fixed point in the room - this is\nhow far out (in the same units as other joint distances) that\npoint is placed, along B's forward direction, at the moment C\nbegins. Only the point's distance matters, not exact accuracy -\nit just needs to be far enough that small position changes\ndon't swing the direction to it much.");
                            ImGui.EndTooltip();
                        }
                        ImGui.DragFloat("Spot Re-anchor Rate##SimpleLock", ref orbitSpotReanchorRate, 0.05f, 0.0f, 5.0f);
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("Fraction/second the spot point drifts to re-match the live\nbase direction while C continues. Fixes an unnatural extra\nneck correction after C ends, if the character's true heading\ndrifted during a long attack (root motion repositioning) - the\ncamera would otherwise whip to reconcile that gap once B takes\nover. 0 disables (spot point stays perfectly fixed, original\nbehavior). Keep well below \"one rotation's worth\" of speed.");
                            ImGui.EndTooltip();
                        }
                        ImGui.DragFloat("Deviation Smoothing##SimpleLock", ref orbitClampSmoothing, 0.01f, 0.01f, 1.0f);
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("1.0 = raw, instant (no smoothing). Lower values filter out\nhigh-frequency wobble (e.g. natural walk-cycle micro-twist)\nbefore it's clamped, at the cost of reacting more slowly to\nreal look-around. Lower this if the camera jitters/twitches\nwhile walking.");
                            ImGui.EndTooltip();
                        }
                        ImGui.DragFloat("Base Smoothing (Default)##SimpleLock", ref orbitClampBaseSmoothing, 0.01f, 0.01f, 1.0f);
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("1.0 = raw, instant. Lower values filter noise in the player's\nown heading itself (e.g. analog-stick jitter reflected into\nmovement direction) before B/C use it as their reference.\nLower this if the camera still shakes slightly while walking\nstraight with a gamepad even with Deviation Smoothing low.\n\nThis is the fallback used only when a motion matches none of\nthe profiles below at all - every profile below has its own\nindependent Base Smoothing that takes priority whenever it\nmatches.");
                            ImGui.EndTooltip();
                        }
                        ImGui.Text($"Deviation - Yaw: {orbitLastRelYaw:F0}  Pitch: {orbitLastRelPitch:F0}  Roll: {orbitLastRelRoll:F0} (deg)");
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("How far the face-basis rotation currently deviates from Base\nRotation Joint, decomposed per-axis. Watch these while turning,\nwalking, and doing regular attacks (with clamp OFF) to set each\nC profile's ranges below (a bit higher than what you see during\nnormal play, for whichever profile that motion falls under).\n\n\"C\" motions use a ballet-\"spotting\"-style follow: within Range,\npinned to true forward; beyond Range+Blend, the camera tracks the\nactual motion exactly with no ceiling (so a full-body spin attack\nkeeps being followed all the way through instead of getting stuck\npartway), settling back at forward once the motion's own deviation\nshrinks back under Range again. Roll range is usually kept low (a\nreal neck can only tilt sideways so far) so sideways barrel rolls\nstart rotating the camera quickly instead of staying locked forward.");
                            ImGui.EndTooltip();
                        }
                        //ver10.1
                        ImGui.Separator();
                        ImGui.Text($"Current Motion: {orbitLastActionName}");
                        ImGui.Text($"Action SubState (+0x760): {orbitActionSubState}");
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("ActionController + 0x760 の生の値 (Cheat Engine で見ていたものと同じ)。\nモーション名も {Lmt}.{Id} も変わらないまま、上半身側の状態だけが\n変わる場面 (回復薬を飲む=8 / しまう=9、弓のビン装填=14 / 解除=15 など)\nでここだけが変化する。\n\nこの値をキーワード欄で使うには、末尾に \"#<数値>\" を付ける:\n  #8                      … 値が 8 のときだけ一致 (モーション名は不問)\n  Common::IDLE#8          … アクション名一致 かつ 値が 8\n  WP_11::IDLE@12.156#14   … アクション名 + {Lmt}.{Id} + 値\n\n-1 はまだ読めていない状態。");
                            ImGui.EndTooltip();
                        }
                        ImGui.Text($"SubState Elapsed: {orbitSubStateElapsedSec:F2}s");
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("Action SubState (+0x760) が最後に変化してからの経過秒数。\n0 にリセットされた瞬間が、そのサブ動作の \"開始\" とみなされる。\n\"Use SubState Timer for T\" を ON にしたプロファイルの Gaze\nKeyframes / GazeRanges は、この値を Assumed Duration で\n割ったものを T として使う。実際にサブ動作がどれくらい\n続くか、この数値をストップウォッチ代わりに実測して\nAssumed Duration を決める。");
                            ImGui.EndTooltip();
                        }
                        //ver10.1ここまで
                        string motionStatus = orbitLastProfileIndex >= 0
                            ? $"-> Profile {orbitLastProfileIndex + 1} ({orbitProfiles[orbitLastProfileIndex].Mode})"
                            : "-> A: Full Rotation (default, unmatched - clamp bypassed)";
                        ImGui.Text(motionStatus);
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("Matches Player > Animation > Current's action name (the part\nafter the comma, e.g. \"SPIN_ATTACK3\" in \"WP_04::SPIN_ATTACK3\").\nChecked top-to-bottom across every profile below, first match\nwins, regardless of that profile's Mode; anything matching none\nof them defaults to \"A\" (Full Rotation, unclamped, follows raw\nmotion exactly).");
                            ImGui.EndTooltip();
                        }
                        if (lastPlayer != null)
                        {
                            ImGui.Text($"Motion Progress (T): {getMotionProgress(lastPlayer):F3}");
                            if (ImGui.BeginItemTooltip())
                            {
                                ImGui.Text("Current animation playback position, 0.0 (just started) to\n1.0 (about to end/loop). This is the T value used by any C\nprofile's Gaze Keyframes below - pause on a frame (Animation\npanel's Frame slider) and read this to find the T for that pose.");
                                ImGui.EndTooltip();
                            }
                        }
                        ImGui.Text(orbitProfileTransitionActive
                            ? $"Profile Switch Blend: {orbitProfileTransitionTimer:F2}/{orbitCurrentTransitionDuration:F2}s"
                            : "Profile Switch Blend: (idle)");
                        
                        // --- [改善1] Animation の Frame スライダー & Paused をここに配置 ---ver7
                        if (lastPlayer != null && lastPlayer.AnimationLayer != null)
                        {
                            var animLayer = lastPlayer.AnimationLayer;
                            bool animPaused = animLayer.Paused;
                            if (ImGui.Checkbox("Animation Paused", ref animPaused))
                            {
                                animLayer.Paused = animPaused;
                            }
                            ImGui.SameLine();
                            ImGui.SetNextItemWidth(width * 0.55f);
                            ImGui.SliderFloat("Frame", ref animLayer.CurrentFrame, 0.0f, animLayer.MaxFrame, "%.3f");
                        }

                        ImGui.Text("Motion Profiles (checked top-to-bottom, first match wins):");
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("Each profile below is its own independent keyword list, Camera\nMode (Full Rotation \"A\" / Base Only Ignore-X \"B (Ignore-X)\" /\nBase Only \"B\" / Normal \"C\"), and Target/Correction/Base\nSmoothing/Transition settings - plus, for Normal mode, its own\nYaw/Pitch/Roll Range/Blend and attack-specific options. Checked\ntop-to-bottom, first matching profile wins. Anything matching\nnone of them defaults to \"A\" (Full Rotation, unclamped, follows\nraw motion exactly) using the top-level default fields above.\nSame keyword matching rules throughout: action name (or the part\nafter \"::\"), matched against Current's text after the comma, OR\nan exact \"{Lmt}.{Id}\" key (the part before the comma).");
                            ImGui.EndTooltip();
                        }
                        //ver14
                        int moveProfileFrom = -1;
                        int moveProfileTo = -1;
                        string? moveProfileToGroup = null;
                        string? currentWeaponGroup = null;
                        bool weaponGroupSectionOpen = true;
                        bool weaponGroupIndented = false;
                        string? moveGroupFromName = null;
                        string? moveGroupToName = null;
                        int commitWeaponGroupIndex = -1;

                        for (int profileIndex = 0; profileIndex < orbitProfiles.Count; profileIndex++)
                        {
                            OrbitProfile profile = orbitProfiles[profileIndex];
                            // ID をリスト上の位置ではなくプロファイル自体に固定する。
                            // これをしないと、並び替えが起きたフレームで「同じ位置の
                            // 入力欄」が別のプロファイルの中身に差し替わり、編集内容が
                            // 隣のプロファイルに書き込まれてしまう。
                            ImGui.PushID(profile.Uid);
                            bool profileMatchedNow = orbitLastProfileIndex == profileIndex;

                            // --- 武器種(WeaponGroup)ごとの折りたたみ見出し ---
                            // 直前のプロファイルとWeaponGroupが変わった箇所でだけ、新しい見出しを描画する。
                            // 同じWeaponGroupのプロファイルは、リスト上で連続して並んでいる必要がある
                            // (▲▼やドラッグ＆ドロップで並び順をまとめてから、WeaponGroup欄に
                            // 同じ文字列を入力すること)。
                            if (profileIndex == 0 || profile.WeaponGroup != currentWeaponGroup)
                            {
                                if (profileIndex > 0 && weaponGroupIndented)
                                {
                                    ImGui.Unindent();
                                }
                                currentWeaponGroup = profile.WeaponGroup;
                                string weaponGroupLabel = string.IsNullOrWhiteSpace(currentWeaponGroup)
                                    ? "(Ungrouped)"
                                    : currentWeaponGroup;
                                weaponGroupSectionOpen = ImGui.CollapsingHeader($"{weaponGroupLabel}###WeaponGroup_{weaponGroupLabel}");
                                weaponGroupIndented = weaponGroupSectionOpen;

                                // --- グループ見出し自体をドラッグ&ドロップで並び替える ---
                                if (ImGui.BeginDragDropSource())
                                {
                                    int dragPayloadGroupIndex = profileIndex;
                                    ImGui.SetDragDropPayload("NEWCAMERA_GROUP", (IntPtr)(&dragPayloadGroupIndex), (uint)sizeof(int));
                                    ImGui.Text($"Move group: {weaponGroupLabel}");
                                    ImGui.EndDragDropSource();
                                }
                                if (ImGui.BeginDragDropTarget())
                                {
                                    ImGuiPayloadPtr groupPayload = ImGui.AcceptDragDropPayload("NEWCAMERA_GROUP");
                                    if (groupPayload.NativePtr != null)
                                    {
                                        int draggedGroupProfileIndex = *(int*)groupPayload.Data;
                                        if (draggedGroupProfileIndex >= 0 && draggedGroupProfileIndex < orbitProfiles.Count)
                                        {
                                            string draggedGroupName = orbitProfiles[draggedGroupProfileIndex].WeaponGroup ?? "";
                                            if (draggedGroupName != currentWeaponGroup)
                                            {
                                                moveGroupFromName = draggedGroupName;
                                                moveGroupToName = currentWeaponGroup;
                                            }
                                        }
                                    }
                                    // プロファイル1件を見出しに落とすと、そのグループへ移籍する
                                    // (グループ名も自動的に書き換わる)。
                                    ImGuiPayloadPtr intoGroupPayload = ImGui.AcceptDragDropPayload("NEWCAMERA_PROFILE");
                                    if (intoGroupPayload.NativePtr != null)
                                    {
                                        int draggedProfileIntoGroup = *(int*)intoGroupPayload.Data;
                                        if (draggedProfileIntoGroup >= 0 && draggedProfileIntoGroup < orbitProfiles.Count)
                                        {
                                            moveProfileFrom = draggedProfileIntoGroup;
                                            moveProfileTo = profileIndex;
                                            moveProfileToGroup = currentWeaponGroup ?? "";
                                        }
                                    }
                                    ImGui.EndDragDropTarget();
                                }

                                if (weaponGroupIndented)
                                {
                                    ImGui.Indent();
                                }
                            }

                            if (!weaponGroupSectionOpen)
                            {
                                ImGui.PopID();
                                continue;
                            }

                            //ver14
                            // --- 並び替えは見出しのドラッグ&ドロップで行う ---
                            //ver10
                            bool profileRemoved = false;
                            //ver14ここまで

                            if (ImGui.Button($"X##del_{profileIndex}"))
                            {
                                orbitProfiles.RemoveAt(profileIndex);
                                profileRemoved = true;
                            }
                            ImGui.SameLine();

                            string profileCommentLabel = string.IsNullOrWhiteSpace(profile.Comment)
                                ? ""
                                : $" - {profile.Comment}";
                            string profileHeaderLabel = $"Profile {profileIndex + 1} ({profile.Mode}){profileCommentLabel} - {profile.Keywords.Count} keyword(s){(profileMatchedNow ? " [ACTIVE]" : "")}###ProfileHeader";
                            bool profileOpen = ImGui.CollapsingHeader(profileHeaderLabel);

                            // --- 見出しを掴んでドラッグ＆ドロップで並び替え ---
                            // 掴んだプロファイルの番号をペイロードとして渡し、落とした
                            // 先のプロファイル番号を移動先にする。実際の並び替えは
                            // ループを抜けたあと (下の moveProfileFrom/To の処理) で
                            // 行うので、描画中にリストが壊れることはない。
                            if (ImGui.BeginDragDropSource())
                            {
                                int dragPayloadIndex = profileIndex;
                                ImGui.SetDragDropPayload("NEWCAMERA_PROFILE", (IntPtr)(&dragPayloadIndex), (uint)sizeof(int));
                                ImGui.Text($"Move: {profileHeaderLabel.Split("###")[0]}");
                                ImGui.EndDragDropSource();
                            }
                            if (ImGui.BeginDragDropTarget())
                            {
                                ImGuiPayloadPtr profilePayload = ImGui.AcceptDragDropPayload("NEWCAMERA_PROFILE");
                                if (profilePayload.NativePtr != null)
                                {
                                    int draggedProfileIndex = *(int*)profilePayload.Data;
                                    //ver14
                                    if (draggedProfileIndex >= 0
                                        && draggedProfileIndex < orbitProfiles.Count
                                        && draggedProfileIndex != profileIndex)
                                    {
                                        moveProfileFrom = draggedProfileIndex;
                                        moveProfileTo = profileIndex;
                                        // 別グループのプロファイルの上に落とした場合は、
                                        // 落とした先のグループへ移籍したものとして扱う。
                                        moveProfileToGroup = profile.WeaponGroup ?? "";
                                    }
                                    //ver14ここまで
                                }
                                ImGui.EndDragDropTarget();
                            }
                            //ver10ここまで

                            if (!profileRemoved && profileOpen)
                            {
                                //ver10
                                ImGui.Indent();

                                ImGui.SetNextItemWidth(width * 0.6f);
                                ImGui.InputText("Comment##Profile", ref profile.Comment, 128);
                                if (ImGui.BeginItemTooltip())
                                {
                                    ImGui.Text("プロファイルの見出しにそのまま表示される自由記入のメモ。\nツリーを開かなくても何のプロファイルか分かるようにするためのもので、\nマッチング判定には一切使われない。Save Settings で NewCamera.json に\n保存される。\n\n注意: ImGui の標準フォントは日本語の字形を持っていないため、\n日本語はメモ帳に一度書いて貼り付ける必要がある。\n(日本語でもJSONには保存される)");
                                    ImGui.EndTooltip();
                                }
                                //ver14
                                ImGui.SetNextItemWidth(width * 0.4f);
                                ImGui.InputText("##WeaponGroupInput", ref profile.WeaponGroupInput, 64);
                                ImGui.SameLine();
                                if (ImGui.Button("Set Group##Profile"))
                                {
                                    commitWeaponGroupIndex = profileIndex;
                                }
                                ImGui.SameLine();
                                ImGui.Text("Weapon Group");
                                if (ImGui.BeginItemTooltip())
                                {
                                    ImGui.Text("武器種などでプロファイルをグループ化するための名前(自由記入)。\n左の欄に入力しただけでは何も起こらない。\"Set Group\" ボタンを\n押した瞬間だけ確定し、他に同じ名前のプロファイルがあれば\nその末尾へ自動的に移動して1つの折りたたみ見出しにまとまる。\n空欄で確定すると (Ungrouped) にまとまる。\n\nグループ間の移動は、プロファイルの見出しをつかんで移動先の\nグループ見出し (または移動先グループ内のプロファイル見出し) に\nドロップしても行える (グループ名も自動で書き換わる)。\nグループ見出し同士をドラッグ&ドロップすると、グループ全体を\n1つの塊として並び替えられる。");
                                    ImGui.EndTooltip();
                                }
                                //ver14ここまで
                                int modeInt = (int)profile.Mode;
                                ImGui.SetNextItemWidth(width * 0.4f);
                                //ver10ここまで

                                if (ImGui.Combo("Camera Mode##Profile", ref modeInt, "Full Rotation (A)\0Base Only Ignore-X (B Ignore-X)\0Base Only (B)\0Normal (C, Clamped/Spotted)\0"))
                                {
                                    profile.Mode = (OrbitProfileMode)modeInt;
                                }
                                if (ImGui.BeginItemTooltip())
                                {
                                    ImGui.Text("Full Rotation (\"A\"): unclamped, follows raw motion exactly.\nBase Only Ignore-X (\"B (Ignore-X)\"): base-only, hip-projected\n(X-ignored) position - for locomotion (WALK/RUN/DASH-like).\nBase Only (\"B\"): base-only, raw nose position.\nNormal (\"C\"): clamped/spotted head-tracking follow (attacks) -\nreveals the Yaw/Pitch/Roll Range/Blend section and attack-\nspecific options below.");
                                    ImGui.EndTooltip();
                                }

                                // --- [改善3] Add Keyword 入力欄をリストの最上部に配置 & 先頭に追加 ---
                                ImGui.SetNextItemWidth(width * 0.3f);
                                ImGui.InputText("##NewProfileKeyword", ref profile.KeywordInput, 64);
                                ImGui.SameLine();
                                if (ImGui.Button("Add Keyword") && profile.KeywordInput.Length > 0)
                                {
                                    profile.Keywords.Insert(0, profile.KeywordInput); // 先頭に挿入
                                    profile.KeywordInput = "";
                                }

                                for (int keywordIndex = 0; keywordIndex < profile.Keywords.Count; keywordIndex++)
                                {
                                    ImGui.PushID(keywordIndex);
                                    string keywordValue = profile.Keywords[keywordIndex];
                                    ImGui.SetNextItemWidth(width * 0.3f);
                                    if (ImGui.InputText("##ProfileKeyword", ref keywordValue, 64))
                                    {
                                        profile.Keywords[keywordIndex] = keywordValue;
                                    }
                                    ImGui.SameLine();
                                    if (ImGui.Button("Remove"))
                                    {
                                        profile.Keywords.RemoveAt(keywordIndex);
                                        ImGui.PopID();
                                        break;
                                    }
                                    ImGui.PopID();
                                }
                                //ver7ここまで

                                if (profile.Mode == OrbitProfileMode.Normal)
                                {
                                    ImGui.Text("Yaw (turning left/right):");
                                    ImGui.DragFloat("Yaw Range (deg)##Profile", ref profile.YawRange, 1.0f, 0.0f, 180.0f);
                                    ImGui.DragFloat("Yaw Blend (deg)##Profile", ref profile.YawBlend, 1.0f, 0.1f, 90.0f);
                                    ImGui.Text("Pitch (front-flip/somersault):");
                                    ImGui.DragFloat("Pitch Range (deg)##Profile", ref profile.PitchRange, 1.0f, 0.0f, 180.0f);
                                    ImGui.DragFloat("Pitch Blend (deg)##Profile", ref profile.PitchBlend, 1.0f, 0.1f, 90.0f);
                                    ImGui.Text("Roll (sideways barrel roll):");
                                    ImGui.DragFloat("Roll Range (deg)##Profile", ref profile.RollRange, 1.0f, 0.0f, 180.0f);
                                    ImGui.DragFloat("Roll Blend (deg)##Profile", ref profile.RollBlend, 1.0f, 0.1f, 90.0f);
                                }

                                ImGui.DragFloat("Target Y##Profile", ref profile.TargetY, 1.0f);
                                ImGui.SameLine();
                                ImGui.DragFloat("Target Right##Profile", ref profile.TargetRight, 1.0f);
                                ImGui.SameLine();
                                ImGui.DragFloat("Target Forward##Profile", ref profile.TargetForward, 1.0f);
                                if (ImGui.BeginItemTooltip())
                                {
                                    ImGui.Text("This profile's own eye-position offset - independent of every\nother profile's own, and of the top-level default (Target\nY/Right/Forward above) used only when no profile matches at\nall.");
                                    ImGui.EndTooltip();
                                }
                                //ver12
                                ImGui.Checkbox("Override Position/Basis Joint##Profile", ref profile.PositionJointOverrideEnable);
                                if (ImGui.BeginItemTooltip())
                                {
                                    ImGui.Text("When enabled, this profile uses a DIFFERENT joint than the\nglobal \"Use Face Joints\"/\"Target Joint\" above for BOTH the\neye's position AND (with Simple Lock + Face Basis on) the\nnose position used to build the face-basis rotation.\nLets a specific motion (e.g. drinking a potion) sit the\ncamera at a body joint - a hand, say - instead of the face,\nfor a close-up \"prop cam\" shot instead of a first-person head\nview. Useful when the head-anchored view makes an item look\nlike it's clipping into the wrong part of the face, since the\nitem-and-camera relationship stays fixed instead of drifting\nwith every slightly-different head angle.\nUses the previous frame's profile match, so there's up to 1\nframe of lag exactly on the switch frame - not noticeable over\na multi-second motion. Combine with a short \"Profile Switch\nBlend Time\" and low \"Base Smoothing\" above so the switch itself\nis quick.");
                                    ImGui.EndTooltip();
                                }
                                if (profile.PositionJointOverrideEnable)
                                {
                                    ImGui.Checkbox("Use Face Joints##ProfilePositionOverride", ref profile.PositionJointOverrideUseFace);
                                    ImGui.SameLine();
                                    ImGui.SetNextItemWidth(width * 0.3f);
                                    ImGui.DragInt("Joint Index##ProfilePositionOverride", ref profile.PositionJointOverrideJoint, 1.0f, 0, 60);
                                    if (ImGui.BeginItemTooltip())
                                    {
                                        ImGui.Text("Same joint list/indices as the global \"Use Face Joints\"/\n\"Target Joint\" above - e.g. unchecked + 10-27 for the\nbody-joint range that includes the hand.");
                                        ImGui.EndTooltip();
                                    }
                                }
                                //ver12ここまで
                                ImGui.DragFloat("Correction Yaw (deg)##Profile", ref profile.CorrectionYaw, 1.0f, -180.0f, 180.0f);
                                ImGui.SameLine();
                                ImGui.DragFloat("Correction Pitch (deg)##Profile", ref profile.CorrectionPitch, 1.0f, -180.0f, 180.0f);
                                ImGui.SameLine();
                                ImGui.DragFloat("Correction Roll (deg)##Profile", ref profile.CorrectionRoll, 1.0f, -180.0f, 180.0f);
                                if (ImGui.BeginItemTooltip())
                                {
                                    ImGui.Text("This profile's own baseline yaw/pitch/roll (what \"0\ndeviation\" points toward) - fixes a fixed *angular* offset\n(e.g. reticle sitting a few degrees off true center) that\nTarget Y/Right/Forward above can't, since that only moves\nthe eye's *position*, not which way it points. Independent\nof every other profile's own, and of the top-level default\nused only when no profile matches at all.\n\nLIMITATION (Normal mode only): has no visible effect on a\nprofile with Yaw/Pitch/RollRange near 0 (full real-time\ntracking) - the correction mathematically cancels itself out\nfor those. Use \"Use Native Aim Camera\" below instead for\nfull-tracking profiles that need a corrected aim direction.");
                                    ImGui.EndTooltip();
                                }
                                ImGui.DragFloat("Base Smoothing##Profile", ref profile.ClampBaseSmoothing, 0.01f, 0.01f, 1.0f);
                                if (ImGui.BeginItemTooltip())
                                {
                                    ImGui.Text("This profile's own low-pass filter strength on the base\ndirection - 1.0 = raw/instant, lower = smoother. Independent\nof every other profile's own, and of the top-level default\n(\"Base Smoothing (Default)\" above) used only when no profile\nmatches at all.");
                                    ImGui.EndTooltip();
                                }
                                ImGui.Checkbox("Enable Transition Blend##Profile", ref profile.EnableTransitionBlend);
                                if (profile.EnableTransitionBlend)
                                {
                                    ImGui.SameLine();
                                    ImGui.SetNextItemWidth(width * 0.2f);
                                    ImGui.DragFloat("Profile Switch Blend Time (s)##Profile", ref profile.ProfileTransitionDuration, 0.01f, 0.0f, 2.0f);
                                }
                                if (ImGui.BeginItemTooltip())
                                {
                                    ImGui.Text("Whether switching into this profile triggers a fixed-duration\nSlerp blend instead of an instant cut, and (if enabled) how\nlong that blend takes for THIS profile specifically -\nindependent of every other profile's own, and of the\ntop-level default used only when no profile matches at all.");
                                    ImGui.EndTooltip();
                                }

                                if (profile.Mode == OrbitProfileMode.Normal)
                                {
                                    ImGui.Separator();
                                    ImGui.DragFloat("Shake Suppress (sec, 0=off)##Profile", ref profile.ShakeSuppressSeconds, 0.01f, 0.0f, 2.0f);
                                    if (ImGui.BeginItemTooltip())
                                    {
                                        ImGui.Text("Freezes the raw joint deviation for this many seconds,\nstarting the instant R2 is pressed or released while this\nprofile is active - the two moments an animation kick\nactually happens (nocking, then firing) - then snaps\nstraight back to instant tracking. R-stick input is not\naffected. 0 disables.");
                                        ImGui.EndTooltip();
                                    }
                                    ImGui.DragFloat("Look Sensitivity x##Profile", ref profile.LookSensitivityMultiplier, 0.01f, 0.0f, 5.0f);
                                    if (ImGui.BeginItemTooltip())
                                    {
                                        ImGui.Text("Scales right-stick response while this profile is active,\nso it can be tuned to actually match this motion's own\non-screen aim speed (e.g. AIM_IDLE) instead of whatever\nfeels right for ordinary free-look. 1.0 = no change.");
                                        ImGui.EndTooltip();
                                    }
                                    ImGui.Checkbox("Use Native Aim Camera (experimental)##Profile", ref profile.UseNativeAimCamera);
                                    if (ImGui.BeginItemTooltip())
                                    {
                                        ImGui.Text("Instead of this mod's own joint+clamp+R-stick camera, use\nthe game's own native camera directly while this profile is\nactive (captured right after the game computes it each\nframe, before this mod would otherwise override it) -\neverything above (Range/Blend, Target offset, Correction,\nMax Joint Speed) is simply skipped for this profile. Matches\nwhat you see with Enable Free Camera disabled. Experimental -\nverify this actually tracks correctly in-game.");
                                        ImGui.EndTooltip();
                                    }
                                    ImGui.DragFloat("Delay Before Native Camera (sec)##Profile", ref profile.UseNativeAimCameraDelaySeconds, 0.01f, 0.0f, 1.0f);
                                    if (ImGui.BeginItemTooltip())
                                    {
                                        ImGui.Text("Waits this long after L2 is first pressed before switching\nto the native camera above. The native camera isn't actively\ndriven while Free Camera is on, so it's just sitting wherever\nit last was; if the base game's \"Aim Direction\" setting is\n\"Player Facing\", the instant L2 is pressed it animates the\nnative camera from that stale pose to the player's actual\nfacing over a short transition (commonly around 0.25s) -\ninvisible normally (Free Camera hides the native camera\nentirely), but switching to it immediately would show that\nswing on screen. Set this to roughly that transition's\nduration (or a little longer) so the switch-over happens\nafter it's already settled and looks seamless. 0 = switch\ninstantly (may show the swing). While this timer counts\ndown, this mod also smoothly pre-blends its own camera's\npitch toward the native camera's pitch, so pitch has\nalready caught up by the time the switch happens too -\nyaw is unaffected since it already matches.");
                                        ImGui.EndTooltip();
                                    }
                                    ImGui.Checkbox("Disable Look While Aiming (L2)##Profile", ref profile.DisableLookWhileAiming);
                                    if (ImGui.BeginItemTooltip())
                                    {
                                        ImGui.Text("While L2 is held and this profile is active, this mod's own\nR-stick-driven camera offset is suppressed entirely - the\ncamera just stays centered instead of trying to track the\naim. The reticle still moves normally (the base game reads\nthe raw stick directly, independent of this mod's camera) -\nonly this mod's own camera stops chasing it. Fixes visible\ndrift between the camera and the actual reticle caused by\nthis mod's R-stick response (acceleration, return-to-center\nspeed) not matching the base game's own aim response to the\nsame stick input. An alternative to Use Native Aim Camera\nabove for profiles where that isn't wanted.");
                                        ImGui.EndTooltip();
                                    }
                                    //ver13
                                    ImGui.Checkbox("Hide Slinger While Active##Profile", ref profile.HideSlingerWhileActive);
                                    if (ImGui.BeginItemTooltip())
                                    {
                                        ImGui.Text("While this profile is matched (e.g. AIM_IDLE), forces the\nSlinger (Armor.Slinger) fully hidden, and shows it again the\nmoment this profile stops matching. Confirmed to work during\nactual hunting, unlike per-part Body hiding.");
                                        ImGui.EndTooltip();
                                    }
                                    //ver13ここまで
                                    //ver15
                                    ImGui.Checkbox("Disable Free Camera While Active##Profile", ref profile.DisableFreeCameraWhileActive);
                                    if (ImGui.BeginItemTooltip())
                                    {
                                        ImGui.Text("While this profile is matched (e.g. clutch claw grapple or\nmonster riding), forces Enable Free Camera off, switching\nback to the game's own native third-person camera - useful\nwhen this mod's first-person view shakes too much for these\nmotions. The moment this profile stops matching, Enable Free\nCamera is restored to whatever it was set to before this\nprofile forced it off.");
                                        ImGui.EndTooltip();
                                    }
                                    //ver15ここまで
                                    ImGui.Separator();
                                    ImGui.Checkbox("Use Gaze Keyframes (ignore Range/Blend above)", ref profile.UseGazeKeyframes);
                                    if (ImGui.BeginItemTooltip())
                                    {
                                        ImGui.Text("Instead of following the live joint deviation via\nRange/Blend above (which reproduces any noise/violence in\nthe raw skeletal motion), look up a Yaw/Pitch/Roll target\nfrom the keyframe list below, keyed by how far the current\nmotion has played (0.0 = motion start, 1.0 = motion end),\nand Slerp smoothly between the two surrounding keyframes.\nUse this for fast/noisy attack motions where the raw\ndeviation is too violent to look at directly and you'd\nrather hand-author a simple, deliberate glance instead.\nSave Settings saves these into NewCamera.json along with\neverything else.");
                                        ImGui.EndTooltip();
                                    }
                                    //ver10.1
                                    if (profile.UseGazeKeyframes)
                                    {
                                        ImGui.Indent();
                                        ImGui.TextWrapped("T = motion progress (0.0 start .. 1.0 end). Keep sorted low-to-high. Use the Animation panel's Frame slider to find T for a given pose, and the Deviation readout above to sanity-check Yaw/Pitch/Roll while scrubbing.");

                                        ImGui.Checkbox("Use SubState Timer for T (instead of Motion Frame)##GazeSubState", ref profile.GazeUseSubStateTimer);
                                        if (ImGui.BeginItemTooltip())
                                        {
                                            ImGui.Text("OFF (default): T はモーションの再生位置 (Frame/MaxFrame) から取る。\nON: T の代わりに \"Action SubState (+0x760) が最後に変化してから\n何秒経ったか\" ÷ Assumed Duration を使う。\n\nActionName/motionKey が変わらないまま上半身側の状態だけが\n変わる場面 (回復薬を飲む=8 など、キーワードを \"#8\" で\nマッチさせているプロファイル) は、ベースモーションの\nフレームがそのサブ動作の進行と一致しないため、こちらを使う。\n\nこの下の Keyframe の T は、いずれの場合も 0.0-1.0 のまま\n共通で解釈される (単に何を基準に 0.0-1.0 を測るかが変わるだけ)。");
                                            ImGui.EndTooltip();
                                        }
                                        if (profile.GazeUseSubStateTimer)
                                        {
                                            ImGui.Indent();
                                            ImGui.SetNextItemWidth(width * 0.3f);
                                            ImGui.DragFloat("Assumed Duration (sec)##GazeSubState", ref profile.GazeSubStateDuration, 0.05f, 0.05f, 10.0f);
                                            if (ImGui.BeginItemTooltip())
                                            {
                                                ImGui.Text("経過秒数をこの値で割ったものを T (0.0-1.0) として使う。\nこのサブステートが実際にどれくらい続くか、\"SubState Elapsed\"\n(DEBUG 画面) をストップウォッチ代わりに見ながら実測して\n近い値に合わせる。短すぎると T=1.0 に貼り付いたまま\n終盤のキーフレームで固定され、長すぎると T が最後まで\n1.0 に届かない。");
                                                ImGui.EndTooltip();
                                            }
                                            ImGui.Text($"SubState Elapsed: {orbitSubStateElapsedSec:F2}s  ->  T: {getSubStateProgress(profile.GazeSubStateDuration):F3}");
                                            ImGui.Unindent();
                                        }

                                        int gazeKeyframeRemoveIndex = -1;
                                        //ver10.1ここまで

                                        for (int gazeKeyframeIndex = 0; gazeKeyframeIndex < profile.GazeKeyframes.Count; gazeKeyframeIndex++)
                                        {
                                            ImGui.PushID(gazeKeyframeIndex);
                                            OrbitGazeKeyframe gazeKeyframe = profile.GazeKeyframes[gazeKeyframeIndex];
                                            ImGui.SetNextItemWidth(width * 0.15f);
                                            ImGui.DragFloat("T##GazeKeyframe", ref gazeKeyframe.T, 0.01f, 0.0f, 1.0f);
                                            ImGui.SameLine();
                                            ImGui.SetNextItemWidth(width * 0.15f);
                                            ImGui.DragFloat("Yaw##GazeKeyframe", ref gazeKeyframe.Yaw, 1.0f, -180.0f, 180.0f);
                                            ImGui.SameLine();
                                            ImGui.SetNextItemWidth(width * 0.15f);
                                            ImGui.DragFloat("Pitch##GazeKeyframe", ref gazeKeyframe.Pitch, 1.0f, -180.0f, 180.0f);
                                            ImGui.SameLine();
                                            ImGui.SetNextItemWidth(width * 0.15f);
                                            ImGui.DragFloat("Roll##GazeKeyframe", ref gazeKeyframe.Roll, 1.0f, -180.0f, 180.0f);
                                            ImGui.SameLine();
                                            if (ImGui.SmallButton("Remove##GazeKeyframe"))
                                            {
                                                gazeKeyframeRemoveIndex = gazeKeyframeIndex;
                                            }
                                            ImGui.PopID();
                                        }
                                        if (gazeKeyframeRemoveIndex >= 0)
                                        {
                                            profile.GazeKeyframes.RemoveAt(gazeKeyframeRemoveIndex);
                                        }

                                        //ver10
                                        if (ImGui.Button("Add Keyframe##GazeKeyframe"))
                                        {
                                            // New keyframe starts just after the
                                            // last one (or at 0 if this is the
                                            // first), copying its angles, so
                                            // adding one doesn't snap the curve -
                                            // adjust T/angles afterward.
                                            float nextT = profile.GazeKeyframes.Count > 0
                                                ? Math.Clamp(profile.GazeKeyframes[^1].T + 0.05f, 0.0f, 1.0f)
                                                : 0.0f;
                                            float lastYaw = profile.GazeKeyframes.Count > 0 ? profile.GazeKeyframes[^1].Yaw : 0.0f;
                                            float lastPitch = profile.GazeKeyframes.Count > 0 ? profile.GazeKeyframes[^1].Pitch : 0.0f;
                                            float lastRoll = profile.GazeKeyframes.Count > 0 ? profile.GazeKeyframes[^1].Roll : 0.0f;
                                            profile.GazeKeyframes.Add(new OrbitGazeKeyframe { T = nextT, Yaw = lastYaw, Pitch = lastPitch, Roll = lastRoll });
                                        }

                                        ImGui.Separator();
                                        ImGui.Checkbox("Limit Gaze To Ranges##GazeRange", ref profile.GazeUseRanges);
                                        if (ImGui.BeginItemTooltip())
                                        {
                                            ImGui.Text("OFF: Gaze Keyframes をモーション全体で使う (従来どおり)。\nON : 下で指定した T 区間の中でだけ Gaze Keyframes を使い、\n     区間の外では上の Range/Blend による通常の追従に戻る。\n\n何回転もする攻撃で「最初の振りかぶり」と「最後に剣を抜いて\n構え直すところ」だけ揺れを抑え、間の回転部分は完全追従に\n任せたい、という場合に使う。区間は複数登録できる。\n\n区間をひとつも登録していない場合は制限なし (全体で有効) 扱い。");
                                            ImGui.EndTooltip();
                                        }
                                        if (profile.GazeUseRanges)
                                        {
                                            ImGui.Indent();
                                            ImGui.TextWrapped("Start / End = motion progress T (0.0 start .. 1.0 end). Blend = extra T added on both sides to cross-fade over (0 = hard cut). Overlapping ranges use whichever is strongest.");
                                            ImGui.Text($"Gaze Weight now: {orbitLastGazeWeight:F2}");
                                            if (ImGui.BeginItemTooltip())
                                            {
                                                ImGui.Text("いま実際に Gaze Keyframes がどれだけ効いているか。\n1.00 = Gaze のみ、0.00 = 通常の Range/Blend 追従のみ、\n途中の値 = 区間の出入り口でクロスフェード中。\nこのプロファイルが [ACTIVE] のときだけ意味のある値になる。");
                                                ImGui.EndTooltip();
                                            }
                                            int gazeRangeRemoveIndex = -1;
                                            for (int gazeRangeIndex = 0; gazeRangeIndex < profile.GazeRanges.Count; gazeRangeIndex++)
                                            {
                                                ImGui.PushID(7000 + gazeRangeIndex);
                                                OrbitGazeRange gazeRange = profile.GazeRanges[gazeRangeIndex];
                                                ImGui.SetNextItemWidth(width * 0.15f);
                                                ImGui.DragFloat("Start##GazeRange", ref gazeRange.Start, 0.01f, 0.0f, 1.0f);
                                                ImGui.SameLine();
                                                ImGui.SetNextItemWidth(width * 0.15f);
                                                ImGui.DragFloat("End##GazeRange", ref gazeRange.End, 0.01f, 0.0f, 1.0f);
                                                ImGui.SameLine();
                                                ImGui.SetNextItemWidth(width * 0.15f);
                                                ImGui.DragFloat("Blend##GazeRange", ref gazeRange.Blend, 0.01f, 0.0f, 0.5f);
                                                ImGui.SameLine();
                                                if (ImGui.SmallButton("Remove##GazeRange"))
                                                {
                                                    gazeRangeRemoveIndex = gazeRangeIndex;
                                                }
                                                ImGui.PopID();
                                            }
                                            if (gazeRangeRemoveIndex >= 0)
                                            {
                                                profile.GazeRanges.RemoveAt(gazeRangeRemoveIndex);
                                            }
                                            if (ImGui.Button("Add Range##GazeRange"))
                                            {
                                                profile.GazeRanges.Add(new OrbitGazeRange { Start = 0.0f, End = 0.2f, Blend = 0.05f });
                                            }
                                            ImGui.Unindent();
                                        }
                                        //ver11
                                        ImGui.Unindent();
                                    }

                                    ImGui.Separator();
                                    ImGui.Checkbox("Use Gaze Position Keyframes (XYZ)##ProfilePos", ref profile.UseGazePositionKeyframes);
                                    if (ImGui.BeginItemTooltip())
                                    {
                                        ImGui.Text("Yaw/Pitch/Rollではなく、Target Y/Right/Forward (カメラの\n位置オフセット) 側を T ごとに指定するバージョン。上の\nRotation用 Gaze Keyframesと同じ T (Use SubState Timer for T /\nLimit Gaze To Ranges の設定も共通で使われる) 上で、\n位置だけを別に手打ちできる。\n\n例: 振りかぶりでわずかに後ろへ引く (Forwardをマイナスに)、\n構え直しで元の位置に戻す、といった演出に使う。");
                                        ImGui.EndTooltip();
                                    }
                                    if (profile.UseGazePositionKeyframes)
                                    {
                                        ImGui.Indent();
                                        ImGui.TextWrapped("T source is shared with the Rotation Gaze Keyframes above. Y = up(+)/down(-), Right = right(+)/left(-), Forward = forward(+)/back(-), same units as Target Y/Right/Forward.");
                                        int gazePositionKeyframeRemoveIndex = -1;
                                        for (int gazePositionKeyframeIndex = 0; gazePositionKeyframeIndex < profile.GazePositionKeyframes.Count; gazePositionKeyframeIndex++)
                                        {
                                            ImGui.PushID(8000 + gazePositionKeyframeIndex);
                                            OrbitGazePositionKeyframe gazePositionKeyframe = profile.GazePositionKeyframes[gazePositionKeyframeIndex];
                                            ImGui.SetNextItemWidth(width * 0.15f);
                                            ImGui.DragFloat("T##GazePosKeyframe", ref gazePositionKeyframe.T, 0.01f, 0.0f, 1.0f);
                                            ImGui.SameLine();
                                            ImGui.SetNextItemWidth(width * 0.15f);
                                            ImGui.DragFloat("Y##GazePosKeyframe", ref gazePositionKeyframe.Y, 1.0f);
                                            ImGui.SameLine();
                                            ImGui.SetNextItemWidth(width * 0.15f);
                                            ImGui.DragFloat("Right##GazePosKeyframe", ref gazePositionKeyframe.Right, 1.0f);
                                            ImGui.SameLine();
                                            ImGui.SetNextItemWidth(width * 0.15f);
                                            ImGui.DragFloat("Forward##GazePosKeyframe", ref gazePositionKeyframe.Forward, 1.0f);
                                            ImGui.SameLine();
                                            if (ImGui.SmallButton("Remove##GazePosKeyframe"))
                                            {
                                                gazePositionKeyframeRemoveIndex = gazePositionKeyframeIndex;
                                            }
                                            ImGui.PopID();
                                        }
                                        if (gazePositionKeyframeRemoveIndex >= 0)
                                        {
                                            profile.GazePositionKeyframes.RemoveAt(gazePositionKeyframeRemoveIndex);
                                        }
                                        if (ImGui.Button("Add Keyframe##GazePosKeyframe"))
                                        {
                                            // 新しいキーフレームは、直前のキーフレームの直後(なければT=0)、
                                            // 値も直前のものを引き継ぐ (なければこのプロファイルの現在の
                                            // Target Y/Right/Forward) - 追加した瞬間にカクつかないように。
                                            float nextT = profile.GazePositionKeyframes.Count > 0
                                                ? Math.Clamp(profile.GazePositionKeyframes[^1].T + 0.05f, 0.0f, 1.0f)
                                                : 0.0f;
                                            float lastY = profile.GazePositionKeyframes.Count > 0 ? profile.GazePositionKeyframes[^1].Y : profile.TargetY;
                                            float lastRight = profile.GazePositionKeyframes.Count > 0 ? profile.GazePositionKeyframes[^1].Right : profile.TargetRight;
                                            float lastForward = profile.GazePositionKeyframes.Count > 0 ? profile.GazePositionKeyframes[^1].Forward : profile.TargetForward;
                                            profile.GazePositionKeyframes.Add(new OrbitGazePositionKeyframe { T = nextT, Y = lastY, Right = lastRight, Forward = lastForward });
                                        }
                                        ImGui.Unindent();
                                    }
                                }
                                ImGui.Unindent();
                            }
                            ImGui.PopID();
                            //ver11
                            if (profileRemoved)
                            {
                                break;
                            }
                            ImGui.Separator();
                        }
                        //ver14
                        if (weaponGroupIndented)
                        {
                            ImGui.Unindent();
                        }
                        //ver14ここまで

                        //ver10
                        // --- ループ完了後に安全に並び替える ---ver7
                        // ▲▼ (隣との移動) と、ドラッグ＆ドロップ (任意の位置へ移動) の
                        // 両方をここで処理する。単純な入れ替えではなく「いったん抜いて
                        // 目的の位置に差し込む」方式にしてあるので、離れた位置へ
                        // ドロップしても間のプロファイルの並び順が保たれる。
                        // 隣同士の移動の場合は結果が従来の入れ替えと完全に同じ。
                        if (moveProfileFrom >= 0 && moveProfileTo >= 0
                            && moveProfileFrom != moveProfileTo
                            && moveProfileFrom < orbitProfiles.Count
                            && moveProfileTo < orbitProfiles.Count)
                        {
                            //ver14
                            OrbitProfile movedProfile = orbitProfiles[moveProfileFrom];
                            // 別グループへドロップした場合は、移動と同時に
                            // グループ名も移動先のものに書き換える。
                            if (moveProfileToGroup != null)
                            {
                                movedProfile.WeaponGroup = moveProfileToGroup;
                                movedProfile.WeaponGroupInput = moveProfileToGroup;
                            }
                            orbitProfiles.RemoveAt(moveProfileFrom);
                            orbitProfiles.Insert(moveProfileTo, movedProfile);
                        }
                        //ver14ここまで

                        // --- グループ見出しのドラッグ&ドロップによる、グループ単位の並び替え ---
                        if (moveGroupFromName != null && moveGroupToName != null
                            && moveGroupFromName != moveGroupToName)
                        {
                            List<OrbitProfile> movedGroupBlock = new List<OrbitProfile>();
                            for (int i = orbitProfiles.Count - 1; i >= 0; i--)
                            {
                                if ((orbitProfiles[i].WeaponGroup ?? "") == moveGroupFromName)
                                {
                                    movedGroupBlock.Insert(0, orbitProfiles[i]);
                                    orbitProfiles.RemoveAt(i);
                                }
                            }
                            int groupInsertAt = orbitProfiles.FindIndex(p => (p.WeaponGroup ?? "") == moveGroupToName);
                            if (groupInsertAt < 0)
                            {
                                groupInsertAt = orbitProfiles.Count;
                            }
                            orbitProfiles.InsertRange(groupInsertAt, movedGroupBlock);
                        }
                        //ver14
                        // --- "Set Group" ボタンが押された時だけ、そのプロファイル1つを
                        // 確定し、既存の同名グループの末尾へ移動する。
                        // 入力中は一切動かさないので、編集内容が隣に飛ぶことはない。---
                        if (commitWeaponGroupIndex >= 0 && commitWeaponGroupIndex < orbitProfiles.Count)
                        {
                            OrbitProfile committedProfile = orbitProfiles[commitWeaponGroupIndex];
                            committedProfile.WeaponGroup = committedProfile.WeaponGroupInput ?? "";
                            string committedGroupName = committedProfile.WeaponGroup;
                            bool hasOtherMember = false;
                            for (int i = 0; i < orbitProfiles.Count; i++)
                            {
                                if (i != commitWeaponGroupIndex && (orbitProfiles[i].WeaponGroup ?? "") == committedGroupName)
                                {
                                    hasOtherMember = true;
                                    break;
                                }
                            }
                            if (hasOtherMember)
                            {
                                orbitProfiles.RemoveAt(commitWeaponGroupIndex);
                                int committedInsertAt = orbitProfiles.FindIndex(p => (p.WeaponGroup ?? "") == committedGroupName);
                                while (committedInsertAt >= 0 && committedInsertAt < orbitProfiles.Count
                                    && (orbitProfiles[committedInsertAt].WeaponGroup ?? "") == committedGroupName)
                                {
                                    committedInsertAt++;
                                }
                                orbitProfiles.Insert(committedInsertAt, committedProfile);
                            }
                        }
                        //ver14ここまで

                        if (ImGui.Button("Add Profile"))
                        {
                            orbitProfiles.Add(new OrbitProfile());
                        }

                        ImGui.Checkbox("Force Level Roll (non-Full-Rotation only)", ref orbitForceLevelRoll);
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("Keeps Up as close to true world-up as possible for any matched\nBase Only / Base Only Ignore-X / Normal profile (and the\nunmatched-motion fallback, which also isn't Full Rotation) -\nremoves roll around the forward axis entirely, regardless of\nthe computed roll deviation. Does NOT apply to Full Rotation\n(\"A\") - a motion that genuinely rolls sideways (e.g. a lateral\nroll dodge) will still roll if it's registered under Full\nRotation; only register it under a Normal-mode profile instead\nif the roll should be flattened out.");
                            ImGui.EndTooltip();
                        }
                        ImGui.DragFloat("Transition Max Speed (deg/s)", ref orbitFinalRotationMaxSpeed, 10.0f, 0.0f, 5000.0f);
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("Caps how fast the camera can visibly rotate per frame,\nregardless of cause - smooths out switching between A/B/C and\npressing the recenter button so they animate instead of\nsnapping instantly. Set high enough that real full-body-\nrotation motions (A) still track at effectively full speed;\nset to 0 to disable (raw, instant).");
                            ImGui.EndTooltip();
                        }
                        ImGui.DragFloat("Position Transition Max Speed", ref orbitFinalPositionMaxSpeed, 50.0f, 0.0f, 50000.0f);
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("Same idea, but for position - smooths out the jump when the\ncamera's position source changes discontinuously (e.g. B\n(Ignore-X)'s hip-projected position vs. everything else's raw\nnose position). Independent of the Lerp setting above. Set to\n0 to disable (raw, instant).");
                            ImGui.EndTooltip();
                        }
                        ImGui.Checkbox("Enable Transition Blend (Default)", ref orbitDefaultEnableTransitionBlend);
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("Fallback used only when a motion matches none of the profiles\nabove at all (defaults to \"A\") - every profile above has its\nown independent Enable Transition Blend that takes priority\nwhenever it matches.");
                            ImGui.EndTooltip();
                        }
                        ImGui.DragFloat("Profile Switch Blend Time (Default) (s)", ref orbitProfileTransitionDuration, 0.01f, 0.0f, 2.0f);
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("The instant the matched profile changes (a different profile,\nor switching to/from no profile matching at all) - which can\njump to a completely different YPR even when the underlying\njoint data didn't jump at all - Slerp from whatever was on\nscreen the previous frame to the new target over exactly the\nentered profile's own Profile Switch Blend Time (or this\nfallback value, if no profile matched), instead of snapping\nstraight to it. Unlike Transition Max Speed above (which takes\nlonger for bigger jumps), this always takes the same fixed\namount of time regardless of jump size. The two stack: this\nruns first, then Transition Max Speed still applies on top as a\ngeneral safety net. Set to 0 to disable (falls back to\nTransition Max Speed alone).");
                            ImGui.EndTooltip();
                        }
                        ImGui.Separator();
                        if (ImGui.Button("Save All Settings"))
                        {
                            saveSessionAndOrbitalSettings();
                        }
                        if (ImGui.BeginItemTooltip())
                        {
                            ImGui.Text("Saves this A/B/C tab (keyword lists, ranges, smoothing,\ncorrections, etc.), the rest of the Orbital Camera setup\n(Target Joint, Use Face Joints, Target Y, Lerp, Recenter Tap Max\nFrames), and the session Toggles/armor-hide/near-clip/slow-motion\nsettings below - to this plugin's normal config file, reloaded\nautomatically on next launch.");
                            ImGui.EndTooltip();
                        }
                        ImGui.SameLine();
                        if (ImGui.Button("Reload Saved Settings"))
                        {
                            loadSessionAndOrbitalSettings();
                        }
                    }
                }
                if (!orbitSimpleLock)
                {
                ImGui.InputInt("Rotation Joint", ref orbitRotationJoint);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("This joint's rotation is what the camera switches to once the\nangle from Base Rotation Joint exceeds what a human neck could\ndo (see below) - i.e. dodge rolls / spin attacks. Set to -1 to\nalways use the base joint / player rotation only.");
                    ImGui.EndTooltip();
                }
                ImGui.Checkbox("Rotation Uses Face Joints", ref orbitRotationJointUseFace);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Whether \"Rotation Joint\" above indexes Body Joints (off) or\nFace Joints (on).");
                    ImGui.EndTooltip();
                }
                ImGui.InputInt("Base Rotation Joint", ref orbitBaseRotationJoint);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Stable \"always faces forward\" reference joint (e.g. Body Joint 0).\nThe camera locks onto this direction, like eyes tracking straight\nahead, as long as that's within human neck range of the\nRotation Joint below. Set to -1 to use the player's raw body\nrotation as the lock-on target instead of a joint.");
                    ImGui.EndTooltip();
                }
                ImGui.Checkbox("Base Uses Face Joints", ref orbitBaseRotationJointUseFace);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Whether \"Base Rotation Joint\" above indexes Body Joints (off) or\nFace Joints (on).");
                    ImGui.EndTooltip();
                }
                ImGui.Text($"Deviation - Yaw: {orbitLastRelYaw:F0}  Pitch: {orbitLastRelPitch:F0}  Roll: {orbitLastRelRoll:F0} (deg)");
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("How far the Rotation Joint currently deviates from the Base\nRotation Joint, decomposed per-axis. Watch these while turning,\nwalking, rolling sideways, doing a forward roll, etc. to set the\nranges below (a bit higher than what you see during normal play).");
                    ImGui.EndTooltip();
                }
                ImGui.Text("Yaw (turning left/right):");
                ImGui.DragFloat("Yaw Range (deg)", ref orbitNeckYawRange, 1.0f, 0.0f, 180.0f);
                ImGui.DragFloat("Yaw Blend (deg)", ref orbitNeckYawBlend, 1.0f, 0.1f, 90.0f);
                ImGui.DragFloat("Yaw Max Follow (deg)", ref orbitNeckYawMaxFollow, 1.0f, 0.0f, 360.0f);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Caps how far the camera actually turns even if the joint keeps\nspinning further (e.g. a multi-rotation hammer spin) - prevents\nan unnatural continuous \"ballerina\" spin. Raise this if turns\nfeel clipped too early; lower it to reduce dizziness on big spins.");
                    ImGui.EndTooltip();
                }
                ImGui.Text("Pitch (front-flip/somersault):");
                ImGui.DragFloat("Pitch Range (deg)", ref orbitNeckPitchRange, 1.0f, 0.0f, 180.0f);
                ImGui.DragFloat("Pitch Blend (deg)", ref orbitNeckPitchBlend, 1.0f, 0.1f, 90.0f);
                ImGui.DragFloat("Pitch Max Follow (deg)", ref orbitNeckPitchMaxFollow, 1.0f, 0.0f, 360.0f);
                ImGui.Text("Roll (sideways barrel roll):");
                ImGui.DragFloat("Roll Range (deg)", ref orbitNeckRollRange, 1.0f, 0.0f, 180.0f);
                ImGui.DragFloat("Roll Blend (deg)", ref orbitNeckRollBlend, 1.0f, 0.1f, 90.0f);
                ImGui.DragFloat("Roll Max Follow (deg)", ref orbitNeckRollMaxFollow, 1.0f, 0.0f, 360.0f);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Roll range is usually kept low (a real neck can only tilt\nsideways so far) so sideways barrel rolls start rotating the\ncamera quickly instead of staying locked forward.");
                    ImGui.EndTooltip();
                }
                ImGui.DragFloat("Rotation Joint Yaw Offset", ref orbitRotationJointYawOffset, 1.0f);
                ImGui.DragFloat("Rotation Joint Pitch Offset", ref orbitRotationJointPitchOffset, 1.0f);
                ImGui.DragFloat("Rotation Joint Roll Offset", ref orbitRotationJointRollOffset, 1.0f);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Fixes a Rotation Joint whose rest pose doesn't line up with\ntrue forward (e.g. facing a bit off to one side). Adjust until\nthe view faces straight ahead while standing still.");
                    ImGui.EndTooltip();
                }
                ImGui.Separator();
                ImGui.Checkbox("Stabilize Rotation", ref orbitStabilize);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Filters small/high-frequency rotation noise from the targeted\njoint (neck sway while walking, idle-stance tilt) while still\ntracking large fast rotations (rolls, spin attacks) at full speed.");
                    ImGui.EndTooltip();
                }
                if (orbitStabilize)
                {
                    ImGui.Text($"Measured speed: {orbitLastAngularSpeed:F0} deg/s");
                    ImGui.DragFloat("Min Speed (deg/s)", ref orbitStabilizeMinSpeed, 1.0f, 0.0f, 2000.0f);
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("At or below this angular speed, treat motion as \"noise\"\n(walking/idle neck sway) and apply heavy smoothing + full\nroll re-leveling. Watch \"Measured speed\" above while walking\nand set this a bit above the numbers you see there.");
                        ImGui.EndTooltip();
                    }
                    ImGui.DragFloat("Max Speed (deg/s)", ref orbitStabilizeMaxSpeed, 1.0f, 0.0f, 5000.0f);
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("At or above this angular speed, track the raw rotation\ninstantly with no roll-leveling - full dodge roll/spin attack\nfeel. Watch \"Measured speed\" during a roll and set this a bit\nbelow the numbers you see there.");
                        ImGui.EndTooltip();
                    }
                    ImGui.DragFloat("Min Smoothing", ref orbitStabilizeMinAlpha, 0.005f, 0.0f, 1.0f);
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("Smoothing strength applied at/below Min Speed. Lower = smoother\n/more delay while standing/walking, higher = snappier but shakier.");
                        ImGui.EndTooltip();
                    }
                    ImGui.Checkbox("Re-level Roll When Slow", ref orbitLevelRoll);
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("Gradually cancels camera roll/tilt the slower the current\nrotation is (fully canceled at/below Min Speed, not canceled\nat/above Max Speed). Turn off to always keep the raw roll.");
                        ImGui.EndTooltip();
                    }
                }
                }
                ImGui.Separator();
                ImGui.Text("Recenter Button (resets manual look to forward):");
                if (orbitRecenterButton == null)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, 0xFF0000FF);
                }
                ImGui.InputText("##RecenterButton", ref typedRecenterButton, 12);
                if (orbitRecenterButton == null)
                {
                    ImGui.PopStyleColor();
                }
                ImGui.SameLine();
                if (ImGui.Button("Save##RecenterButton"))
                {
                    orbitRecenterButton = Config.ParseButton(typedRecenterButton);
                }
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Button that snaps the manual free-look offset back to zero\n(facing forward), replacing the base game's own recenter button\nwhich doesn't work while this camera is active. Type a button\nname, e.g. L1, R1, R3, Triangle, Square, Up, Down, Left, Right,\nthen click Save. Red text means the name wasn't recognized.");
                    ImGui.EndTooltip();
                }
                //ver9.3
                ImGui.InputFloat("Tap Max Seconds", ref orbitRecenterTapMaxSeconds, 0.05f, 0.1f);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Only a *short tap* (held no longer than this many seconds)\nrecenters, matching the base game (a long hold instead opens\nthe item wheel and is left alone). This is real time, not\nframes, so it behaves the same at any framerate. Raise this if\nquick taps aren't being recognized; lower it if long holds\nsometimes recenter by mistake.");
                    ImGui.EndTooltip();
                }
                //ver9.3ここまで
                ImGui.Separator();
                ImGui.Checkbox("Return-to-Center Look", ref orbitReturnToCenterLook);
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.Text("Right stick tilt (angle + amount) directly sets the view's\nyaw/pitch offset from forward every frame, instead of the\nnormal free-look (which accumulates and stays wherever you\nleft it). Releasing the stick springs the view back to forward\nautomatically - useful when scripted motions (talking to an\nNPC, etc.) can otherwise start with a leftover free-look\noffset still applied. Replaces stick-driven look only; the\nRecenter Button above still works but becomes mostly redundant.");
                    ImGui.EndTooltip();
                }
                if (orbitReturnToCenterLook)
                {
                    ImGui.DragFloat("Max Yaw", ref orbitReturnToCenterYawMax, 1.0f, 1.0f, 180.0f);
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("Yaw offset (degrees) at full stick tilt, left or right.");
                        ImGui.EndTooltip();
                    }
                    ImGui.DragFloat("Max Pitch", ref orbitReturnToCenterPitchMax, 1.0f, 1.0f, 89.0f);
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("Pitch offset (degrees) at full stick tilt, up or down.");
                        ImGui.EndTooltip();
                    }
                    //ver8 全周アングルスナップスライダー追加
                    ImGui.DragFloat("Return Speed", ref orbitReturnToCenterSpeed, 0.25f, 0.5f, 30.0f);
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("How quickly the view springs back to forward once the\nstick is released. Higher = snappier/more instant, lower =\nsofter/slower drift back to center.");
                        ImGui.EndTooltip();
                    }
                    ImGui.DragInt("R-Stick Angle Snap (steps)", ref orbitRightStickAngleSnapSteps, 1.0f, 0, 360);
                    if (ImGui.BeginItemTooltip())
                    {
                        ImGui.Text("Rounds the right stick's angle to this many fixed\npositions around the circle (12 = every 30deg, like clock\nhours) before Return-to-Center Look sees it. Reduces\njitter/\"spasming\" on diagonals near full deflection.\n0 = off (full continuous angle resolution).");
                        ImGui.EndTooltip();
                    }
                }
                //ver8 ここまで
                ImGui.PopItemWidth();
                ImGui.PopID();

                ImGui.Separator();
            }
            ImGui.PopID();

            ImGui.PushID("Credits");
            if (ImGui.CollapsingHeader("Credits"))
            {
                ImGui.TextWrapped("Fexty/SharpPluginLoader Authors: Framework for this mod and reference for various memory locations.");
                ImGui.TextWrapped("Otis_Inf: Initial LOD and object fading adjustment locations, time of day and game speed.");
                ImGui.TextWrapped("Andoryuuta: MHW-ClassPropDump/MHW-DTI-Dumps.");
                ImGui.TextWrapped("MonsterHunterWorldModding/wiki Authors.");
                ImGui.Separator();
            }
            ImGui.PopID();
        }
    }
}
