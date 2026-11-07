using UnityEngine;

public class BalanceProjectile : MonoBehaviour
{
    public BalanceTestArena arena;
    bool recorded, spent;
    void OnCollisionEnter(Collision collision)
    {
        if(!arena) return;
        // A ball that has hit something is spent. Rolling about the floor it would otherwise get under
        // the feet, which is a test of something else: the step planner cannot see it.
        if(!spent && arena.spentBallsClearLegs)
        {
            spent=true;
            var mine=GetComponent<Collider>();
            foreach(var body in arena.humanoid.bodies)
                if(body.name.Contains("Foot") || body.name.Contains("Shin")) Physics.IgnoreCollision(mine,body.GetComponent<Collider>());
        }
        if(!collision.rigidbody) return;
        if(collision.rigidbody.GetComponentInParent<JointBalanceHumanoid>()!=arena.humanoid) return;
        if(collision.rigidbody.name.Contains("Foot")) arena.ballsUnderfoot++;
        if(recorded) return;
        recorded=true;
        arena.RegisterImpact(collision.impulse.magnitude);
    }
}
