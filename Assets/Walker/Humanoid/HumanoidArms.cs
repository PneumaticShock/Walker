using UnityEngine;

// The arms: a hand put where it is wanted, things taken into the hand, held and let go. All of it through
// the shoulder, elbow and wrist drives: JointBalanceHumanoid asks Target() for each arm joint, and an arm
// that has nothing to do keeps the balance pose it had before.
//
// A hand's frame is its forearm's at rest: -y runs down the fingers, +z is the front of the body, +x is to
// the body's right for either hand (the arms are built hanging, without a mirror). A pose asking the hand's
// x to point the other way asks the forearm to turn half round, past what it can. Held things are rigidly
// jointed to the hand, the way a firm grip holds them, and count in the body's centre of mass.
[RequireComponent(typeof(JointBalanceHumanoid))]
public class HumanoidArms : MonoBehaviour
{
    [Tooltip("Shoulder joint to elbow joint (m).")] public float upperArm = .30f;
    [Tooltip("Elbow joint to wrist joint (m).")] public float forearm = .285f;
    [Tooltip("How fast an arm joint's target may move (degrees per second).")]
    [Range(30, 2000)] public float slew = 400f;
    [Tooltip("How close the hand has to be to a grip to close on it (m), and how well turned (degrees).")]
    public float graspDistance = .03f, graspAngle = 20f;

    [System.Serializable] public class Arm
    {
        public bool active;                 // the controller has this arm
        public Vector3 hand;                // wanted position of the middle of the hand
        public Quaternion handRotation = Quaternion.identity;
        // Which way the elbow points, in the body's heading frame: out to the arm's own side (Start sets it).
        // Out to the side joins every pose the arm takes without swinging the elbow through the arm's line:
        // hanging, reaching from above (elbow up) and the straight line ahead.
        public Vector3 elbowHint = Vector3.zero;
        [System.NonSerialized] public Quaternion upper, fore, palm;
        [System.NonSerialized] public Rigidbody held;
        [System.NonSerialized] public ConfigurableJoint grip;
        [System.NonSerialized] public Vector3 heldGrip;            // grip point in the held thing's frame
        [System.NonSerialized] public Quaternion heldInHand;       // held thing's rotation in the hand's frame
        [System.NonSerialized] public float reach;                 // how far the hand really is from its target (m)
        [System.NonSerialized] public Vector3 correction;          // what the hand target is moved by to take out a steady miss
        [System.NonSerialized] public bool solved;                 // the IK has a solution to keep to
        [System.NonSerialized] public Vector3 lastBend;            // which way the elbow pointed last
        // Take out a steady miss of the hand while this is set: for closing on a grip. Left on while holding a
        // pose, it closes a slow loop on where the hand is in the world, and the trunk sways with it.
        [System.NonSerialized] public bool correcting;
    }
    [Tooltip("Shoulder and elbow drives: their damping as a share of what the body was built with, and the shoulder's torque cap (N m; 0 keeps the built 60).")]
    public float armDampingScale = 1f, shoulderTorque = 0f;
    [Tooltip("How much of the trunk's real turn the shoulder's target is measured from (0 its target only, 1 its real turn only).")]
    [Range(0,1)] public float actualTrunkShare = 0;
    [Tooltip("Experiment: the forearm turns about its length to carry the hand's roll.")]
    public bool forearmTwist = true;
    [Tooltip("How far the forearm may turn about its length from where the swing leaves it (degrees); the wrist, which turns the hand little, takes the rest. Pointing across the body with the quillons upright (Thibault's guard at N) the wrist is at its limit, but 150 here with 20 at the wrist upset the lesson's footwork more than it helped the aim.")]
    [Range(60, 170)] public float maxForearmTwist = 120f;
    [Tooltip("How quickly a steady miss of the hand is taken out, by moving its target (1/s), and how far at most (m). Misses over 0.08 m are moves under way, not steady, and are left alone.")]
    public float correctionRate = 1.5f, maxCorrection = .06f;
    public Arm left = new Arm(), right = new Arm();
    // The trunk's target, as JointBalanceHumanoid last gave it.
    Quaternion trunkTarget = Quaternion.identity;
    JointBalanceHumanoid body;
    ConfigurableJoint[] shoulder = new ConfigurableJoint[2], elbow = new ConfigurableJoint[2], wrist = new ConfigurableJoint[2];

    public Arm this[int side] => side == 0 ? left : right;
    public bool AnyActive => left.active || right.active;
    public Rigidbody Hand(int side) => body && body.handBodies != null ? body.handBodies[side] : null;

    void Start()
    {
        body = GetComponent<JointBalanceHumanoid>();
        trunkTarget = body.Heading;
        foreach (var j in body.motors)
        {
            int side = j.name.StartsWith("Left") ? 0 : 1;
            if (j.name.Contains("Upper Arm")) shoulder[side] = j;
            else if (j.name.Contains("Forearm")) elbow[side] = j;
            else if (j.name.Contains("Hand")) wrist[side] = j;
        }
        // The arm joints' human ranges are the body's own (JointBalanceHumanoid.Awake).
        for (int side = 0; side < 2; side++)
        {
            var j = shoulder[side]; var e = elbow[side];
            foreach (var arm in new[] { j, e })
            {
                var d = arm.slerpDrive; d.positionDamper *= armDampingScale;
                if (arm == j && shoulderTorque > 0) d.maximumForce = shoulderTorque;
                arm.slerpDrive = d;
            }
        }
    }

    public Vector3 Shoulder(int side) => shoulder[side].connectedBody.transform.TransformPoint(shoulder[side].connectedAnchor);
    public float Reach => upperArm + forearm;

    // JointBalanceHumanoid: the world rotation an arm joint should have, if this controller has the arm, and
    // the parent rotation to hold it against. That is the parent as it should be, not as it is: measured
    // from the trunk as it really is, a world pose for the arm is a negative spring on the trunk (it yaws one
    // way, the shoulder turns the arm the other, and the reaction yaws it further), and with shoulder and
    // waist as stiff as each other the trunk and arm swing together at about 3 Hz. `trunk` is the trunk's
    // own target.
    public bool Target(string joint, Quaternion trunk, out Quaternion world, out Quaternion parent, out float jointSlew)
    {
        world = parent = Quaternion.identity; jointSlew = slew;
        trunkTarget = trunk;
        if (!body || !joint.Contains("Arm") && !joint.Contains("Forearm") && !joint.Contains("Hand")) return false;
        int side = joint.StartsWith("Left") ? 0 : 1;
        var a = this[side];
        if (!a.active) return false;
        // An arm made active since this controller's last FixedUpdate has no solution yet, and its rotations
        // are still zero quaternions, which the joint would reset to identity. Solve it now.
        if (!a.solved && shoulder[side] != null) Solve(side, a);
        if (!a.solved) return false;
        if (joint.Contains("Upper Arm")) { world = a.upper; parent = Quaternion.Slerp(trunk, shoulder[side].connectedBody.rotation, actualTrunkShare); }
        else if (joint.Contains("Forearm")) { world = a.fore; parent = a.upper; }
        else { world = a.palm; parent = a.fore; }
        return true;
    }

    void FixedUpdate()
    {
        if (!body || shoulder[0] == null) return;
        for (int side = 0; side < 2; side++)
        {
            var a = this[side];
            if (!a.active) { a.correction = Vector3.zero; a.solved = false; continue; }
            var hand = Hand(side);
            if (hand)
            {
                // Drives that settle short under load, and the arm model's small errors, leave the hand a few
                // centimetres off: an integral term on the target takes that out.
                a.reach = Vector3.Distance(hand.position, a.hand);
                if (a.correcting && a.reach < .08f) a.correction = Vector3.ClampMagnitude(a.correction + (a.hand - hand.position) * correctionRate * Time.fixedDeltaTime, maxCorrection);
                else if (!a.correcting) a.correction = Vector3.MoveTowards(a.correction, Vector3.zero, .05f * Time.fixedDeltaTime);
            }
            Solve(side, a);
        }
    }

    // Two-link IK to the wrist, with the elbow out towards the hint. Each segment's x axis is the elbow's
    // hinge axis, as for the legs; the forearm then turns about its own length to carry the hand's roll, so
    // the wrist is left only flexion and deviation, which is what it can do.
    void Solve(int side, Arm a)
    {
        Vector3 s = Shoulder(side);
        Vector3 w = a.hand + a.correction + a.handRotation * Vector3.up * .05f;
        Vector3 ray = w - s;
        // Never quite straight: at full stretch the elbow's direction is undefined, and the arm whips about.
        float d = Mathf.Clamp(ray.magnitude, Mathf.Abs(upperArm - forearm) + .02f, upperArm + forearm - .015f);
        Vector3 along = ray.sqrMagnitude > 1e-6f ? ray.normalized : Vector3.down;
        // The elbow goes where the hand's own turn wants the forearm: back up the hand's length from the wrist.
        // That leaves the wrist the least to bend; taking a hanging grip from above, it puts the elbow up and
        // forward with the forearm pointing down. Near straight that says little, and it swings with every
        // small move of the hand, so the hint takes over from 85% of full stretch to 97%.
        Vector3 preferred = Vector3.ProjectOnPlane(w + a.handRotation * Vector3.up * forearm - s, along);
        // A hint along the arm itself says nothing: then the way the elbow last pointed, and failing that,
        // behind the body.
        Vector3 hint = body.Heading * (a.elbowHint.sqrMagnitude > 0 ? a.elbowHint : new Vector3(side == 0 ? -1 : 1, -.3f, -.3f));
        Vector3 hinted = Vector3.ProjectOnPlane(hint, along);
        if (hinted.sqrMagnitude < .04f * hint.sqrMagnitude) hinted = Vector3.ProjectOnPlane(a.lastBend, along);
        if (hinted.sqrMagnitude < .01f) hinted = Vector3.ProjectOnPlane(-(body.Heading * Vector3.forward), along);
        if (hinted.sqrMagnitude < .01f) hinted = Vector3.ProjectOnPlane(body.Heading * Vector3.right, along);
        float stretch = d / (upperArm + forearm);
        // And only when it is clearly off the line from shoulder to wrist: with the hand in line with the
        // forearm, as Thibault holds the sword, it lies on that line, a few centimetres to any side.
        float trust = Mathf.Clamp01((.97f - stretch) / .12f) * Mathf.Clamp01((preferred.magnitude - .04f) / .08f);
        Vector3 bend = preferred.normalized * trust + hinted.normalized * (1 - trust);
        if (bend.sqrMagnitude < 1e-6f) bend = hinted;
        bend.Normalize();
        a.lastBend = bend;
        float x = (d * d + upperArm * upperArm - forearm * forearm) / (2 * d);
        float y = Mathf.Sqrt(Mathf.Max(0, upperArm * upperArm - x * x));
        Vector3 e = s + along * x + bend * y;
        Vector3 wristAt = s + along * d;
        Vector3 upperUp = (s - e).normalized, foreUp = (e - wristAt).normalized;
        // Shoulder and elbow are ball joints here. Each segment swings to its new direction by the least
        // rotation from where it hangs at rest on its parent: no hinge frame, so no second solution half round
        // about the arm's length to switch to part way through a move and jam on a limit. (A joint's own x is
        // its twist axis; turning a segment about its length is a swing, limited like any other.)
        // The trunk as it should be, not as it is (see Target): from the trunk's real turn, the arm's own
        // target would turn with every sway of it, and feed it.
        Quaternion rest = trunkTarget;
        a.upper = Quaternion.FromToRotation(rest * Vector3.up, upperUp) * rest; a.solved = true;
        Quaternion fore = Quaternion.FromToRotation(a.upper * Vector3.up, foreUp) * a.upper;
        // Then the forearm turns about its length towards the hand's facing, as far as 120 degrees: the wrist
        // takes what is left.
        Vector3 handFront = Vector3.ProjectOnPlane(a.handRotation * Vector3.forward, foreUp);
        // (Tried: keeping the turn continuous from tick to tick instead of measuring it fresh, so that it stops at
        // its limit rather than wrapping from +180 to -180. It added falls to Thibault's lesson.)
        if (forearmTwist && handFront.sqrMagnitude > 1e-4f)
            fore = Quaternion.AngleAxis(Mathf.Clamp(Vector3.SignedAngle(fore * Vector3.forward, handFront, foreUp), -maxForearmTwist, maxForearmTwist), foreUp) * fore;
        a.fore = fore;
        a.palm = a.handRotation;
    }

    // With a thing held: the hand pose that puts its grip point at `grip`, the thing turned to `rotation`.
    // With nothing held: the middle of the hand at `grip`, the hand turned to `rotation`.
    public void Pose(int side, Vector3 grip, Quaternion rotation)
    {
        var a = this[side];
        a.active = true;
        if (!a.held) { a.hand = grip; a.handRotation = rotation; return; }
        a.handRotation = rotation * Quaternion.Inverse(a.heldInHand);
        // The grip joint's anchor on the hand is where the grip point sits in the hand's frame.
        a.hand = grip - a.handRotation * a.grip.connectedAnchor;
    }
    // Where a held thing's grip point really is.
    public Vector3 HeldGrip(int side) { var a = this[side]; return a.held ? a.held.transform.TransformPoint(a.heldGrip) : Vector3.zero; }

    public void Relax(int side) { this[side].active = false; }

    // Close the hand on a thing's grip point if the hand is there and turned as `inHand` says it should be
    // held. True once held.
    public bool TryGrasp(int side, Rigidbody thing, Vector3 gripLocal, Quaternion inHand)
    {
        var a = this[side]; var hand = Hand(side);
        if (!hand || !thing) return false;
        if (a.held == thing) return true;
        Vector3 gripWorld = thing.transform.TransformPoint(gripLocal);
        float off = Vector3.Distance(hand.position, gripWorld);
        float turn = Quaternion.Angle(hand.rotation * inHand, thing.rotation);
        if (off > graspDistance || turn > graspAngle) return false;
        var j = thing.gameObject.AddComponent<ConfigurableJoint>();
        j.connectedBody = hand; j.autoConfigureConnectedAnchor = false;
        j.anchor = gripLocal; j.connectedAnchor = hand.transform.InverseTransformPoint(gripWorld);
        j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;
        j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Locked;
        j.enableCollision = false;
        a.held = thing; a.grip = j; a.heldGrip = gripLocal;
        a.heldInHand = Quaternion.Inverse(hand.rotation) * thing.rotation;
        IgnoreBody(thing, true);
        thing.solverIterations = 20; thing.solverVelocityIterations = 10;
        if (!body.carried.Contains(thing)) body.carried.Add(thing);
        return true;
    }

    public void Release(int side)
    {
        var a = this[side];
        if (a.grip) Destroy(a.grip);
        if (a.held) { body.carried.Remove(a.held); IgnoreBody(a.held, false); }
        a.held = null; a.grip = null;
    }

    // A held thing passes through its holder's body, as a hand's fingers wrap round it.
    public void IgnoreBody(Rigidbody thing, bool ignore)
    {
        foreach (var c in thing.GetComponentsInChildren<Collider>())
            foreach (var b in body.bodies)
                foreach (var bc in b.GetComponents<Collider>()) Physics.IgnoreCollision(c, bc, ignore);
    }
}
