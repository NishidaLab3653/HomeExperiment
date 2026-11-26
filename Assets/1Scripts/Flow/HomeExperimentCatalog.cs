using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// コントロールパネル用: データの場所にある実験の一覧と、それぞれの Env の中身(部屋・キネクトの位置・点群)をまとめる。
// 新しい実験を作るとき、既存の実験から部屋・キネクトの位置・点群を引き継ぐ。
public static class HomeExperimentCatalog
{
    public class KinectEnv
    {
        public string kinectId;
        public bool hasPose;
        public string serialNumber = "";
        public bool hasPointCloud;
        public DateTime pointCloudSavedAt;
    }

    public class ExperimentSummary
    {
        public string name;
        public bool hasEnv;
        public int roomObjectCount;
        public int targetGroupCount;
        public string roomSavedAt = "";
        public int subjectCount;
        public readonly SortedDictionary<string, KinectEnv> kinects = new SortedDictionary<string, KinectEnv>();

        public KinectEnv Kinect(string id) => kinects.TryGetValue(id, out KinectEnv info) ? info : null;

        // 「部屋 2個 / 位置 A B / 点群 A」のような1行の説明
        public string Describe()
        {
            if (!hasEnv)
                return "Env がまだありません(部屋とキネクトの位置合わせが未実施)";

            List<string> poses = new List<string>();
            List<string> clouds = new List<string>();

            foreach (KinectEnv kinect in kinects.Values)
            {
                if (kinect.hasPose)
                    poses.Add(kinect.kinectId);

                if (kinect.hasPointCloud)
                    clouds.Add(kinect.kinectId);
            }

            string room = roomObjectCount > 0 ? $"部屋 {roomObjectCount}個" : "部屋なし";

            if (targetGroupCount > 0)
                room += $"・対象物体 {targetGroupCount}個";

            return $"{room} / 位置 {Join(poses)} / 点群 {Join(clouds)} / 実験対象者 {subjectCount}人";
        }

        private static string Join(List<string> ids) => ids.Count == 0 ? "なし" : string.Join(" ", ids);
    }

    public static List<ExperimentSummary> List(string rootDirectory)
    {
        List<ExperimentSummary> result = new List<ExperimentSummary>();
        string root = HomeExperimentPaths.ResolveRootDirectory(rootDirectory);

        if (!Directory.Exists(root))
            return result;

        foreach (string name in HomeExperimentPaths.GetChildDirectoryNames(root))
            result.Add(Summarize(rootDirectory, name));

        return result;
    }

    public static ExperimentSummary Summarize(string rootDirectory, string experimentName)
    {
        ExperimentSummary summary = new ExperimentSummary { name = experimentName };

        if (!HomeExperimentPaths.IsValidFolderName(experimentName, out _))
            return summary;

        string envDir = HomeExperimentPaths.GetEnvDirectory(rootDirectory, experimentName);
        summary.hasEnv = Directory.Exists(envDir) && Directory.GetFiles(envDir, "*", SearchOption.AllDirectories).Length > 0;
        summary.subjectCount = HomeExperimentPaths.GetChildDirectoryNames(
            HomeExperimentPaths.GetSkeletonRootDirectory(rootDirectory, experimentName)).Length;

        if (!summary.hasEnv)
            return summary;

        HomeRoomObjectList rooms = Read<HomeRoomObjectList>(HomeExperimentPaths.GetRoomObjectsPath(rootDirectory, experimentName));

        if (rooms != null)
        {
            summary.roomObjectCount = rooms.objects.Count;
            summary.roomSavedAt = rooms.savedAt ?? "";
        }

        HomeRoomObjectList targets = Read<HomeRoomObjectList>(HomeExperimentPaths.GetTargetObjectsPath(rootDirectory, experimentName));

        if (targets != null)
        {
            HashSet<string> groups = new HashSet<string>();

            foreach (HomeRoomObjectData data in targets.objects)
                groups.Add(data.group);

            summary.targetGroupCount = groups.Count;
        }

        foreach (string path in Directory.GetFiles(envDir, "Kinect*_*"))
        {
            string file = Path.GetFileName(path);
            int underscore = file.IndexOf('_');

            if (underscore <= "Kinect".Length)
                continue;

            string id = file.Substring("Kinect".Length, underscore - "Kinect".Length);
            KinectEnv kinect = summary.Kinect(id);

            if (kinect == null)
            {
                kinect = new KinectEnv { kinectId = id };
                summary.kinects[id] = kinect;
            }

            if (file.EndsWith("_Transform.json", StringComparison.OrdinalIgnoreCase))
            {
                kinect.hasPose = true;
                kinect.serialNumber = Read<HomeKinectTransformData>(path)?.serialNumber ?? "";
            }
            else if (file.EndsWith("_PointCloud.ply", StringComparison.OrdinalIgnoreCase))
            {
                kinect.hasPointCloud = true;
                kinect.pointCloudSavedAt = File.GetLastWriteTime(path);
            }
        }

        return summary;
    }

    // 新しい実験のフォルダ(<データの場所>/<名前>/Env)を作る。copyFrom を指定すると、そこから選んだものを Env にコピーする
    public static bool Create(string rootDirectory, string experimentName, string copyFrom,
                              bool copyRoom, bool copyPoses, bool copyPointClouds, out string message)
    {
        if (!HomeExperimentPaths.IsValidRootDirectory(rootDirectory, out message) ||
            !HomeExperimentPaths.IsValidFolderName(experimentName, out message))
            return false;

        string experimentDir = HomeExperimentPaths.GetExperimentDirectory(rootDirectory, experimentName);

        if (Directory.Exists(experimentDir))
        {
            message = $"同じ名前の実験がすでにあります: {experimentName}";
            return false;
        }

        string envDir = HomeExperimentPaths.GetEnvDirectory(rootDirectory, experimentName);
        Directory.CreateDirectory(envDir);
        int copied = 0;

        if (!string.IsNullOrEmpty(copyFrom))
        {
            string sourceEnv = HomeExperimentPaths.GetEnvDirectory(rootDirectory, copyFrom);

            if (Directory.Exists(sourceEnv))
            {
                foreach (string path in Directory.GetFiles(sourceEnv, "*", SearchOption.AllDirectories))
                {
                    string relative = HomeSyncProtocol.ToRelative(sourceEnv, path);
                    string file = Path.GetFileName(relative);

                    if (!HomeSyncProtocol.IsSyncTarget(relative))
                        continue;

                    bool isRoom = file == HomeExperimentPaths.RoomObjectsFileName ||
                                  file == HomeExperimentPaths.TargetObjectsFileName ||
                                  relative.StartsWith(HomeExperimentPaths.MeshFolderName + "/", StringComparison.Ordinal);
                    bool isPose = file.StartsWith("Kinect", StringComparison.Ordinal) && file.EndsWith("_Transform.json", StringComparison.Ordinal);
                    bool isCloud = file.StartsWith("Kinect", StringComparison.Ordinal) && file.EndsWith("_PointCloud.ply", StringComparison.Ordinal);

                    if ((isRoom && copyRoom) || (isPose && copyPoses) || (isCloud && copyPointClouds))
                    {
                        string target = Path.Combine(envDir, relative.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(path, target);
                        copied++;
                    }
                }
            }
        }

        // 引き継いだ RoomObjects.json などの実験の名前を、新しい名前に書き換える
        foreach (string file in new[] { HomeExperimentPaths.RoomObjectsFileName, HomeExperimentPaths.TargetObjectsFileName })
        {
            string path = Path.Combine(envDir, file);
            HomeRoomObjectList list = Read<HomeRoomObjectList>(path);

            if (list != null)
            {
                list.experimentName = experimentName;
                File.WriteAllText(path, JsonUtility.ToJson(list, true));
            }
        }

        HomeExperimentPaths.RefreshAssetDatabase();
        message = string.IsNullOrEmpty(copyFrom)
            ? $"実験 {experimentName} を作りました。"
            : $"実験 {experimentName} を作り、{copyFrom} から {copied} 個のファイルを引き継ぎました。";
        return true;
    }

    private static T Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonUtility.FromJson<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

// このPCにつながっている Azure Kinect を調べる(デバイス番号とシリアル番号)
public static class HomeKinectDetector
{
    public class Device
    {
        public int index;
        // 開けなかった(他のアプリやプレイ中のシーンが使っている)ときは null
        public string serialNumber;
    }

    public static List<Device> Detect(out string error)
    {
        List<Device> result = new List<Device>();
        error = null;

        try
        {
            int count = Microsoft.Azure.Kinect.Sensor.Device.GetInstalledCount();

            for (int i = 0; i < count; i++)
            {
                Device device = new Device { index = i };

                try
                {
                    using (Microsoft.Azure.Kinect.Sensor.Device opened = Microsoft.Azure.Kinect.Sensor.Device.Open(i))
                        device.serialNumber = opened.SerialNum;
                }
                catch (Exception)
                {
                    device.serialNumber = null;
                }

                result.Add(device);
            }
        }
        catch (Exception e)
        {
            error = $"キネクトを調べられませんでした: {e.Message}";
        }

        return result;
    }
}
