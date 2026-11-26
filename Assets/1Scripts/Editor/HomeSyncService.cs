using System;
using System.Collections.Concurrent;
using UnityEditor;
using UnityEngine;

// 同期をエディタで動かし続ける部分。このPCの役割(HomeSyncSettings.role)に応じて、
// 制御PCなら HomeSyncServer、録画PCなら HomeSyncClient を起動する(「このPCだけ」「使わない」ではネットワークを使わない)。
// プレイ開始やスクリプトの再コンパイルで止まっても、自動で起動し直す。
// 設定はシーンマネージャー(Assets/0Scenes/HomeSceneManager.unity)の画面で変える。状態は HomeSyncStatus を通して画面に渡す。
[InitializeOnLoad]
public static class HomeSyncService
{
    private const string ManagerScene = "Assets/0Scenes/HomeSceneManager.unity";

    private static HomeSyncServer server;
    private static HomeSyncClient client;
    private static long publishedRevision = -1;
    private static string publishedExperiment;
    private static string publishedSubject;
    private static string runningConfig;
    private static double nextSettingsCheck;

    static HomeSyncService()
    {
        AssemblyReloadEvents.beforeAssemblyReload += Stop;
        EditorApplication.quitting += Stop;
        EditorApplication.update += Update;
        EditorApplication.delayCall += Restart;
    }

    [MenuItem("HomeExperiment/シーンマネージャーで始める %#h", priority = 0)]
    public static void PlayFromManager()
    {
        if (EditorApplication.isPlaying)
        {
            EditorApplication.isPlaying = false;
            return;
        }

        if (!UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(ManagerScene);
        EditorApplication.isPlaying = true;
    }

    private static string NetworkConfig(HomeSyncSettings s) =>
        $"{s.role}|{s.DisplayName}|{s.dataRootDirectory}|{s.controlAddress}|{s.port}";

    public static void Restart()
    {
        Stop();
        HomeSyncStatus.Error = "";

        HomeSyncSettings settings = HomeSyncSettings.Current;
        _ = HomeSyncPaths.ProjectRoot;
        runningConfig = NetworkConfig(settings);

        if (!settings.UsesNetwork)
            return;

        if (!HomeExperimentPaths.IsValidRootDirectory(settings.dataRootDirectory, out string error))
        {
            HomeSyncStatus.Error = $"データの場所が正しくありません。{error}";
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
                HomeSyncStatus.Error = server.Error;
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
        HomeSyncStatus.Running = false;
        HomeSyncStatus.Connected = false;
    }

    private static void Update()
    {
        // 画面で役割・データの場所などが変わったら起動し直す
        if (EditorApplication.timeSinceStartup >= nextSettingsCheck)
        {
            nextSettingsCheck = EditorApplication.timeSinceStartup + 1.0;

            if (NetworkConfig(HomeSyncSettings.Current) != runningConfig)
                Restart();
        }

        HomeSyncStatus.Running = server != null || client != null;

        if (server != null)
        {
            // 制御PCでセッションが変わったら、配る内容を差し替える
            HomeSessionData session = HomeSession.Current;

            if (session.revision != publishedRevision || session.experimentName != publishedExperiment || session.subjectName != publishedSubject)
            {
                server.SetSession(session);
                publishedRevision = session.revision;
                publishedExperiment = session.experimentName;
                publishedSubject = session.subjectName;
            }

            HomeSyncStatus.Clients = server.GetClients().ConvertAll(c => new HomeSyncStatus.ClientInfo
            {
                name = c.name, address = c.address, status = c.status, lastSeen = c.lastSeen
            });
            Drain(server.Logs);
        }

        if (client != null)
        {
            HomeSyncStatus.Connected = client.Connected;
            HomeSyncStatus.Status = client.Status;
            HomeSyncStatus.ServerDescription = client.ServerDescription;
            HomeSyncStatus.LastSyncedAt = client.LastSyncedAt;

            if (HomeSyncStatus.SyncNowRequested)
            {
                HomeSyncStatus.SyncNowRequested = false;
                client.SyncNow();
            }

            Drain(client.Logs);

            if (client.ConsumeRefreshRequest())
                AssetDatabase.Refresh();
        }
    }

    private static void Drain(ConcurrentQueue<string> logs)
    {
        while (logs.TryDequeue(out string message))
        {
            Debug.Log($"[HomeSync] {message}");
            HomeSyncStatus.AddLog(message);
        }
    }
}
