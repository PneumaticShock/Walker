using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Keyboard control of the humanoid's skills, to try them by hand in Play mode: walking, jumping, taking a sword
// from a rack, and Thibault's lesson against a dummy. Everything goes through the same calls the skill bench
// tests (HumanoidFootwork, SwordHandling, ThibaultManual); nothing here moves a body directly.
// Runs before the humanoid starts, to keep an untouched copy of it for a second fencer.
[DefaultExecutionOrder(-1000)]
public class SkillsDemo : MonoBehaviour
{
    public BalanceTestArena arena;
    [Tooltip("Walking speed with W, and with Shift held (m/s). The gait is tested from 0.3 to 0.9 m/s.")]
    public float walkSpeed = .5f, fastSpeed = .9f;
    [Tooltip("Sideways and backward speed with A, D and S (m/s). Untested: the bench walks forward only.")]
    public float stepSpeed = .25f;
    [Tooltip("Height of a jump with J (m). Jumps in place of 0.10 m land about 9 times in 10.")]
    public float jumpHeight = .10f;
    [Tooltip("Start with the floor still (the moving floor is the arena's ball-test mode; G toggles it).")]
    public bool stillFloor = true;
    [Tooltip("The main camera keeps its offset from the humanoid as it walks (C toggles).")]
    public bool followCamera = true;
    [Tooltip("V: a second fencer faces the first across Thibault's circle; both take swords and the guard, and each attacks in turn.")]
    public bool sparring = true;
    Vector3 cameraOffset;
    GameObject template, second;
    Transform targetA, targetB;
    ThibaultManual manualB;

    JointBalanceHumanoid h;
    HumanoidFootwork f;
    HumanoidArms arms;
    SwordHandling handling;
    ThibaultManual manual;
    BumpyFloor bumps;
    Sword sword;
    GameObject rack;
    readonly List<GameObject> made = new List<GameObject>();
    Coroutine routine;
    bool carrying, walking, savedClosing;
    string doing = "Standing";

    void Awake()
    {
        if (!arena) arena = FindAnyObjectByType<BalanceTestArena>();
        // The humanoid adds its hands and changes its joints as it starts, so the copy is made before that, and
        // kept inactive until a second fencer is wanted.
        if (sparring && arena && arena.humanoid)
        {
            var original = arena.humanoid.gameObject;
            bool active = original.activeSelf;
            original.SetActive(false);
            template = Instantiate(original, transform);
            template.name = "Second Fencer";
            original.SetActive(active);
        }
    }

    void Start()
    {
        if (!arena) arena = FindAnyObjectByType<BalanceTestArena>();
        h = arena.humanoid; f = h.GetComponent<HumanoidFootwork>();
        arms = h.GetComponent<HumanoidArms>(); if (!arms) arms = h.gameObject.AddComponent<HumanoidArms>();
        handling = h.GetComponent<SwordHandling>(); if (!handling) handling = h.gameObject.AddComponent<SwordHandling>();
        savedClosing = f.closingSteps;
        if (stillFloor) arena.shiftingGround = false;
        if (Camera.main) cameraOffset = Camera.main.transform.position - Vector3.ProjectOnPlane(h.pelvis.position, Vector3.up);
    }

    void LateUpdate()
    {
        if (!followCamera || !Camera.main || h.pelvis.position.y < -2) return;
        Vector3 want = Vector3.ProjectOnPlane(h.pelvis.position, Vector3.up) + cameraOffset;
        Camera.main.transform.position = Vector3.Lerp(Camera.main.transform.position, want, 1 - Mathf.Exp(-3 * Time.deltaTime));
    }

    void Update()
    {
        var k = Keyboard.current;
        if (k == null) return;
        // Off the edge of the floor: back to the start.
        if (h.pelvis.position.y < -5) { Clear(); arena.ResetTrial(); doing = "Fell off the floor: reset"; return; }
        if (k.rKey.wasPressedThisFrame) { Clear(); return; }
        if (k.cKey.wasPressedThisFrame) followCamera = !followCamera;
        if (k.kKey.wasPressedThisFrame) ToggleBumps();
        bool busy = routine != null;
        if (!busy)
        {
            if (k.jKey.wasPressedThisFrame && !f.Jumping) { f.jumpHeight = jumpHeight; f.Jump(Vector3.zero); doing = "Jumping"; }
            if (k.pKey.wasPressedThisFrame && !handling.Holding) routine = StartCoroutine(PickUp());
            if (k.lKey.wasPressedThisFrame) routine = StartCoroutine(Lesson());
            if (k.vKey.wasPressedThisFrame && template) StartSparring();
            if (k.xKey.wasPressedThisFrame && handling.Holding) { handling.Drop(); carrying = false; doing = "Dropped the sword"; }
        }
        // Walking: forward with W (Shift for faster), and slowly to the side or back with A, D, S.
        Vector3 want = Vector3.zero;
        if (!busy)
        {
            if (k.wKey.isPressed) want.z = k.leftShiftKey.isPressed || k.rightShiftKey.isPressed ? fastSpeed : walkSpeed;
            else if (k.sKey.isPressed) want.z = -stepSpeed;
            if (k.aKey.isPressed) want.x = -stepSpeed;
            else if (k.dKey.isPressed) want.x = stepSpeed;
        }
        if (want != Vector3.zero) { f.gaitVelocity = want; walking = true; doing = "Walking " + want.magnitude.ToString("0.0") + " m/s"; }
        else if (walking) { f.gaitVelocity = Vector3.zero; walking = false; doing = "Standing"; }
    }

    void FixedUpdate()
    {
        // Where each fencer's face and breast are, for the other to aim at.
        if (second) { Mark(targetA, h); Mark(targetB, second.GetComponent<JointBalanceHumanoid>()); }
        // The sword stays in the carry pose while nothing else poses it.
        if (carrying && routine == null && handling.Holding) { var (g, r) = Carry(); handling.Hold(g, r); }
    }

    // Carried as Thibault holds it: the arm out ahead and a little down, the sword in line with it.
    (Vector3, Quaternion) Carry()
    {
        Quaternion heading = h.Heading;
        Vector3 along = heading * new Vector3(0, -.15f, 1).normalized, quillons = heading * Vector3.right;
        return (arms.Shoulder(handling.side) + along * (arms.Reach + .04f), Quaternion.LookRotation(Vector3.Cross(quillons, along), along));
    }

    IEnumerator Settle()
    {
        f.gaitVelocity = Vector3.zero; walking = false;
        for (float t = 0; t < 3 && (f.phase != HumanoidFootwork.Phase.Stand || f.Jumping); t += Time.deltaTime) yield return null;
        for (float t = 0; t < .5f; t += Time.deltaTime) yield return null;
    }

    IEnumerator PickUp()
    {
        yield return TakeSword();
        routine = null;
    }

    IEnumerator TakeSword()
    {
        doing = "Taking the sword";
        yield return Settle();
        Quaternion heading = h.Heading;
        Vector3 grip = arms.Shoulder(handling.side) + heading * new Vector3(0, -.33f, .30f);
        sword = Sword.Build(grip + Vector3.up * .005f, heading * Quaternion.Euler(180, 0, 0));
        rack = SwordRack.Build(grip, heading);
        made.Add(sword.gameObject); made.Add(rack);
        for (float t = 0; t < .6f; t += Time.fixedDeltaTime) yield return new WaitForFixedUpdate();
        yield return handling.PickUp(sword, Carry);
        carrying = handling.Holding;
        doing = carrying ? "Holding the sword" : "Missed the grip: " + handling.state;
    }

    IEnumerator Lesson()
    {
        // With no sword in hand, it is taken from its rack first: that is the way into the lesson the bench
        // finds steadiest (3 of 3 standing, against 1 of 3 with a sword put straight into the raised hand).
        if (!handling.Holding)
        {
            yield return TakeSword();
            if (!handling.Holding) { routine = null; yield break; }
        }
        else yield return Settle();
        doing = "Thibault's lesson";
        // The rack stands where the guard's first step goes: put away.
        if (rack) Destroy(rack);
        for (float t = 0; t < 1.5f; t += Time.fixedDeltaTime) { var (g, r) = Carry(); handling.Hold(g, r); yield return new WaitForFixedUpdate(); }
        Quaternion heading = h.Heading; Vector3 forward = heading * Vector3.forward;
        if (!manual) manual = gameObject.AddComponent<ThibaultManual>();
        manual.fencer = h; manual.sword = handling;
        manual.GuardFrom(h.feet[1].position, forward);
        if (manual.opponent) Destroy(manual.opponent.gameObject);
        manual.opponent = ThibaultManual.BuildDummy(manual.X + forward * .15f, -forward);
        made.Add(manual.opponent.gameObject);
        yield return manual.Lesson();
        doing = "Lesson done: " + string.Join(" | ", manual.log);
        carrying = false;   // the manual keeps the guard
        routine = null;
    }

    public void StartSparring() { if (routine == null) routine = StartCoroutine(Spar()); }

    static void Mark(Transform target, JointBalanceHumanoid body)
    {
        if (!target || !body) return;
        Rigidbody head = body.bodies[2], trunk = body.bodies[1];
        target.Find("Face").position = head.position + head.rotation * new Vector3(0, -.02f, .11f);
        target.Find("Breast").position = trunk.position + trunk.rotation * new Vector3(0, .02f, .12f);
    }
    static Transform Target(string name)
    {
        var t = new GameObject(name).transform;
        new GameObject("Face").transform.SetParent(t, false);
        new GameObject("Breast").transform.SetParent(t, false);
        return t;
    }

    // A bout on Thibault's circle. The second fencer is put where its guard step lands on the first one's X,
    // so both fence on the one circle. Both take a sword from a rack and the guard; then the first goes round
    // by S to N and delivers the imbrocade while the second keeps its guard, and then the second does.
    IEnumerator Spar()
    {
        doing = "Sparring: the first fencer takes a sword";
        if (!handling.Holding)
        {
            yield return TakeSword();
            if (!handling.Holding) { routine = null; yield break; }
        }
        else yield return Settle();
        if (rack) Destroy(rack);
        Quaternion heading = h.Heading; Vector3 forward = heading * Vector3.forward;
        if (!manual) manual = gameObject.AddComponent<ThibaultManual>();
        manual.StopAllCoroutines(); manual.StopHolding();
        manual.fencer = h; manual.sword = handling;
        manual.GuardFrom(h.feet[1].position, forward);
        if (manual.opponent && manual.opponent != targetB) Destroy(manual.opponent.gameObject);

        if (!second)
        {
            doing = "Sparring: the second fencer steps in";
            Quaternion turn = Quaternion.AngleAxis(180, Vector3.up) * heading;
            Vector3 footFromRoot = template.transform.Find("Right Foot").position - template.transform.position;
            Vector3 root = manual.X + forward * manual.stanceLength - turn * footFromRoot;
            root.y = h.transform.position.y;
            second = Instantiate(template, root, turn * template.transform.rotation);
            second.transform.SetParent(null);
            second.name = "Second Fencer";
            second.SetActive(true);
            Apart(handling.sword ? handling.sword.gameObject : null, second);
            targetA = Target("First Fencer Target"); targetB = Target("Second Fencer Target");
            for (float t = 0; t < 2.5f; t += Time.fixedDeltaTime) yield return new WaitForFixedUpdate();
        }
        var hB = second.GetComponent<JointBalanceHumanoid>();
        var armsB = second.GetComponent<HumanoidArms>(); if (!armsB) armsB = second.AddComponent<HumanoidArms>();
        var handB = second.GetComponent<SwordHandling>(); if (!handB) handB = second.AddComponent<SwordHandling>();
        yield return new WaitForFixedUpdate();
        if (!handB.Holding)
        {
            doing = "Sparring: the second fencer takes a sword";
            Quaternion headingB = hB.Heading;
            Vector3 grip = armsB.Shoulder(handB.side) + headingB * new Vector3(0, -.33f, .30f);
            var swordB = Sword.Build(grip + Vector3.up * .005f, headingB * Quaternion.Euler(180, 0, 0));
            var rackB = SwordRack.Build(grip, headingB);
            made.Add(swordB.gameObject);
            for (float t = 0; t < .6f; t += Time.fixedDeltaTime) yield return new WaitForFixedUpdate();
            yield return handB.PickUp(swordB, () => CarryFor(hB, armsB, handB));
            Destroy(rackB);
            Apart(swordB.gameObject, h.gameObject);
            if (handling.sword) Apart(swordB.gameObject, handling.sword.gameObject);
            if (!handB.Holding) { doing = "Sparring: the second fencer missed the grip"; routine = null; yield break; }
        }
        Mark(targetA, h); Mark(targetB, hB);
        manual.opponent = targetB;
        if (!manualB) manualB = second.AddComponent<ThibaultManual>();
        manualB.fencer = hB; manualB.sword = handB; manualB.opponent = targetA;
        manualB.GuardFrom(hB.feet[1].position, hB.Heading * Vector3.forward);
        carrying = false;

        // Both at rest, feet square, the swords carried, before the guard: a closing step still under way when
        // the guard step starts crosses the feet.
        var fB = second.GetComponent<HumanoidFootwork>();
        for (float t = 0; t < 6 && (f.phase != HumanoidFootwork.Phase.Stand || fB.phase != HumanoidFootwork.Phase.Stand || t < 1.5f); t += Time.fixedDeltaTime)
        {
            var (g, r) = Carry(); handling.Hold(g, r);
            var (gB, rB) = CarryFor(hB, armsB, handB); handB.Hold(gB, rB);
            yield return new WaitForFixedUpdate();
        }
        doing = "Sparring: on guard";
        manualB.StartCoroutine(manualB.TakeGuard());
        yield return manual.TakeGuard();
        while (manualB.action != "In guard") yield return null;
        for (float t = 0; t < 1; t += Time.deltaTime) yield return null;
        doing = "Sparring: the first fencer attacks";
        yield return manual.Attack();
        doing = "Sparring: the second fencer attacks";
        yield return manualB.Attack();
        doing = "Bout over: first " + Hit(manual) + ", second " + Hit(manualB);
        routine = null;
    }
    // Neither sword touches the other fencer or the other sword: at Thibault's measure the points reach the
    // faces, and blades pressed into a body, or crossed and pushed, throw the fencers over. A touch is
    // measured by how near the point comes to the breast, as the lesson measures it.
    static void Apart(GameObject sword, GameObject other)
    {
        if (!sword || !other) return;
        foreach (var a in sword.GetComponentsInChildren<Collider>())
            foreach (var b in other.GetComponentsInChildren<Collider>())
                Physics.IgnoreCollision(a, b);
    }
    static string Hit(ThibaultManual m) => float.IsInfinity(m.closest) ? "did not finish" : "point within " + m.closest.ToString("0.00") + " m of the breast";

    (Vector3, Quaternion) CarryFor(JointBalanceHumanoid body, HumanoidArms arms, SwordHandling hand)
    {
        Quaternion heading = body.Heading;
        Vector3 along = heading * new Vector3(0, -.15f, 1).normalized, quillons = heading * Vector3.right;
        return (arms.Shoulder(hand.side) + along * (arms.Reach + .04f), Quaternion.LookRotation(Vector3.Cross(quillons, along), along));
    }

    void ToggleBumps()
    {
        if (!bumps)
        {
            bumps = GetComponent<BumpyFloor>(); if (!bumps) bumps = gameObject.AddComponent<BumpyFloor>();
            if (!bumps.material && arena.platforms.Length > 0) { var look = arena.platforms[0].GetComponentInChildren<Renderer>(); if (look) bumps.material = look.sharedMaterial; }
        }
        if (bumps.enabled && bumpsShown) { bumps.Hide(); bumpsShown = false; doing = "Level floor"; }
        else { bumps.flatAt = h.pelvis.position; bumps.Show(arena.platforms); bumpsShown = true; doing = "Rough floor"; }
    }
    bool bumpsShown;

    // Back to the start: the arena resets the body (it also listens for R); everything made here goes.
    void Clear()
    {
        if (routine != null) StopCoroutine(routine);
        routine = null;
        if (manual) { manual.StopAllCoroutines(); manual.StopHolding(); }
        if (handling.Holding) handling.Drop();
        arms.Relax(0); arms.Relax(1);
        foreach (var o in made) if (o) Destroy(o);
        made.Clear();
        if (manualB) { manualB.StopAllCoroutines(); manualB.StopHolding(); }
        if (second) Destroy(second);
        if (targetA) Destroy(targetA.gameObject);
        if (targetB) Destroy(targetB.gameObject);
        carrying = false; walking = false;
        f.gaitVelocity = Vector3.zero; f.closingSteps = savedClosing; f.heldStanceSpread = 0;
        doing = "Reset";
    }

    void OnGUI()
    {
        const float w = 330;
        GUILayout.BeginArea(new Rect(Screen.width - w - 10, 10, w, 390), GUI.skin.box);
        GUILayout.Label("<b>SKILLS</b>  (click the Game view first)", new GUIStyle(GUI.skin.label) { richText = true });
        GUILayout.Label("W  walk " + walkSpeed.ToString("0.0") + " m/s, Shift+W " + fastSpeed.ToString("0.0") + " m/s\nS / A / D  slow back / sideways (untested)\nJ  jump in place\nP  take the sword from a rack\nX  drop the sword\nL  Thibault's lesson (takes the sword first if none held)\nV  sparring: a second fencer, a bout on the circle\nK  rough floor on / off\nC  camera follow: " + (followCamera ? "on" : "off") + "\nR  reset\n(arena: G moving floor, Space / B fire balls)");
        GUILayout.Label("Now: " + doing);
        GUILayout.Label("Footwork: " + f.state + (manual && routine != null ? "   Lesson: " + manual.action : ""));
        GUILayout.EndArea();
    }
}
