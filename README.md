# Walker

A physics-simulated humanoid in Unity, meant eventually for a VR game. It balances on its own, takes recovery steps when pushed, walks, jumps in place, takes up a sword and fences from Girard Thibault's *Académie de l'Espée* (1630). It has no animations. Everything it does goes through torque-limited `ConfigurableJoint` drives, with no root forces or body-position corrections.

![The humanoid stepping to recover from a burst of balls](Docs/walker-burst.png)

## Running it

You need Unity **6000.6.8f1**. Other versions haven't been tested.

1. In Unity Hub, choose **Add > Add project from disk** and select this folder. The first open downloads packages and rebuilds the `Library` folder, which takes a few minutes.
2. Open `Assets/Scenes/SampleScene.unity` and press Play.
3. Click the Game view so it receives keyboard input.

Skills (the **Skills Demo** object, panel at the top right):

| Key | Action |
|---|---|
| W | Walk forward at 0.5 m/s; with Shift, 0.9 m/s |
| S / A / D | Walk slowly back or sideways (untested) |
| J | Jump in place (0.10 m) |
| P | Take a sword from a rack |
| X | Drop the sword |
| L | Thibault's lesson against a dummy (takes the sword from its rack first if none is held) |
| V | Sparring: a second fencer steps in across Thibault's circle; both take swords and the guard, then each attacks in turn |
| K | Rough floor on or off (bumps of about 4 cm) |
| C | Camera follow on or off |
| R | Reset |

Ball tests (the arena, panel at the top left):

| Key | Action |
|---|---|
| Space | Fire the next of four launchers |
| 1–4 | Fire a specific launcher |
| B | Fire a burst of 6 balls, 0.1 s apart, from the next launcher |
| F | Turn automatic fire on or off |
| G | Pause or resume the moving floor |
| R | Reset the trial |

The floor starts still. G starts the moving floor, the hardest case for balance, and R resets.

## How it works

**Standing.** `JointBalanceHumanoid` estimates the capture point, the spot where the body would come to rest if its foot were placed there. It feeds the error back through the ankles, hips, spine, knees and arms as joint targets.

**Stepping.** `HumanoidFootwork` takes over when the capture point moves beyond what standing feedback can manage:

1. The foot nearest the capture point stays down, and the other steps to where the body would come to rest. The target is limited to what a nearly straight leg can reach, and recomputed every physics tick.
2. A bigger push takes several short steps.
3. Once the body settles, a pelvis-controlled hold brings it to rest in whatever stance the feet landed in.
4. A closing step returns the feet to a normal stance if they need it, and standing feedback takes over again.

**Walking.** A capture-point (DCM) gait: each step lands where the capture point will be at touchdown, less the lead the next stance needs, and the stance ankle steers the capture point along its planned path.

**Jumping.** A crouch, then a constant-acceleration push to the take-off speed for the height wanted, with the centre of pressure kept under the centre of mass so the push adds no spin. In flight the trunk is held rigid on the pelvis.

**Arms and the sword.** `HumanoidArms` solves each arm as two ball joints to a hand pose, held against the trunk's target rather than its actual pose, which would act as a negative spring. `SwordHandling` reaches for a rack, grips the sword with a locked joint, draws it and raises it in two stages, and corrects the blade's aim from its measured direction.

**Getting up (in progress).** `HumanoidGetUp` notices the body lying still, lets go of whatever the hands hold, and takes every joint: a list of poses from face down, then a rise from a squat to standing with the ankles keeping the centre of mass over the feet, and back to the standing controller facing wherever the feet point. Squatting down and rising works. From face down it gets as far as the folded kneel (hips back toward the heels, knees on the floor, arms reaching forward), following a person's way up: push from the bottom of a push-up while the knees and hips fold back, then one knee up, that foot down, and push up. The palms go close beside the chest, elbows back and up as in a narrow push-up. The arms are solved with a hinged elbow and follow their solution closely, so the hands go where they are sent instead of sweeping out along the floor. Planted there, the arms are solved for shoulders a little higher than they are, so they push: a full push-up, chest 0.55 m up. Then the hands push the body back while the knees and hips fold (the closing Z), and the trunk is raised on straight arms to about 70 degrees from upright. It does not yet come upright: let go there, it tips forward over the knees. The knee stops at 120 degrees, and bending it further only lifts the light shins. Walking the hands back until the centre of mass is over the knees (a `walkhands` pose: the hand further ahead steps back, the other stays planted) does not work yet either: the centre of mass is still 0.39 m ahead of the knees there, and one straight arm cannot hold the trunk while the other hand is off the floor, so the body sinks and rolls onto its side. Sliding both loaded palms back instead, on a lighter grip, works: the hands come under the shoulders and the body kneels steadily on hands and knees with the trunk 58 degrees from upright, A quick push-off from there did not throw the trunk upright. That push-up was made with straight legs, though; the get-up now pushes up as a person does, knees on the floor and the hips folding as the hands push, and folds back onto the knees, which is where it now ends. Raising the trunk from there, and walking the hands back with whole-arm steps (the shoulders over the other hand first, then the arm lifted from its shoulder), do not work yet: in the runs so far a hand slid away or under the body and it rolled onto its side. It is not in the skills demo yet; the bench runs it.

**Thibault.** `ThibaultManual` lays out Thibault's circle (its diameter is the fencer's stature), takes the guard (the straight line: arm and sword in line, point at the face), steps round by S to N with placed steps, and delivers an imbrocade (hand high, nails down, point onto the breast). Placed steps (`HumanoidFootwork.StepTo`) shift the weight until the capture point is where, by touchdown, it will have run on to the middle of the new stance, then steer it there with the stance ankle.

**Sparring.** V puts a second humanoid where its guard step lands on the first one's far point, so both fence on the one circle. Each takes a sword from a rack and the guard, then the first goes round by S to N and delivers the imbrocade at the second's breast while the second keeps its guard, and then the second does the same. Neither sword collides with the other fencer or the other sword. At Thibault's measure the points reach the faces, and a blade pressed into a body or crossed with another threw the fencers over. A touch is measured, as in the lesson, by how near the point comes to the breast.

A few settings matter if you change things:

- **Fixed timestep:** this must be 0.01 s, as saved in `ProjectSettings/TimeManager.asset`. At Unity's default of 0.02 s the humanoid falls while standing untouched.
- **Torque limits:** the knee is limited to 280 N·m and the hip to 300 N·m. Set them with `Maximum Force` on the Slerp Drive of the Shin and Thigh joints. While getting up, the arms may use up to 120 N·m at the shoulder and 80 at the elbow (60 and 40 otherwise).
- **Arm collisions:** the arms collide with the trunk, the head and each other (`armsCollide`), so a hand or forearm cannot pass through the chest. Legs still pass through each other. With it on, walking at 0.5 m/s passed 2/2, at 0.9 m/s 1/2, sword pick-up 2/2, and the lesson after pick-up finished standing 1/2.
- **Joint ranges:** the body was built with 65 degrees of hip flexion and 90 to 100 degrees at the shoulder and elbow. `JointBalanceHumanoid` widens them to human ranges when it starts (`hipFlexion` 120; shoulder to 170, elbow to 150): without them there is no squat and no way up off the floor. Walking measured the same with either hip range.
- **Scene values:** values saved in the scene override the defaults in code, so a changed default has no effect until the scene is updated too.
- **Split feet:** `splitFeet` on the humanoid hinges each foot at the ball, with a human toe joint's range. It is off: it neither helped nor hurt measurably. See [ARENA.md](Assets/Walker/Platform/ARENA.md).

## Project layout

```
Assets/Walker/
  Humanoid/   The controller: JointBalanceHumanoid, HumanoidFootwork, HumanoidArms, HumanoidGetUp, FootContactSensor
  Fencing/    SwordHandling, and ThibaultManual (more manuals to follow)
  Props/      The sword and its rack
  Platform/   The test arena: moving floor, bumpy floor, ball launchers, the skills demo, scene builder
  Testing/    Benches and fixtures, which use both of the above
Assets/Scenes/SampleScene.unity   The scene to open
BalanceResults/                   Bench output (only the results the docs cite are tracked)
Docs/                             Images for this README
```

The humanoid code doesn't reference the platform or testing code.

## Results

### Skills

Measured on 2026-11-08 with `SkillBench`, on a still floor. Walking runs are 0.3, 0.5, 0.7 and 0.9 m/s, two runs each, and pass if the humanoid covers the distance, stays up and stops. The bumpy floor has bumps of about 4 cm (slopes of 4, 7 and 11 degrees at the median, 95th percentile and maximum); the rough slabs are the arena's slabs held still at heights of up to 4 cm and tilts of up to 4 degrees.

| Skill | Passed |
|---|---|
| Walking, level floor | 5/8 |
| Walking, bumpy floor | 5/8 |
| Walking, rough slabs | 0/8 |
| Jump in place, 0.10 m (level, slabs, bumps) | 5/6 |
| Jump in place, 0.15 m (earlier bench, 9 runs) | 4/9 |
| Forward jumps (earlier bench, 18 runs) | 0/18 |
| Taking the sword from its rack | 3/3 |
| Thibault's lesson after taking the sword (as the demo runs it): finished standing | 3/3, then 2/4 in a later bench |
| Same, all of the lesson's criteria | 0/3 |
| Thibault's lesson with the sword put straight into the hand: finished standing | 1/3 |
| Getting up: squatting down and standing up again | 1/1 |
| Getting up from lying face down, face up or on a side | 0 (not reached standing yet) |
| Sparring bout (V), both fencers still standing at the end | 0/3; in one bout the first fencer's attack finished standing, point within 9 cm of the other's breast |

Thibault's lesson is measured against its own criteria: the guard straight within 10 degrees and on the face within 0.1 m, each foot within 6 cm of its point on the circle, the imbrocade's point within 6 cm of the breast, and no fall. With the guard 0.34 m wide and 0.22 m long, the feet landed within 1 to 8 cm of their points in all six runs, and the guard at C aimed within 4 cm of the face. Every run still missed at least one criterion. The imbrocade's point came within 7 to 37 cm of the breast. The guard taken back up at N missed the face by 11 to 25 cm: there the body cannot turn to face the opponent, so the sword arm points 42 degrees across it and the wrist is at its limit. With the sword put straight into the hand, two of three runs fell after the imbrocade, while the arm came back to the guard. A demo run with the latest code finished standing, with the feet within 5 cm, the point within 22 cm of the breast, and the guard at N 11 cm off the face.

### Balance

Measured on 2026-11-07 with `RecoveryStepBench`, three runs per case, on flat ground unless noted. A run passes if the humanoid stays up and is moving slower than 0.15 m/s at the end. Balls are 1.5 kg at about 8 m/s.

| Scenario | Passed |
|---|---|
| 1 to 5 balls, 0.1 s apart, from each of 8 directions | 119/120 |
| 6 balls, 0.1 s apart, from each of 8 directions | 22/24 |
| 6 balls, mixed directions | 12/12 |
| Second hit 1 s after the first step lands: 40 N·s push, then another | 88/96 |
| Second hit 1 s after the first step lands: 4 balls, then 4 more | 88/96 |
| Two pushes 3 s apart | 11/12 |
| 6-ball bursts with no reset, 6 s apart | 7/9 |
| 6-ball bursts with no reset, 3 s apart | 5/9 |
| Moving floor: pushes and one or two balls | 41/51 |

Pushes at the pelvis hold up to 70 N·s forward, and up to 60 N·s backward and sideways, which is the most the bench tries in those directions.

### Moving floor

The scene's floor moves each slab up to 10 cm and tilts it up to 7 degrees, out of step with its neighbours, so most steps land on a different slab across a stair. Between neighbours front to back, the step is over 7 cm about half the time and reaches 22 cm. This is much harder than flat ground, and the humanoid often falls.

| Moving floor, 8 directions, repeats not used for tuning | Passed | Fell |
|---|---|---|
| 1 ball (3 runs per direction) | 24/24 | 0 |
| 2 balls (12 runs per direction) | 74/96 | 16 |
| 3 balls | 52/96 | 35 |
| 4 balls | 44/96 | 35 |
| 5 balls | 38/96 | 52 |
| 6 balls (24 runs per direction) | 77/192 | 104 |
| 6 balls, mixed directions (3 runs per case) | 12/12 | 0 |
| No balls, one foot where four slabs meet (3 runs per case) | 22/36 | 13 |
| Same, with the floor still | 36/36 | 0 |

Each repeat starts about 0.4 s later than the last, so the runs meet the slabs across their whole cycle. Three settings for uneven ground (`roughClearance`, `pathClearance`, `stepDownLead`) cut the 6-ball falls from 137 to 104 and leave level ground unchanged. [ARENA.md](Assets/Walker/Platform/ARENA.md) has what goes wrong on this floor, the before-and-after numbers, and what was tried and rejected.

Known limits:

- Burst after burst with no reset is less reliable than a single burst.
- The moving floor is by far the weakest case. From three balls in one direction, about half the runs or more fail. See [ARENA.md](Assets/Walker/Platform/ARENA.md) for why.
- Outcomes near the limit are chaotic, so a single run says little. Run the bench with `repeats` of at least 2 after any tuning.
- Running is not implemented yet: the gait is tested up to 0.9 m/s, and has no flight phase.
- Forward jumps fall; only jumps in place land reliably.
- The body cannot turn; walking and fencing keep the facing it starts with.
- The lesson's footwork is chaotic: small changes anywhere in the controller flip runs between passing a step and falling. Judge changes on three or more runs.
- In the ball tests the camera doesn't follow (the skills demo's does), so a long run of bursts can walk the humanoid out of frame.
- Self-collision is off, so the legs can pass through each other on a crossing step.
- After a fall the body stays down: getting up works only from a squat so far.
- In the sparring bout the second fencer, which faces the other way, fell in each of three bouts, during its guard or its attack. A humanoid turned to face backwards had never been tested before, so a facing assumption somewhere is possible.

## More detail

- [AGENTS.md](AGENTS.md): rules and workflow for coding agents (Claude Code reads it through CLAUDE.md), and the headless bench: `powershell -File Tools/bench.ps1 -Filter "walk 0.5 level|getup squat" -Repeats 2` with the editor closed.
- [ARENA.md](Assets/Walker/Platform/ARENA.md): arena settings, how to run the bench, and full results, including a comparison with a 240 N·m hip.
- [RESEARCH.md](Assets/Walker/Humanoid/RESEARCH.md): the papers behind the controller, and what was learned on this rig.
