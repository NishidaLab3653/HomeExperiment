using System;
using System.IO;
using UnityEngine;

// 複数のPCで共有する「実験の名前・実験対象者」(セッション)と、このPCだけの同期の設定。
//
// どちらも <プロジェクト>/UserSettings/ に置く(git では共有しない、PCごとのファイル)。
//   HomeExperimentSession.json … セッション。制御PCで決めて配信し、録画PCは同期で受け取って上書きする
//   HomeExperimentSync.json    … このPCの役割、データの場所、制御PCのアドレス、このPCで使うキネクト、位置合わせの基準など
//
// 計測のシーン(0HomeSetup / 1HeadDirRecording / 1HeadDirRecordingMulti)のコンポーネントは、
// Use Shared Session がオンで、このPCの役割が「使わない」以外なら、インスペクターの値ではなくここの値を使う
// (HomeSharedSession)。設定はメニューの HomeExperiment > コントロールパネル で行う。

[Serializable]
public class HomeSessionData
{
    public string experimentName = "";
    public string subjectName = "";
    // 配信するたびに 1 増える。録画PCはこれが変わったら受け取り直す
    public long revision;
    public string updatedAt = "";
    public string updatedBy = "";

    // キネクトの割り当て(制御PCで決めて全PCに配る)。どのキネクトを、どのPCの、どのキネクト本体で、どの同期モードで使うか
    public HomeKinectAssignment[] kinectAssignments = new HomeKinectAssignment[0];

    public bool HasKinectAssignments
    {
        get
        {
            if (kinectAssignments == null)
                return false;

            foreach (HomeKinectAssignment assignment in kinectAssignments)
            {
                if (assignment != null && assignment.IsAssigned)
                    return true;
            }

            return false;
        }
    }

    public HomeKinectAssignment FindAssignment(string kinectId)
    {
        if (kinectAssignments != null)
        {
            foreach (HomeKinectAssignment assignment in kinectAssignments)
            {
                if (assignment != null && assignment.kinectId == kinectId)
                    return assignment;
            }
        }

        return null;
    }

    public HomeSessionData Clone() => JsonUtility.FromJson<HomeSessionData>(JsonUtility.ToJson(this));
}

// キネクト1台の割り当て。録画PCは pcName が自分の名前のものだけを使う。
// デバイスの番号はUSBのつなぎ方で変わることがあるので、キネクト本体はシリアル番号で決める(番号は見つからなかったときの予備)
[Serializable]
public class HomeKinectAssignment
{
    public string kinectId;
    // 使うPCの名前(空なら使わない)
    public string pcName = "";
    public string serialNumber = "";
    public int deviceIndex;
    public HomeKinectDeviceIndex.SyncMode syncMode = HomeKinectDeviceIndex.SyncMode.Standalone;

    public bool IsAssigned => !string.IsNullOrWhiteSpace(pcName);

    public bool IsAssignedTo(string name) =>
        IsAssigned && string.Equals(pcName.Trim(), (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}

public enum HomeSyncRole
{
    // 同期を使わない(今までどおり、シーンのインスペクターの値を使う)
    Off,
    // 制御PC: セッションと Env を持ち、録画PCに配る
    Control,
    // 録画PC: 制御PCからセッションと Env を受け取り、自分で保存した Env を制御PCへ送る
    Recorder,
    // このPCだけで使う: ネットワークは使わず、コントロールパネルの設定だけを使う
    Single
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

    // このPCで使うキネクト(使うか・デバイス番号・同期ケーブルでの役割)。シーンの Kinects の表の代わりに使う
    public HomeKinectDeviceIndex[] kinects = HomeKinectDeviceIndex.CreateDefault();
    // キネクトのデバイス番号ごとのシリアル番号(コントロールパネルで調べたときの記録。表示用)
    public string[] detectedSerials = new string[0];

    // 位置合わせ(シーン0)の基準にするキネクトと、その点群をどこから取るか
    public string referenceKinectId = "A";
    public HomeEnvSetup.ReferencePointCloudSource referencePointCloudSource = HomeEnvSetup.ReferencePointCloudSource.SavedEnvIfExists;

    // 制御PCにキネクトをつながない(実験・部屋の管理だけをする)。オンのとき、計測のシーンではこのPCのキネクトを全部使わない
    public bool noKinectsHere;

    public string DisplayName => string.IsNullOrWhiteSpace(pcName) ? Environment.MachineName : pcName.Trim();

    // ネットワークを使う役割か
    public bool UsesNetwork => role == HomeSyncRole.Control || role == HomeSyncRole.Recorder;

    // 実験の名前・実験対象者をこのPCで決められるか(録画PCは制御PCから受け取るだけ)
    public bool CanEditSession => role == HomeSyncRole.Control || role == HomeSyncRole.Single;

    // このPCでキネクトを使うか(キネクトをつながない制御PCだけ false)
    public bool HasKinectsHere => !(role == HomeSyncRole.Control && noKinectsHere);

    // 部屋と物体の配置をこのPCで行うか(録画PCは制御PCから受け取るだけ)
    public bool CanEditRoom => role == HomeSyncRole.Control || role == HomeSyncRole.Single;

    public HomeSyncSettings Clone() => JsonUtility.FromJson<HomeSyncSettings>(JsonUtility.ToJson(this));

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

    // 制御PCでキネクトの割り当てを変えて配信する(revision を 1 増やす)
    public static HomeSessionData PublishKinectAssignments(HomeKinectAssignment[] assignments, string by)
    {
        HomeSessionData data = Current.Clone();
        data.kinectAssignments = JsonUtility.FromJson<HomeSessionData>(
            JsonUtility.ToJson(new HomeSessionData { kinectAssignments = assignments })).kinectAssignments;
        data.revision++;
        data.updatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        data.updatedBy = by;
        HomeSyncPaths.WriteJson(HomeSyncPaths.SessionPath, data);
        current = data;
        currentStamp = File.GetLastWriteTimeUtc(HomeSyncPaths.SessionPath);
        return data;
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

    public static HomeKinectDeviceIndex[] KinectTable(bool useSharedSession, HomeKinectDeviceIndex[] inspectorValue) =>
        IsActive(useSharedSession) ? ResolveKinectTable(HomeSyncSettings.Current, HomeSession.Current) : inspectorValue;

    // このPCで使うキネクトの表。
    //   このPCだけ      … このPCの設定の表
    //   制御PC・録画PC … 制御PCが配った割り当てのうち、このPCの名前のものだけ使う。キネクト本体はシリアル番号でデバイスの番号に直す
    //                     (キネクトをつながない制御PCは全部使わない。設定ファイルの表は書き換えない)
    public static HomeKinectDeviceIndex[] ResolveKinectTable(HomeSyncSettings settings, HomeSessionData session)
    {
        HomeKinectDeviceIndex[] table = settings.kinects != null && settings.kinects.Length > 0
            ? settings.kinects
            : HomeKinectDeviceIndex.CreateDefault();

        if (!settings.UsesNetwork)
            return table;

        string[] serials = settings.detectedSerials ?? new string[0];
        HomeKinectDeviceIndex[] result = new HomeKinectDeviceIndex[table.Length];

        for (int i = 0; i < table.Length; i++)
        {
            string id = table[i].kinectId;
            HomeKinectAssignment assignment = session?.FindAssignment(id);
            bool mine = settings.HasKinectsHere && assignment != null && assignment.IsAssignedTo(settings.DisplayName);
            int device = assignment != null ? assignment.deviceIndex : table[i].deviceIndex;

            if (mine && !string.IsNullOrEmpty(assignment.serialNumber))
            {
                int found = Array.IndexOf(serials, assignment.serialNumber);

                if (found >= 0)
                    device = found;
            }

            result[i] = new HomeKinectDeviceIndex(id, Mathf.Max(0, device))
            {
                use = mine,
                syncMode = assignment != null ? assignment.syncMode : HomeKinectDeviceIndex.SyncMode.Standalone
            };
        }

        return result;
    }
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
