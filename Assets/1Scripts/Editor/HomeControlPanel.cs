using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// メニューの HomeExperiment > コントロールパネル。計測の準備を上から順に進める画面。
//   1. このPCの役割(制御PC / 録画PC / このPCだけ / 使わない)とデータの場所
//   2. 実験(既存の実験を選ぶ / 新しく作る。既存の実験から部屋・位置・点群を引き継げる)
//   3. 実験対象者
//   4. このPCで使うキネクト(つながっているキネクトを調べて選ぶ)
//   5. 位置合わせの基準(どのキネクトの点群を基準にするか、保存済みを使うか撮り直すか)
//   6. シーンを開く・再生する
//   7. 同期の状態
// ここでの設定は UserSettings/ に保存され(PCごと)、計測のシーンはインスペクターの代わりにこの値を使う。
public class HomeControlPanel : EditorWindow
{
    private const string SetupScene = "Assets/0Scenes/1Measurement/0HomeSetup.unity";
    private const string RecordingScene = "Assets/0Scenes/1Measurement/1HeadDirRecording.unity";
    private const string RecordingMultiScene = "Assets/0Scenes/1Measurement/1HeadDirRecordingMulti.unity";

    private static readonly string[] SyncModeLabels = { "シーンの設定のまま", "単独(同期ケーブルなし)", "Master(同期信号を出す)", "Subordinate(信号を受ける)" };

    private Vector2 scroll;
    private int experimentTab;
    private string newExperimentName = "";
    private int copyFromIndex;
    private bool copyRoom = true;
    private bool copyPoses = true;
    private bool copyPointClouds = true;
    private string subjectInput;
    private List<HomeKinectDetector.Device> detected;
    private string detectError;
    private bool showLogs;
    private List<HomeExperimentCatalog.ExperimentSummary> experiments = new List<HomeExperimentCatalog.ExperimentSummary>();
    private double nextRefresh;
    private string lastMessage;
    private MessageType lastMessageType;

    private GUIStyle titleStyle;
    private GUIStyle stepStyle;
    private GUIStyle bigButton;
    private GUIStyle rowButton;

    [MenuItem("HomeExperiment/コントロールパネル %#h", priority = 0)]
    public static void Open()
    {
        HomeControlPanel window = GetWindow<HomeControlPanel>("HomeExperiment");
        window.minSize = new Vector2(420, 400);
        window.Show();
    }

    public static void RepaintOpenWindows()
    {
        foreach (HomeControlPanel window in Resources.FindObjectsOfTypeAll<HomeControlPanel>())
            window.Repaint();
    }

    private void OnEnable()
    {
        subjectInput = HomeSession.Current.subjectName;
        RefreshExperiments();
    }

    private void Update()
    {
        // 同期の状態・実験の一覧をこまめに表示し直す
        if (EditorApplication.timeSinceStartup >= nextRefresh)
        {
            nextRefresh = EditorApplication.timeSinceStartup + 1.5;
            RefreshExperiments();
            Repaint();
        }
    }

    private void RefreshExperiments()
    {
        experiments = HomeExperimentCatalog.List(HomeSyncSettings.Current.dataRootDirectory);
    }

    private void InitStyles()
    {
        if (titleStyle != null)
            return;

        titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 16 };
        stepStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
        bigButton = new GUIStyle(GUI.skin.button) { fontSize = 12, fixedHeight = 34, wordWrap = true };
        rowButton = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, wordWrap = true, richText = true, padding = new RectOffset(8, 8, 6, 6) };
    }

    private void OnGUI()
    {
        InitStyles();
        HomeSyncSettings settings = HomeSyncSettings.Current;

        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.LabelField("HomeExperiment コントロールパネル", titleStyle);
        EditorGUILayout.LabelField(StatusLine(settings), EditorStyles.wordWrappedMiniLabel);

        if (!string.IsNullOrEmpty(lastMessage))
            EditorGUILayout.HelpBox(lastMessage, lastMessageType);

        if (!string.IsNullOrEmpty(HomeSyncService.StartError))
            EditorGUILayout.HelpBox(HomeSyncService.StartError, MessageType.Error);

        Step(1, "このPCの役割", () => DrawRole(settings));

        if (settings.role == HomeSyncRole.Off)
        {
            EditorGUILayout.HelpBox("「使わない」では、計測のシーンは今までどおりインスペクターの値(実験の名前・実験対象者・データの場所・Kinects の表)を使います。", MessageType.Info);
            EditorGUILayout.EndScrollView();
            return;
        }

        Step(2, "実験", () => DrawExperiment(settings));
        Step(3, "実験対象者", () => DrawSubject(settings));
        Step(4, "このPCで使うキネクト", () => DrawKinects(settings));
        Step(5, "位置合わせの基準(シーン0)", () => DrawReference(settings));
        Step(6, "シーンを開く", DrawScenes);

        if (settings.UsesNetwork)
            Step(7, "同期の状態", () => DrawSync(settings));

        EditorGUILayout.EndScrollView();
    }

    private static string StatusLine(HomeSyncSettings settings)
    {
        HomeSessionData session = HomeSession.Current;
        string role = settings.role == HomeSyncRole.Control ? "制御PC" :
                      settings.role == HomeSyncRole.Recorder ? "録画PC" :
                      settings.role == HomeSyncRole.Single ? "このPCだけ" : "使わない";
        string connection = "";

        if (settings.role == HomeSyncRole.Control && HomeSyncService.Server != null)
            connection = $" / 録画PC {HomeSyncService.Server.GetClients().FindAll(c => (DateTime.Now - c.lastSeen).TotalSeconds < 10).Count}台と接続中";
        else if (settings.role == HomeSyncRole.Recorder && HomeSyncService.Client != null)
            connection = HomeSyncService.Client.Connected ? " / 制御PCと接続中" : " / 制御PCにつながっていません";

        return $"{settings.DisplayName}: {role}{connection}   実験: {Show(session.experimentName)}   実験対象者: {Show(session.subjectName)}";
    }

    private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "(未設定)" : value;

    private void Step(int number, string title, Action body)
    {
        EditorGUILayout.Space(6);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField($"{number}. {title}", stepStyle);
            EditorGUILayout.Space(2);
            body();
        }
    }

    private void Notify(string message, MessageType type)
    {
        lastMessage = message;
        lastMessageType = type;
        HomeSyncService.AddLog(message);
    }

    private static void SaveSettings(HomeSyncSettings edited)
    {
        HomeSyncSettings.Save(edited);
        HomeSyncService.RestartIfNeeded();
    }

    // ------------------------------------------------------------
    // 1. 役割
    // ------------------------------------------------------------

    private void DrawRole(HomeSyncSettings settings)
    {
        HomeSyncRole[] roles = { HomeSyncRole.Control, HomeSyncRole.Recorder, HomeSyncRole.Single, HomeSyncRole.Off };
        string[] labels = { "制御PC", "録画PC", "このPCだけ", "使わない" };
        string[] descriptions =
        {
            "実験の名前・実験対象者を決めて、部屋・キネクトの位置などを全PCに配るPC(1台だけ)。キネクトをつないで記録してもよい。",
            "制御PCから実験の名前・実験対象者・部屋などを受け取って記録するPC。自分のキネクトの位置合わせの結果は制御PCへ送る。",
            "ネットワークを使わず、このPCだけで準備から記録まで行う。設定はこの画面で行う。",
            "この画面の設定を使わず、今までどおりシーンのインスペクターで設定する。"
        };

        using (new EditorGUILayout.HorizontalScope())
        {
            for (int i = 0; i < roles.Length; i++)
            {
                bool selected = settings.role == roles[i];
                Color previous = GUI.backgroundColor;

                if (selected)
                    GUI.backgroundColor = new Color(0.55f, 0.8f, 1f);

                if (GUILayout.Button(labels[i], bigButton) && !selected)
                {
                    HomeSyncSettings edited = settings.Clone();
                    edited.role = roles[i];
                    SaveSettings(edited);
                    RefreshExperiments();
                }

                GUI.backgroundColor = previous;
            }
        }

        EditorGUILayout.LabelField(descriptions[Array.IndexOf(roles, settings.role)], EditorStyles.wordWrappedMiniLabel);

        if (settings.role == HomeSyncRole.Off)
            return;

        EditorGUILayout.Space(4);
        string pcName = EditorGUILayout.DelayedTextField(new GUIContent("このPCの名前", "空ならコンピューター名。制御PCの一覧に表示する。"), settings.pcName);

        string dataRoot;

        using (new EditorGUILayout.HorizontalScope())
        {
            dataRoot = EditorGUILayout.DelayedTextField(new GUIContent("データの場所", "空なら Assets/Data/HomeExperiment。例: D:\\HomeExperiment"), settings.dataRootDirectory);

            if (GUILayout.Button("選ぶ", GUILayout.Width(44)))
            {
                string picked = EditorUtility.OpenFolderPanel("データの場所", HomeExperimentPaths.ResolveRootDirectory(settings.dataRootDirectory), "");

                if (!string.IsNullOrEmpty(picked))
                    dataRoot = picked.Replace('/', '\\');
            }
        }

        EditorGUILayout.LabelField(" ", HomeExperimentPaths.ResolveRootDirectory(dataRoot), EditorStyles.miniLabel);

        string address = settings.controlAddress;

        if (settings.role == HomeSyncRole.Recorder)
            address = EditorGUILayout.DelayedTextField(new GUIContent("制御PCのアドレス", "空なら同じネットワークの中を自動で探す。見つからないときは制御PCのIPアドレスを入れる。"), settings.controlAddress);

        if (pcName != settings.pcName || dataRoot != settings.dataRootDirectory || address != settings.controlAddress)
        {
            HomeSyncSettings edited = settings.Clone();
            edited.pcName = pcName;
            edited.dataRootDirectory = dataRoot;
            edited.controlAddress = address;
            SaveSettings(edited);
            RefreshExperiments();
        }

        if (settings.role == HomeSyncRole.Control)
            EditorGUILayout.LabelField("このPCのアドレス", string.Join(", ", HomeSyncProtocol.GetLocalAddresses()), EditorStyles.wordWrappedMiniLabel);
    }

    // ------------------------------------------------------------
    // 2. 実験
    // ------------------------------------------------------------

    private void DrawExperiment(HomeSyncSettings settings)
    {
        HomeSessionData session = HomeSession.Current;

        if (!settings.CanEditSession)
        {
            EditorGUILayout.LabelField("実験は制御PCで選びます。", EditorStyles.wordWrappedMiniLabel);
            HomeExperimentCatalog.ExperimentSummary current = experiments.Find(e => e.name == session.experimentName);
            EditorGUILayout.LabelField("制御PCが選んだ実験", Show(session.experimentName), EditorStyles.boldLabel);

            if (current != null)
                EditorGUILayout.LabelField("このPCに届いている Env", current.Describe(), EditorStyles.wordWrappedMiniLabel);

            return;
        }

        experimentTab = GUILayout.Toolbar(experimentTab, new[] { "既存の実験を使う", "新しく作る" });
        EditorGUILayout.Space(4);

        if (experimentTab == 0)
            DrawExistingExperiments(settings, session);
        else
            DrawNewExperiment(settings);
    }

    private void DrawExistingExperiments(HomeSyncSettings settings, HomeSessionData session)
    {
        if (experiments.Count == 0)
        {
            EditorGUILayout.HelpBox("データの場所に実験がありません。「新しく作る」から作ってください。", MessageType.Info);
            return;
        }

        foreach (HomeExperimentCatalog.ExperimentSummary experiment in experiments)
        {
            bool selected = experiment.name == session.experimentName;
            Color previous = GUI.backgroundColor;

            if (selected)
                GUI.backgroundColor = new Color(0.55f, 0.8f, 1f);

            string label = $"<b>{(selected ? "● " : "")}{experiment.name}</b>\n<size=10>{experiment.Describe()}</size>";

            if (GUILayout.Button(label, rowButton) && !selected)
                SelectExperiment(settings, experiment.name);

            GUI.backgroundColor = previous;
        }
    }

    private void SelectExperiment(HomeSyncSettings settings, string experimentName)
    {
        // 実験が変わったら実験対象者は選び直す
        HomeSession.Publish(experimentName, "", settings.DisplayName);
        subjectInput = "";
        Notify(settings.role == HomeSyncRole.Control
            ? $"実験を {experimentName} にして、全PCに配りました。"
            : $"実験を {experimentName} にしました。", MessageType.Info);

        if (Application.isPlaying)
            Notify("プレイ中に実験を変えたので、各PCでプレイし直してください(読み込んでいる Env は前の実験のままです)。", MessageType.Warning);
    }

    private void DrawNewExperiment(HomeSyncSettings settings)
    {
        newExperimentName = EditorGUILayout.TextField("実験の名前", newExperimentName);

        List<string> sources = new List<string> { "引き継がない(空の実験)" };

        foreach (HomeExperimentCatalog.ExperimentSummary experiment in experiments)
        {
            if (experiment.hasEnv)
                sources.Add(experiment.name);
        }

        copyFromIndex = Mathf.Clamp(copyFromIndex, 0, sources.Count - 1);
        copyFromIndex = EditorGUILayout.Popup(new GUIContent("引き継ぐ実験", "同じ部屋・同じキネクトの配置で続ける場合に選ぶ。"), copyFromIndex, sources.ToArray());

        if (copyFromIndex > 0)
        {
            HomeExperimentCatalog.ExperimentSummary source = experiments.Find(e => e.name == sources[copyFromIndex]);
            EditorGUILayout.LabelField(" ", source?.Describe() ?? "", EditorStyles.wordWrappedMiniLabel);
            EditorGUI.indentLevel++;
            copyRoom = EditorGUILayout.ToggleLeft("部屋と対象物体", copyRoom);
            copyPoses = EditorGUILayout.ToggleLeft("キネクトの位置(位置合わせをやり直さない場合)", copyPoses);
            copyPointClouds = EditorGUILayout.ToggleLeft("キネクトの点群(位置合わせの基準に使う)", copyPointClouds);
            EditorGUI.indentLevel--;
        }

        bool valid = HomeExperimentPaths.IsValidFolderName(newExperimentName, out string error);
        bool exists = experiments.Exists(e => e.name == newExperimentName);

        if (!string.IsNullOrEmpty(newExperimentName) && !valid)
            EditorGUILayout.HelpBox(error, MessageType.Warning);
        else if (exists)
            EditorGUILayout.HelpBox("同じ名前の実験がすでにあります。", MessageType.Warning);

        using (new EditorGUI.DisabledScope(!valid || exists))
        {
            if (GUILayout.Button(settings.role == HomeSyncRole.Control ? "作って全PCに配る" : "作る", bigButton))
            {
                string copyFrom = copyFromIndex > 0 ? sources[copyFromIndex] : null;

                if (HomeExperimentCatalog.Create(settings.dataRootDirectory, newExperimentName, copyFrom, copyRoom, copyPoses, copyPointClouds, out string message))
                {
                    Notify(message, MessageType.Info);
                    SelectExperiment(settings, newExperimentName);
                    newExperimentName = "";
                    experimentTab = 0;
                    RefreshExperiments();
                }
                else
                {
                    Notify(message, MessageType.Error);
                }
            }
        }
    }

    // ------------------------------------------------------------
    // 3. 実験対象者
    // ------------------------------------------------------------

    private void DrawSubject(HomeSyncSettings settings)
    {
        HomeSessionData session = HomeSession.Current;

        if (!settings.CanEditSession)
        {
            EditorGUILayout.LabelField("制御PCが決めた実験対象者", Show(session.subjectName), EditorStyles.boldLabel);
            return;
        }

        if (!HomeExperimentPaths.IsValidFolderName(session.experimentName, out _))
        {
            EditorGUILayout.LabelField("先に実験を選んでください。", EditorStyles.wordWrappedMiniLabel);
            return;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            subjectInput = EditorGUILayout.TextField("実験対象者", subjectInput ?? "");

            if (GUILayout.Button("▼", GUILayout.Width(24)))
            {
                GenericMenu menu = new GenericMenu();
                string[] names = HomeExperimentPaths.GetChildDirectoryNames(
                    HomeExperimentPaths.GetSkeletonRootDirectory(settings.dataRootDirectory, session.experimentName));

                if (names.Length == 0)
                    menu.AddDisabledItem(new GUIContent("(まだ記録した人はいません)"));

                foreach (string name in names)
                    menu.AddItem(new GUIContent(name), false, () => { subjectInput = name; GUI.FocusControl(null); });

                menu.ShowAsContext();
            }
        }

        bool changed = (subjectInput ?? "") != session.subjectName;
        bool valid = HomeExperimentPaths.IsValidFolderName(subjectInput, out string error);

        if (changed && !string.IsNullOrEmpty(subjectInput) && !valid)
            EditorGUILayout.HelpBox(error, MessageType.Warning);

        using (new EditorGUI.DisabledScope(!changed || !valid))
        {
            string label = !changed ? $"決定済み: {Show(session.subjectName)}" :
                           settings.role == HomeSyncRole.Control ? "決定して全PCに配る" : "決定";

            if (GUILayout.Button(label, bigButton))
            {
                HomeSession.Publish(session.experimentName, subjectInput, settings.DisplayName);
                Notify($"実験対象者を {subjectInput} にしました。", MessageType.Info);
                GUI.FocusControl(null);
            }
        }
    }

    // ------------------------------------------------------------
    // 4. キネクト
    // ------------------------------------------------------------

    private void DrawKinects(HomeSyncSettings settings)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button(new GUIContent("つながっているキネクトを調べる", "プレイ中はキネクトを使っているので調べられません。"), GUILayout.Height(24)))
                    Detect(settings);
            }
        }

        if (!string.IsNullOrEmpty(detectError))
            EditorGUILayout.HelpBox(detectError, MessageType.Warning);
        else if (detected != null)
            EditorGUILayout.LabelField(detected.Count == 0 ? "キネクトが見つかりません(USBと電源を確認してください)" :
                                       $"見つかったキネクト {detected.Count} 台", EditorStyles.miniLabel);

        HomeExperimentCatalog.ExperimentSummary experiment = experiments.Find(e => e.name == HomeSession.Current.experimentName);
        HomeSyncSettings edited = settings.Clone();
        bool changed = false;
        string[] deviceLabels = DeviceLabels(settings);

        foreach (HomeKinectDeviceIndex entry in edited.kinects)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool use = EditorGUILayout.ToggleLeft($"Kinect {entry.kinectId}", entry.use, EditorStyles.boldLabel, GUILayout.Width(90));

                using (new EditorGUI.DisabledScope(!use))
                {
                    int device = EditorGUILayout.Popup(Mathf.Clamp(entry.deviceIndex, 0, deviceLabels.Length - 1), deviceLabels);
                    int sync = EditorGUILayout.Popup((int)entry.syncMode, SyncModeLabels, GUILayout.Width(150));

                    if (device != entry.deviceIndex || sync != (int)entry.syncMode)
                    {
                        entry.deviceIndex = device;
                        entry.syncMode = (HomeKinectDeviceIndex.SyncMode)sync;
                        changed = true;
                    }
                }

                if (use != entry.use)
                {
                    entry.use = use;
                    changed = true;
                }
            }

            if (entry.use)
            {
                string hint = KinectHint(settings, entry, experiment);

                if (!string.IsNullOrEmpty(hint))
                    EditorGUILayout.LabelField(" ", hint, EditorStyles.wordWrappedMiniLabel);
            }
        }

        string problem = CheckKinectTable(edited);

        if (problem != null)
            EditorGUILayout.HelpBox(problem, MessageType.Warning);

        EditorGUILayout.LabelField("このPCにつないでいるキネクトだけにチェックを入れます。同期ケーブルを使うときは、全PCの中で1台だけ Master、他は Subordinate にします。",
                                   EditorStyles.wordWrappedMiniLabel);

        if (changed)
            SaveSettings(edited);
    }

    private void Detect(HomeSyncSettings settings)
    {
        detected = HomeKinectDetector.Detect(out detectError);

        if (detectError != null)
            return;

        HomeSyncSettings edited = settings.Clone();
        edited.detectedSerials = detected.ConvertAll(d => d.serialNumber ?? "").ToArray();
        SaveSettings(edited);
        Notify($"キネクトを {detected.Count} 台見つけました。", MessageType.Info);
    }

    private static string[] DeviceLabels(HomeSyncSettings settings)
    {
        int count = Mathf.Max(4, settings.detectedSerials?.Length ?? 0);
        string[] labels = new string[count];

        for (int i = 0; i < count; i++)
        {
            string serial = settings.detectedSerials != null && i < settings.detectedSerials.Length ? settings.detectedSerials[i] : null;
            labels[i] = serial == null ? $"デバイス {i}" :
                        serial.Length == 0 ? $"デバイス {i}(使用中で読めず)" : $"デバイス {i}(SN {serial})";
        }

        return labels;
    }

    // 位置合わせ済みのキネクト本体と、今選んでいるデバイスが同じか
    private static string KinectHint(HomeSyncSettings settings, HomeKinectDeviceIndex entry, HomeExperimentCatalog.ExperimentSummary experiment)
    {
        HomeExperimentCatalog.KinectEnv env = experiment?.Kinect(entry.kinectId);

        if (env == null || !env.hasPose)
            return "この実験ではまだ位置合わせしていません。";

        string savedSerial = env.serialNumber;
        string selectedSerial = settings.detectedSerials != null && entry.deviceIndex < settings.detectedSerials.Length
            ? settings.detectedSerials[entry.deviceIndex]
            : null;

        if (string.IsNullOrEmpty(savedSerial) || string.IsNullOrEmpty(selectedSerial))
            return "位置合わせ済み。";

        return savedSerial == selectedSerial
            ? $"位置合わせ済み(SN {savedSerial} と一致)。"
            : $"注意: 位置合わせしたキネクトは SN {savedSerial} ですが、選んでいるデバイスは SN {selectedSerial} です。";
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
    // 5. 位置合わせの基準
    // ------------------------------------------------------------

    private void DrawReference(HomeSyncSettings settings)
    {
        List<string> ids = new List<string>();

        foreach (HomeKinectDeviceIndex entry in settings.kinects)
            ids.Add(entry.kinectId);

        int current = Mathf.Max(0, ids.IndexOf(settings.referenceKinectId));
        int picked = EditorGUILayout.Popup(new GUIContent("基準にするキネクト", "他のキネクトはこのキネクトの点群に合わせて位置を求める(ICP)。"), current, ids.ConvertAll(id => $"Kinect {id}").ToArray());
        int source = GUILayout.Toolbar((int)settings.referencePointCloudSource, new[] { "保存済みの点群を使う", "このPCで撮り直す" });

        if (picked != current || source != (int)settings.referencePointCloudSource)
        {
            HomeSyncSettings edited = settings.Clone();
            edited.referenceKinectId = ids[picked];
            edited.referencePointCloudSource = (HomeEnvSetup.ReferencePointCloudSource)source;
            SaveSettings(edited);
            settings = edited;
        }

        HomeExperimentCatalog.ExperimentSummary experiment = experiments.Find(e => e.name == HomeSession.Current.experimentName);
        HomeExperimentCatalog.KinectEnv reference = experiment?.Kinect(settings.referenceKinectId);
        string state;

        if (settings.referencePointCloudSource == HomeEnvSetup.ReferencePointCloudSource.LiveCapture)
            state = $"Kinect {settings.referenceKinectId} をこのPCにつないで、その場で点群を撮ります。";
        else if (reference != null && reference.hasPointCloud)
            state = $"Kinect {settings.referenceKinectId} の保存済みの点群({reference.pointCloudSavedAt:yyyy-MM-dd HH:mm})を基準にします。このPCにつないでいなくても位置合わせできます。";
        else
            state = $"この実験には Kinect {settings.referenceKinectId} の点群がまだないので、このPCにつないでその場で撮ります。撮って保存すると他のPCにも配られます。";

        EditorGUILayout.LabelField(state, EditorStyles.wordWrappedMiniLabel);
    }

    // ------------------------------------------------------------
    // 6. シーン
    // ------------------------------------------------------------

    private void DrawScenes()
    {
        SceneButton("部屋とキネクトの位置合わせ", "0HomeSetup: 点群を撮って位置合わせし、Spaceキーで Env に保存する", SetupScene);
        SceneButton("記録(このPCのキネクトだけ)", "1HeadDirRecording: Spaceキーで記録の開始・終了", RecordingScene);
        SceneButton("記録(複数のPCで同時に)", "1HeadDirRecordingMulti: 同期ケーブルで全PCの記録をそろえる", RecordingMultiScene);

        EditorGUILayout.Space(4);

        if (GUILayout.Button(Application.isPlaying ? "■ 停止" : "▶ 再生", bigButton))
            EditorApplication.isPlaying = !EditorApplication.isPlaying;
    }

    private void SceneButton(string title, string description, string path)
    {
        bool isOpen = EditorSceneManager.GetActiveScene().path == path;

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(Application.isPlaying || isOpen))
            {
                if (GUILayout.Button(isOpen ? $"{title}(開いています)" : title, GUILayout.Width(220), GUILayout.Height(24)) &&
                    EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    EditorSceneManager.OpenScene(path);
            }

            EditorGUILayout.LabelField(description, EditorStyles.wordWrappedMiniLabel);
        }
    }

    // ------------------------------------------------------------
    // 7. 同期
    // ------------------------------------------------------------

    private void DrawSync(HomeSyncSettings settings)
    {
        if (settings.role == HomeSyncRole.Control)
        {
            HomeSyncServer server = HomeSyncService.Server;

            if (server == null)
            {
                EditorGUILayout.HelpBox("制御PCとして動いていません。", MessageType.Warning);
            }
            else
            {
                List<HomeSyncServer.ClientInfo> clients = server.GetClients();

                if (clients.Count == 0)
                    EditorGUILayout.LabelField("まだ録画PCがつながっていません。録画PCでこの画面を開き、役割を「録画PC」にしてください。", EditorStyles.wordWrappedMiniLabel);

                foreach (HomeSyncServer.ClientInfo info in clients)
                {
                    double seconds = (DateTime.Now - info.lastSeen).TotalSeconds;
                    string alive = seconds < 10 ? "● 接続中" : $"○ {seconds:F0}秒前まで接続";
                    EditorGUILayout.LabelField($"{info.name} ({info.address})  {alive}", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(info.status ?? "", EditorStyles.wordWrappedMiniLabel);
                }
            }
        }
        else
        {
            HomeSyncClient client = HomeSyncService.Client;

            if (client == null)
            {
                EditorGUILayout.HelpBox("録画PCとして動いていません。", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(client.Status, client.Connected ? MessageType.Info : MessageType.Warning);
                EditorGUILayout.LabelField("制御PC", client.Connected ? client.ServerDescription : "(未接続)");
                EditorGUILayout.LabelField("最後に同期した時刻", string.IsNullOrEmpty(client.LastSyncedAt) ? "-" : client.LastSyncedAt);

                if (GUILayout.Button("今すぐ同期"))
                    client.SyncNow();
            }
        }

        showLogs = EditorGUILayout.Foldout(showLogs, "ログ", true);

        if (showLogs)
        {
            for (int i = HomeSyncService.RecentLogs.Count - 1; i >= Mathf.Max(0, HomeSyncService.RecentLogs.Count - 50); i--)
                EditorGUILayout.LabelField(HomeSyncService.RecentLogs[i], EditorStyles.wordWrappedMiniLabel);
        }
    }
}
