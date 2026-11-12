using UnityEngine;

// A continuous rough floor: one mesh with smooth bumps of a few centimetres, built at runtime, like broken
// ground or a grassy field. The slab floor is the harder kind of rough, with a step at every slab edge.
// Show() puts it in place of the slabs (their colliders and renderers off), Hide() brings the slabs back.
public class BumpyFloor : MonoBehaviour
{
    [Tooltip("Peak height of the bumps above and below the mean (m).")]
    [Range(0,.15f)] public float amplitude = .04f;
    [Tooltip("Typical distance between bumps (m).")]
    [Range(.2f,3)] public float wavelength = 1f;
    [Tooltip("Within this distance of `flatAt` the floor stays level, so that the humanoid starts on flat ground (m).")]
    public float flatRadius = .45f;
    public Vector3 flatAt;
    public Vector2 xRange = new Vector2(-3, 3), zRange = new Vector2(-3, 12);
    [Range(.02f,.2f)] public float resolution = .05f;
    public int seed = 1;
    public Material material;
    MeshCollider meshCollider;
    MeshRenderer meshRenderer;
    Collider[] hidden;
    Renderer[] hiddenLooks;

    public float Height(float x, float z)
    {
        float k = 1 / wavelength, s = seed * 17.31f;
        // Two octaves of Perlin noise, scaled to about +-1. A finer one would make bumps smaller than the foot.
        float n = (Mathf.PerlinNoise(x * k + s, z * k - s) - .5f) * 2.4f
                + (Mathf.PerlinNoise(x * k * 2.1f - s, z * k * 2.1f + s * .5f) - .5f) * .5f;
        float d = new Vector2(x - flatAt.x, z - flatAt.z).magnitude;
        float fade = Mathf.SmoothStep(0, 1, (d - flatRadius) / .4f);
        return Mathf.Clamp(n * 1.1f, -1, 1) * amplitude * fade;
    }

    public void Build()
    {
        int nx = Mathf.CeilToInt((xRange.y - xRange.x) / resolution) + 1, nz = Mathf.CeilToInt((zRange.y - zRange.x) / resolution) + 1;
        var vertices = new Vector3[nx * nz];
        for (int i = 0; i < nx; i++) for (int j = 0; j < nz; j++)
        {
            float x = xRange.x + i * resolution, z = zRange.x + j * resolution;
            vertices[i * nz + j] = new Vector3(x, Height(x, z), z);
        }
        var triangles = new int[(nx - 1) * (nz - 1) * 6]; int t = 0;
        for (int i = 0; i < nx - 1; i++) for (int j = 0; j < nz - 1; j++)
        {
            int a = i * nz + j, b = a + 1, c = a + nz, d = c + 1;
            triangles[t++] = a; triangles[t++] = b; triangles[t++] = c;
            triangles[t++] = c; triangles[t++] = b; triangles[t++] = d;
        }
        var mesh = new Mesh { name = "Bumpy floor", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.vertices = vertices; mesh.triangles = triangles; mesh.RecalculateNormals(); mesh.RecalculateBounds();
        if (!meshCollider)
        {
            var go = new GameObject("Bumpy Floor Surface") { layer = 0 };
            go.transform.SetParent(transform, false);
            meshCollider = go.AddComponent<MeshCollider>();
            go.AddComponent<MeshFilter>();
            meshRenderer = go.AddComponent<MeshRenderer>();
        }
        meshCollider.sharedMesh = mesh;
        meshCollider.GetComponent<MeshFilter>().sharedMesh = mesh;
        if (material) meshRenderer.sharedMaterial = material;
        meshCollider.transform.position = Vector3.zero; meshCollider.transform.rotation = Quaternion.identity;
    }

    // Height range and slope of the built surface, for reports: "min..max m, slope p50/p95/max degrees".
    public string Describe()
    {
        if (!meshCollider || !meshCollider.sharedMesh) return "not built";
        var mesh = meshCollider.sharedMesh; var v = mesh.vertices; var n = mesh.normals;
        float lo = float.PositiveInfinity, hi = float.NegativeInfinity; var slopes = new System.Collections.Generic.List<float>();
        for (int i = 0; i < v.Length; i++)
        {
            if (Mathf.Abs(v[i].x) > 1.5f || v[i].z < -1 || v[i].z > 9) continue;
            lo = Mathf.Min(lo, v[i].y); hi = Mathf.Max(hi, v[i].y); slopes.Add(Vector3.Angle(n[i], Vector3.up));
        }
        slopes.Sort();
        return lo.ToString("0.000") + ".." + hi.ToString("0.000") + " m, slope " + slopes[slopes.Count / 2].ToString("0.0") + "/" + slopes[slopes.Count * 95 / 100].ToString("0.0") + "/" + slopes[slopes.Count - 1].ToString("0.0") + " deg";
    }

    public void Show(Rigidbody[] slabs)
    {
        Build();
        meshCollider.gameObject.SetActive(true);
        hidden = new Collider[slabs.Length]; hiddenLooks = new Renderer[slabs.Length];
        for (int i = 0; i < slabs.Length; i++)
        {
            hidden[i] = slabs[i].GetComponent<Collider>(); if (hidden[i]) hidden[i].enabled = false;
            hiddenLooks[i] = slabs[i].GetComponentInChildren<Renderer>(); if (hiddenLooks[i]) hiddenLooks[i].enabled = false;
        }
        Physics.SyncTransforms();
    }

    public void Hide()
    {
        if (meshCollider) meshCollider.gameObject.SetActive(false);
        if (hidden != null) foreach (var c in hidden) if (c) c.enabled = true;
        if (hiddenLooks != null) foreach (var r in hiddenLooks) if (r) r.enabled = true;
        hidden = null; hiddenLooks = null;
        Physics.SyncTransforms();
    }
}
