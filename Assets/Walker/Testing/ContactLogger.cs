using System.Text;
using UnityEngine;

// Test fixture: every physics tick for `seconds`, the contact load on each humanoid body and the height and
// pitch of each foot part, written to BalanceResults/contacts-<label>.csv. Add it in edit mode to start with
// the first tick. It found the split foot's missing contact loads.
public class ContactLogger : MonoBehaviour
{
    public JointBalanceHumanoid humanoid;
    public string label = "log";
    public float seconds = 2;
    FootContactSensor[] sensors;
    readonly StringBuilder log = new StringBuilder();
    float t;
    bool written;
    void Start()
    {
        sensors = new FootContactSensor[humanoid.bodies.Length];
        log.Append("t,pelvisY");
        for (int i = 0; i < humanoid.bodies.Length; i++)
        {
            var b = humanoid.bodies[i];
            // A logger of its own on every body, so the footwork's sensors are left alone.
            sensors[i] = b.gameObject.AddComponent<FootContactSensor>();
            log.Append(",").Append(b.name.Replace(' ', '_'));
        }
        foreach (var f in humanoid.feet) log.Append(",").Append(f.name.Replace(' ', '_')).Append("_y,").Append(f.name.Replace(' ', '_')).Append("_pitch");
        if (humanoid.fronts != null) foreach (var f in humanoid.fronts) log.Append(",").Append(f.name.Replace(' ', '_')).Append("_y,").Append(f.name.Replace(' ', '_')).Append("_pitch");
        log.AppendLine();
    }
    void FixedUpdate()
    {
        if (written) return;
        t += Time.fixedDeltaTime;
        log.Append(t.ToString("0.00")).Append(",").Append(humanoid.pelvis.position.y.ToString("0.000"));
        foreach (var s in sensors) log.Append(",").Append(s.load.ToString("0"));
        void Part(Rigidbody b) { Vector3 f = b.rotation * Vector3.forward; log.Append(",").Append(b.position.y.ToString("0.000")).Append(",").Append((Mathf.Atan2(-f.y, f.z) * Mathf.Rad2Deg).ToString("0.0")); }
        foreach (var f in humanoid.feet) Part(f);
        if (humanoid.fronts != null) foreach (var f in humanoid.fronts) Part(f);
        log.AppendLine();
        if (Mathf.Abs(t - .3f) < .005f)
        {
            // What each foot part is resting on, and whether physics has been told to ignore that contact.
            var parts = new System.Collections.Generic.List<Rigidbody>(humanoid.feet);
            if (humanoid.fronts != null) parts.AddRange(humanoid.fronts);
            var info = new StringBuilder("#");
            foreach (var b in parts)
            {
                var c = b.GetComponent<BoxCollider>();
                info.Append(b.name).Append(" size ").Append(c.size.ToString("0.000")).Append(" centre ").Append(c.center.ToString("0.000")).Append(" minY ").Append(c.bounds.min.y.ToString("0.000"));
                info.Append(" mass ").Append(b.mass.ToString("0.00")).Append(" com ").Append(b.centerOfMass.ToString("0.000"));
                foreach (var x in Physics.OverlapBox(c.bounds.center - Vector3.up * (c.bounds.extents.y + .005f), new Vector3(c.bounds.extents.x, .01f, c.bounds.extents.z), Quaternion.identity, 1))
                    if (!x.GetComponentInParent<JointBalanceHumanoid>()) info.Append(" on ").Append(x.name).Append(" ignored=").Append(Physics.GetIgnoreCollision(c, x));
                info.Append(" | ");
            }
            log.AppendLine(info.ToString());
        }
        if (t >= seconds)
        {
            System.IO.Directory.CreateDirectory("BalanceResults");
            System.IO.File.WriteAllText("BalanceResults/contacts-" + label + ".csv", log.ToString());
            written = true;
        }
    }
}
