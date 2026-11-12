# Balance test arena

Open SampleScene, press Play, and click the Game view.

- Space fires the next of four launchers.
- 1–4 fire from a specific direction.
- F toggles automatic fire, cycling directions every three seconds.
- G pauses/resumes the moving slabs at their current poses.
- R resets the humanoid, clears projectiles and counters, and resets the ground phase. This is an explicit test reset, not part of balance recovery.

Select **Balance Test Arena** to adjust projectile mass, speed, radius, interval, slab height amplitude, tilt, and oscillation frequency. Default projectiles are 1.5 kg, approximately 8 m/s. The initial velocity includes ballistic compensation; after launch they are ordinary gravity-driven Rigidbody spheres with continuous collision detection. Impact statistics report the first humanoid collision impulse per projectile.

The floor's 420 kinematic slabs use Rigidbody.MovePosition/MoveRotation in FixedUpdate. Ground motion ramps in after two seconds. Defaults are 2.5 cm vertical amplitude and 2 degree tilt, with phase offsets creating changing uneven support. The old ground remains below the slabs as a catch floor. Kinematic platforms are external terrain actuators; the humanoid still receives balancing actuation only through its joint drives.

Verification: a single launch registered approximately 11.5 N s of physical contact impulse while the humanoid remained upright on moving ground. Automatic fire registered 11/11 hits during the check. Reset restored the initial pose and cleared counters. The Game view control guide was visually checked. Higher disturbance settings can topple the humanoid; use R to restart.

The earlier BalanceValidation fixture assumes a flat ground at y=0. Its recorded research comparison predates this arena; do not run that fixture on this scene without restoring its flat-ground setup.

## Recovery steps

The fixed timestep must be 0.01 s, saved in `ProjectSettings/TimeManager.asset`. At Unity's default of 0.02 s the humanoid falls while standing untouched, and logs a warning.

`HumanoidFootwork` watches the capture point. While it stays within what the standing feedback can manage, nothing changes. Beyond that, the foot nearest the capture point stays down and the other steps to where the body would come to rest, limited to what a nearly straight leg can reach and recomputed every physics tick. A bigger push takes several short steps. Once the body has settled, a pelvis-controlled hold brings it to rest in whatever stance the feet landed in, a closing step brings the feet back to a normal stance if they need it, and the standing feedback takes over. All actuation remains ConfigurableJoint targets; no root forces or body-position corrections are used. The HUD shows the footwork state and steps landed. Press `B` for a burst of balls from the next launcher (`burstSize`, `burstInterval`).

Torque limits were raised for this: knee 280 N m (was 140) and hip 300 N m (was 180). To change them, set `Maximum Force` on the Slerp Drive of the Shin and Thigh joints. The floor is 20 by 21 slabs, about 16 by 17 m, so the launcher stands now sit on it and have no colliders. `BalanceArenaBuilder.ExpandFloor()` adds missing slabs to an existing arena. With `spentBallsClearLegs` on, a ball that has hit something passes through the feet and shins, because the step planner cannot see a ball on the floor.

`RecoveryStepBench` measures this. Add it to the arena in Play mode and call `Run("label")`, or `RunNamed("label","filter")`; set `repeats` to run each case more than once, because outcomes near the limit are chaotic and a single run says little. Results go to `BalanceResults/`: `recovery-final4.json` for the saved settings and `recovery-final5.json` for a 240 N m hip.

Measured 2026-11-07 on flat ground unless noted, three runs per case, with the settings saved in the scene:

| Scenario | Hip 300 N m (saved) | Hip 240 N m |
|---|---|---|
| Original bench: quiet, pushes, terrain, one or two projectiles | 132/138 | 129/138 |
| Balls 0.1 s apart, one of 8 directions: 1 ball | 24/24 | 24/24 |
| 2 balls | 24/24 | 24/24 |
| 3 balls | 23/24 | 23/24 |
| 4 balls | 24/24 | 22/24 |
| 5 balls | 24/24 | 23/24 |
| 6 balls | 22/24 | 23/24 |
| 6 balls, mixed directions | 12/12 | 12/12 |
| Second hit 1 s after the first step lands: 40 N s push, then 40 N s push | 88/96 | 91/96 |
| Second hit 1 s after the first step lands: 4 balls, then 4 balls | 88/96 | 82/96 |
| Two pushes 3 s apart | 11/12 | 9/12 |
| Step from rest | 5/6 | 6/6 |
| 6-ball bursts in a row with no reset, 6 s apart | 7/9 | 4/9 |
| 6-ball bursts in a row with no reset, 3 s apart | 5/9 | 4/9 |
| Moving ground, 10 cm and 7 degrees (the scene's setting) | 41/51 | 41/51 |

At the original 180 N m hip, a single run of the burst, push, second-hit and moving-ground sets passed 56/141, about 40%. The same sets pass about 91% at 240 N m and 92% at 300 N m. Holding the pelvis level in single support takes more than 180 N m once the trunk leans, and the pelvis rolls over by nearly 50 degrees. 300 N m is saved because it does better when bursts keep coming, and it also holds a 70 N s forward push.

Pushes at the pelvis: sideways and backward hold at the 60 N s the bench goes up to, and forward up to 70 N s. Of the runs that recovered with steps, 349 of 359 ended in a normal stance. After a ball burst the humanoid is back in quiet standing about 4 s after the first ball is launched (median), and takes 2 to 9 steps for 6 balls.

Known limits:

- **Burst after burst with no reset is less reliable than one burst.** The next can catch the humanoid still stepping from the last.
- **Moving ground is the weakest case,** about 4 times in 5. The failures vary from run to run; a foot landing across a slab edge or on a tilted slab is common among them.
- **A case near the limit can go either way.** Re-run the bench after any tuning, with `repeats` of at least 2.
- **The camera does not follow.** A long run of bursts can still walk the humanoid out of frame, and eventually off the floor.
- Self-collision is off, so the legs can pass through each other on a crossing step.

Saved scene values override defaults in code, so a changed default has no effect until the scene is updated too. `FootworkTrial`, `FootworkSearch` and `RecoveryStepBench` are optional development fixtures, not attached to the saved scene.

## Why bursts sometimes end in a fall

Investigated 2026-11-07, after three falls from front-right in six runs looked like a weak direction. It is not one. With `RunNamed("label","burst 6 from")` and `repeats` of 24, 181 of 192 runs passed, 5 fell and 6 had not settled, so about 2.6% fall. Front-right passed 22/24 with 2 falls, front-left 21/24 with 1, and the other directions 22 to 24 with at most 1 each.

Falls end the same way. In the four measured, one step lets the pelvis drop by at least 0.13 m, the next foot lands on a knee bent about 80 to 100 degrees, and that leg folds. What starts the drop varies: a stance foot carrying almost no load, one placed too far from the body, or one sliding. Feet crossing during the hold is not the general cause: it came before only one of five falls traced, and runs that crossed also recovered.

Tried and rejected, and not kept in the code: before lifting a foot, wait up to 0.3 s until the other foot carries 100 N and the pelvis is above 0.86 m, unless the capture point is at least 0.12 m outside the feet. A low pelvis does predict a fall: 9 of 24 steps started below 0.90 m were followed by one within 1.2 s, against 5 of 602 otherwise. But preventing those lifts did not help, so it looks like a symptom rather than the cause.

| Runs that passed | Saved settings | With the wait |
|---|---|---|
| 6 balls, 8 directions, 24 runs each (the set it was picked on) | 181/192 | 188/192 |
| Full bench, 3 runs per case | 571/627 | 569/627 |

On the full bench, 17 runs improved and 19 got worse.

When measuring something this rare:

- **Three runs per case cannot resolve it.** At 2.6%, no falls in 24 runs happens about half the time.
- **A rerun is close to a replay.** 92 of 96 runs matched when the same set ran twice, so it is not a second sample. More `repeats` gives new start times instead.
- **Case order matters.** Front-right's second and third runs fell when the case ran after the front case, and passed when it ran alone.
- **Judge a change on the full bench,** not on the cases it was picked on.

## Moving floor

Investigated 2026-11-07 and 2026-11-08. The bench has the same ball tests on the moving floor (`moving ground N balls from ...`, `moving ground 6 balls mixed ...`, `moving ground again ...`), and a test with no balls where one foot starts where four slabs meet (`corner moving ...`, `corner still ...`, and a control for each). Each repeat of a moving-floor case starts 0.37 s later than the last (`repeatTicks`), so together the repeats meet the slabs across their whole cycle. `firstRepeat` and `stride` choose which repeats run, so a setting tuned on some repeats can be checked on others it has never seen.

The slabs are 0.79 m across. Their phase steps by 1.17 rad from one to the next along z, but by about -0.43 rad along x, so neighbours front to back differ the most. Over the cycle, the step between neighbours is over 7 cm (the swing clearance on level ground) 48% of the time, over 15 cm 10% of the time, and reaches 22 cm. Tilt alone is as hard as both together: 16/48 runs passed with tilt and no height motion, 19/48 with height and no tilt, and 17/48 with both.

What goes wrong:

- **Stepping up lands on a bent knee,** about 3 degrees more for every centimetre. As the other foot lifts, the swing leg's reaction puts up to 1.7 times body weight on that knee. Falling backward, a stance knee bent more than about 55 degrees when the other foot lifts usually folds rather than straightens, and from 70 to 80 degrees half of them fold. On level ground a lift with the knee at 70 degrees or more almost never happens: 8 of 501.
- **Feet falling backward land about 15 cm short of the plan,** often touching down early on higher ground. The foot ends up under or behind the hip, which bends the knee further.
- **Stepping down, the leg cannot reach** until the body has come down, so the foot lands late.
- **A foot across a border rests on the higher slab's edge** and pivots on it. The ankle has no leverage there, while the support model still assumes the whole foot. With no balls and one foot where four slabs meet, 36/36 runs stay up on a still floor and only 17/36 on the moving one. Because the feet stand side by side, both of them straddle the front-back border in that test.
- **Once stepping starts on this floor it often does not stop.** In the corner test with no balls at all, speed reached a median of 0.98 m/s.

Kept, and saved in the scene. On level ground they change nothing: the same flat cases passed 132/144 with and without them, with 6 falls either way.

- `roughClearance` (0.13 m): more swing clearance where the ground is uneven, meaning a step between the feet, a tilted slab, or a step under the stance foot. Level ground keeps `stepClearance`.
- `pathClearance` (0.03 m): lift the swing foot over higher ground on the way to the landing.
- `stepDownLead` (0.7): before a lower landing, bring the pelvis down over that fraction of the swing; before a higher one, straighten the stance leg.

Measured on repeats not used for tuning, 8 directions, before and after:

| Moving floor | Passed before | Passed now | Falls before | Falls now |
|---|---|---|---|---|
| 2 balls, 12 runs per direction | 72/96 | 74/96 | 22 | 16 |
| 3 balls, 12 runs per direction | 55/96 | 52/96 | 38 | 35 |
| 4 balls, 12 runs per direction | 33/96 | 44/96 | 53 | 35 |
| 5 balls, 12 runs per direction | 35/96 | 38/96 | 57 | 52 |
| 6 balls, 24 runs per direction | 49/192 | 77/192 | 137 | 104 |

With three runs per case: 1 ball 24/24 before and after, 6 balls in mixed directions 12/12 before and after, a second burst of 4 balls 1 s after the first step lands 28/96 before and 34/96 now, and the corner test with no balls 17/36 before and 22/36 now. Only the 6-ball gain is clear on its own (44 runs better and 16 worse, p = 0.0004). For 2 to 5 balls the falls are fewer at every size, but the gain in passes is within noise.

Tried and rejected, each measured on the same cases as the baseline:

- **Searching near the landing for ground that does not straddle a step or bend the knee further.** It moved feet away from where they were needed, and on its own passed 28/96 against 32 to 36 for the baseline. Avoiding straddled landings did help the corner test (25/36), but cut 6-ball passes to 11/48.
- **Waiting before a lift** until the other foot carries load or its knee is less bent: no gain. Any waiting hurts on this floor. A slower step (`stepTime` 0.28) or a longer wait for the landing foot's load each passed only 6/48.
- **Standing on the higher foot,** and **pressing the toe on a bent stance knee** to bring the ground force closer to the knee: no gain, 14/48 and 12/48 against 16/48.
- **Stepping off an edge while standing calmly:** no gain on its own (17/48 against 16/48 with balls, 17/36 against 16/36 on the corner test).
- **A less eager step-again threshold** (`stepAgainMargin` 0.10 or 0.15): no better, or worse (8/48).
- **The existing settings, swept one at a time:** none made a decisive difference.
- **Support from where each foot really touches** (2026-11-08), taken from the contact points and used only where it is clearly smaller than the sole. It does recognise a foot across a border, and never fires on level or tilted ground. But it made things worse in every form tried, mostly by stepping more. On the corner test it passed 12/36 against 23/36 (p = 0.027), and on 6 balls 8/48 against 16/48 (p = 0.021). Adding a step trigger from a normal stance did no better (11/36 and 8/48). Limiting where the ankle presses, without the margin, still came in at 15/36 and 10/48. A guess at why: pressing past the edge can tip the foot down onto the neighbouring slab, which adds support, while respecting the edge leaves the foot perched on it.

The ground under a planted foot reads up to 2.4 cm high on a tilted slab, because it is the highest of five samples, but it reads much the same under both feet, so it does not upset the load sharing.

Not tried yet: anything that brings a run of steps on this floor to a stop.

## Split feet

Tried 2026-11-08. `splitFeet` on `JointBalanceHumanoid` cuts each foot at `splitAt` of its length from the heel, hinged across the foot, when Play mode starts. The rear part stays the foot the controller works with. The default is the ball of the foot (0.72) with human ranges for the first metatarsophalangeal joint, 76 degrees up and 34 down (the average of normal specimens in a 1986 Journal of Bone and Joint Surgery study; clinical norms run 70 to 90 up). The front part weighs 0.15 kg on a 30 N m per radian spring. It is off in the scene.

What it took to work at all:

- **The foot sensor ignored the rear part.** `FootContactSensor` counted only upward impulses, but which way Unity reports a contact's impulse depends on the order of the two bodies in the pair, and that order differed for the parts made at Play time. With the rear part's load read as zero, the controller saw its feet as unloaded and fell over standing still. It now counts the size of the vertical impulse. The rigid feet read the same as before, tick for tick.
- **The rear part must keep the whole foot's mass and inertia.** A stiff drive on a light link acts with a stiffness set by the link's inertia, and the ankle was tuned on the whole foot. A rear part with its own share of the mass (37% of the inertia at the ball, 9% at the midfoot) fell over standing still.
- **The hinge is a `HingeJoint`.** With a `ConfigurableJoint` it was unclear which way its limits acted. `HingeTest` checks the limits act on the right sides: about 94 degrees up and 42 down against 76 and 34, the excess being a 0.15 kg part slammed into its stop.

Results, with the saved terrain settings:

| Moving floor | Rigid | Ball split, human (30 N m/rad) | Ball split, stiffer (150 N m/rad) |
|---|---|---|---|
| Quiet standing, still, tilted and moving floors | 6/6 | 6/6 | 6/6 |
| No balls, one foot where four slabs meet (36 runs) | 23 passed, 12 fell | 18 passed, 18 fell | 19 passed, 15 fell |
| 6 balls, 8 directions (48 runs) | 16 passed, 27 fell | 19 passed, 23 fell | 17 passed, 24 fell |

None of these differences is beyond noise (p 0.38 to 1.0). The rear part, from heel to ball, is still 0.20 m of rigid sole, and that is what straddles a slab border, so the hinge rarely gets to bend into one. The controller also knows nothing of the toes.

A split at the midfoot (0.45, 15 degrees up and 25 down, 400 N m per radian) cannot stand: its rear part ends 4 cm ahead of the ankle, so ordinary standing pressure has to go through the joint, and a spring that soft in series with the ankle is weaker than the roughly 650 N m per radian the body needs to stay upright. The human midfoot is far stiffer under load than its range suggests.
