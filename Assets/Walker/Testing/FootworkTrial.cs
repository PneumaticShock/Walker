using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Optional validation fixture; never attached to the saved playable scene.
public class FootworkTrial : MonoBehaviour
{
    [System.Serializable] public class Sample { public float time,height,leftKnee,rightKnee; public int started,landed; public Vector3 pelvis,leftFoot,rightFoot; public string detail; }
    [System.Serializable] public class Result { public bool terrain,steps,finished; public float impulse,minimumHeight=10; public List<Sample> samples=new List<Sample>(); }
    public Result result;
    bool projectileTrial;
    public void BeginProjectiles(float spacing=1f)
    { StopAllCoroutines(); projectileTrial=true; StartCoroutine(Run(true,true,0,30,spacing)); }
    public void Begin(bool terrain,bool steps,float impulse,float seconds)
    { StopAllCoroutines(); projectileTrial=false; StartCoroutine(Run(terrain,steps,impulse,seconds)); }
    IEnumerator Run(bool terrain,bool steps,float impulse,float seconds,float spacing=1f)
    {
        var a=Object.FindAnyObjectByType<BalanceTestArena>(); var h=a.humanoid; var f=h.GetComponent<HumanoidFootwork>();
        a.automaticFire=false; a.shiftingGround=terrain; f.recoverySteps=steps; a.ResetTrial();
        result=new Result{terrain=terrain,steps=steps,impulse=impulse};
        int ticks=Mathf.RoundToInt(seconds/Time.fixedDeltaTime),kick=Mathf.RoundToInt(3/Time.fixedDeltaTime);
        int rear=0;
        for(int j=1;j<a.launchers.Length;j++) if(a.launchers[j].position.z<a.launchers[rear].position.z) rear=j;
        for(int i=0;i<ticks;i++)
        {
            if(projectileTrial)
            { if(i==kick || i==kick+Mathf.RoundToInt(spacing/Time.fixedDeltaTime)) a.Fire(rear); }
            else if(i==kick) h.pelvis.AddForce(Vector3.forward*impulse,ForceMode.Impulse);
            yield return new WaitForFixedUpdate();
            result.minimumHeight=Mathf.Min(result.minimumHeight,h.pelvis.position.y);
            if(i%10==0)result.samples.Add(new Sample{time=h.elapsed,height=h.pelvis.position.y,leftKnee=f.leftKneeDegrees,rightKnee=f.rightKneeDegrees,started=f.stepsStarted,landed=f.stepsLanded,pelvis=h.pelvis.position,leftFoot=h.feet[0].position,rightFoot=h.feet[1].position,detail="goal "+f.landingTarget+" pelvis "+h.pelvis.rotation.eulerAngles+" thigh "+h.bodies[8].rotation.eulerAngles+" desired "+f.thighs[1].eulerAngles+" motor "+h.motors[7].targetRotation.eulerAngles+" state "+f.state});
        }
        result.finished=true;
        System.IO.Directory.CreateDirectory("BalanceResults");
        System.IO.File.WriteAllText("BalanceResults/footwork-"+(projectileTrial?"two-rear-projectiles":(terrain?"terrain":"push"))+"-"+(steps?"steps":"stance")+".json",JsonUtility.ToJson(result,true));
    }
}
