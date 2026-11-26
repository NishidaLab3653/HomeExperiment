using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// シーンマネージャー(HomeSceneManager.unity)からの各シーンへの切り替えと、切り替えた先のシーンに重ねる案内。
//
// シーンマネージャーから別のシーンへ移ると、画面の右上に
//   - いまのシーンでやること(手順)
//   - 「メニューに戻る」ボタン(記録中・試し撮り中は押せない)
// を出す(HomeSceneFlowOverlay。シーンをまたいで残る)。シーンを直接再生したときは出さない(今までどおり)。
public static class HomeSceneFlow
{
    public const string ManagerScene = "Assets/0Scenes/HomeSceneManager.unity";
    public const string SetupScene = "Assets/0Scenes/1Measurement/0HomeSetup.unity";
    public const string RecordingScene = "Assets/0Scenes/1Measurement/1HeadDirRecording.unity";
    public const string RecordingMultiScene = "Assets/0Scenes/1Measurement/1HeadDirRecordingMulti.unity";

    public class SceneInfo
    {
        public string path;
        public string title;
        public string[] steps;

        public SceneInfo(string path, string title, params string[] steps)
        {
            this.path = path;
            this.title = title;
            this.steps = steps;
        }
    }

    public static readonly SceneInfo Setup = new SceneInfo(SetupScene, "キャリブレーション(部屋とキネクトの位置合わせ)",
        "各キネクトの点群が表示されるまで待つ(基準キネクトは保存済みの点群を使うこともある)",
        "点群が部屋に重なるよう、画面のボタンで位置合わせ(ICP)する",
        "合ったら Space キーで保存する(キネクトの位置・点群。制御PCなら部屋も)",
        "続けて Space キーで試し撮り → もう一度 Space で終了し、フレームの抜けを確かめる(任意)",
        "終わったら「メニューに戻る」");

    public static readonly SceneInfo Recording = new SceneInfo(RecordingScene, "記録(このPCのキネクトだけ)",
        "左上の表示でキネクトが「準備完了」になるまで待つ",
        "Space キーで記録開始、もう一度 Space キーで終了して保存する",
        "終わったら「メニューに戻る」(記録中は戻れない)");

    public static readonly SceneInfo RecordingMulti = new SceneInfo(RecordingMultiScene, "記録(複数のPCで同時に)",
        "全PCで、左上の表示でキネクトが「準備完了」になるまで待つ",
        "Master のキネクトをつないだPCで Space キーを押すと、全PCの記録が始まる",
        "Master のPCでもう一度 Space キーで終了(他のPCは数秒後に自動で終了・保存)",
        "終わったら「メニューに戻る」(記録中は戻れない)");

    public static readonly List<SceneInfo> AnalysisScenes = new List<SceneInfo>
    {
        new SceneInfo("Assets/0Scenes/2Analysis/2HeadDirViewer.unity", "骨格の再生", "インスペクターで実験・実験対象者を選んで確認する"),
        new SceneInfo("Assets/0Scenes/2Analysis/3HeadDirFilter.unity", "骨格の統合(フィルタ)", "インスペクターで実験・実験対象者を選んで実行する"),
        new SceneInfo("Assets/0Scenes/2Analysis/4GazeAnalyze.unity", "視線の解析", "インスペクターで実験・実験対象者・骨格を選んで実行する"),
        new SceneInfo("Assets/0Scenes/2Analysis/5GazeExport.unity", "解析結果の書き出し", "インスペクターで実験・実験対象者・骨格を選んで実行する"),
        new SceneInfo("Assets/0Scenes/2Analysis/6ArrivalCheck.unity", "到達の確認", "インスペクターで実験・実験対象者を選んで確認する"),
    };

    public static void Load(SceneInfo scene)
    {
        HomeSceneFlowOverlay.Show(scene);
        LoadPath(scene.path);
    }

    public static void ReturnToMenu()
    {
        HomeSceneFlowOverlay.Hide();
        LoadPath(ManagerScene);
    }

    private static void LoadPath(string path)
    {
#if UNITY_EDITOR
        // エディタでは Build Settings に入っていなくても読み込めるよう、パスで読み込む
        UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Single));
#else
        SceneManager.LoadScene(System.IO.Path.GetFileNameWithoutExtension(path));
#endif
    }
}

// 切り替えた先のシーンの右上に、やることと「メニューに戻る」を出す
public class HomeSceneFlowOverlay : MonoBehaviour
{
    private static HomeSceneFlowOverlay instance;
    private HomeSceneFlow.SceneInfo scene;
    private bool collapsed;
    private GUIStyle boxStyle;
    private GUIStyle titleStyle;
    private GUIStyle textStyle;

    public static void Show(HomeSceneFlow.SceneInfo scene)
    {
        if (instance == null)
        {
            GameObject go = new GameObject("HomeSceneFlowOverlay");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<HomeSceneFlowOverlay>();
        }

        instance.scene = scene;
        instance.collapsed = false;
    }

    public static void Hide()
    {
        if (instance != null)
            Destroy(instance.gameObject);

        instance = null;
    }

    // 記録中・試し撮り中はシーンを離れない(データを失わないため)
    private static string BusyReason()
    {
        foreach (HomeSkeletonRecordingController controller in FindObjectsOfType<HomeSkeletonRecordingController>())
        {
            if (controller.IsRecording)
                return "記録中は戻れません。Space キーで記録を終了してください。";
        }

        foreach (HomeTestRecording test in FindObjectsOfType<HomeTestRecording>())
        {
            if (test.IsRecording)
                return "試し撮り中は戻れません。Space キーで試し撮りを終了してください。";
        }

        return null;
    }

    private void OnGUI()
    {
        if (scene == null)
            return;

        if (boxStyle == null)
        {
            boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(12, 12, 10, 10) };
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, wordWrap = true };
            textStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
        }

        const float width = 380f;
        GUILayout.BeginArea(new Rect(Screen.width - width - 10, 10, width, Screen.height - 20));
        GUILayout.BeginVertical(boxStyle);

        using (new GUILayout.HorizontalScope())
        {
            GUILayout.Label(scene.title, titleStyle);

            if (GUILayout.Button(collapsed ? "▼" : "▲", GUILayout.Width(28)))
                collapsed = !collapsed;
        }

        if (!collapsed)
        {
            for (int i = 0; i < scene.steps.Length; i++)
                GUILayout.Label($"{i + 1}. {scene.steps[i]}", textStyle);

            HomeSessionData session = HomeSession.Current;
            GUILayout.Space(4);
            GUILayout.Label($"実験: {Show(session.experimentName)}   実験対象者: {Show(session.subjectName)}", textStyle);
        }

        string busy = BusyReason();
        GUI.enabled = busy == null;

        if (GUILayout.Button("メニューに戻る", GUILayout.Height(32)))
            HomeSceneFlow.ReturnToMenu();

        GUI.enabled = true;

        if (busy != null && !collapsed)
            GUILayout.Label(busy, textStyle);

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "(未設定)" : value;
}
