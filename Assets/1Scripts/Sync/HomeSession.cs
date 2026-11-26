using System;
using System.IO;
using UnityEngine;

// 複数のPCで共有する「実験の名前・実験対象者」(セッション)と、このPCだけの同期の設定。
//
// どちらも <プロジェクト>/UserSettings/ に置く(git では共有しない、PCごとのファイル)。
//   HomeExperimentSession.json … セッション。制御PCで決めて配信し、録画PCは同期で受け取って上書きする
//   HomeExperimentSync.json    … このPCの役割(制御PC / 録画PC / 使わない)、データの場所、制御PCのアドレスなど
//
// 計測のシーン(0HomeSetup / 1HeadDirRecording / 1HeadDirRecordingMulti)のコンポーネントは、
// Use Shared Session がオンで、このPCの役割が「使わない」以外なら、インスペクターの値ではなくここの値を使う
// (HomeSharedSession)。同期ウィンドウは メニューの Window > Home Experiment Sync。

[Serializable]
public class HomeSessionData
{
    public string experimentName = "";
    public string subjectName = "";
    // 配信するたびに 1 増える。録画PCはこれが変わったら受け取り直す
    public long revision;
    public string updatedAt = "";
    public string updatedBy = "";

    public HomeSessionData Clone() => (HomeSessionData)MemberwiseClone();
}

public enum HomeSyncRole
{
    // 同期を使わない(今までどおり、シーンのインスペクターの値を使う)
    Off,
    // 制御PC: セッションと Env を持ち、録画PCに配る
    Control,
    // 録画PC: 制御PCからセッションと Env を受け取り、自分で保存した Env を制御PCへ送る
    Recorder
}

[Serializable]
public class HomeSyncSettings
{
    public HomeSyncRole role = HomeSyncRole.Off;
    // このPCの名前(制御PCの一覧に表示する)。空ならコンピューター名
    public string pcName = "";
    // このPCのデータの場所。空なら既定の Assets/Data/HomeExperiment。例: D:\HomeExperiment
    public string dataRootDirectory = "";
    // 録画PCから見た制御PCのアドレス。空なら同じネットワーク内を自動で探す
    public string controlAddress = "";
    public int port = HomeSyncProtocol.DefaultPort;

    public string DisplayName => string.IsNullOrWhiteSpace(pcName) ? Environment.MachineName : pcName.Trim();

    public HomeSyncSettings Clone() => (HomeSyncSettings)MemberwiseClone();

    private static HomeSyncSettings current;
    private static DateTime currentStamp;
    private static float nextCheck;

    // メインスレッドから使う。ファイルが書き換わっていたら読み直す
    public static HomeSyncSettings Current
    {
        get
        {
            if (current == null || Time.realtimeSinceStartup >= nextCheck)
            {
                nextCheck = Time.realtimeSinceStartup + 0.5f;
                string path = HomeSyncPaths.SettingsPath;
                DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

                if (current == null || stamp != currentStamp)
                {
                    current = HomeSyncPaths.ReadJson<HomeSyncSettings>(path) ?? new HomeSyncSettings();
                    currentStamp = stamp;
                }
            }

            return current;
        }
    }

    public static void Save(HomeSyncSettings settings)
    {
        HomeSyncPaths.WriteJson(HomeSyncPaths.SettingsPath, settings);
        current = settings.Clone();
        currentStamp = File.GetLastWriteTimeUtc(HomeSyncPaths.SettingsPath);
    }
}

public static class HomeSession
{
    private static HomeSessionData current;
    private static DateTime currentStamp;
    private static float nextCheck;

    // メインスレッドから使う。同期でファイルが書き換わっていたら読み直す
    public static HomeSessionData Current
    {
        get
        {
            if (current == null || Time.realtimeSinceStartup >= nextCheck)
            {
                nextCheck = Time.realtimeSinceStartup + 0.25f;
                string path = HomeSyncPaths.SessionPath;
                DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

                if (current == null || stamp != currentStamp)
                {
                    current = HomeSyncPaths.ReadJson<HomeSessionData>(path) ?? new HomeSessionData();
                    currentStamp = stamp;
                }
            }

            return current;
        }
    }

    // 制御PCで実験の名前・実験対象者を変えて配信する(revision を 1 増やす)
    public static HomeSessionData Publish(string experimentName, string subjectName, string by)
    {
        HomeSessionData data = Current.Clone();
        data.experimentName = (experimentName ?? "").Trim();
        data.subjectName = (subjectName ?? "").Trim();
        data.revision++;
        data.updatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        data.updatedBy = by;
        HomeSyncPaths.WriteJson(HomeSyncPaths.SessionPath, data);
        current = data;
        currentStamp = File.GetLastWriteTimeUtc(HomeSyncPaths.SessionPath);
        return data;
    }
}

// 計測のシーンのコンポーネントが、インスペクターの値と共有セッションの値のどちらを使うかを決める
public static class HomeSharedSession
{
    public static bool IsActive(bool useSharedSession) =>
        useSharedSession && HomeSyncSettings.Current.role != HomeSyncRole.Off;

    public static string ExperimentName(bool useSharedSession, string inspectorValue)
    {
        if (IsActive(useSharedSession) && !string.IsNullOrWhiteSpace(HomeSession.Current.experimentName))
            return HomeSession.Current.experimentName;

        return inspectorValue;
    }

    public static string SubjectName(bool useSharedSession, string inspectorValue)
    {
        if (IsActive(useSharedSession) && !string.IsNullOrWhiteSpace(HomeSession.Current.subjectName))
            return HomeSession.Current.subjectName;

        return inspectorValue;
    }

    public static string DataRootDirectory(bool useSharedSession, string inspectorValue) =>
        IsActive(useSharedSession) ? HomeSyncSettings.Current.dataRootDirectory : inspectorValue;
}

public static class HomeSyncPaths
{
    private static string projectRoot;

    // <プロジェクト>(Assets の1つ上)。最初はメインスレッドから呼ぶこと(Application.dataPath を使うため)
    public static string ProjectRoot =>
        projectRoot ?? (projectRoot = Directory.GetParent(Application.dataPath).FullName);

    public static string UserSettingsDirectory => Path.Combine(ProjectRoot, "UserSettings");
    public static string SessionPath => Path.Combine(UserSettingsDirectory, "HomeExperimentSession.json");
    public static string SettingsPath => Path.Combine(UserSettingsDirectory, "HomeExperimentSync.json");
    public static string SyncStatePath => Path.Combine(UserSettingsDirectory, "HomeExperimentSyncState.json");

    public static T ReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonUtility.FromJson<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[HomeSync] 読み込めませんでした: {path}\n{e.Message}");
            return null;
        }
    }

    // 書きかけのファイルを読まれないよう、一時ファイルに書いてから置き換える
    public static void WriteJson(string path, object data)
    {
        WriteAllBytesAtomic(path, System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(data, true)));
    }

    public static void WriteAllBytesAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);

        if (File.Exists(path))
            File.Replace(temp, path, null);
        else
            File.Move(temp, path);
    }
}
