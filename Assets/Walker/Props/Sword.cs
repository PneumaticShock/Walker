using UnityEngine;

// A rapier made of primitives, as Thibault would have used: by his rule the blade, cross to point, is half
// the fencer's stature, about 0.9 m. One rigid body of 1.2 kg balanced a little past the guard, as the heavy
// hilt of a rapier puts it. Its frame: +y from pommel to point, the quillons along x, the flat facing z,
// the middle of the grip at the origin.
public class Sword : MonoBehaviour
{
    public const float GripLength = .12f, GuardAt = .075f, Blade = .9f;
    public static float TipAt => GuardAt + .01f + Blade;
    public Rigidbody body;
    public Vector3 Grip => transform.position;
    public Vector3 Tip => transform.TransformPoint(new Vector3(0, TipAt, 0));
    public Vector3 Along => transform.up;

    public static Sword Build(Vector3 grip, Quaternion rotation, Material steel = null, Material leather = null)
    {
        var go = new GameObject("Rapier");
        go.transform.SetPositionAndRotation(grip, rotation);
        var sword = go.AddComponent<Sword>();
        void Part(string name, PrimitiveType type, Vector3 at, Vector3 size, Material look)
        {
            var p = GameObject.CreatePrimitive(type);
            p.name = name; p.transform.SetParent(go.transform, false);
            p.transform.localPosition = at; p.transform.localScale = size;
            if (look) p.GetComponent<Renderer>().sharedMaterial = look;
            // Primitive colliders stay, sized by the scale; the sphere and capsule fit their shapes.
        }
        Part("Blade", PrimitiveType.Cube, new Vector3(0, GuardAt + .01f + Blade * .5f, 0), new Vector3(.022f, Blade, .006f), steel);
        Part("Quillons", PrimitiveType.Cube, new Vector3(0, GuardAt, 0), new Vector3(.26f, .014f, .014f), steel);
        Part("Grip", PrimitiveType.Cube, Vector3.zero, new Vector3(.026f, GripLength, .026f), leather);
        Part("Pommel", PrimitiveType.Sphere, new Vector3(0, -GripLength * .5f - .02f, 0), Vector3.one * .045f, steel);
        var tip = new GameObject("Tip").transform; tip.SetParent(go.transform, false); tip.localPosition = new Vector3(0, TipAt, 0);
        var body = go.AddComponent<Rigidbody>();
        body.mass = 1.2f;
        body.automaticCenterOfMass = false; body.automaticInertiaTensor = false;
        body.centerOfMass = new Vector3(0, GuardAt + .07f, 0);
        // About 0.07 kg m^2 across the blade at the balance point, little about its length.
        body.inertiaTensor = new Vector3(.07f, .0008f, .07f); body.inertiaTensorRotation = Quaternion.identity;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.linearDamping = 0; body.angularDamping = .05f;
        sword.body = body;
        // New colliders sit where they were made, at the world's origin, until physics catches up.
        Physics.SyncTransforms();
        return sword;
    }
}

// A rack that holds a sword point down by its quillons on two prongs, open towards whoever takes it: drawn
// back out of the prongs, the sword comes free.
public static class SwordRack
{
    public static GameObject Build(Vector3 grip, Quaternion facing, Material look = null)
    {
        // The sword hangs point down, so its guard is below the grip.
        Vector3 guard = grip + Vector3.down * Sword.GuardAt;
        var rack = new GameObject("Sword Rack");
        rack.transform.SetPositionAndRotation(new Vector3(guard.x, 0, guard.z), facing);
        void Piece(string name, Vector3 local, Vector3 size)
        {
            var p = GameObject.CreatePrimitive(PrimitiveType.Cube);
            p.name = name; p.transform.SetParent(rack.transform, false);
            p.transform.localPosition = local; p.transform.localScale = size;
            if (look) p.GetComponent<Renderer>().sharedMaterial = look;
        }
        float top = guard.y - .007f - .01f;
        // Two prongs either side of the blade, along the way it is drawn out: local -z is towards the taker.
        // Each stands on its own post, out to the side: once drawn, the sword is raised point forward, and its
        // blade sweeps the whole upright plane in front of the hand, so nothing may cross it.
        Piece("Prong L", new Vector3(-.035f, top, .03f), new Vector3(.02f, .02f, .14f));
        Piece("Prong R", new Vector3(.035f, top, .03f), new Vector3(.02f, .02f, .14f));
        Piece("Arm L", new Vector3(-.07f, top, .09f), new Vector3(.07f, .02f, .02f));
        Piece("Arm R", new Vector3(.07f, top, .09f), new Vector3(.07f, .02f, .02f));
        Piece("Post L", new Vector3(-.11f, top * .5f, .09f), new Vector3(.03f, top, .03f));
        Piece("Post R", new Vector3(.11f, top * .5f, .09f), new Vector3(.03f, top, .03f));
        Piece("Foot L", new Vector3(-.11f, .01f, .09f), new Vector3(.08f, .02f, .2f));
        Piece("Foot R", new Vector3(.11f, .01f, .09f), new Vector3(.08f, .02f, .2f));
        Physics.SyncTransforms();
        return rack;
    }
}
