using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

// Optional validation fixture; never attached to the saved playable scene.
// Runs a matrix of disturbances through ordinary FixedUpdate physics and the production
// controller. Pushes and resets are test inputs, applied only between/at the start of trials.
// Add it to the arena in Play mode and call Run("label"); poll `finished`.
public class RecoveryStepBench : MonoBehaviour
{
    [System.Serializable] public class Case
    {
        public string name;
        public Vector3 impulse;
        public bool terrain;
        public bool steps=true;
        public int projectiles;      // fired from `launcher`, `spacing` seconds apart
        public int launcher;
        public float spacing=.2f;
        public float seconds=10;
        public Vector3 second;       // a second push, `secondAfter` seconds after the first
        public float secondAfter=3;
        public float settle=3;       // seconds before the disturbance
        public Vector3[] from;       // balls come from these directions in turn, instead of `launcher`
        public int secondBalls;      // a second burst, from `secondFrom`
        public Vector3[] secondFrom;
        public float afterLanding=-1; // fire the second disturbance this long after the first step lands
        public bool hold;            // test the pelvis-controlled hold alone, with no steps
        public int march=-1;         // test a step taken from rest onto this foot
        public Vector3[] series;     // bursts of `projectiles` balls, one from each of these, `seriesGap` apart
        public float seriesGap=6;
        public int repeatTicks=7;    // how much later each repeat starts
        public int cornerFoot=-1;    // start with this foot where four slabs meet, at `corner`
        public Vector3 corner;
    }
    [System.Serializable] public class Row
    {
        public string name;
        public bool fell, recovered;
        public float fellAfter=-1, minimumPelvis=10, finalPelvis, finalSpeed, peakTrunk, reaction=-1, travel;
        public int started, landed, cancelled;
        public float firstStep;
        public string finalState;
        public int hits, landedBeforeSecond, underfoot;
        public bool normalAtEnd;
        public float lastStep=-1, standBack=-1, slip;
        public float impulse, secondAt=-1;
    }
    [System.Serializable] public class Report { public string label; public string settings; public float timestep; public List<Row> rows=new List<Row>(); public int passed, total; }

    public bool finished;
    public int completed, total;
    public float timeScale=8;
    public string lastRow="";
    public Report report;
    float savedScale=1;

    public static List<Case> Matrix()
    {
        var cases=new List<Case>();
        cases.Add(new Case{name="quiet",seconds=6});
        void Push(string name,Vector3 dir,params float[] sizes)
        { foreach(float s in sizes) cases.Add(new Case{name="push "+name+" "+s+"Ns",impulse=dir*s}); }
        Push("forward",Vector3.forward,10,20,30,40,50,60,70,80);
        Push("backward",Vector3.back,10,20,30,40,50,60);
        Push("right",Vector3.right,10,20,30,40,50,60);
        Push("left",Vector3.left,10,20,30,40,50,60);
        Vector3 fr=(Vector3.forward+Vector3.right).normalized, bl=(Vector3.back+Vector3.left).normalized;
        Push("forward-right",fr,20,40,60);
        Push("backward-left",bl,20,40,60);
        foreach(float s in new[]{20f,40f})
        {
            cases.Add(new Case{name="terrain push forward "+s+"Ns",impulse=Vector3.forward*s,terrain=true});
            cases.Add(new Case{name="terrain push right "+s+"Ns",impulse=Vector3.right*s,terrain=true});
        }
        cases.Add(new Case{name="terrain quiet",terrain=true,seconds=20});
        for(int l=0;l<4;l++) cases.Add(new Case{name="projectile launcher"+(l+1),projectiles=1,launcher=l});
        for(int l=0;l<4;l++) cases.Add(new Case{name="two projectiles launcher"+(l+1),projectiles=2,launcher=l,spacing=.2f});
        // The moving ground reaches full amplitude 6 s in. This is what pressing Play shows.
        foreach(float s in new[]{20f,30f,40f}) cases.Add(new Case{name="moving ground forward "+s+"Ns",impulse=Vector3.forward*s,terrain=true,settle=8});
        foreach(float s in new[]{30f,40f}) cases.Add(new Case{name="moving ground backward "+s+"Ns",impulse=Vector3.back*s,terrain=true,settle=8});
        foreach(float s in new[]{30f,40f,50f}) cases.Add(new Case{name="moving ground right "+s+"Ns",impulse=Vector3.right*s,terrain=true,settle=8});
        cases.Add(new Case{name="moving ground quiet",terrain=true,settle=8,seconds=30});
        for(int l=0;l<4;l++) cases.Add(new Case{name="moving ground projectile launcher"+(l+1),projectiles=1,launcher=l,terrain=true,settle=8});
        for(int l=0;l<4;l++) cases.Add(new Case{name="moving ground two projectiles launcher"+(l+1),projectiles=2,launcher=l,spacing=.2f,terrain=true,settle=8});
        cases.Add(new Case{name="repeat forward 30Ns then forward 30Ns",impulse=Vector3.forward*30,second=Vector3.forward*30,seconds=12});
        cases.Add(new Case{name="repeat forward 30Ns then backward 30Ns",impulse=Vector3.forward*30,second=Vector3.back*30,seconds=12});
        cases.Add(new Case{name="repeat forward 30Ns then right 30Ns",impulse=Vector3.forward*30,second=Vector3.right*30,seconds=12});
        cases.Add(new Case{name="repeat backward 30Ns then forward 20Ns",impulse=Vector3.back*30,second=Vector3.forward*20,seconds=12});
        cases.Add(new Case{name="march onto left",march=0,settle=4,seconds=5});
        cases.Add(new Case{name="march onto right",march=1,settle=4,seconds=5});
        // What the B key does: burst after burst with no reset, with the spent balls still on the floor.
        foreach(float gap in new[]{6f,3f})
        {
            cases.Add(new Case{name="series around "+gap+"s",projectiles=6,spacing=.1f,series=new[]{Dir(0,-1),Dir(1,0),Dir(0,1),Dir(-1,0),Dir(0,1),Dir(0,-1)},seriesGap=gap,seconds=6*gap+6});
            cases.Add(new Case{name="series opposite "+gap+"s",projectiles=6,spacing=.1f,series=new[]{Dir(0,1),Dir(0,-1),Dir(1,0),Dir(-1,0),Dir(1,1),Dir(-1,-1)},seriesGap=gap,seconds=6*gap+6});
            cases.Add(new Case{name="series same "+gap+"s",projectiles=6,spacing=.1f,series=new[]{Dir(0,-1),Dir(0,-1),Dir(0,-1),Dir(0,-1)},seriesGap=gap,seconds=4*gap+6});
        }
        // The hold alone, in a normal stance, with stepping off.
        foreach(var d in new[]{("forward",Vector3.forward),("backward",Vector3.back),("right",Vector3.right)})
            foreach(float m in new[]{0f,10f,20f,30f,40f,50f}) cases.Add(new Case{name="hold "+d.Item1+" "+m+"Ns",impulse=d.Item2*m,hold=true,steps=false,settle=4});
        // Flat ground: bursts of balls 0.1 s apart, from every direction.
        foreach(var side in Sides) for(int n=1;n<=6;n++) cases.Add(new Case{name="burst "+n+" from "+side.name,projectiles=n,from=new[]{side.dir},spacing=.1f});
        cases.Add(new Case{name="burst 6 mixed around",projectiles=6,from=new[]{Dir(0,1),Dir(1,0),Dir(0,-1),Dir(-1,0),Dir(1,1),Dir(-1,-1)},spacing=.1f});
        cases.Add(new Case{name="burst 6 mixed front-back",projectiles=6,from=new[]{Dir(0,1),Dir(0,-1)},spacing=.1f});
        cases.Add(new Case{name="burst 6 mixed left-right",projectiles=6,from=new[]{Dir(1,0),Dir(-1,0)},spacing=.1f});
        cases.Add(new Case{name="burst 6 mixed scattered",projectiles=6,from=new[]{Dir(1,1),Dir(-1,0),Dir(0,-1),Dir(1,-1),Dir(-1,1),Dir(1,0)},spacing=.1f});
        // A second hit from each side, about a second after the step that caught the first.
        foreach(var first in new[]{Sides[0],Sides[2],Sides[4],Sides[6]}) foreach(var side in Sides)
        {
            cases.Add(new Case{name="again 4 from "+first.name+" then 4 from "+side.name,projectiles=4,from=new[]{first.dir},spacing=.1f,secondBalls=4,secondFrom=new[]{side.dir},afterLanding=1,seconds=12});
            cases.Add(new Case{name="again push from "+first.name+" then from "+side.name,impulse=-first.dir*40,second=-side.dir*40,afterLanding=1,seconds=12});
        }
        // The same balls on the moving floor at full height and tilt. Each repeat starts about 0.4 s later,
        // so that together they meet the slabs across their whole cycle, and the steps between them.
        const int Sweep=37;
        foreach(var side in Sides) for(int n=1;n<=6;n++) cases.Add(new Case{name="moving ground "+n+" balls from "+side.name,projectiles=n,from=new[]{side.dir},spacing=.1f,terrain=true,settle=8,repeatTicks=Sweep});
        cases.Add(new Case{name="moving ground 6 balls mixed around",projectiles=6,from=new[]{Dir(0,1),Dir(1,0),Dir(0,-1),Dir(-1,0),Dir(1,1),Dir(-1,-1)},spacing=.1f,terrain=true,settle=8,repeatTicks=Sweep});
        cases.Add(new Case{name="moving ground 6 balls mixed front-back",projectiles=6,from=new[]{Dir(0,1),Dir(0,-1)},spacing=.1f,terrain=true,settle=8,repeatTicks=Sweep});
        cases.Add(new Case{name="moving ground 6 balls mixed left-right",projectiles=6,from=new[]{Dir(1,0),Dir(-1,0)},spacing=.1f,terrain=true,settle=8,repeatTicks=Sweep});
        cases.Add(new Case{name="moving ground 6 balls mixed scattered",projectiles=6,from=new[]{Dir(1,1),Dir(-1,0),Dir(0,-1),Dir(1,-1),Dir(-1,1),Dir(1,0)},spacing=.1f,terrain=true,settle=8,repeatTicks=Sweep});
        foreach(var first in new[]{Sides[0],Sides[2],Sides[4],Sides[6]}) foreach(var side in Sides)
            cases.Add(new Case{name="moving ground again 4 from "+first.name+" then 4 from "+side.name,projectiles=4,from=new[]{first.dir},spacing=.1f,secondBalls=4,secondFrom=new[]{side.dir},afterLanding=1,seconds=12,terrain=true,settle=8,repeatTicks=Sweep});
        // No balls: one foot starts where four slabs meet. Moving slabs for nearly five of their cycles, and
        // still ones, which leaves only the geometry: the gaps between the slabs, and no step between them.
        foreach(var at in new[]{new Vector3(0,0,.4f),new Vector3(0,0,-.4f),new Vector3(.8f,0,.4f),new Vector3(-.8f,0,.4f),new Vector3(.8f,0,-.4f),new Vector3(-.8f,0,-.4f)})
            for(int foot=0;foot<2;foot++)
            {
                string where=(foot==0?"left":"right")+" foot x"+at.x.ToString("0.0",CultureInfo.InvariantCulture)+" z"+at.z.ToString("0.0",CultureInfo.InvariantCulture);
                cases.Add(new Case{name="corner moving "+where,cornerFoot=foot,corner=at,terrain=true,settle=1,seconds=40});
                cases.Add(new Case{name="corner still "+where,cornerFoot=foot,corner=at,settle=1,seconds=20});
            }
        cases.Add(new Case{name="corner moving control",terrain=true,settle=1,seconds=40});
        cases.Add(new Case{name="corner still control",settle=1,seconds=20});
        return cases;
    }
    static Vector3 Dir(float x,float z) => new Vector3(x,0,z).normalized;
    static readonly (string name,Vector3 dir)[] Sides={("front",Dir(0,1)),("front-right",Dir(1,1)),("right",Dir(1,0)),("back-right",Dir(1,-1)),("back",Dir(0,-1)),("back-left",Dir(-1,-1)),("left",Dir(-1,0)),("front-left",Dir(-1,1))};
    void Launch(BalanceTestArena a,Case c,int ball,Vector3[] from)
    {
        if(from==null) { a.Fire(c.launcher); return; }
        Vector3 launcher=a.launchers[0].position;
        Vector3 d=from[ball%from.Length]*new Vector2(launcher.x,launcher.z).magnitude;
        a.FireFrom(new Vector3(d.x,launcher.y,d.z));
    }

    public bool stepsEnabled=true;
    public void Run(string label)
    {
        var all=Matrix(); foreach(var c in all) c.steps=stepsEnabled && !c.hold;
        StopAllCoroutines(); StartCoroutine(RunCases(label,all,false));
    }
    public void RunNamed(string label,string filter)
    {
        string[] any=filter.Split('|');
        var picked=Matrix().FindAll(c=>System.Array.Exists(any,f=>c.name.Contains(f)));
        foreach(var c in picked) c.steps=stepsEnabled && !c.hold;
        StopAllCoroutines(); StartCoroutine(RunCases(label,picked,traces));
    }
    public bool traces=true;

    IEnumerator RunCases(string label,List<Case> cases,bool trace)
    {
        savedScale=Time.timeScale; Time.timeScale=timeScale;
        float savedStep=Time.maximumDeltaTime; Time.maximumDeltaTime=2f;
        finished=false; completed=0; total=cases.Count;
        report=new Report{label=label,timestep=Time.fixedDeltaTime,settings=JsonUtility.ToJson(GetComponent<BalanceTestArena>().humanoid.GetComponent<HumanoidFootwork>())};
        System.IO.Directory.CreateDirectory("BalanceResults");
        total*=repeats;
        foreach(var c in cases) for(int r=0;r<repeats;r++)
        {
            yield return RunCase(c,trace,firstRepeat+r*stride);
            completed++;
            string path="BalanceResults/recovery-"+label+".json";
            report.total=report.rows.Count; report.passed=report.rows.FindAll(r=>r.recovered).Count;
            System.IO.File.WriteAllText(path,JsonUtility.ToJson(report,true));
        }
        Time.timeScale=savedScale; Time.maximumDeltaTime=savedStep;
        finished=true;
    }

    // Tuning sweep. Each setting is "field=value;field=value" on HumanoidFootwork. One line of
    // results per setting goes to BalanceResults/sweep-<label>.txt, best-first once finished.
    public void Sweep(string label,string[] settings,string[] caseNames)
    {
        StopAllCoroutines(); StartCoroutine(RunSweep(label,settings,caseNames));
    }
    // Same, reading a file: first line is case names separated by '|', then one setting per line.
    public void SweepFile(string label,string path)
    {
        var lines=System.IO.File.ReadAllLines(path);
        var settings=new string[lines.Length-1];
        System.Array.Copy(lines,1,settings,0,settings.Length);
        Sweep(label,settings,lines[0].Split('|'));
    }
    IEnumerator RunSweep(string label,string[] settings,string[] caseNames)
    {
        var f=GetComponent<BalanceTestArena>().humanoid.GetComponent<HumanoidFootwork>();
        var cases=Matrix().FindAll(c=>System.Array.IndexOf(caseNames,c.name)>=0);
        savedScale=Time.timeScale; Time.timeScale=timeScale;
        float savedStep=Time.maximumDeltaTime; Time.maximumDeltaTime=2f;
        finished=false; completed=0; total=settings.Length;
        var lines=new List<string>();
        var type=typeof(HumanoidFootwork);
        string original=JsonUtility.ToJson(f);
        foreach(string setting in settings)
        {
            JsonUtility.FromJsonOverwrite(original,f);
            foreach(string pair in setting.Split(';'))
            {
                if(pair.Length==0) continue;
                var kv=pair.Split('=');
                float value=float.Parse(kv[1],CultureInfo.InvariantCulture);
                var field=type.GetField(kv[0]);
                if(field.FieldType==typeof(bool)) field.SetValue(f,value!=0); else field.SetValue(f,value);
            }
            report=new Report{label=label,timestep=Time.fixedDeltaTime};
            foreach(var c in cases) for(int r=0;r<repeats;r++) yield return RunCase(c,false,r);
            int ok=report.rows.FindAll(r=>r.recovered).Count;
            float held=0; var sb=new StringBuilder();
            foreach(var r in report.rows)
            {
                held+=r.fell?r.fellAfter:r.recovered?10:5;
                sb.Append(r.recovered?'.':r.fell?'F':'u');
            }
            lines.Add(ok.ToString("00")+"/"+report.rows.Count+" held="+held.ToString("000.0",CultureInfo.InvariantCulture)+" "+sb+" "+setting);
            lines.Sort((a,b)=>string.CompareOrdinal(b,a));
            System.IO.File.WriteAllLines("BalanceResults/sweep-"+label+".txt",lines);
            completed++;
        }
        JsonUtility.FromJsonOverwrite(original,f);
        Time.timeScale=savedScale; Time.maximumDeltaTime=savedStep;
        finished=true;
    }

    // Physics here is chaotic: one run says little, so `repeats` runs each case several times,
    // starting the disturbance a few ticks later each time.
    public int repeats=1;
    // Which repeats to run: firstRepeat, then every stride after it. A setting tuned on some repeats
    // can then be checked on others it has never seen.
    public int firstRepeat=0, stride=1;
    IEnumerator RunCase(Case c,bool trace,int repeat=0)
    {
        var a=GetComponent<BalanceTestArena>(); var h=a.humanoid; var f=h.GetComponent<HumanoidFootwork>();
        a.automaticFire=false; a.shiftingGround=c.terrain; f.recoverySteps=c.steps; a.ResetTrial(); f.holdOnly=false;
        if(c.cornerFoot>=0)
        {
            // The floor is level at the start of a trial, so the whole body can just be moved across.
            Vector3 move=c.corner-h.feet[c.cornerFoot].position; move.y=0;
            foreach(var body in h.bodies) body.position+=move;
            Physics.SyncTransforms();
        }
        var row=new Row{name=c.name};
        var log=trace?new StringBuilder("t,state,foot,comX,comZ,comY,cpX,cpZ,pelvisY,lX,lY,lZ,rX,rY,rZ,goalX,goalZ,supX,supZ,margin,trunk,pelvisPitch,lPitch,rPitch,lK,rK,lLoad,rLoad,lTY,lTZ,rTY,rTZ,lTX,rTX,pCmd,lKa,rKa,pelX,pelZ,rThC,rThA,rShC,rShA,rFoC,rFoA,rHipT,rKneeT,thighW,lRoll,rRoll,lAnkP,lAnkR,rAnkP,rAnkR,comVX,comVZ,lCopX,lCopZ,rCopX,rCopZ,lKneeZ,rKneeZ,lKneeY,rKneeY,pelRoll,trunkRoll,lThRoll,rThRoll,lShRoll,rShRoll,trunkPitch,lHipY,rHipY,lG,rG,lSpread,rSpread,goalY,lContact,rContact,progress\n"):null;
        float dt=Time.fixedDeltaTime;
        int settle=Mathf.RoundToInt(c.settle/dt)+repeat*c.repeatTicks, ticks=Mathf.RoundToInt(c.seconds/dt);
        Vector3 startL=h.feet[0].position, startR=h.feet[1].position;
        float shot=c.projectiles>0?settle:-1; int fired=0;
        // Second disturbance: at a fixed time, or a set time after the first step lands. If no
        // step ever lands, it goes off anyway, so the case still tests what it can.
        int secondTick=c.afterLanding>=0?settle+Mathf.RoundToInt(4/dt):settle+Mathf.RoundToInt(c.secondAfter/dt);
        bool hasSecond=c.second!=Vector3.zero || c.secondBalls>0, secondStarted=false;
        float secondShot=0; int secondFired=0; bool sawLanding=false;
        for(int i=0;i<settle+ticks;i++)
        {
            if(c.hold && i==settle-200) { f.holdOnly=true; f.EnterHold(h); }
            if(c.march>=0 && i==settle) f.ShiftOnto(h,c.march);
            if(i==settle && c.impulse!=Vector3.zero) h.pelvis.AddForce(c.impulse,ForceMode.Impulse);
            if(c.afterLanding>=0 && !sawLanding && i>settle && f.stepsLanded>0)
            { sawLanding=true; secondTick=Mathf.Min(secondTick,i+Mathf.RoundToInt(c.afterLanding/dt)); }
            if(hasSecond && !secondStarted && i>=secondTick)
            {
                secondStarted=true; secondShot=i;
                row.secondAt=(i-settle)*dt; row.landedBeforeSecond=f.stepsLanded;
                if(c.second!=Vector3.zero) h.pelvis.AddForce(c.second,ForceMode.Impulse);
            }
            if(c.series!=null)
            {
                int burst=(i-settle)/Mathf.RoundToInt(c.seriesGap/dt);
                int into=(i-settle)-burst*Mathf.RoundToInt(c.seriesGap/dt);
                int ballTicks=Mathf.RoundToInt(c.spacing/dt);
                if(i>=settle && burst<c.series.Length && into%ballTicks==0 && into/ballTicks<c.projectiles) Launch(a,c,0,new[]{c.series[burst]});
            }
            else if(c.projectiles>0 && fired<c.projectiles && i>=shot)
            { Launch(a,c,fired,c.from); fired++; shot=i+c.spacing/dt; }
            if(secondStarted && secondFired<c.secondBalls && i>=secondShot)
            { Launch(a,c,secondFired,c.secondFrom); secondFired++; secondShot=i+c.spacing/dt; }
            yield return new WaitForFixedUpdate();
            float t=(i-settle)*dt;
            float trunk=Vector3.Angle(h.bodies[1].rotation*Vector3.up,Vector3.up);
            if(i>=settle)
            {
                row.minimumPelvis=Mathf.Min(row.minimumPelvis,h.pelvis.position.y);
                row.peakTrunk=Mathf.Max(row.peakTrunk,trunk);
                if(row.reaction<0 && f.steppingFoot>=0) row.reaction=t;
                if(row.firstStep==0 && f.stepsLanded>0) row.firstStep=t;
                if(f.steppingFoot>=0)
                {
                    row.lastStep=t;
                    int planted=1-f.steppingFoot;
                    if(f.Contact(planted)) row.slip+=Horizontal(h.feet[planted].linearVelocity)*dt;
                }
                // When the humanoid last came back to quiet standing in a normal stance.
                if(f.phase!=HumanoidFootwork.Phase.Stand || !f.NormalStance) row.standBack=-1;
                else if(row.standBack<0) row.standBack=t;
                row.travel=Mathf.Max(row.travel,Mathf.Max(Horizontal(h.feet[0].position-startL),Horizontal(h.feet[1].position-startR)));
                bool down=h.pelvis.position.y<.55f || trunk>60;
                if(down && !row.fell) { row.fell=true; row.fellAfter=t; }
            }
            if(trace)
            {
                log.AppendLine(string.Join(",",F(t),f.state,f.steppingFoot.ToString(),F(h.centerOfMass.x),F(h.centerOfMass.z),F(h.centerOfMass.y),F(f.capture.x),F(f.capture.z),F(h.pelvis.position.y),
                    F(h.feet[0].position.x),F(h.feet[0].position.y),F(h.feet[0].position.z),F(h.feet[1].position.x),F(h.feet[1].position.y),F(h.feet[1].position.z),
                    F(f.landingTarget.x),F(f.landingTarget.z),F(f.supportPoint.x),F(f.supportPoint.z),F(f.supportMargin),F(trunk),F(Pitch(h.pelvis)),F(Pitch(h.feet[0])),F(Pitch(h.feet[1])),F(f.leftKneeDegrees),F(f.rightKneeDegrees),F(f.Load(0)),F(f.Load(1)),
                    F(f.Target(0).y),F(f.Target(0).z),F(f.Target(1).y),F(f.Target(1).z),F(f.Target(0).x),F(f.Target(1).x),F(f.PelvisCommand),F(Vector3.Angle(h.bodies[3].rotation*Vector3.up,h.bodies[4].rotation*Vector3.up)),F(Vector3.Angle(h.bodies[8].rotation*Vector3.up,h.bodies[9].rotation*Vector3.up)),F(h.pelvis.position.x),F(h.pelvis.position.z),
                    F(PitchOf(f.thighs[1])),F(Pitch(h.bodies[8])),F(PitchOf(f.shins[1])),F(Pitch(h.bodies[9])),F(PitchOf(h.motors[7].targetRotation)),F(Pitch(h.feet[1])),
                    F(h.motors[7].currentTorque.magnitude),F(h.motors[8].currentTorque.magnitude),F(h.bodies[8].angularVelocity.x),
                    F(Roll(h.feet[0])),F(Roll(h.feet[1])),"0","0","0","0",F(h.centerOfMassVelocity.x),F(h.centerOfMassVelocity.z),
                    F(f.Pressure(0).x),F(f.Pressure(0).z),F(f.Pressure(1).x),F(f.Pressure(1).z),F(Knee(h,3).z),F(Knee(h,8).z),F(Knee(h,3).y),F(Knee(h,8).y),F(Roll(h.pelvis)),F(Roll(h.bodies[1])),F(Roll(h.bodies[3])),F(Roll(h.bodies[8])),F(Roll(h.bodies[4])),F(Roll(h.bodies[9])),F(Pitch(h.bodies[1])),F((h.pelvis.position+h.pelvis.rotation*new Vector3(-.14f,-.07f,0)).y),F((h.pelvis.position+h.pelvis.rotation*new Vector3(.14f,-.07f,0)).y),
                    F(f.GroundUnder(0)),F(f.GroundUnder(1)),F(f.FootSpread(0)),F(f.FootSpread(1)),F(f.landingTarget.y),f.Contact(0)?"1":"0",f.Contact(1)?"1":"0",F(f.SwingProgress)));
            }
        }
        row.hits=a.impacts; row.impulse=a.totalImpactImpulse; row.normalAtEnd=f.NormalStance; row.underfoot=a.ballsUnderfoot;
        row.started=f.stepsStarted; row.landed=f.stepsLanded; row.cancelled=f.stepsCancelled;
        row.finalPelvis=h.pelvis.position.y; row.finalSpeed=h.pelvis.linearVelocity.magnitude; row.finalState=f.state;
        row.recovered=!row.fell && row.finalSpeed<.15f;
        f.holdOnly=false;
        report.rows.Add(row);
        lastRow=c.name+": "+(row.recovered?"OK":row.fell?"FELL":"UNSETTLED");
        if(trace) System.IO.File.WriteAllText("BalanceResults/trace-"+c.name.Replace(' ','_')+(repeat>0?"_r"+repeat:"")+".csv",log.ToString());
    }
    // Signed roll; positive means the body's top leans to the right (+x).
    static float Roll(Rigidbody b) { Vector3 up=b.rotation*Vector3.up; return Mathf.Atan2(up.x,up.y)*Mathf.Rad2Deg; }
    // World position of the knee: 0.2 m down the thigh from its centre.
    static Vector3 Knee(JointBalanceHumanoid h,int thigh) => h.bodies[thigh].position+h.bodies[thigh].rotation*Vector3.down*.2f;
    static float PitchOf(Quaternion q) { Vector3 up=q*Vector3.up; return Mathf.Atan2(up.z,up.y)*Mathf.Rad2Deg; }
    static float Horizontal(Vector3 v) { v.y=0; return v.magnitude; }
    // Signed pitch about world X; positive means the body's top leans forward (+z).
    static float Pitch(Rigidbody b) { Vector3 up=b.rotation*Vector3.up; return Mathf.Atan2(up.z,up.y)*Mathf.Rad2Deg; }
    static string F(float v) => v.ToString("0.###",CultureInfo.InvariantCulture);
}
