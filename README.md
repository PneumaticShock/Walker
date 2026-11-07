# Walker

A physics-simulated humanoid in Unity that balances on its own and takes recovery steps when it gets pushed. It has no animations. Balancing and stepping use only torque-limited `ConfigurableJoint` drives, with no root forces or body-position corrections.

![The humanoid stepping to recover from a burst of balls](Docs/walker-burst.png)

## Running it

You need Unity **6000.6.8f1**. Other versions haven't been tested.

1. In Unity Hub, choose **Add > Add project from disk** and select this folder. The first open downloads packages and rebuilds the `Library` folder, which takes a few minutes.
2. Open `Assets/Scenes/SampleScene.unity` and press Play.
3. Click the Game view so it receives keyboard input.

| Key | Action |
|---|---|
| Space | Fire the next of four launchers |
| 1–4 | Fire a specific launcher |
| B | Fire a burst of 6 balls, 0.1 s apart, from the next launcher |
| F | Turn automatic fire on or off |
| G | Pause or resume the moving floor |
| R | Reset the trial |

The floor starts out moving, which is the hardest case. Press G and then R for flat ground.

## How it works

**Standing.** `JointBalanceHumanoid` estimates the capture point, the spot where the body would come to rest if its foot were placed there. It feeds the error back through the ankles, hips, spine, knees and arms as joint targets.

**Stepping.** `HumanoidFootwork` takes over when the capture point moves beyond what standing feedback can manage:

1. The foot nearest the capture point stays down, and the other steps to where the body would come to rest. The target is limited to what a nearly straight leg can reach, and recomputed every physics tick.
2. A bigger push takes several short steps.
3. Once the body settles, a pelvis-controlled hold brings it to rest in whatever stance the feet landed in.
4. A closing step returns the feet to a normal stance if they need it, and standing feedback takes over again.

A few settings matter if you change things:

- **Fixed timestep:** this must be 0.01 s, as saved in `ProjectSettings/TimeManager.asset`. At Unity's default of 0.02 s the humanoid falls while standing untouched.
- **Torque limits:** the knee is limited to 280 N·m and the hip to 300 N·m. Set them with `Maximum Force` on the Slerp Drive of the Shin and Thigh joints.
- **Scene values:** values saved in the scene override the defaults in code, so a changed default has no effect until the scene is updated too.

## Project layout

```
Assets/Walker/
  Humanoid/   The controller: JointBalanceHumanoid, HumanoidFootwork, FootContactSensor
  Platform/   The test arena: moving floor, ball launchers, materials, scene builder
  Testing/    Benches and fixtures, which use both of the above
Assets/Scenes/SampleScene.unity   The scene to open
BalanceResults/                   Bench output (only the results the docs cite are tracked)
Docs/                             Images for this README
```

The humanoid code doesn't reference the platform or testing code.

## Results

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
| Moving floor, 10 cm and 7 degrees (the scene's setting) | 41/51 |

Pushes at the pelvis hold up to 70 N·s forward, and up to 60 N·s backward and sideways, which is the most the bench tries in those directions.

Known limits:

- Burst after burst with no reset is less reliable than a single burst.
- The moving floor is the weakest case. A foot landing across a slab edge or on a tilted slab is a common cause of failure.
- Outcomes near the limit are chaotic, so a single run says little. Run the bench with `repeats` of at least 2 after any tuning.
- The camera doesn't follow, so a long run of bursts can walk the humanoid out of frame and eventually off the floor.
- Self-collision is off, so the legs can pass through each other on a crossing step.

## More detail

- [ARENA.md](Assets/Walker/Platform/ARENA.md): arena settings, how to run the bench, and full results, including a comparison with a 240 N·m hip.
- [RESEARCH.md](Assets/Walker/Humanoid/RESEARCH.md): the papers behind the controller, and what was learned on this rig.
