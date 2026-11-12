using UnityEngine;

// Passive sensing only: reports real collision contact and load for one foot.
// Added at runtime by HumanoidFootwork, so it is not serialized into the scene.
public class FootContactSensor : MonoBehaviour
{
    // Vertical contact load in newtons, measured over the last physics step.
    public float load;
    public bool touching;
    // Load-weighted contact position: where this foot's centre of pressure really is.
    public Vector3 pressure;
    float collected, weightSum;
    Vector3 weighted;
    static readonly ContactPoint[] points = new ContactPoint[16];
    int stamp=-1, seen=-1;

    void OnCollisionEnter(Collision c) => Collect(c);
    void OnCollisionStay(Collision c) => Collect(c);
    void Collect(Collision c)
    {
        // Rig bodies ignore each other, but a projectile may still strike the foot.
        if(c.rigidbody && !c.rigidbody.isKinematic) return;
        int tick=Mathf.RoundToInt(Time.fixedTime/Time.fixedDeltaTime);
        if(tick!=stamp) { collected=0; weightSum=0; weighted=Vector3.zero; stamp=tick; }
        // Which way round Unity reports the impulse depends on the order of the two bodies in the contact pair,
        // which is not always the same (it differs for a body created at runtime). Support from the ground
        // is always upward on the foot, so its size is what counts.
        collected+=Mathf.Abs(c.impulse.y)/Time.fixedDeltaTime;
        int n=c.GetContacts(points);
        for(int i=0;i<n;i++)
        {
            float w=Mathf.Abs(points[i].impulse.y);
            weighted+=points[i].point*w; weightSum+=w;
        }
        seen=tick;
    }
    // Collision callbacks run after the physics step; publish at the next FixedUpdate.
    void FixedUpdate()
    {
        int tick=Mathf.RoundToInt(Time.fixedTime/Time.fixedDeltaTime);
        touching=seen>=tick-1;
        load=touching?collected:0;
        if(weightSum>0) pressure=weighted/weightSum;
    }
}
