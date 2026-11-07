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
    public enum Phase { Stand, Prepare, Swing, DoubleSupport, Hold, WeightShift }

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
    [Range(.01f,.2f)] public float holdCentre = .3f;
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
    [Header("Diagnostics")]
    public Phase phase;
    public string state = "Standing";
    public int steppingFoot = -1, stepsStarted, stepsLanded, stepsCancelled, closingStepsTaken;
    public float leftKneeDegrees, rightKneeDegrees, settling;
    public Vector3 landingTarget, supportPoint, capture;
    public float supportMargin;

    readonly Quaternion[] landingPose = new Quaternion[12];
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

    void Start()
    {
        var h=GetComponent<JointBalanceHumanoid>();
        foreach(var body in h.bodies) totalMass+=body.mass;
        for(int i=0;i<2;i++)
        {
            sensors[i]=h.feet[i].gameObject.AddComponent<FootContactSensor>();
            shinBodies[i]=h.feet[i].GetComponent<ConfigurableJoint>().connectedBody;
            thighBodies[i]=shinBodies[i].GetComponent<ConfigurableJoint>().connectedBody;
        }
    }
    public bool Contact(int foot) => sensors[foot] && sensors[foot].touching;
    public float Load(int foot) => sensors[foot] ? sensors[foot].load : 0;
    public Vector3 Pressure(int foot) => sensors[foot] ? sensors[foot].pressure : Vector3.zero;
    public Vector3 Target(int foot) => targets[foot];
    public float PelvisCommand => pelvisHeight;
    public bool IsSwinging => phase==Phase.Swing && steppingFoot>=0;
    // In single support the leg on the ground holds the pelvis level from its hip, rather than
    // tracking a pose: a knee that holds height, and a hip that reacts against the actual thigh.
    public bool HoldsPelvis => phase==Phase.Swing;
    // A leg off the ground tracks a foot target from the actual pelvis, like a swing leg.
    // That includes a foot only lightly on the ground: a leg holding the pelvis has no target of
    // its own, so without real load on it there is nothing to stop the foot sliding away.
    public bool LegIsFree(int leg) => (IsSwinging && leg==steppingFoot) || (phase==Phase.DoubleSupport && !bearing[leg]) || ((phase==Phase.Stand || phase==Phase.Hold) && hanging[leg]);
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
        y=float.NegativeInfinity; n=Vector3.zero; int count=0;
        // Sample the footprint, not just the centre: a heel or toe can straddle a slab edge.
        for(int i=0;i<5;i++)
        {
            Vector3 offset=i==0?Vector3.zero:new Vector3((i==1||i==2?-.055f:.055f),0,(i%2==0?.105f:-.105f));
            if(Physics.Raycast(p+heading*offset+Vector3.up*.45f,Vector3.down,out var hit,1.1f,1,QueryTriggerInteraction.Ignore) && hit.normal.y>.45f)
            { y=Mathf.Max(y,hit.point.y); n+=hit.normal; count++; }
        }
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

    // Accept the plan if there is ground there; otherwise shorten the step until there is.
    bool FindFoothold(Vector3 goal, Quaternion heading, int stance, out Vector3 foothold)
    {
        foothold=goal;
        for(float scale=1;scale>=.3f;scale-=.14f)
        {
            Vector3 candidate=swingStart+Vector3.ProjectOnPlane(goal-swingStart,Vector3.up)*scale;
            if(Ground(candidate,heading,out float y,out _) && Mathf.Abs(y-ground[stance])<.15f)
            { foothold=candidate; foothold.y=y+FootRestHeight; return true; }
        }
        return false;
    }

    void BeginStep(JointBalanceHumanoid h, Quaternion heading, Vector3 right, int foot, bool isVoluntary)
    {
        steppingFoot=foot; lastFoot=foot; voluntary=isVoluntary; phase=Phase.Swing;
        swingStart=h.feet[foot].position; startHeight=swingStart.y;
        kneeTargets[1-foot]=KneeBend(1-foot);
        timer=0; quiet=0; unsafeTicks=0; stepsStarted++; requested=false;
        landingTarget=swingStart;
        if(!isVoluntary && FindFoothold(Plan(h,foot,heading,right,stepTime),heading,1-foot,out var foothold)) landingTarget=foothold;
    }

    Vector3 Plan(JointBalanceHumanoid h, int foot, Quaternion heading, Vector3 right, float remaining) =>
        sequence>0 && CapturedBy(h,heading,1-foot)?PlanClosing(h,foot,heading*Vector3.forward,right,1-foot):PlanLanding(h,foot,heading,right,remaining);

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
        steppedSinceNominal=true; sequence++;
        if(landed && voluntary) closingStepsTaken++;
        steppingFoot=-1; phase=Phase.DoubleSupport; timer=0; quiet=0; unsafeTicks=0; cooldown=.03f; leadLoaded=0;
        for(int i=0;i<2;i++) { kneeTargets[i]=KneeBend(i); bearing[i]=true; }
    }

    public void Plan(JointBalanceHumanoid h, Quaternion heading, Quaternion upright)
    {
        float dt=Time.fixedDeltaTime;
        settling=Mathf.Max(0,settling-dt);
        cooldown=Mathf.Max(0,cooldown-dt);
        compression=Mathf.MoveTowards(compression,standingCompression,dt*.035f);
        bool valid=true;
        for(int i=0;i<2;i++)
        {
            targets[i]=h.feet[i].position;
            valid &= Ground(targets[i],heading,out ground[i],out normals[i]);
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
            if(smoothLoad[i]>plantLoad || phase==Phase.Swing || phase==Phase.Prepare || phase==Phase.Stand) planted[i]=h.feet[i].position;
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
        switch(phase)
        {
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
                bool comfortable=StanceIsAcceptable(forward,right);
                bool stepNeeded=supportMargin>stepAgainMargin && unsafeTicks>=2;
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
                if(canStep && supportMargin>stepAgainMargin && unsafeTicks>=2)
                {
                    int foot=FootToMove(mid,right);
                    if(Contact(1-foot)) BeginStep(h,heading,right,foot,false);
                    break;
                }
                float holdFlatSpeed=Vector3.ProjectOnPlane(h.centerOfMassVelocity,Vector3.up).magnitude;
                bool calm=holdFlatSpeed<holdSpeed && Vector3.ProjectOnPlane(capture-mid,Vector3.up).magnitude<holdCentre && bearing[0] && bearing[1];
                quiet=calm?quiet+dt:0;
                bool normal=StanceIsNormal(forward,right);
                if(holdOnly) break;
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
                    if(FindFoothold(closed,heading,stayFoot,out var closedSpot)) closed=closedSpot;
                    landingTarget=closed;
                }
                else if(timer>1.6f) { phase=Phase.Hold; timer=0; quiet=0; }
                break;
        }

        if(phase==Phase.Swing)
        {
            int i=steppingFoot, stance=1-i;
            // A disturbance during an unhurried step turns it into a real recovery step.
            if(voluntary && SupportDistance(h,capture,heading,i!=0,i!=1)>.04f) voluntary=false;
            float duration=voluntary?stepTime/closingPace:stepTime;
            timer+=dt;
            float progress=Mathf.Clamp01(timer/duration);
            // Stop replanning just before arrival so that it has a fixed place to come down on.
            if(!voluntary && progress<.85f && FindFoothold(Plan(h,i,heading,right,duration-timer),heading,stance,out var foothold))
                landingTarget=Vector3.Lerp(landingTarget,foothold,1-Mathf.Exp(-40*dt));
            if(Ground(landingTarget,heading,out float gy,out var gn)) { landingTarget.y=gy+FootRestHeight; normals[i]=gn; }
            // Clear the ground first, then travel.
            float travel=Mathf.SmoothStep(0,1,Mathf.Clamp01((progress-.08f)/.92f));
            Vector3 p=Vector3.Lerp(swingStart,landingTarget,travel);
            p.y=Mathf.Lerp(startHeight,landingTarget.y,travel)+stepClearance*Mathf.Sin(Mathf.PI*Mathf.Pow(progress,swingShape));
            // Touchdown comes from contact, not the clock: past the end of the
            // trajectory, keep reaching down until the sole actually lands.
            if(timer>duration) p.y-=Mathf.Min(.10f,(timer-duration)*.7f);
            targets[i]=p;
            servoTarget=targets[stance];
            state=voluntary?"Closing step":"Recovery step";
            // A toe scuff on the way out is not a landing. Real load is, wherever the foot is:
            // the body has come down on it, and dragging it onward would only trip.
            bool touched=timer>.08f && Contact(i) && (progress>touchdownProgress && Load(i)>5 || progress>.4f && Load(i)>scuffLoad) && h.feet[i].position.y-ground[i]<.11f;
            if(touched) FinishSwing(true);
            else if(timer>duration+.30f) FinishSwing(false);
        }
        supportPoint=phase==Phase.Stand?Vector3.Lerp(supportPoint,mid,1-Mathf.Exp(-8*dt)):servoTarget;

        if(phase==Phase.Stand) PoseStanding(h,heading,upright,forward,right,mid);
        else { PoseStepping(h,heading,forward,servoTarget,dt); PlaceCentreOfPressure(h,heading,servoTarget); }
    }

    void GoToStand(JointBalanceHumanoid h)
    {
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
        return spread<normalSpread && width>normalMinWidth && width<normalMaxWidth;
    }
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
            float above=ground[stance]+stanceHeight-HipDrop-stanceAnkle.y;
            float span=Mathf.Sqrt(above*above+Vector3.ProjectOnPlane(stanceAnkle-stanceHip,Vector3.up).sqrMagnitude);
            float kneeForHeight=2*Mathf.Acos(Mathf.Clamp(span/.8f,0,1))*Mathf.Rad2Deg;
            // Falling behind the foot, the ground force passes behind the knee and a bent knee folds.
            bool behind=Vector3.Dot(capture-stanceAnkle,forward)<0;
            HoldKnee(stance,behind?stanceKnee:Mathf.Clamp(kneeForHeight,stanceKnee,70),heading,dt);
            float bend=SolveLeg(swing,ActualHip(h,swing),targets[swing]+soles[swing]*AnkleFromFoot,forward,MinSwingLeg);
            leftKneeDegrees=swing==0?bend:kneeTargets[stance]; rightKneeDegrees=swing==1?bend:kneeTargets[stance];
            pelvisHeight=h.pelvis.position.y;
            return;
        }
        // Double support: IK to a commanded pelvis keeps the pose consistent with wherever the
        // feet actually landed, and the servo moves the pelvis towards where it should be.
        Vector3 command=h.pelvis.position;
        // The shift before a closing step is voluntary, so there is no reason for it to be violent.
        command+=Vector3.ClampMagnitude(Vector3.ProjectOnPlane(servoTarget-capture,Vector3.up)*servoGain,servoLimit*(phase==Phase.WeightShift?closingShift:1));
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
        if(phase!=Phase.Prepare) pelvisHeight=Mathf.MoveTowards(pelvisHeight,Mathf.Max(Mathf.Min(reachable,groundMean+tall),lowest),dt*riseRate);
        // Damp the height as well as setting it. Stiff legs catching a falling body otherwise throw it
        // back up clear of the ground, and a foot in the air has nothing to hold it in place.
        command.y=pelvisHeight-h.pelvis.linearVelocity.y*heightDamping;
        // Share the load so that the two feet together put the centre of pressure where it is wanted.
        Vector3 wantedPressure=capture+Vector3.ProjectOnPlane(capture-servoTarget,Vector3.up)*copGain;
        Vector3 between=Vector3.ProjectOnPlane(targets[1]-targets[0],Vector3.up);
        // Both feet keep some load: an unloaded foot would be lifted clear by the length difference.
        float share=Mathf.Clamp(Vector3.Dot(wantedPressure-targets[0],between)/Mathf.Max(.01f,between.sqrMagnitude),loadShareMargin,1-loadShareMargin);
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
                bend=SolveLeg(i,hip,ankle,forward,MinStanceLeg);
            }
            if(i==0) leftKneeDegrees=bend; else rightKneeDegrees=bend;
        }
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
        Vector3 wanted=capture+Vector3.ProjectOnPlane(capture-servoTarget,Vector3.up)*(steppingFoot>=0?singleSupportGain:copGain);
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
        return leg==steppingFoot && !voluntary ? 1200f : 360f;
    }
}
