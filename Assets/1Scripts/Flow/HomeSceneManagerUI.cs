using System;
using System.Collections.Generic;
using UnityEngine;

// シーンマネージャー(Assets/0Scenes/HomeSceneManager.unity)の画面。再生すると、画面の案内に沿って
//   1. このPCの設定(役割・データの場所)
//   2. 実験(既存の実験を選ぶ / 新しく作る。既存の実験から部屋・位置・点群を引き継げる)
//   3. キネクト(このPCにつないでいるキネクトを調べて選ぶ)
//   4. キャリブレーション(基準の点群を選んで、位置合わせのシーンへ)
//   5. 記録(実験対象者を決めて、記録のシーンへ)
//   6. 分析(分析のシーンを開く)
//   7. 同期の状態
// を進められる。設定は PC ごとのファイル(UserSettings/)に保存し、計測のシーンはインスペクターの代わりにこの値を使う。
// メニューの HomeExperiment > シーンマネージャーで始める(Ctrl+Shift+H)で、このシーンを開いて再生できる。
public class HomeSceneManagerUI : MonoBehaviour
{
    private enum Page
    {
        Home,
        Pc,
        Experiment,
        Kinects,
        Calibration,
        Recording,
        Analysis,
        Sync
    }

    private const float VirtualHeight = 900f;

    private static readonly string[] SyncModeLabels = { "シーンのまま", "単独", "Master", "Subordinate" };

    private Page page = Page.Home;
    private Vector2 scroll;
    private string message = "";
    private bool messageIsError;

    // 実験
    private int experimentTab;
    private string newExperimentName = "";
    private int copyFromIndex;
    private bool copyRoom = true;
    private bool copyPoses = true;
    private bool copyPointClouds = true;
    private List<HomeExperimentCatalog.ExperimentSummary> experiments = new List<HomeExperimentCatalog.ExperimentSummary>();
    private float nextRefresh;

    // 入力中の値
    private string pcNameInput;
    private string dataRootInput;
    private string addressInput;
    private string subjectInput;

    // キネクト
    private List<HomeKinectDetector.Device> detected;
    private string detectError;

    private GUIStyle titleStyle;
    private GUIStyle headStyle;
    private GUIStyle textStyle;
    private GUIStyle smallStyle;
    private GUIStyle cardStyle;
    private GUIStyle cardTitleStyle;
    private GUIStyle bigButton;
    private GUIStyle buttonStyle;
    private GUIStyle fieldStyle;
    private GUIStyle boxStyle;
    private GUIStyle toggleStyle;

    private void Start()
    {
        HomeSyncSettings settings = HomeSyncSettings.Current;
        pcNameInput = settings.pcName;
        dataRootInput = settings.dataRootDirectory;
        addressInput = settings.controlAddress;
        subjectInput = HomeSession.Current.subjectName;
        RefreshExperiments();
    }

    private void Update()
    {
        if (Time.unscaledTime >= nextRefresh)
        {
            nextRefresh = Time.unscaledTime + 1.5f;
            RefreshExperiments();
        }
    }

    private void RefreshExperiments()
    {
        experiments = HomeExperimentCatalog.List(HomeSyncSettings.Current.dataRootDirectory);
    }

    private HomeExperimentCatalog.ExperimentSummary CurrentExperiment =>
        experiments.Find(e => e.name == HomeSession.Current.experimentName);

    // ------------------------------------------------------------
    // 画面全体
    // ------------------------------------------------------------

    private void InitStyles()
    {
        if (titleStyle != null)
            return;

        titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold };
        headStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, wordWrap = true };
        textStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, wordWrap = true, richText = true };
        smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true, richText = true };
        cardStyle = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.UpperLeft, wordWrap = true, richText = true, padding = new RectOffset(16, 16, 12, 12) };
        cardTitleStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
        bigButton = new GUIStyle(GUI.skin.button) { fontSize = 19, fontStyle = FontStyle.Bold, fixedHeight = 52, wordWrap = true };
        buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 16, fixedHeight = 36, wordWrap = true, richText = true };
        fieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 18, fixedHeight = 34, alignment = TextAnchor.MiddleLeft };
        boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(16, 16, 12, 12), alignment = TextAnchor.UpperLeft };
        toggleStyle = new GUIStyle(GUI.skin.toggle) { fontSize = 17, wordWrap = true, richText = true };
    }

    private void OnGUI()
    {
        InitStyles();

        // 画面の大きさに合わせて全体を拡大・縮小する(高さ 900 を基準)
        float scale = Mathf.Clamp(Screen.height / VirtualHeight, 0.6f, 2f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        float width = Screen.width / scale;
        float height = Screen.height / scale;
        float contentWidth = Mathf.Min(1000f, width - 40f);

        GUILayout.BeginArea(new Rect((width - contentWidth) / 2f, 16f, contentWidth, height - 32f));
        DrawHeader();
        scroll = GUILayout.BeginScrollView(scroll);

        switch (page)
        {
            case Page.Home: DrawHome(); break;
            case Page.Pc: DrawPc(); break;
            case Page.Experiment: DrawExperiment(); break;
            case Page.Kinects: DrawKinects(); break;
            case Page.Calibration: DrawCalibration(); break;
            case Page.Recording: DrawRecording(); break;
            case Page.Analysis: DrawAnalysis(); break;
            case Page.Sync: DrawSync(); break;
        }

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawHeader()
    {
        HomeSyncSettings settings = HomeSyncSettings.Current;
        HomeSessionData session = HomeSession.Current;

        using (new GUILayout.HorizontalScope())
        {
            GUILayout.Label("家庭実験 シーンマネージャー", titleStyle);
            GUILayout.FlexibleSpace();

            if (page != Page.Home && GUILayout.Button("メニューへ", buttonStyle, GUILayout.Width(140)))
                Go(Page.Home);
        }

        GUILayout.Label($"{settings.DisplayName}: <b>{RoleName(settings.role)}</b>{ConnectionText(settings)}　　" +
                        $"実験: <b>{Show(session.experimentName)}</b>　実験対象者: <b>{Show(session.subjectName)}</b>", smallStyle);

        if (!string.IsNullOrEmpty(message))
        {
            Color previous = GUI.color;
            GUI.color = messageIsError ? new Color(1f, 0.7f, 0.7f) : new Color(0.75f, 1f, 0.8f);
            GUILayout.Label(message, boxStyle);
            GUI.color = previous;
        }

        if (!string.IsNullOrEmpty(HomeSyncStatus.Error))
            GUILayout.Label($"<color=#ff8080>{HomeSyncStatus.Error}</color>", smallStyle);

        GUILayout.Space(8);
    }

    private void Go(Page next)
    {
        page = next;
        scroll = Vector2.zero;
        message = "";
        GUI.FocusControl(null);
    }

    private void Notify(string text, bool isError = false)
    {
        message = text;
        messageIsError = isError;
        HomeSyncStatus.AddLog(text);
        Debug.Log($"[HomeSceneManager] {text}");
    }

    private void Heading(string title, string instruction)
    {
        GUILayout.Label(title, headStyle);
        GUILayout.Label(instruction, textStyle);
        GUILayout.Space(10);
    }

    private static string RoleName(HomeSyncRole role) =>
        role == HomeSyncRole.Control ? "制御PC" :
        role == HomeSyncRole.Recorder ? "録画PC" :
        role == HomeSyncRole.Single ? "このPCだけ" : "未設定";

    private static string ConnectionText(HomeSyncSettings settings)
    {
        if (settings.role == HomeSyncRole.Control)
            return $"(録画PC {HomeSyncStatus.Clients.FindAll(c => c.IsAlive).Count}台と接続中)";

        if (settings.role == HomeSyncRole.Recorder)
            return HomeSyncStatus.Connected ? "(制御PCと接続中)" : "(制御PCにつながっていません)";

        return "";
    }

    private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "未設定" : value;

    private static void SaveSettings(HomeSyncSettings edited) => HomeSyncSettings.Save(edited);

    // ------------------------------------------------------------
    // メニュー
    // ------------------------------------------------------------

    private void DrawHome()
    {
        HomeSyncSettings settings = HomeSyncSettings.Current;
        Page next = RecommendedPage(out string nextText);

        GUILayout.Label($"次にやること: <b>{nextText}</b>", textStyle);
        GUILayout.Space(8);

        Card(Page.Pc, "1. このPCの設定", PcSummary(settings), next);
        Card(Page.Experiment, "2. 実験", ExperimentSummaryText(), next);
        Card(Page.Kinects, "3. キネクト", KinectSummary(settings), next);
        Card(Page.Calibration, "4. キャリブレーション", CalibrationSummary(settings), next);
        Card(Page.Recording, "5. 記録", RecordingSummary(), next);
        Card(Page.Analysis, "分析", "記録したデータを再生・解析するシーンを開く", next);

        if (settings.UsesNetwork)
            Card(Page.Sync, "同期の状態", SyncSummary(settings), next);
    }

    private void Card(Page target, string title, string summary, Page recommended)
    {
        Color previous = GUI.backgroundColor;

        if (target == recommended)
            GUI.backgroundColor = new Color(0.55f, 0.85f, 1f);

        string mark = target == recommended ? "　← 次はここ" : "";

        if (GUILayout.Button($"<size=20><b>{title}</b></size>{mark}\n<size=15>{summary}</size>", cardStyle, GUILayout.MinHeight(70)))
            Go(target);

        GUI.backgroundColor = previous;
        GUILayout.Space(4);
    }

    // いまの状態から、次に開くとよいページ
    private Page RecommendedPage(out string text)
    {
        HomeSyncSettings settings = HomeSyncSettings.Current;
        HomeSessionData session = HomeSession.Current;

        if (settings.role == HomeSyncRole.Off)
        {
            text = "このPCの役割を決める";
            return Page.Pc;
        }

        if (!HomeExperimentPaths.IsValidFolderName(session.experimentName, out _))
        {
            text = settings.CanEditSession ? "実験を選ぶか、新しく作る" : "制御PCで実験が選ばれるのを待つ";
            return settings.CanEditSession ? Page.Experiment : (settings.UsesNetwork ? Page.Sync : Page.Experiment);
        }

        if (UsedKinects(settings).Count == 0)
        {
            text = "このPCで使うキネクトを選ぶ";
            return Page.Kinects;
        }

        List<string> missing = MissingPoses(settings);

        if (missing.Count > 0)
        {
            text = $"キャリブレーション(Kinect {string.Join(", ", missing)} の位置合わせがまだ)";
            return Page.Calibration;
        }

        text = HomeExperimentPaths.IsValidFolderName(session.subjectName, out _)
            ? "記録する"
            : settings.CanEditSession ? "実験対象者を決めて記録する" : "制御PCで実験対象者が決まるのを待つ";
        return Page.Recording;
    }

    private static List<HomeKinectDeviceIndex> UsedKinects(HomeSyncSettings settings) =>
        new List<HomeKinectDeviceIndex>(Array.FindAll(settings.kinects ?? new HomeKinectDeviceIndex[0], k => k.use));

    private List<string> MissingPoses(HomeSyncSettings settings)
    {
        List<string> missing = new List<string>();
        HomeExperimentCatalog.ExperimentSummary experiment = CurrentExperiment;

        foreach (HomeKinectDeviceIndex kinect in UsedKinects(settings))
        {
            HomeExperimentCatalog.KinectEnv env = experiment?.Kinect(kinect.kinectId);

            if (env == null || !env.hasPose)
                missing.Add(kinect.kinectId);
        }

        return missing;
    }

    private static string PcSummary(HomeSyncSettings settings) =>
        settings.role == HomeSyncRole.Off
            ? "役割がまだ決まっていません"
            : $"{RoleName(settings.role)} / データの場所: {HomeExperimentPaths.ResolveRootDirectory(settings.dataRootDirectory)}";

    private string ExperimentSummaryText()
    {
        HomeExperimentCatalog.ExperimentSummary experiment = CurrentExperiment;
        string name = HomeSession.Current.experimentName;
        return string.IsNullOrEmpty(name) ? "実験がまだ選ばれていません" : $"{name}: {experiment?.Describe() ?? "このPCにまだ Env が届いていません"}";
    }

    private static string KinectSummary(HomeSyncSettings settings)
    {
        List<HomeKinectDeviceIndex> used = UsedKinects(settings);

        if (used.Count == 0)
            return "使うキネクトがまだ選ばれていません";

        return "このPCで使う: " + string.Join(", ", used.ConvertAll(k =>
            $"Kinect {k.kinectId}(デバイス {k.deviceIndex}{(k.syncMode == HomeKinectDeviceIndex.SyncMode.KeepComponent ? "" : " / " + SyncModeLabels[(int)k.syncMode])})"));
    }

    private string CalibrationSummary(HomeSyncSettings settings)
    {
        List<string> missing = MissingPoses(settings);
        string reference = $"基準: Kinect {settings.referenceKinectId}";
        return missing.Count == 0 ? $"このPCのキネクトは位置合わせ済み / {reference}" : $"未完了: Kinect {string.Join(", ", missing)} / {reference}";
    }

    private static string RecordingSummary()
    {
        string subject = HomeSession.Current.subjectName;
        return string.IsNullOrEmpty(subject) ? "実験対象者がまだ決まっていません" : $"実験対象者: {subject}";
    }

    private static string SyncSummary(HomeSyncSettings settings)
    {
        if (settings.role == HomeSyncRole.Control)
        {
            List<HomeSyncStatus.ClientInfo> alive = HomeSyncStatus.Clients.FindAll(c => c.IsAlive);
            return alive.Count == 0 ? "録画PCはまだつながっていません" : "接続中: " + string.Join(", ", alive.ConvertAll(c => c.name));
        }

        return HomeSyncStatus.Connected ? $"制御PC {HomeSyncStatus.ServerDescription} と接続中" : HomeSyncStatus.Status;
    }

    // ------------------------------------------------------------
    // 1. このPCの設定
    // ------------------------------------------------------------

    private void DrawPc()
    {
        HomeSyncSettings settings = HomeSyncSettings.Current;
        Heading("1. このPCの設定", "このPCの役割を選んでください。全PCで実験の名前や部屋をそろえるときは、1台を「制御PC」、残りを「録画PC」にします。");

        HomeSyncRole[] roles = { HomeSyncRole.Control, HomeSyncRole.Recorder, HomeSyncRole.Single };
        string[] titles = { "制御PC", "録画PC", "このPCだけ" };
        string[] descriptions =
        {
            "実験・実験対象者を決めて、部屋やキネクトの位置を全PCに配る(1台だけ)。キネクトをつないで記録してもよい。",
            "制御PCから実験・実験対象者・部屋を受け取って記録する。自分のキネクトの位置合わせの結果は制御PCへ送る。",
            "ネットワークを使わず、このPC1台で準備から記録まで行う。"
        };

        for (int i = 0; i < roles.Length; i++)
        {
            bool selected = settings.role == roles[i];
            Color previous = GUI.backgroundColor;

            if (selected)
                GUI.backgroundColor = new Color(0.55f, 0.85f, 1f);

            if (GUILayout.Button($"<size=20><b>{(selected ? "● " : "")}{titles[i]}</b></size>\n<size=15>{descriptions[i]}</size>", cardStyle, GUILayout.MinHeight(64)) && !selected)
            {
                HomeSyncSettings edited = settings.Clone();
                edited.role = roles[i];
                SaveSettings(edited);
                Notify($"このPCを「{titles[i]}」にしました。");
            }

            GUI.backgroundColor = previous;
        }

        if (settings.role == HomeSyncRole.Off)
            return;

        if (GUILayout.Button("この画面の設定を使わない(シーンのインスペクターの設定に戻す)", buttonStyle))
        {
            HomeSyncSettings edited = settings.Clone();
            edited.role = HomeSyncRole.Off;
            SaveSettings(edited);
            Notify("この画面の設定を使わないようにしました。計測のシーンはインスペクターの設定を使います。");
            return;
        }

        GUILayout.Space(12);
        GUILayout.Label("このPCの名前(空ならコンピューター名。制御PCの一覧に出ます)", textStyle);
        pcNameInput = GUILayout.TextField(pcNameInput ?? "", fieldStyle);

        GUILayout.Label("データの場所(空なら Assets/Data/HomeExperiment。例: D:\\HomeExperiment)", textStyle);

        using (new GUILayout.HorizontalScope())
        {
            dataRootInput = GUILayout.TextField(dataRootInput ?? "", fieldStyle);
#if UNITY_EDITOR
            if (GUILayout.Button("選ぶ", buttonStyle, GUILayout.Width(80)))
            {
                string picked = UnityEditor.EditorUtility.OpenFolderPanel("データの場所", HomeExperimentPaths.ResolveRootDirectory(dataRootInput), "");

                if (!string.IsNullOrEmpty(picked))
                    dataRootInput = picked.Replace('/', '\\');
            }
#endif
        }

        GUILayout.Label($"→ {HomeExperimentPaths.ResolveRootDirectory(dataRootInput)}", smallStyle);

        if (settings.role == HomeSyncRole.Recorder)
        {
            GUILayout.Label("制御PCのアドレス(空なら同じネットワークの中を自動で探します。見つからないときだけ制御PCのIPアドレスを入れます)", textStyle);
            addressInput = GUILayout.TextField(addressInput ?? "", fieldStyle);
        }

        if (settings.role == HomeSyncRole.Control)
            GUILayout.Label($"このPCのアドレス: {string.Join(", ", HomeSyncProtocol.GetLocalAddresses())}", smallStyle);

        bool changed = (pcNameInput ?? "") != settings.pcName || (dataRootInput ?? "") != settings.dataRootDirectory ||
                       (settings.role == HomeSyncRole.Recorder && (addressInput ?? "") != settings.controlAddress);
        bool rootValid = HomeExperimentPaths.IsValidRootDirectory(dataRootInput, out string rootError);

        if (!rootValid)
            GUILayout.Label($"<color=#ff8080>{rootError}</color>", smallStyle);

        GUILayout.Space(8);
        GUI.enabled = changed && rootValid;

        if (GUILayout.Button(changed ? "保存する" : "保存済み", bigButton))
        {
            HomeSyncSettings edited = settings.Clone();
            edited.pcName = (pcNameInput ?? "").Trim();
            edited.dataRootDirectory = (dataRootInput ?? "").Trim();
            edited.controlAddress = (addressInput ?? "").Trim();
            SaveSettings(edited);
            RefreshExperiments();
            Notify("このPCの設定を保存しました。");
        }

        GUI.enabled = true;
        NextButton(Page.Experiment, "次へ: 実験");
    }

    private void NextButton(Page target, string label)
    {
        GUILayout.Space(16);

        if (GUILayout.Button(label + " →", bigButton))
            Go(target);
    }

    // ------------------------------------------------------------
    // 2. 実験
    // ------------------------------------------------------------

    private void DrawExperiment()
    {
        HomeSyncSettings settings = HomeSyncSettings.Current;
        HomeSessionData session = HomeSession.Current;

        if (!settings.CanEditSession)
        {
            Heading("2. 実験", "録画PCでは、実験は制御PCで選びます。制御PCで選ばれると、ここに表示され、部屋などのデータも届きます。");
            GUILayout.Label($"制御PCが選んだ実験: <b>{Show(session.experimentName)}</b>", textStyle);

            if (CurrentExperiment != null)
                GUILayout.Label($"このPCに届いているデータ: {CurrentExperiment.Describe()}", textStyle);

            NextButton(Page.Kinects, "次へ: キネクト");
            return;
        }

        Heading("2. 実験", "続きの実験なら一覧から選び、初めての実験なら「新しく作る」を選んでください。" +
                          (settings.role == HomeSyncRole.Control ? "選んだ実験は全PCに配られます。" : ""));
        experimentTab = GUILayout.Toolbar(experimentTab, new[] { "既存の実験を選ぶ", "新しく作る" }, buttonStyle);
        GUILayout.Space(8);

        if (experimentTab == 0)
        {
            if (experiments.Count == 0)
                GUILayout.Label("データの場所に実験がありません。「新しく作る」から作ってください。", textStyle);

            foreach (HomeExperimentCatalog.ExperimentSummary experiment in experiments)
            {
                bool selected = experiment.name == session.experimentName;
                Color previous = GUI.backgroundColor;

                if (selected)
                    GUI.backgroundColor = new Color(0.55f, 0.85f, 1f);

                if (GUILayout.Button($"<size=19><b>{(selected ? "● " : "")}{experiment.name}</b></size>\n<size=14>{experiment.Describe()}</size>", cardStyle, GUILayout.MinHeight(56)) && !selected)
                    SelectExperiment(settings, experiment.name);

                GUI.backgroundColor = previous;
            }
        }
        else
        {
            DrawNewExperiment(settings);
        }

        if (HomeExperimentPaths.IsValidFolderName(session.experimentName, out _))
            NextButton(Page.Kinects, "次へ: キネクト");
    }

    private void SelectExperiment(HomeSyncSettings settings, string experimentName)
    {
        // 実験が変わったら実験対象者は決め直す
        HomeSession.Publish(experimentName, "", settings.DisplayName);
        subjectInput = "";
        Notify(settings.role == HomeSyncRole.Control ? $"実験を {experimentName} にして、全PCに配りました。" : $"実験を {experimentName} にしました。");
    }

    private void DrawNewExperiment(HomeSyncSettings settings)
    {
        GUILayout.Label("実験の名前(フォルダ名になります。例: 20261201_oono)", textStyle);
        newExperimentName = GUILayout.TextField(newExperimentName ?? "", fieldStyle);

        List<string> sources = new List<string> { "引き継がない" };
        sources.AddRange(experiments.FindAll(e => e.hasEnv).ConvertAll(e => e.name));
        copyFromIndex = Mathf.Clamp(copyFromIndex, 0, sources.Count - 1);

        GUILayout.Space(8);
        GUILayout.Label("同じ部屋・同じキネクトの配置で続けるなら、引き継ぐ実験を選んでください。", textStyle);
        copyFromIndex = GUILayout.SelectionGrid(copyFromIndex, sources.ToArray(), Mathf.Min(3, sources.Count), buttonStyle);

        if (copyFromIndex > 0)
        {
            HomeExperimentCatalog.ExperimentSummary source = experiments.Find(e => e.name == sources[copyFromIndex]);
            GUILayout.Label(source?.Describe() ?? "", smallStyle);
            copyRoom = GUILayout.Toggle(copyRoom, " 部屋と対象物体", toggleStyle);
            copyPoses = GUILayout.Toggle(copyPoses, " キネクトの位置(キネクトを動かしていないなら、位置合わせをやり直さなくてよい)", toggleStyle);
            copyPointClouds = GUILayout.Toggle(copyPointClouds, " キネクトの点群(位置合わせの基準に使う)", toggleStyle);
        }

        bool valid = HomeExperimentPaths.IsValidFolderName(newExperimentName, out string error);
        bool exists = experiments.Exists(e => e.name == newExperimentName);

        if (!string.IsNullOrEmpty(newExperimentName) && !valid)
            GUILayout.Label($"<color=#ff8080>{error}</color>", smallStyle);
        else if (exists)
            GUILayout.Label("<color=#ff8080>同じ名前の実験がすでにあります。</color>", smallStyle);

        GUILayout.Space(8);
        GUI.enabled = valid && !exists;

        if (GUILayout.Button(settings.role == HomeSyncRole.Control ? "作って全PCに配る" : "作る", bigButton))
        {
            string copyFrom = copyFromIndex > 0 ? sources[copyFromIndex] : null;

            if (HomeExperimentCatalog.Create(settings.dataRootDirectory, newExperimentName, copyFrom, copyRoom, copyPoses, copyPointClouds, out string result))
            {
                string created = newExperimentName;
                RefreshExperiments();
                SelectExperiment(settings, created);
                Notify(result);
                newExperimentName = "";
                experimentTab = 0;
            }
            else
            {
                Notify(result, true);
            }
        }

        GUI.enabled = true;
    }

    // ------------------------------------------------------------
    // 3. キネクト
    // ------------------------------------------------------------

    private void DrawKinects()
    {
        HomeSyncSettings settings = HomeSyncSettings.Current;
        Heading("3. キネクト", "このPCにUSBでつないでいるキネクトを選びます。まず「つながっているキネクトを調べる」を押してください。" +
                            "同期ケーブルを使うときは、全PCの中で1台だけ Master、他は Subordinate にします。");

        if (GUILayout.Button("つながっているキネクトを調べる", bigButton))
        {
            detected = HomeKinectDetector.Detect(out detectError);

            if (detectError == null)
            {
                HomeSyncSettings edited = settings.Clone();
                edited.detectedSerials = detected.ConvertAll(d => d.serialNumber ?? "").ToArray();
                SaveSettings(edited);
                settings = edited;
                Notify(detected.Count == 0 ? "キネクトが見つかりません。USBと電源を確認してください。" : $"キネクトを {detected.Count} 台見つけました。", detected.Count == 0);
            }
            else
            {
                Notify(detectError, true);
            }
        }

        int deviceCount = Mathf.Max(1, settings.detectedSerials?.Length ?? 0);

        if (settings.detectedSerials != null && settings.detectedSerials.Length > 0)
        {
            for (int i = 0; i < settings.detectedSerials.Length; i++)
            {
                string serial = settings.detectedSerials[i];
                GUILayout.Label($"デバイス {i}: {(string.IsNullOrEmpty(serial) ? "使用中のためシリアル番号を読めませんでした" : "SN " + serial)}", smallStyle);
            }
        }
        else
        {
            GUILayout.Label("まだ調べていません。", smallStyle);
            deviceCount = 4;
        }

        GUILayout.Space(8);
        HomeSyncSettings working = settings.Clone();
        bool changed = false;
        HomeExperimentCatalog.ExperimentSummary experiment = CurrentExperiment;
        string[] deviceLabels = new string[deviceCount];

        for (int i = 0; i < deviceCount; i++)
            deviceLabels[i] = $"デバイス {i}";

        foreach (HomeKinectDeviceIndex entry in working.kinects)
        {
            GUILayout.BeginVertical(boxStyle);
            bool use = GUILayout.Toggle(entry.use, $" <b>Kinect {entry.kinectId}</b> をこのPCで使う", toggleStyle);

            if (use != entry.use)
            {
                entry.use = use;
                changed = true;
            }

            if (entry.use)
            {
                int device = GUILayout.Toolbar(Mathf.Clamp(entry.deviceIndex, 0, deviceCount - 1), deviceLabels, buttonStyle);
                int sync = GUILayout.Toolbar((int)entry.syncMode, SyncModeLabels, buttonStyle);

                if (device != entry.deviceIndex || sync != (int)entry.syncMode)
                {
                    entry.deviceIndex = device;
                    entry.syncMode = (HomeKinectDeviceIndex.SyncMode)sync;
                    changed = true;
                }

                GUILayout.Label(KinectHint(working, entry, experiment), smallStyle);
            }

            GUILayout.EndVertical();
        }

        string problem = CheckKinectTable(working);

        if (problem != null)
            GUILayout.Label($"<color=#ff8080>{problem}</color>", textStyle);

        if (changed)
            SaveSettings(working);

        NextButton(Page.Calibration, "次へ: キャリブレーション");
    }

    private static string KinectHint(HomeSyncSettings settings, HomeKinectDeviceIndex entry, HomeExperimentCatalog.ExperimentSummary experiment)
    {
        HomeExperimentCatalog.KinectEnv env = experiment?.Kinect(entry.kinectId);

        if (env == null || !env.hasPose)
            return "この実験ではまだ位置合わせしていません(4. キャリブレーションで行います)。";

        string saved = env.serialNumber;
        string selected = settings.detectedSerials != null && entry.deviceIndex < settings.detectedSerials.Length
            ? settings.detectedSerials[entry.deviceIndex]
            : null;

        if (string.IsNullOrEmpty(saved) || string.IsNullOrEmpty(selected))
            return "位置合わせ済み。";

        return saved == selected
            ? $"位置合わせ済み(SN {saved} と一致)。"
            : $"<color=#ffb060>注意: 位置合わせしたのは SN {saved} のキネクトですが、選んでいるデバイスは SN {selected} です。</color>";
    }

    private static string CheckKinectTable(HomeSyncSettings settings)
    {
        HashSet<int> devices = new HashSet<int>();
        int masters = 0;
        int used = 0;

        foreach (HomeKinectDeviceIndex entry in settings.kinects)
        {
            if (!entry.use)
                continue;

            used++;

            if (!devices.Add(entry.deviceIndex))
                return $"同じデバイス {entry.deviceIndex} を2つのキネクトに選んでいます。";

            if (entry.syncMode == HomeKinectDeviceIndex.SyncMode.Master)
                masters++;
        }

        if (used == 0)
            return "使うキネクトが選ばれていません。";

        return masters > 1 ? "Master が2台以上あります。Master は全PCの中で1台だけにします。" : null;
    }

    // ------------------------------------------------------------
    // 4. キャリブレーション
    // ------------------------------------------------------------

    private void DrawCalibration()
    {
        HomeSyncSettings settings = HomeSyncSettings.Current;
        HomeExperimentCatalog.ExperimentSummary experiment = CurrentExperiment;
        Heading("4. キャリブレーション", "部屋の中でのキネクトの位置を求めます。基準にするキネクトの点群に、他のキネクトの点群を合わせます。" +
                                       "基準と点群の取り方を選んで「キャリブレーションを始める」を押してください。");

        if (!HomeExperimentPaths.IsValidFolderName(HomeSession.Current.experimentName, out _))
        {
            GUILayout.Label("先に実験を選んでください。", textStyle);
            NextButton(Page.Experiment, "実験を選ぶ");
            return;
        }

        GUILayout.Label("基準にするキネクト", textStyle);
        List<string> ids = new List<string>(Array.ConvertAll(settings.kinects, k => k.kinectId));
        int current = Mathf.Max(0, ids.IndexOf(settings.referenceKinectId));
        int picked = GUILayout.Toolbar(current, ids.ConvertAll(id => $"Kinect {id}").ToArray(), buttonStyle);

        GUILayout.Label("基準の点群", textStyle);
        int source = GUILayout.Toolbar((int)settings.referencePointCloudSource, new[] { "保存済みの点群を使う", "このPCで撮り直す" }, buttonStyle);

        if (picked != current || source != (int)settings.referencePointCloudSource)
        {
            HomeSyncSettings edited = settings.Clone();
            edited.referenceKinectId = ids[picked];
            edited.referencePointCloudSource = (HomeEnvSetup.ReferencePointCloudSource)source;
            SaveSettings(edited);
            settings = edited;
        }

        HomeExperimentCatalog.KinectEnv reference = experiment?.Kinect(settings.referenceKinectId);
        string state;

        if (settings.referencePointCloudSource == HomeEnvSetup.ReferencePointCloudSource.LiveCapture)
            state = $"Kinect {settings.referenceKinectId} をこのPCにつないで、その場で点群を撮ります。";
        else if (reference != null && reference.hasPointCloud)
            state = $"Kinect {settings.referenceKinectId} の保存済みの点群({reference.pointCloudSavedAt:yyyy-MM-dd HH:mm})を基準にします。このPCにつないでいなくても位置合わせできます。";
        else
            state = $"この実験には Kinect {settings.referenceKinectId} の点群がまだありません。このPCにつないで、その場で撮ります(保存すると他のPCにも配られます)。";

        GUILayout.Label(state, smallStyle);
        GUILayout.Space(8);

        GUILayout.Label("この実験のキネクトの状態", textStyle);

        foreach (HomeKinectDeviceIndex entry in settings.kinects)
        {
            HomeExperimentCatalog.KinectEnv env = experiment?.Kinect(entry.kinectId);
            string pose = env != null && env.hasPose ? "位置 ✓" : "位置 未";
            string cloud = env != null && env.hasPointCloud ? $"点群 ✓({env.pointCloudSavedAt:MM/dd HH:mm})" : "点群 未";
            string here = entry.use ? "(このPC)" : "";
            GUILayout.Label($"Kinect {entry.kinectId}{here}: {pose} / {cloud}", smallStyle);
        }

        if (UsedKinects(settings).Count == 0)
            GUILayout.Label("<color=#ff8080>このPCで使うキネクトが選ばれていません(3. キネクト)。</color>", textStyle);

        GUILayout.Space(12);

        if (GUILayout.Button("キャリブレーションを始める", bigButton))
            HomeSceneFlow.Load(HomeSceneFlow.Setup);
    }

    // ------------------------------------------------------------
    // 5. 記録
    // ------------------------------------------------------------

    private void DrawRecording()
    {
        HomeSyncSettings settings = HomeSyncSettings.Current;
        HomeSessionData session = HomeSession.Current;
        Heading("5. 記録", "実験対象者を決めて、記録のシーンを選んでください。記録のシーンでは Space キーで開始・終了します。");

        if (!HomeExperimentPaths.IsValidFolderName(session.experimentName, out _))
        {
            GUILayout.Label("先に実験を選んでください。", textStyle);
            NextButton(Page.Experiment, "実験を選ぶ");
            return;
        }

        if (settings.CanEditSession)
        {
            GUILayout.Label("実験対象者(フォルダ名になります)", textStyle);
            subjectInput = GUILayout.TextField(subjectInput ?? "", fieldStyle);

            string[] existing = HomeExperimentPaths.GetChildDirectoryNames(
                HomeExperimentPaths.GetSkeletonRootDirectory(settings.dataRootDirectory, session.experimentName));

            if (existing.Length > 0)
            {
                GUILayout.Label("これまでに記録した人(押すと入力欄に入ります。同じ人で撮り直すと前のデータは名前を変えて残ります)", smallStyle);
                int chosen = GUILayout.SelectionGrid(-1, existing, Mathf.Min(4, existing.Length), buttonStyle);

                if (chosen >= 0)
                    subjectInput = existing[chosen];
            }

            bool changed = (subjectInput ?? "") != session.subjectName;
            bool valid = HomeExperimentPaths.IsValidFolderName(subjectInput, out string error);

            if (changed && !string.IsNullOrEmpty(subjectInput) && !valid)
                GUILayout.Label($"<color=#ff8080>{error}</color>", smallStyle);

            GUI.enabled = changed && valid;

            if (GUILayout.Button(!changed ? $"決定済み: {Show(session.subjectName)}" :
                                 settings.role == HomeSyncRole.Control ? "決定して全PCに配る" : "決定", bigButton))
            {
                HomeSession.Publish(session.experimentName, subjectInput, settings.DisplayName);
                Notify($"実験対象者を {subjectInput} にしました。");
            }

            GUI.enabled = true;
        }
        else
        {
            GUILayout.Label($"制御PCが決めた実験対象者: <b>{Show(session.subjectName)}</b>", textStyle);
        }

        GUILayout.Space(12);
        GUILayout.Label("記録の前の確認", textStyle);
        List<string> problems = RecordingProblems(settings);

        if (problems.Count == 0)
            GUILayout.Label("✓ 準備できています。", textStyle);

        foreach (string problem in problems)
            GUILayout.Label($"<color=#ffb060>・{problem}</color>", textStyle);

        GUILayout.Space(12);

        if (GUILayout.Button("<size=20><b>記録(このPCのキネクトだけ)</b></size>\n<size=15>同期ケーブルを使わず、このPCだけで記録する</size>", cardStyle, GUILayout.MinHeight(64)))
            HomeSceneFlow.Load(HomeSceneFlow.Recording);

        if (GUILayout.Button("<size=20><b>記録(複数のPCで同時に)</b></size>\n<size=15>同期ケーブルでつないだ全PCで記録をそろえる</size>", cardStyle, GUILayout.MinHeight(64)))
            HomeSceneFlow.Load(HomeSceneFlow.RecordingMulti);
    }

    private List<string> RecordingProblems(HomeSyncSettings settings)
    {
        List<string> problems = new List<string>();
        HomeSessionData session = HomeSession.Current;

        if (!HomeExperimentPaths.IsValidFolderName(session.subjectName, out _))
            problems.Add("実験対象者が決まっていません。");

        if (UsedKinects(settings).Count == 0)
            problems.Add("このPCで使うキネクトが選ばれていません(3. キネクト)。");

        List<string> missing = MissingPoses(settings);

        if (missing.Count > 0)
            problems.Add($"Kinect {string.Join(", ", missing)} の位置合わせがまだです(4. キャリブレーション)。");

        if (settings.role == HomeSyncRole.Recorder && !HomeSyncStatus.Connected)
            problems.Add("制御PCにつながっていません(実験・実験対象者が最新でない可能性があります)。");

        string tableProblem = CheckKinectTable(settings);

        if (tableProblem != null && UsedKinects(settings).Count > 0)
            problems.Add(tableProblem);

        return problems;
    }

    // ------------------------------------------------------------
    // 分析
    // ------------------------------------------------------------

    private void DrawAnalysis()
    {
        Heading("分析", "記録したデータを再生・解析するシーンを開きます。分析のシーンでは、実験・実験対象者などをインスペクターで選びます。");

        foreach (HomeSceneFlow.SceneInfo scene in HomeSceneFlow.AnalysisScenes)
        {
            if (GUILayout.Button($"<size=19><b>{scene.title}</b></size>\n<size=14>{System.IO.Path.GetFileNameWithoutExtension(scene.path)}</size>", cardStyle, GUILayout.MinHeight(56)))
                HomeSceneFlow.Load(scene);
        }
    }

    // ------------------------------------------------------------
    // 同期の状態
    // ------------------------------------------------------------

    private void DrawSync()
    {
        HomeSyncSettings settings = HomeSyncSettings.Current;
        Heading("同期の状態", settings.role == HomeSyncRole.Control
            ? "つながっている録画PCの一覧です。録画PCでこのシーンを開き、役割を「録画PC」にすると、ここに出ます。"
            : "制御PCとの接続の状態です。同じネットワーク(Wi-Fi・LANケーブル)につながっていれば、自動で制御PCを見つけます。");

        if (settings.role == HomeSyncRole.Control)
        {
            if (HomeSyncStatus.Clients.Count == 0)
                GUILayout.Label("まだ録画PCがつながっていません。", textStyle);

            foreach (HomeSyncStatus.ClientInfo client in HomeSyncStatus.Clients)
            {
                GUILayout.BeginVertical(boxStyle);
                GUILayout.Label($"<b>{client.name}</b>({client.address})　{(client.IsAlive ? "● 接続中" : $"○ {(DateTime.Now - client.lastSeen).TotalSeconds:F0}秒前まで接続")}", textStyle);
                GUILayout.Label(client.status ?? "", smallStyle);
                GUILayout.EndVertical();
            }

            GUILayout.Label($"このPCのアドレス: {string.Join(", ", HomeSyncProtocol.GetLocalAddresses())}", smallStyle);
        }
        else
        {
            GUILayout.Label(HomeSyncStatus.Connected ? $"● 制御PC {HomeSyncStatus.ServerDescription} と接続中" : "○ 制御PCにつながっていません", textStyle);
            GUILayout.Label(HomeSyncStatus.Status, smallStyle);
            GUILayout.Label($"最後に同期した時刻: {(string.IsNullOrEmpty(HomeSyncStatus.LastSyncedAt) ? "-" : HomeSyncStatus.LastSyncedAt)}", smallStyle);

            if (GUILayout.Button("今すぐ同期", buttonStyle))
                HomeSyncStatus.SyncNowRequested = true;
        }

        GUILayout.Space(12);
        GUILayout.Label("ログ", textStyle);
        List<string> logs = HomeSyncStatus.RecentLogs(30);

        for (int i = logs.Count - 1; i >= 0; i--)
            GUILayout.Label(logs[i], smallStyle);
    }
}
