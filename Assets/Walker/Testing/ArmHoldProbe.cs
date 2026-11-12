using UnityEngine;

// Test fixture: holds the right hand out straight ahead (optionally with a sword in it) and records how far
// the hand wanders sideways and up and down. Add in Play mode; read `report` after `seconds`.
public class ArmHoldProbe : MonoBehaviour
{
    public bool withSword, uncounted;
    public float seconds = 4f, stretch = .97f;
    public string report = "", path = "";
    float clock, minX = 9, maxX = -9, minY = 9, maxY = -9;
    HumanoidArms arms; SwordHandling handling; JointBalanceHumanoid body; Sword sword;
    void Start()
    {
        body = GetComponent<JointBalanceHumanoid>();
        arms = GetComponent<HumanoidArms>() ?? gameObject.AddComponent<HumanoidArms>();
        handling = GetComponent<SwordHandling>(); if (!handling) handling = gameObject.AddComponent<SwordHandling>();
    }
    (Vector3, Quaternion) Pose()
    {
        Quaternion heading = body.Heading;
        Vector3 along = heading * new Vector3(0, -.15f, 1).normalized;
        return (arms.Shoulder(1) + along * (arms.Reach * stretch + .05f), Quaternion.LookRotation(Vector3.Cross(heading * Vector3.right, along), along));
    }
    void FixedUpdate()
    {
        if (!arms) return;
        clock += Time.fixedDeltaTime;
        var (g, r) = Pose();
        if (clock < 1.5f) { var s = Mathf.SmoothStep(0, 1, clock / 1.5f); handling.Hold(Vector3.Lerp(arms.Hand(1).position, g, s), r); return; }
        if (withSword && !sword) { sword = Sword.Build(arms.Hand(1).position, arms.Hand(1).rotation * handling.InHand); handling.PutInHand(sword); if (uncounted) body.carried.Clear(); }
        handling.Hold(g, r);
        if (clock < 2.5f) return;
        var p = arms.Hand(1).position;
        if (Mathf.Repeat(clock, .1f) < Time.fixedDeltaTime) { float ty = Vector3.SignedAngle(Vector3.forward, Vector3.ProjectOnPlane(body.bodies[1].rotation * Vector3.forward, Vector3.up), Vector3.up), py = Vector3.SignedAngle(Vector3.forward, Vector3.ProjectOnPlane(body.pelvis.rotation * Vector3.forward, Vector3.up), Vector3.up); path += "trunk " + ty.ToString("0.0") + " pelvis " + py.ToString("0.0") + " hand x " + p.x.ToString("0.00") + " | "; }
        minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
        if (clock > 2.5f + seconds && report == "") report = "sword " + withSword + " stretch " + stretch + ": x spread " + (maxX - minX).ToString("0.000") + " y spread " + (maxY - minY).ToString("0.000") + " miss " + Vector3.Distance(p, arms.right.hand).ToString("0.000");
    }
}
