using System.Collections;
using UnityEngine;

// Taking a sword and holding it: the hand reaches the grip, closes on it, draws the sword out of its rack
// and brings it up. Afterwards anything that poses the sword (a fencing manual) goes through Hold().
// Moves are interpolated hand poses, from wherever the hand really is, so nothing starts with a jump.
[RequireComponent(typeof(HumanoidArms))]
public class SwordHandling : MonoBehaviour
{
    [Tooltip("The hand that holds the sword: 0 left, 1 right.")]
    public int side = 1;
    [Tooltip("How the sword sits in the hand, as its rotation in the hand's frame. Point down the fingers, quillons across the palm: in line with the arm, as Thibault holds it.")]
    public Vector3 inHandEuler = new Vector3(180, 0, 0);
    public Quaternion InHand => Quaternion.Euler(inHandEuler);
    public Sword sword;
    public string state = "Empty-handed";
    public bool Holding => sword && arms[side].held == sword.body;
    HumanoidArms arms;
    JointBalanceHumanoid body;
    Vector3 posedGrip; Quaternion posedRotation;

    void Awake() { arms = GetComponent<HumanoidArms>(); body = GetComponent<JointBalanceHumanoid>(); }

    // Where the sword's grip and the sword's rotation are being commanded now (or really are, before that).
    public Vector3 CommandedGrip => arms[side].active ? posedGrip : (Holding ? sword.Grip : arms.Hand(side).position);
    public Quaternion CommandedRotation => arms[side].active ? posedRotation : (Holding ? sword.transform.rotation : arms.Hand(side).rotation * InHand);

    [Tooltip("Rate at which the blade's measured direction error is taken out of the commanded rotation (1/s); 0 is off. The wrist settles a few degrees short under the sword's weight, which at the length of a lunge misses the face.")]
    public float aimRate = 2f;
    [Tooltip("Most the aim correction may turn the commanded rotation (degrees).")]
    public float aimLimit = 20f;
    Quaternion aimFix = Quaternion.identity, lastPosed = Quaternion.identity;

    // Pose the held sword: its grip here, turned so. With no sword held, the hand goes where the grip would be.
    public void Hold(Vector3 grip, Quaternion rotation)
    {
        if (Holding && aimRate > 0)
        {
            // Integrate only while the pose is nearly still and the blade near it: during a move the lag is
            // the drives', and winding it up would overshoot at the end.
            float dt = Time.fixedDeltaTime;
            Vector3 want = rotation * Vector3.up, have = sword.Along;
            if (Quaternion.Angle(lastPosed, rotation) < 20 * dt && Vector3.Angle(have, want) < 25)
            {
                Quaternion.FromToRotation(have, want).ToAngleAxis(out float angle, out Vector3 axis);
                if (angle > 180) angle -= 360;
                aimFix = Quaternion.AngleAxis(angle * aimRate * dt, axis) * aimFix;
                float total = Quaternion.Angle(Quaternion.identity, aimFix);
                if (total > aimLimit) aimFix = Quaternion.Slerp(Quaternion.identity, aimFix, aimLimit / total);
            }
        }
        else aimFix = Quaternion.identity;
        lastPosed = rotation;
        posedGrip = grip; posedRotation = rotation;
        if (Holding) arms.Pose(side, grip, aimFix * rotation);
        else arms.Pose(side, grip, rotation * Quaternion.Inverse(InHand));
    }

    // From the pose now to `to`, smoothly, over `seconds`. `to` is asked again each tick, so it can move.
    public IEnumerator MoveTo(System.Func<(Vector3 grip, Quaternion rotation)> to, float seconds)
    {
        Vector3 fromGrip = CommandedGrip; Quaternion fromRotation = CommandedRotation;
        for (float t = 0; t < seconds; t += Time.fixedDeltaTime)
        {
            var (g, r) = to();
            float s = Mathf.SmoothStep(0, 1, t / seconds);
            Hold(Vector3.Lerp(fromGrip, g, s), Quaternion.Slerp(fromRotation, r, s));
            yield return new WaitForFixedUpdate();
        }
        var (gEnd, rEnd) = to(); Hold(gEnd, rEnd);
    }

    // Take the sword from where it is (hanging point down in a rack) and bring it up to `then`.
    public IEnumerator PickUp(Sword s, System.Func<(Vector3, Quaternion)> then, float raiseSeconds = 1.2f)
    {
        sword = s;
        Vector3 back = -(body.Heading * Vector3.forward);
        // Fingers round a grip pass through it: no knocking the sword off its rack on the way in.
        arms.IgnoreBody(s.body, true);
        state = "Reaching";
        arms[side].correcting = true;
        yield return MoveTo(() => (s.Grip + back * .14f + Vector3.up * .02f, s.transform.rotation), 1.0f);
        yield return MoveTo(() => (s.Grip, s.transform.rotation), .5f);
        state = "Grasping";
        for (float t = 0; t < 1.5f && !Holding; t += Time.fixedDeltaTime)
        {
            Hold(s.Grip, s.transform.rotation);
            arms.TryGrasp(side, s.body, Vector3.zero, InHand);
            yield return new WaitForFixedUpdate();
        }
        arms[side].correcting = false;
        if (!Holding) { state = "Missed the grip"; arms.IgnoreBody(s.body, false); yield break; }
        state = "Drawing";
        Vector3 from = s.Grip; Quaternion hanging = s.transform.rotation;
        // Only as far as clears the prongs: further back by the chest the forearm cannot stay pointing down
        // without bending the elbow past its limit.
        yield return MoveTo(() => (from + back * .12f + Vector3.up * .03f, hanging), .6f);
        state = "Raising";
        // Turned up first, close to the body, and only then carried out: swung up at arm's length in one move,
        // the sword lags the wrist by 70 degrees and then whips round, and the reaction turns the whole body and
        // drives it back a step. Close in, it turns about the hand with little to move.
        yield return MoveTo(() =>
        {
            var (g, r) = then();
            Vector3 shoulder = arms.Shoulder(side);
            return (shoulder + (g - shoulder) * .55f + Vector3.down * .05f, r);
        }, raiseSeconds * .6f);
        yield return MoveTo(then, raiseSeconds * .6f);
        state = "Holding";
    }

    // Put a sword straight into the hand where it is, as if it had been taken up already (for tests that
    // begin with the sword in hand).
    public bool PutInHand(Sword s)
    {
        sword = s;
        var hand = arms.Hand(side);
        s.transform.SetPositionAndRotation(hand.position, hand.rotation * InHand);
        s.body.position = hand.position; s.body.rotation = hand.rotation * InHand;
        s.body.linearVelocity = hand.linearVelocity; s.body.angularVelocity = Vector3.zero;
        Physics.SyncTransforms();
        bool held = arms.TryGrasp(side, s.body, Vector3.zero, InHand);
        posedGrip = s.Grip; posedRotation = s.transform.rotation;
        state = held ? "Holding" : "Missed the grip";
        return held;
    }

    public void Drop()
    {
        arms.Release(side); arms.Relax(side);
        state = "Empty-handed";
    }
}
