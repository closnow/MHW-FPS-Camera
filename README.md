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
- Camera profiles are **only fully tuned for Lance**. Great Sword, Switch Axe, and Bow are partially tuned (WIP/incomplete). All other weapon types are untouched. Please edit `NewCamera.json` yourself to adjust other weapons (see Profile System below).
- Keyboard/mouse controls are not supported. **Gamepad (controller) is required.**

### Requirements
- [SharpPluginLoader (SPL) for MHW](https://github.com/Fexty12573/SharpPluginLoader)  

### Installation
Place both **`NewCamera.dll`** (built) and **`NewCamera.json`** into the following folder:
`nativePC/plugins/CSharp/NewCamera/`  
*(In short, place `NewCamera.json` in the same directory as `NewCamera.dll`)*

### Recommended In-Game Settings
* **GAME SETTINGS**
  * Head Armor: Do not show
* **CONTROLS**
  * Directional Control: Type 1
* **CAMERA**
  * Reticle Direction: Player Direction
  * Terrain Camera: Off (Do not adjust)
  * Dynamic Camera: OFF
* **DISPLAY**
  * Screen Vibration: None

### Controls & Hotkeys
> ⚠️ **Important Note on the GUI "Binds" Menu:**  
> The "Binds" section is still visible in the in-game GUI, but **all shortcut features there have been deprecated/disabled** in the code to prevent misfires. Changing bindings in the GUI will have no effect. Please use the fixed hotkeys below instead:

* **[F7]**: Toggle Free Camera ON / OFF (Use this to temporarily disable the mod camera when reading the Quest Board or Handler's book, as the UI is too far to read in first-person).
* **[F9] (Short Press)**: Toggle 0.1x Slow Motion ON / OFF
* **[F9] (Hold 400ms)**: 10x Fast Forward *(Press F9 twice to reset back to normal speed: Fast -> 0.1x -> 1.0x).*

> **Note on SharpPluginLoader GUI key:**  
> The default GUI key for SharpPluginLoader (SPL) is **[F9]**, which conflicts with this mod's slow-motion key. Please open `nativePC/plugins/CSharp/loader-config.json` in a text editor and change the SPL menu key to **[F10]** or another preferred key.

### Camera Behavior & Specifications
* **Head-Lock Anchor**: Camera is anchored to Face Joint 45 (Nose). Head rotation is reconstructed from 4 face basis points (Nose, Throat center, Right ear, Left ear) via `SimpleLock`.
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

#### 3. How to Save & Load Profiles
1. Open the SPL GUI.
2. Under `DEBUG`, check `Simple Lock(position from Target Joint, rotation from below)`.
3. Scroll down to find the **`Save All Settings`** and **`Reload Saved Settings`** buttons.
4. Click them to save/reload your edits to `NewCamera.json`.

### Credits
* Original Mod: [NewCamera by Akon City Software](https://www.nexusmods.com/monsterhunterworld/mods/8300)
* Base Framework: SharpPluginLoader by Fexty

---

<a name="japanese"></a>
## 日本語

モンスターハンター：ワールド (MHW) を一人称視点（FPS視点）でプレイするためのMODです。  
既存のMOD「[NewCamera](https://www.nexusmods.com/monsterhunterworld/mods/8300)」をベースに、FPS視点向けに改造・コードの調整を行いました。

### ⚠️ 免責事項・注意事項
- **画面揺れ・3D酔い注意**: アクション中に画面が激しく揺れたり急回転します。酔いやすい方や発作の恐れがある方はご注意ください。
- **本MODは「現状渡し (As-is)」となります。カスタマーサポート、要望受付、バグ修正などは一切行いません。**
- 本コードの改修の大部分は**AIの支援を受けて作成**しています。そのため、コードの最適化や予期せぬ不具合、他MODとの競合などの技術的な問題への対応は一切できません。
- プロファイル（カメラ視点）の調整は**ランスのみ**完了しています。大剣、スラッシュアックス、弓は調整途中のため不完全です。その他の武器種は未着手です。必要に応じてご自身で `NewCamera.json` を編集して調整してください（下記プロファイル仕様を参照）。
- キーボードでの操作は想定していません。**ゲームパッド（コントローラー）での操作を前提**としています。

### 必須MOD
- [SharpPluginLoader (SPL) for MHW](https://github.com/Fexty12573/SharpPluginLoader)  

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
> ゲーム内のGUI上に「Binds」という設定項目が表示されていますが、誤作動防止のため**ショートカット機能はコード側で廃止（無効化）**しています。GUI上でキー設定を変更しても機能しません。操作には以下の固定キーを使用してください。

* **[F7]**: MODカメラ (Enable Free Camera) の ON / OFF (※クエストボードや受付嬢の本はオブジェクト上にUIが表示されて遠くて見づらいため、利用時は一時的にF7でOFFにしてください)
* **[F9] (短押し)**: 0.1倍速スローモーションの ON / OFF
* **[F9] (400ms長押し)**: 10倍速早送り *(※解除はF9を2回押す：倍速 → 0.1倍 → 通常)*

> **SharpPluginLoaderのGUIキー変更のお願い:**  
> SharpPluginLoader (SPL) のデフォルトメニューキーは **[F9]** です。本MODの機能と被ってしまうため、`nativePC/plugins/CSharp/loader-config.json` を開き、SPLのメニューキーを **[F10]** 等に変更してご使用ください。

### カメラの主な挙動・仕様
* **頭部固定アンカー**: カメラ位置は顔ボーンの鼻（Face Joint 45）に固定。鼻・舌根・左右の耳の4点から頭部回転（Face Basis）を計算しています。
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

#### 3. プロファイルのセーブ＆ロード
1. SPLのGUIを開きます。
2. `DEBUG` メニュー内の `Simple Lock(position from Target Joint, rotation from below)` にチェックを入れます。
3. 下部に出る **`Save All Settings`** / **`Reload Saved Setting`** ボタンで `NewCamera.json` のセーブ・ロードが行えます。

### クレジット
* Original Mod: [NewCamera by Akon City Software](https://www.nexusmods.com/monsterhunterworld/mods/8300)
* Base Framework: SharpPluginLoader by Fexty
