using UnityEngine;

// Test aid: renders the main camera every `every` seconds of game time, from beside the humanoid, into one
// contact sheet (`columns` wide, numbered left to right, top to bottom), saved as `file` once full or when
// Save() is called. For looking at a motion frame by frame.
public class Snapshots : MonoBehaviour
{
    public JointBalanceHumanoid humanoid;
    public string file = "Snapshots/sheet.png";
    public float every = .5f;
    public int count = 16, columns = 4, width = 320, height = 200;
    [Tooltip("Where the camera stands from the pelvis (m), and how high it looks.")]
    public Vector3 offset = new Vector3(2.2f, .5f, .4f);
    public float lookHeight = .4f;
    public int taken;
    float next;
    RenderTexture target;
    Texture2D sheet;

    // Game time advances a fixed step per rendered frame while recording, so the frames are evenly spaced
    // however slowly the editor renders.
    void Start() { next = Time.time; Time.captureDeltaTime = .02f; }
    void OnDisable() { Time.captureDeltaTime = 0; }

    void LateUpdate()
    {
        if (taken >= count || Time.time < next || !humanoid) return;
        next += every;
        var cam = Camera.main;
        var demo = FindAnyObjectByType<SkillsDemo>(); if (demo) demo.followCamera = false;
        Vector3 p = humanoid.pelvis.position; p.y = lookHeight;
        cam.transform.position = p + offset; cam.transform.LookAt(p);
        if (!target) target = new RenderTexture(width, height, 24);
        int rows = (count + columns - 1) / columns;
        if (!sheet) sheet = new Texture2D(width * columns, height * rows, TextureFormat.RGB24, false);
        var saved = cam.targetTexture;
        cam.targetTexture = target; cam.Render(); cam.targetTexture = saved;
        var was = RenderTexture.active; RenderTexture.active = target;
        int x = taken % columns, y = rows - 1 - taken / columns;
        sheet.ReadPixels(new Rect(0, 0, width, height), x * width, y * height);
        RenderTexture.active = was;
        taken++;
        if (taken == count) { Save(); Time.captureDeltaTime = 0; }
    }

    public void Save()
    {
        if (!sheet) return;
        sheet.Apply();
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
        System.IO.File.WriteAllBytes(file, sheet.EncodeToPNG());
    }
}
