# Working on Walker

A physics humanoid in Unity 6000.6.8f1 (open `Assets/Scenes/SampleScene.unity`), meant for a VR game. README.md
describes what it does and the measured results; read it first. This file is for coding agents (Codex reads it;
CLAUDE.md imports it for Claude Code).

## Hard rules

- All actuation goes through torque-limited `ConfigurableJoint` drives. No root forces, no setting body positions
  or velocities (test fixtures that reset or lay the body down are the only exception).
- Torque caps: knee 280 N·m, hip 300 N·m. Do not raise them.
- Fixed timestep 0.01 s (`ProjectSettings/TimeManager.asset`). At 0.02 s the humanoid falls standing still.
- Values saved in the scene override code defaults: a changed default does nothing until the scene is updated.
- Never recompile while the editor is in Play mode: the domain reload leaves the humanoid's runtime arrays null.
- The humanoid code (`Assets/Walker/Humanoid`) must not reference Platform, Testing, Fencing or Props code.

## Judging a change

- The simulation is deterministic for a given build but chaotic across changes: a single run says little. Judge
  every change on several repeats, and on the regression set below, before keeping it.
- Analyse with numbers (bench traces, measured positions and angles), not screenshots. Add the quantity you need
  to SkillBench's trace. Use `Testing/Snapshots.cs` contact sheets only to check data that looks wrong.
- Write down what was tried and rejected, with the numbers (README "Results", `Assets/Walker/Platform/ARENA.md`,
  `Assets/Walker/Humanoid/RESEARCH.md`).

## Running the bench

- Headless (close the editor on this project first):
  `powershell -File Tools/bench.ps1 -Filter "walk 0.5 level|getup squat" -Label check -Repeats 2`
  Exit code 0 when every case passed. Report: `BalanceResults/skills-<label>.json`; traces
  `BalanceResults/skill-<case>.csv`. The runner is `Assets/Walker/Testing/Editor/HeadlessBench.cs`.
- From an open editor: `HeadlessBench.Start("label", "filter", repeats)` runs the same and stays open.
- Case names are in `SkillBench.Matrix()`: walk, jump, sword pickup, thibault lesson, thibault after pickup,
  getup front/back/left/right/squat/push. The ball tests are `RecoveryStepBench` (see ARENA.md).
- Regression set after any controller change: `walk 0.5 level|walk 0.9 level|sword pickup|thibault after pickup|getup squat`, 2-3 repeats.

## Layout

- `Humanoid/`: JointBalanceHumanoid (standing balance, joint ranges, get-up hook), HumanoidFootwork (steps, gait,
  jumps, placed steps), HumanoidArms (arm IK, grasp), HumanoidGetUp (pose sequences, planted-palm arm IK, rise).
- `Fencing/`: SwordHandling, ThibaultManual (circle, guard, S/N travel, imbrocade; TakeGuard + Attack).
- `Platform/`: arena, bumpy floor, SkillsDemo (keyboard demo, sparring on V).
- `Testing/`: SkillBench, RecoveryStepBench, probes.

## Open work (see README "Known limits")

- Get-up from face down: reaches the knee push-up and the fold onto the knees; raising the trunk and the
  half-kneel are not done. Candidate: optimise the pose parameters against the bench, or learn the get-up.
- Sparring: the second fencer (facing 180 degrees) falls in most bouts; check for facing assumptions.
- Thibault: the imbrocade's point and the guard at N miss the lesson's criteria. Running and forward jumps.
