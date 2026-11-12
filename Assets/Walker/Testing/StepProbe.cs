using UnityEngine;

// Test fixture: one placed step (HumanoidFootwork.StepTo) from standing, logged every 0.05 s into `log`.
public class StepProbe : MonoBehaviour
{
    public int foot = 1;
    public Vector3 offset = new Vector3(0, 0, .28f);
    public float startAt = .5f, seconds = 4;
    public string log = "";
    float clock; bool sent;
    void FixedUpdate()
    {
        var h = GetComponent<JointBalanceHumanoid>(); var f = GetComponent<HumanoidFootwork>();
        clock += Time.fixedDeltaTime;
        if (!sent && clock > startAt) { f.closingSteps = false; f.heldStanceSpread = .38f; f.StepTo(foot, h.feet[foot].position + h.Heading * offset); sent = true; }
        if (sent && clock < startAt + seconds && Mathf.Repeat(clock, .05f) < Time.fixedDeltaTime)
            log += clock.ToString("0.00") + " " + f.state + " phase " + f.phase + " com(" + h.centerOfMass.x.ToString("0.00") + "," + h.centerOfMass.z.ToString("0.00") + ") cp(" + f.capture.x.ToString("0.00") + "," + f.capture.z.ToString("0.00") + ") L(" + h.feet[0].position.x.ToString("0.00") + "," + h.feet[0].position.z.ToString("0.00") + ") R(" + h.feet[1].position.x.ToString("0.00") + "," + h.feet[1].position.z.ToString("0.00") + ") goal(" + f.landingTarget.x.ToString("0.00") + "," + f.landingTarget.z.ToString("0.00") + ") loads " + f.Load(0).ToString("0") + "/" + f.Load(1).ToString("0") + " pel " + h.pelvis.position.y.ToString("0.00") + "\n";
    }
}
