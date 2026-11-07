# Whole-body standing balance

## Research and what is implemented

1. Yin, Loken, van de Panne (2007), **SIMBICON: Simple Biped Locomotion Control**, section 3.3, equation (1): multi-joint pose feedback from horizontal COM displacement and velocity. https://www.cs.ubc.ca/~van/papers/2007-siggraph-simbicon.pdf
   This implementation distributes feedback to hips, spine, neck, knees and shoulders, then converts desired segment orientations into relative joint targets. The torso target is world-referenced using the measured pelvis rotation, with the drive applying its equal-and-opposite reaction to the pelvis. It implements standing feedback, not the paper's walking state machine, swing-foot placement, or feedback-error learning. World orientation is realized through the spine joint here, not the paper's stance/swing hip torque distribution.
2. Pratt, Carff, Drakunov, Goswami (2006), **Capture Point: A Step toward Humanoid Push Recovery**. https://www.ambarish.com/paper/Pratt_Goswami_Humanoids2006.pdf
   The controller estimates the linear-pendulum capture point `c + v / sqrt(g/h)`. This is an approximate state estimate, not a guarantee that this articulated body can recover. Recovery stepping is described below.
3. Kim, Lim, Park, Park (2024 manuscript), **A Model Predictive Capture Point Control Framework for Robust Humanoid Balancing via Ankle, Hip, and Stepping Strategies**. https://arxiv.org/html/2307.13243v2
   Motivates coordinated upper-body angular momentum feedback. We measure centroidal angular momentum (spin plus orbital terms), and use bounded, filtered torso/arm pose feedback. This is NOT a reproduction of the paper's MPC/QP optimizer, dynamics constraints, or HQP momentum tracking. Gains and joint allocation are engineering adaptations for this Unity rig.

## Recovery stepping: what came from where

- Coros, Beaudoin, van de Panne (2010), **Generalized Biped Walking Control**. https://www.cs.ubc.ca/~van/papers/2010-TOG-gbwc/paper.pdf
  Step distance from the inverted pendulum, `d = v sqrt(h/g + v^2/(4g^2))`, per axis and recomputed every tick; a step-length cap with several steps for bigger pushes; the foot blended to its target over a fixed step time. Their virtual forces and gravity compensation are not used here.
- Yin, Loken, van de Panne (2007), **SIMBICON**. The stance hip holds the pelvis in world space while the swing hip tracks the foot in world space. Implemented as drive targets, not their torque formula.
- Hof (2008), **The extrapolated centre of mass concept**. Landing a fixed distance outside the extrapolated centre of mass, used here sideways (`lateralMargin`).
- Pratt et al. (2006), above. The capture point as the trigger.
- Kajita et al. (2010), **Biped Walking Stabilization Based on Linear Inverted Pendulum Tracking**. Foot force difference control: a small difference in leg length shares the load between the feet (`loadShareGain`). Used from memory of the method, with gains found on this rig.

These were learned on this rig rather than from the papers, each from a measured trace:

- A Slerp drive damps towards zero joint velocity, so a moving joint trails its target by damping/stiffness, 0.04 s here plus one physics step. On a swing leg that is about 10 cm of foot overshoot. The swing leg's targets lead by that time (`driveLead`).
- On links this light a stiff drive delivers far less than its stiffness suggests, because the solver limits it by the link's inertia. At the ankle, 5 degrees of error gives only about 40 N m. So a planted ankle is commanded by angle offset, calibrated from measured pressure under the foot (`anklePitchGain`, `ankleRollGain`). Running the drive at its cap does not work: PhysX clips each axis separately, so pitch torque turns into roll and yaw torque once the shin leans.
- A planted ankle has to be able to push. With no ankle torque the centre of pressure stays under the ankle, the ground force passes well behind a bent knee, and the knee cannot extend.
- The stance knee holds pelvis height, as in the linear inverted pendulum, rather than a fixed bend, and a foot lands only as far out as a nearly straight leg reaches. Bigger pushes then take more short steps.
- The foot nearest the capture point stays down. Lifting the other starts single support near where the body is falling, so the fall grows less during the step.
- In single support the stance hip has to hold everything above it. At 180 N m it could not: the pelvis rolled over by nearly 50 degrees, which read as a sinking pelvis and a folding knee. At 300 N m it holds to a few degrees. The trunk rides on the pelvis meanwhile, or righting it pushes the pelvis the wrong way.
- The stance hip must also keep the knee facing forward. Nothing else stops a bent knee swinging sideways like a door about the hip-ankle line, and the leg folds however strong the knee is (`kneeFacing`).
- Two stiff legs on the ground fight. Load jumps between the feet regardless of what balance needs, and a foot with little load slides. Three things fixed it: load sharing by leg length, dropping any press towards the other foot, and having a lightly loaded leg work from where the pelvis really is, with a fixed place for its foot (`LegPush`, `plantLoad`).
- A foot off the ground needs a fixed reference. The standing pose takes each leg's angle from where its foot is, which for a foot in the air is a runaway. Switching modes on load alone is a limit cycle that walks the foot across the floor, so it also checks the foot's height (`hangHeight`).
- Handing back to standing must not relax the legs' lean quickly, or carry over the ankle's torque target. Both throw the body.

Tried and not useful here: more solver iterations, the Temporal Gauss-Seidel solver, more foot friction, and limiting the swing hip's torque.

Measured results and known limits are in `Platform/ARENA.md`.

## Physical implementation

All production actuation is through ConfigurableJoint Slerp drives. Maximum motor torques are ankle 100, knee 280, hip 300, spine 180, neck 30, shoulder 60, elbow 40 N m. These are approximate adult-scale caps, not individualized biomechanical measurements. Passive joint constraints and contacts have separate solver reaction forces; the motor torque limits do not bound those passive forces.

No production root forces, transform corrections, kinematic bodies, body damping, or joint projection. Solver iterations are initialized on every load. Rigidbody transforms have unit scale. Self-collisions are disabled within the rig; collisions with the environment remain active. This is a simplified block humanoid, not an anatomically validated model.

## Validation

`BalanceValidation` is a temporary Play mode fixture. It tests quiet stance, forward/backward/side impulses, sustained force, gradual terrain roll, and a larger impulse. It compares ankle-only feedback against whole-body feedback. It resets the bodies only BETWEEN trials; recovery uses ordinary FixedUpdate physics and the production joint controller. Results are written to `BalanceResults/validation.json` at the project root. Do not leave this fixture attached for normal use.

Inspector diagnostics include capture point, centroidal angular momentum, balance state, and hip/arm target angles. Motor saturation and finite foot contact make unlimited push recovery physically impossible.

## Measured results (2026-11-06, before the torque limits were raised)

Unity 6000.6.8f1, 0.01 s physics timestep, 20 position / 10 velocity solver iterations, normal FixedUpdate simulation. Four-times time scale accelerates wall-clock execution only. Each trial lasts 12 simulated seconds; pushes begin at 2 s. Pass requires pelvis height above 0.75 m throughout and final pelvis speed below 0.15 m/s. Test resets occur between scenarios only.

| Scenario | Ankle-only | Whole-body |
|---|---|---|
| Quiet stance | Pass | Pass |
| Forward impulse, 10 N s | Pass | Pass |
| Backward impulse, 10 N s | Pass | Pass |
| Side impulse, 10 N s | Pass | Pass |
| Forward force, 15 N for 5 s | Pass | Pass |
| Ground roll, 0 to 3 degrees over 2 s | Pass | Pass |
| Forward impulse, 25 N s | Fall | Pass |

In the 25 N s trial, whole-body minimum pelvis height was about 0.99 m, versus 0.11 m for ankle-only. Whole-body peak trunk tilt was 10.21 degrees; hip and arm command peaks were 6.59 and 9.85 degrees. These are single-run comparisons, not statistical robustness guarantees. The whole-body variant also uses three-times the baseline ankle feedback gains; the comparison measures the complete controller, not an isolated contribution of the arms.

Terrain sensing currently uses downward foot rays and an averaged normal. It does not estimate a full support polygon, contact pressure, friction cones, or independent uneven-foot IK. These are remaining limitations, as are simplified symmetric joint limits and the absence of a get-up behaviour.
