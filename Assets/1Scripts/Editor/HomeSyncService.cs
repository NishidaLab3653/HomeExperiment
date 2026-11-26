using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// 同期をエディタで動かし続ける部分。このPCの役割(HomeSyncSettings.role)に応じて、
// 制御PCなら HomeSyncServer、録画PCなら HomeSyncClient を起動する(「このPCだけ」「使わない」ではネットワークを使わない)。
// プレイ開始やスクリプトの再コンパイルで止まっても、自動で起動し直す。画面は HomeControlPanel。
[InitializeOnLoad]
public static class HomeSyncService
{
    private static HomeSyncServer server;
    private static HomeSyncClient client;
    private static long publishedRevision = -1;
    private static string publishedExperiment;
    private static string publishedSubject;
    private static string runningConfig;

    public static HomeSyncServer Server => server;
    public static HomeSyncClient Client => client;
    public static string StartError { get; private set; }
    public static readonly List<string> RecentLogs = new List<string>();

    static HomeSyncService()
    {
        AssemblyReloadEvents.beforeAssemblyReload += Stop;
        EditorApplication.quitting += Stop;
        EditorApplication.update += Update;
        EditorApplication.delayCall += () =>
        {
            Restart();

            // まだ一度も設定していないPCでは、最初にコントロールパネルを開く
            if (!Application.isBatchMode && !System.IO.File.Exists(HomeSyncPaths.SettingsPath))
                HomeControlPanel.Open();
        };
    }

    // ネットワークに関係する設定(役割・名前・データの場所・アドレス・ポート)が変わったときだけ起動し直す
    public static void RestartIfNeeded()
    {
        if (NetworkConfig(HomeSyncSettings.Current) != runningConfig)
            Restart();
    }

    private static string NetworkConfig(HomeSyncSettings s) =>
        $"{s.role}|{s.DisplayName}|{s.dataRootDirectory}|{s.controlAddress}|{s.port}";

    public static void Restart()
    {
        Stop();
        StartError = null;

        HomeSyncSettings settings = HomeSyncSettings.Current;
        _ = HomeSyncPaths.ProjectRoot;
        runningConfig = NetworkConfig(settings);

        if (!settings.UsesNetwork)
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
            // 制御PCでセッションが変わったら、配る内容を差し替える
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

    public static void AddLog(string message)
    {
        Debug.Log($"[HomeExperiment] {message}");
        RecentLogs.Add($"{DateTime.Now:HH:mm:ss} {message}");
        HomeControlPanel.RepaintOpenWindows();
    }

    private static void Drain(ConcurrentQueue<string> logs)
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
            HomeControlPanel.RepaintOpenWindows();
    }
}
