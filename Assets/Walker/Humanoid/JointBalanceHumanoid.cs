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
    Quaternion heading;
    Vector3 filteredVelocity, bodyCorrection, armCorrection;
    HumanoidFootwork footwork;
    Quaternion[] lastTargets = new Quaternion[12];
    JointDrive[] standing;
    bool haveLastTargets;

    void Awake()
    {
        heading = transform.rotation;
        footwork=GetComponent<HumanoidFootwork>();
        if (bodies == null) return;
        if (splitFeet) SplitFeet();
        standing = System.Array.ConvertAll(motors, joint => joint.slerpDrive);
        // Standing itself fails at the Unity default of 0.02 s: the humanoid falls without being touched.
        if (Time.fixedDeltaTime > .0101f) Debug.LogWarning("JointBalanceHumanoid is only validated at a 0.01 s fixed timestep, and cannot stand at 0.02 s. Current: " + Time.fixedDeltaTime + " s. Set it in Project Settings > Time.", this);
        // Rigidbody solver overrides are runtime settings, not persisted with the scene.
        foreach (var body in bodies)
        {
            body.solverIterations = 20;
            body.solverVelocityIterations = 10;
        }
        foreach (var a in bodies) foreach (var b in bodies)
            if (a != b) Physics.IgnoreCollision(a.GetComponent<Collider>(), b.GetComponent<Collider>());
    }
    void FixedUpdate()
    {
        elapsed += Time.fixedDeltaTime;
        float mass = 0; Vector3 velocity = Vector3.zero;
        centerOfMass = Vector3.zero;
        foreach (var body in bodies) { mass += body.mass; centerOfMass += body.worldCenterOfMass * body.mass; velocity += body.linearVelocity * body.mass; }
        centerOfMass /= mass; velocity /= mass; centerOfMassVelocity = velocity;
        bool stepping = footwork && footwork.enabled && footwork.IsStepping;
        float blend = 1f - Mathf.Exp(-Time.fixedDeltaTime / .08f);
        filteredVelocity = Vector3.Lerp(filteredVelocity, velocity, blend);
        angularMomentum = Vector3.zero;
        foreach (var body in bodies)
        {
            Quaternion inertiaFrame = body.rotation * body.inertiaTensorRotation;
            Vector3 spin = inertiaFrame * Vector3.Scale(body.inertiaTensor, Quaternion.Inverse(inertiaFrame) * body.angularVelocity);
            angularMomentum += spin + Vector3.Cross(body.worldCenterOfMass - centerOfMass, body.mass * (body.linearVelocity - velocity));
        }
        Vector3 support = Vector3.zero, normal = Vector3.zero; int contacts = 0;
        for(int footIndex=0;footIndex<feet.Length;footIndex++)
        {
            var foot=feet[footIndex];
            if (Physics.Raycast(foot.position + Vector3.up * .03f, Vector3.down, out var hit, .18f, 1 << 0, QueryTriggerInteraction.Ignore))
            { support += hit.point; normal += hit.normal; contacts++; }
        }
        supported = contacts > 0;
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
        Vector3 wantedBody = wholeBodyBalance && supported && !stepping ? response * hipGain - momentumLean : Vector3.zero;
        Vector3 wantedArms = wholeBodyBalance && supported ? -response * armGain + momentumLean : Vector3.zero;
        bodyCorrection = Vector3.Lerp(bodyCorrection, Vector3.ClampMagnitude(wantedBody, Mathf.Tan(maximumHipAngle * Mathf.Deg2Rad)), blend);
        armCorrection = Vector3.Lerp(armCorrection, Vector3.ClampMagnitude(wantedArms, Mathf.Tan(maximumArmAngle * Mathf.Deg2Rad)), blend);
        hipTargetDegrees = Mathf.Atan(bodyCorrection.magnitude) * Mathf.Rad2Deg;
        armTargetDegrees = Mathf.Atan(armCorrection.magnitude) * Mathf.Rad2Deg;
        Quaternion upright = Quaternion.FromToRotation(Vector3.up, Vector3.up + lean) * heading;
        Quaternion trunk = Quaternion.FromToRotation(Vector3.up, Vector3.up + bodyCorrection) * upright;
        Quaternion hips = Quaternion.FromToRotation(Vector3.up, Vector3.up - bodyCorrection * .35f) * upright;
        Quaternion arms = Quaternion.FromToRotation(Vector3.up, Vector3.up + armCorrection) * trunk;
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
                else if(joint.name=="Torso" && footwork.HoldsPelvis) { childWorld=joint.connectedBody.rotation; parentWorld=joint.connectedBody.rotation; }
                slew=footwork.TargetSlew(leg,isFoot||isThigh||isShin);
            }
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
            // Bound target slew as well as motor torque; no transform changes or root forces.
            joint.targetRotation = plantedAnkle ? target : Quaternion.RotateTowards(joint.targetRotation, target, slew * Time.fixedDeltaTime);
        }
        haveLastTargets = true;
    }

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
        haveLastTargets = false;
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

