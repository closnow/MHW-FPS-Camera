# MHW FPS Camera Mod (Based on NewCamera)

[English](#english) | [日本語](#japanese)

---

<a name="english"></a>
## English

A mod that enables a First-Person Shooter (FPS) perspective in *Monster Hunter: World*.  
Based on [NewCamera](https://www.nexusmods.com/monsterhunterworld/mods/8300) by Akon City Software, heavily modified and tuned for FPS gameplay.

### ⚠️ Disclaimer & Notice
- **Warning**: Severe screen shake and rapid camera rotations may cause motion sickness. If you are prone to motion sickness or seizures, please use with caution and stop immediately if you feel unwell.
- **This mod is provided "AS-IS". No technical support, bug fixes, or feature requests will be accepted.**
- Most of the modifications in this code were created with the **assistance of AI**. As such, the author cannot address technical issues such as code optimization, unexpected bugs, or conflicts with other mods.
- Camera profiles are **only fully tuned for Lance**. Great Sword, Switch Axe, and Bow are partially tuned (incomplete). All other weapon types are untouched. Please edit `NewCamera.json` yourself to adjust other weapons (see Profile System below).
- Keyboard/mouse controls are not supported. **Gamepad (controller) is required.**

### Requirements
* [SharpPluginLoader (SPL) for MHW](https://github.com/Fexty12573/SharpPluginLoader)
* [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
* [Stracker's Loader](https://www.nexusmods.com/monsterhunterworld/mods/1982)

### Installation
Place both **`NewCamera.dll`** (built) and **`NewCamera.json`** into the following folder:
`nativePC/plugins/CSharp/NewCamera/`  
*(In short, place `NewCamera.json` in the same directory as `NewCamera.dll`)*

### Recommended In-Game Settings
* **GAME SETTINGS**
  * Head Armor: Hidden
* **CONTROLS**
  * Directional Control Type: Type 1
* **CAMERA**
  * Reticle Direction: Character's Direction
  * Camera Terrain Adjustment: Don't Adjust
  * Dynamic Camera Settings: Off
* **DISPLAY**
  * Screen Vibration: Off

### Controls & Hotkeys
> ⚠️ **Important Note on the GUI "Binds" Menu:**  
> The "Binds" section is still visible in the in-game GUI, but **all shortcut features there have been disabled** in the code to prevent misfires. Changing bindings in the GUI will have no effect. Please use the fixed hotkeys below instead:

* **[F7]**: Toggle Free Camera ON / OFF (Use this to temporarily disable the mod camera when reading the Quest Board or Handler's book, as the UI is too far to read in first-person).
* **[F9] (Short Press)**: Toggle 0.1x Slow Motion ON / OFF
* **[F9] (Hold 400ms)**: 10x Fast Forward *(Press F9 twice to reset back to normal speed: Fast -> 0.1x -> 1.0x).*

> **Note on SharpPluginLoader GUI key:**  
> The default GUI key for SharpPluginLoader (SPL) is **[F9]**, which conflicts with this mod's slow-motion key. Please open `loader-config.json` in your game root directory (e.g., `.../Monster Hunter World/loader-config.json`) and add the `"SPL"` section to change the menu key to **[F10]**:
> ```json
> {
>   "logfile": false,
>   "logcmd": true,
>   "logLevel": "ERROR",
>   "outputEveryPath": false,
>   "enablePluginLoader": true,
>   "SPL": {
>     "ImGuiRenderingEnabled": true,
>     "PrimitiveRenderingEnabled": true,
>     "MenuKey": "F10"
>   }
> }
> ```

### Camera Behavior & Specifications
* **Head-Lock Anchor**: Camera is anchored to Face Joint 45 (Nose). Head rotation is reconstructed from 4 face basis points (Nose, Throat, Right ear, Left ear) via `SimpleLock`.
* **Return-to-Center Look (Free-Look)**: Tilting the right stick allows you to look around freely. Releasing the stick holds the camera angle in world space until your character turns and catches up to that direction, smoothly restoring forward tracking.
* **L1 Recenter**: Tapping L1 (held for under 0.3s) eases both Yaw and Pitch back to forward level (Pitch resets to 0°). Long press opens the radial menu as normal.
* **Auto 3rd-Person Fallback**: While grappling with the Clutch Claw or riding a monster, the camera **automatically switches back to the native third-person view** to prevent severe motion sickness, and returns to FPS view once the motion ends.
* **Slinger Hiding**: The slinger is automatically hidden during certain aiming/action profiles to prevent it from obstructing your view.

---

### Profile System & Tuning Guide (How to tune other weapons)

Profiles in `NewCamera.json` are evaluated **top-to-bottom; the first matching profile is used**. If no profile matches, it defaults to **Mode 0 (Profile A)**.

#### 1. Camera Modes
* **Mode 0 (Profile A - Full Rotation)**:
  * Fully follows raw face rotation (YPR) without clamping. Used for rolls, cartwheels, and full-body spins.
* **Mode 1 (Profile B Ignore-X)**:
  * Rotation follows base direction. Ignores head-tracking shifts on the X-axis. Ideal for movement (WALK / RUN / DASH) where the head naturally sways or turns toward NPCs/items.
* **Mode 2 (Profile B - Base Only)**:
  * Both rotation and position stay locked to the base joint.
* **Mode 3 (Profile C - Clamped / Spotted Follow)**:
  * Ballet "spotting" follow. Locks forward within human neck ranges, but tracks large rotational attacks. Supports Gaze Keyframes for scripted camera angles during attack swings.

#### 2. Keyword Matching Rules (`Keywords`)
* **Action Name**: Matches full name (`WP_00::VSLASH`) or suffix (`VSLASH`).
* **Motion Key**: Matches exact `{Lmt}.{Id}` shown in the animation debug panel (e.g., `12.156`).
* **Strict Combined Match**: `ActionName@motionKey` (e.g., `WP_00::VSLASH@12.156`).
* **SubState Filter**: Matches `ActionController+0x760` state (e.g., `#8` or `Common::IDLE#8` to distinguish drinking potions or loading bow coatings while in idle).

#### 3. Editing Profiles (In-Game GUI)
Every field described in the Profile Parameter Reference below — Camera Mode, Keywords, Range/Blend, Target offsets, Gaze Keyframes, and everything else — can be edited directly from the SharpPluginLoader debug GUI, not just by hand-editing `NewCamera.json`. Day-to-day tuning is normally done this way:

1. Open the SPL GUI (**[F10]**, see the GUI key remap note above).
2. Under `DEBUG`, check `Simple Lock (position from Target Joint, rotation from below)` to reveal the Orbital Camera / Profile panel.
3. Scroll down to find the **`Save All Settings`** and **`Reload Saved Settings`** buttons, which write the panel's current state to `NewCamera.json` / read it back.

Editing the JSON file directly is only really needed for bulk changes, copying profiles between weapons, or version control.

#### 4. Profile Parameter Reference
These fields exist on every profile entry in `Profiles`. Fields marked **(Mode 3 / Normal only)** have no effect unless that profile's Camera Mode is set to Normal (Profile C).

* **Yaw/Pitch/Roll Range & Blend** *(Mode 3 / Normal only)*: `Range` is how far (in degrees) the head can turn before the camera starts to clamp it; `Blend` is the width of the gradual transition zone just outside `Range`, where the camera gradually stops following further rotation instead of snapping.
* **Correction Yaw/Pitch/Roll**: This profile's own baseline "forward" direction on top of the base joint's own direction. Use this to fix a fixed angular offset (e.g. a reticle sitting a few degrees off-center) that a position offset can't fix.
* **Target Y/Right/Forward**: This profile's own eye-position offset (up/right/forward from the anchor joint), independent of every other profile's own offset.
* **Position/Basis Joint Override** *(Mode 3 / Normal only, experimental)*: Lets this profile anchor the camera to a different joint than the mod's global setting, for motions where the default anchor doesn't track correctly.
* **Base Smoothing (Clamp Base Smoothing)**: Low-pass filter strength on the base direction used for clamping. 1.0 = instant/raw, lower = smoother.
* **Enable Transition Blend / Profile Switch Blend Time**: Whether switching into/out of this profile triggers a smooth Slerp blend, and how long it takes for this profile specifically.
* **Shake Suppress** *(Mode 3 / Normal only)*: Freezes the raw joint deviation for a set number of seconds starting the instant R2 is pressed/released. 0 = disabled.
* **Look Sensitivity Multiplier** *(Mode 3 / Normal only)*: 1.0 = the same right-stick response as ordinary free-look. Lets this profile's stick sensitivity be tuned independently of every other profile.
* **Use Native Aim Camera (+ Delay)** *(Mode 3 / Normal only)*: Uses the game's own native camera pose directly instead of this mod's joint-based reconstruction. The Delay setting (seconds) postpones the switch-over after L2 is pressed, in case the native camera itself needs a moment to catch up to the player's facing.
* **Disable Look While Aiming** *(Mode 3 / Normal only, applies while L2 is held)*: Suppresses this mod's own right-stick-driven camera tracking while aiming; the reticle still follows the stick normally through the base game's own aim response.
* **Hide Slinger While Active** *(Mode 3 / Normal only)*: Per-profile toggle — hides the slinger model only while this specific profile is active, not a blanket setting for every profile.
* **Disable Free Camera While Active** *(Mode 3 / Normal only)*: Per-profile toggle that temporarily forces the game's native third-person camera back on while this profile is active, then returns to FPS view once it's no longer matched. This is the mechanism behind the Clutch Claw/mounting auto-fallback described above — it can be applied to any other motion the same way.
* **Comment / WeaponGroup**: Free-text fields for your own organization in the GUI's profile list. They are saved to `NewCamera.json` but never used for matching — safe to write anything here.

#### 5. Advanced: Gaze Keyframes *(Mode 3 / Normal only)*
For attack motions where the raw per-frame joint deviation is too fast or noisy to track directly, a profile can instead use a scripted look curve:

* **Gaze Keyframes**: A list of `(T, Yaw, Pitch, Roll)` points, where `T` is motion progress from 0.0 (start) to 1.0 (end). The camera looks away from the base direction by the given Yaw/Pitch/Roll, Slerp-interpolated between keyframes. Enable with **Use Gaze Keyframes**, which ignores Range/Blend entirely while active.
* **Gaze Ranges**: Instead of scripting the whole motion, limits Gaze Keyframes to specific `T` windows (`Start`/`End`/`Blend`); outside those windows the profile falls back to ordinary Range/Blend follow. Enable with **Limit Gaze To Ranges**.
* **Use SubState Timer**: Switches how `T` is measured — from the motion's own frame progress to elapsed time in the current `ActionController` sub-state instead. Useful for sub-motions (e.g. drinking a potion, loading a bowgun coating) whose timing doesn't track the base motion's own progress. `Assumed Duration` sets the seconds used to normalize elapsed time back into a 0–1 `T`.
* **Gaze Position Keyframes**: The same `T`-based keyframe idea, but for eye position (`Y`/`Right`/`Forward`) instead of rotation — a separate curve from the rotation Gaze Keyframes above, enabled independently with **Use Gaze Position Keyframes (XYZ)**.

### Credits
* Original Mod: [NewCamera by Akon City Software](https://www.nexusmods.com/monsterhunterworld/mods/8300)
* Base Framework: SharpPluginLoader by Fexty

### Support / Donation
If you enjoy this mod, you can support me here:  
☕ [Ko-fi (Buy me a coffee)](https://ko-fi.com/closnow)

---

<a name="japanese"></a>
## 日本語

モンスターハンター：ワールド (MHW) を一人称視点（FPS視点）でプレイするためのMODです。  
既存のMOD「[NewCamera](https://www.nexusmods.com/monsterhunterworld/mods/8300)」をベースに、FPS視点向けに改造・コードの調整を行いました。

### ⚠️ 免責事項・注意事項
- **画面揺れ・3D酔い注意**: アクション中に画面が激しく揺れたり急回転します。酔いやすい方や発作の恐れがある方はご注意ください。
- **本MODは「現状渡し (As-is)」となります。個別の導入サポートや質問対応、要望受付、バグ修正などは一切行いません。**
- 本コードの改修の大部分は**AIの支援を受けて作成**しています。そのため、コードの最適化や予期せぬ不具合、他MODとの競合などの技術的な問題への対応は一切できません。
- プロファイル（カメラ視点）の調整は**ランスのみ**完了しています。大剣、スラッシュアックス、弓は調整途中のため不完全です。その他の武器種は未着手です。必要に応じてご自身で `NewCamera.json` を編集して調整してください（下記プロファイル仕様を参照）。
- キーボードでの操作は想定していません。**ゲームパッド（コントローラー）での操作を前提**としています。

### 必須MOD・環境
* [SharpPluginLoader (SPL) for MHW](https://github.com/Fexty12573/SharpPluginLoader)
* [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
* [Stracker's Loader](https://www.nexusmods.com/monsterhunterworld/mods/1982)

### 導入方法
ビルドした **`NewCamera.dll`** と **`NewCamera.json`** を、以下のフォルダに一緒に配置してください：
`nativePC/plugins/CSharp/NewCamera/`  
*(※要するに、`NewCamera.dll` と全く同じ場所に `NewCamera.json` を置けばOKです)*

### 推奨ゲーム内設定
* **GAME SETTING**
  * 頭装備の表示：表示しない
* **CONTROLS**
  * 方向指定タイプ：タイプ1
* **CAMERA**
  * 照準方向：プレイヤーの向き
  * 地形補正カメラの設定：補正しない
  * 空間補正カメラの設定：OFF
* **DISPLAY**
  * 画面の振動：振動なし

### 操作方法・ショートカット
> ⚠️ **GUI内の「Binds」メニューについて（重要）：**  
> ゲーム内のGUI上に「Binds」という設定項目が表示されていますが、誤作動防止のため**ショートカット機能はコード側で無効化**しています。GUI上でキー設定を変更しても機能しません。操作には以下の固定キーを使用してください。

* **[F7]**: MODカメラ (Enable Free Camera) の ON / OFF (※クエストボードや受付嬢の本はオブジェクト上にUIが表示されて遠くて見づらいため、利用時は一時的にF7でOFFにしてください)
* **[F9] (短押し)**: 0.1倍速スローモーションの ON / OFF
* **[F9] (400ms長押し)**: 10倍速早送り *(※解除はF9を2回押す：倍速 → 0.1倍 → 通常)*

> **SharpPluginLoaderのGUIキー変更のお願い:**  
> SharpPluginLoader (SPL) のデフォルトメニューキーは **[F9]** です。本MODの機能と被ってしまうため、ゲームのルートフォルダ直下にある `loader-config.json` を開き、以下のように `"SPL"` の設定を追記してメニューキーを **[F10]** に変更してください：
> ```json
> {
>   "logfile": false,
>   "logcmd": true,
>   "logLevel": "ERROR",
>   "outputEveryPath": false,
>   "enablePluginLoader": true,
>   "SPL": {
>     "ImGuiRenderingEnabled": true,
>     "PrimitiveRenderingEnabled": true,
>     "MenuKey": "F10"
>   }
> }
> ```

### カメラの主な挙動・仕様
* **頭部固定アンカー**: カメラ位置は顔ボーンの鼻（Face Joint 45）に固定。鼻・舌・左右の耳の4点から頭部回転（Face Basis）を計算しています。
* **フリールック（Return-to-Center）**: 右スティックを傾けると自由に周囲を見渡せます。スティックを離すとカメラはその場で維持され、キャラクターが旋回して正面が追いついた時点でスムーズに正面追従へ復帰します。
* **L1リセンター**: 0.3秒以下の短押しで正面へスムーズに戻ります（Pitchも0°の水平にリセット）。長押し時は通常通りアイテムホイールが開きます。
* **しがみつき・乗り時の自動三人称化**: クラッチクローでのしがみつき中やモンスターの乗り状態中は、激しい画面酔いを防ぐため**自動でゲーム本来の三人称視点へ戻り、アクション終了後に自動でFPS視点へ復帰**します。
* **スリンガー自動非表示**: 照準時などに腕のスリンガーが視界を塞がないよう、該当プロファイル中は自動でスリンガーモデルが非表示になります。

---

### プロファイルシステム仕様・調整ガイド（他武器の調整方法）

`NewCamera.json` のプロファイルは**上から順に照合され、最初に一致したものが適用**されます。一致するものがない場合は **Mode 0 (Profile A)** にフォールバックします。

#### 1. 各モード（Camera Mode）の役割
* **Mode 0 (Profile A - フル追従)**:
  * クランプ（可動制限）なし。頭部ボーンの回転に完全追従します。前転回避や激しい回転技向け。
* **Mode 1 (Profile B Ignore-X - X軸ブレ無視)**:
  * 回転は基準方向に固定し、歩行・走行中のヘッドトラッキングによる左右の揺れや、NPC/アイテム方向を向く首振りを無視します。移動モーション向け。
* **Mode 2 (Profile B - 基準完全固定)**:
  * 回転・位置ともに基準方向に完全依存します。
* **Mode 3 (Profile C - スポッティング追従)**:
  * バレエのスポッティング視点。首の可動範囲内は正面を維持し、大きく回転する攻撃時は追従します。攻撃モーション中の視点誘導（Gaze Keyframes）もここで設定します。

#### 2. キーワード（Keywords）の指定ルール
* **アクション名**: 完全一致（`WP_00::VSLASH`）または末尾（`VSLASH`）。
* **モーションキー**: アニメーション画面で確認できる `{Lmt}.{Id}` の完全一致（例: `12.156`）。
* **組み合わせ厳密マッチ**: `ActionName@motionKey`（例: `WP_00::VSLASH@12.156`）。
* **サブステート指定**: `ActionController+0x760` の値で絞り込み（例: `#8` や `Common::IDLE#8` ➔ 待機モーションのまま回復薬を飲んだりビンを装填した状態を判別）。

#### 3. プロファイルの編集（GUIから）
下記の「プロファイル パラメータ リファレンス」に載っている項目——Camera Mode、Keywords、Range/Blend、Target位置オフセット、Gaze Keyframesなど——は、`NewCamera.json` を直接手で編集しなくても、SharpPluginLoaderのデバッグGUIから直接編集できます。通常のチューニングは以下の手順で行います。

1. SPLのGUIを開きます（**[F10]**、上記のGUIキー変更を参照）。
2. `DEBUG` メニュー内の `Simple Lock(position from Target Joint, rotation from below)` にチェックを入れると、Orbital Camera / Profile パネルが表示されます。
3. 下部にある **`Save All Settings`** / **`Reload Saved Settings`** ボタンで、パネルの現在の状態を `NewCamera.json` に書き出す/読み込みます。

JSONファイルを直接編集する必要があるのは、一括変更や、武器間でのプロファイルのコピー、バージョン管理を行う場合くらいです。

#### 4. プロファイル パラメータ リファレンス
以下は `Profiles` の各エントリに存在する項目です。**(Mode 3 / Normal 専用)** と記載した項目は、そのプロファイルの Camera Mode が Normal (Profile C) 以外の場合は効果を持ちません。

* **Yaw/Pitch/Roll の Range・Blend** *(Mode 3 / Normal 専用)*：`Range` は頭がどこまで（度数で）回転してもカメラがクランプを始めない範囲、`Blend` は `Range` のすぐ外側にある、急に止めるのではなく徐々に追従を弱めていく緩衝帯の幅です。
* **Correction Yaw/Pitch/Roll**：基準ジョイントの向きに上乗せする、そのプロファイル固有の「正面」の補正値。位置オフセットでは直せない、一定角度のズレ（照準が数度ずれて見える等）を直すためのものです。
* **Target Y/Right/Forward**：そのプロファイル固有の視点位置オフセット（アンカーとなるジョイントからの上/右/前方向）。他のどのプロファイルの値からも独立しています。
* **Position/Basis Joint Override** *(Mode 3 / Normal 専用、実験的機能)*：このプロファイルだけ、MOD全体のデフォルトとは別のジョイントをカメラの基準にできる機能。デフォルトのアンカーがうまく追従しないモーション向けです。
* **Base Smoothing (Clamp Base Smoothing)**：クランプの基準方向にかけるローパスフィルタの強さ。1.0で生の値そのまま、値を下げるほど滑らかになります。
* **Enable Transition Blend / Profile Switch Blend Time**：このプロファイルへの切り替わり/切り替え後に、滑らかなスラープブレンドを行うかどうかと、その所要時間（このプロファイル固有の値）。
* **Shake Suppress** *(Mode 3 / Normal 専用)*：R2を押した/離した瞬間から指定秒数、関節の生の変位をフリーズします。0で無効。
* **Look Sensitivity Multiplier** *(Mode 3 / Normal 専用)*：1.0で通常のフリールックと同じ右スティック感度になります。このプロファイル中だけ、他のプロファイルとは独立してスティック感度を調整できます。
* **Use Native Aim Camera (+ Delay)** *(Mode 3 / Normal 専用)*：本MOD独自のジョイントベースの再構築ではなく、ゲーム本来のカメラ姿勢をそのまま使います。Delay（秒）は、L2を押してから切り替わるまでの遅延で、ゲーム側のカメラ自体がプレイヤーの向きに追いつくまで少し待たせたい場合に使います。
* **Disable Look While Aiming** *(Mode 3 / Normal 専用、L2を押している間のみ有効)*：照準中、本MOD側の右スティック追従を止めます。照準自体はゲーム本来の挙動どおりスティックに追従し続けます。
* **Hide Slinger While Active** *(Mode 3 / Normal 専用)*：プロファイル単位のトグルです。そのプロファイルが有効な間だけスリンガーを非表示にするもので、全プロファイル共通の自動仕様ではありません。
* **Disable Free Camera While Active** *(Mode 3 / Normal 専用)*：このプロファイルが有効な間だけ、一時的にゲーム本来の三人称視点に戻し、条件から外れたらFPS視点に復帰させるプロファイル単位のトグルです。上記のクラッチクロー・搭乗時の自動三人称化は、この仕組みを使って実現されています。同じ方法で他の好きなモーションにも適用できます。
* **Comment / WeaponGroup**：GUIのプロファイル一覧を整理するための自由記述欄です。`NewCamera.json` には保存されますが、マッチング処理には一切使われません。自由に書き換えて問題ありません。

#### 5. 応用：Gaze Keyframes *(Mode 3 / Normal 専用)*
1フレームごとの生の関節変位を追うには速すぎたり、ノイズが多すぎたりする攻撃モーション向けに、プロファイルはスクリプト化された視点カーブを使うこともできます。

* **Gaze Keyframes**：`(T, Yaw, Pitch, Roll)` のリスト。`T` はモーションの進行度（0.0=開始 〜 1.0=終了）です。基準方向から指定したYaw/Pitch/Rollだけ視点をずらし、キーフレーム間をSlerpで補間します。**Use Gaze Keyframes** を有効にすると、有効な間はRange/Blendを完全に無視します。
* **Gaze Ranges**：モーション全体をスクリプト化する代わりに、Gaze Keyframesを特定の `T` 区間（`Start`/`End`/`Blend`）だけに限定できます。その区間の外では通常のRange/Blend追従に戻ります。**Limit Gaze To Ranges** で有効化します。
* **Use SubState Timer**：`T` の測り方を、モーション自体のフレーム進行度から、現在の `ActionController` サブステートの経過時間に切り替えます。回復薬を飲む、瓶を装填するなど、本体モーションの進行度とタイミングが一致しないサブモーション向けです。`Assumed Duration` は、経過時間を0〜1の `T` に正規化する際の想定秒数です。
* **Gaze Position Keyframes**：上記の回転用Gaze Keyframesと同じ考え方を、視点位置（`Y`/`Right`/`Forward`）に適用したものです。回転側とは独立したカーブとして、**Use Gaze Position Keyframes (XYZ)** で個別に有効化します。

### クレジット
* Original Mod: [NewCamera by Akon City Software](https://www.nexusmods.com/monsterhunterworld/mods/8300)
* Base Framework: SharpPluginLoader by Fexty

### 寄付・サポート
もしこのMODを気に入っていただけましたら、こちらからサポートしていただけると励みになります：  
☕ [Ko-fi (Buy me a coffee)](https://ko-fi.com/closnow)
