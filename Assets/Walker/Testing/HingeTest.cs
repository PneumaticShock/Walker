using UnityEngine;

// Test fixture for the split foot's hinge on its own, high above the floor, with the rear part held still.
// Torque the front part up for a second, then down for a second, and record how far it went each way.
// With the spring off it should stop near `up` and `down`, which checks the limits act on the right sides.
public class HingeTest : MonoBehaviour
{
    public float up = 76, down = 34, stiffness = 30, torque = 1;
    public bool spring;
    public Vector3 at = new Vector3(0, 5, 0);
    public string result = "running";
    Rigidbody rear, front;
    HingeJoint hinge;
    float t, mostUp, mostDown;
    void Start()
    {
        rear = Box("Hinge test rear", at, new Vector3(.15f, .12f, .2f), 1, true);
        front = Box("Hinge test front", at + new Vector3(0, 0, .14f), new Vector3(.15f, .12f, .08f), .15f, false);
        // The two parts of one foot never collide with each other, as in the humanoid.
        Physics.IgnoreCollision(rear.GetComponent<Collider>(), front.GetComponent<Collider>());
        hinge = JointBalanceHumanoid.FrontHinge(front.gameObject, rear, at + new Vector3(0, -.06f + .03f, .1f), up, down, stiffness);
        hinge.useSpring = spring;
    }
    Rigidbody Box(string name, Vector3 p, Vector3 size, float mass, bool held)
    {
        var go = new GameObject(name) { layer = 2 };
        go.transform.position = p;
        go.AddComponent<BoxCollider>().size = size;
        var b = go.AddComponent<Rigidbody>(); b.mass = mass; b.isKinematic = held; b.useGravity = false;
        return b;
    }
    void FixedUpdate()
    {
        if (!front) return;
        t += Time.fixedDeltaTime;
        // Pitch of the front part relative to the rear, positive when its far end is above the rear part's line.
        Vector3 f = Quaternion.Inverse(rear.rotation) * (front.rotation * Vector3.forward);
        float bendUp = Mathf.Atan2(f.y, f.z) * Mathf.Rad2Deg;
        mostUp = Mathf.Max(mostUp, bendUp); mostDown = Mathf.Max(mostDown, -bendUp);
        // About +x a positive torque turns +z towards -y: front end down.
        if (t < 1) front.AddTorque(rear.rotation * Vector3.right * -torque);
        else if (t < 2) front.AddTorque(rear.rotation * Vector3.right * torque);
        else if (result == "running")
        {
            result = $"up {mostUp:0.0} (limit {up}), down {mostDown:0.0} (limit {down})";
            Destroy(rear.gameObject); Destroy(front.gameObject);
        }
    }
}
