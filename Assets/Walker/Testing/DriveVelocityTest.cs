using UnityEngine;

// Test fixture: which frame and sign ConfigurableJoint.targetAngularVelocity takes. Four rigs, each a fixed
// parent and a spinning child joined by a slerp drive that only damps. Each FixedUpdate sets the target
// velocity to the child's own spin, by one of four conventions; the right one leaves the spin undamped.
// Add to any object in Play mode, wait a second, read `report`.
public class DriveVelocityTest : MonoBehaviour
{
    public string report = "";
    ConfigurableJoint[] joints = new ConfigurableJoint[5];
    Rigidbody[] children = new Rigidbody[5];
    float clock;
    readonly Vector3 spin0 = new Vector3(1.5f, .8f, -.6f);

    void Start()
    {
        for (int m = 0; m < 5; m++)
        {
            Vector3 at = new Vector3(40 + m * 3, 20, 40);
            var parent = new GameObject("Drive test parent " + m).AddComponent<Rigidbody>();
            parent.isKinematic = true; parent.transform.SetPositionAndRotation(at, Quaternion.Euler(30, 50, -20));
            var child = new GameObject("Drive test child " + m).AddComponent<Rigidbody>();
            child.useGravity = false; child.transform.SetPositionAndRotation(at + Vector3.down * .5f, Quaternion.Euler(-15, 20, 70));
            child.inertiaTensor = new Vector3(.1f, .1f, .1f); child.inertiaTensorRotation = Quaternion.identity; child.angularDamping = 0; child.maxAngularVelocity = 50;
            var j = child.gameObject.AddComponent<ConfigurableJoint>();
            j.connectedBody = parent; j.autoConfigureConnectedAnchor = true;
            j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;
            j.rotationDriveMode = RotationDriveMode.Slerp;
            j.slerpDrive = new JointDrive { positionSpring = 0, positionDamper = 2, maximumForce = 1e6f };
            child.angularVelocity = spin0;
            joints[m] = j; children[m] = child;
        }
    }

    void FixedUpdate()
    {
        clock += Time.fixedDeltaTime;
        for (int m = 0; m < 5; m++)
        {
            Vector3 w = children[m].angularVelocity;
            Vector3 v = Vector3.zero;
            if (m == 1) v = Quaternion.Inverse(children[m].rotation) * w;
            if (m == 2) v = -(Quaternion.Inverse(children[m].rotation) * w);
            if (m == 3) v = Quaternion.Inverse(joints[m].connectedBody.rotation) * w;
            if (m == 4) v = -(Quaternion.Inverse(joints[m].connectedBody.rotation) * w);
            joints[m].targetAngularVelocity = v;
        }
        if (clock > .6f && report == "")
        {
            for (int m = 0; m < 5; m++) report += "mode " + m + ": spin " + children[m].angularVelocity.magnitude.ToString("0.00") + " of " + spin0.magnitude.ToString("0.00") + " | ";
        }
    }
}
