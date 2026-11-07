using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FootworkSearch : MonoBehaviour
{
    public bool finished;
    public int tested;
    public string best="none";
    IEnumerator Start()
    {
        var f=GetComponent<HumanoidFootwork>(); var h=GetComponent<JointBalanceHumanoid>();
        var trial=GetComponent<FootworkTrial>(); if(!trial)trial=gameObject.AddComponent<FootworkTrial>();
        var rows=new List<string>(); float bestScore=-100;
        foreach(float margin in new[]{.04f})
        foreach(float time in new[]{.24f,.28f,.32f})
        foreach(float overshoot in new[]{.03f,.06f,.10f})
        foreach(float gain in new[]{.25f,.35f})
        {
            f.triggerMargin=margin; f.stepTime=time; f.landingOvershoot=overshoot; f.servoGain=gain;
            trial.BeginProjectiles(.2f);
            while(!trial.result.finished)yield return null;
            float score=trial.result.minimumHeight-h.pelvis.linearVelocity.magnitude*.1f;
            string row=$"time={time} overshoot={overshoot} gain={gain} min={trial.result.minimumHeight} landed={f.stepsLanded} final={h.pelvis.position.y} speed={h.pelvis.linearVelocity.magnitude}";
            rows.Add(row); tested++;
            if(score>bestScore){bestScore=score;best=row;}
            System.IO.File.WriteAllLines("BalanceResults/footwork-search.txt",rows);
        }
        System.IO.File.WriteAllLines("BalanceResults/footwork-search.txt",rows);
        finished=true;
    }
}
