using UnityEngine;

// Produces joint pose targets only. Never moves a body or applies a root force.
//
// Push recovery by stepping, after Coros, Beaudoin & van de Panne, "Generalized Biped Walking Control"
// (SIGGRAPH 2010), SIMBICON (Yin et al. 2007), capture-point stepping (Pratt et al. 2006), the
// extrapolated centre of mass (Hof 2008) and foot force difference control (Kajita et al. 2010):
//   Stand          The standing feedback in JointBalanceHumanoid handles the disturbance, for as
//                  long as the capture point xi = c + v/omega stays within what it can manage.
//   Prepare        Brief shift towards the stance foot, which limits the sideways fall that
//                  single support would otherwise start.
//   Swing          The foot nearer xi stays down. The other blends over a fixed step time to wherever
//                  the body would come to rest, limited to what a nearly straight leg can reach, and
//                  recomputed every tick. Stance leg: a knee that holds pelvis height, a hip that
//                  holds the pelvis level and the knee facing forward, an ankle that places the
//                  centre of pressure.
//   DoubleSupport  A moment for the new foot to take load. While the stance is too long or xi is
//                  past the support, the next foot steps at once, using the body's own momentum to
//                  carry it over the leading foot. A push too big for one step takes several short
//                  ones.
//   Hold           The legs position the pelvis directly from wherever the feet are planted. The
//                  ankles place the centre of pressure on each foot and the difference in leg length
//                  shares the load between them, which brings the body to rest in any stance.
//   WeightShift    From rest, moves the capture point over one foot, so that a closing step can bring
//                  the other one back to a normal stance. Then the standing feedback takes over.
public class HumanoidFootwork : MonoBehaviour
{
    public enum Phase { Stand, Prepare, Swing, DoubleSupport, Hold, WeightShift, JumpCrouch, JumpPush, Flight }

    const float FootHalfWidth=.075f, FootHalfLength=.14f, FootRestHeight=.065f;
    const float HipHalfWidth=.14f, HipDrop=.07f, StandingPelvis=1.005f;
    const float MaxLeg=.797f, ReachLeg=.78f, MinLoadedLeg=.72f, MinStanceLeg=.68f, MinSwingLeg=.42f;
    const float SettleTime=.25f;
    const float NominalWidth=.28f, MinFootGap=.20f, FallenHeight=.55f, FallenTrunk=50f;
    static readonly Vector3 AnkleFromFoot=new Vector3(0,.075f,-.055f);

    [Header("Stance")]
    public bool recoverySteps = true;
    [Range(.015f,.10f)] public float standingCompression = 0.03f;
    [Header("Recovery step")]
    [Tooltip("Seconds from lift-off to the planned touchdown.")]
    [Range(.15f,.6f)] public float stepTime = .24f;
    [Range(.03f,.18f)] public float stepClearance = .07f;
    [Tooltip("Seconds the swing leg's joint targets lead the plan, to cancel the drive's lag. The drive's damping over its stiffness, plus one physics step.")]
    [Range(0,.1f)] public float driveLead = .05f;
    [Tooltip("Longest step, measured from where the foot started.")]
    [Range(.15f,1f)] public float maximumStep = .8f;
    [Tooltip("From a normal stance, step when the capture point is this far ahead of the feet. The standing feedback is not equally strong in every direction, so each limit is set from what it survives without stepping (about 20 N s forward, 30 back, 40 sideways).")]
    [Range(.04f,.3f)] public float stepAhead = .11f;
    [Range(.04f,.3f)] public float stepBehind = .15f;
    [Range(.04f,.3f)] public float stepSideways = .2f;
    [Tooltip("Treat the capture point as near the edge of the support polygon within this distance.")]
    [Range(0,.12f)] public float triggerMargin = .02f;
    [Tooltip("Land this far past where the body would come to rest.")]
    [Range(0,.2f)] public float landingOvershoot = 0f;
    [Tooltip("Land this far outside the capture point, on the swing foot's side.")]
    [Range(0,.25f)] public float lateralMargin = .1f;
    [Tooltip("Before a forward or backward step, shift towards the stance foot until the capture point is this close to it sideways.")]
    [Range(.02f,.14f)] public float shiftLift = .09f;
    [Tooltip("Longest time to spend on that shift before lifting anyway. The fall in the step direction does not wait.")]
    [Range(0,.25f)] public float shiftTime = .10f;
    [Tooltip("Degrees per second the standing pose relaxes from where the legs landed to its centred stance. Too fast and the front knee, extending at its torque cap, shoves the body backward.")]
    [Range(2,120)] public float standingRelax = 10f;
    [Tooltip("The same, once the feet are close to a normal stance.")]
    [Range(2,240)] public float nearRelax = 80f;
    [Tooltip("Seconds after touchdown before deciding what happens next.")]
    [Range(.01f,.4f)] public float landingHold = .03f;
    [Tooltip("Contact before this much of the step is a scuff, not a landing, unless it carries real load.")]
    [Range(.3f,1)] public float touchdownProgress = .75f;
    [Tooltip("Load in newtons that ends a step early. A rear foot dragging on its way out carries a few hundred, and is not a landing.")]
    [Range(50,1500)] public float scuffLoad = 1000f;
    [Tooltip("Shape of the swing height: 1 is symmetric, lower peaks earlier and comes down sooner.")]
    [Range(.4f,1.5f)] public float swingShape = 1f;
    [Tooltip("After landing, step again only once the capture point is this far outside the support polygon.")]
    [Range(0,.15f)] public float stepAgainMargin = .05f;
    [Tooltip("After landing, bring the trailing foot up as soon as the capture point is within this distance of the leading foot. The body's own momentum then carries it over that foot. Waiting until it has stopped between the feet leaves a stance it cannot get out of.")]
    [Range(0,.3f)] public float closeMargin = 0.05f;
    [Tooltip("A follow-up step only closes up beside the stance foot if the capture point is within this distance of its support. Otherwise it steps to catch the body.")]
    [Range(-.1f,.15f)] public float capturedMargin = -0.03f;
    [Tooltip("The trailing foot lifts once the new foot has carried this many newtons for leadLoadTime seconds, or after leadLoadLimit seconds anyway.")]
    [Range(50,1000)] public float leadLoad = 300f;
    [Range(0,.1f)] public float leadLoadTime = .03f;
    [Range(0,.3f)] public float leadLoadLimit = .12f;
    [Tooltip("A foot carrying less than this many newtons for hangTime seconds goes back to where it was planted.")]
    [Range(0,200)] public float hangLoad = 60f;
    [Range(.01f,.3f)] public float hangTime = .03f;
    [Tooltip("Metres above its resting height that a foot counts as off the ground.")]
    [Range(.002f,.06f)] public float hangHeight = .02f;
    [Tooltip("Where a closing step centres the stance, from the centre of mass (0) to the capture point (1).")]
    [Range(0,1)] public float closeBlend = .6f;
    [Tooltip("Longest to wait for that, in seconds, before the standing feedback takes back over.")]
    [Range(.03f,.5f)] public float closeWait = .2f;
    [Tooltip("How far from the hip a foot lands, as a share of what a straight leg reaches at the present height. A leg landing straight takes load through the knee's limit; a bent one has to hold it with torque, and folds. Bigger pushes take more short steps, not one long one.")]
    [Range(.4f,1.4f)] public float landingReach = .85f;
    [Tooltip("A moving body carries the foot past its target by about this many seconds of travel, so aim that much short.")]
    [Range(0,.25f)] public float footCarry = .05f;
    [Tooltip("The same, when stepping straight backward, as a share of landingReach.")]
    [Range(.3f,1.2f)] public float backReach = .75f;
    [Tooltip("Keep the foot nearest the capture point on the ground and move the other one, so that single support does not begin far from where the body is falling.")]
    public bool stanceDownwind = true;
    [Tooltip("Pelvis height the stance leg holds while stepping, above the ground under that foot.")]
    [Range(.85f,1.01f)] public float stanceHeight = .96f;
    [Tooltip("Seconds of the pelvis's vertical speed taken off the height asked of the legs in double support.")]
    [Range(0,.3f)] public float heightDamping = .05f;
    [Tooltip("Metres per second the pelvis may rise in double support. Too fast launches the body off its feet.")]
    [Range(.05f,1)] public float riseRate = .3f;
    [Header("Stance leg while stepping")]
    [Tooltip("Straightest the stance knee is asked to go, in degrees.")]
    [Range(2,45)] public float stanceKnee = 6f;
    [Tooltip("How fast a loaded knee's target moves, in degrees per second.")]
    [Range(10,600)] public float kneeRate = 240f;
    [Tooltip("How hard the ankles place the centre of pressure beyond the capture point, to drive it back to where it should be.")]
    [Range(0,5)] public float copGain = 1f;
    [Tooltip("Torque limit for holding a planted foot level, as a multiple of what its load can react at the wanted centre of pressure. The ankle sits high above the sole, so sideways ground force rolls the foot unless this is above 1.")]
    [Range(.5f,6)] public float ankleAuthority = 2;
    [Tooltip("Share of the centre of pressure wanted that the ankle is asked for.")]
    [Range(0,1.5f)] public float copAuthority = 1f;
    [Tooltip("Torque per degree of tilt that keeps a planted foot level.")]
    [Range(0,40)] public float ankleLevelling = 6f;
    [Tooltip("Share of the way to the heel the ankle may place the centre of pressure.")]
    [Range(0,1)] public float heelPress = 0.2f;
    [Tooltip("Torque per degree holding the shin's heading to the foot's, in N m.")]
    [Range(0,20)] public float shinHeadingHold = 3f;
    [Tooltip("How much of the sideways swing of a bent stance knee the hip corrects.")]
    [Range(0,1)] public float kneeFacing = 0.75f;
    [Tooltip("In single support, how hard the ankle drives the capture point towards the stance foot.")]
    [Range(0,3)] public float singleSupportGain = .5f;
    [Tooltip("Torque the planted ankle delivers per radian of target offset, in N m.")]
    [Range(20,2000)] public float anklePitchGain = 300;
    [Range(20,2000)] public float ankleRollGain = 70;
    [Tooltip("Ankle damping while stepping. The standing value is high enough to tip the foot.")]
    [Range(0,600)] public float stanceAnkleDamping = 40f;
    [Header("Holding a stance after stepping")]
    [Tooltip("Longest to stay in the pelvis-controlled hold before trying the next thing, in seconds.")]
    [Range(.2f,5)] public float holdLimit = 2.5f;
    [Tooltip("The capture point counts as settled within this distance of the middle of the feet.")]
    [Range(.01f,.4f)] public float holdCentre = .3f;
    [Tooltip("The body counts as settled below this speed, in metres per second.")]
    [Range(.01f,.4f)] public float holdSpeed = .1f;
    [Tooltip("Stance width and spread that count as back to normal, in metres.")]
    [Range(.1f,.3f)] public float normalMinWidth = .2f;
    [Range(.3f,.6f)] public float normalMaxWidth = .38f;
    [Range(.05f,.25f)] public float normalSpread = .12f;
    [Tooltip("Time constant of the load each ankle works from, in seconds.")]
    [Range(.005f,.2f)] public float loadFilter = .03f;
    [Tooltip("Load in newtons above which a foot's position is taken as where it is planted, and which it needs to take the full pelvis command.")]
    [Range(50,700)] public float plantLoad = 250f;
    [Tooltip("Load in newtons below which a leg takes none of the pelvis command.")]
    [Range(0,300)] public float pushLoad = 60f;
    [Tooltip("Foot force difference control (Kajita et al. 2010): metres per second of leg length difference per newton of error in how the load is shared. Two stiff legs on the ground otherwise fight, with the load jumping between them regardless of what balance needs.")]
    [Range(0,.005f)] public float loadShareGain = .0003f;
    [Tooltip("Seconds for that length difference to leak away.")]
    [Range(.05f,2)] public float loadShareLeak = .3f;
    [Range(0,.1f)] public float loadShareLimit = .04f;
    [Tooltip("Smallest share of the load either foot is asked to keep.")]
    [Range(0,.45f)] public float loadShareMargin = .15f;
    [Header("Pelvis servo while shifting weight")]
    [Range(0,1)] public float servoGain = .35f;
    [Range(0,.2f)] public float servoLimit = .06f;
    [Header("Closing step")]
    [Tooltip("Bring a stance that is not normal back together once the body has settled.")]
    public bool closingSteps = true;
    [Tooltip("Pace of the closing step, relative to a recovery step.")]
    [Range(.1f,1f)] public float closingPace = .5f;
    [Tooltip("Seconds settled before closing the stance.")]
    [Range(.02f,1f)] public float quietTime = .2f;
    [Tooltip("How gently to shift over the stay foot before a closing step, relative to the recovery servo.")]
    [Range(.05f,1f)] public float closingShift = .5f;
    [Tooltip("Furthest ahead or behind the stay foot a closing step lands, in metres.")]
    [Range(0,.2f)] public float closeSpread = .1f;
    [Tooltip("Lift the closing foot once the capture point is this far inside the stay foot's support (negative) or outside it (positive).")]
    [Range(-.08f,.15f)] public float liftMargin = -.03f;
    [Header("Uneven ground")]
    [Tooltip("Lift the swing foot this far over higher ground ahead of it (m). 0 turns this off.")]
    [Range(0,.15f)] public float pathClearance = .03f;
    [Tooltip("Over this fraction of the swing, bring the pelvis to where the leg can reach a lower landing. 0 turns this off.")]
    [Range(0,1)] public float stepDownLead = .7f;
    [Tooltip("Swing clearance used instead of stepClearance where the ground is uneven: a step between the feet, a tilted slab, or a step under the stance foot. 0 turns this off.")]
    [Range(0,.25f)] public float roughClearance = .13f;
    [Header("Jumping")]
    [Tooltip("How high a jump lifts the centre of mass above where it leaves the ground (m).")]
    [Range(.02f,.4f)] public float jumpHeight = .12f;
    [Tooltip("How far the pelvis comes down before the push (m).")]
    [Range(0,.25f)] public float jumpCrouch = .12f;
    [Range(.1f,1)] public float jumpCrouchTime = .35f;
    [Tooltip("Shortest hip-to-ankle distance while crouching and pushing (m); standing stepping keeps 0.68.")]
    [Range(.45f,.75f)] public float jumpShortestLeg = .58f;
    [Tooltip("How far ahead of the feet the body leans before a moving jump, per m/s of take-off speed (s).")]
    [Range(0,.4f)] public float jumpLean = .3f;
    [Tooltip("How far ahead of the pelvis the push leads it, per m/s of speed wanted and of speed lacking (s).")]
    [Range(0,.3f)] public float jumpLeadTime = .06f;
    [Tooltip("How far behind the centre of mass the push puts the centre of pressure per m/s the body is slower than its take-off speed (s).")]
    [Range(0,.5f)] public float jumpSpeedGain = .1f;
    [Tooltip("How quickly the push takes back the body's spin, by where it puts the centre of pressure (1/s).")]
    [Range(0,40)] public float jumpSpinGain = 10;
    [Tooltip("Hip-to-ankle distance a leg reaches for the ground with in the air: a little under straight, ready to give (m).")]
    [Range(.6f,.8f)] public float jumpLandLeg = .74f;
    [Tooltip("How far ahead of where the body comes down the feet land, as a share of v/omega.")]
    [Range(0,1.2f)] public float jumpLandLead = .5f;
    [Tooltip("How far the pelvis is let down on landing (m).")]
    [Range(0,.2f)] public float jumpLandAbsorb = .06f;
    [Tooltip("Total foot load under which the feet have left the ground, and over which a foot has come down (N).")]
    [Range(5,200)] public float jumpContactLoad = 40;
    public int jumpsMade, jumpsLanded;
    // Jump with this horizontal take-off velocity (heading frame, m/s) as soon as both feet are down.
    public void Jump(Vector3 velocity) { jumpRequested=true; jumpVelocity=new Vector3(velocity.x,0,velocity.z); }
    public bool JumpPending => jumpRequested;
    bool jumpRequested, airborne;
    Vector3 jumpVelocity, jumpFrom, jumpPushFrom;
    float jumpPushTime, jumpPushRise;
    [Header("Gait")]
    [Tooltip("Walking velocity wanted, in the heading frame (x to the right, z forward), m/s. Zero stands still; the recovery steps then bring the body to rest.")]
    public Vector3 gaitVelocity;
    [Tooltip("Duration of a walking step (s).")]
    [Range(.2f,.8f)] public float gaitStepTime = .38f;
    [Tooltip("Distance between the two feet's tracks (m).")]
    [Range(.1f,.4f)] public float gaitWidth = .22f;
    [Tooltip("Shortest time both feet stay down between walking steps (s).")]
    [Range(0,.2f)] public float gaitDoubleSupport = .04f;
    [Tooltip("Longest the lean before the first walking step may take (s).")]
    [Range(.1f,1.5f)] public float gaitLean = .6f;
    [Tooltip("How quickly the walking velocity actually used follows gaitVelocity (m/s per second).")]
    [Range(.1f,5)] public float gaitAcceleration = .8f;
    [Tooltip("Swing clearance of a walking step, over level or uneven ground alike: pathClearance lifts it over raised slabs on the way (m).")]
    [Range(.02f,.2f)] public float gaitClearance = .06f;
    [Tooltip("Shape of a walking step's swing height (see swingShape).")]
    [Range(.4f,1.5f)] public float gaitSwingShape = .7f;
    [Tooltip("How far the stance ankle moves the centre of pressure per metre of capture point error from the step's planned path (0 turns it off).")]
    [Range(0,4)] public float gaitCaptureGain = 1.5f;
    [Tooltip("Most the stance knee bends while walking (degrees).")]
    [Range(6,60)] public float gaitKneeLimit = 22f;
    [Tooltip("How much lower a walking step can land without the body coming down for it (m).")]
    [Range(0,.15f)] public float gaitStepAbsorb = .05f;
    [Tooltip("A walking step moves its landing off a slab edge when the ground under the footprint differs by more than this (m).")]
    [Range(.005f,.1f)] public float edgeSpread = .02f;
    [Tooltip("Longest walking step, from the stance foot forward (m).")]
    [Range(.2f,1.2f)] public float gaitMaximumStep = .55f;
    [Tooltip("How much longer the longest step gets per m/s the body is faster than commanded (s).")]
    [Range(0,2)] public float gaitBrakeStep = .5f;
    [Tooltip("Longest distance a walking swing may carry the foot (m).")]
    [Range(.4f,1.4f)] public float gaitMaximumSwing = 1f;
    [Tooltip("How far the stance ankle moves the centre of pressure per m/s of speed error while walking (m per m/s).")]
    [Range(0,.5f)] public float gaitSpeedGain = .15f;
    // The velocity in use, following gaitVelocity at gaitAcceleration.
    public Vector3 GaitCommand => gaitCommand;
    public Vector3 GaitPressureWanted => gaitPressureWanted;
    Vector3 gaitCommand, gaitPressureWanted;
    [Header("Diagnostics")]
    public Phase phase;
    public string state = "Standing";
    public int steppingFoot = -1, stepsStarted, stepsLanded, stepsCancelled, closingStepsTaken;
    public float leftKneeDegrees, rightKneeDegrees, settling;
    public Vector3 landingTarget, supportPoint, capture;
    public float supportMargin;

    // One per joint motor; sized in GoToStand, since hands and split feet add motors at Awake.
    Quaternion[] landingPose = new Quaternion[12];
    public readonly Quaternion[] thighs = new Quaternion[2], shins = new Quaternion[2], soles = new Quaternion[2];
    readonly Vector3[] targets = new Vector3[2], normals = new Vector3[2];
    readonly float[] ground = new float[2], unloaded = new float[2];
    readonly Quaternion[] ankleTargets = new Quaternion[2];
    readonly float[] ankleTorque = new float[2], smoothLoad = new float[2];
    readonly FootContactSensor[] sensors = new FootContactSensor[2];
    readonly Rigidbody[] shinBodies = new Rigidbody[2], thighBodies = new Rigidbody[2];
    readonly float[] kneeTargets = new float[2];
    readonly Vector3[] planted = new Vector3[2];
    readonly bool[] bearing = new bool[2], hanging = new bool[2];
    readonly float[] kneeOffset = new float[2], pitchOffset = new float[2], rollOffset = new float[2];
    bool bumpless, steppedSinceNominal;
    static readonly Vector2[] corners = new Vector2[8], hull = new Vector2[16];
    bool requested, voluntary;
    float loadDifference, legDifference, leadLoaded, timer, cooldown, compression, omega = 3f, startHeight, pelvisHeight, quiet, totalMass;
    int lastFoot, unsafeTicks, beyondLimit, stayFoot, preparingFoot, sequence;
    Vector3 swingStart;

    [ContextMenu("Request recovery step (Play mode)")]
    public void RequestRecoveryStep() { if(Application.isPlaying) requested=true; }
    // Put one foot down at a place (world position, the foot's middle), unhurried: the weight goes onto the
    // other foot first, as for a closing step. For footwork such as a fencing manual's.
    public void StepTo(int foot, Vector3 place) { placedFoot=foot; placedTarget=place; placedPending=true; }
    public bool PlacedStepPending => placedPending || phase==Phase.WeightShift || IsSwinging && voluntary;
    void PlacedStep(JointBalanceHumanoid h, Quaternion heading, Vector3 right)
    {
        int foot=placedFoot;
        BeginStep(h,heading,right,foot,true);
        Vector3 place=placedTarget;
        if(FindFoothold(place,heading,1-foot,out var spot)) place=spot;
        landingTarget=place;
        PlacedLift(foot,place,out placedGoalPoint);
        placedPending=false; placedSteps++; placingStep=true;
    }
    public int placedSteps;
    int placedFoot; Vector3 placedTarget, placedGoalPoint; bool placedPending, placingStep, placedLanding;
    float placedClosest, placedStalled;
    Vector3 placedPush;
    public Vector3 PlacedPush => placedPush;
    float PlacedDuration => stepTime/placedPace;
    // Where xi should be when `foot` lifts for a step to `place`, and where it should be at touchdown.
    Vector3 PlacedLift(int foot, Vector3 place, out Vector3 goal)
    {
        Vector3 p=Vector3.ProjectOnPlane(targets[1-foot],Vector3.up);
        goal=Vector3.Lerp(p,Vector3.ProjectOnPlane(place,Vector3.up),placedGoal);
        return p+(goal-p)*Mathf.Exp(-omega*PlacedDuration);
    }
    // During a placed swing: the centre of pressure that brings xi to the goal by the end of the swing.
    // From xi = p + (xi0 - p) e^(omega tau): p = (xi e^(omega tau) - goal) / (e^(omega tau) - 1), which the
    // foot then limits. Late in the swing tau is held at a floor, or the gain grows without bound.
    Vector3 PlacedPressure()
    {
        float e=Mathf.Exp(omega*Mathf.Max(.1f,PlacedDuration-timer));
        return (Vector3.ProjectOnPlane(capture,Vector3.up)*e-placedGoalPoint)/(e-1);
    }

    void Start()
    {
        var h=GetComponent<JointBalanceHumanoid>();
        foreach(var body in h.bodies) totalMass+=body.mass;
        for(int i=0;i<2;i++)
        {
            sensors[i]=h.feet[i].gameObject.AddComponent<FootContactSensor>();
            // A split foot's front part is a body of its own, with its own contacts. It is part of the same foot.
            if(h.fronts!=null) frontSensors[i]=h.fronts[i].gameObject.AddComponent<FootContactSensor>();
            shinBodies[i]=h.feet[i].GetComponent<ConfigurableJoint>().connectedBody;
            thighBodies[i]=shinBodies[i].GetComponent<ConfigurableJoint>().connectedBody;
        }
    }
    readonly FootContactSensor[] frontSensors = new FootContactSensor[2];
    public bool Contact(int foot) => sensors[foot] && sensors[foot].touching || frontSensors[foot] && frontSensors[foot].touching;
    public float Load(int foot) => (sensors[foot] ? sensors[foot].load : 0)+(frontSensors[foot] ? frontSensors[foot].load : 0);
    public Vector3 Pressure(int foot)
    {
        if(!sensors[foot]) return Vector3.zero;
        var front=frontSensors[foot];
        if(!front || front.load<=0) return sensors[foot].pressure;
        float rear=sensors[foot].load;
        return rear<=0?front.pressure:(sensors[foot].pressure*rear+front.pressure*front.load)/(rear+front.load);
    }
    public Vector3 Target(int foot) => targets[foot];
    public float GroundUnder(int foot) => ground[foot];
    public float SwingProgress => IsSwinging?Mathf.Clamp01(timer/stepTime):0;
    // How far apart the highest and lowest ground under the footprint are: large when it straddles a step.
    public float FootSpread(int foot) => footSpread[foot];
    float lastSpread;
    readonly float[] footSpread = new float[2];
    public float PelvisCommand => pelvisHeight;
    public bool IsSwinging => phase==Phase.Swing && steppingFoot>=0;
    // In single support the leg on the ground holds the pelvis level from its hip, rather than
    // tracking a pose: a knee that holds height, and a hip that reacts against the actual thigh.
    public bool HoldsPelvis => phase==Phase.Swing;
    // A leg off the ground tracks a foot target from the actual pelvis, like a swing leg.
    // That includes a foot only lightly on the ground: a leg holding the pelvis has no target of
    // its own, so without real load on it there is nothing to stop the foot sliding away.
    public bool LegIsFree(int leg) => (IsSwinging && leg==steppingFoot) || phase==Phase.Flight || (phase==Phase.DoubleSupport && !bearing[leg]) || ((phase==Phase.Stand || phase==Phase.Hold) && hanging[leg]);
    public bool Jumping => phase==Phase.JumpCrouch || phase==Phase.JumpPush || phase==Phase.Flight;
    float KneeBend(int leg) => Vector3.Angle(thighBodies[leg].rotation*Vector3.up,shinBodies[leg].rotation*Vector3.up);
    // True whenever the step controller, not the standing feedback, owns the posture.
    public bool IsStepping => phase!=Phase.Stand;
    // Outside the standing law the ankles place the centre of pressure directly. As position servos
    // on so light a link they are too weak: even 5 degrees of error gives only about 40 N m.
    public bool AnklesAreSoft => phase!=Phase.Stand;
    // World-space target for a planted foot while stepping: see PlaceCentreOfPressure.
    public Quaternion AnkleTarget(int leg) => ankleTargets[leg];
    public float AnkleTorque(int leg) => ankleTorque[leg];
    // The stance hip holds the pelvis and has no say over the thigh, and the hinge at the knee cannot
    // stop the whole leg turning about the hip-ankle line. A bent knee then swings sideways like a
    // door and the leg folds, however strong the knee. So the hip also turns the knee back to the front.
    public Quaternion StanceThigh(JointBalanceHumanoid h, int leg, Vector3 forward)
    {
        Quaternion thigh=thighBodies[leg].rotation;
        if(kneeFacing<=0) return thigh;
        Vector3 hip=ActualHip(h,leg), knee=thighBodies[leg].position+thigh*Vector3.down*.2f;
        Vector3 ankle=shinBodies[leg].position+shinBodies[leg].rotation*Vector3.down*.2f;
        Vector3 axis=(ankle-hip).normalized;
        Vector3 out_=Vector3.ProjectOnPlane(knee-hip,axis);
        if(out_.sqrMagnitude<.0004f) return thigh;
        float turn=Vector3.SignedAngle(out_,Vector3.ProjectOnPlane(forward,axis),axis);
        return Quaternion.AngleAxis(Mathf.Clamp(turn*kneeFacing,-30,30),axis)*thigh;
    }
    // How far a planted leg can act on the body, from what it carries. One carrying little moves its
    // foot instead of the body: it works from where the pelvis really is, not where it should be.
    public float LegPush(int leg) => phase==Phase.Stand?1:Mathf.Clamp01((smoothLoad[leg]-pushLoad)/Mathf.Max(1,plantLoad-pushLoad));
    bool Ground(Vector3 p, Quaternion heading, out float y, out Vector3 n)
    {
        y=float.NegativeInfinity; n=Vector3.zero; int count=0; float low=float.PositiveInfinity;
        // Sample the footprint, not just the centre: a heel or toe can straddle a slab edge.
        for(int i=0;i<5;i++)
        {
            Vector3 offset=i==0?Vector3.zero:new Vector3((i==1||i==2?-.055f:.055f),0,(i%2==0?.105f:-.105f));
            if(Physics.Raycast(p+heading*offset+Vector3.up*.45f,Vector3.down,out var hit,1.1f,1,QueryTriggerInteraction.Ignore) && hit.normal.y>.45f)
            { y=Mathf.Max(y,hit.point.y); low=Mathf.Min(low,hit.point.y); n+=hit.normal; count++; }
        }
        // A ray down the gap between two slabs reaches the floor far below: that is not a step under the foot.
        lastSpread=count==0 || y-low>.28f?0:y-low;
        if(count==0) { y=p.y-.06f; n=Vector3.up; return false; }
        n.Normalize(); return true;
    }

    // Signed distance from a point to the convex hull of the chosen footprints; positive is outside.
    float SupportDistance(JointBalanceHumanoid h, Vector3 point, Quaternion heading, bool useLeft, bool useRight)
    {
        int n=0;
        for(int f=0;f<2;f++)
        {
            if(f==0?!useLeft:!useRight) continue;
            Vector3 c=h.feet[f].position;
            for(int sx=-1;sx<=1;sx+=2) for(int sz=-1;sz<=1;sz+=2)
            {
                Vector3 w=c+heading*new Vector3(sx*FootHalfWidth,0,sz*FootHalfLength);
                corners[n++]=new Vector2(w.x,w.z);
            }
        }
        for(int i=1;i<n;i++)
        {
            var key=corners[i]; int j=i-1;
            while(j>=0 && (corners[j].x>key.x || (corners[j].x==key.x && corners[j].y>key.y))) { corners[j+1]=corners[j]; j--; }
            corners[j+1]=key;
        }
        int k=0;
        for(int i=0;i<n;i++)
        {
            while(k>=2 && Cross(hull[k-2],hull[k-1],corners[i])<=0) k--;
            hull[k++]=corners[i];
        }
        for(int i=n-2,lower=k+1;i>=0;i--)
        {
            while(k>=lower && Cross(hull[k-2],hull[k-1],corners[i])<=0) k--;
            hull[k++]=corners[i];
        }
        Vector2 p=new Vector2(point.x,point.z);
        float distance=float.NegativeInfinity;
        for(int i=0;i<k-1;i++)
        {
            Vector2 e=(hull[i+1]-hull[i]).normalized;
            distance=Mathf.Max(distance,Vector2.Dot(new Vector2(e.y,-e.x),p-hull[i]));
        }
        return distance;
    }
    static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x-o.x)*(b.y-o.y)-(a.y-o.y)*(b.x-o.x);

    Vector3 ActualHip(JointBalanceHumanoid h, int leg) =>
        h.pelvis.position+h.pelvis.rotation*new Vector3(leg==0?-HipHalfWidth:HipHalfWidth,-HipDrop,0);

    // Where the body would come to rest over this foot if the step landed now. Inverted pendulum
    // energy balance, d = v*sqrt(h/g + v^2/(4g^2)), applied separately in each horizontal axis.
    // Recomputed every tick rather than predicted ahead, so it is right when the foot arrives.
    Vector3 PlanLanding(JointBalanceHumanoid h, int foot, Quaternion heading, Vector3 right, float remaining)
    {
        int stance=1-foot;
        Vector3 forward=heading*Vector3.forward, stanceCentre=h.feet[stance].position;
        Vector3 flatVelocity=Vector3.ProjectOnPlane(h.centerOfMassVelocity,Vector3.up);
        float g=Physics.gravity.magnitude, height=Mathf.Max(.5f,h.centerOfMass.y-ground[stance]);
        float sideways=Vector3.Dot(flatVelocity,right), ahead=Vector3.Dot(flatVelocity,forward);
        Vector3 offset=right*(sideways*Mathf.Sqrt(height/g+sideways*sideways/(4*g*g)))
            +forward*(ahead*Mathf.Sqrt(height/g+ahead*ahead/(4*g*g)));
        if(flatVelocity.sqrMagnitude>.0025f) offset+=flatVelocity.normalized*landingOvershoot;
        // Lifting a foot starts a sideways fall towards it, so land a fixed distance outside the
        // extrapolated centre of mass on that side (Hof 2008), which leaves xi between the feet.
        float sign=foot==0?-1:1;
        offset+=right*(sign*lateralMargin);
        Vector3 comOnGround=Vector3.ProjectOnPlane(h.centerOfMass,Vector3.up);
        Vector3 goal=comOnGround+offset;
        // Reach is set by the leg, from where the hip will be on touchdown: a foot that lands within
        // it does so on a nearly straight knee, and whatever is left over is for the next step.
        Vector3 hip=ActualHip(h,foot)+flatVelocity*(remaining+.03f);
        Vector3 ankleOffset=heading*AnkleFromFoot;
        float vertical=Mathf.Max(.60f,hip.y-(ground[stance]+FootRestHeight+AnkleFromFoot.y));
        // Going backward the swing hip's sweep pitches the pelvis back, and the knee bends the wrong
        // way for it, so short quick steps do better than long ones.
        float backward=Mathf.Clamp01(-Vector3.Dot(flatVelocity,forward)/Mathf.Max(.05f,flatVelocity.magnitude));
        float reach=Mathf.Sqrt(Mathf.Max(.0025f,ReachLeg*ReachLeg-vertical*vertical))*landingReach*Mathf.Lerp(1,backReach,backward);
        Vector3 fromHip=Vector3.ClampMagnitude(Vector3.ProjectOnPlane(goal+ankleOffset-hip,Vector3.up),reach);
        goal=Vector3.ProjectOnPlane(hip,Vector3.up)+fromHip-Vector3.ProjectOnPlane(ankleOffset,Vector3.up);
        goal=swingStart+Vector3.ClampMagnitude(Vector3.ProjectOnPlane(goal-swingStart,Vector3.up),maximumStep);
        // A foot landing in line with the other leaves a tightrope stance. Unless the body is already
        // falling past the stance foot, when getting under it matters more than anything else.
        // The leg is carried along with the body while it comes to rest on the target.
        goal-=flatVelocity*footCarry;
        float pastStance=-Vector3.Dot(capture-stanceCentre,right)*sign;
        float gap=Vector3.Dot(goal-stanceCentre,right)*sign;
        if(pastStance<FootHalfWidth && gap<MinFootGap) goal+=right*sign*(MinFootGap-gap);
        goal.y=ground[stance]+FootRestHeight;
        return goal;
    }

    // The capture point is over the stance foot, so this step only has to finish the job: land where
    // the capture point ends up centred between the feet, in something close to a normal stance.
    Vector3 PlanClosing(JointBalanceHumanoid h, int foot, Vector3 forward, Vector3 right, int stance)
    {
        float sign=foot==0?-1:1;
        // Both feet brake the body once down, so it stops short of xi. Centre on where it will stop.
        Vector3 rest=Vector3.Lerp(h.centerOfMass,capture,closeBlend);
        Vector3 toCapture=Vector3.ProjectOnPlane(rest-targets[stance],Vector3.up)*2;
        float across=Mathf.Clamp(Vector3.Dot(toCapture,right)*sign,MinFootGap,NominalWidth+.08f)*sign;
        float along=Mathf.Clamp(Vector3.Dot(toCapture,forward),-.12f,.12f);
        Vector3 goal=targets[stance]+right*across+forward*along;
        goal.y=ground[stance]+FootRestHeight;
        return goal;
    }

    // Only a capture point really over the stance foot is captured. Just past its edge the body is
    // still falling away, and a short step alongside would land on the wrong side of it.
    bool CapturedBy(JointBalanceHumanoid h, Quaternion heading, int stance) =>
        SupportDistance(h,capture,heading,stance==0,stance==1)<capturedMargin;

    // The highest ground under a footprint, out to its corners.
    bool HighestUnder(Vector3 p, Quaternion heading, out float top)
    {
        top=float.NegativeInfinity;
        for(int i=0;i<5;i++)
        {
            Vector3 offset=i==0?Vector3.zero:new Vector3(i==1||i==2?-.085f:.085f,0,i%2==0?.155f:-.155f);
            if(Physics.Raycast(p+heading*offset+Vector3.up*.45f,Vector3.down,out var hit,1.1f,1,QueryTriggerInteraction.Ignore) && hit.normal.y>.45f)
                top=Mathf.Max(top,hit.point.y);
        }
        return !float.IsNegativeInfinity(top);
    }

    // Accept the plan if there is ground there; otherwise shorten the step until there is.
    bool FindFoothold(Vector3 goal, Quaternion heading, int stance, out Vector3 foothold)
    {
        foothold=goal;
        for(float scale=1;scale>=.3f;scale-=.14f)
        {
            Vector3 candidate=swingStart+Vector3.ProjectOnPlane(goal-swingStart,Vector3.up)*scale;
            if(Ground(candidate,heading,out float y,out _) && Mathf.Abs(y-ground[stance])<.15f)
            {
                foothold=candidate; foothold.y=y+FootRestHeight;
                if(gaitStep && FootholdRisk(candidate,heading)>edgeSpread) SaferFoothold(heading,stance,ref foothold);
                return true;
            }
        }
        return false;
    }
    // A walking step has the time to choose a better place nearby than one where the foot would come down
    // on an edge. Smallest moves first: each centimetre moved is that much error in the step.
    static readonly Vector2[] edgeShifts={new Vector2(0,.04f),new Vector2(0,-.04f),new Vector2(.04f,0),new Vector2(0,.08f),new Vector2(0,-.08f),new Vector2(.08f,0),new Vector2(0,.12f),new Vector2(0,-.12f),new Vector2(.04f,.06f),new Vector2(.04f,-.06f)};
    void SaferFoothold(Quaternion heading, int stance, ref Vector3 foothold)
    {
        Vector3 forward=heading*Vector3.forward, outward=heading*Vector3.right*(stance==0?1:-1);
        foreach(var s in edgeShifts)
        {
            Vector3 candidate=foothold+outward*s.x+forward*s.y;
            if(Ground(candidate,heading,out float y,out _) && FootholdRisk(candidate,heading)<=edgeSpread && Mathf.Abs(y-ground[stance])<.15f)
            { foothold=candidate; foothold.y=y+FootRestHeight; return; }
        }
    }
    // What can go wrong with a foot put down here, as a height (m). A foot whose middle is on one slab while
    // its heel or toe is over a higher one rests on that edge and tips over it: the risk is how much higher.
    // The other way round is a foot on a kerb, well supported, as long as most of it is on the slab: with
    // much of it hanging over, the risk counts as twice edgeSpread. Samples cover the footprint and a little
    // beyond, where a foot landing a couple of centimetres off target ends up. A single surface is safe however
    // it is tilted, as the sole is laid on it, and a ray down the gap between slabs, reaching the floor far
    // below, does not count.
    float FootholdRisk(Vector3 p, Quaternion heading)
    {
        if(!Physics.Raycast(p+Vector3.up*.45f,Vector3.down,out var middle,1.1f,1,QueryTriggerInteraction.Ignore)) return 1;
        float risk=0; int over=0;
        for(int i=-1;i<=1;i++) for(int j=-1;j<=1;j++)
        {
            if(i==0 && j==0) continue;
            if(!Physics.Raycast(p+heading*new Vector3(i*.095f,0,j*.175f)+Vector3.up*.45f,Vector3.down,out var hit,1.1f,1,QueryTriggerInteraction.Ignore) || hit.normal.y<.45f) continue;
            if(hit.collider==middle.collider || middle.point.y-hit.point.y>.2f) continue;
            if(hit.point.y>middle.point.y) risk=Mathf.Max(risk,hit.point.y-middle.point.y);
            else if(middle.point.y-hit.point.y>.012f) over++;
        }
        if(over>3) risk=Mathf.Max(risk,edgeSpread*2);
        return risk;
    }

    void BeginStep(JointBalanceHumanoid h, Quaternion heading, Vector3 right, int foot, bool isVoluntary)
    {
        steppingFoot=foot; lastFoot=foot; voluntary=isVoluntary; phase=Phase.Swing;
        gaitStep=!isVoluntary && Walking;
        swingStart=h.feet[foot].position; startHeight=swingStart.y;
        stancePressure=Load(1-foot)>50?Pressure(1-foot):targets[1-foot];
        kneeTargets[1-foot]=KneeBend(1-foot);
        timer=0; quiet=0; unsafeTicks=0; stepsStarted++; requested=false;
        landingTarget=swingStart;
        if(!isVoluntary && FindFoothold(Plan(h,foot,heading,right,stepTime),heading,1-foot,out var foothold)) landingTarget=foothold;
    }

    Vector3 Plan(JointBalanceHumanoid h, int foot, Quaternion heading, Vector3 right, float remaining) =>
        gaitStep?PlanGait(h,foot,heading,right,remaining):
        sequence>0 && CapturedBy(h,heading,1-foot)?PlanClosing(h,foot,heading*Vector3.forward,right,1-foot):PlanLanding(h,foot,heading,right,remaining);

    public bool Walking => gaitVelocity.sqrMagnitude>.0025f || gaitCommand.sqrMagnitude>.0025f;
    public bool GaitStep => gaitStep;
    // Where the last walking plan wanted the foot before the leg's reach and the stance foot's track limited it.
    [System.NonSerialized] public Vector3 gaitRawGoal;
    bool gaitStep;

    // Steady walking at velocity v with step time T, and the centre of pressure on each stance foot in
    // turn: the capture point leads the stance foot by v T/(e^(omega T)-1) along the way, and lies
    // W/(e^(omega T)+1) inside it across. So a step lands where the capture point will be at touchdown,
    // less those offsets for the next stance. Too fast and the next foot lands further ahead, which
    // slows the body: each step corrects the last.
    Vector3 GaitOffset(Quaternion heading, int nextStance, out float e)
    {
        e=Mathf.Exp(omega*gaitStepTime);
        Vector3 v=heading*new Vector3(gaitCommand.x,0,gaitCommand.z);
        float side=nextStance==0?-1:1;
        return v*gaitStepTime/(e-1)-heading*Vector3.right*(side*gaitWidth/(e+1));
    }
    // Land where the capture point will be at touchdown, less the lead it needs over the next stance foot.
    // (Landing where a recovery step would is not the same: that aims for where the body would come to
    // rest, about 0.15 m short of the capture point at walking speed, and the body runs away from it.)
    Vector3 PlanGait(JointBalanceHumanoid h, int foot, Quaternion heading, Vector3 right, float remaining)
    {
        int stance=1-foot;
        // The capture point moves away from the centre of pressure, wherever on the foot the ankle has put
        // it: from the middle of the foot the prediction runs ahead of where it really goes.
        Vector3 p=Vector3.ProjectOnPlane(steppingFoot==foot?stancePressure:targets[stance],Vector3.up);
        Vector3 xi=Vector3.ProjectOnPlane(capture,Vector3.up);
        Vector3 goal=p+(xi-p)*Mathf.Exp(omega*Mathf.Max(0,remaining))-GaitOffset(heading,foot,out _);
        gaitRawGoal=goal;
        // The same reach as a recovery step: from where the hip will be on touchdown.
        Vector3 hip=ActualHip(h,foot)+comVelocity*(remaining+.03f);
        Vector3 ankleOffset=heading*AnkleFromFoot;
        float vertical=Mathf.Max(.60f,hip.y-(ground[stance]+FootRestHeight+AnkleFromFoot.y));
        float reach=Mathf.Sqrt(Mathf.Max(.0025f,ReachLeg*ReachLeg-vertical*vertical))*landingReach;
        Vector3 fromHip=Vector3.ClampMagnitude(Vector3.ProjectOnPlane(goal+ankleOffset-hip,Vector3.up),reach);
        goal=Vector3.ProjectOnPlane(hip,Vector3.up)+fromHip-Vector3.ProjectOnPlane(ankleOffset,Vector3.up);
        // A walking swing travels from behind the body to ahead of it, about two step lengths.
        goal=swingStart+Vector3.ClampMagnitude(Vector3.ProjectOnPlane(goal-swingStart,Vector3.up),gaitMaximumSwing);
        // And no longer a step than walking takes. The swing leg's own speed counts in the body's momentum: a
        // foot rushed out to catch up pushes the body on through the stance foot, which asks for a longer step
        // still, until it lands in a split it cannot rise from. Short of that, the next step makes up the rest.
        // Faster than wanted, a longer step is the brake, and the ankle cannot brake much: it gets longer.
        Vector3 f=heading*Vector3.forward;
        float over=Mathf.Max(0,Vector3.Dot(comVelocity,f)-Vector3.Dot(heading*gaitCommand,f));
        float longest=gaitMaximumStep+over*gaitBrakeStep;
        float ahead=Vector3.Dot(goal-targets[stance],f);
        if(ahead>longest) goal-=f*(ahead-longest);
        // Never across the stance foot's track.
        float sign=foot==0?-1:1;
        float gap=Vector3.Dot(goal-targets[stance],right)*sign;
        if(gap<MinFootGap*.75f) goal+=right*sign*(MinFootGap*.75f-gap);
        goal.y=ground[stance]+FootRestHeight;
        return goal;
    }
    // The ankle holds the centre of pressure near the middle of the stance foot, nudged back when the
    // body is slower than commanded and forward when it is faster, never more than the foot allows.
    Vector3 GaitPressure(Quaternion heading)
    {
        int stance=1-steppingFoot;
        Vector3 error=comVelocity-heading*new Vector3(gaitCommand.x,0,gaitCommand.z);
        Vector3 offset=Vector3.ClampMagnitude(Vector3.ProjectOnPlane(error,Vector3.up)*gaitSpeedGain,.08f);
        if(gaitCaptureGain>0)
        {
            // Where the capture point should be by now, had the step begun as planned: it leaves the stance
            // foot from the lead it needs for the next one and moves away exponentially. Any error in it, the
            // ankle answers by moving the centre of pressure the same way, as far as the foot allows: the
            // capture point then moves less that way.
            Vector3 p0=Vector3.ProjectOnPlane(targets[stance],Vector3.up);
            Vector3 nominal=p0+GaitOffset(heading,stance,out _)*Mathf.Exp(omega*Mathf.Min(timer,gaitStepTime));
            Vector3 xiError=Vector3.ProjectOnPlane(capture,Vector3.up)-nominal;
            Vector3 f=heading*Vector3.forward, r=heading*Vector3.right;
            float along=Mathf.Clamp(Vector3.Dot(xiError,f)*gaitCaptureGain+Vector3.Dot(offset,f),-.07f,.1f);
            float across=Mathf.Clamp(Vector3.Dot(xiError,r)*gaitCaptureGain+Vector3.Dot(offset,r),-.045f,.045f);
            offset=f*along+r*across;
        }
        Vector3 p=targets[stance]+offset;
        gaitPressureWanted=p;
        return p;
    }
    Vector3 comVelocity, stancePressure;
    public Vector3 StancePressure => stancePressure;
    // The stance foot's centre of pressure as measured, smoothed a little: contact impulses are noisy.
    void TrackStancePressure(float dt)
    {
        if(steppingFoot<0) return;
        int stance=1-steppingFoot;
        if(Load(stance)>50) stancePressure=Vector3.Lerp(stancePressure,Pressure(stance),1-Mathf.Exp(-dt/.03f));
    }

    int ChooseFoot(Vector3 mid, Vector3 right)
    {
        float side=Vector3.Dot(capture-mid,right);
        if(Mathf.Abs(side)>.08f) return (side>0)==stanceDownwind?0:1;
        float l=Load(0), r=Load(1);
        if(Mathf.Abs(l-r)>60) return l<r?0:1;
        return 1-lastFoot;
    }

    // The same once the stance is no longer normal: the foot nearer xi stays, the other one moves.
    int FootToMove(Vector3 mid, Vector3 right)
    {
        if(!stanceDownwind)
        {
            Vector3 outward=Vector3.ProjectOnPlane(capture-mid,Vector3.up);
            float side=Vector3.Dot(outward,right);
            if(Mathf.Abs(side)>outward.magnitude*.6f) return side>0?1:0;
        }
        return Vector3.ProjectOnPlane(capture-targets[0],Vector3.up).sqrMagnitude>Vector3.ProjectOnPlane(capture-targets[1],Vector3.up).sqrMagnitude?0:1;
    }

    void FinishSwing(bool landed)
    {
        if(landed) stepsLanded++; else stepsCancelled++;
        placedLanding=placingStep && voluntary;
        placingStep=false;
        steppedSinceNominal=true; sequence++;
        if(landed && voluntary) closingStepsTaken++;
        steppingFoot=-1; phase=Phase.DoubleSupport; timer=0; quiet=0; unsafeTicks=0; cooldown=.03f; leadLoaded=0;
        for(int i=0;i<2;i++) { kneeTargets[i]=KneeBend(i); bearing[i]=true; }
    }

    public void Plan(JointBalanceHumanoid h, Quaternion heading, Quaternion upright)
    {
        float dt=Time.fixedDeltaTime;
        gaitCommand=Vector3.MoveTowards(gaitCommand,gaitVelocity,gaitAcceleration*dt);
        settling=Mathf.Max(0,settling-dt);
        cooldown=Mathf.Max(0,cooldown-dt);
        compression=Mathf.MoveTowards(compression,standingCompression,dt*.035f);
        bool valid=true;
        for(int i=0;i<2;i++)
        {
            targets[i]=h.feet[i].position;
            valid &= Ground(targets[i],heading,out ground[i],out normals[i]);
            footSpread[i]=lastSpread;
            targets[i].y=ground[i]+FootRestHeight;
            // Contact impulses swing wildly from tick to tick, and ankle torque set from them feeds back.
            smoothLoad[i]=Mathf.Lerp(smoothLoad[i],Mathf.Min(Load(i),Physics.gravity.magnitude*totalMass*1.5f),1-Mathf.Exp(-dt/loadFilter));
            // Carrying little is fine when balance wants it that way. Only a foot really off the ground
            // needs putting back: switching modes on load alone becomes a limit cycle that walks the
            // foot across the floor, as each press comes back as a load that ends the press.
            bool aboveGround=h.feet[i].position.y-(ground[i]+FootRestHeight)>hangHeight;
            unloaded[i]=Load(i)>hangLoad || !aboveGround?0:unloaded[i]+dt;
            // The standing pose takes each leg's angle from where its foot is, which for a foot off
            // the ground is a runaway: the foot moves, so the leg moves, so the foot moves.
            hanging[i]=unloaded[i]>hangTime;
            bearing[i]=Load(i)>(bearing[i]?60:120);
            if(smoothLoad[i]>plantLoad || phase==Phase.Swing || phase==Phase.Prepare || phase==Phase.Stand || phase==Phase.Flight) planted[i]=h.feet[i].position;
            // A foot carrying little has little friction to hold it, so it needs a place of its own to
            // be: its own present position would let it wander with the slightest pull from the leg.
            else { targets[i].x=planted[i].x; targets[i].z=planted[i].z; }
        }
        Vector3 forward=heading*Vector3.forward, right=heading*Vector3.right;
        NormalStance=StanceIsNormal(forward,right);
        Vector3 mid=(targets[0]+targets[1])*.5f;
        float groundMean=(ground[0]+ground[1])*.5f;
        omega=Mathf.Sqrt(Physics.gravity.magnitude/Mathf.Max(.5f,h.centerOfMass.y-groundMean));
        // Unfiltered COM velocity: a push changes it instantly, and any lag here multiplies
        // into step error through e^(omega t).
        comVelocity=Vector3.ProjectOnPlane(h.centerOfMassVelocity,Vector3.up);
        capture=h.centerOfMass+Vector3.ProjectOnPlane(h.centerOfMassVelocity,Vector3.up)/omega;
        capture.y=mid.y;
        supportMargin=SupportDistance(h,capture,heading,true,true);
        bool fallen=h.pelvis.position.y<groundMean+FallenHeight || Vector3.Angle(h.bodies[1].rotation*Vector3.up,Vector3.up)>FallenTrunk;
        bool unsafePoint=supportMargin>-triggerMargin;
        unsafeTicks=unsafePoint?unsafeTicks+1:0;
        bool canStep=recoverySteps && valid && h.supported && h.elapsed>1.5f && !fallen && cooldown<=0;

        if(fallen && phase!=Phase.Stand)
        {
            // Stepping cannot help any more. Getting up is out of scope.
            if(steppingFoot>=0) stepsCancelled++;
            steppingFoot=-1; phase=Phase.Stand; settling=0; requested=false; sequence=0;
        }

        Vector3 servoTarget=mid;
        // A jump starts from any stance with both feet down and loaded.
        if(jumpRequested && !fallen && valid && h.elapsed>1.5f && (phase==Phase.Stand || phase==Phase.Hold || phase==Phase.DoubleSupport && timer>landingHold) && Contact(0) && Contact(1) && bearing[0] && bearing[1])
        {
            jumpRequested=false; steppingFoot=-1; phase=Phase.JumpCrouch; timer=0;
            jumpFrom=h.pelvis.position; pelvisHeight=jumpFrom.y;
        }
        switch(phase)
        {
            case Phase.JumpCrouch:
                // Down, and over the feet, or ahead of them for a moving jump: the push then passes the
                // body's weight through the feet at an angle that sends it that way.
                state="Jump: crouching";
                timer+=dt;
                servoTarget=mid+heading*jumpVelocity*jumpLean;
                if(timer>=jumpCrouchTime)
                {
                    phase=Phase.JumpPush; timer=0; jumpPushFrom=h.pelvis.position;
                    // Constant acceleration up to the take-off speed for the height wanted, over what the legs
                    // can still straighten: v = sqrt(2 g h), and the push takes 2 d / v.
                    float extended=groundMean+FootRestHeight+AnkleFromFoot.y+MaxLeg*.98f+HipDrop;
                    jumpPushRise=Mathf.Max(.05f,extended-jumpPushFrom.y);
                    jumpPushTime=2*jumpPushRise/Mathf.Sqrt(2*Physics.gravity.magnitude*jumpHeight);
                }
                break;
            case Phase.JumpPush:
                state="Jump: pushing";
                timer+=dt;
                // The ankles put the centre of pressure behind the capture point for a moving jump, which drives
                // the body that way; for a jump in place, under it.
                servoTarget=jumpVelocity.sqrMagnitude>.01f?capture+heading*jumpVelocity:mid;
                if(timer>.04f && Load(0)+Load(1)<jumpContactLoad) { phase=Phase.Flight; timer=0; jumpsMade++; airborne=Load(0)+Load(1)<jumpContactLoad; }
                else if(timer>jumpPushTime+.25f) { phase=Phase.Hold; timer=0; quiet=0; pelvisHeight=h.pelvis.position.y; }
                break;
            case Phase.Flight:
                state="Jump: in the air";
                timer+=dt;
                // Let go early, the feet are still on the ground for a moment: only a foot that has left it lands.
                if(Load(0)+Load(1)<jumpContactLoad) airborne=true;
                int first=Contact(0) && Load(0)>jumpContactLoad?0:Contact(1) && Load(1)>jumpContactLoad?1:-1;
                if(first>=0 && (airborne && timer>.05f || timer>.15f))
                {
                    // Down: double support as after a step, with the height let down for the legs to give.
                    jumpsLanded++; lastFoot=first; sequence++; steppedSinceNominal=true;
                    phase=Phase.DoubleSupport; timer=0; quiet=0; unsafeTicks=0; leadLoaded=0; cooldown=.03f;
                    pelvisHeight=h.pelvis.position.y-jumpLandAbsorb;
                    for(int i=0;i<2;i++) { kneeTargets[i]=KneeBend(i); bearing[i]=Load(i)>60; }
                }
                else if(timer>2) { phase=Phase.Hold; timer=0; quiet=0; pelvisHeight=h.pelvis.position.y; }
                break;
            case Phase.Stand:
                state=fallen?"Fallen":settling>0?"Settling":"Terrain stance";
                bool nominal=StanceIsNominal(forward,right);
                if(nominal && settling<=0) steppedSinceNominal=false;
                // The standing law assumes the feet are about where they belong.
                if(!fallen && settling<=0 && !StanceIsNormal(forward,right)) { phase=Phase.Hold; timer=0; quiet=0; pelvisHeight=h.pelvis.position.y; break; }
                // After a step the stance is wide and long, and xi may sit near an edge while
                // still recoverable, so only step again once it has clearly left the polygon.
                Vector3 fromMid=capture-mid;
                float along=Vector3.Dot(fromMid,forward), across=Vector3.Dot(fromMid,right);
                float limit=along>0?stepAhead:stepBehind;
                beyondLimit=Mathf.Abs(along)>limit || Mathf.Abs(across)>stepSideways ? beyondLimit+1 : 0;
                bool outside=nominal?beyondLimit>=2:supportMargin>stepAgainMargin && unsafeTicks>=2;
                float standSpeed=Vector3.ProjectOnPlane(h.centerOfMassVelocity,Vector3.up).magnitude;
                quiet=!unsafePoint && standSpeed<.12f ? quiet+dt : 0;
                if(canStep && placedPending && !fallen && settling<=0 && Vector3.ProjectOnPlane(h.centerOfMassVelocity,Vector3.up).magnitude<placedStartSpeed)
                {
                    // A step to a given place: the weight part way across first (see WeightShift).
                    pelvisHeight=h.pelvis.position.y; stayFoot=1-placedFoot; phase=Phase.WeightShift; timer=0;
                    break;
                }
                if(canStep && Walking && !fallen && settling<=0)
                {
                    // Set off: shift onto one foot, then step with the other.
                    pelvisHeight=h.pelvis.position.y;
                    preparingFoot=ChooseFoot(mid,right); phase=Phase.Prepare; timer=0;
                    break;
                }
                if(canStep && (outside || requested))
                {
                    pelvisHeight=h.pelvis.position.y;
                    int foot=nominal?ChooseFoot(mid,right):FootToMove(mid,right);
                    if(requested) { BeginStep(h,heading,right,foot,true); landingTarget=targets[foot]+forward*.20f; }
                    // Falling sideways already unloads the stance side: lift at once.
                    else if(!nominal || Mathf.Abs(Vector3.Dot(capture-mid,right))>.08f) { if(Contact(1-foot)) BeginStep(h,heading,right,foot,false); }
                    else { preparingFoot=foot; phase=Phase.Prepare; timer=0; }
                }
                break;
            case Phase.Prepare:
                // Single support starts a sideways fall about the stance foot that grows as
                // d(e^(omega t)-1). A brief shift shrinks d for little cost in the step direction.
                state="Preparing step";
                timer+=dt;
                servoTarget=targets[1-preparingFoot];
                if(Walking)
                {
                    // Setting off, as people do: the centre of pressure moves back and towards the stance
                    // side, which tips the body forward over the stance foot. Lift once the capture point
                    // leads it by what steady walking needs; the first step then lands ahead.
                    int stanceFoot=1-preparingFoot;
                    // From the speed wanted, not the one in use: that is still ramping up from nothing.
                    Vector3 inUse=gaitCommand; gaitCommand=gaitVelocity;
                    Vector3 lead=GaitOffset(heading,stanceFoot,out _);
                    gaitCommand=inUse;
                    servoTarget=targets[stanceFoot]+lead;
                    Vector3 way=Vector3.ProjectOnPlane(heading*new Vector3(gaitVelocity.x,0,gaitVelocity.z),Vector3.up).normalized;
                    if(lead.sqrMagnitude<.0001f) lead=way*.02f;
                    bool over=Mathf.Abs(Vector3.Dot(capture-servoTarget,right))<shiftLift;
                    bool leading=Vector3.Dot(capture-targets[stanceFoot],way)>=.7f*Vector3.Dot(lead,way);
                    if(over && leading || timer>gaitLean) BeginStep(h,heading,right,preparingFoot,false);
                    break;
                }
                if(Mathf.Abs(Vector3.Dot(capture-servoTarget,right))<shiftLift || timer>shiftTime)
                    BeginStep(h,heading,right,preparingFoot,false);
                break;
            case Phase.DoubleSupport:
                // A moment for the new foot to take load. Then either the next foot must step, or
                // the standing feedback takes back over: it is what brings the body to rest.
                state="Landing";
                timer+=dt;
                // The new foot bounces on landing. Lifting the other one in that moment leaves nothing
                // on the ground to push against: the swing leg's reaction slides the foot back.
                leadLoaded=Contact(lastFoot) && Load(lastFoot)>leadLoad ? leadLoaded+dt : 0;
                if(timer<landingHold) break;
                if(Walking && canStep)
                {
                    // Walking: the trailing foot goes as soon as the new one carries the body.
                    servoTarget=targets[lastFoot];
                    if(timer>=gaitDoubleSupport && Contact(lastFoot) && (leadLoaded>=leadLoadTime || timer>leadLoadLimit)) BeginStep(h,heading,right,1-lastFoot,false);
                    break;
                }
                bool comfortable=StanceIsAcceptable(forward,right);
                bool stepNeeded=supportMargin>stepAgainMargin && unsafeTicks>=2;
                // A placed step lands in the stance it was asked for, however long: not one to close up by
                // drawing the body onto the new foot and bringing the other after it. The body stays on its way
                // to the middle of that stance.
                if(placedLanding) { comfortable=true; servoTarget=placedGoalPoint; }
                Vector3 feetAxis=Vector3.ProjectOnPlane(targets[lastFoot]-targets[1-lastFoot],Vector3.up).normalized;
                bool reached=Vector3.Dot(capture-targets[lastFoot],feetAxis)>-closeMargin;
                // Keep going while there is momentum to use: wait for it to bring xi up to the
                // leading foot, then bring the trailing one through.
                if(!comfortable) servoTarget=targets[lastFoot];
                if(canStep && stepNeeded)
                {
                    int foot=FootToMove(mid,right);
                    if(Contact(1-foot) && (Load(1-foot)>100 && leadLoaded>=leadLoadTime || foot==lastFoot || timer>leadLoadLimit)) BeginStep(h,heading,right,foot,false);
                }
                else if(canStep && !comfortable && reached && (leadLoaded>=leadLoadTime || timer>leadLoadLimit)) BeginStep(h,heading,right,1-lastFoot,false);
                else if(comfortable && bearing[0] && bearing[1] || timer>closeWait) { phase=Phase.Hold; timer=0; quiet=0; }
                break;
            case Phase.Hold:
                // The legs position the pelvis directly from wherever the feet are planted, like a
                // parallel robot on two stiff ankles. That holds any stance the feet can reach, and
                // it is what the body needs before the standing law or a closing step can follow.
                state="Holding stance";
                timer+=dt;
                if(canStep && Walking)
                {
                    preparingFoot=FootToMove(mid,right); phase=Phase.Prepare; timer=0;
                    break;
                }
                if(canStep && supportMargin>stepAgainMargin && unsafeTicks>=2)
                {
                    int foot=FootToMove(mid,right);
                    if(Contact(1-foot)) BeginStep(h,heading,right,foot,false);
                    break;
                }
                // The next placed step once the body has stopped: shifting off a body still moving, the shift
                // starts from wherever the momentum takes xi.
                if(canStep && placedPending)
                {
                    // Not into the standing law meanwhile: the switch of controller throws the body about just
                    // as the shift begins.
                    if(bearing[0] && bearing[1] && timer>.2f && (Vector3.ProjectOnPlane(h.centerOfMassVelocity,Vector3.up).magnitude<placedStartSpeed || timer>1.5f)) { stayFoot=1-placedFoot; phase=Phase.WeightShift; timer=0; }
                    break;
                }
                float holdFlatSpeed=Vector3.ProjectOnPlane(h.centerOfMassVelocity,Vector3.up).magnitude;
                bool calm=holdFlatSpeed<holdSpeed && Vector3.ProjectOnPlane(capture-mid,Vector3.up).magnitude<holdCentre && bearing[0] && bearing[1];
                quiet=calm?quiet+dt:0;
                bool normal=StanceIsNormal(forward,right);
                if(holdOnly) break;
                // A meant stance goes straight to the standing law: the hold lets a body over its front foot creep on.
                if(heldStanceSpread>0 && normal && bearing[0] && bearing[1] && timer>.15f) { GoToStand(h); break; }
                if(normal && (quiet>quietTime || timer>holdLimit)) GoToStand(h);
                else if(!normal && closingSteps && canStep && (quiet>quietTime || timer>holdLimit))
                {
                    // Stay on the foot nearer the body, and bring the other one to it.
                    Vector3 flat=Vector3.ProjectOnPlane(h.centerOfMass,Vector3.up);
                    stayFoot=Vector3.ProjectOnPlane(targets[0]-flat,Vector3.up).sqrMagnitude<Vector3.ProjectOnPlane(targets[1]-flat,Vector3.up).sqrMagnitude?0:1;
                    phase=Phase.WeightShift; timer=0;
                }
                break;
            case Phase.WeightShift:
                state="Shifting weight";
                timer+=dt;
                servoTarget=targets[stayFoot];
                if(supportMargin>stepAgainMargin && unsafeTicks>=2) { phase=Phase.Hold; timer=0; quiet=0; break; }
                // A placed step lifts with xi where, running away from the stay foot as e^(omega t) for the swing,
                // it reaches the middle of the new stance just as the foot lands: the body then comes to rest over
                // both feet. Short of that, it is still on its way back to the stay foot at touchdown; past it, it
                // runs on beyond the landing foot. Measured as a point, not a share of the way between the feet:
                // in a long diagonal stance such as a fencing guard the body is well off that line.
                if(placedPending && canStep)
                {
                    Vector3 lift=PlacedLift(placedFoot,placedTarget,out _);
                    servoTarget=lift;
                    float error=Vector3.ProjectOnPlane(capture-lift,Vector3.up).magnitude;
                    // The closest it has come; a shift that has made no headway for a while has gone as far as it goes.
                    if(timer<dt*1.5f || error<placedClosest-.005f) { placedClosest=error; placedStalled=0; } else placedStalled+=dt;
                    if(error<placedLiftError || placedStalled>.3f && error<placedStallError || timer>2f) PlacedStep(h,heading,right);
                    break;
                }
                // Lift as soon as xi is over the stay foot. The COM need not be there yet:
                // its remaining momentum carries it over without loading the knee.
                if(canStep && SupportDistance(h,capture,heading,stayFoot==0,stayFoot==1)<liftMargin)
                {
                    int foot=1-stayFoot;
                    BeginStep(h,heading,right,foot,true);
                    // Beside the stay foot, a little ahead or behind so that the new stance is
                    // centred on where the body is, or the next thing it would need is another step.
                    float closeAlong=Mathf.Clamp(2*Vector3.Dot(Vector3.ProjectOnPlane(Vector3.Lerp(h.centerOfMass,capture,closeBlend)-targets[stayFoot],Vector3.up),forward),-closeSpread,closeSpread);
                    Vector3 closed=targets[stayFoot]+right*(foot==1?NominalWidth:-NominalWidth)+forward*closeAlong;
                    if(placedPending && placedFoot==foot) { closed=placedTarget; placedPending=false; placedSteps++; placingStep=true; }
                    if(FindFoothold(closed,heading,stayFoot,out var closedSpot)) closed=closedSpot;
                    landingTarget=closed;
                }
                else if(timer>1.6f) { phase=Phase.Hold; timer=0; quiet=0; }
                break;
        }

        if(phase==Phase.Swing)
        {
            int i=steppingFoot, stance=1-i;
            TrackStancePressure(dt);
            // A disturbance during an unhurried step turns it into a real recovery step.
            // A placed step starts from both feet, with xi between them and so well off the stance foot: only a
            // real fall makes it a recovery step.
            if(voluntary && SupportDistance(h,capture,heading,i!=0,i!=1)>(placingStep?.25f:.04f)) { voluntary=false; placingStep=false; }
            float duration=gaitStep?gaitStepTime:voluntary?stepTime/(placingStep?placedPace:closingPace):stepTime;
            timer+=dt;
            float progress=Mathf.Clamp01(timer/duration);
            // Stop replanning just before arrival so that it has a fixed place to come down on.
            if(!voluntary && progress<.85f && FindFoothold(Plan(h,i,heading,right,duration-timer),heading,stance,out var foothold))
                landingTarget=Vector3.Lerp(landingTarget,foothold,1-Mathf.Exp(-40*dt));
            if(Ground(landingTarget,heading,out float gy,out var gn)) { landingTarget.y=gy+FootRestHeight; normals[i]=gn; }
            // Clear the ground first, then travel.
            float travel=Mathf.SmoothStep(0,1,Mathf.Clamp01((progress-.08f)/.92f));
            Vector3 p=Vector3.Lerp(swingStart,landingTarget,travel);
            // Level ground keeps the clearance it was tuned with; uneven ground gets more.
            bool uneven=Mathf.Abs(landingTarget.y-startHeight)>.01f || Vector3.Angle(normals[stance],Vector3.up)>1 || footSpread[stance]>.01f;
            float clearance=gaitStep?gaitClearance:roughClearance>0 && uneven?roughClearance:stepClearance;
            // A walking foot peaks early and comes down slowly, so that the knee has straightened by touchdown.
            p.y=Mathf.Lerp(startHeight,landingTarget.y,travel)+clearance*Mathf.Sin(Mathf.PI*Mathf.Pow(progress,gaitStep?gaitSwingShape:swingShape));
            if(pathClearance>0 && progress<.85f)
            {
                // A straight line to the landing can run into a raised slab on the way there.
                float ahead=float.NegativeInfinity;
                for(int s=0;s<=3;s++)
                    if(HighestUnder(Vector3.Lerp(p,landingTarget,s/3f),heading,out float pathTop)) ahead=Mathf.Max(ahead,pathTop);
                float line=Mathf.Lerp(startHeight,landingTarget.y,travel);
                if(ahead+FootRestHeight>line+.005f)
                    p.y=Mathf.Max(p.y,Mathf.Lerp(p.y,ahead+FootRestHeight+pathClearance,Mathf.Clamp01((.85f-progress)/.15f)));
            }
            // Touchdown comes from contact, not the clock: past the end of the
            // trajectory, keep reaching down until the sole actually lands.
            if(timer>duration) p.y-=Mathf.Min(.10f,(timer-duration)*.7f);
            targets[i]=p;
            // The stance ankle draws xi towards the stance foot, which a recovery step wants. A placed step lands
            // into a stance of its own: drawn onto the stance foot instead, xi is past where both feet together
            // can bring it back once the other one is down.
            servoTarget=placingStep?placedGoalPoint:targets[stance];
            state=gaitStep?"Walking":voluntary?"Closing step":"Recovery step";
            // A toe scuff on the way out is not a landing. Real load is, wherever the foot is:
            // the body has come down on it, and dragging it onward would only trip.
            bool touched=timer>.08f && Contact(i) && (progress>touchdownProgress && Load(i)>5 || progress>.4f && Load(i)>scuffLoad) && h.feet[i].position.y-ground[i]<.11f;
            if(touched) FinishSwing(true);
            else if(timer>duration+.30f) FinishSwing(false);
        }
        supportPoint=phase==Phase.Stand?Vector3.Lerp(supportPoint,mid,1-Mathf.Exp(-8*dt)):servoTarget;

        if(phase==Phase.Stand) PoseStanding(h,heading,upright,forward,right,mid);
        else if(phase==Phase.Flight) PoseFlight(h,heading,forward);
        else { PoseStepping(h,heading,forward,servoTarget,dt); PlaceCentreOfPressure(h,heading,servoTarget); }
    }

    void GoToStand(JointBalanceHumanoid h)
    {
        if(landingPose.Length!=h.motors.Length) landingPose=new Quaternion[h.motors.Length];
        for(int j=0;j<h.motors.Length;j++)
        {
            // A planted ankle's target sits well past the foot, to set its torque. Blending from there
            // would throw the body as the stiff standing drive comes back.
            if(h.motors[j].name.Contains("Foot"))
                h.motors[j].targetRotation=Quaternion.Inverse(Quaternion.Inverse(h.motors[j].connectedBody.rotation)*h.motors[j].transform.rotation);
            landingPose[j]=h.motors[j].targetRotation;
        }
        settling=SettleTime; phase=Phase.Stand; steppingFoot=-1; state="Settling"; quiet=0; bumpless=true; sequence=0;
    }
    public bool NormalStance;
    // Test fixture only: put the humanoid in the hold and keep it there.
    public bool holdOnly;
    public void ShiftOnto(JointBalanceHumanoid h,int foot) { EnterHold(h); stayFoot=foot; phase=Phase.WeightShift; }
    public void EnterHold(JointBalanceHumanoid h) { phase=Phase.Hold; timer=0; quiet=0; pelvisHeight=h.pelvis.position.y; steppingFoot=-1; }
    bool StanceIsNormal(Vector3 forward, Vector3 right)
    {
        Vector3 between=targets[1]-targets[0];
        float width=Vector3.Dot(between,right), spread=Mathf.Abs(Vector3.Dot(between,forward));
        return spread<Mathf.Max(normalSpread,heldStanceSpread) && width>(heldStanceSpread>0?.12f:normalMinWidth) && width<(heldStanceSpread>0?Mathf.Max(normalMaxWidth,heldStanceWidth):normalMaxWidth);
    }
    [Tooltip("A stance that is meant, such as a fencing guard, may be this long front to back and still count as normal, so the standing law holds it rather than the hold, which lets a body leaning over its front foot creep on (m). 0 keeps the normal limits.")]
    [Range(0,.5f)] public float heldStanceSpread = 0;
    [Tooltip("Where a placed step aims to have the capture point when it lands: 0 over the stay foot, 1 over the landing foot.")]
    [Range(0,1)] public float placedGoal = .5f;
    [Tooltip("How close the capture point must come to its lift point before a placed step's foot lifts (m).")]
    [Range(.005f,.1f)] public float placedLiftError = .02f;
    [Tooltip("Smallest share of the load the foot a placed step will lift is asked to keep while the weight shifts.")]
    [Range(0,.45f)] public float placedShareMargin = .03f;
    [Tooltip("A placed step's shift that has stopped closing on its lift point lifts anyway within this distance of it (m).")]
    [Range(0,.15f)] public float placedStallError = .05f;
    [Tooltip("Integral gain of a placed step's weight shift (1/s), and the most it may add to the pelvis command (m).")]
    [Range(0,5)] public float placedIntegral = 1f;
    [Range(0,.2f)] public float placedPushLimit = .1f;
    [Tooltip("A placed step waits in the hold until the body is slower than this (m/s), for at most 1.5 s.")]
    [Range(.02f,.5f)] public float placedStartSpeed = .08f;
    [Tooltip("With a meant stance (heldStanceSpread above 0), the widest stance the standing law still holds (m): footwork such as a fencer's passes through wide stances on its way round.")]
    [Range(.3f,.7f)] public float heldStanceWidth = .56f;
    [Tooltip("How fast a placed step's weight shift is, as a share of a closing step's.")]
    [Range(.1f,1)] public float placedShift = 1f;
    [Tooltip("How brisk a placed step's swing is, as a share of a recovery step's pace (closing steps use closingPace). An unhurried swing over a long step leaves the body time to sink and tip off the stay foot.")]
    [Range(.2f,1.5f)] public float placedPace = 1f;
    bool StanceIsNominal(Vector3 forward, Vector3 right)
    {
        Vector3 between=targets[1]-targets[0];
        float width=Vector3.Dot(between,right), spread=Mathf.Abs(Vector3.Dot(between,forward));
        return spread<.07f && width>NominalWidth-.07f && width<NominalWidth+.07f;
    }
    // Stable and comfortable, even if not nominal. A step only to tidy this up risks more than it
    // gains: a foot often slides a little during a push, and the stance is fine as it is.
    bool StanceIsAcceptable(Vector3 forward, Vector3 right)
    {
        Vector3 between=targets[1]-targets[0];
        float width=Vector3.Dot(between,right), spread=Mathf.Abs(Vector3.Dot(between,forward));
        return spread<.16f && width>.10f && width<.48f;
    }

    // Posture while a step sequence owns the legs.
    void PoseStepping(JointBalanceHumanoid h, Quaternion heading, Vector3 forward, Vector3 servoTarget, float dt)
    {
        for(int i=0;i<2;i++) soles[i]=Quaternion.FromToRotation(Vector3.up,normals[i])*heading;
        if(steppingFoot>=0)
        {
            // Single support. The stance hip keeps the pelvis level (JointBalanceHumanoid), the knee
            // holds height, and the swing leg tracks the foot from where the hip really is.
            int swing=steppingFoot, stance=1-swing;
            // Hold height like the linear inverted pendulum: the knee straightens as the body moves
            // away from the foot, instead of the body sinking on an arc and loading a bent knee.
            Vector3 stanceAnkle=targets[stance]+soles[stance]*AnkleFromFoot;
            Vector3 stanceHip=ActualHip(h,stance);
            float wantPelvis=ground[stance]+stanceHeight;
            bool lowering=false;
            if(gaitStep)
            {
                // Walking over uneven ground, the swing leg takes up a few centimetres of height difference by
                // itself. Lowering the body for them bends a nearly straight stance knee a long way, and the
                // stance foot comes unloaded and slides; raising it for a step up has the leg push off.
                float drop=ground[stance]-(landingTarget.y-FootRestHeight)-gaitStepAbsorb;
                if(drop>0) { wantPelvis-=drop*Mathf.Clamp01(SwingProgress/stepDownLead); lowering=true; }
            }
            else if(stepDownLead>0)
            {
                // Going down a step, the leg can only reach once the body has come down with it. Going
                // up, there is nothing to gain from a stance knee that is not as straight as it goes.
                Vector3 landingAnkle=landingTarget+soles[swing]*AnkleFromFoot;
                float landingAcross=Vector3.ProjectOnPlane(landingAnkle-ActualHip(h,swing),Vector3.up).magnitude;
                float touching=landingAnkle.y+Mathf.Sqrt(Mathf.Max(.09f,ReachLeg*ReachLeg-landingAcross*landingAcross))+HipDrop;
                float landingGround=landingTarget.y-FootRestHeight;
                // Only for a landing that really is lower: on level ground a long step reaches as it always has.
                if(landingGround<ground[stance]-.01f && touching<wantPelvis) { wantPelvis=Mathf.Lerp(wantPelvis,touching,Mathf.Clamp01(SwingProgress/stepDownLead)); lowering=true; }
                else if(landingGround>ground[stance]+.01f) wantPelvis=Mathf.Max(wantPelvis,landingGround+stanceHeight);
            }
            float above=wantPelvis-HipDrop-stanceAnkle.y;
            float span=Mathf.Sqrt(above*above+Vector3.ProjectOnPlane(stanceAnkle-stanceHip,Vector3.up).sqrMagnitude);
            float kneeForHeight=2*Mathf.Acos(Mathf.Clamp(span/.8f,0,1))*Mathf.Rad2Deg;
            // Falling behind the foot, the ground force passes behind the knee and a bent knee folds.
            bool behind=Vector3.Dot(capture-stanceAnkle,forward)<0;
            // Walking, the stance leg vaults the body over a nearly straight knee, which lets the pelvis rise
            // and fall a few centimetres. Holding the height level instead bends the knee while the foot is
            // still ahead, just as it takes the whole weight, and past about 40 degrees the cap cannot hold it.
            HoldKnee(stance,behind && !lowering?stanceKnee:Mathf.Clamp(kneeForHeight,stanceKnee,gaitStep?gaitKneeLimit:70),heading,dt);
            float bend=SolveLeg(swing,ActualHip(h,swing),targets[swing]+soles[swing]*AnkleFromFoot,forward,MinSwingLeg);
            leftKneeDegrees=swing==0?bend:kneeTargets[stance]; rightKneeDegrees=swing==1?bend:kneeTargets[stance];
            pelvisHeight=h.pelvis.position.y;
            return;
        }
        // Double support: IK to a commanded pelvis keeps the pose consistent with wherever the
        // feet actually landed, and the servo moves the pelvis towards where it should be.
        Vector3 command=h.pelvis.position;
        // The shift before a closing step is voluntary, so there is no reason for it to be violent.
        // A placed step's shift slower still: its swing is unhurried, and the momentum of a quick shift
        // carries the body on out past the stay foot while the other is in the air.
        command+=Vector3.ClampMagnitude(Vector3.ProjectOnPlane(servoTarget-capture,Vector3.up)*servoGain,servoLimit*(phase==Phase.WeightShift?closingShift*(placedPending?placedShift:1):1));
        // That alone stalls a long way short in a wide stance: the legs give under the offset it asks for. A
        // placed step has to have xi at its lift point, so its shift integrates what is left.
        if(phase==Phase.WeightShift && placedPending)
        {
            placedPush=Vector3.ClampMagnitude(placedPush+Vector3.ProjectOnPlane(servoTarget-capture,Vector3.up)*(placedIntegral*dt),placedPushLimit);
            command+=placedPush;
        }
        else placedPush=Vector3.zero;
        // Stand as tall as both legs can reach. Never go so low that the nearer leg bends past
        // what the knee's torque cap can hold: the further leg's heel rises instead.
        float reachable=float.PositiveInfinity, lowest=float.NegativeInfinity;
        for(int i=0;i<2;i++)
        {
            // Shifting for a closing step, only the leg that stays matters. Limiting the height by
            // the foot about to leave would bend the stay knee just as it takes the whole body.
            if(phase==Phase.WeightShift && i!=stayFoot) continue;
            Vector3 ankle=targets[i]+soles[i]*AnkleFromFoot;
            Vector3 hip=command+heading*new Vector3(i==0?-HipHalfWidth:HipHalfWidth,-HipDrop,0);
            float horizontal=Vector3.ProjectOnPlane(ankle-hip,Vector3.up).magnitude;
            reachable=Mathf.Min(reachable,ankle.y+Mathf.Sqrt(Mathf.Max(.09f,ReachLeg*ReachLeg-horizontal*horizontal))+HipDrop);
            lowest=Mathf.Max(lowest,ankle.y+Mathf.Sqrt(Mathf.Max(.09f,MinLoadedLeg*MinLoadedLeg-horizontal*horizontal))+HipDrop);
        }
        // While preparing, hold height. Rising here would launch the body, leaving no load on the
        // stance foot when the other one lifts, and it would then land on that knee and fold it.
        float groundMean=(ground[0]+ground[1])*.5f;
        float tall=phase==Phase.Hold?StandingPelvis-compression:stanceHeight;
        // Between walking steps too: the next lift comes within a few hundredths of a second, and rising into
        // it leaves the stance foot unloaded, sliding, as the swing begins. Only come up from too low.
        if(phase==Phase.JumpCrouch) pelvisHeight=jumpFrom.y-jumpCrouch*Mathf.SmoothStep(0,1,timer/jumpCrouchTime);
        else if(phase==Phase.JumpPush)
        {
            // Constant acceleration to the take-off speed, then on up: the legs straighten to their limit.
            float s=Mathf.Min(timer,jumpPushTime), past=Mathf.Max(0,timer-jumpPushTime);
            pelvisHeight=jumpPushFrom.y+jumpPushRise*(s/jumpPushTime)*(s/jumpPushTime)+2*jumpPushRise/jumpPushTime*past;
            // Across the ground the push follows a speed: up to the take-off speed by the end (none for a jump in
            // place). The pelvis is led ahead of where it is by that, and by what the body still lacks of it.
            Vector3 wantedSpeed=heading*jumpVelocity*Mathf.Clamp01(timer/jumpPushTime);
            Vector3 lacking=wantedSpeed-Vector3.ProjectOnPlane(h.centerOfMassVelocity,Vector3.up);
            Vector3 flat=Vector3.ProjectOnPlane(h.pelvis.position,Vector3.up)+(wantedSpeed+lacking)*jumpLeadTime;
            command.x=flat.x; command.z=flat.z;
        }
        else if(Walking && phase==Phase.DoubleSupport) pelvisHeight=Mathf.Max(h.pelvis.position.y,Mathf.MoveTowards(pelvisHeight,lowest,dt*riseRate));
        else if(phase!=Phase.Prepare) pelvisHeight=Mathf.MoveTowards(pelvisHeight,Mathf.Max(Mathf.Min(reachable,groundMean+tall),lowest),dt*riseRate);
        // Damp the height as well as setting it. Stiff legs catching a falling body otherwise throw it
        // back up clear of the ground, and a foot in the air has nothing to hold it in place.
        // Except in a push, where the rise is the point.
        command.y=phase==Phase.JumpPush?pelvisHeight:pelvisHeight-h.pelvis.linearVelocity.y*heightDamping;
        // Share the load so that the two feet together put the centre of pressure where it is wanted.
        Vector3 wantedPressure=capture+Vector3.ProjectOnPlane(capture-servoTarget,Vector3.up)*copGain;
        Vector3 between=Vector3.ProjectOnPlane(targets[1]-targets[0],Vector3.up);
        // Both feet keep some load: an unloaded foot would be lifted clear by the length difference.
        // Except the foot a placed step is about to lift: in a long stance the margin keeps xi far from the
        // stay foot, short of where it must be when the foot goes.
        float margin=phase==Phase.WeightShift && placedPending?placedShareMargin:loadShareMargin;
        float share=Mathf.Clamp(Vector3.Dot(wantedPressure-targets[0],between)/Mathf.Max(.01f,between.sqrMagnitude),margin,1-margin);
        float total=Load(0)+Load(1);
        loadDifference=Mathf.Lerp(loadDifference,Load(1)-Load(0),1-Mathf.Exp(-dt/.03f));
        legDifference+=dt*(loadShareGain*((2*share-1)*total-loadDifference)-legDifference/loadShareLeak);
        legDifference=Mathf.Clamp(legDifference,-loadShareLimit,loadShareLimit);
        for(int i=0;i<2;i++)
        {
            float bend;
            if(LegIsFree(i))
            {
                // Landing can vault the body enough to lift the other foot. Put it back where it
                // was: the body needs it there, and a new step would take that support away.
                Vector3 spot=planted[i];
                Ground(spot,heading,out float spotGround,out _);
                spot.y=spotGround+FootRestHeight-.02f;
                targets[i]=spot;
                bend=SolveLeg(i,ActualHip(h,i),spot+soles[i]*AnkleFromFoot,forward,MinSwingLeg);
            }
            else
            {
                // A longer leg takes more of the load.
                Vector3 ankle=targets[i]+soles[i]*AnkleFromFoot+Vector3.up*((i==1?-.5f:.5f)*legDifference);
                // Commanding the pelvis to move only moves it through a leg with load on it. A leg
                // with little load would move its foot instead, and slide it across the floor, so it
                // works from where the pelvis really is, and takes on the command as its load grows.
                Vector3 hip=command+heading*new Vector3(i==0?-HipHalfWidth:HipHalfWidth,-HipDrop,0);
                float pushing=LegPush(i);
                Vector3 actual=h.pelvis.position+heading*new Vector3(i==0?-HipHalfWidth:HipHalfWidth,-HipDrop,0);
                hip.x=Mathf.Lerp(actual.x,hip.x,pushing); hip.z=Mathf.Lerp(actual.z,hip.z,pushing);
                bend=SolveLeg(i,hip,ankle,forward,Jumping?jumpShortestLeg:MinStanceLeg);
            }
            if(i==0) leftKneeDegrees=bend; else rightKneeDegrees=bend;
        }
    }

    // Pushing off, the ground force is more than the body's weight, and wherever it misses the centre of mass
    // it spins the body, which then turns all through the flight (in place, the standing law put it 5-12 cm
    // behind and the body landed pitched 45 degrees forward). So the centre of pressure goes under the centre
    // of mass, moved by what turns the spin already gained back: the moment of a vertical force F at offset d
    // is (-d.z F, 0, d.x F), and that is set to -k L.
    // Across the ground, the body speeds up away from the centre of pressure: slower than the take-off speed,
    // it goes behind the centre of mass by jumpSpeedGain per m/s lacking.
    Vector3 JumpPressure(JointBalanceHumanoid h, Quaternion heading)
    {
        float force=Mathf.Max(300,Load(0)+Load(1));
        Vector3 spin=h.angularMomentum;
        Vector3 lacking=heading*jumpVelocity-Vector3.ProjectOnPlane(h.centerOfMassVelocity,Vector3.up);
        return Vector3.ProjectOnPlane(h.centerOfMass,Vector3.up)-lacking*jumpSpeedGain+new Vector3(-spin.z,0,spin.x)*(jumpSpinGain/force);
    }

    // In the air: each leg reaches, a little bent, for where its foot should come down. That is beside
    // where the centre of mass will be on landing, a share of v/omega further on, so that the capture
    // point lands between the feet and the legs can take the body to rest. Nothing else can be done up
    // here: with no ground, any torque only turns one part against another.
    void PoseFlight(JointBalanceHumanoid h, Quaternion heading, Vector3 forward)
    {
        float g=Physics.gravity.magnitude, groundMean=(ground[0]+ground[1])*.5f;
        float landHeight=StandingPelvis-.08f;
        float drop=Mathf.Max(0,h.centerOfMass.y-(groundMean+landHeight)), vy=h.centerOfMassVelocity.y;
        float t=(vy+Mathf.Sqrt(vy*vy+2*g*drop))/g;
        Vector3 vh=Vector3.ProjectOnPlane(h.centerOfMassVelocity,Vector3.up);
        Vector3 landing=Vector3.ProjectOnPlane(h.centerOfMass,Vector3.up)+vh*t+vh/Mathf.Sqrt(g/landHeight)*jumpLandLead;
        for(int i=0;i<2;i++)
        {
            Vector3 spot=landing+heading*Vector3.right*(i==0?-HipHalfWidth:HipHalfWidth);
            if(Ground(spot,heading,out float y,out var n)) { spot.y=y+FootRestHeight; soles[i]=Quaternion.FromToRotation(Vector3.up,n)*heading; }
            else { spot.y=groundMean+FootRestHeight; soles[i]=heading; }
            Vector3 hip=ActualHip(h,i);
            Vector3 reach=Vector3.ClampMagnitude(spot+soles[i]*AnkleFromFoot-hip,jumpLandLeg);
            targets[i]=hip+reach-soles[i]*AnkleFromFoot;
            float bend=SolveLeg(i,hip,hip+reach,forward,MinSwingLeg);
            if(i==0) leftKneeDegrees=bend; else rightKneeDegrees=bend;
        }
        pelvisHeight=h.pelvis.position.y;
    }

    void HoldKnee(int leg, float wanted, Quaternion heading, float dt)
    {
        kneeTargets[leg]=Mathf.MoveTowards(kneeTargets[leg],wanted,kneeRate*dt);
        thighs[leg]=heading*Quaternion.Euler(-kneeTargets[leg]*.5f,0,0);
        shins[leg]=heading*Quaternion.Euler(kneeTargets[leg]*.5f,0,0);
    }

    // A planted ankle is run as a torque source. The torque places the centre of pressure towards the
    // capture point, which slows the fall and brings the ground force in front of the knee so that it
    // can straighten. It is limited by what the load on the foot can react, so that it is not what tips
    // the foot, and set by the angle offset of the drive's target: see below.
    void PlaceCentreOfPressure(JointBalanceHumanoid h, Quaternion heading, Vector3 servoTarget)
    {
        // Pressing beyond xi drives it back towards the target (the divergent component of
        // motion moves away from the centre of pressure).
        Vector3 wanted=phase==Phase.JumpPush?JumpPressure(h,heading):gaitStep && steppingFoot>=0?GaitPressure(heading):placingStep && steppingFoot>=0?PlacedPressure():capture+Vector3.ProjectOnPlane(capture-servoTarget,Vector3.up)*(steppingFoot>=0?singleSupportGain:copGain);
        float weight=Physics.gravity.magnitude*totalMass;
        for(int i=0;i<2;i++)
        {
            ankleTorque[i]=0;
            if(LegIsFree(i)) { ankleTargets[i]=soles[i]; continue; }
            Vector3 ankle=Vector3.ProjectOnPlane(h.feet[i].position+heading*new Vector3(0,0,AnkleFromFoot.z),Vector3.up);
            Vector3 toWanted=Vector3.ProjectOnPlane(wanted-ankle,Vector3.up);
            // With both feet down, how the load is shared moves the centre of pressure between them.
            // Pressing each foot's inner edge as well would only make them fight, and creep together.
            if(steppingFoot<0)
            {
                Vector3 toOther=Vector3.ProjectOnPlane(targets[1-i]-targets[i],Vector3.up).normalized;
                float inward=Vector3.Dot(toWanted,toOther);
                if(inward>0) toWanted-=toOther*inward;
            }
            Vector3 local=Quaternion.Inverse(heading)*toWanted;
            float x=Mathf.Clamp(local.x,-FootHalfWidth*.8f,FootHalfWidth*.8f);
            // Pressing the heel pushes the shin forward, which folds a knee that is already taking the
            // body falling backward, so the ankle gives up most of what it could do there.
            float z=Mathf.Clamp(local.z,(-FootHalfLength-AnkleFromFoot.z)*(steppingFoot>=0?heelPress:.8f),(FootHalfLength-AnkleFromFoot.z)*.8f);
            float load=smoothLoad[i];
            // Foot-frame axes: +x pitches the toe down and presses the toe, -z presses the outer edge.
            Vector3 press=new Vector3(load*z,0,-load*x)*copAuthority;
            (soles[i]*Quaternion.Inverse(h.feet[i].rotation)).ToAngleAxis(out float tilt,out Vector3 tiltAxis);
            if(tilt>180) tilt-=360;
            Vector3 level=Mathf.Abs(tilt)<.01f?Vector3.zero:Quaternion.Inverse(h.feet[i].rotation)*tiltAxis*(tilt*ankleLevelling);
            // Stance leg: the hip holds the pelvis and the foot is flat, so nothing else stops a bent
            // knee swinging sideways like a door about the hip-ankle line. Hold the shin's heading.
            Vector3 shinAhead=Quaternion.Inverse(h.feet[i].rotation)*(shinBodies[i].rotation*Vector3.forward);
            Vector3 hold=new Vector3(0,Mathf.Atan2(shinAhead.x,shinAhead.z)*Mathf.Rad2Deg*shinHeadingHold,0);
            Vector3 torque=press+level+hold;
            float limit=8+load*Mathf.Max(.05f,Mathf.Sqrt(x*x+z*z))*ankleAuthority;
            torque=Vector3.ClampMagnitude(torque,limit);
            ankleTorque[i]=torque.magnitude;
            // Never saturate: PhysX clips each axis of the drive separately, in the shin's frame, so
            // at the cap a lean of the shin turns pitch torque into roll and yaw torque. On so light
            // a link a stiff spring delivers a torque set by the angle offset, not by its stiffness.
            ankleTargets[i]=h.feet[i].rotation*Quaternion.Euler(torque.x*Mathf.Rad2Deg/anklePitchGain,torque.y*Mathf.Rad2Deg/anklePitchGain,torque.z*Mathf.Rad2Deg/ankleRollGain);
        }
    }

    // Two 0.4 m segments. Thigh and shin share the knee plane's normal so the target is a pure
    // rotation about the hinge; using a fixed world axis would ask the knee to twist.
    float SolveLeg(int i, Vector3 hip, Vector3 ankle, Vector3 forward, float minimum)
    {
        Vector3 ray=ankle-hip;
        float distance=Mathf.Clamp(ray.magnitude,minimum,MaxLeg);
        Vector3 direction=ray.normalized;
        Vector3 bend=Vector3.ProjectOnPlane(forward,direction).normalized;
        Vector3 axis=Vector3.Cross(bend,direction).normalized;
        Vector3 knee=hip+direction*(distance*.5f)+bend*Mathf.Sqrt(Mathf.Max(0,.16f-distance*distance*.25f));
        Vector3 reachableAnkle=hip+direction*distance;
        Vector3 thighUp=(hip-knee).normalized, shinUp=(knee-reachableAnkle).normalized;
        thighs[i]=Quaternion.LookRotation(Vector3.Cross(axis,thighUp),thighUp);
        shins[i]=Quaternion.LookRotation(Vector3.Cross(axis,shinUp),shinUp);
        return Quaternion.Angle(thighs[i],shins[i]);
    }

    // Quiet stance: mean support height, each leg bending independently on uneven ground.
    void PoseStanding(JointBalanceHumanoid h, Quaternion heading, Quaternion upright, Vector3 forward, Vector3 right, Vector3 mid)
    {
        Vector3 desiredPelvis=h.pelvis.position;
        desiredPelvis.y=(ground[0]+ground[1])*.5f+StandingPelvis-compression;
        for(int i=0;i<2;i++)
        {
            Vector3 hip=desiredPelvis+heading*new Vector3(i==0?-HipHalfWidth:HipHalfWidth,-HipDrop,0);
            soles[i]=Quaternion.FromToRotation(Vector3.up,normals[i])*heading;
            Vector3 ankle=targets[i]+soles[i]*AnkleFromFoot;
            Vector3 ray=ankle-hip;
            float distance=Mathf.Clamp(Mathf.Abs(ray.y),.64f,MaxLeg);
            float bendAngle=2*Mathf.Asin(Mathf.Sqrt(Mathf.Max(0,.16f-distance*distance*.25f))/.4f)*Mathf.Rad2Deg;
            if(hanging[i])
            {
                Vector3 spot=planted[i];
                Ground(spot,heading,out float spotGround,out _);
                spot.y=spotGround+FootRestHeight-.02f;
                targets[i]=spot;
                float free=SolveLeg(i,ActualHip(h,i),spot+soles[i]*AnkleFromFoot,forward,MinSwingLeg);
                if(i==0) leftKneeDegrees=free; else rightKneeDegrees=free;
                kneeOffset[i]=pitchOffset[i]=rollOffset[i]=0;
                continue;
            }
            // Preserve the ankle balance feedback while varying each stance leg's
            // length. IK from the measured pelvis alone would follow a fall.
            float spread=Vector3.Dot(targets[i]-mid,forward);
            float stagger=-Mathf.Atan2(spread,Mathf.Max(.5f,distance))*Mathf.Rad2Deg;
            // A step can leave the feet wider or narrower than the hips. Upright legs would pull
            // them back under the hips and slide them. Only after a step, so that a disturbance
            // that needs no step is handled exactly as it would be without any of this.
            float wide=steppedSinceNominal?Vector3.Dot(targets[i]-mid,right)-(i==0?-HipHalfWidth:HipHalfWidth):0;
            float splay=Mathf.Atan2(wide,Mathf.Max(.5f,distance))*Mathf.Rad2Deg;
            // Bumpless handback: begin from the pose this leg is actually in, then relax to the
            // centred stance. A jump in target here would make the landed knee extend at its
            // torque cap, and that shove is what used to set the body rocking.
            if(bumpless)
            {
                Vector3 legUp=(thighBodies[i].rotation*Vector3.up+shinBodies[i].rotation*Vector3.up).normalized;
                pitchOffset[i]=Mathf.Atan2(legUp.z,legUp.y)*Mathf.Rad2Deg-stagger;
                rollOffset[i]=-Mathf.Atan2(legUp.x,legUp.y)*Mathf.Rad2Deg-splay;
                kneeOffset[i]=KneeBend(i)-bendAngle;
            }
            // Bent knees are the weakest place to take the next push, so straighten as fast as the
            // stance allows: a long stance needs it slow, one close to normal does not.
            float relax=(StanceIsAcceptable(forward,right)?nearRelax:standingRelax)*Time.fixedDeltaTime;
            kneeOffset[i]=Mathf.MoveTowards(kneeOffset[i],0,relax);
            // The legs' lean is what carries the pelvis over the feet, so it relaxes slowly whatever
            // the knees do: fast here throws the body sideways at about a metre per second.
            float lean=standingRelax*Time.fixedDeltaTime;
            pitchOffset[i]=Mathf.MoveTowards(pitchOffset[i],0,lean);
            rollOffset[i]=Mathf.MoveTowards(rollOffset[i],0,lean);
            bendAngle+=kneeOffset[i]; stagger+=pitchOffset[i]; splay+=rollOffset[i];
            Quaternion legPlane=upright*Quaternion.Euler(0,0,splay);
            thighs[i]=legPlane*Quaternion.Euler(stagger-bendAngle*.5f,0,0);
            shins[i]=legPlane*Quaternion.Euler(stagger+bendAngle*.5f,0,0);
            if(i==0) leftKneeDegrees=bendAngle; else rightKneeDegrees=bendAngle;
        }
        bumpless=false;
    }

    public void ResetPlanner()
    {
        steppingFoot=-1; phase=Phase.Stand;
        timer=cooldown=compression=settling=quiet=0; lastFoot=0; unsafeTicks=0; sequence=0;
        unloaded[0]=unloaded[1]=0; bumpless=steppedSinceNominal=false; legDifference=loadDifference=0;
        for(int i=0;i<2;i++) kneeOffset[i]=pitchOffset[i]=rollOffset[i]=0;
        stepsStarted=stepsLanded=stepsCancelled=closingStepsTaken=0; state="Standing"; requested=voluntary=false;
        jumpRequested=false; jumpsMade=jumpsLanded=0; placedPending=false; placedSteps=0;
    }
    public Quaternion BlendLandingTarget(int motor, Quaternion target)
    {
        bool leg=motor==2 || motor==3 || motor==4 || motor==7 || motor==8 || motor==9;
        return leg && settling>0 && phase==Phase.Stand ? Quaternion.Slerp(landingPose[motor],target,Mathf.SmoothStep(0,1,1-settling/SettleTime)) : target;
    }
    // Degrees per second a joint target may move this tick.
    public float TargetSlew(int leg, bool legJoint)
    {
        if(phase==Phase.Stand) return settling>0?240f:90f;
        if(!legJoint) return 240f;
        // A push straightens the knees by some 60 degrees in a fifth of a second.
        if(Jumping) return 1200f;
        return leg==steppingFoot && !voluntary ? 1200f : 360f;
    }
}
