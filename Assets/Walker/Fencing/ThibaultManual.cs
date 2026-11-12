using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Girard Thibault, Académie de l'Espée (Leiden, 1630): the first manual for the humanoid to fence by.
//
// What is taken from it:
//  - The mysterious circle, drawn on the floor between the two fencers: its diameter is the fencer's stature,
//    and its radius the sword's length from point to cross. Each fencer begins with the right foot on the
//    circle at an end of the diameter that joins them: ours at C, the opponent's at X.
//  - Points on it to step to: the ends N of the diameter across, and the corners of the square inscribed in
//    the circle (S and G on our side, the right and the left). The plates letter many more points along the
//    square's sides and the chords; these are the ones the actions below use.
//  - The straight line, the guard: standing upright on a narrow stance, the right foot forward, the sword
//    arm straight and the sword in line with it, the point at the opponent's face.
//  - Approaching the opponent round the circle to the right, outside his line, each step taken with the right
//    foot, the left following so that the stance is kept.
//  - The thrust, with the arm and sword kept in line, and the imbrocade: the hand carried high, the nails
//    turned down, and the point brought down onto the opponent's breast.
// Simplifications here: the body cannot turn, so the fencer faces along the diameter throughout; the opponent
// is a dummy that stands still; the letters S and G are placed at the square's near corners.
public class ThibaultManual : MonoBehaviour
{
    public JointBalanceHumanoid fencer;
    public SwordHandling sword;
    [Tooltip("The opponent: a dummy with children named Face and Breast on its surface.")]
    public Transform opponent;
    [Tooltip("Stature of the fencer, the circle's diameter (m).")]
    public float stature = 1.8f;
    [Tooltip("Middle of the circle, on the floor.")]
    public Vector3 centre;
    [Tooltip("Along the floor from our end of the diameter to the opponent's.")]
    public Vector3 toward = Vector3.forward;
    [Tooltip("Stance: how far the left foot stands behind the right, and to its left (m).")]
    public float stanceLength = .22f, stanceWidth = .34f;
    [Tooltip("Longest single step the footwork takes (m); longer moves on the circle are made in several.")]
    public float maxStep = .3f;
    [Tooltip("A foot that lands further than this from its point steps again (m).")]
    public float adjustAbove = .045f;
    [Tooltip("How fast the point is laid onto its mark once on the target (1/s).")]
    public float pointRate = 6f;
    Vector3 pointFix;
    [Tooltip("How far past the target's surface the point is driven (m).")]
    public float drive = .02f;

    public string action = "None";
    public float Radius => stature * .5f;
    Vector3 Right => Vector3.Cross(Vector3.up, toward).normalized;
    HumanoidFootwork feet;

    // A point on the circle's figure, from its coordinates in radii: x to the right, z towards the opponent.
    public Vector3 Point(float x, float z) => centre + (Right * x + toward * z) * Radius;
    public Vector3 C => Point(0, -1);
    public Vector3 X => Point(0, 1);
    public Vector3 NRight => Point(1, 0);
    public Vector3 NLeft => Point(-1, 0);
    public Vector3 S => Point(.7071f, -.7071f);
    public Vector3 G => Point(-.7071f, -.7071f);

    public Vector3 Face => opponent.Find("Face").position;
    public Vector3 Breast => opponent.Find("Breast").position;

    // Measurements for tests: the guard's straightness and aim, how far the feet landed from their points,
    // how close the point came to what it was sent at.
    public float guardBend, guardAim, worstFoot, closest = float.PositiveInfinity;
    public readonly List<string> log = new List<string>();

    void Awake() { if (fencer) feet = fencer.GetComponent<HumanoidFootwork>(); }

    // Where the left foot stands when the right is at `rightFoot`: behind it and to its left. Thibault's fencer
    // turns to keep facing across the circle; this one cannot turn, so the stance stays square to the way it
    // faces. Turned towards X on a body that still faces along the diameter, at N the left foot would stand
    // straight behind the right, on a stance with no width at all.
    public Vector3 LeftFootFor(Vector3 rightFoot) => rightFoot - toward * stanceLength - Right * stanceWidth;

    // The straight line: the sword continues the straight arm, its point at the face.
    public (Vector3, Quaternion) StraightLine()
    {
        var arms = fencer.GetComponent<HumanoidArms>();
        Vector3 shoulder = arms.Shoulder(sword.side);
        Vector3 dir = (Face - shoulder).normalized;
        Vector3 grip = shoulder + dir * (arms.Reach - .01f + .05f);
        return (grip, Aligned(dir, Vector3.up));
    }
    // A sword turned to point along `dir`, with its quillons as near to `quillons` as that allows.
    static Quaternion Aligned(Vector3 dir, Vector3 quillons)
    {
        Vector3 x = Vector3.ProjectOnPlane(quillons, dir).normalized;
        if (x.sqrMagnitude < .5f) x = Vector3.ProjectOnPlane(Vector3.right, dir).normalized;
        return Quaternion.LookRotation(Vector3.Cross(x, dir), dir);
    }

    // A thrust that keeps the line: the point driven at `target` along the arm, if it is within reach.
    public (Vector3, Quaternion) ThrustAt(Vector3 target)
    {
        var arms = fencer.GetComponent<HumanoidArms>();
        Vector3 shoulder = arms.Shoulder(sword.side);
        Vector3 dir = (target - shoulder).normalized;
        float reach = arms.Reach - .01f + .05f;
        Vector3 grip = shoulder + dir * Mathf.Min(reach, Vector3.Distance(shoulder, target) + drive - Sword.TipAt);
        return (grip, Aligned(dir, Vector3.up));
    }

    // The imbrocade: the hand high above the shoulder, nails down, and the point coming down onto `target`.
    public (Vector3, Quaternion) ImbrocadeAt(Vector3 target, float through)
    {
        var arms = fencer.GetComponent<HumanoidArms>();
        Vector3 shoulder = arms.Shoulder(sword.side);
        Vector3 high = shoulder + Vector3.up * .32f + Vector3.ProjectOnPlane(target - shoulder, Vector3.up).normalized * .25f;
        Vector3 dir = (target - high).normalized;
        // The grip on the line from high to the target, as far along it as the arm allows, the point past
        // the target by `through`.
        Vector3 grip = target + dir * through - dir * Sword.TipAt;
        Vector3 fromShoulder = grip - shoulder;
        float reach = arms.Reach - .01f + .05f;
        if (fromShoulder.magnitude > reach) grip = shoulder + fromShoulder.normalized * reach;
        // Nails down: the quillons lie across, from side to side.
        return (grip, Aligned(dir, Vector3.Cross(Vector3.up, toward)));
    }

    // Measure the guard as it is: the angle between the arm and the blade, and how far the face is from the
    // blade's line.
    public void MeasureGuard()
    {
        var arms = fencer.GetComponent<HumanoidArms>();
        if (!sword.Holding) return;
        var s = sword.sword;
        Vector3 shoulder = arms.Shoulder(sword.side);
        guardBend = Vector3.Angle(s.Grip - shoulder, s.Along);
        Vector3 toFace = Face - s.Grip;
        guardAim = Vector3.Cross(s.Along, toFace).magnitude;
    }

    // Move the stance to have the right foot at `to`: a step of the right foot, the left following, as many
    // times as it takes with no step longer than maxStep. A step of 0.7 m from S to N or C to S at once is a
    // lunge, and lands far off its mark. Only where the feet end up is measured.
    IEnumerator Travel(Vector3 to, string name)
    {
        Vector3 from = Vector3.ProjectOnPlane(fencer.feet[1].position, Vector3.up), end = Vector3.ProjectOnPlane(to, Vector3.up);
        int n = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(from, end) / maxStep));
        // Mostly forward, the back foot goes first: the front one first would stretch the stance by the
        // whole step, to a lunge of 0.5 m the body cannot hold; this way it shortens and opens out again.
        // Mostly to the side, the right foot leads, as the manual has it.
        Vector3 each = (end - from) / n;
        bool backFirst = Vector3.Dot(each, toward) > Mathf.Abs(Vector3.Dot(each, Right));
        for (int k = 1; k <= n; k++)
        {
            Vector3 r = Vector3.Lerp(from, end, k / (float)n) + Vector3.up * to.y;
            if (backFirst) yield return StepAndWait(0, LeftFootFor(r), name, k == n);
            yield return StepAndWait(1, r, name, k == n);
            if (!backFirst) yield return StepAndWait(0, LeftFootFor(r), name, k == n);
        }
    }

    IEnumerator StepAndWait(int foot, Vector3 place, string name, bool measure = true)
    {
        float Off() => Vector3.Distance(Vector3.ProjectOnPlane(fencer.feet[foot].position, Vector3.up), Vector3.ProjectOnPlane(place, Vector3.up));
        // A step that lands off its point is put right with a small one, as a fencer settles his feet on the
        // circle; at most twice.
        for (int tries = 0; tries < 3 && (tries == 0 || measure && Off() > adjustAbove); tries++)
        {
            feet.StepTo(foot, place);
            yield return new WaitForFixedUpdate();
            int before = feet.stepsLanded;
            for (float t = 0; t < 8 && (feet.PlacedStepPending || feet.stepsLanded == before); t += Time.fixedDeltaTime) yield return new WaitForFixedUpdate();
            // Let the landing settle before measuring where the foot ended up.
            for (float t = 0; t < .4f; t += Time.fixedDeltaTime) yield return new WaitForFixedUpdate();
        }
        if (!measure) yield break;
        float off = Off();
        worstFoot = Mathf.Max(worstFoot, off);
        log.Add(name + " " + (foot == 0 ? "left" : "right") + " foot off by " + off.ToString("0.000") + " m");
    }

    IEnumerator HoldFor(System.Func<(Vector3, Quaternion)> pose, float seconds, bool measure = false)
    {
        for (float t = 0; t < seconds; t += Time.fixedDeltaTime)
        {
            var (g, r) = pose(); sword.Hold(g, r);
            if (measure && t > seconds * .5f) MeasureGuard();
            yield return new WaitForFixedUpdate();
        }
    }
    IEnumerator Track(Vector3 target, float seconds)
    {
        for (float t = 0; t < seconds; t += Time.fixedDeltaTime)
        {
            if (sword.Holding) closest = Mathf.Min(closest, Vector3.Distance(sword.sword.Tip, target));
            yield return new WaitForFixedUpdate();
        }
    }

    // Keep the sword in a pose while footwork goes on.
    Coroutine holding;
    void HoldWhile(System.Func<(Vector3, Quaternion)> pose) { StopHolding(); holding = StartCoroutine(HoldFor(pose, 1e6f)); }
    public void StopHolding() { if (holding != null) StopCoroutine(holding); holding = null; }

    // The lesson as a test runs it: into the guard at C, round to the right by S to N, and an imbrocade to
    // the breast; then back to the guard, held. The sword must be in hand, the fencer standing square with
    // the left foot about where LeftFootFor(C) puts it (see GuardFrom).
    public IEnumerator Lesson()
    {
        yield return TakeGuard();
        yield return Attack();
    }

    // The first part of the lesson: the straight line, then the right foot forward onto C. The guard is held
    // afterwards, until something else poses the sword.
    public IEnumerator TakeGuard()
    {
        if (!feet) feet = fencer.GetComponent<HumanoidFootwork>();
        feet.closingSteps = false;
        // The guard is a long stance meant to be kept: the standing law holds it.
        feet.heldStanceSpread = stanceLength + .1f;
        action = "Guard"; log.Clear(); worstFoot = 0; closest = float.PositiveInfinity;
        yield return sword.MoveTo(StraightLine, 1.0f);
        // Into the stance: the right foot forward onto C. Not the left back: the weight then stays on the
        // front foot, and the sword held out leans it further forward still.
        HoldWhile(StraightLine);
        yield return StepAndWait(1, C, "guard");
        StopHolding();
        yield return HoldFor(StraightLine, 1.5f, true);
        log.Add("guard: arm-blade angle " + guardBend.ToString("0.0") + " deg, face off the line by " + guardAim.ToString("0.000") + " m");
        bendAtGuard = guardBend; aimAtGuard = guardAim;
        action = "In guard";
        HoldWhile(StraightLine);
    }
    float bendAtGuard, aimAtGuard;

    // The rest of the lesson, from the guard at C: round by S to N, the imbrocade, and back to the guard, held.
    public IEnumerator Attack()
    {
        action = "To S";
        HoldWhile(StraightLine);
        yield return Travel(S, "S");
        action = "To N";
        yield return Travel(NRight, "N");
        StopHolding();
        action = "Imbrocade";
        pointFix = Vector3.zero;
        // The hand carried up first, unhurried: lifted in half a second the sword's weight leaves the wrist 30
        // degrees behind, and the point comes down from wherever that left it.
        yield return sword.MoveTo(() => ImbrocadeAt(Breast + pointFix, -.15f), .8f);
        StartCoroutine(Track(Breast, 1.4f));
        yield return sword.MoveTo(() => ImbrocadeAt(Breast + pointFix, drive), .3f);
        // On the breast, the point is laid onto the mark by eye: whatever it misses by across the blade is
        // taken out of where it is sent.
        for (float t = 0; t < 1.1f; t += Time.fixedDeltaTime)
        {
            if (sword.Holding)
            {
                Vector3 miss = Vector3.ProjectOnPlane(Breast - sword.sword.Tip, sword.sword.Along);
                if (miss.magnitude < .3f) pointFix = Vector3.ClampMagnitude(pointFix + miss * (pointRate * Time.fixedDeltaTime), .2f);
            }
            var (g, r) = ImbrocadeAt(Breast + pointFix, drive); sword.Hold(g, r);
            yield return new WaitForFixedUpdate();
        }
        log.Add("imbrocade: point came within " + closest.ToString("0.000") + " m of the breast");
        action = "Recover";
        // Back to the guard as slowly as it was left: rolled back from nails down in less, the forearm's turn
        // and the shoulder's lag together throw the arm about.
        yield return sword.MoveTo(StraightLine, 1.2f);
        yield return HoldFor(StraightLine, 2f, true);
        log.Add("recovered: arm-blade angle " + guardBend.ToString("0.0") + " deg, face off the line by " + guardAim.ToString("0.000") + " m");
        guardBend = Mathf.Max(guardBend, bendAtGuard); guardAim = Mathf.Max(guardAim, aimAtGuard);
        action = "Done";
        HoldWhile(StraightLine);
    }

    // Lay the circle out for a fencer standing square, about to step into the guard: C is where the right
    // foot goes, one stance length ahead of where it is, and the opponent faces it across the diameter.
    public void GuardFrom(Vector3 rightFoot, Vector3 forward)
    {
        toward = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
        centre = Vector3.ProjectOnPlane(rightFoot, Vector3.up) + toward * (stanceLength + Radius);
    }

    // A dummy to fence: a post the height of a man, with the face and breast marked on its front.
    public static Transform BuildDummy(Vector3 foot, Vector3 facing, Material look = null)
    {
        var go = new GameObject("Opponent Dummy");
        go.transform.SetPositionAndRotation(foot, Quaternion.LookRotation(Vector3.ProjectOnPlane(facing, Vector3.up), Vector3.up));
        var post = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        post.name = "Body"; post.transform.SetParent(go.transform, false);
        post.transform.localPosition = new Vector3(0, .875f, 0); post.transform.localScale = new Vector3(.36f, .875f, .36f);
        if (look) post.GetComponent<Renderer>().sharedMaterial = look;
        var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        head.name = "Head"; head.transform.SetParent(go.transform, false);
        head.transform.localPosition = new Vector3(0, 1.6f, 0); head.transform.localScale = Vector3.one * .24f;
        if (look) head.GetComponent<Renderer>().sharedMaterial = look;
        var face = new GameObject("Face").transform; face.SetParent(go.transform, false); face.localPosition = new Vector3(0, 1.6f, .12f);
        var breast = new GameObject("Breast").transform; breast.SetParent(go.transform, false); breast.localPosition = new Vector3(0, 1.3f, .18f);
        // New colliders sit where they were made, at the world's origin, until physics catches up.
        Physics.SyncTransforms();
        return go.transform;
    }
}
