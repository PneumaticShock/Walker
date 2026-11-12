using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

// Optional validation fixture for the humanoid's skills: walking (and later jumping, running, picking up
// the sword and the fencing manual). Add it to the arena in Play mode and call RunNamed("label","filter");
// poll `finished`. Results go to BalanceResults/skills-<label>.json, traces to skill-<case>.csv.
public class SkillBench : MonoBehaviour
{
    [System.Serializable] public class Case
    {
        public string name;
        public string skill;          // "walk", "jump"
        public float speed;           // m/s, forward (for a jump, its take-off speed)
        public float height;          // jump: rise of the centre of mass wanted (m)
        public bool rough;            // the still rough slab floor instead of a level one
        public bool bumpy;            // the continuous bumpy floor instead of the slabs
        public bool pickupFirst;      // thibault: take the sword from its rack first, as the skills demo does
        public float seconds = 10;    // how long the skill is asked for
        public float settle = 2;      // standing still before
        public float after = 5;       // and after, to come to rest
    }
    [System.Serializable] public class Row
    {
        public string name;
        public bool passed, fell;
        public float fellAfter = -1;
        public float distance, speed, drift, minimumPelvis = 10, peakTrunk, finalSpeed;
        public float rise, flightTime, flightDistance;
        public int steps, jumps, landings;
        public string finalState, why, notes;
    }
    [System.Serializable] public class Report { public string label; public string settings; public List<Row> rows = new List<Row>(); public int passed, total; }

    public float timeScale = 10;
    [Tooltip("Seconds a get-up case allows, if set (0 keeps each case's own).")] public float getUpSeconds = 0;
    public int repeats = 1, firstRepeat = 0;
    public bool traces = true;
    public float roughHeight = .04f, roughTilt = 4f;
    public float bumpHeight = .04f, bumpWavelength = .8f;
    public bool finished;
    public int completed, total;
    public string lastRow = "";
    public Report report;

    public static List<Case> Matrix()
    {
        var cases = new List<Case>();
        foreach (float v in new[] { .3f, .5f, .7f, .9f })
            foreach (string floor in new[] { "level", "rough", "bumpy" })
                cases.Add(new Case { name = "walk " + v.ToString("0.0", CultureInfo.InvariantCulture) + " " + floor, skill = "walk", speed = v, rough = floor == "rough", bumpy = floor == "bumpy", seconds = Mathf.Min(10, 6.5f / v) });
        foreach (var (hgt, v) in new[] { (.10f, 0f), (.15f, 0f), (.10f, .5f), (.10f, 1f) })
            foreach (string floor in new[] { "level", "rough", "bumpy" })
                cases.Add(new Case { name = "jump h" + hgt.ToString("0.00", CultureInfo.InvariantCulture) + " v" + v.ToString("0.0", CultureInfo.InvariantCulture) + " " + floor, skill = "jump", speed = v, height = hgt, rough = floor == "rough", bumpy = floor == "bumpy", seconds = 1, after = 5 });
        // The sword: taken from its rack; and Thibault's lesson, on the level floor of a fencing hall.
        cases.Add(new Case { name = "sword pickup level", skill = "pickup", seconds = 6, after = 3 });
        cases.Add(new Case { name = "thibault lesson level", skill = "thibault", seconds = 60, after = 2 });
        cases.Add(new Case { name = "thibault after pickup level", skill = "thibault", pickupFirst = true, seconds = 66, after = 2 });
        // Getting up: laid on the floor face down, face up and on each side (speed is the yaw it is laid at, so
        // repeats differ), from a squat, and after being knocked down by a push.
        foreach (string how in new[] { "front", "back", "left", "right", "squat", "push forward", "push back" })
            cases.Add(new Case { name = "getup " + how, skill = "getup", seconds = 40, after = 2 });
        return cases;
    }

    public void RunNamed(string label, string filter)
    {
        string[] any = filter.Split('|');
        var picked = Matrix().FindAll(c => System.Array.Exists(any, f => c.name.Contains(f)));
        StopAllCoroutines(); StartCoroutine(RunCases(label, picked));
    }

    IEnumerator RunCases(string label, List<Case> cases)
    {
        float savedScale = Time.timeScale; Time.timeScale = timeScale;
        float savedStep = Time.maximumDeltaTime; Time.maximumDeltaTime = 2f;
        finished = false; completed = 0; total = cases.Count * repeats;
        var a = GetComponent<BalanceTestArena>();
        report = new Report { label = label, settings = JsonUtility.ToJson(a.humanoid.GetComponent<HumanoidFootwork>()) };
        System.IO.Directory.CreateDirectory("BalanceResults");
        foreach (var c in cases) for (int r = 0; r < repeats; r++)
        {
            if (c.skill == "pickup" || c.skill == "thibault") yield return RunSwordCase(c, firstRepeat + r);
            else if (c.skill == "getup") yield return RunGetUpCase(c, firstRepeat + r);
            else yield return RunCase(c, firstRepeat + r);
            completed++;
            report.total = report.rows.Count; report.passed = report.rows.FindAll(x => x.passed).Count;
            System.IO.File.WriteAllText("BalanceResults/skills-" + label + ".json", JsonUtility.ToJson(report, true));
        }
        Time.timeScale = savedScale; Time.maximumDeltaTime = savedStep;
        finished = true;
    }

    IEnumerator RunCase(Case c, int repeat)
    {
        var a = GetComponent<BalanceTestArena>(); var h = a.humanoid; var f = h.GetComponent<HumanoidFootwork>();
        a.automaticFire = false; a.shiftingGround = false;
        float savedAt = a.roughAt, savedHeight = a.roughHeight, savedTilt = a.roughTilt;
        // Each repeat takes the floor's pose at a different moment, so the rough floor differs between them.
        a.roughAt = c.rough ? 3 + repeat * 1.37f : -1; a.roughHeight = roughHeight; a.roughTilt = roughTilt;
        f.gaitVelocity = Vector3.zero; f.recoverySteps = true;
        BumpyFloor bumps = null;
        if (c.bumpy)
        {
            bumps = GetComponent<BumpyFloor>();
            if (!bumps) bumps = gameObject.AddComponent<BumpyFloor>();
            bumps.amplitude = bumpHeight; bumps.wavelength = bumpWavelength; bumps.seed = repeat + 1;
            bumps.flatAt = h.pelvis.position;
            if (!bumps.material && a.platforms.Length > 0) { var look = a.platforms[0].GetComponentInChildren<Renderer>(); if (look) bumps.material = look.sharedMaterial; }
            bumps.Show(a.platforms);
        }
        a.ResetTrial();
        var row = new Row { name = c.name };
        var log = traces ? new StringBuilder("t,state,foot,comX,comZ,vX,vZ,cpX,cpZ,pelvisY,trunk,lX,lZ,rX,rZ,goalX,goalZ,lLoad,rLoad,margin,copX,copZ,wantX,wantZ,cmd,lG,rG,goalY,lKnee,rKnee,pelvisCmd,lH,rH,lTilt,rTilt,lKneeA,rKneeA,pelvisPitch,pelvisRoll,rawX,rawZ,spX,spZ,eTorso,eLHip,eLKnee,eLAnkle,eRHip,eRKnee,eRAnkle,lhx,lhy,lhz,rhx,rhy,rhz,lkx,lky,lkz,rkx,rky,rkz,Lx,Ly,Lz,comY,vY\n") : null;
        float dt = Time.fixedDeltaTime;
        int settle = Mathf.RoundToInt(c.settle / dt), go = Mathf.RoundToInt(c.seconds / dt), after = Mathf.RoundToInt(c.after / dt);
        Vector3 start = Vector3.zero, halfway = Vector3.zero, lastSecond = Vector3.zero;
        int startSteps = 0;
        float savedJumpHeight = f.jumpHeight, standingHeight = 0, takeoffAt = -1, apex = float.NegativeInfinity;
        Vector3 takeoff = Vector3.zero;
        int startJumps = 0, startLandings = 0;
        for (int i = 0; i < settle + go + after; i++)
        {
            if (i == settle)
            {
                start = h.centerOfMass; startSteps = f.stepsLanded; startJumps = f.jumpsMade; startLandings = f.jumpsLanded; standingHeight = h.centerOfMass.y;
                if (c.skill == "walk") f.gaitVelocity = new Vector3(0, 0, c.speed);
                if (c.skill == "jump") { f.jumpHeight = c.height; f.Jump(new Vector3(0, 0, c.speed)); }
            }
            if (c.skill == "jump" && i > settle)
            {
                // The first flight only: when it began and ended, where, and how high the body got.
                bool flying = f.phase == HumanoidFootwork.Phase.Flight;
                if (flying && takeoffAt < 0) { takeoffAt = (i - settle) * dt; takeoff = h.centerOfMass; }
                if (flying && row.flightTime == 0) apex = Mathf.Max(apex, h.centerOfMass.y);
                if (!flying && takeoffAt >= 0 && row.flightTime == 0)
                {
                    row.flightTime = (i - settle) * dt - takeoffAt;
                    row.flightDistance = Vector3.Dot(h.centerOfMass - takeoff, Vector3.forward);
                    row.rise = apex - standingHeight;
                }
            }
            if (i == settle + go / 2) halfway = h.centerOfMass;
            if (i == settle + go + after - Mathf.RoundToInt(1 / dt)) lastSecond = h.centerOfMass;
            if (i == settle + go)
            {
                f.gaitVelocity = Vector3.zero;
                Vector3 moved = h.centerOfMass - start;
                row.distance = moved.z; row.drift = moved.x;
                row.speed = (h.centerOfMass.z - halfway.z) / (c.seconds - (go / 2) * dt);
                row.steps = f.stepsLanded - startSteps;
            }
            yield return new WaitForFixedUpdate();
            float t = (i - settle) * dt;
            float trunk = Vector3.Angle(h.bodies[1].rotation * Vector3.up, Vector3.up);
            if (i >= settle)
            {
                row.minimumPelvis = Mathf.Min(row.minimumPelvis, h.pelvis.position.y);
                row.peakTrunk = Mathf.Max(row.peakTrunk, trunk);
                bool down = h.pelvis.position.y - GroundBelow(h.pelvis.position) < .55f || trunk > 60;
                if (down && !row.fell) { row.fell = true; row.fellAfter = t; }
            }
            if (log != null && i % 2 == 0)
                log.AppendLine(string.Join(",", F(t), f.state, f.steppingFoot.ToString(), F(h.centerOfMass.x), F(h.centerOfMass.z), F(h.centerOfMassVelocity.x), F(h.centerOfMassVelocity.z), F(f.capture.x), F(f.capture.z), F(h.pelvis.position.y), F(trunk),
                    F(h.feet[0].position.x), F(h.feet[0].position.z), F(h.feet[1].position.x), F(h.feet[1].position.z), F(f.landingTarget.x), F(f.landingTarget.z), F(f.Load(0)), F(f.Load(1)), F(f.supportMargin),
                    F(Pressure(f).x), F(Pressure(f).z), F(f.GaitPressureWanted.x), F(f.GaitPressureWanted.z), F(f.GaitCommand.z),
                    F(f.GroundUnder(0)), F(f.GroundUnder(1)), F(f.landingTarget.y), F(f.leftKneeDegrees), F(f.rightKneeDegrees), F(f.PelvisCommand),
                    F(h.feet[0].position.y - f.GroundUnder(0)), F(h.feet[1].position.y - f.GroundUnder(1)),
                    F(Vector3.Angle(h.feet[0].rotation * Vector3.up, Vector3.up)), F(Vector3.Angle(h.feet[1].rotation * Vector3.up, Vector3.up)),
                    F(Vector3.Angle(h.bodies[3].rotation * Vector3.up, h.bodies[4].rotation * Vector3.up)), F(Vector3.Angle(h.bodies[8].rotation * Vector3.up, h.bodies[9].rotation * Vector3.up)),
                    F(Pitch(h.pelvis.rotation)), F(Roll(h.pelvis.rotation)), F(f.gaitRawGoal.x), F(f.gaitRawGoal.z), F(f.StancePressure.x), F(f.StancePressure.z),
                    F(h.DriveError(0)), F(h.DriveError(2)), F(h.DriveError(3)), F(h.DriveError(4)), F(h.DriveError(7)), F(h.DriveError(8)), F(h.DriveError(9)),
                    V(h.DriveErrorAxes(2)), V(h.DriveErrorAxes(7)), V(h.DriveErrorAxes(3)), V(h.DriveErrorAxes(8)),
                    V(h.angularMomentum), F(h.centerOfMass.y), F(h.centerOfMassVelocity.y)));
        }
        // At rest: moved under 10 cm over the last second. Standing on uneven ground sways, so a single
        // instant's speed says little.
        row.finalSpeed = Vector3.ProjectOnPlane(h.centerOfMass - lastSecond, Vector3.up).magnitude;
        row.finalState = f.state;
        if (c.skill == "walk")
        {
            var why = new List<string>();
            if (row.fell) why.Add("fell at " + row.fellAfter.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            if (Mathf.Abs(row.speed - c.speed) > .3f * c.speed) why.Add("speed " + row.speed.ToString("0.00", CultureInfo.InvariantCulture));
            if (Mathf.Abs(row.drift) > .5f) why.Add("drifted " + row.drift.ToString("0.00", CultureInfo.InvariantCulture) + " m");
            if (row.finalSpeed > .1f) why.Add("did not stop");
            row.passed = why.Count == 0;
            row.why = string.Join("; ", why);
        }
        if (c.skill == "jump")
        {
            // A jump: the body leaves the ground, rises at least 60% of the height asked for above where it
            // stood, travels at least half of what the take-off speed would carry it in that flight, lands,
            // and comes to rest standing.
            row.jumps = f.jumpsMade - startJumps; row.landings = f.jumpsLanded - startLandings;
            row.distance = (h.centerOfMass - start).z; row.drift = (h.centerOfMass - start).x;
            var why = new List<string>();
            if (row.jumps < 1) why.Add("never left the ground");
            else if (row.landings < 1) why.Add("did not land");
            if (row.fell) why.Add("fell at " + row.fellAfter.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            if (row.jumps >= 1 && row.rise < .6f * c.height) why.Add("rose " + row.rise.ToString("0.000", CultureInfo.InvariantCulture) + " m");
            if (c.speed > 0 && row.jumps >= 1 && row.flightDistance < .5f * c.speed * row.flightTime) why.Add("flew " + row.flightDistance.ToString("0.00", CultureInfo.InvariantCulture) + " m");
            if (row.finalSpeed > .1f) why.Add("did not stop");
            row.passed = why.Count == 0;
            row.why = string.Join("; ", why);
        }
        f.jumpHeight = savedJumpHeight;
        report.rows.Add(row);
        lastRow = c.name + ": " + (row.passed ? "OK" : row.why);
        if (log != null) System.IO.File.WriteAllText("BalanceResults/skill-" + c.name.Replace(' ', '_') + (repeat > 0 ? "_r" + repeat : "") + ".csv", log.ToString());
        f.gaitVelocity = Vector3.zero;
        a.roughAt = savedAt; a.roughHeight = savedHeight; a.roughTilt = savedTilt;
        if (bumps) bumps.Hide();
    }

    // The sword cases, on the level floor: taking the sword from its rack, and Thibault's lesson against a dummy.
    IEnumerator RunSwordCase(Case c, int repeat)
    {
        var a = GetComponent<BalanceTestArena>(); var h = a.humanoid; var f = h.GetComponent<HumanoidFootwork>();
        a.automaticFire = false; a.shiftingGround = false;
        float savedAt = a.roughAt; a.roughAt = -1;
        f.gaitVelocity = Vector3.zero; f.recoverySteps = true;
        bool savedClosing = f.closingSteps;
        var arms = h.GetComponent<HumanoidArms>(); if (!arms) arms = h.gameObject.AddComponent<HumanoidArms>();
        var handling = h.GetComponent<SwordHandling>(); if (!handling) handling = h.gameObject.AddComponent<SwordHandling>();
        a.ResetTrial();
        var row = new Row { name = c.name };
        var made = new List<GameObject>();
        float dt = Time.fixedDeltaTime;
        Quaternion heading = h.Heading;
        Vector3 forward = heading * Vector3.forward;
        var log = traces ? new StringBuilder("t,state,action,pelvisY,trunk,comX,comZ,gripX,gripY,gripZ,wantX,wantY,wantZ,tipX,tipY,tipZ,lX,lZ,rX,rZ,lLoad,rLoad,handX,handY,handZ,handWantX,handWantY,handWantZ,handTurn,eShoulder,eElbow,eWrist,pelvisYaw,trunkYaw,capX,capZ,supX,supZ,pushX,pushZ,pelX,pelZ,trunkPitch,hipTarget,wTwist,wSwingY,wSwingZ,wTwistAt,wSwingYAt,wSwingZAt\n") : null;
        int shoulderMotor = System.Array.FindIndex(h.motors, m => m.name == (handling.side == 0 ? "Left" : "Right") + " Upper Arm");
        int elbowMotor = System.Array.FindIndex(h.motors, m => m.name == (handling.side == 0 ? "Left" : "Right") + " Forearm");
        int wristMotor = System.Array.FindIndex(h.motors, m => m.name == (handling.side == 0 ? "Left" : "Right") + " Hand");
        bool done = false;
        Vector3 startCom = h.centerOfMass, lastSecond = Vector3.zero;
        ThibaultManual manual = null;
        Sword sword = null;
        string fallNote = "";
        // Settle standing first.
        for (int i = 0; i < Mathf.RoundToInt(c.settle / dt); i++) yield return new WaitForFixedUpdate();
        Vector3 shoulder = arms.Shoulder(handling.side);
        // Carried as Thibault holds it: the arm out straight ahead and a little down, the sword in line with it.
        (Vector3, Quaternion) Carry()
        {
            // Quillons to the right, as the hand's x axis lies at rest: the other way the palm would turn over.
            Vector3 along = heading * new Vector3(0, -.15f, 1).normalized, quillons = heading * Vector3.right;
            return (arms.Shoulder(handling.side) + along * (arms.Reach + .04f), Quaternion.LookRotation(Vector3.Cross(quillons, along), along));
        }
        IEnumerator Act()
        {
            if (c.skill == "pickup")
            {
                Vector3 grip = shoulder + heading * new Vector3(0, -.33f, .30f);
                sword = Sword.Build(grip + Vector3.up * .005f, heading * Quaternion.Euler(180, 0, 0));
                made.Add(sword.gameObject); made.Add(SwordRack.Build(grip, heading));
                for (float t = 0; t < .6f; t += dt) yield return new WaitForFixedUpdate();
                yield return handling.PickUp(sword, Carry);
                for (float t = 0; t < 2.5f; t += dt) { var (g, r) = Carry(); handling.Hold(g, r); yield return new WaitForFixedUpdate(); }
            }
            else
            {
                if (c.pickupFirst)
                {
                    // As the skills demo does it: the sword taken from its rack, carried a moment, then the lesson.
                    Vector3 grip = shoulder + heading * new Vector3(0, -.33f, .30f);
                    sword = Sword.Build(grip + Vector3.up * .005f, heading * Quaternion.Euler(180, 0, 0));
                    var rack = SwordRack.Build(grip, heading);
                    made.Add(sword.gameObject); made.Add(rack);
                    for (float t = 0; t < .6f; t += dt) yield return new WaitForFixedUpdate();
                    yield return handling.PickUp(sword, Carry);
                    // The rack stands where the guard's first step goes: put away, as the demo does.
                    Destroy(rack);
                    for (float t = 0; t < 1.5f; t += dt) { var (g, r) = Carry(); handling.Hold(g, r); yield return new WaitForFixedUpdate(); }
                }
                else
                {
                    // Arm up and forward first, and the sword put in the hand there (a hanging hand would put its point
                    // through the floor).
                    yield return handling.MoveTo(Carry, 1.0f);
                    for (float t = 0; t < .5f; t += dt) { var (g, r) = Carry(); handling.Hold(g, r); yield return new WaitForFixedUpdate(); }
                    sword = Sword.Build(arms.Hand(handling.side).position, arms.Hand(handling.side).rotation * handling.InHand);
                    made.Add(sword.gameObject);
                    handling.PutInHand(sword);
                }
                manual = GetComponent<ThibaultManual>(); if (!manual) manual = gameObject.AddComponent<ThibaultManual>();
                manual.fencer = h; manual.sword = handling;
                manual.GuardFrom(h.feet[1].position, forward);
                manual.opponent = ThibaultManual.BuildDummy(manual.X + forward * .15f, -forward);
                made.Add(manual.opponent.gameObject);
                yield return manual.Lesson();
                for (float t = 0; t < 1.5f; t += dt) yield return new WaitForFixedUpdate();
            }
            done = true;
        }
        void Trace(float t, float trunk)
        {
            if (log == null) return;
            Vector3 grip = sword ? sword.Grip : arms.Hand(handling.side).position, want = handling.CommandedGrip, tip = sword ? sword.Tip : Vector3.zero;
            log.AppendLine(string.Join(",", F(t), f.state, manual ? manual.action : handling.state, F(h.pelvis.position.y), F(trunk), F(h.centerOfMass.x), F(h.centerOfMass.z),
                F(grip.x), F(grip.y), F(grip.z), F(want.x), F(want.y), F(want.z), F(tip.x), F(tip.y), F(tip.z),
                F(h.feet[0].position.x), F(h.feet[0].position.z), F(h.feet[1].position.x), F(h.feet[1].position.z), F(f.Load(0)), F(f.Load(1)),
                V(arms.Hand(handling.side).position), V(arms[handling.side].hand), F(Quaternion.Angle(arms.Hand(handling.side).rotation, arms[handling.side].handRotation)),
                F(h.DriveError(shoulderMotor)), F(h.DriveError(elbowMotor)), F(h.DriveError(wristMotor)),
                F(Yaw(heading, h.pelvis.rotation)), F(Yaw(heading, h.bodies[1].rotation)),
                F(f.capture.x), F(f.capture.z), F(f.supportPoint.x), F(f.supportPoint.z), F(f.PlacedPush.x), F(f.PlacedPush.z), F(h.pelvis.position.x), F(h.pelvis.position.z),
                F(Mathf.Asin(Mathf.Clamp(Vector3.Dot(h.bodies[1].rotation * Vector3.up, forward), -1, 1)) * Mathf.Rad2Deg), F(h.hipTargetDegrees),
                TwistSwing(Quaternion.Inverse(h.motors[wristMotor].targetRotation)),
                TwistSwing(Quaternion.Inverse(h.motors[wristMotor].connectedBody.rotation) * h.motors[wristMotor].transform.rotation)));
        }
        var acting = StartCoroutine(Act());
        float limit = c.seconds + c.after;
        int ticks = 0;
        for (float t = 0; t < limit && !done; t += dt)
        {
            yield return new WaitForFixedUpdate();
            ticks++;
            float trunk = Vector3.Angle(h.bodies[1].rotation * Vector3.up, Vector3.up);
            row.minimumPelvis = Mathf.Min(row.minimumPelvis, h.pelvis.position.y);
            row.peakTrunk = Mathf.Max(row.peakTrunk, trunk);
            float floor = GroundBelow(h.pelvis.position);
            bool down = h.pelvis.position.y - floor < .55f || trunk > 60;
            if (down && !row.fell)
            {
                row.fell = true; row.fellAfter = t;
                string under = "";
                foreach (var hit in Physics.RaycastAll(h.pelvis.position, Vector3.down, 3, 1, QueryTriggerInteraction.Ignore)) under += hit.collider.name + "@" + hit.point.y.ToString("0.00") + (hit.rigidbody ? (hit.rigidbody.isKinematic ? "k" : "d") : "s") + " ";
                fallNote = "fall: pelvis " + h.pelvis.position.y.ToString("0.00") + " floor " + floor.ToString("0.00") + " trunk " + trunk.ToString("0") + " under " + under;
            }
            if (ticks % 2 == 0) Trace(t, trunk);
        }
        if (!done) StopCoroutine(acting);
        // At rest: under 10 cm over the last second. The sword stays in its carry pose meanwhile, as it would in
        // a game: left where it was last put in the world, the body's own sway measures as the arm's error.
        Vector3 before = h.centerOfMass;
        for (float t = 0; t < 1; t += dt)
        {
            if (manual) manual.MeasureGuard();
            else if (done && c.skill == "pickup" && handling.Holding) { var (g, r) = Carry(); handling.Hold(g, r); }
            yield return new WaitForFixedUpdate();
            if (++ticks % 2 == 0) Trace(limit + t, Vector3.Angle(h.bodies[1].rotation * Vector3.up, Vector3.up));
        }
        row.finalSpeed = Vector3.ProjectOnPlane(h.centerOfMass - before, Vector3.up).magnitude;
        row.finalState = f.state;
        var why = new List<string>();
        if (row.fell) why.Add("fell at " + row.fellAfter.ToString("0.0", CultureInfo.InvariantCulture) + " s");
        if (!done) why.Add("did not finish");
        if (row.finalSpeed > .1f) why.Add("did not stop");
        if (c.skill == "pickup")
        {
            if (!handling.Holding) why.Add("not holding the sword: " + handling.state);
            else
            {
                var (g, r) = Carry();
                float off = Vector3.Distance(sword.Grip, g), turn = Vector3.Angle(sword.Along, r * Vector3.up);
                row.distance = off; row.rise = turn;
                if (off > .05f) why.Add("grip " + off.ToString("0.000", CultureInfo.InvariantCulture) + " m from the carry pose");
                if (turn > 10) why.Add("blade " + turn.ToString("0", CultureInfo.InvariantCulture) + " deg off");
            }
            row.notes = handling.state;
            if (handling.Holding)
            {
                var (g, _) = Carry();
                row.notes += " | grip " + sword.Grip.ToString("F3") + " wanted " + g.ToString("F3") + " hand " + arms.Hand(handling.side).position.ToString("F3") + " hand wanted " + arms[handling.side].hand.ToString("F3") + " correction " + arms[handling.side].correction.ToString("F3") + " anchor " + arms[handling.side].grip.connectedAnchor.ToString("F3");
            }
        }
        else if (manual)
        {
            // Thibault: the guard straight (arm and blade in line within 10 degrees) and on the face (within
            // 0.1 m of the line), the feet on their points (6 cm), the point to the breast (6 cm), no fall.
            if (manual.guardBend > 10) why.Add("guard bent " + manual.guardBend.ToString("0.0", CultureInfo.InvariantCulture) + " deg");
            if (manual.guardAim > .1f) why.Add("guard off the face by " + manual.guardAim.ToString("0.00", CultureInfo.InvariantCulture) + " m");
            if (manual.worstFoot > .06f) why.Add("a foot " + manual.worstFoot.ToString("0.00", CultureInfo.InvariantCulture) + " m off its point");
            if (manual.closest > .06f) why.Add("point " + manual.closest.ToString("0.00", CultureInfo.InvariantCulture) + " m from the breast");
            row.distance = manual.closest; row.rise = manual.guardBend; row.drift = manual.worstFoot; row.speed = manual.guardAim;
            row.notes = string.Join(" | ", manual.log);
            manual.StopHolding();
        }
        row.passed = why.Count == 0;
        row.why = string.Join("; ", why);
        if (fallNote != "") row.notes = fallNote + " | " + row.notes;
        report.rows.Add(row);
        lastRow = c.name + ": " + (row.passed ? "OK" : row.why);
        if (log != null) System.IO.File.WriteAllText("BalanceResults/skill-" + c.name.Replace(' ', '_') + (repeat > 0 ? "_r" + repeat : "") + ".csv", log.ToString());
        handling.Drop();
        foreach (var g in made) if (g) Destroy(g);
        f.closingSteps = savedClosing; f.heldStanceSpread = 0;
        a.roughAt = savedAt;
        yield return new WaitForFixedUpdate();
    }

    // Getting up. The body is laid on the floor by the fixture (as the arena's reset stands it up), or knocked
    // down by a push at the pelvis, and has `seconds` to stand. It passes standing (pelvis 0.85 m up, trunk
    // within 20 degrees of upright) and at rest at the end, having got up by itself.
    IEnumerator RunGetUpCase(Case c, int repeat)
    {
        var a = GetComponent<BalanceTestArena>(); var h = a.humanoid; var f = h.GetComponent<HumanoidFootwork>();
        a.automaticFire = false; a.shiftingGround = false;
        float savedAt = a.roughAt; a.roughAt = -1;
        f.gaitVelocity = Vector3.zero; f.recoverySteps = true;
        var g = h.GetComponent<HumanoidGetUp>(); if (!g) g = h.gameObject.AddComponent<HumanoidGetUp>();
        g.enabled = true;
        a.ResetTrial();
        var row = new Row { name = c.name };
        float dt = Time.fixedDeltaTime;
        string how = c.name.Substring("getup ".Length);
        float yaw = repeat * 37f;
        if (how == "front" || how == "back" || how == "left" || how == "right")
        {
            Quaternion turn = Quaternion.Euler(0, yaw, 0) * (how == "front" ? Quaternion.Euler(90, 0, 0) : how == "back" ? Quaternion.Euler(-90, 0, 0) : how == "left" ? Quaternion.Euler(0, 0, 90) : Quaternion.Euler(0, 0, -90));
            Lay(h, turn);
        }
        for (int i = 0; i < Mathf.RoundToInt(.5f / dt); i++) yield return new WaitForFixedUpdate();
        if (how == "squat") g.SquatAndRise();
        if (how.StartsWith("push"))
        {
            for (int i = 0; i < Mathf.RoundToInt(1.5f / dt); i++) yield return new WaitForFixedUpdate();
            Vector3 dir = Quaternion.Euler(0, yaw, 0) * (how == "push forward" ? Vector3.forward : Vector3.back);
            h.bodies[1].AddForce(dir * 130, ForceMode.Impulse);
        }
        var log = traces ? new StringBuilder("t,stage,pelvisY,trunk,faceDown,comX,comZ,comY,lFootY,rFootY,lUp,rUp,rise,ahead,lKnee,rKnee,lHip,rHip,waist,lKneeY,rKneeY,lHandY,rHandY,chestY,lShoulder,rShoulder,lElbow,rElbow,lHandX,lHandZ,pelX,pelZ,lHandFwd,lHandOut,lHandDown,rHandFwd,rHandOut,rHandDown,comAheadKnees\n") : null;
        float limit = getUpSeconds > 0 ? getUpSeconds : c.seconds, upAt = -1;
        int ticks = 0;
        int startUps = g.getUps;
        for (float t = 0; t < limit; t += dt)
        {
            yield return new WaitForFixedUpdate();
            float trunk = Vector3.Angle(h.bodies[1].rotation * Vector3.up, Vector3.up);
            row.minimumPelvis = Mathf.Min(row.minimumPelvis, h.pelvis.position.y);
            row.peakTrunk = Mathf.Max(row.peakTrunk, trunk);
            if (g.getUps > startUps && upAt < 0) upAt = t;
            if (log != null && ++ticks % 4 == 0)
                log.AppendLine(string.Join(",", F(t), g.state, F(h.pelvis.position.y), F(trunk), F(g.FaceDown), F(h.centerOfMass.x), F(h.centerOfMass.z), F(h.centerOfMass.y),
                    F(h.feet[0].position.y), F(h.feet[1].position.y), F(Vector3.Angle(h.feet[0].rotation * Vector3.up, Vector3.up)), F(Vector3.Angle(h.feet[1].rotation * Vector3.up, Vector3.up)),
                    F(g.rise), F(Vector3.Dot(g.comError, g.Facing * Vector3.forward)),
                    F(-Swing(h, 3)), F(-Swing(h, 8)), F(Swing(h, 2)), F(Swing(h, 7)), F(Swing(h, 0)),
                    F(h.motors[3].transform.TransformPoint(h.motors[3].anchor).y), F(h.motors[8].transform.TransformPoint(h.motors[8].anchor).y),
                    F(h.handBodies[0].position.y), F(h.handBodies[1].position.y), F(h.bodies[1].position.y),
                    F(Swing(h, 5)), F(Swing(h, 10)), F(Swing(h, 6)), F(Swing(h, 11)),
                    F(h.handBodies[0].position.x), F(h.handBodies[0].position.z), F(h.pelvis.position.x), F(h.pelvis.position.z),
                    HandFromShoulder(h, 0), HandFromShoulder(h, 1), F(g.comFromKnees)));
            if (upAt >= 0 && t > upAt + 4) break;
        }
        Vector3 before = h.centerOfMass;
        for (float t = 0; t < 1; t += dt) yield return new WaitForFixedUpdate();
        row.finalSpeed = Vector3.ProjectOnPlane(h.centerOfMass - before, Vector3.up).magnitude;
        row.finalState = g.Active ? "Getting up: " + g.state : f.state;
        float height = h.pelvis.position.y - GroundBelow(h.pelvis.position), tilt = Vector3.Angle(h.bodies[1].rotation * Vector3.up, Vector3.up);
        var why = new List<string>();
        if (g.getUps == startUps) why.Add("never got up (" + g.state + ")");
        if (height < .85f || tilt > 20) why.Add("not standing at the end: pelvis " + height.ToString("0.00", CultureInfo.InvariantCulture) + " trunk " + tilt.ToString("0", CultureInfo.InvariantCulture));
        if (row.finalSpeed > .1f) why.Add("did not stop");
        row.speed = upAt; row.jumps = g.getUps - startUps; row.landings = g.falls;
        row.passed = why.Count == 0;
        row.why = string.Join("; ", why);
        row.notes = "stood after " + upAt.ToString("0.0", CultureInfo.InvariantCulture) + " s; falls " + g.falls + " getups " + g.getUps + " stage " + g.state;
        report.rows.Add(row);
        lastRow = c.name + ": " + (row.passed ? "OK" : row.why);
        if (log != null) System.IO.File.WriteAllText("BalanceResults/skill-" + c.name.Replace(' ', '_') + (repeat > 0 ? "_r" + repeat : "") + ".csv", log.ToString());
        if (g.Active) g.Abort();
        f.enabled = true;
        a.roughAt = savedAt;
        yield return new WaitForFixedUpdate();
    }

    // Lay the body on the floor, turned by `turn` about the pelvis from its standing pose: a fixture, like the
    // arena's reset, and not something the controller does.
    static void Lay(JointBalanceHumanoid h, Quaternion turn)
    {
        Vector3 pivot = h.pelvis.position;
        foreach (var b in h.bodies)
        {
            b.position = pivot + turn * (b.position - pivot); b.rotation = turn * b.rotation;
            b.transform.SetPositionAndRotation(b.position, b.rotation);
            b.linearVelocity = Vector3.zero; b.angularVelocity = Vector3.zero;
        }
        Physics.SyncTransforms();
        float low = float.PositiveInfinity;
        foreach (var b in h.bodies) foreach (var col in b.GetComponents<Collider>()) low = Mathf.Min(low, col.bounds.min.y);
        Vector3 up = Vector3.up * (GroundBelow(pivot + Vector3.up * 2) + .02f - low);
        foreach (var b in h.bodies) { b.position += up; b.transform.position = b.position; b.WakeUp(); }
        Physics.SyncTransforms();
    }
    // Where a hand is from its shoulder joint in the trunk's frame (m): toward the head, out to its own side,
    // and out of the chest (down, lying face down), comma-joined.
    static string HandFromShoulder(JointBalanceHumanoid h, int side)
    {
        var j = h.motors[side == 0 ? 5 : 10];
        Vector3 d = h.bodies[1].transform.InverseTransformDirection(h.handBodies[side].position - j.transform.TransformPoint(j.anchor));
        return F(d.y) + "," + F(side == 0 ? -d.x : d.x) + "," + F(d.z);
    }
    // How far a joint swings its child forward in the parent's frame (degrees): flexion for the hip, shoulder,
    // elbow and waist, minus flexion for the knee.
    static float Swing(JointBalanceHumanoid h, int motor)
    {
        var j = h.motors[motor];
        Quaternion r = Quaternion.Inverse(j.connectedBody.rotation) * j.transform.rotation;
        if (j.name == "Torso" || j.name == "Head") { Vector3 u = r * Vector3.up; return Mathf.Atan2(u.z, u.y) * Mathf.Rad2Deg; }
        Vector3 d = r * Vector3.down; return Mathf.Atan2(d.z, -d.y) * Mathf.Rad2Deg;
    }

    // Where the whole body's centre of pressure really is, from both feet.
    static Vector3 Pressure(HumanoidFootwork f)
    {
        float l = f.Load(0), r = f.Load(1);
        return l + r > 1 ? (f.Pressure(0) * l + f.Pressure(1) * r) / (l + r) : Vector3.zero;
    }
    // Forward pitch and rightward roll of a body, in degrees, from where its up axis points.
    static float Pitch(Quaternion q) { Vector3 u = q * Vector3.up; return Mathf.Atan2(u.z, u.y) * Mathf.Rad2Deg; }
    static float Roll(Quaternion q) { Vector3 u = q * Vector3.up; return Mathf.Atan2(u.x, u.y) * Mathf.Rad2Deg; }
    // The floor under a point: the highest hit that is not something moving, such as a sword in the hand.
    static float GroundBelow(Vector3 p)
    {
        float best = float.NegativeInfinity;
        foreach (var hit in Physics.RaycastAll(p, Vector3.down, 3, 1, QueryTriggerInteraction.Ignore))
            if ((!hit.rigidbody || hit.rigidbody.isKinematic) && hit.point.y > best) best = hit.point.y;
        return float.IsNegativeInfinity(best) ? 0 : best;
    }
    static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    // A joint rotation as twist about its x axis and the swing left over, about y and z (degrees), comma-joined.
    static string TwistSwing(Quaternion q)
    {
        if (q.w < 0) { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; }
        float twist = 2 * Mathf.Atan2(q.x, q.w);
        Quaternion swing = q * Quaternion.Inverse(Quaternion.AngleAxis(twist * Mathf.Rad2Deg, Vector3.right));
        swing.ToAngleAxis(out float a, out Vector3 axis); if (a > 180) a -= 360;
        return F(twist * Mathf.Rad2Deg) + "," + F(axis.y * a) + "," + F(axis.z * a);
    }
    // How far a body's forward is turned about the vertical from the heading (degrees, + to the right).
    static float Yaw(Quaternion heading, Quaternion r) => Vector3.SignedAngle(heading * Vector3.forward, Vector3.ProjectOnPlane(r * Vector3.forward, Vector3.up), Vector3.up);
    static string V(Vector3 v) => F(v.x) + "," + F(v.y) + "," + F(v.z);
}
