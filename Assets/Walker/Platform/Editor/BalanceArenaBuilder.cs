using UnityEditor;
using UnityEngine;

public static class BalanceArenaBuilder
{
    // Floor extent in slabs, 0.8 m each: 20 by 21 slabs, about 16 by 17 m.
    const int FirstColumn=-7, LastColumn=12, FirstRow=-8, LastRow=12;

    // Adds whatever slabs are missing. The central 6 by 5 keep their places at the start of the list,
    // because a slab's phase of motion comes from its index: the ground under the start position
    // moves exactly as it did on the smaller floor.
    static void AddSlabs(BalanceTestArena arena, Material blue, Material teal)
    {
        var slabs=new System.Collections.Generic.List<Rigidbody>(arena.platforms);
        void Add(int x,int z)
        {
            string name="Moving Slab "+x+"-"+z;
            if(slabs.Exists(s=>s.name==name)) return;
            var tile=GameObject.CreatePrimitive(PrimitiveType.Cube); tile.name=name;
            Undo.RegisterCreatedObjectUndo(tile,"Expand balance floor");
            tile.transform.SetParent(arena.transform); tile.transform.position=new Vector3((x-2.5f)*.8f,-.15f,(z-2)*.8f);
            tile.transform.localScale=new Vector3(.788f,.3f,.788f);
            tile.GetComponent<Renderer>().sharedMaterial=(x+z+100)%2==0?blue:teal;
            var body=tile.AddComponent<Rigidbody>(); body.isKinematic=true; body.useGravity=false;
            body.interpolation=RigidbodyInterpolation.Interpolate; body.collisionDetectionMode=CollisionDetectionMode.ContinuousSpeculative;
            slabs.Add(body);
        }
        for(int x=0;x<6;x++) for(int z=0;z<5;z++) Add(x,z);
        for(int x=FirstColumn;x<=LastColumn;x++) for(int z=FirstRow;z<=LastRow;z++) Add(x,z);
        Undo.RecordObject(arena,"Expand balance floor");
        arena.platforms=slabs.ToArray();
        EditorUtility.SetDirty(arena);
    }

    // Enlarges the floor of an arena that already exists, and saves the scene.
    const string MaterialFolder="Assets/Walker/Platform/Materials/";

    public static string ExpandFloor()
    {
        var arena=Object.FindAnyObjectByType<BalanceTestArena>();
        if(!arena) throw new System.InvalidOperationException("Arena missing");
        var blue=AssetDatabase.LoadAssetAtPath<Material>(MaterialFolder+"PlatformBlue.mat");
        var teal=AssetDatabase.LoadAssetAtPath<Material>(MaterialFolder+"PlatformTeal.mat");
        int before=arena.platforms.Length;
        AddSlabs(arena,blue,teal);
        int stands=0;
        foreach(Transform child in arena.transform)
            if(child.name=="Stand" && child.TryGetComponent<Collider>(out var collider)) { Undo.DestroyObjectImmediate(collider); stands++; }
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(arena.gameObject.scene);
        return "slabs "+before+" -> "+arena.platforms.Length+", stand colliders removed "+stands;
    }

    public static void Build()
    {
        if(Object.FindAnyObjectByType<BalanceTestArena>()) throw new System.InvalidOperationException("Arena already exists");
        var h=Object.FindAnyObjectByType<JointBalanceHumanoid>();
        if(!h) throw new System.InvalidOperationException("Humanoid missing");
        Material Material(string name,Color color)
        {
            string path=MaterialFolder+name+".mat";
            var mat=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(!mat) { mat=new Material(Shader.Find("Universal Render Pipeline/Lit")); mat.color=color; AssetDatabase.CreateAsset(mat,path); }
            return mat;
        }
        var blue=Material("PlatformBlue",new Color(.12f,.30f,.38f));
        var teal=Material("PlatformTeal",new Color(.17f,.44f,.47f));
        var orange=Material("ProjectileOrange",new Color(1,.27f,.055f));
        var metal=Material("LauncherMetal",new Color(.12f,.14f,.18f));
        var root=new GameObject("Balance Test Arena"); Undo.RegisterCreatedObjectUndo(root,"Create balance arena");
        var arena=root.AddComponent<BalanceTestArena>(); arena.humanoid=h; arena.projectileMaterial=orange;
        var slabs=new System.Collections.Generic.List<Rigidbody>();
        arena.platforms=new Rigidbody[0];
        AddSlabs(arena,blue,teal);
        arena.launchers=new Transform[4];
        var positions=new[]{new Vector3(0,1.25f,-3),new Vector3(3,1.25f,0),new Vector3(0,1.25f,3),new Vector3(-3,1.25f,0)};
        for(int i=0;i<4;i++)
        {
            var muzzle=new GameObject("Launcher "+(i+1)); muzzle.transform.SetParent(root.transform); muzzle.transform.position=positions[i];
            muzzle.transform.LookAt(new Vector3(0,1.25f,0)); arena.launchers[i]=muzzle.transform;
            var barrel=GameObject.CreatePrimitive(PrimitiveType.Cylinder); barrel.name="Barrel"; barrel.transform.SetParent(muzzle.transform,false);
            barrel.transform.localPosition=new Vector3(0,0,-.3f); barrel.transform.localRotation=Quaternion.Euler(90,0,0); barrel.transform.localScale=new Vector3(.22f,.3f,.22f);
            Object.DestroyImmediate(barrel.GetComponent<Collider>()); barrel.GetComponent<Renderer>().sharedMaterial=metal;
            var stand=GameObject.CreatePrimitive(PrimitiveType.Cube); stand.name="Stand"; stand.transform.SetParent(root.transform);
            stand.transform.position=positions[i]+new Vector3(0,-.8f,0); stand.transform.localScale=new Vector3(.28f,.9f,.28f);
            stand.GetComponent<Renderer>().sharedMaterial=metal;
            // The floor runs on past the launchers, so the stands are only there to be seen.
            Object.DestroyImmediate(stand.GetComponent<Collider>());
        }
        var ground=GameObject.Find("Balance Ground"); Undo.RecordObject(ground.transform,"Lower catch floor"); ground.transform.position=new Vector3(0,-1.5f,0);
        var camera=Camera.main; Undo.RecordObject(camera.transform,"Frame balance arena");
        camera.transform.position=new Vector3(4.8f,3.2f,-6.5f); camera.transform.LookAt(new Vector3(-.4f,.65f,0));
        camera.fieldOfView=48;
        Selection.activeGameObject=root;
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(h.gameObject.scene);
        AssetDatabase.SaveAssets();
    }
}
