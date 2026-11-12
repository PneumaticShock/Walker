using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class BalanceTestArena : MonoBehaviour
{
    public JointBalanceHumanoid humanoid;
    public Rigidbody[] platforms;
    public Transform[] launchers;
    [Header("Projectiles")]
    [Min(.1f)] public float projectileMass = 1.5f;
    [Range(2,20)] public float projectileSpeed = 8f;
    [Range(.08f,.4f)] public float projectileRadius = .15f;
    public bool automaticFire;
    [Min(.5f)] public float fireInterval = 3f;
    [Range(1,12)] public int burstSize = 6;
    [Range(.02f,1)] public float burstInterval = .1f;
    [Tooltip("Once a ball has hit something it passes through the feet and shins, so that spent balls do not trip the humanoid.")]
    public bool spentBallsClearLegs = true;
    [Header("Moving ground")]
    public bool shiftingGround = true;
    [Range(0,.15f)] public float heightAmplitude = .025f;
    [Range(0,10)] public float tiltDegrees = 2f;
    [Range(.02f,.5f)] public float frequency = .12f;
    [Header("Still rough floor")]
    [Tooltip("On a trial reset, hold the slabs still at the heights and tilts the moving floor has at this time (s), with roughHeight and roughTilt in place of its amplitude and tilt. Negative leaves the floor level. The two slabs under the starting position stay level, so the humanoid does not start inside one.")]
    public float roughAt = -1;
    [Range(0,.15f)] public float roughHeight = .04f;
    [Range(0,10)] public float roughTilt = 4f;
    public int shots, impacts, ballsUnderfoot;
    public float lastImpactImpulse, totalImpactImpulse;
    public Material projectileMaterial;
    Vector3[] floorPositions, bodyPositions;
    Quaternion[] floorRotations, bodyRotations;
    readonly List<GameObject> projectiles = new List<GameObject>();
    float clock, nextShot;
    int nextLauncher;
    TMPro.TextMeshProUGUI hud;
    HumanoidFootwork footwork;

    void Start()
    {
        floorPositions = System.Array.ConvertAll(platforms, p => p.position);
        floorRotations = System.Array.ConvertAll(platforms, p => p.rotation);
        bodyPositions = System.Array.ConvertAll(humanoid.bodies, b => b.position);
        bodyRotations = System.Array.ConvertAll(humanoid.bodies, b => b.rotation);
        nextShot = 4;
        footwork = humanoid.GetComponent<HumanoidFootwork>();
        CreateGuide();
    }
    void Update()
    {
        var k=Keyboard.current;
        if(k!=null)
        {
            if(k.spaceKey.wasPressedThisFrame) Fire(nextLauncher++ % launchers.Length);
            if(k.digit1Key.wasPressedThisFrame) Fire(0);
            if(k.digit2Key.wasPressedThisFrame) Fire(1);
            if(k.digit3Key.wasPressedThisFrame) Fire(2);
            if(k.digit4Key.wasPressedThisFrame) Fire(3);
            if(k.bKey.wasPressedThisFrame) FireBurst(nextLauncher++ % launchers.Length);
            if(k.fKey.wasPressedThisFrame) automaticFire=!automaticFire;
            if(k.gKey.wasPressedThisFrame) shiftingGround=!shiftingGround;
            if(k.rKey.wasPressedThisFrame) ResetTrial();
        }
        if(automaticFire && clock>=nextShot)
        { Fire(nextLauncher++ % launchers.Length); nextShot=clock+Mathf.Max(.5f,fireInterval); }
        if(hud) hud.text = "BALANCE LAB\n\nSPACE  Fire next launcher\n1 / 2 / 3 / 4  Choose direction\nB  Burst of "+burstSize+", next launcher\nF  Automatic fire: "+(automaticFire?"ON":"OFF")+"\nG  Ground motion: "+(shiftingGround?"ON":"PAUSED")+"\nR  Reset trial\n\nProjectile: "+projectileMass.ToString("F1")+" kg at ~"+projectileSpeed.ToString("F0")+" m/s\nShots / body hits: "+shots+" / "+impacts+"\nLast hit: "+lastImpactImpulse.ToString("F1")+" N s\n\n"+humanoid.balanceState+"\nFootwork: "+footwork.state+"   Steps: "+footwork.stepsLanded+"\nClick Game view for keyboard control";
    }
    void FixedUpdate()
    {
        clock+=Time.fixedDeltaTime;
        if(!shiftingGround || floorPositions==null) return;
        float ramp=Mathf.SmoothStep(0,1,Mathf.Clamp01((clock-2)/4));
        float t=clock*frequency*2*Mathf.PI;
        for(int i=0;i<platforms.Length;i++)
        {
            float phase=i*1.17f;
            // Each foot can stand on a separate slab with its own elevation and slope.
            float offset=Mathf.Sin(t+phase)*heightAmplitude*ramp;
            platforms[i].MovePosition(floorPositions[i]+Vector3.up*offset);
            platforms[i].MoveRotation(floorRotations[i]*Quaternion.Euler(Mathf.Sin(t*.71f+phase)*tiltDegrees*ramp,0,Mathf.Cos(t*.83f+phase)*tiltDegrees*ramp));
        }
    }
    // The pose the moving floor has at `time`, held still.
    public void PoseFloor(float time, float amplitude, float tilt)
    {
        float t=time*frequency*2*Mathf.PI;
        Vector3 start=humanoid?humanoid.pelvis.position:Vector3.zero;
        for(int i=0;i<platforms.Length;i++)
        {
            Vector3 p=floorPositions[i];
            if(Mathf.Abs(p.x-start.x)<.8f && Mathf.Abs(p.z-start.z)<.4f) { platforms[i].position=p; platforms[i].rotation=floorRotations[i]; continue; }
            float phase=i*1.17f;
            platforms[i].position=p+Vector3.up*Mathf.Sin(t+phase)*amplitude;
            platforms[i].rotation=floorRotations[i]*Quaternion.Euler(Mathf.Sin(t*.71f+phase)*tilt,0,Mathf.Cos(t*.83f+phase)*tilt);
        }
        Physics.SyncTransforms();
    }
    public void Fire(int index)
    {
        if(!Application.isPlaying || !humanoid || launchers.Length==0) return;
        FireFrom(launchers[index % launchers.Length].position);
    }
    // A burst of balls from one launcher, each launched at the torso as it is at that moment.
    public void FireBurst(int index)
    {
        if(!Application.isPlaying || !humanoid || launchers.Length==0) return;
        StartCoroutine(Burst(index));
    }
    System.Collections.IEnumerator Burst(int index)
    {
        for(int i=0;i<burstSize;i++)
        {
            Fire(index);
            float wait=clock+burstInterval-Time.fixedDeltaTime*.5f;
            while(clock<wait) yield return new WaitForFixedUpdate();
        }
    }
    public void FireFrom(Vector3 start)
    {
        if(!Application.isPlaying || !humanoid) return;
        projectiles.RemoveAll(p=>!p);
        if(projectiles.Count>=20) { Destroy(projectiles[0]); projectiles.RemoveAt(0); }
        Vector3 target=humanoid.bodies[1].worldCenterOfMass;
        float flight=Vector3.Distance(start,target)/Mathf.Max(2,projectileSpeed);
        var ball=GameObject.CreatePrimitive(PrimitiveType.Sphere); ball.name="Test Projectile"; ball.layer=2;
        ball.transform.position=start; ball.transform.localScale=Vector3.one*projectileRadius*2;
        if(projectileMaterial) ball.GetComponent<Renderer>().sharedMaterial=projectileMaterial;
        var body=ball.AddComponent<Rigidbody>(); body.mass=Mathf.Max(.1f,projectileMass);
        body.collisionDetectionMode=CollisionDetectionMode.ContinuousDynamic;
        body.interpolation=RigidbodyInterpolation.Interpolate;
        body.linearVelocity=(target-start)/flight-Physics.gravity*flight*.5f;
        // Initial launch velocity only. Subsequent motion and impact are entirely simulated.
        ball.AddComponent<BalanceProjectile>().arena=this;
        projectiles.Add(ball); shots++; Destroy(ball,8);
    }
    public void RegisterImpact(float impulse) { impacts++; lastImpactImpulse=impulse; totalImpactImpulse+=impulse; }
    [ContextMenu("Reset trial (Play mode)")]
    public void ResetTrial()
    {
        if(!Application.isPlaying || bodyPositions==null) return;
        foreach(var p in projectiles) if(p) { p.SetActive(false); Destroy(p); }
        projectiles.Clear();
        for(int i=0;i<platforms.Length;i++) { platforms[i].position=floorPositions[i]; platforms[i].rotation=floorRotations[i]; }
        for(int i=0;i<humanoid.bodies.Length;i++)
        {
            var b=humanoid.bodies[i]; b.position=bodyPositions[i]; b.rotation=bodyRotations[i];
            b.linearVelocity=Vector3.zero; b.angularVelocity=Vector3.zero; b.WakeUp();
        }
        StopAllCoroutines();
        // After the humanoid is back at the start, so the level slabs are the ones under it now.
        if(roughAt>=0) PoseFloor(roughAt,roughHeight,roughTilt);
        humanoid.ResetFeedback(); Physics.SyncTransforms(); clock=0; nextShot=4; shots=impacts=ballsUnderfoot=0; lastImpactImpulse=totalImpactImpulse=0;
    }
    void CreateGuide()
    {
        var canvasObject=new GameObject("Balance Controls",typeof(Canvas),typeof(UnityEngine.UI.CanvasScaler));
        canvasObject.transform.SetParent(transform,false);
        canvasObject.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;
        var scaler=canvasObject.GetComponent<UnityEngine.UI.CanvasScaler>();
        scaler.uiScaleMode=UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution=new Vector2(1280,720); scaler.matchWidthOrHeight=.5f;
        var panel=new GameObject("Guide Panel",typeof(RectTransform),typeof(UnityEngine.UI.Image)); panel.transform.SetParent(canvasObject.transform,false);
        var rect=panel.GetComponent<RectTransform>(); rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1); rect.anchoredPosition=new Vector2(16,-16); rect.sizeDelta=new Vector2(320,415);
        var background=panel.GetComponent<UnityEngine.UI.Image>(); background.color=new Color(.025f,.045f,.07f,.88f); background.raycastTarget=false;
        var label=new GameObject("Instructions",typeof(RectTransform)); label.transform.SetParent(panel.transform,false);
        hud=label.AddComponent<TMPro.TextMeshProUGUI>();
        hud.font=Resources.Load<TMPro.TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        hud.fontSize=17; hud.color=Color.white; hud.raycastTarget=false;
        var textRect=label.GetComponent<RectTransform>(); textRect.anchorMin=Vector2.zero; textRect.anchorMax=Vector2.one; textRect.offsetMin=new Vector2(16,12); textRect.offsetMax=new Vector2(-12,-12);
    }
}
