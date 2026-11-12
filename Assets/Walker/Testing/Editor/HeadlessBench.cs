using System;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Runs SkillBench with nobody at the editor, for scripts, sweeps and CI:
//   Unity.exe -batchmode -projectPath <project> -executeMethod HeadlessBench.Run
//             -benchLabel <label> -benchFilter "<case|case>" [-benchRepeats N] [-benchTimeScale S]
// Opens SampleScene, enters Play mode, runs the cases and writes BalanceResults/skills-<label>.json (SkillBench's
// report). In batch mode it then exits: 0 when every case passed, 1 when any failed, 2 on an error. The project
// must not be open in another editor. From an open editor, Start() runs the same way and leaves the editor open.
[InitializeOnLoad]
public static class HeadlessBench
{
    const string Key = "HeadlessBench.Request", Scene = "Assets/Scenes/SampleScene.unity";
    static SkillBench running;

    static HeadlessBench()
    {
        EditorApplication.playModeStateChanged += OnPlayMode;
        EditorApplication.update += Watch;
    }

    public static void Run()
    {
        string[] args = Environment.GetCommandLineArgs();
        string Arg(string name, string fallback)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }
        Start(Arg("-benchLabel", "headless"), Arg("-benchFilter", "walk 0.5 level"),
              int.Parse(Arg("-benchRepeats", "1"), CultureInfo.InvariantCulture),
              float.Parse(Arg("-benchTimeScale", "10"), CultureInfo.InvariantCulture));
    }

    public static void Start(string label, string filter, int repeats = 1, float timeScale = 10)
    {
        // Kept across the domain reload that entering Play mode may bring.
        SessionState.SetString(Key, string.Join("\n", label, filter, repeats.ToString(CultureInfo.InvariantCulture), timeScale.ToString(CultureInfo.InvariantCulture)));
        if (EditorSceneManager.GetActiveScene().path != Scene) EditorSceneManager.OpenScene(Scene);
        EditorApplication.EnterPlaymode();
    }

    static void OnPlayMode(PlayModeStateChange change)
    {
        if (change != PlayModeStateChange.EnteredPlayMode) return;
        string request = SessionState.GetString(Key, "");
        if (request == "") return;
        string[] p = request.Split('\n');
        var arena = UnityEngine.Object.FindAnyObjectByType<BalanceTestArena>();
        if (!arena) { Finish(2, "no BalanceTestArena in " + Scene); return; }
        var bench = arena.gameObject.AddComponent<SkillBench>();
        bench.repeats = int.Parse(p[2], CultureInfo.InvariantCulture);
        bench.timeScale = float.Parse(p[3], CultureInfo.InvariantCulture);
        bench.RunNamed(p[0], p[1]);
        running = bench;
    }

    static void Watch()
    {
        if (!running || !running.finished) return;
        var report = running.report;
        running = null;
        Finish(report.passed == report.total ? 0 : 1, "passed " + report.passed + " / " + report.total + ", report in BalanceResults/skills-" + report.label + ".json");
    }

    static void Finish(int code, string message)
    {
        SessionState.EraseString(Key);
        Debug.Log("HeadlessBench: " + message);
        if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }
}
