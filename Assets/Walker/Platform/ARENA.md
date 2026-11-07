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
