# Accuracy

VR Motion Recorder cannot read VRChat's body pose in an ordinary world, so it solves the body again from the same headset, controller and tracker data. This page shows how close that solve comes to VRChat's own IK 2.0, measured on takes that hold both the raw devices and VRChat's bones.

- **11-point tracking, standing: 1.66 cm** mean distance per bone. **6-point** (head, hands, hips, feet): **2.06 cm**.
- **Three body trackers** (hips and feet): **0.70–1.55 cm** on the four reference takes, 0.77–2.75 cm on the four stretches of a take with several calibrations.
- **Lying on the floor** (11-point): **5.17 cm**.
- Everything here was measured on **one setup and one body**. Read the numbers as a careful single-rig result, not as a promise for yours. Measurements from other rigs are very welcome (see the end of this page).

## What is compared

VRChat exposes its IK result only to scripts that run inside a world. A world with a HUMR recorder (Hyakuashi Udon Motion Recorder) writes every humanoid bone to VRChat's log 30 times a second. With the app recording at the same time, one take holds VRChat's bones and the raw SteamVR devices they were computed from. The app's solve of those raw devices is then compared with VRChat's bones.

The number used throughout is the **mean distance between the same bone in both poses**, in avatar centimetres (VRChat's world units at the avatar's own size). Only still frames after VRChat's calibration confirm are scored: HUMR frames carry VRChat's clock, and on fast motion a small timing difference would count as distance. Rotations are given in degrees where they say more than positions.

How the solve works is described in [Architecture](architecture.md); the settings named here are explained in [calibration.md](calibration.md).

## The setup and the takes

One person, one test avatar (a second one for the scaling take), a PC VR headset, two Index controllers and lighthouse body trackers. The hand constants are specific to Index controllers; nothing else depends on the model of a device.

| Name                       | Length | Body trackers                    | What it contains                                                                                                       |
| -------------------------- | ------ | -------------------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| Reference take R1          | –      | hips, feet                       | Lock Hip, avatar measured by height                                                                                    |
| Reference take R2          | –      | hips, feet                       | Lock Head, by height                                                                                                   |
| Reference take R3          | –      | hips, feet                       | Lock Both, by height                                                                                                   |
| Reference take R4          | –      | hips, feet                       | Lock Both, by arm span                                                                                                 |
| The multi-calibration take | 96 s   | hips, feet                       | two calibrations, a measurement change, a spine mode change, thumbstick moves, a playspace mover and a playspace reset |
| The 11-point take          | 148 s  | hips, chest, elbows, knees, feet | standing, crouching, 20 s lying on the floor, a jump, all three spine modes, two calibrations; scored in windows A–G   |
| The scaling take           | 167 s  | hips, chest, feet                | an edited copy of a base model, four calibrations, four height changes in VRChat                                       |
| The binding take           | –      | hips, feet                       | no VRChat reference; used for how far bones drift from their devices                                                   |

### Fitting VRChat's world to SteamVR's

VRChat's world is not SteamVR's playspace: it differs by the spawn point, every thumbstick move and turn, and the tracking scale. For each take the measurement harness fits `VRChat world = s · yaw · SteamVR + T`:

- with a separate yaw and T for each stretch between thumbstick moves;
- anchored on the hands and feet, which VRChat keeps on their devices in every mode;
- with robust weights, on still frames after VRChat's confirm.

Then it reads off each bone in its device's frame.

### VRChat's settings

VRChat keeps its IK settings in the registry under `HKCU\Software\VRChat\VRChat`. The app reads them and never writes them:

- `VRC_IK_FBT_SPINE_MODE`: 0 = Lock Hip, 1 = Lock Head, 2 = Lock Both;
- `VRC_IK_AVATAR_MEASUREMENT_TYPE`: 0 = Arm Span, 1 = Height;
- `PlayerHeight`: the User Real Height, in metres;
- `VRC_IK_HEIGHT_RATIO` = 0.4537, the arm ratio of "by arm span".

Changing the measurement type reloads the avatar; changing the spine mode does not.

## What VRChat does with the devices

| Take | Spine / measurement     | Eye height VRChat uses            | Head vs headset, p95 | Hips vs tracker, p95                      | Feet vs trackers              |
| ---- | ----------------------- | --------------------------------- | -------------------- | ----------------------------------------- | ----------------------------- |
| R1   | Lock Hip / by height    | 0.940 × User Real Height          | 9.3 cm (free)        | 0.9 cm (locked)                           | lift up to 2.7 / 1.6 cm (p95) |
| R2   | Lock Head / by height   | 0.941 × User Real Height          | 0.8 cm (locked)      | 3.0 cm: the hips come down (up to 3.0 cm) | planted (1.0 cm)              |
| R3   | Lock Both / by height   | 0.940 × User Real Height          | 2.6 cm               | 1.2 cm                                    | lift up to 1.6 / 1.3 cm       |
| R4   | Lock Both / by arm span | – (scale from the arm, see below) | 2.3 cm               | 1.0 cm                                    | lift up to 0.6 / 1.1 cm       |

- **Scale by height.** The tracking scale is the same within ±0.3 % over the three takes, which means VRChat's eye height is 0.940 × the User Real Height. Fits on different anchors agree: hips + feet against hands + feet within 0.1 % (R1), each hand alone +0.1 % and +0.3 % (R2). The head alone gives a value 1 % larger, but it moves too little to pin the scale.
- **Scale by arm span.** VRChat's formula is: the avatar's head bone to wrist, arm straight out in the T-pose, divided by 0.4537, is mapped onto the eye height. The scale fitted on R4 is +0.0 … +1.2 % of what the formula predicts. An eye-to-wrist reach would be +2.1 % and a wrist-to-wrist span −0.7 %. In this mode the arms do not reach fully in VRChat either.
- **Hands.** VRChat's hand bone sits at the same place in the controller's frame in all four takes (rotation within 0.5°): (∓2.1, 2.2, −10.3) avatar cm, at Euler (−5.5, ∓81.3, ∓132.3) relative to the controller on the test avatar, mirror-symmetric. The app stores it relative to the hand's T-pose, so it does not depend on the avatar. The offset follows the avatar's size, not the tracking scale: 10.6–10.9 avatar cm in both measurement modes, although their tracking scales differ by about 5 %.
- **Out of reach,** VRChat leaves the hand short and does not stretch the arm. With the target 1.7 / 3.2 cm further away than a straight arm, the hand stays 2.0 / 2.7 cm short and the shoulder joint moves 0.5 / 0.8 cm. The app does the same.
- **Head.** The head bone sits 4.1 cm below and 4.0 cm behind the headset (avatar cm), rigidly in Lock Head. VRChat logs this per avatar as `eyeToNeck:(0.00, 0.04, 0.04)`. The test avatar's view position is 4.1 cm in front of its eye bones, but VRChat's IK puts the head bone only halfway: the effective eye point is 1.94 cm in front of the eye bones. The app uses that measured point.
- **Calibration.** VRChat's feet stay 2 cm below the floor by height and 9.7 cm below by arm span (legs straight, hips 9.6 cm low). That is the avatar's eye height minus scale × headset height at the confirm: the avatar hangs from the headset.
- **Calibration mode.** From the "Calibrate FBT" click until both triggers confirm, VRChat holds the avatar in its calibration pose. For those 3–6 s the hands are 30–60 cm and up to 77° off the controllers.

## The solve against VRChat with three body trackers

Mean distance of the same bone, avatar cm, still frames after the confirm. The solve is calibrated at VRChat's confirm moment and uses the spine mode VRChat used.

| Take                   | Starting point | + VRChat's offsets, scale and hang | + knee plane | + spine and arm (now) | Knee position, worst side, mean / p95, before → after the knee plane |
| ---------------------- | -------------- | ---------------------------------- | ------------ | --------------------- | -------------------------------------------------------------------- |
| R1 Lock Hip            | 3.92 cm        | 2.08 cm                            | 1.53 cm      | **1.18 cm**           | 9.1 / 37.2 → 1.7 / 3.0 cm                                            |
| R2 Lock Head           | 3.17 cm        | 1.42 cm                            | 1.14 cm      | **0.70 cm**           | 4.4 / 40.8 → 1.1 / 1.7 cm                                            |
| R3 Lock Both           | 4.54 cm        | 2.61 cm                            | 1.79 cm      | **1.55 cm**           | 8.8 / 43.0 → 1.8 / 2.1 cm                                            |
| R4 Lock Both, arm span | 7.19 cm        | 1.84 cm                            | 1.80 cm      | **1.37 cm**           | 3.5 / 9.9 → 3.0 / 5.2 cm                                             |

The starting point is the base solver with the app's first calibration scheme. The middle columns come from the same harness as the last one; its segmenting was later made stricter for the 11-point take, which moves R1 by 0.01–0.02 cm.

The changes behind the columns, all on by default:

- VRChat's hand and head offsets;
- the 0.94 eye-height ratio;
- VRChat's arm-span formula;
- hanging the avatar from the headset at calibration (the feet were 1.9 cm too high before; 9.5 cm by arm span);
- the feet and hips behaviour of each spine mode (see [calibration.md](calibration.md));
- the knee plane (below);
- the spine for a tracked hip (below);
- the arm for arms without elbow trackers (below).

### Knees

The knee direction that fits VRChat is `cross(hip→foot, n)`, where `n` is the pelvis' right axis turned toward the foot target's right axis by a weight. The base solver instead pushed a knee hint along the hips' forward axis, projected off the hip→foot line. In a deep squat the torso leans so far that this projection is close to zero, so the knee direction became noise and the knees caved in.

- VRChat matches weight **1**: the knee plane follows the foot target fully. Knee position, mean: 1.1–3.0 cm at weight 1, 1.7–3.5 cm at 0.5, 2.0–5.8 cm at 0.25.
- In a squat in R2 (12.9–13.1 s, knee at 42°), VRChat's right knee sits (14.4, 18.0, 11.7) cm off the hip–foot line (hips' yaw frame: out, up, forward). The solve had (−19.4, −4.9, 16.6) and now has (15.2, 17.2, 12.2).
- It applies to legs without a knee tracker. A thigh twist correction was tried as well and changed nothing measurable, so it is not used.

Code: the knee-plane block in `src/Vrmr.Fbik/Limbs/IkLegSolveCore.cs`.

### Spine and chest

With a hip tracker the spine follows the model below instead of the base solver's soft spine. (With a chest tracker as well, see the 11-point take.)

How it was found: the harness dumps every still frame (VRChat's bones, the solve's, the devices). A candidate spine model was then driven with VRChat's **own** hip pose, headset-derived head target and hand positions, so that the only difference left is the spine model. The base solver put most of the bend into the lowest spine segment and rolled it 3–9° sideways (spine bone up to 24° off VRChat), with the chest 2.9–3.6 cm off. The model below got the chest to 0.1–1.4 cm.

Per frame:

1. **Start pose:** the rest (T-pose) spine, hung from the hip target. VRChat's standing animation (`proxy_stand_still`) has spine muscles ≈ 0, which is the T-pose spine. Feeding back the last frame's result instead drifted the rotations by 10–40°, so VRChat evidently starts from a fresh pose every frame.
2. **Chest target** = head target × (chest relative to the head in the start pose), turned by the hands at full weight.
3. **Spine passes:**
   - The body follows the head with stiffness 0.55 and moves back when the head goes down (factor 0.5).
   - One FABRIK iteration, its start blended 0.2 toward the start pose's pelvis, the target pushed out to at least the spine's length.
   - Spine and chest bend toward the chest target (the neck resists with stiffness 0.2, the chest's bend is clamped at weight 0.5); the chain is moved to the head; a second FABRIK iteration.
   - Neck and head bend toward the head target (half each, the head's bend clamped at weight 0.6).
4. **Pelvis:** the body goes back onto the hip target, then a triangle (chest, neck, head) aims at the head target.

Three changes to that model were measured and kept:

- **The pelvis keeps the tracker's rotation.** The FABRIK pass alone would swing the pelvis bone 5–20° toward the spine; VRChat's hips stay within about 1° of the tracker.
- **Only the chest–neck–head triangle.** An earlier triangle over spine, chest and head (weight 0.9) is left out: without it the chest is 1.23 → 0.67 cm off VRChat (mean of the 7 windows, model driven by VRChat's targets).
- **The triangle's head target is pushed out to the spine's current length** when the headset comes closer than that. This is VRChat's "head free" in Lock Hip. In a deep bend (R1, 35.8–36.0 s) VRChat's head stays 9 cm above the headset point instead of the spine folding; in a normal stance it stays 1–2 cm above when the headset is 1–2 cm closer than the spine's length. With it the neck is 3.1 → 1.3 cm off on the Lock Hip windows.

The model's other constants (the neck stiffness, the body-follow stiffness, the pelvis blend, the chest turn by the hands, the move back when crouching, the neck/head split, the head clamp) were each varied. None gave a consistent gain (the best single change, 0.04 cm, was mixed across takes), so they stay at their starting values.

The spine modes are how far the head is followed after the spine:

- Lock Hip, 0: the hips stay on the tracker, the head is free;
- Lock Head, 1: the whole body moves until the head is on the headset;
- Lock Both, 0.25.

Code: `src/Vrmr.Fbik/Spine/VrcSpine.cs`.

### Shoulders and elbows

Arms without an elbow or shoulder tracker use the arm model below instead of the base solver's arm and shoulder solve. Driven by VRChat's own chest and hands, it puts the shoulder joint within 0.4–0.65 cm of VRChat's (relative to the chest; base solver 0.4–1.0 cm) and the elbow within 1.7 cm before any fitting.

- **Shoulder:** the clavicle turns toward the hand target by a damped yaw (offset 45°, limits −20…50° and −50…20°) and pitch (offset −30°, limits ±45°). The clavicle and upper arm twist when the hand is raised.
- **Elbow:** a triangle (upper arm, forearm) to the hand target, in a bend plane. The bend direction is built from the chest frame and the shoulder→target direction, plus the clavicle direction, minus the hand's wrist→palm and palm→thumb axes (guessed from the hand bone and its closest child). It is then turned by a swivel offset around the shoulder→hand line.
  - Fitted to VRChat (elbow *position* given VRChat's chest and hands, a grid over the three constants): **swivel offset 2°** (mirrored for the right arm), **wrist→palm at half weight**, palm→thumb at full weight.
  - Elbow 1.67 → 0.59 cm, swivel 5.6° → 2.3° (base solver: 2–13°, depending on the take).
- **Forearm twist:** in the plain model the forearm follows the upper arm only. VRChat's forearm takes **0.55** of the wrist's twist (0.44–0.76 per take, 0.51–0.61 on the takes with wide wrist turns), as a twist relaxer would. That share is added: forearm rotation error 4–11° → 2–7°.
- **Out of reach,** the arm points at the target and the hand stays short (no stretch), as in VRChat.

Folded arms: with the hand near the shoulder, VRChat's elbow flips sides quickly (the multi-calibration take, 51.44–51.59 s: 170° of swivel in 0.15 s). This arm follows it. The base solver's flip stabilisation held the elbow on the other side, 15–21 cm off VRChat.

Arms with an elbow tracker use the same arm with the tracker as a goal for the bend direction (see the 11-point take). A shoulder tracker keeps the base solver's solve.

Code: `src/Vrmr.Fbik/Limbs/VrcArm.cs`.

### Per bone, before and after the spine and arm

Mean distance to VRChat, avatar cm, still frames. "Before" is the build that already had the knee plane. Bold = better. "MC" is the multi-calibration take.

| Window                               | All bones       | Chest           | Shoulders L / R                   | Elbows L / R                      | Neck            | Head            |
| ------------------------------------ | --------------- | --------------- | --------------------------------- | --------------------------------- | --------------- | --------------- |
| R1 Lock Hip                          | 1.53 → **1.18** | 3.16 → **1.75** | 2.10 → **1.65** / 2.73 → **1.66** | 1.64 → **1.50** / 2.67 → **1.28** | 2.23 → **1.37** | 2.01 → **1.30** |
| R2 Lock Head                         | 1.14 → **0.70** | 1.60 → **0.71** | 1.34 → **1.31** / 1.44 → **0.71** | 1.71 → **0.88** / 1.64 → **0.62** | 0.96 → 0.98     | 0.34 → 0.34     |
| R3 Lock Both                         | 1.79 → **1.55** | 1.95 → 1.96     | 2.30 → **1.79** / 1.96 → **1.41** | 3.41 → **2.85** / 3.15 → **1.53** | 1.61 → 1.82     | 1.58 → **1.51** |
| R4 Lock Both, arm span               | 1.80 → **1.37** | 2.24 → **1.11** | 2.53 → **1.44** / 2.55 → **1.11** | 2.15 → **1.59** / 2.77 → **1.17** | 2.21 → **1.50** | 1.43 → **1.22** |
| MC 10.8–13.9 s (Lock Both, arm span) | 2.65 → **2.44** | 2.70 → **2.40** | 3.60 → **1.94** / 2.83 → **1.57** | 1.49 → 1.94 / 2.45 → **1.53**     | 2.83 → **2.16** | 0.46 → 1.56     |
| MC 16.1–63.7 s (Lock Hip)            | 1.80 → **1.21** | 3.57 → **1.14** | 2.65 → **1.50** / 2.64 → **1.68** | 2.77 → **1.32** / 2.56 → **1.68** | 3.03 → **1.49** | 2.76 → **1.74** |
| MC 70.6–96 s (Lock Hip)              | 0.80 → **0.56** | 2.89 → **0.87** | 0.49 → 0.58 / 1.97 → **0.54**     | 0.86 → **0.76** / 1.43 → **0.57** | 0.56 → 0.77     | 0.52 → 0.55     |

- Hips, legs and hands are unchanged or better in every window (R2: hips 1.36 → 0.64, knees 1.26 / 1.65 → 1.07 / 0.80).
- Worse:
  - the neck in R3 and in MC 70.6–96 s (+0.2 cm);
  - the left elbow and the head in MC 10.8–13.9 s (the calibration there puts the hips too high, see "What is left");
  - the left shoulder in MC 70.6–96 s (+0.09 cm);
  - the chest's p95 in R3: 2.3 → 4.7 cm (the forward lean, see "What is left").
- The last two MC windows moved a little after this table was made: the stricter segmenting gives 1.23 cm for 16.1–63.7 s, and the later change of the confirm moment gives 0.77 cm for 70.6–96 s (see "The calibration moment").

Bone rotations, mean difference to VRChat, before → now:

| Window                 | Spine    | Chest    | Neck      | Upper arm L / R       | Forearm L / R       |
| ---------------------- | -------- | -------- | --------- | --------------------- | ------------------- |
| R1 Lock Hip            | 17° → 6° | 9° → 6°  | 10° → 11° | 13° → 7° / 14° → 7°   | 8° → 4° / 9° → 4°   |
| R2 Lock Head           | 5° → 5°  | 3° → 6°  | 9° → 9°   | 11° → 11° / 10° → 5°  | 8° → 3° / 6° → 2°   |
| R3 Lock Both           | 7° → 8°  | 4° → 6°  | 12° → 8°  | 13° → 17° / 12° → 4°  | 11° → 7° / 7° → 3°  |
| R4 Lock Both, arm span | 14° → 3° | 1° → 5°  | 17° → 8°  | 12° → 10° / 11° → 5°  | 7° → 6° / 9° → 5°   |
| MC 10.8–13.9 s         | 24° → 4° | 1° → 6°  | 28° → 14° | 10° → 2° / 6° → 3°    | 6° → 6° / 8° → 6°   |
| MC 16.1–63.7 s         | 23° → 4° | 12° → 8° | 15° → 10° | 13° → 13° / 12° → 10° | 10° → 5° / 10° → 6° |
| MC 70.6–96 s           | 17° → 4° | 11° → 6° | 6° → 12°  | 14° → 12° / 12° → 2°  | 5° → 3° / 4° → 2°   |

The spine bone's 17–24° error was not an axis convention: it was the base solver's bend distribution.

Other parts that were compared:

- Procedural stepping only drives feet without trackers, which VRChat does not use with foot trackers.
- Not used: a spine curvature added to the start pose. The best fit (4° / 4° / −8° on spine / chest / neck) gave 0.875 → 0.810 cm, but VRChat's standing animation does not have it.

### Device binding and joint speeds

How far each bone drifts from its device on the binding take (p95, before → after the spine and arm):

- Hands and feet: unchanged.
- The slack of the head and hips moves to VRChat's measured values:
  - Lock Hip: head 2.1 → 3.0 cm (VRChat 9.3 cm, head free);
  - Lock Head: hips 2.3 → 2.9 cm (VRChat 3.0 cm);
  - Lock Both: head 1.2 → 2.2 cm, hips 1.0 → 0.7 cm (VRChat 2.3–2.6 cm and 1.0–1.2 cm).

The same take in Lock Head, before and after VRChat's offsets, the hang from the headset and the per-mode behaviour of feet and hips (avatar cm):

|                                                              | Before          | After                                               |
| ------------------------------------------------------------ | --------------- | --------------------------------------------------- |
| Head bone in the headset's frame                             | (0, −4.1, −2.1) | (0, −4.1, −4.0), VRChat's                           |
| Hand bone against the controller, p95 drift                  | 0.8 / 2.0 cm    | 0.2 / 0.6 cm                                        |
| Frames with the right hand more than 1 cm off the controller | 27 %            | 1.9 %                                               |
| Standing feet against their rest height                      | −0.2 cm         | −2.5 cm (hanging from the headset; VRChat: −2.0 cm) |
| Lock Hip: drift of the hips                                  | 1.0 cm          | 0.0 cm (the feet lift, p95 1.2 / 1.6 cm)            |

Joint speeds on the multi-calibration take did not change with the spine and arm: p50 0.75 m/s, p99 2.31 m/s. Only the playspace reset at 49.15 s is fast. A 3-point solve (no trackers) did not change either.

### What is left

- The chest's *rotation*: 5–8° off VRChat in every mode. (It was 1–4° in Lock Head and Lock Both and 9–12° in Lock Hip, with the chest's *position* 1.6–3.6 cm off.) The same spine model driven by VRChat's own hip, head and hand targets is 2–8° off, so the difference lies in the model, not in the inputs.
- A strong forward lean in Lock Both: in R3 (9.0–9.6 s) VRChat bends the lower spine about 38° and puts the chest 4–5 cm further forward than the solve does. The base solver's spine was closer there.
- Thigh rotations: 10–18° of twist, mostly constant. It is an axis convention difference; the positions agree.
- The confirm moment: the full press fits the 11-point take (1 cm per bone better), but the start of the pull fitted the multi-calibration take's second calibration 0.2 cm better. New takes record the analog trigger values to settle it.
- The extra trackers: the left elbow 2.9–5.5 cm, the chest's rotation 3.5–9.8°, knee twist up to 19° when crouching; lying on the floor 5.2 cm per bone.
- The arm-span scale: VRChat's own fits are −0.5 … +1.9 % of the formula's value across the takes. Also, the arm-span calibration of the multi-calibration take (10.8–13.9 s) puts the solve's hips 2.4 cm above VRChat's, so the spine there is compressed and the head stays 1.7 cm above the headset point (head 0.46 → 1.56 cm in that stretch).

## Extra trackers: the 11-point take

148 s with every point VRChat supports: the headset, two controllers and 8 lighthouse trackers on hips, chest, elbows, knees and feet. It contains standing, crouching, about 20 s lying on the floor (with tracking losses), a jump, Lock Hip, then Lock Head (from 96.5 s) and Lock Both (from 101.5 s), and two VRChat calibrations (confirmed at 24.0 s and 115.9 s).

How it was scored: one raw → VRChat mapping for 24–146 s, fitted on the hands and feet, the feet with separate offsets before and after the second calibration, lying and calibration mode left out. Its scale is within 0.5 % of the reference takes' and its rms is 1.24 cm. Then per window:

| Window | Time          | What                                                              |
| ------ | ------------- | ----------------------------------------------------------------- |
| A      | 1.4–20.2 s    | Lock Hip, VRChat's calibration from before the take (its own fit) |
| B      | 24.3–44 s     | Lock Hip                                                          |
| C      | 44–63.5 s     | lying on the floor (its own fit)                                  |
| D      | 63.5–96.4 s   | Lock Hip, crouching                                               |
| E      | 96.6–101.5 s  | Lock Head                                                         |
| F      | 101.7–109.5 s | Lock Both                                                         |
| G      | 116.1–145.7 s | Lock Both, second calibration                                     |

Short windows fitted on their own put the fit's vertical offset into the foot offsets (F got a vertical shift of +3.1 cm), which is why one mapping is used.

### What VRChat does with the extra trackers

VRChat's bones against each tracker's bound point, still frames: **none of the extra trackers is bound rigidly.** The hips stay within 0.2–0.8 cm of their tracker (median), but the chest drifts 1.4–2.1 cm (4 cm when crouching), the elbows 1.1–2.9 cm and the knees 1.7–3.9 cm (median; 3–11 cm p95). They act as goals:

- **Elbows: a goal for the bend direction.** The arm is the one described above, with the bend direction turned toward the tracker's bound elbow point. Weight **1** fits best: elbow error, mean of both arms over the standing windows, at weights 0 / 0.5 / 1: 4.60 / 4.01 / 3.02 cm (the base solver's tracked elbow: 3.29 cm).
- **Chest: a direction goal plus a fading position pull.** The goal turns the chest's forward toward a point in front of the tracker; on its own the chest is 1.3–3.3 cm off VRChat. VRChat also keeps the chest near the tracker's chest point, but not when crouching. So after the pelvis is placed, the spine bone swings the chest toward the tracker's point: fully within 9 % of the spine's length (3 cm on the test avatar), fading further out (weight × reach / distance). Then the chest–neck–head triangle aims at the head again. Chest 0.75–2.4 cm in every window. (A full pull gives 0.8 cm standing but 6 cm crouching. The base solver's spine with the chest tracker had the chest 1.6–6.0 cm off.)
- **Knees:** the knee already bends toward the tracker's bound point, through the knee plane; the knee *direction* is within 3–7° of VRChat's. The **twist** was not: the base solver rolled the shin by the knee tracker's rotation, while VRChat's shin is not bound to it (6–36° median drift). Measured on VRChat's own bones, its thigh takes **20 %** and its shin **50 %** of the foot's twist against the rest leg under the hips. (Taking the thigh's twist fully from the foot gives 14–27° on VRChat's bones.) With these shares the shin's rotation error is 3–19° instead of 5–31° standing (mean per window). This is also what made the ankles look wrong: the shin was twisted against the foot. Without a knee tracker the leg twist stays as it was: it matches VRChat there (shin 2–5° on the three-tracker takes; the shares would make it 3–13°).
- **Feet:** the foot's rotation is 0.2–3.4° off VRChat (mean) in every window. The foot has the tracker's bound rotation, as in VRChat.

### Per window

All bones and single bones, mean distance to VRChat in cm, before → now. "Before" is the build before the work on extra trackers (the goals above, the leg twist shares and the confirm moment below). A single value means no change. Bold = better.

| Window                                        | All bones       | Hips            | Chest           | Neck / Head                   | Shoulders L / R               | Elbows L / R                  | Knees L / R                   | Feet L / R                    | Knee rot. L / R                   |
| --------------------------------------------- | --------------- | --------------- | --------------- | ----------------------------- | ----------------------------- | ----------------------------- | ----------------------------- | ----------------------------- | --------------------------------- |
| A Lock Hip (calibration from before the take) | 1.60 → **1.47** | 1.25 → 1.25     | 1.59 → **1.09** | 1.76 / 1.70 → **1.23 / 1.32** | 1.61 / 1.78 → **1.47 / 1.50** | 4.36 / 2.37 → 4.43 / **1.84** | 1.40 / 1.55                   | 1.17 / 1.25                   | 5.7° / 5.4° → **4.0° / 2.9°**     |
| B Lock Hip                                    | 2.59 → **1.35** | 3.48 → **1.15** | 2.99 → **1.21** | 2.31 / 2.13 → **1.67 / 1.29** | 2.57 / 2.00 → **1.12 / 0.97** | 3.63 / 1.72 → **3.46** / 2.17 | 3.06 / 2.65 → **1.36 / 1.21** | 2.36 / 2.68 → **0.81 / 1.43** | 7.5° / 8.6° → 8.3° / **4.4°**     |
| D Lock Hip, crouching                         | 3.82 → **1.87** | 3.54 → **1.32** | 6.04 → **2.38** | 4.73 / 4.39 → **1.92 / 2.69** | 4.44 / 3.96 → **1.77 / 2.14** | 6.10 / 3.26 → **5.48 / 2.26** | 4.08 / 4.03 → **1.85 / 2.24** | 3.39 / 3.16 → **1.17 / 1.20** | 31.2° / 12.8° → **19.1° / 12.6°** |
| E Lock Head                                   | 2.90 → **1.91** | 4.37 → **2.06** | 2.95 → **1.81** | 2.12 / 1.33 → **1.91** / 1.33 | 2.52 / 2.55 → **1.70 / 2.08** | 2.59 / 2.41 → 3.78 / **1.97** | 3.00 / 2.98 → **2.37 / 2.50** | 3.39 / 3.09 → **1.18 / 1.17** | 19.5° / 11.1° → **14.0° / 8.2°**  |
| F Lock Both                                   | 2.86 → **1.74** | 3.62 → **1.22** | 1.83 → **1.04** | 2.15 / 1.68 → **1.70 / 1.18** | 1.89 / 1.55 → 2.00 / 2.18     | 2.13 / 2.26 → 3.07 / **2.00** | 4.57 / 3.58 → **1.99 / 1.67** | 3.30 / 3.13 → **1.01 / 1.23** | 19.9° / 11.4° → **6.0° / 6.0°**   |
| G Lock Both, second calibration               | 1.85 → **1.64** | 1.30 → **1.04** | 1.57 → **0.76** | 1.79 / 0.87 → 2.24 / 0.94     | 2.41 / 2.64 → 2.86 / **2.60** | 3.58 / 2.71 → **3.39 / 2.52** | 4.03 / 2.37 → **2.36 / 1.98** | 1.00 / 1.13 → **0.94 / 1.03** | 7.3° / 8.7° → **6.3° / 6.9°**     |
| C lying on the floor                          | 6.45 → **5.17** | 8.83 → **7.07** | 12.3 → **8.80** | 8.11 / 7.92 → **5.61 / 5.47** | 7.43 / 7.48 → **6.55 / 7.09** | 7.03 / 6.90 → **5.22** / 9.76 | 4.91 / 7.02 → **4.35 / 5.43** | 3.11 / 2.82 → **1.07 / 1.02** | –                                 |

Worse: the left elbow in E and F (+1.0–1.2 cm), the right elbow in B (+0.45 cm) and C (+2.9 cm), the shoulders in F and the left one in G (+0.1–0.6 cm), the neck in G (+0.45 cm). The left elbow stays 2.9–5.5 cm off VRChat's in the standing windows; VRChat's own left elbow is 1.7–8.5 cm from that tracker's bound point. The chest's rotation is 3.5–9.8° off VRChat (it was 3.8–18.7°; worse in G, 3.8 → 7.9°).

### Fewer trackers on the same take

Any subset of the 11 points can be solved. Here trackers are left out of the solve only, so a solve with fewer trackers is compared with VRChat's 11-point pose. Mean distance of all bones to VRChat, avatar cm, still frames: the mean of the six standing windows (A, B, D, E, F, G), and window C (lying on the floor) separately. Before → now as in the table above.

| Setup                                                        | Standing: before → now | Lying on the floor: before → now      |
| ------------------------------------------------------------ | ---------------------- | ------------------------------------- |
| **11-point** (head, hands, hips, chest, elbows, knees, feet) | 2.60 → **1.66**        | 6.45 → **5.17**                       |
| 10 (no chest)                                                | 2.41 → **1.71**        | 5.87 → **5.06**                       |
| 10 (no hips)                                                 | 3.95 → **3.70**        | 7.81 → **7.77**                       |
| 9 (no elbows)                                                | 2.83 → **1.87**        | 7.04 → **5.20**                       |
| 8 (no chest, no elbows)                                      | 2.60 → **1.90**        | 6.20 → **5.13**                       |
| 6 (head, hands, hips, feet)                                  | 2.74 → **2.06**        | 6.53 → **5.44**                       |
| 3 (head, hands)                                              | 6.20 → 6.15            | 32.3 → 32.3 (the legs are procedural) |

The 3-point and no-hips rows mostly measure how far a guessed body is from VRChat's tracked one.

## Several calibrations in one take

The multi-calibration take (96 s) checks that the solve follows VRChat through everything VRChat lets you do while recording: Lock Both by arm span at the start, a "Calibrate FBT" click at 3.4 s and its confirm at 10.1 s, a switch to *by height* (avatar reload at 13.95 s), a switch to Lock Hip at 16.0 s, thumbstick moves and smooth turns (38–51 s), a playspace-mover tool (the floor raised by 0.4–1.2 m from 41.5 s), a playspace reset at 49.15 s, and a second calibration (click at 63.8 s, confirm at 70.0 s).

Mean distance of all bones to VRChat (still frames; thumbstick moves and turns excluded). *Before* = one calibration for the whole take, Lock Hip by height; *after* = each stretch with the calibration, scale and spine mode VRChat had; *now* = with the spine and arm above as well.

| Stretch     | VRChat                                                                                                          | Before  | After   | Now                                                                       |
| ----------- | --------------------------------------------------------------------------------------------------------------- | ------- | ------- | ------------------------------------------------------------------------- |
| 1.6–3.3 s   | Lock Both, arm span, VRChat's calibration from before the take (not in the take: the solve uses the 10.1 s one) | 3.93 cm | 2.92 cm | 2.75 cm                                                                   |
| 10.8–13.9 s | Lock Both, arm span, calibration at 10.1 s                                                                      | 4.45 cm | 2.65 cm | **2.44 cm**                                                               |
| 16.1–63.7 s | Lock Hip, by height, calibration at 10.1 s; mover, reset, thumbstick                                            | 2.95 cm | 1.80 cm | **1.21 cm**                                                               |
| 70.6–96 s   | Lock Hip, by height, calibration at 70.0 s                                                                      | 0.79 cm | 0.80 cm | **0.56 cm** (0.77 cm since the confirm moment is the full press, 70.24 s) |

- After a measurement change VRChat applies the same calibration again at the new scale: with the 10.1 s calibration rescaled, the solve's feet stay within 0.9–1.0 cm of VRChat's.
- Stretches are crossfaded over 0.3 s.
- In calibration mode you stand in the calibration pose with the trackers strapped as at the confirm, so the new calibration is applied from the click on. Joint speeds in those stretches stay at p95 0.75–1.25 m/s, like the rest of the take.
- The playspace reset is found as one jump at 49.15 s: 83 cm, all 6 devices, rigid within 1.8 cm. No false jumps were found in the other takes. The thresholds (every device ≥ 12 cm or a common yaw ≥ 8°, rigid within 3 cm) were set on this one setup.

## The calibration moment

VRChat's "Calibrate FBT" reloads the avatar; the confirm with both triggers follows 3.4–15 s later on the takes. The app has to bind the trackers at the same instant VRChat did.

- **It is the full press.** On the 11-point take the headset moved 8 cm in the 0.3 s before the confirm. VRChat's binding fits the moment both triggers were fully pressed (23.95–24.05 s; VRChat's log line is at 24.01 s), not the start of the pull (23.74 s, one trigger moving). The start of the pull put the hips 3.7 cm off for the whole Lock Hip stretch: window B went 2.67 → 1.73 cm per bone from this alone. The app uses the moment both triggers are above 0.7, minus 0.03 s.
- On the multi-calibration take nothing moved during the second confirm, and the start of the pull fitted 0.2 cm better there (70.6–96 s: 0.56 → 0.77 cm). This is the one regression of the change.
- The detector found the confirm within 0.07 s of an independent trigger probe on R1, R2 and R4 (9.59 s, 4.99 s and 6.45 s into the takes; R3 has no confirm of its own and uses R2's calibration). On the binding take the confirm is at 18.15 s; an earlier detector had picked the menu click at 2.62 s.
- Replaying a full day's VRChat log through the live detector finds exactly the 5 confirms in it and none of the 5 other avatar loads.

## Avatar scaling: the scaling take

167 s in a HUMR world on an edited copy of a base model (its own avatar id, in no Unity project), with trackers on feet, hips and chest, Lock Both by height, four VRChat calibrations and four height changes. VRChat does not always report a height change over OSC:

| Take time | What VRChat did | What OSC said                                                  |
| --------- | --------------- | -------------------------------------------------------------- |
| 31.8 s    | ×1.36           | `ScaleFactor` 1.3606 only (`EyeHeightAsMeters` did not change) |
| 80.5 s    | back to ×1      | nothing                                                        |
| 102.4 s   | ×0.69           | all five scaling parameters                                    |
| 108.8 s   | reset to ×1     | all five scaling parameters                                    |

A stretch without an OSC report is marked uncertain. With VRChat's IK in the take its size is measured instead: at 80.5 s the measured size is 0.984, snapped to the avatar's own size (within 3 %).

Mean distance of all bones to VRChat's IK per window of constant size, each with its own VRChat calibration (still frames; VRChat's bones computed on the base model's skeleton at the size VRChat showed it). "Before" is the previous build given the same skeleton:

| Window        | VRChat size | Before  | Now         |
| ------------- | ----------- | ------- | ----------- |
| 1–31 s        | ×1          | 4.68 cm | **2.60 cm** |
| 89.5–102 s    | ×1          | 3.96 cm | **2.42 cm** |
| 109.5–141.5 s | ×1          | 5.34 cm | **2.87 cm** |
| 150.5–167 s   | ×1          | 5.15 cm | **2.68 cm** |
| 103–108.4 s   | ×0.69       | 6.18 cm | **1.67 cm** |
| 43.5–66.5 s   | ×1.36       | 10.2 cm | **7.8 cm**  |
| 72–80 s       | ×1.36       | 4.9 cm  | 5.3 cm      |

- At ×1.36 VRChat applies its calibration at a tracking scale up to 2 % smaller than ×1.36 would give, so its feet sit about 5 cm lower and the difference is mostly a constant offset.
- The ×1 windows are 2.4–2.9 cm, against 1.4–1.9 cm on the first test avatar's own skeleton. Two reasons: VRChat's bones are computed here on the base model, not on the edited avatar (VRChat's fitted tracking scale is 2 % below the base model's), and the HUMR frames had to be re-timed (next section).
- What had gone wrong on this take before: the hip tracker was taken for an elbow, so the solve had no hips; VRChat's frames drifted up to 0.8 s late; and the height changes were not handled.
- The other takes did not change with this work: the 11-point take's standing windows are 1.66 cm as before (1.47 / 1.35 / 1.87 / 1.91 / 1.74 / 1.64), the multi-calibration take 2.44 / 1.23 / 0.77 cm.

## Other measurements

- **Timing of VRChat's IK frames.** HUMR stamps the frames with VRChat's game clock, which falls behind real time on long frames (avatar reloads, calibrations, height changes). On the scaling take the frames were in step for the first 31 s, then 0.6 s late at 110–141 s and 0.8 s late at 151–167 s. New takes are timed against a sliding minimum (±4 s) of the frames' arrival. Older takes are re-aligned when loaded: VRChat's head height against the headset's, in 16 s windows every 4 s, interpolated. On the first test avatar's takes the shift stays under 0.06 s and nothing changes.
- **Tracker roles from VRChat's IK.** A tracker on a bone keeps its distances to the headset and controllers changing like that bone's distances to VRChat's head and hands. On the 11-point take all 8 trackers are matched to the right bone, each 1.3–2.9 cm clearer than its next-best bone.
- **Tracker roles from motion alone.** On the binding take a method that used heights at the calibration moment had the feet swapped; the left-foot tracker sits 11 cm left of the head over the whole take, and the current method gets them right. An elbow tracker must keep a steady distance to its controller (10–90 % spread under 7 % of the eye height). On the scaling take the hip tracker (35 cm from a resting left hand, spread 41 cm) had been taken for the left elbow without that test.
- **Tracking loss.** On the 11-point take the repair found 19 losses, 10.3 s in all: hips 2.5 s and chest 2.0 s while lying on them, a knee frozen for 1.1 s and then jumping 55 cm, the left elbow for 0.5 s and 1.0 s, and single-sample foot glitches of up to 78 cm.
- **VRChat jumps.** On the 11-point take: one jump, 11.36–12.24 s, 43 cm.
- **The live view's mapping.** These measurements used the live presenter’s HUMR skeleton overlay, with the mapping estimated from the last 3 s. Record now previews SteamVR devices only. On recorded takes replayed as if live, VRChat's head bone lands 4.3 cm (median) from where its headset says it should be, the hands 3.3 cm (the 11-point take). On takes with thumbstick moves it is 6–10 cm, and the mapping catches up within 3 s.

Numbers for scenes (the transition match score, the foot lock) are in [scenes.md](scenes.md); round-trip accuracy in Blender and Unity is in [export-and-pipeline.md](export-and-pipeline.md).

## What is and is not in the code

The arm, spine and knee behaviour is matched to VRChat's IK 2.0 by measurement on recorded takes; no third-party IK source code is included.

The body solver these parts sit in (the "base solver" on this page) is derived from MIT-licensed software; its notice is in `THIRD_PARTY_NOTICES.md` at the root of the repository. The matched parts are the project's own code:

- `src/Vrmr.Fbik/Spine/VrcSpine.cs`: the spine and the chest tracker's goal;
- `src/Vrmr.Fbik/Limbs/VrcArm.cs`: shoulder, elbow, forearm twist and the elbow tracker's goal;
- the knee-plane block in `src/Vrmr.Fbik/Limbs/IkLegSolveCore.cs`;
- `src/Vrmr.Solve/`: calibration (offsets, scale, hang, the confirm moment), tracker roles, repairs, jumps and avatar scaling.

The project layout is described in [architecture.md](architecture.md).

## Reproduce it on your own takes

1. In VRChat, switch OSC on and set logging to Full (HUMR writes to VRChat's log).
2. Start the app and join a world that has a HUMR recorder. Record in the world and in the app at the same time; the app can follow the in-world HUMR button.
3. The take now holds the raw devices and VRChat's IK frames. `vrmr info <take.vrmr>` shows how many VRChat IK frames it contains.
4. In the take's settings the body can be switched between VRChat's own IK (the reference) and the IK solve, which is enough to compare the two by eye.

The measurement harness itself (the world fit and the per-bone scoring) is not in the repository yet, so the tables on this page cannot be regenerated from the repository alone today.

`vrmr dump` together with `tools/compare_dumps.py` is a different tool: it is the regression check that a code change did not alter the motion. It says nothing about how close the motion is to VRChat. See [CONTRIBUTING.md](../CONTRIBUTING.md).

Takes are personal data (your motion, your avatar's id and name, the world's name). Share one only if you are comfortable with that.

## Limits: one setup

Every constant and every number on this page comes from one person, one set of straps and one set of devices. In particular:

- the hand pose on the controller is measured for Index controllers only; other controllers use SteamVR's skeletal wrist with the base solver's offsets;
- the head offset, the 0.94 eye-height ratio and the spine, elbow and twist constants are fitted on a single test avatar and one body;
- the playspace-jump thresholds and the confirm timing are set on one tracking system.

If you have a different headset, other controllers or trackers, or a very different avatar, your measurements would help most. Use the [device report form](../.github/ISSUE_TEMPLATE/device.yml) and include what you measured and how.
