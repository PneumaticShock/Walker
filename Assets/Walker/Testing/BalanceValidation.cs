using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Explicit test fixture. Pose resets happen only BETWEEN trials, never during recovery.
public class BalanceValidation : MonoBehaviour
{
    [System.Serializable] public class Trial
    {
        public string name;
        public bool wholeBody, passed;
        public float minimumHeight, finalSpeed, peakHipTarget, peakArmTarget, peakTrunkAngle;
    }
    [System.Serializable] public class Report { public List<Trial> trials = new List<Trial>(); }
    public bool finished;
    public string result;
    public IEnumerator Start()
    {
        var h = GetComponent<JointBalanceHumanoid>();
        var pushes = GetComponent<ExternalPush>();
        Vector3 oldForce = pushes.constantForce; pushes.constantForce = Vector3.zero;
        var ground = GameObject.Find("Balance Ground").transform;
        Quaternion groundRotation = ground.rotation;
        var positions = System.Array.ConvertAll(h.bodies, b => b.position);
        var rotations = System.Array.ConvertAll(h.bodies, b => b.rotation);
        var report = new Report();
        var wait = new WaitForFixedUpdate();
        foreach (bool full in new[] { false, true })
        for (int scenario = 0; scenario < 7; scenario++)
        {
            ground.rotation = groundRotation;
            for (int i=0;i<h.bodies.Length;i++)
            {
                var b=h.bodies[i]; b.position=positions[i]; b.rotation=rotations[i];
                b.linearVelocity=Vector3.zero; b.angularVelocity=Vector3.zero; b.WakeUp();
            }
            h.ResetFeedback(); h.wholeBodyBalance=full; Physics.SyncTransforms();
            string[] names={"quiet", "forward impulse 10 Ns", "backward impulse 10 Ns", "side impulse 10 Ns", "forward force 15 N", "ground roll 3 degrees", "forward impulse 25 Ns"};
            var trial=new Trial {name=names[scenario],wholeBody=full,minimumHeight=10};
            for(int tick=0;tick<1200;tick++)
            {
                if(tick==200)
                {
                    Vector3 impulse=scenario==1?Vector3.forward*10:scenario==2?Vector3.back*10:scenario==3?Vector3.right*10:scenario==6?Vector3.forward*25:Vector3.zero;
                    h.pelvis.AddForce(impulse,ForceMode.Impulse);
                }
                if(scenario==4 && tick>=200 && tick<700) h.pelvis.AddForce(Vector3.forward*15,ForceMode.Force);
                if(scenario==5 && tick>=200 && tick<400)
                { ground.rotation=groundRotation*Quaternion.Euler(0,0,3f*(tick-200)/199f); Physics.SyncTransforms(); }
                yield return wait;
                trial.minimumHeight=Mathf.Min(trial.minimumHeight,h.pelvis.position.y);
                trial.peakHipTarget=Mathf.Max(trial.peakHipTarget,h.hipTargetDegrees);
                trial.peakArmTarget=Mathf.Max(trial.peakArmTarget,h.armTargetDegrees);
                trial.peakTrunkAngle=Mathf.Max(trial.peakTrunkAngle,Vector3.Angle(h.bodies[1].rotation*Vector3.up,Vector3.up));
            }
            trial.finalSpeed=h.pelvis.linearVelocity.magnitude;
            trial.passed=trial.minimumHeight>.75f && trial.finalSpeed<.15f;
            report.trials.Add(trial);
        }
        h.wholeBodyBalance=true; pushes.constantForce=oldForce; ground.rotation=groundRotation;
        result=JsonUtility.ToJson(report,true);
        System.IO.Directory.CreateDirectory("BalanceResults");
        System.IO.File.WriteAllText("BalanceResults/validation.json",result);
        finished=true;
    }
}
