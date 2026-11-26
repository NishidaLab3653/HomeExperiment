using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// 同期をエディタで動かし続ける部分。役割(HomeSyncSettings.role)に応じて、
// 制御PCなら HomeSyncServer、録画PCなら HomeSyncClient を起動する。
// プレイ開始やスクリプトの再コンパイルで止まっても、自動で起動し直す。
[InitializeOnLoad]
public static class HomeSyncService
{
    private static HomeSyncServer server;
    private static HomeSyncClient client;
    private static long publishedRevision = -1;
    private static string publishedExperiment;
    private static string publishedSubject;

    public static HomeSyncServer Server => server;
    public static HomeSyncClient Client => client;
    public static string StartError { get; private set; }
    public static readonly List<string> RecentLogs = new List<string>();

    static HomeSyncService()
    {
        AssemblyReloadEvents.beforeAssemblyReload += Stop;
        EditorApplication.quitting += Stop;
        EditorApplication.update += Update;
        EditorApplication.delayCall += Restart;
    }

    public static void Restart()
    {
        Stop();
        StartError = null;

        HomeSyncSettings settings = HomeSyncSettings.Current;
        _ = HomeSyncPaths.ProjectRoot;

        if (settings.role == HomeSyncRole.Off)
            return;

        if (!HomeExperimentPaths.IsValidRootDirectory(settings.dataRootDirectory, out string error))
        {
            StartError = $"データの場所が正しくありません。{error}";
            return;
        }

        string dataRoot = HomeExperimentPaths.ResolveRootDirectory(settings.dataRootDirectory);

        if (settings.role == HomeSyncRole.Control)
        {
            server = new HomeSyncServer(settings.DisplayName, settings.port, dataRoot, HomeSyncPaths.ProjectRoot);
            server.SetSession(HomeSession.Current);
            publishedRevision = HomeSession.Current.revision;

            if (!server.Start())
            {
                StartError = server.Error;
                server = null;
            }
        }
        else
        {
            client = new HomeSyncClient(settings.DisplayName, settings.controlAddress, settings.port, dataRoot, HomeSyncPaths.ProjectRoot);
            client.Start();
        }
    }

    public static void Stop()
    {
        server?.Stop();
        client?.Stop();
        server = null;
        client = null;
    }

    private static void Update()
    {
        if (server != null)
        {
            // 制御PCでセッションが変わったら(同期ウィンドウ・別のエディタから)配る内容を差し替える
            HomeSessionData session = HomeSession.Current;

            if (session.revision != publishedRevision || session.experimentName != publishedExperiment || session.subjectName != publishedSubject)
            {
                server.SetSession(session);
                publishedRevision = session.revision;
                publishedExperiment = session.experimentName;
                publishedSubject = session.subjectName;
            }

            Drain(server.Logs);
        }

        if (client != null)
        {
            Drain(client.Logs);

            if (client.ConsumeRefreshRequest())
                AssetDatabase.Refresh();
        }
    }

    private static void Drain(System.Collections.Concurrent.ConcurrentQueue<string> logs)
    {
        bool any = false;

        while (logs.TryDequeue(out string message))
        {
            Debug.Log($"[HomeSync] {message}");
            RecentLogs.Add($"{DateTime.Now:HH:mm:ss} {message}");
            any = true;
        }

        if (RecentLogs.Count > 200)
            RecentLogs.RemoveRange(0, RecentLogs.Count - 200);

        if (any)
            HomeSyncWindow.RepaintOpenWindows();
    }
}

// メニューの Window > Home Experiment Sync。このPCの役割の設定と、同期の状態の確認・セッションの配信を行う。
public class HomeSyncWindow : EditorWindow
{
    private static readonly string[] RoleLabels = { "使わない", "制御PC(配る側)", "録画PC(受け取る側)" };

    private HomeSyncSettings editing;
    private string editExperiment;
    private string editSubject;
    private Vector2 scroll;
    private Vector2 logScroll;
    private double nextRepaint;

    [MenuItem("Window/Home Experiment Sync")]
    public static void Open()
    {
        GetWindow<HomeSyncWindow>("Home Sync").Show();
    }

    public static void RepaintOpenWindows()
    {
        foreach (HomeSyncWindow window in Resources.FindObjectsOfTypeAll<HomeSyncWindow>())
            window.Repaint();
    }

    private void OnEnable()
    {
        editing = HomeSyncSettings.Current.Clone();
        editExperiment = HomeSession.Current.experimentName;
        editSubject = HomeSession.Current.subjectName;
    }

    private void Update()
    {
        // 接続状態などをこまめに表示し直す
        if (EditorApplication.timeSinceStartup >= nextRepaint)
        {
            nextRepaint = EditorApplication.timeSinceStartup + 1.0;
            Repaint();
        }
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        DrawSettings();
        EditorGUILayout.Space(8);

        HomeSyncRole role = HomeSyncSettings.Current.role;

        if (!string.IsNullOrEmpty(HomeSyncService.StartError))
            EditorGUILayout.HelpBox(HomeSyncService.StartError, MessageType.Error);

        if (role == HomeSyncRole.Control)
            DrawControl();
        else if (role == HomeSyncRole.Recorder)
            DrawRecorder();
        else
            EditorGUILayout.HelpBox("同期を使っていません。計測のシーンは、今までどおりインスペクターの実験の名前・実験対象者・データの場所を使います。", MessageType.Info);

        EditorGUILayout.Space(8);
        DrawLogs();
        EditorGUILayout.EndScrollView();
    }

    private void DrawSettings()
    {
        EditorGUILayout.LabelField("このPCの設定", EditorStyles.boldLabel);

        editing.role = (HomeSyncRole)EditorGUILayout.Popup("役割", (int)editing.role, RoleLabels);
        editing.pcName = EditorGUILayout.TextField(new GUIContent("このPCの名前", "空ならコンピューター名。制御PCの一覧に表示する。"), editing.pcName);

        using (new EditorGUILayout.HorizontalScope())
        {
            editing.dataRootDirectory = EditorGUILayout.TextField(
                new GUIContent("データの場所", "空なら既定の Assets/Data/HomeExperiment。同期を使うと、計測のシーンはインスペクターの Data Root Directory の代わりにここを使う。"),
                editing.dataRootDirectory);

            if (GUILayout.Button("選ぶ", GUILayout.Width(44)))
            {
                string picked = EditorUtility.OpenFolderPanel("データの場所", HomeExperimentPaths.ResolveRootDirectory(editing.dataRootDirectory), "");

                if (!string.IsNullOrEmpty(picked))
                    editing.dataRootDirectory = picked.Replace('/', '\\');
            }
        }

        if (editing.role == HomeSyncRole.Recorder)
            editing.controlAddress = EditorGUILayout.TextField(new GUIContent("制御PCのアドレス", "空なら同じネットワークの中を自動で探す。見つからないときは制御PCのIPアドレスを入れる。"), editing.controlAddress);

        editing.port = EditorGUILayout.IntField(new GUIContent("ポート", $"全PCで同じにする。既定 {HomeSyncProtocol.DefaultPort}(探索には {HomeSyncProtocol.DiscoveryPort} も使う)"), editing.port);

        bool changed = JsonUtility.ToJson(editing) != JsonUtility.ToJson(HomeSyncSettings.Current);

        using (new EditorGUI.DisabledScope(!changed))
        {
            if (GUILayout.Button(changed ? "設定を保存して同期をやり直す" : "設定は保存済み"))
            {
                HomeSyncSettings.Save(editing);
                editing = HomeSyncSettings.Current.Clone();
                HomeSyncService.Restart();
                GUI.FocusControl(null);
            }
        }
    }

    private void DrawControl()
    {
        HomeSyncServer server = HomeSyncService.Server;
        EditorGUILayout.LabelField("制御PC", EditorStyles.boldLabel);

        if (server == null)
        {
            EditorGUILayout.HelpBox("制御PCとして動いていません。", MessageType.Warning);
            return;
        }

        EditorGUILayout.LabelField("このPCのアドレス", string.Join(", ", HomeSyncProtocol.GetLocalAddresses()), EditorStyles.wordWrappedLabel);

        // セッション
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("実験の名前・実験対象者を全PCに配る", EditorStyles.boldLabel);
        HomeSessionData session = HomeSession.Current;
        string dataRoot = HomeExperimentPaths.ResolveRootDirectory(HomeSyncSettings.Current.dataRootDirectory);

        using (new EditorGUILayout.HorizontalScope())
        {
            editExperiment = EditorGUILayout.TextField("実験の名前", editExperiment);
            ShowFolderMenu(dataRoot, name => editExperiment = name);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            editSubject = EditorGUILayout.TextField("実験対象者", editSubject);

            if (HomeExperimentPaths.IsValidFolderName(editExperiment, out _))
                ShowFolderMenu(HomeExperimentPaths.GetSkeletonRootDirectory(HomeSyncSettings.Current.dataRootDirectory, editExperiment), name => editSubject = name);
        }

        bool sessionChanged = editExperiment != session.experimentName || editSubject != session.subjectName;
        string experimentError = null;
        string subjectError = null;
        bool valid = HomeExperimentPaths.IsValidFolderName(editExperiment, out experimentError) &&
                     (string.IsNullOrWhiteSpace(editSubject) || HomeExperimentPaths.IsValidFolderName(editSubject, out subjectError));

        if (!valid)
            EditorGUILayout.HelpBox(experimentError ?? subjectError, MessageType.Warning);

        using (new EditorGUI.DisabledScope(!sessionChanged || !valid))
        {
            if (GUILayout.Button(sessionChanged ? "全PCに配る" : $"配信済み(#{session.revision} {session.updatedAt})"))
            {
                HomeSession.Publish(editExperiment, editSubject, HomeSyncSettings.Current.DisplayName);
                GUI.FocusControl(null);
            }
        }

        if (Application.isPlaying && sessionChanged && editExperiment != session.experimentName)
            EditorGUILayout.HelpBox("プレイ中に実験の名前を変えた場合、各PCでプレイし直すまで新しい実験の Env は読み込まれません(記録も始められません)。", MessageType.Info);

        // 録画PCの一覧
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("つながっている録画PC", EditorStyles.boldLabel);
        List<HomeSyncServer.ClientInfo> clients = server.GetClients();

        if (clients.Count == 0)
            EditorGUILayout.HelpBox("まだ録画PCがつながっていません。録画PCの同期ウィンドウで役割を「録画PC」にしてください。", MessageType.Info);

        foreach (HomeSyncServer.ClientInfo info in clients)
        {
            double seconds = (DateTime.Now - info.lastSeen).TotalSeconds;
            string alive = seconds < 10 ? "接続中" : $"{seconds:F0}秒前";

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField($"{info.name} ({info.address}) {alive}", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(info.status ?? "", EditorStyles.wordWrappedMiniLabel);
            }
        }
    }

    private void DrawRecorder()
    {
        HomeSyncClient client = HomeSyncService.Client;
        EditorGUILayout.LabelField("録画PC", EditorStyles.boldLabel);

        if (client == null)
        {
            EditorGUILayout.HelpBox("録画PCとして動いていません。", MessageType.Warning);
            return;
        }

        EditorGUILayout.HelpBox(client.Status, client.Connected ? MessageType.Info : MessageType.Warning);
        EditorGUILayout.LabelField("制御PC", client.Connected ? client.ServerDescription : "(未接続)");
        EditorGUILayout.LabelField("最後に同期した時刻", string.IsNullOrEmpty(client.LastSyncedAt) ? "-" : client.LastSyncedAt);

        HomeSessionData session = HomeSession.Current;
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("制御PCから受け取った値", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("実験の名前", string.IsNullOrEmpty(session.experimentName) ? "(未受信)" : session.experimentName);
        EditorGUILayout.LabelField("実験対象者", string.IsNullOrEmpty(session.subjectName) ? "(未受信)" : session.subjectName);

        if (GUILayout.Button("今すぐ同期"))
            client.SyncNow();
    }

    private void DrawLogs()
    {
        EditorGUILayout.LabelField("ログ", EditorStyles.boldLabel);
        logScroll = EditorGUILayout.BeginScrollView(logScroll, GUILayout.Height(160));

        for (int i = HomeSyncService.RecentLogs.Count - 1; i >= 0; i--)
            EditorGUILayout.LabelField(HomeSyncService.RecentLogs[i], EditorStyles.wordWrappedMiniLabel);

        EditorGUILayout.EndScrollView();
    }

    private static void ShowFolderMenu(string parent, Action<string> onPick)
    {
        if (!GUILayout.Button("▼", GUILayout.Width(24)))
            return;

        GenericMenu menu = new GenericMenu();
        string[] names = HomeExperimentPaths.GetChildDirectoryNames(parent);

        if (names.Length == 0)
            menu.AddDisabledItem(new GUIContent("(フォルダがありません)"));

        foreach (string name in names)
            menu.AddItem(new GUIContent(name), false, () => onPick(name));

        menu.ShowAsContext();
    }
}
