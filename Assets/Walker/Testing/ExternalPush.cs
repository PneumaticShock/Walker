using UnityEngine;

// Explicit external disturbance source, separate from the balance controller.
public class ExternalPush : MonoBehaviour
{
    public JointBalanceHumanoid humanoid;
    public Vector3 constantForce;
    public Vector3 impulse = new Vector3(10,0,0);
    void FixedUpdate() { if(humanoid && constantForce != Vector3.zero) humanoid.pelvis.AddForce(constantForce, ForceMode.Force); }
    [ContextMenu("Apply impulse (Play mode)")]
    public void Push() { if(Application.isPlaying && humanoid) humanoid.pelvis.AddForce(impulse, ForceMode.Impulse); }
}
