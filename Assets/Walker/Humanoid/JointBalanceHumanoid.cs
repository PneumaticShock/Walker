using UnityEngine;

// All stabilization is performed by bounded ConfigurableJoint angular drives.
public class JointBalanceHumanoid : MonoBehaviour
{
    public Rigidbody pelvis;
    public Rigidbody[] bodies;
    public ConfigurableJoint[] motors;
    public Rigidbody[] feet;
    // Conservative outer-loop gains: fast COM feedback can excite the capped ankle servos.
    public float positionGain = 0.2f, velocityGain = 0.1f, maximumLean = 12f;
    public Vector3 centerOfMass, centerOfMassVelocity;
    public bool supported;
    public float elapsed;
    [Header("Whole-body feedback (SIMBICON-style multi-joint state feedback)")]
    public bool wholeBodyBalance = true;
    public float hipGain = 2f, armGain = 3f, momentumGain = 0.002f;
    public float maximumHipAngle = 22f, maximumArmAngle = 50f;
    public Vector3 capturePoint, angularMomentum;
    public float balanceDemand, hipTargetDegrees, armTargetDegrees;
    public string balanceState;
    [Header("Feet")]
    [Tooltip("Split each foot in two at Awake, with a joint between a rear part, which stays the foot the controller works with, and a front part.")]
    public bool splitFeet;
    [Tooltip("Where the split is, as a fraction of the foot's length from the heel. About 0.72 is the ball of the foot (the metatarsophalangeal joints); about 0.4 is the midfoot.")]
    [Range(.3f,.9f)] public float splitAt = .72f;
    [Tooltip("How far the front part can bend up (dorsiflexion) and down (plantarflexion), in degrees. Human first MTP joint: about 76 up and 34 down.")]
    public float frontUp = 76, frontDown = 34;
    [Tooltip("The spring holding the front part in line (N m per radian). Toe muscles are weak.")]
    public float frontStiffness = 30;
    [Tooltip("Height of the hinge above the sole (m).")]
    public float frontHinge = .03f;
    [Tooltip("Mass of the front part (kg). The rear part keeps the whole foot's mass and inertia: the ankle's drive was tuned on them, and on a light link a stiff drive's real stiffness is set by its inertia.")]
    public float frontMass = .15f;
    // The front parts once split, otherwise null. They are appended to `bodies`, so existing indices stay put.
    [System.NonSerialized] public Rigidbody[] fronts;
    [Header("Hands")]
    [Tooltip("Add a hand to each forearm at Awake, on a wrist joint. The hand's mass comes out of the forearm's, so the arm weighs what it did.")]
    public bool hands = true;
    [Range(.2f,1)] public float handMass = .45f;
    [Tooltip("Torque cap of the wrist drives (N m). A human wrist manages about 10-20.")]
    [Range(2,40)] public float wristTorque = 15f;
    [Tooltip("How far the wrist lets the hand turn about the forearm's length (degrees); the forearm does most of that.")]
    [Range(5,40)] public float handTwistLimit = 10f;
    [Tooltip("The trunk takes its turn about the vertical from the pelvis, and only its lean from the world.")]
    public bool trunkYawFromPelvis;
    // The hands once added, otherwise null; appended to `bodies` and their wrists to `motors`.
    [System.NonSerialized] public Rigidbody[] handBodies;
    // Things held in a hand. They count in the centre of mass and its momentum like a part of the body.
    [System.NonSerialized] public readonly System.Collections.Generic.List<Rigidbody> carried = new System.Collections.Generic.List<Rigidbody>();
    [Tooltip("How far the thigh can swing forward (degrees), a human hip's flexion. Built at 65, which nothing upright reaches, but which leaves no way into a squat, and so no way up from the ground.")]
    [Range(65,130)] public float hipFlexion = 120f;
    Quaternion heading;
    public Quaternion Heading => heading;
    // Which way standing faces. Set after getting up, facing wherever the feet ended up.
    public void Face(Quaternion facing) { heading = Quaternion.LookRotation(Vector3.ProjectOnPlane(facing * Vector3.forward, Vector3.up).normalized, Vector3.up); }
    HumanoidGetUp getUp;
    Vector3 filteredVelocity, bodyCorrection, armCorrection;
    HumanoidFootwork footwork;
    HumanoidArms armControl;
    Quaternion[] lastTargets = new Quaternion[12];
    JointDrive[] standing;
    bool haveLastTargets;

    void Awake()
    {
        heading = transform.rotation;
        footwork=GetComponent<HumanoidFootwork>();
        armControl=GetComponent<HumanoidArms>();
        if (bodies == null) return;
        if (splitFeet) SplitFeet();
        if (hands) AddHands();
        lastTargets = new Quaternion[motors.Length];
        standing = System.Array.ConvertAll(motors, joint => joint.slerpDrive);
        foreach (var joint in motors)
        {
            // Hip flexion is the joint's positive x angle (knee flexion is its negative one, -120 to 0).
            if (joint.name.Contains("Thigh")) { var limit = joint.highAngularXLimit; limit.limit = hipFlexion; joint.highAngularXLimit = limit; }
            // A shoulder raises the arm forward well past the horizontal, and out to the side (a high guard or an
            // imbrocade carries the hand above the head; pushing up off the floor puts it beside the head), and an
            // elbow folds to about 145 degrees while the forearm turns about its length. Built with every arm
            // joint at 90 to 100 degrees.
            if (joint.name.Contains("Upper Arm"))
            {
                joint.lowAngularXLimit = new SoftJointLimit { limit = -170 }; joint.highAngularXLimit = new SoftJointLimit { limit = 170 };
                joint.angularYLimit = new SoftJointLimit { limit = 120 }; joint.angularZLimit = new SoftJointLimit { limit = 150 };
            }
            if (joint.name.Contains("Forearm"))
            {
                joint.lowAngularXLimit = new SoftJointLimit { limit = -150 }; joint.highAngularXLimit = new SoftJointLimit { limit = 150 };
                joint.angularYLimit = new SoftJointLimit { limit = 150 }; joint.angularZLimit = new SoftJointLimit { limit = 150 };
            }
        }
        // Standing itself fails at the Unity default of 0.02 s: the humanoid falls without being touched.
        if (Time.fixedDeltaTime > .0101f) Debug.LogWarning("JointBalanceHumanoid is only validated at a 0.01 s fixed timestep, and cannot stand at 0.02 s. Current: " + Time.fixedDeltaTime + " s. Set it in Project Settings > Time.", this);
        // Rigidbody solver overrides are runtime settings, not persisted with the scene.
        foreach (var body in bodies)
        {
            body.solverIterations = 20;
            body.solverVelocityIterations = 10;
        }
        foreach (var a in bodies) foreach (var b in bodies)
            if (a != b) Physics.IgnoreCollision(a.GetComponent<Collider>(), b.GetComponent<Collider>(), !armsCollide || !ArmMeetsBody(a, b));
    }

    [Tooltip("The arms collide with the trunk, the head and each other, so a hand or forearm cannot pass through the chest. Legs and everything else still pass through each other.")]
    public bool armsCollide = true;
    // An arm part (upper arm, forearm, hand) and the trunk, pelvis, head or the other arm. Parts joined to each
    // other (upper arm and trunk, forearm and upper arm, hand and forearm) never collide: their joints say so.
    static bool ArmMeetsBody(Rigidbody a, Rigidbody b)
    {
        bool Arm(Rigidbody r) => r.name.Contains("Upper Arm") || r.name.Contains("Forearm") || r.name.Contains("Hand");
        bool Trunk(Rigidbody r) => r.name == "Pelvis" || r.name == "Torso" || r.name == "Head";
        bool Side(Rigidbody r, string s) => r.name.StartsWith(s);
        if (Arm(a) && Trunk(b) || Arm(b) && Trunk(a)) return true;
        return Arm(a) && Arm(b) && Side(a, "Left") != Side(b, "Left");
    }
    void FixedUpdate()
    {
        elapsed += Time.fixedDeltaTime;
        if (!armControl) armControl = GetComponent<HumanoidArms>();
        float mass = 0; Vector3 velocity = Vector3.zero;
        centerOfMass = Vector3.zero;
        foreach (var body in bodies) { mass += body.mass; centerOfMass += body.worldCenterOfMass * body.mass; velocity += body.linearVelocity * body.mass; }
        foreach (var body in carried) if (body) { mass += body.mass; centerOfMass += body.worldCenterOfMass * body.mass; velocity += body.linearVelocity * body.mass; }
        centerOfMass /= mass; velocity /= mass; centerOfMassVelocity = velocity;
        bool stepping = footwork && footwork.enabled && footwork.IsStepping;
        float blend = 1f - Mathf.Exp(-Time.fixedDeltaTime / .08f);
        filteredVelocity = Vector3.Lerp(filteredVelocity, velocity, blend);
        angularMomentum = Vector3.zero;
        foreach (var body in bodies) AddMomentum(body, velocity);
        foreach (var body in carried) if (body) AddMomentum(body, velocity);
        Vector3 support = Vector3.zero, normal = Vector3.zero; int contacts = 0;
        for(int footIndex=0;footIndex<feet.Length;footIndex++)
        {
            var foot=feet[footIndex];
            if (Physics.Raycast(foot.position + Vector3.up * .03f, Vector3.down, out var hit, .18f, 1 << 0, QueryTriggerInteraction.Ignore))
            { support += hit.point; normal += hit.normal; contacts++; }
        }
        supported = contacts > 0;
        // Lying after a fall, and getting up: that controller has every joint.
        if (!getUp) getUp = GetComponent<HumanoidGetUp>();
        if (getUp && getUp.isActiveAndEnabled && getUp.Steer(this)) { DriveGetUp(); return; }
        Vector3 lean = Vector3.zero;
        if (supported)
        {
            support /= contacts;
            float recoveryGain = wholeBodyBalance ? 3f : 1f;
            lean = Vector3.ProjectOnPlane((support - centerOfMass) * positionGain - filteredVelocity * velocityGain, Vector3.up) * recoveryGain;
            lean = Vector3.ClampMagnitude(lean, Mathf.Tan(maximumLean * Mathf.Deg2Rad));
        }
        // While stepping the foot placement restores balance. Posture feedback would pitch the
        // trunk, and its reaction torque would throw the pelvis further into the fall.
        if(stepping) lean = Vector3.zero;
        // xi = c + v / sqrt(g/h), the capture-point state of the linear pendulum.
        float height = Mathf.Max(.3f, centerOfMass.y - support.y);
        capturePoint = centerOfMass + Vector3.ProjectOnPlane(filteredVelocity, Vector3.up) / Mathf.Sqrt(Physics.gravity.magnitude / height);
        Vector3 error = supported ? Vector3.ProjectOnPlane(support - capturePoint, Vector3.up) : Vector3.zero;
        // Dead zone avoids continually moving the trunk in a quiet, already supported stance.
        float excess = Mathf.Max(0, error.magnitude - .035f);
        Vector3 response = error.sqrMagnitude > .000001f ? error.normalized * excess : Vector3.zero;
        balanceDemand = Mathf.Clamp01(excess / .16f);
        balanceState = !supported ? "Airborne" : balanceDemand > .1f ? "Whole-body recovery" : "Quiet stance";
        Vector3 momentumLean = Vector3.Cross(Vector3.up, angularMomentum) * momentumGain / Mathf.Max(1f, mass / 75f);
        // COM-restoring posture feedback plus a smaller centroidal momentum damping term.
        // Not with an arm out holding something: the error is measured from the middle of the feet, and in a long
        // stance such as a fencing guard the body rightly stands well ahead of that. Leaning the trunk back for
        // it throws the hips forward, which only moves xi further ahead (19 degrees, and a step, in Thibault's
        // lesson). The ankles and the stance hold it; the fencer stands upright, as the guard has him.
        bool armsHolding = armControl && armControl.isActiveAndEnabled && armControl.AnyActive;
        Vector3 wantedBody = wholeBodyBalance && supported && !stepping && !armsHolding ? response * hipGain - momentumLean : Vector3.zero;
        Vector3 wantedArms = wholeBodyBalance && supported ? -response * armGain + momentumLean : Vector3.zero;
        bodyCorrection = Vector3.Lerp(bodyCorrection, Vector3.ClampMagnitude(wantedBody, Mathf.Tan(maximumHipAngle * Mathf.Deg2Rad)), blend);
        armCorrection = Vector3.Lerp(armCorrection, Vector3.ClampMagnitude(wantedArms, Mathf.Tan(maximumArmAngle * Mathf.Deg2Rad)), blend);
        hipTargetDegrees = Mathf.Atan(bodyCorrection.magnitude) * Mathf.Rad2Deg;
        armTargetDegrees = Mathf.Atan(armCorrection.magnitude) * Mathf.Rad2Deg;
        Quaternion upright = Quaternion.FromToRotation(Vector3.up, Vector3.up + lean) * heading;
        Quaternion trunk = Quaternion.FromToRotation(Vector3.up, Vector3.up + bodyCorrection) * upright;
        Quaternion hips = Quaternion.FromToRotation(Vector3.up, Vector3.up - bodyCorrection * .35f) * upright;
        Quaternion arms = Quaternion.FromToRotation(Vector3.up, Vector3.up + armCorrection) * trunk;
        // The trunk is held upright in the world from the pelvis as it really is. In yaw that is a spring the
        // wrong way on the pelvis: it turns, the waist turns the trunk back, and the reaction turns the pelvis
        // further. A light trunk never showed it; with a sword held out the trunk's yaw inertia is several times
        // more, and pelvis and trunk swing about +-10 degrees at 2 Hz. So the trunk takes its turn from the
        // pelvis, and only its lean from the world. Always while an arm is reaching or holding; otherwise as set.
        if (trunkYawFromPelvis || armControl && armControl.isActiveAndEnabled && armControl.AnyActive)
        {
            Vector3 pelvisAhead = Vector3.ProjectOnPlane(pelvis.rotation * Vector3.forward, Vector3.up);
            if (pelvisAhead.sqrMagnitude > .01f) trunk = Quaternion.FromToRotation(Vector3.ProjectOnPlane(heading * Vector3.forward, Vector3.up), pelvisAhead) * trunk;
        }
        // Small, symmetric knee flexion lowers the posture during recovery. Return is filtered.
        float crouch = wholeBodyBalance ? Mathf.Min(8f, hipTargetDegrees * .35f) : 0;
        Quaternion thigh = upright * Quaternion.Euler(-crouch, 0, 0);
        Quaternion shin = upright * Quaternion.Euler(crouch, 0, 0);
        if(footwork && footwork.enabled) footwork.Plan(this,heading,upright);
        for(int motorIndex=0;motorIndex<motors.Length;motorIndex++)
        {
            var joint=motors[motorIndex];
            // All segments are authored with identity rest rotations and XYZ joint axes.
            bool isFoot = joint.name.Contains("Foot");
            Quaternion childWorld, parentWorld;
            if (isFoot) { childWorld = supported ? Quaternion.FromToRotation(Vector3.up, normal.normalized) * heading : upright; parentWorld = shin; }
            else if (joint.name.Contains("Thigh")) { childWorld = thigh; parentWorld = hips; }
            else if (joint.name.Contains("Shin")) { childWorld = shin; parentWorld = thigh; }
            else if (joint.name == "Torso") { childWorld = trunk; parentWorld = wholeBodyBalance ? joint.connectedBody.rotation : hips; }
            else if (joint.name == "Head") { childWorld = upright; parentWorld = trunk; }
            else if (joint.name.Contains("Upper Arm")) { childWorld = arms; parentWorld = trunk; }
            else { childWorld = arms; parentWorld = arms; }
            float slew=90f;
            bool swingLeg=false, plantedAnkle=false;
            if(footwork && footwork.enabled)
            {
                int leg=joint.name.StartsWith("Left")?0:1;
                bool isThigh=joint.name.Contains("Thigh"), isShin=joint.name.Contains("Shin");
                swingLeg=footwork.LegIsFree(leg) && (isFoot||isThigh||isShin);
                bool stanceLeg=footwork.HoldsPelvis && !footwork.LegIsFree(leg);
                if(isFoot)
                {
                    parentWorld=joint.connectedBody.rotation;
                    // Level the swing sole against the actual shin, or the toe drops
                    // whenever the shin lags its target and stubs on the ground.
                    if(swingLeg) childWorld=footwork.soles[leg];
                    // A planted foot stays level in world space; see HumanoidFootwork.
                    else if(footwork.AnklesAreSoft) childWorld=footwork.AnkleTarget(leg);
                    else { childWorld=footwork.soles[leg]; parentWorld=footwork.shins[leg]; }
                    // The standing ankle would apply full torque for a fraction of a degree of
                    // tilt and tip the foot, so it softens while stepping.
                    // While stepping, a planted foot's ankle is a torque source: the drive's cap sets it.
                    plantedAnkle=footwork.AnklesAreSoft && !swingLeg;
                    if(plantedAnkle) SetDrive(motorIndex,standing[motorIndex].positionSpring,footwork.stanceAnkleDamping,standing[motorIndex].maximumForce);
                    else RestoreDrive(motorIndex);
                }
                else if(isThigh)
                {
                    // Swing hip tracks the world-space trajectory from the actual pelvis.
                    // Stance hip does the opposite: it holds the pelvis level in world space,
                    // reacting against the actual thigh (SIMBICON's stance-hip role).
                    childWorld=stanceLeg?footwork.StanceThigh(this,leg,heading*Vector3.forward):footwork.thighs[leg];
                    // A lightly loaded leg follows the pelvis as it really is, or a pitch of the pelvis swings its foot.
                    parentWorld=swingLeg?joint.connectedBody.rotation:stanceLeg?hips:Quaternion.Slerp(joint.connectedBody.rotation,hips,footwork.LegPush(leg));
                }
                else if(isShin) { childWorld=footwork.shins[leg]; parentWorld=footwork.thighs[leg]; }
                // In single support the trunk rides on the pelvis, and the stance hip holds them both.
                // Righting the trunk from the pelvis would push the pelvis the other way with as much
                // torque as the hip has, and the pelvis would roll over towards the swing foot.
                // In the air too: with nothing to push against, righting the trunk only turns the pelvis the other way.
                else if(joint.name=="Torso" && (footwork.HoldsPelvis || footwork.phase==HumanoidFootwork.Phase.Flight)) { childWorld=joint.connectedBody.rotation; parentWorld=joint.connectedBody.rotation; }
                slew=footwork.TargetSlew(leg,isFoot||isThigh||isShin);
            }
            // An arm that is reaching or holding something takes the arm controller's pose, held against its
            // parent as the parent should be (see HumanoidArms.Target). The trunk's target is the pelvis's own
            // while the stance hip holds them both.
            Quaternion trunkWanted=footwork && footwork.enabled && (footwork.HoldsPelvis || footwork.phase==HumanoidFootwork.Phase.Flight)?hips:trunk;
            if(armControl && armControl.isActiveAndEnabled && armControl.Target(joint.name, trunkWanted, out Quaternion armWorld, out Quaternion armParent, out float armSlew))
            { childWorld=armWorld; parentWorld=armParent; slew=armSlew; }
            Quaternion target = Quaternion.Inverse(Quaternion.Inverse(parentWorld) * childWorld);
            if(footwork && footwork.enabled) target=footwork.BlendLandingTarget(motorIndex,target);
            // A Slerp drive damps towards zero joint velocity, so a moving joint trails its target
            // by c/k seconds. On a swing leg that is about 10 cm at the foot, which is the
            // difference between landing on target and past it. Leading the target by the same
            // time cancels the lag: k(q + c/k q' - q) balances the damping term c q'.
            Quaternion unled=target;
            if(swingLeg && footwork.steppingFoot>=0 && footwork.driveLead>0 && haveLastTargets)
            {
                (target*Quaternion.Inverse(lastTargets[motorIndex])).ToAngleAxis(out float change,out Vector3 axis);
                if(change>180) change-=360;
                target=Quaternion.AngleAxis(Mathf.Clamp(change*footwork.driveLead/Time.fixedDeltaTime,-30,30),axis)*target;
            }
            lastTargets[motorIndex]=unled;
            // A target that is not a rotation (NaN, or all zeros) PhysX replaces with identity, which throws the
            // joint to its rest pose. Keep the last good one, and say which joint once.
            if(!Valid(target))
            {
                if(!badTargetReported) { badTargetReported=true; Debug.LogWarning($"{name}: invalid drive target for {joint.name} ({target}); keeping the previous one."); }
                target=joint.targetRotation; lastTargets[motorIndex]=target;
            }
            // Bound target slew as well as motor torque; no transform changes or root forces.
            joint.targetRotation = plantedAnkle ? target : Quaternion.RotateTowards(joint.targetRotation, target, slew * Time.fixedDeltaTime);
        }
        haveLastTargets = true;
    }

    void DriveGetUp()
    {
        for (int i = 0; i < motors.Length; i++)
        {
            if (!getUp.Target(i, out Quaternion local, out float cap, out float speed)) { RestoreDrive(i); continue; }
            float rate = speed * Time.fixedDeltaTime;
            SetDrive(i, standing[i].positionSpring, standing[i].positionDamper, cap > 0 ? cap : standing[i].maximumForce);
            Quaternion target = Quaternion.Inverse(local);
            if (!Valid(target)) continue;
            motors[i].targetRotation = Quaternion.RotateTowards(motors[i].targetRotation, target, rate);
            lastTargets[i] = motors[i].targetRotation;
        }
    }

    // Standing again after getting up: the feedback starts from the body as it is, not from before the fall.
    public void ResumeFromGetUp()
    {
        filteredVelocity = centerOfMassVelocity;
        bodyCorrection = armCorrection = Vector3.zero;
        haveLastTargets = false;
    }

    bool badTargetReported;
    static bool Valid(Quaternion q) { float n=q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w; return !float.IsNaN(n) && !float.IsInfinity(n) && n>.5f; }

    void AddMomentum(Rigidbody body, Vector3 velocity)
    {
        Quaternion inertiaFrame = body.rotation * body.inertiaTensorRotation;
        Vector3 spin = inertiaFrame * Vector3.Scale(body.inertiaTensor, Quaternion.Inverse(inertiaFrame) * body.angularVelocity);
        angularMomentum += spin + Vector3.Cross(body.worldCenterOfMass - centerOfMass, body.mass * (body.linearVelocity - velocity));
    }

    // A hand on each forearm, hinged at the wrist, made like the rest of the body. The forearm gives up the
    // hand's mass, and keeps its inertia, so the arm weighs and swings as it did.
    void AddHands()
    {
        handBodies = new Rigidbody[2];
        var all = new System.Collections.Generic.List<Rigidbody>(bodies);
        var joints = new System.Collections.Generic.List<ConfigurableJoint>(motors);
        for (int i = 0; i < 2; i++)
        {
            Rigidbody forearm = bodies[i == 0 ? 7 : 12];
            var box = forearm.GetComponent<BoxCollider>();
            float bottom = box.center.y - box.size.y * .5f;
            Vector3 wrist = forearm.transform.TransformPoint(new Vector3(box.center.x, bottom, box.center.z));
            Vector3 inertia = forearm.inertiaTensor; Quaternion inertiaRotation = forearm.inertiaTensorRotation;
            forearm.mass = Mathf.Max(.3f, forearm.mass - handMass);
            forearm.inertiaTensor = inertia; forearm.inertiaTensorRotation = inertiaRotation;

            var go = new GameObject((i == 0 ? "Left" : "Right") + " Hand") { layer = forearm.gameObject.layer };
            go.transform.SetParent(forearm.transform.parent, false);
            go.transform.SetPositionAndRotation(wrist + forearm.rotation * Vector3.down * .05f, forearm.rotation);
            var collider = go.AddComponent<BoxCollider>(); collider.size = new Vector3(.08f, .1f, .04f);
            collider.sharedMaterial = box.sharedMaterial;
            var look = GameObject.CreatePrimitive(PrimitiveType.Cube);
            look.name = "Visual"; Destroy(look.GetComponent<Collider>());
            look.transform.SetParent(go.transform, false); look.transform.localScale = collider.size;
            var forearmLook = forearm.transform.Find("Visual");
            if (forearmLook) look.GetComponent<Renderer>().sharedMaterial = forearmLook.GetComponent<Renderer>().sharedMaterial;
            var body = go.AddComponent<Rigidbody>();
            body.mass = handMass; body.linearDamping = 0; body.angularDamping = 0;
            body.interpolation = forearm.interpolation; body.collisionDetectionMode = forearm.collisionDetectionMode;

            var j = go.AddComponent<ConfigurableJoint>();
            j.connectedBody = forearm; j.autoConfigureConnectedAnchor = false;
            j.anchor = go.transform.InverseTransformPoint(wrist); j.connectedAnchor = forearm.transform.InverseTransformPoint(wrist);
            j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;
            j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Limited;
            // Flexion and extension about x, deviation about z; turning the hand is the forearm's job.
            j.lowAngularXLimit = new SoftJointLimit { limit = -75 }; j.highAngularXLimit = new SoftJointLimit { limit = 75 };
            j.angularYLimit = new SoftJointLimit { limit = handTwistLimit }; j.angularZLimit = new SoftJointLimit { limit = 35 };
            j.rotationDriveMode = RotationDriveMode.Slerp;
            j.slerpDrive = new JointDrive { positionSpring = 4000, positionDamper = 60, maximumForce = wristTorque };
            j.projectionMode = JointProjectionMode.None;
            handBodies[i] = body; all.Add(body); joints.Add(j);
        }
        bodies = all.ToArray(); motors = joints.ToArray();
    }

    // How far a joint is from its drive's target, in degrees. Past cap/spring (about a degree for the
    // legs) the drive is at its torque cap. For diagnosis: the joints report no drive torque of their own.
    public float DriveError(int motor)
    {
        var joint=motors[motor];
        Quaternion relative=Quaternion.Inverse(joint.connectedBody.rotation)*joint.transform.rotation;
        return Quaternion.Angle(relative,Quaternion.Inverse(joint.targetRotation));
    }
    // The same error about each of the joint's own axes (x is flexion for the legs), in degrees.
    public Vector3 DriveErrorAxes(int motor)
    {
        var joint=motors[motor];
        Quaternion relative=Quaternion.Inverse(joint.connectedBody.rotation)*joint.transform.rotation;
        (Quaternion.Inverse(Quaternion.Inverse(joint.targetRotation))*relative).ToAngleAxis(out float angle,out Vector3 axis);
        if(angle>180) angle-=360;
        return axis*angle;
    }
    public float SaturationAngle(int motor) { var d=motors[motor].slerpDrive; return d.positionSpring>0?d.maximumForce/d.positionSpring*Mathf.Rad2Deg:999; }

    void SetDrive(int motor, float spring, float damper, float cap)
    {
        var drive=motors[motor].slerpDrive;
        if(drive.positionSpring==spring && drive.positionDamper==damper && drive.maximumForce==cap) return;
        drive.positionSpring=spring; drive.positionDamper=damper; drive.maximumForce=cap;
        motors[motor].slerpDrive=drive;
    }
    void RestoreDrive(int motor) => SetDrive(motor,standing[motor].positionSpring,standing[motor].positionDamper,standing[motor].maximumForce);

    // Cut each foot box at `splitAt` and hinge the front part to the rear one across the foot. The rear part
    // keeps the foot's body and transform, so everything that works from the foot's position is unchanged.
    void SplitFeet()
    {
        fronts = new Rigidbody[2];
        var all = new System.Collections.Generic.List<Rigidbody>(bodies);
        for (int i = 0; i < 2; i++)
        {
            var foot = feet[i];
            var box = foot.GetComponent<BoxCollider>();
            Vector3 size = box.size, centre = box.center;
            float heel = centre.z - size.z * .5f, cut = heel + size.z * splitAt;
            float rearLength = cut - heel, frontLength = size.z - rearLength;
            float mass = foot.mass;
            Vector3 inertia = foot.inertiaTensor, centreOfMass = foot.centerOfMass;
            Quaternion inertiaRotation = foot.inertiaTensorRotation;
            box.size = new Vector3(size.x, size.y, rearLength);
            box.center = new Vector3(centre.x, centre.y, heel + rearLength * .5f);
            var visual = foot.transform.Find("Visual");
            if (visual) { visual.localScale = box.size; visual.localPosition = box.center; }
            foot.mass = mass; foot.centerOfMass = centreOfMass;
            foot.inertiaTensor = inertia; foot.inertiaTensorRotation = inertiaRotation;

            var go = new GameObject(foot.name + " Front") { layer = foot.gameObject.layer };
            go.transform.SetParent(foot.transform.parent, false);
            go.transform.SetPositionAndRotation(foot.transform.TransformPoint(new Vector3(centre.x, centre.y, cut + frontLength * .5f)), foot.rotation);
            var frontBox = go.AddComponent<BoxCollider>();
            frontBox.size = new Vector3(size.x, size.y, frontLength);
            frontBox.sharedMaterial = box.sharedMaterial;
            var look = GameObject.CreatePrimitive(PrimitiveType.Cube);
            look.name = "Visual"; Destroy(look.GetComponent<Collider>());
            look.transform.SetParent(go.transform, false); look.transform.localScale = frontBox.size;
            if (visual) look.GetComponent<Renderer>().sharedMaterial = visual.GetComponent<Renderer>().sharedMaterial;
            var body = go.AddComponent<Rigidbody>();
            body.mass = frontMass;
            body.interpolation = foot.interpolation; body.collisionDetectionMode = foot.collisionDetectionMode;
            body.linearDamping = 0; body.angularDamping = 0;

            // A hinge across the foot, a little above the sole like the metatarsal heads.
            Vector3 hinge = foot.transform.TransformPoint(new Vector3(centre.x, centre.y - size.y * .5f + frontHinge, cut));
            FrontHinge(go, foot, hinge, frontUp, frontDown, frontStiffness);
            fronts[i] = body; all.Add(body);
        }
        bodies = all.ToArray();
    }

    // The hinge between a foot's rear and front parts, about the rear part's own x axis (across the foot).
    // A negative angle lifts the front end (HingeTest checks the limits act on the right sides).
    public static HingeJoint FrontHinge(GameObject front, Rigidbody rear, Vector3 hingeWorld, float up, float down, float stiffness)
    {
        var joint = front.AddComponent<HingeJoint>();
        joint.connectedBody = rear;
        joint.autoConfigureConnectedAnchor = false;
        joint.anchor = front.transform.InverseTransformPoint(hingeWorld);
        joint.connectedAnchor = rear.transform.InverseTransformPoint(hingeWorld);
        joint.axis = Vector3.right;
        joint.useLimits = true;
        joint.limits = new JointLimits { min = -up, max = down };
        joint.useSpring = true;
        joint.spring = new JointSpring { spring = stiffness, damper = stiffness * .05f, targetPosition = 0 };
        return joint;
    }

    // Test reset clears controller memory; it does not move or apply forces to any body.
    public void ResetFeedback()
    {
        filteredVelocity = bodyCorrection = armCorrection = Vector3.zero;
        elapsed = 0;
        heading = transform.rotation;   // back to the start pose, facing as it started
        haveLastTargets = false;
        if (!getUp) getUp = GetComponent<HumanoidGetUp>();
        if (getUp) getUp.Abort();
        if(footwork) footwork.ResetPlanner();
        foreach (var joint in motors) joint.targetRotation = Quaternion.identity;
    }

    public static JointBalanceHumanoid Build(Vector3 origin)
    {
        var root = new GameObject("Joint-balanced Humanoid"); root.transform.position = origin;
        var control = root.AddComponent<JointBalanceHumanoid>();
        var parts = new System.Collections.Generic.List<Rigidbody>();
        var joints = new System.Collections.Generic.List<ConfigurableJoint>();
        var material = new PhysicsMaterial("Humanoid contact") { staticFriction = .8f, dynamicFriction = .7f, bounciness = 0 };
        Rigidbody Part(string name, Vector3 p, Vector3 size, float mass)
        {
            var go = new GameObject(name); go.layer = 2;
            go.transform.SetParent(root.transform); go.transform.position = origin + p;
            go.AddComponent<BoxCollider>().size = size;
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "Visual"; visual.transform.SetParent(go.transform, false); visual.transform.localScale = size;
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            go.GetComponent<Collider>().sharedMaterial = material;
            var rb = go.AddComponent<Rigidbody>(); rb.mass = mass; rb.linearDamping = 0; rb.angularDamping = 0;
            rb.solverIterations = 20; rb.solverVelocityIterations = 10; rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic; parts.Add(rb); return rb;
        }
        void Joint(Rigidbody child, Rigidbody parent, Vector3 anchor, float torque, float limit)
        {
            var j = child.gameObject.AddComponent<ConfigurableJoint>(); j.connectedBody = parent;
            j.autoConfigureConnectedAnchor = false; j.anchor = child.transform.InverseTransformPoint(origin + anchor);
            j.connectedAnchor = parent.transform.InverseTransformPoint(origin + anchor);
            j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;
            j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Limited;
            j.lowAngularXLimit = new SoftJointLimit { limit = -limit }; j.highAngularXLimit = new SoftJointLimit { limit = limit };
            j.angularYLimit = j.angularZLimit = new SoftJointLimit { limit = limit };
            if(child.name.Contains("Shin"))
            {
                j.lowAngularXLimit = new SoftJointLimit { limit = -120 };
                j.highAngularXLimit = new SoftJointLimit { limit = 0 };
                j.angularYLimit = j.angularZLimit = new SoftJointLimit { limit = 3 };
            }
            if(child.name.Contains("Foot"))
            {
                j.lowAngularXLimit = new SoftJointLimit { limit = -45 };
                j.highAngularXLimit = new SoftJointLimit { limit = 45 };
            }
            j.rotationDriveMode = RotationDriveMode.Slerp;
            j.slerpDrive = new JointDrive { positionSpring = 15000, positionDamper = 600, maximumForce = torque };
            j.projectionMode = JointProjectionMode.None; joints.Add(j);
        }
        var pelvis = Part("Pelvis", new Vector3(0,1.02f,0), new Vector3(.30f,.20f,.22f), 12);
        var chest = Part("Torso", new Vector3(0,1.34f,0), new Vector3(.38f,.42f,.23f), 25);
        Joint(chest,pelvis,new Vector3(0,1.13f,0),180,35);
        var head = Part("Head",new Vector3(0,1.68f,0),new Vector3(.20f,.24f,.20f),5);
        Joint(head,chest,new Vector3(0,1.55f,0),30,45);
        var feet = new Rigidbody[2];
        for(int i=0;i<2;i++)
        {
            float s = i == 0 ? -1 : 1, x = s * .14f; string side = i == 0 ? "Left " : "Right ";
            var thigh = Part(side+"Thigh",new Vector3(x,.75f,0),new Vector3(.13f,.38f,.15f),8);
            var shin = Part(side+"Shin",new Vector3(x,.35f,0),new Vector3(.11f,.38f,.13f),3.5f);
            var foot = Part(side+"Foot",new Vector3(x,.075f,.055f),new Vector3(.15f,.12f,.28f),1);
            Joint(thigh,pelvis,new Vector3(x,.95f,0),300,65);
            Joint(shin,thigh,new Vector3(x,.55f,0),280,75);
            Joint(foot,shin,new Vector3(x,.15f,0),100,30); feet[i]=foot;
            var arm = Part(side+"Upper Arm",new Vector3(s*.29f,1.32f,0),new Vector3(.11f,.29f,.12f),2);
            var forearm = Part(side+"Forearm",new Vector3(s*.29f,1.02f,0),new Vector3(.09f,.27f,.10f),1.5f);
            Joint(arm,chest,new Vector3(s*.25f,1.47f,0),60,90);
            Joint(forearm,arm,new Vector3(s*.29f,1.17f,0),40,100);
        }
        // Suppress adjacent/body self collisions; terrain and external objects still collide.
        foreach(var a in parts) foreach(var b in parts) if(a != b) Physics.IgnoreCollision(a.GetComponent<Collider>(),b.GetComponent<Collider>());
        control.pelvis=pelvis; control.bodies=parts.ToArray(); control.motors=joints.ToArray(); control.feet=feet;
        control.footwork=root.AddComponent<HumanoidFootwork>();
        return control;
    }
}

