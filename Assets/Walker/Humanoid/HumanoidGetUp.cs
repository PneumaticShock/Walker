using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// Getting up after a fall, through the same bounded joint drives as everything else: a sequence of poses
// (a pose-control graph, after Faloutsos, van de Panne and Terzopoulos, "Composable controllers for
// physics-based character animation", SIGGRAPH 2001), with feedback where balance matters:
//   Down     Lying still for a moment. Whatever the hands hold is let go (the Fell event), and the footwork
//            and arms stand aside.
//   Roll     On the back or a side: an arm reaches across and the far leg pushes, until the body is face down.
//   Floor    Face down to a squat, through the poses in `floor`: knees bent, drawn under the hips, sat back
//            onto the feet, heels lowered, arms forward for balance.
//   Rise     From the squat to standing, the centre of mass held over the feet by the ankles.
// Then the standing controller takes over, facing the way the feet now point. JointBalanceHumanoid asks
// Steer() each tick and, while it returns true, takes this controller's joint targets for every joint.
[RequireComponent(typeof(JointBalanceHumanoid))]
public class HumanoidGetUp : MonoBehaviour
{
    [Tooltip("Get up without being asked, once lying still.")] public bool automatic = true;
    [Tooltip("How long the body must lie still before getting up (s), and how slow the pelvis must be to count as still (m/s).")]
    public float lieStill = .8f, stillSpeed = .3f;
    [Tooltip("Pelvis height over the ground (m) and trunk tilt (degrees) that count as lying.")]
    public float lyingHeight = .5f, lyingTilt = 55f;
    [Tooltip("Joint target speed while getting up (degrees per second).")] public float slew = 120f;
    [Tooltip("Seconds from the squat to standing, at best.")] public float riseSeconds = 2.5f;
    [Tooltip("Friction of the palms on the floor while getting up. The body's own material slides when a push-up starts with the hands beside the chest, as it must from lying flat.")]
    public float palmFriction = 1.2f;
    [Tooltip("Walking the hands back: each step's length (m) and time (s), how far behind the knees the centre of mass must come before the hands let go (m, negative is behind), and the most steps taken.")]
    public float handStep = .15f, handStepTime = 1f, comOverKnees = -.02f;
    public int maxHandSteps = 8;
    [Tooltip("A hand step: how far the shoulders move over the other hand first (m), and the share of the step spent on that before the hand lifts.")]
    public float handShift = .12f, liftAfter = .4f;
    [Tooltip("How high a walking hand is lifted (m): low, so it is off the floor as briefly as possible.")] public float handLift = .03f;
    [System.NonSerialized] public float comFromKnees;
    int handSteps, walkingHand = -1;
    Vector3 Knees()
    {
        Vector3 sum = Vector3.zero; int n = 0;
        foreach (var j in h.motors) if (j.name.Contains("Shin")) { sum += j.transform.TransformPoint(j.anchor); n++; }
        return n > 0 ? sum / n : h.pelvis.position;
    }
    // The hand further ahead along the floor.
    int LeadingHand()
    {
        if (h.handBodies == null) return 0;
        Vector3 ahead = floorHeading * Vector3.forward;
        return Vector3.Dot(h.handBodies[0].position - h.handBodies[1].position, ahead) >= 0 ? 0 : 1;
    }
    [Tooltip("Joint target speed of an arm placing or planting its palm (degrees per second).")] public float armRate = 400f;

    // Poses, as joint angles in degrees: "joint flexion [turn-in] [in-toward-the-middle]; ...", for the joints
    // waist, head, hip, knee, ankle, shoulder, elbow and wrist ("l" or "r" in front for one side only).
    // Flexion is the anatomical sense for every joint: the waist and head bend forward, the thigh and arm
    // swing forward, the knee and elbow bend, the toes lift. The second number turns a limb in (or the
    // waist and head to the right), the third brings it in toward the body's middle (or leans them right).
    [TextArea] public string roll = "waist 20 0 0; head 30; lhip 40 0 0; rhip 70 0 30; rknee 90; lshoulder 40 0 -30; rshoulder 90 0 60; relbow 20";
    // Face down to the squat, one pose after another: "seconds: pose".
    // Status (2026-11-09): from face down, a knee push-up as a person does it (knees kept on the floor, the hips
    // folding as the hands push; chest about 0.5 m up), then the closing Z onto the knees (knees 120, hips 0.45 m).
    // Not yet: raising the trunk from there, and walking the hands back ("walkhands", a whole-arm step: the
    // shoulders shift over the other hand first, then the arm lifts from its shoulder). From a straight-legged
    // push-up instead, sliding the palms back ("slide", "grip") reached a steady kneel on hands and knees, trunk 58
    // degrees from upright. The squat and the rise work (SkillBench "getup squat").
    public string[] floor =
    {
        // The palms close beside the chest, elbows back and up, as for a narrow push-up; the shins kept flat.
        "2.5: place -.12 .12; waist -10; head -30; hip 0; shinflat 10; ankle -30",
        // Push up with the knees on the floor, the hips folding.
        "2.5: plant .15 .1; waist -10; head -30; shinflat 10; hip 35; ankle -30; shouldercap 120; elbowcap 80",
        // The closing Z: the hands push the body back while the knees and hips fold.
        "2.5: plant .15 .35; waist 0; head -10; shinflat 40; hip 100; ankle -30; shouldercap 120; elbowcap 80",
    };
    [Tooltip("Segment tilts in the squat the rise starts from (degrees forward, from vertical): shin, thigh (negative, leaning back), pelvis, trunk; and the shoulders' flexion.")]
    public float squatShin = 30, squatThigh = -90, squatPelvis = 25, squatTrunk = 55, squatArms = 90;
    [Tooltip("Where the centre of mass is held during the rise: this far ahead of the ankles (m). Standing holds it about over them.")]
    public float comAhead = .03f;
    [Tooltip("Ankle feedback during the rise: degrees of shin tilt per metre the centre of mass is ahead of the feet, and per m/s.")]
    public float comGain = 120, comDamping = 40;

    public enum Stage { Standing, Down, Roll, Floor, Lower, Rise }
    public Stage stage;
    public string state = "Standing";
    public float stageTime, rise;
    public int step;
    public int falls, getUps, rollSide = 1;
    public Vector3 comError;
    public event System.Action Fell;
    public bool Active => stage != Stage.Standing;

    JointBalanceHumanoid h;
    HumanoidFootwork f;
    HumanoidArms arms;
    Quaternion[] local;
    bool[] driven;
    float[] caps, rates;
    [System.NonSerialized] public float still, calm;
    string lastPose;
    Dictionary<string, Vector3> parsed = new Dictionary<string, Vector3>();
    string[] key;
    int[] side;
    float ankleFix;
    Quaternion standHeading = Quaternion.identity;
    static readonly Vector3 AnkleFromFoot = new Vector3(0, .075f, -.055f);

    void Awake()
    {
        h = GetComponent<JointBalanceHumanoid>();
        f = GetComponent<HumanoidFootwork>();
    }

    void Map()
    {
        int n = h.motors.Length;
        if (key != null && key.Length == n) return;
        key = new string[n]; side = new int[n]; local = new Quaternion[n]; driven = new bool[n]; caps = new float[n]; rates = new float[n];
        for (int i = 0; i < n; i++)
        {
            string m = h.motors[i].name;
            side[i] = m.StartsWith("Left") ? 0 : m.StartsWith("Right") ? 1 : -1;
            key[i] = m == "Torso" ? "waist" : m == "Head" ? "head" : m.Contains("Thigh") ? "hip" : m.Contains("Shin") ? "knee" : m.Contains("Foot") ? "ankle"
                : m.Contains("Upper Arm") ? "shoulder" : m.Contains("Forearm") ? "elbow" : m.Contains("Hand") ? "wrist" : "";
            local[i] = Quaternion.identity;
        }
    }

    // Lying: low, or low and tipped over. Low is measured from the body's own lowest point, which is on the
    // ground whatever the body is doing there (a ray down from the pelvis can pass between floor slabs).
    // The trunk's front faces down when lying prone.
    float Lowest()
    {
        float low = float.PositiveInfinity;
        foreach (var b in h.bodies) { var c = b.GetComponent<Collider>(); if (c) low = Mathf.Min(low, c.bounds.min.y); }
        return low;
    }
    public float PelvisHeight => h.pelvis.position.y - Lowest();
    public float TrunkTilt => Vector3.Angle(h.bodies[1].rotation * Vector3.up, Vector3.up);
    public bool Lying => PelvisHeight < lyingHeight && TrunkTilt > lyingTilt || PelvisHeight < .3f;
    public float FaceDown => Vector3.Dot(h.bodies[1].rotation * Vector3.forward, Vector3.down);

    // Called by JointBalanceHumanoid every physics tick, before it computes its own targets. True: this
    // controller has the joints (Target gives them).
    public bool Steer(JointBalanceHumanoid body)
    {
        h = body; Map();
        float dt = Time.fixedDeltaTime;
        stageTime += dt;
        switch (stage)
        {
            case Stage.Standing:
                // The body's own speed, smoothed: a lying body's drives jitter its parts.
                calm = Mathf.Lerp(calm, h.centerOfMassVelocity.magnitude, dt / .2f);
                still = automatic && Lying && calm < stillSpeed ? still + dt : 0;
                if (still < lieStill) return false;
                Begin();
                break;
            case Stage.Down:
                // Lying still in whatever pose the fall left (the joints keep their last targets).
                if (stageTime > .3f) { step = 0; FloorFrame(); Go(FaceDown > .6f ? Stage.Floor : Stage.Roll); }
                break;
            case Stage.Roll:
                Pose(roll, rollSide < 0);
                if (FaceDown > .7f && stageTime > .5f) { step = 0; FloorFrame(); Go(Stage.Floor); }
                else if (stageTime > 5) { rollSide = -rollSide; Go(Stage.Roll); }
                break;
            case Stage.Floor:
            {
                string[] parts = floor[step].Split(new[] { ':' }, 2);
                float seconds = float.Parse(parts[0], CultureInfo.InvariantCulture);
                // "walkhands": with the arms straight but the centre of mass still ahead of the knees, the hands
                // walk back, the one further forward first, a step at a time, until it is over them.
                if (parts[1].Contains("walkhands"))
                {
                    comFromKnees = Vector3.Dot(h.centerOfMass - Knees(), floorHeading * Vector3.forward);
                    if (stageTime > handStepTime)
                    {
                        if (comFromKnees <= comOverKnees || handSteps >= maxHandSteps) { handSteps = 0; walkingHand = -1; if (step + 1 < floor.Length) step++; Go(Stage.Floor); break; }
                        walkingHand = LeadingHand(); handSteps++; stageTime = 0;
                    }
                    poseProgress = stageTime / handStepTime;
                    // The shoulders go over the hand that stays (to the right while the left hand steps).
                    string shiftTo = (walkingHand == 0 ? handShift : -handShift).ToString(CultureInfo.InvariantCulture);
                    Pose(walkingHand < 0 ? parts[1] : parts[1] + (walkingHand == 0 ? "; lstep " : "; rstep ") + handStep.ToString(CultureInfo.InvariantCulture) + "; shift " + shiftTo + "; n" + handSteps, false);
                    break;
                }
                poseProgress = stageTime / seconds;
                Pose(parts[1], false);
                if (stageTime > seconds)
                {
                    if (step + 1 < floor.Length) { step++; Go(Stage.Floor); }
                    else { Go(Stage.Rise); rise = 0; ankleFix = 0; standHeading = FeetHeading(); }
                }
                break;
            }
            case Stage.Lower:
                Rise(dt, 0);
                if (rise <= 0 && stageTime > 1) Go(Stage.Rise);
                break;
            case Stage.Rise:
                Rise(dt, 1);
                if (rise >= 1 && stageTime > .5f) { Finish(); return false; }
                break;
        }
        // Fallen back down from part way up: start again.
        if (stage == Stage.Rise && stageTime > 1.5f && PelvisHeight < .3f) Go(Stage.Down);
        state = stage == Stage.Floor ? "Floor " + step : stage.ToString();
        return true;
    }

    // The joint target for a motor, as the child's rotation in its parent's frame; false leaves it to the
    // standing controller (never, while getting up).
    public bool Target(int motor, out Quaternion rotation, out float cap) => Target(motor, out rotation, out cap, out _);
    // `rate`: how fast the joint's target may move (degrees per second).
    public bool Target(int motor, out Quaternion rotation, out float cap, out float rate)
    {
        rate = rates != null && motor < rates.Length && rates[motor] > 0 ? rates[motor] : slew;
        rotation = local != null && motor < local.Length ? local[motor] : Quaternion.identity;
        cap = caps != null && motor < caps.Length ? caps[motor] : 0;
        return driven != null && motor < driven.Length && driven[motor];
    }

    public void Begin()
    {
        Map();
        falls++;
        Fell?.Invoke();
        Palms(true);
        arms = GetComponent<HumanoidArms>();
        if (arms) { arms.Relax(0); arms.Relax(1); }
        if (f) f.enabled = false;
        // Hold the pose the fall left: each joint's current target, as a local rotation.
        for (int i = 0; i < h.motors.Length; i++) { local[i] = Quaternion.Inverse(h.motors[i].targetRotation); driven[i] = true; caps[i] = 0; }
        // The roll pose turns the body over to its left; mirrored, to its right. Roll the way it already leans:
        // to the left when the right side is the higher.
        rollSide = Vector3.Dot(h.bodies[1].rotation * Vector3.right, Vector3.up) >= 0 ? 1 : -1;
        Go(Stage.Down);
    }

    void Go(Stage next) { stage = next; stageTime = 0; lastPose = null; state = next.ToString(); }

    void Finish()
    {
        getUps++;
        stage = Stage.Standing; state = "Standing"; still = 0;
        for (int i = 0; i < driven.Length; i++) driven[i] = false;
        h.Face(standHeading);
        h.ResumeFromGetUp();
        if (f) { f.ResetPlanner(); f.enabled = true; }
        Palms(false);
    }

    // Joint angles from a pose string into local rotations (mirrored for the right side).
    void Pose(string pose, bool mirror)
    {
        bool fresh = pose != lastPose;
        if (fresh) { Parse(pose); lastPose = pose; }
        // "plant up back": the palms stay where they are on the floor, and the arms are solved for shoulders
        // `up` m higher and `back` m further back than they are, so the arms push the chest up and back
        // through their drives instead of sweeping along the floor.
        bool plant = parsed.TryGetValue("plant", out var push);
        // "place ahead out" (or "lplace", "rplace"): over the pose, each palm goes in an arc to the floor that far
        // ahead of its shoulder and out from it, and is held there as if planted.
        if (!plant && (parsed.ContainsKey("place") || parsed.ContainsKey("lplace") || parsed.ContainsKey("rplace"))) { plant = true; push = Vector3.zero; }
        // Each pose starts from where the palms are; "lstep d" (or "rstep") walks that hand d m back toward the
        // knees over the pose, lifted on the way, while the other stays put.
        if (plant && (!planting || fresh)) for (int s = 0; s < 2; s++) planted[s] = h.handBodies != null && h.handBodies[s] ? h.handBodies[s].position : Vector3.zero;
        planting = plant;
        if (fresh) Grip(parsed.TryGetValue("grip", out var grip) ? grip.x : palmFriction);
        for (int i = 0; i < key.Length; i++)
        {
            int s = side[i];
            if (mirror && s >= 0) s = 1 - s;
            string k = key[i];
            Vector3 a = s >= 0 && parsed.TryGetValue((s == 0 ? "l" : "r") + k, out var one) ? one : parsed.TryGetValue(k, out var both) ? both : Vector3.zero;
            local[i] = Local(k, side[i], a);
            driven[i] = true;
            // Feedback on the world, measured from the thigh as it really is. "shinflat a": the knee presses the
            // shin flat on the floor (tilted a degrees more), so its torque lifts the thigh instead, and the hips
            // rise and go back over the heels as far as the arms let them. "pelvis a": the hip holds the pelvis at
            // a degrees from upright, leaning forward, whatever the thigh does.
            var thigh = k == "knee" ? h.motors[i].connectedBody : k == "hip" ? h.motors[i].GetComponent<Rigidbody>() : null;
            string sideKey = side[i] == 0 ? "l" : side[i] == 1 ? "r" : "";
            if (k == "knee" && (parsed.TryGetValue(sideKey + "shinflat", out var flat) || !parsed.ContainsKey((side[i] == 0 ? "r" : "l") + "shinflat") && parsed.TryGetValue("shinflat", out flat)))
                local[i] = Quaternion.Inverse(thigh.rotation) * floorHeading * Quaternion.AngleAxis(90 + flat.x, Vector3.right);
            if (k == "hip" && parsed.TryGetValue("pelvis", out var tilt))
                local[i] = Quaternion.Inverse(floorHeading * Quaternion.AngleAxis(tilt.x, Vector3.right)) * thigh.rotation;
            rates[i] = 0;
            // An arm on its solution follows it closely: lagging, its joints each turn their own way and the
            // palm sweeps out along the floor instead of going where it is sent.
            if (plant && (k == "shoulder" || k == "elbow") && h.handBodies != null) { local[i] = PlantedArm(i, side[i], push, k == "shoulder"); rates[i] = armRate; }
            // "kneecap 20": that joint's torque limit for this pose (N m), to let it give way; 0 keeps the built one.
            caps[i] = s >= 0 && parsed.TryGetValue((s == 0 ? "l" : "r") + k + "cap", out var oneCap) ? oneCap.x : parsed.TryGetValue(k + "cap", out var bothCap) ? bothCap.x : 0;
        }
    }

    bool planting;
    float poseProgress;
    readonly Vector3[] planted = new Vector3[2];

    // Two-link arm solution from a wanted shoulder point to the planted palm, the elbow bent back toward the
    // feet, out and up as in a push-up; each segment swung the least way from its rest, relative to the trunk
    // and upper arm as they are.
    Quaternion PlantedArm(int motor, int s, Vector3 push, bool shoulderJoint)
    {
        var sj = h.motors[shoulderJoint ? motor : motor - 1];
        var ej = h.motors[shoulderJoint ? motor + 1 : motor];
        Vector3 shoulder = sj.transform.TransformPoint(sj.anchor), elbowNow = ej.transform.TransformPoint(ej.anchor);
        float a = Vector3.Distance(shoulder, elbowNow), b = Vector3.Distance(elbowNow, h.handBodies[s].position);
        Vector3 ahead = floorHeading * Vector3.forward, outward = floorHeading * (s == 0 ? Vector3.left : Vector3.right);
        // A hand being moved is solved from its shoulder as it is; only planted hands push.
        bool stepping = parsed.ContainsKey((s == 0 ? "l" : "r") + "step");
        bool moving = stepping && poseProgress > liftAfter || parsed.ContainsKey((s == 0 ? "l" : "r") + "place") || parsed.ContainsKey("place");
        // "shift a": the planted arms push the shoulders a m to the right (negative, left), over one hand.
        float sideways = parsed.TryGetValue("shift", out var shiftBy) ? shiftBy.x * Mathf.SmoothStep(0, 1, Mathf.Clamp01(poseProgress / liftAfter)) : 0;
        Vector3 from = moving ? shoulder : shoulder + Vector3.up * push.x - ahead * push.y + floorHeading * Vector3.right * sideways;
        Vector3 palm = planted[s];
        if (parsed.TryGetValue("slide", out var slide))
            palm = planted[s] - ahead * (slide.x * Mathf.SmoothStep(0, 1, Mathf.Clamp01(poseProgress / .8f)));
        if (parsed.TryGetValue((s == 0 ? "l" : "r") + "place", out var where) || parsed.TryGetValue("place", out where))
        {
            float p = Mathf.Clamp01(poseProgress / .8f);
            Vector3 goal = new Vector3(shoulder.x, Lowest() + .04f, shoulder.z) + ahead * where.x + outward * where.y;
            // Up first, across held high (the joints lag their targets, and a palm put down early sticks), down last.
            float lift = .2f * Mathf.SmoothStep(0, 1, p / .2f) * (1 - Mathf.SmoothStep(0, 1, (p - .75f) / .25f));
            palm = Vector3.Lerp(planted[s], goal, Mathf.SmoothStep(0, 1, Mathf.Clamp01((p - .1f) / .65f))) + Vector3.up * lift;
        }
        if (parsed.TryGetValue((s == 0 ? "l" : "r") + "step", out var step))
        {
            float p = Mathf.Clamp01((poseProgress - liftAfter) / (.9f - liftAfter));
            palm = planted[s] - ahead * (step.x * Mathf.SmoothStep(0, 1, p)) + Vector3.up * (handLift * Mathf.Sin(Mathf.PI * p));
        }
        Vector3 d = palm - from;
        float L = Mathf.Clamp(d.magnitude, Mathf.Abs(a - b) + .01f, a + b - .001f);
        Vector3 along = d.normalized;
        // The elbows go back and up, close to the body, as in a narrow push-up.
        Vector3 hint = Vector3.ProjectOnPlane(-ahead * .5f + Vector3.up, along).normalized;
        float cosA = Mathf.Clamp((a * a + L * L - b * b) / (2 * a * L), -1, 1);
        Vector3 upper = along * cosA + hint * Mathf.Sqrt(1 - cosA * cosA);
        Vector3 fore = (from + along * L - (from + upper * a)).normalized;
        // The elbow is a hinge: the upper arm is turned about its length so that its front faces the way the
        // forearm bends, and the elbow then bends about its own x axis only.
        Quaternion trunk = sj.connectedBody.rotation;
        Vector3 front = Vector3.ProjectOnPlane(fore, upper);
        if (front.sqrMagnitude < 1e-4f) front = Vector3.ProjectOnPlane(trunk * Vector3.forward, upper);
        Quaternion upperWorld = Quaternion.LookRotation(front.normalized, -upper);
        if (shoulderJoint) return Quaternion.Inverse(trunk) * upperWorld;
        return Quaternion.AngleAxis(-Vector3.Angle(upper, fore), Vector3.right);
    }

    // Anatomical angles to the joint's local rotation. A segment hangs along -y (the trunk and head stand
    // along +y); local +x turns its lower end back. Flexion swings it forward, then it is brought in toward
    // the middle, about its parent's forward axis, and turned in about its own length.
    static Quaternion Local(string k, int s, Vector3 a)
    {
        float flex = a.x, turn = a.y, inward = a.z;
        float sign = k == "waist" || k == "head" || k == "knee" ? 1 : -1;   // knee flexion is +x; hip, shoulder, elbow, ankle -x
        if (k == "ankle") sign = -1;
        float z, y;
        if (s < 0) { y = turn; z = -inward; }            // waist, head: turn right, lean right
        else if (s == 0) { y = turn; z = inward; }       // left: +z brings the lower end right, toward the middle
        else { y = -turn; z = -inward; }
        return Quaternion.AngleAxis(sign * flex, Vector3.right) * Quaternion.AngleAxis(z, Vector3.forward) * Quaternion.AngleAxis(y, Vector3.up);
    }

    void Parse(string pose)
    {
        parsed.Clear();
        foreach (var part in pose.Split(';'))
        {
            var w = part.Trim().Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
            if (w.Length < 2) continue;
            var v = Vector3.zero;
            for (int j = 1; j < w.Length && j < 4; j++) v[j - 1] = float.Parse(w[j], CultureInfo.InvariantCulture);
            parsed[w[0].ToLowerInvariant()] = v;
        }
    }

    // Lying face down: "ahead" is where the head is.
    Quaternion floorHeading = Quaternion.identity;
    void FloorFrame()
    {
        Vector3 ahead = Vector3.ProjectOnPlane(h.bodies[1].rotation * Vector3.up, Vector3.up);
        floorHeading = ahead.sqrMagnitude > .01f ? Quaternion.LookRotation(ahead.normalized, Vector3.up) : Quaternion.LookRotation(Vector3.ProjectOnPlane(h.bodies[1].rotation * Vector3.forward, Vector3.up).normalized, Vector3.up);
    }

    // Which way the feet point, level.
    Quaternion FeetHeading()
    {
        Vector3 ahead = Vector3.ProjectOnPlane(h.feet[0].rotation * Vector3.forward + h.feet[1].rotation * Vector3.forward, Vector3.up);
        return ahead.sqrMagnitude > .01f ? Quaternion.LookRotation(ahead.normalized, Vector3.up) : h.Heading;
    }

    // From the squat to standing: each segment's tilt from vertical eased from the squat's to upright, the
    // whole chain tilted at the ankles by however much keeps the centre of mass over the middle of the feet.
    // The rise waits while the centre of mass is off the feet.
    void Rise(float dt, float goal)
    {
        Quaternion heading = standHeading;
        Vector3 forward = heading * Vector3.forward;
        Vector3 ankles = Vector3.zero;
        foreach (var foot in h.feet) ankles += foot.position + foot.rotation * AnkleFromFoot;
        Vector3 feet = ankles * .5f + forward * comAhead;
        comError = Vector3.ProjectOnPlane(h.centerOfMass - feet, Vector3.up);
        float ahead = Vector3.Dot(comError, forward), speed = Vector3.Dot(h.centerOfMassVelocity, forward);
        float wantFix = Mathf.Clamp(-(comGain * ahead + comDamping * speed), -20, 20);
        ankleFix = Mathf.MoveTowards(ankleFix, wantFix, 60 * dt);
        rise = Mathf.MoveTowards(rise, goal, dt / riseSeconds * Mathf.Clamp01(1.5f - Mathf.Abs(ahead) / .06f));
        float s = Mathf.SmoothStep(0, 1, rise);
        float shin = Mathf.Lerp(squatShin, 0, s) + ankleFix, thigh = Mathf.Lerp(squatThigh, 0, s) + ankleFix;
        float pelvis = Mathf.Lerp(squatPelvis, 0, s) + ankleFix, trunk = Mathf.Lerp(squatTrunk, 0, s) + ankleFix;
        Quaternion Tilt(float degrees) => heading * Quaternion.AngleAxis(degrees, Vector3.right);
        Quaternion footW = heading, shinW = Tilt(shin), thighW = Tilt(thigh), pelvisW = Tilt(pelvis), trunkW = Tilt(trunk);
        Quaternion headW = Tilt(Mathf.Lerp(20, 0, s));
        for (int i = 0; i < key.Length; i++)
        {
            string k = key[i];
            Quaternion q = Quaternion.identity;
            if (k == "ankle") q = Quaternion.Inverse(shinW) * footW;
            else if (k == "knee") q = Quaternion.Inverse(thighW) * shinW;
            else if (k == "hip") q = Quaternion.Inverse(pelvisW) * thighW;
            else if (k == "waist") q = Quaternion.Inverse(pelvisW) * trunkW;
            else if (k == "head") q = Quaternion.Inverse(trunkW) * headW;
            else if (k == "shoulder") q = Local(k, side[i], new Vector3(Mathf.Lerp(squatArms, 0, s), 0, -10 * (1 - s)));
            else q = Local(k, side[i], Vector3.zero);
            local[i] = q; driven[i] = true; caps[i] = 0;
        }
    }

    // Starts getting up now, from wherever the body is (for tests and for game code that knocks it down).
    public void GetUpNow() { if (!Active) { Map(); Begin(); } }
    // From standing: down into the squat, and up again (a test of the last two stages on their own).
    public void SquatAndRise() { Map(); Begin(); falls--; Go(Stage.Lower); rise = 1; ankleFix = 0; standHeading = FeetHeading(); }
    // Stop getting up and give the joints back (a reset).
    public void Abort()
    {
        if (!Active) return;
        stage = Stage.Standing; state = "Standing"; still = 0;
        if (driven != null) for (int i = 0; i < driven.Length; i++) driven[i] = false;
        if (f) { f.ResetPlanner(); f.enabled = true; }
        Palms(false);
    }
    public Quaternion Facing => standHeading;

    void Grip(float friction)
    {
        if (!palm) return;
        palm.staticFriction = friction; palm.dynamicFriction = friction * .9f;
    }

    // The palms grip the floor while getting up; the hands' own material comes back afterwards.
    PhysicsMaterial palm, handMaterial;
    void Palms(bool grip)
    {
        if (h.handBodies == null) return;
        if (!palm) palm = new PhysicsMaterial("Palm") { staticFriction = palmFriction, dynamicFriction = palmFriction * .9f, frictionCombine = PhysicsMaterialCombine.Maximum };
        foreach (var hand in h.handBodies)
        {
            var c = hand ? hand.GetComponent<Collider>() : null;
            if (!c) continue;
            if (grip) { if (c.sharedMaterial != palm) handMaterial = c.sharedMaterial; c.sharedMaterial = palm; }
            else if (c.sharedMaterial == palm) c.sharedMaterial = handMaterial;
        }
    }
}
