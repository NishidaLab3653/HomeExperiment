using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// 録画PCで動かす同期クライアント。数秒ごとに制御PC(HomeSyncServer)に問い合わせて、
//   1. セッション(実験の名前・実験対象者)が変わっていたら受け取る
//   2. その実験の Env を制御PCとそろえる(相手だけが変えたファイルは受け取り、自分だけが変えたファイルは送る)
//   3. Env が参照している部屋のモデルなどがこのプロジェクトに無ければ、制御PCから受け取る
// を行う。スレッドで動くので Unity の API は使わない(アセットの読み込み直しは RefreshRequested を見てメインスレッドで行う)。
//
// Env のそろえ方(3者比較): 前回そろえたときの各ファイルのハッシュを UserSettings/HomeExperimentSyncState.json に覚えておき、
//   制御PCだけが変えた → 受け取る / このPCだけが変えた → 送る(シーン0で保存した自分のキネクトの位置姿勢など) /
//   両方が変えた → 制御PCの方を使い、このPCのファイルは <名前>.conflict-<日時> として残す。
public class HomeSyncClient
{
    private const int PollIntervalMs = 2000;
    private const int TimeoutMs = 10000;

    [Serializable]
    private class SyncState
    {
        public List<ExperimentState> experiments = new List<ExperimentState>();
    }

    [Serializable]
    private class ExperimentState
    {
        public string experimentName;
        public List<HomeSyncFileEntry> files = new List<HomeSyncFileEntry>();
    }

    private readonly string clientName;
    private readonly string manualAddress;
    private readonly int port;
    private readonly string dataRoot;
    private readonly string projectRoot;
    private readonly HomeSyncHashCache hashCache = new HomeSyncHashCache();
    private readonly HomeSyncAssetIndex assetIndex;
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private readonly HashSet<string> failedAssets = new HashSet<string>();

    private Thread thread;
    private volatile bool running;
    private IPEndPoint server;
    private string lastServerEnvHash;
    private string lastLocalEnvHash;
    private string lastExperiment;
    private int refreshRequested;

    public ConcurrentQueue<string> Logs { get; } = new ConcurrentQueue<string>();

    // 同期ウィンドウに表示する状態
    public volatile string Status = "制御PCを探しています…";
    public volatile string ServerDescription = "";
    public volatile string LastSyncedAt = "";
    public volatile bool Connected;

    public HomeSyncClient(string clientName, string manualAddress, int port, string dataRoot, string projectRoot)
    {
        this.clientName = clientName;
        this.manualAddress = manualAddress?.Trim() ?? "";
        this.port = port;
        this.dataRoot = dataRoot;
        this.projectRoot = projectRoot;
        assetIndex = new HomeSyncAssetIndex(projectRoot);
    }

    public void Start()
    {
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Name = "HomeSyncClient" };
        thread.Start();
    }

    public void Stop()
    {
        running = false;
        wake.Set();
    }

    // 「今すぐ同期」: 次の問い合わせを待たずに、Env も一覧から比べ直す
    public void SyncNow()
    {
        lastServerEnvHash = null;
        lastLocalEnvHash = null;
        lock (failedAssets)
            failedAssets.Clear();
        wake.Set();
    }

    // アセットを受け取ったので、メインスレッドで AssetDatabase.Refresh してほしいか
    public bool ConsumeRefreshRequest() => Interlocked.Exchange(ref refreshRequested, 0) == 1;

    private void Log(string message) => Logs.Enqueue(message);

    private void Loop()
    {
        while (running)
        {
            try
            {
                PollOnce();
            }
            catch (Exception e)
            {
                if (Connected)
                    Log($"制御PCとの通信が切れました: {e.Message}");

                Connected = false;
                server = null;
                Status = $"制御PCにつながっていません({e.Message})";
            }

            wake.WaitOne(PollIntervalMs);
        }
    }

    private void PollOnce()
    {
        if (server == null)
        {
            server = FindServer();

            if (server == null)
            {
                Status = string.IsNullOrEmpty(manualAddress)
                    ? "制御PCが見つかりません(同じネットワークにつながっているか、制御PCで同期ウィンドウが動いているか確認してください)"
                    : $"制御PC {manualAddress} が見つかりません";
                return;
            }
        }

        HomeSyncResponse status = Request(new HomeSyncRequest { type = HomeSyncProtocol.RequestStatus, clientStatus = Status }, null, out _);

        if (!Connected)
            Log($"制御PC {status.serverName} ({server.Address}) につながりました。");

        Connected = true;
        ServerDescription = $"{status.serverName} ({server.Address}:{server.Port})";

        // 1. セッション
        HomeSessionData local = HomeSyncPaths.ReadJson<HomeSessionData>(HomeSyncPaths.SessionPath) ?? new HomeSessionData();
        HomeSessionData remote = status.session ?? new HomeSessionData();

        if (remote.revision != local.revision || remote.experimentName != local.experimentName || remote.subjectName != local.subjectName)
        {
            HomeSyncPaths.WriteJson(HomeSyncPaths.SessionPath, remote);
            Log($"セッションを受け取りました: 実験 {Show(remote.experimentName)} / 実験対象者 {Show(remote.subjectName)}");
        }

        string experiment = remote.experimentName;

        if (!HomeExperimentPaths.IsValidFolderName(experiment, out _))
        {
            Status = "制御PCで実験の名前がまだ決められていません";
            return;
        }

        // 2. Env(どちらかが変わったときだけ一覧を比べる)
        string envDir = Path.Combine(dataRoot, experiment, HomeExperimentPaths.EnvFolderName);
        string localHash = HomeSyncProtocol.ManifestHash(HomeSyncProtocol.BuildManifest(envDir, hashCache));
        bool envChanged = experiment != lastExperiment || status.envHash != lastServerEnvHash || localHash != lastLocalEnvHash;

        if (envChanged)
        {
            int changes = SyncEnv(experiment, envDir);
            lastExperiment = experiment;
            lastServerEnvHash = Request(new HomeSyncRequest { type = HomeSyncProtocol.RequestStatus }, null, out _).envHash;
            lastLocalEnvHash = HomeSyncProtocol.ManifestHash(HomeSyncProtocol.BuildManifest(envDir, hashCache));

            if (changes > 0)
                LastSyncedAt = DateTime.Now.ToString("HH:mm:ss");
        }

        // 3. 部屋のモデルなど
        int missing = EnsureAssets(envDir);

        Status = missing > 0
            ? $"同期済み(実験 {experiment})。部屋のモデルなど {missing} 個を受け取れませんでした"
            : $"同期済み(実験 {experiment} / 実験対象者 {Show(remote.subjectName)})";

        if (string.IsNullOrEmpty(LastSyncedAt))
            LastSyncedAt = DateTime.Now.ToString("HH:mm:ss");
    }

    private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "(未入力)" : value;

    // ------------------------------------------------------------
    // Env
    // ------------------------------------------------------------

    private int SyncEnv(string experiment, string envDir)
    {
        HomeSyncResponse manifest = Request(new HomeSyncRequest { type = HomeSyncProtocol.RequestManifest, experiment = experiment }, null, out _);
        Dictionary<string, string> remote = ToMap(manifest.files);
        Dictionary<string, string> local = ToMap(HomeSyncProtocol.BuildManifest(envDir, hashCache));

        SyncState state = HomeSyncPaths.ReadJson<SyncState>(HomeSyncPaths.SyncStatePath) ?? new SyncState();
        ExperimentState experimentState = state.experiments.Find(e => e.experimentName == experiment);

        if (experimentState == null)
        {
            experimentState = new ExperimentState { experimentName = experiment };
            state.experiments.Add(experimentState);
        }

        Dictionary<string, string> baseline = ToMap(experimentState.files);
        HashSet<string> paths = new HashSet<string>(remote.Keys);
        paths.UnionWith(local.Keys);
        paths.UnionWith(baseline.Keys);

        int changes = 0;

        foreach (string path in paths)
        {
            remote.TryGetValue(path, out string r);
            local.TryGetValue(path, out string l);
            baseline.TryGetValue(path, out string b);

            if (r == l)
            {
                SetBaseline(baseline, path, r);
                continue;
            }

            if (!HomeSyncProtocol.TryResolveInside(envDir, path, out string fullPath))
                continue;

            if (l == b)
            {
                // 制御PCだけが変えた → 受け取る(制御PCで消えていれば消す)
                if (r == null)
                {
                    File.Delete(fullPath);
                    Log($"削除しました(制御PCで削除済み): {experiment}/Env/{path}");
                }
                else
                {
                    Download(experiment, path, fullPath);
                }

                SetBaseline(baseline, path, r);
            }
            else if (r == b)
            {
                // このPCだけが変えた → 送る(このPCで消していれば制御PCでも消す)
                if (l == null)
                {
                    Request(new HomeSyncRequest { type = HomeSyncProtocol.RequestDeleteFile, experiment = experiment, path = path }, null, out _);
                    Log($"制御PCでも削除しました: {experiment}/Env/{path}");
                }
                else
                {
                    Request(new HomeSyncRequest { type = HomeSyncProtocol.RequestPutFile, experiment = experiment, path = path }, File.ReadAllBytes(fullPath), out _);
                    Log($"制御PCへ送りました: {experiment}/Env/{path}");
                }

                SetBaseline(baseline, path, l);
            }
            else
            {
                // 両方が変えた → 制御PCの方を使い、このPCのファイルは残しておく
                if (l != null)
                {
                    string backup = $"{fullPath}.conflict-{DateTime.Now:yyyyMMdd_HHmmss}";
                    File.Copy(fullPath, backup, true);
                    Log($"制御PCとこのPCの両方で変わっていたため、制御PCの方を使います。このPCの分は {Path.GetFileName(backup)} に残しました: {experiment}/Env/{path}");
                }

                if (r == null)
                    File.Delete(fullPath);
                else
                    Download(experiment, path, fullPath);

                SetBaseline(baseline, path, r);
            }

            changes++;
        }

        experimentState.files.Clear();

        foreach (KeyValuePair<string, string> pair in baseline)
            experimentState.files.Add(new HomeSyncFileEntry { path = pair.Key, hash = pair.Value });

        HomeSyncPaths.WriteJson(HomeSyncPaths.SyncStatePath, state);

        if (changes > 0)
        {
            Log($"Env をそろえました: {experiment} (変更 {changes} 件)");

            // データの場所がプロジェクトの中(既定の Assets/Data/HomeExperiment)なら、Unity にも読み込み直してもらう
            if (dataRoot.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                Interlocked.Exchange(ref refreshRequested, 1);
        }

        return changes;
    }

    private void Download(string experiment, string path, string fullPath)
    {
        Request(new HomeSyncRequest { type = HomeSyncProtocol.RequestGetFile, experiment = experiment, path = path }, null, out byte[] body);
        HomeSyncPaths.WriteAllBytesAtomic(fullPath, body);
        Log($"受け取りました: {experiment}/Env/{path}");
    }

    private static Dictionary<string, string> ToMap(List<HomeSyncFileEntry> files)
    {
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (HomeSyncFileEntry file in files)
            map[file.path] = file.hash;

        return map;
    }

    private static void SetBaseline(Dictionary<string, string> baseline, string path, string hash)
    {
        if (hash == null)
            baseline.Remove(path);
        else
            baseline[path] = hash;
    }

    // ------------------------------------------------------------
    // 部屋のモデルなどのアセット
    // ------------------------------------------------------------

    // Env が参照しているアセットのうち、このプロジェクトに無いものを制御PCから受け取る。受け取れなかった個数を返す
    private int EnsureAssets(string envDir)
    {
        HashSet<string> guids = new HashSet<string>();
        CollectGuids(Path.Combine(envDir, HomeExperimentPaths.RoomObjectsFileName), guids);
        CollectGuids(Path.Combine(envDir, HomeExperimentPaths.TargetObjectsFileName), guids);

        int missing = 0;
        bool indexRefreshed = false;

        foreach (string guid in guids)
        {
            if (assetIndex.TryGetPath(guid, out _))
                continue;

            if (!indexRefreshed)
            {
                // 前回から手で追加したアセットもあるかもしれないので、一度だけ調べ直す
                assetIndex.Invalidate();
                indexRefreshed = true;

                if (assetIndex.TryGetPath(guid, out _))
                    continue;
            }

            lock (failedAssets)
            {
                if (failedAssets.Contains(guid))
                {
                    missing++;
                    continue;
                }
            }

            if (!FetchAsset(guid))
            {
                lock (failedAssets)
                    failedAssets.Add(guid);

                missing++;
            }
        }

        return missing;
    }

    private static void CollectGuids(string path, HashSet<string> guids)
    {
        if (!File.Exists(path))
            return;

        HomeRoomObjectList list;

        try
        {
            list = JsonUtility.FromJson<HomeRoomObjectList>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return;
        }

        if (list?.objects == null)
            return;

        foreach (HomeRoomObjectData data in list.objects)
        {
            AddGuid(guids, data.assetGuid, data.assetPath);
            AddGuid(guids, data.meshAssetGuid, data.meshAssetPath);

            if (data.materials == null)
                continue;

            foreach (HomeMaterialData material in data.materials)
                AddGuid(guids, material.assetGuid, material.assetPath);
        }
    }

    // Unity に最初から入っているもの(Assets の外)は送れないし、送る必要もない
    private static void AddGuid(HashSet<string> guids, string guid, string assetPath)
    {
        if (!string.IsNullOrEmpty(guid) && assetPath != null && assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            guids.Add(guid);
    }

    private bool FetchAsset(string guid)
    {
        HomeSyncResponse response;
        byte[] body;

        try
        {
            response = Request(new HomeSyncRequest { type = HomeSyncProtocol.RequestAsset, guid = guid }, null, out body);
        }
        catch (Exception e)
        {
            Log($"部屋のモデルなどを受け取れませんでした (guid={guid}): {e.Message}");
            return false;
        }

        long offset = 0;
        int written = 0;

        foreach (HomeSyncFileEntry file in response.files)
        {
            byte[] content = new byte[file.size];
            Array.Copy(body, offset, content, 0, file.size);
            offset += file.size;

            if (!file.path.StartsWith("Assets/", StringComparison.Ordinal) ||
                !HomeSyncProtocol.TryResolveInside(projectRoot, file.path, out string fullPath))
                continue;

            // 同じ名前のファイルがすでにあれば上書きしない(このPCで別のものとして使っているかもしれないため)
            if (File.Exists(fullPath))
                continue;

            HomeSyncPaths.WriteAllBytesAtomic(fullPath, content);
            written++;
        }

        assetIndex.Invalidate();

        if (!assetIndex.TryGetPath(guid, out string assetPath))
        {
            Log($"部屋のモデルなどを受け取りましたが、同じ場所に同じ名前の別のファイルがあるため使えません (guid={guid})。" +
                "このPCのプロジェクトのそのファイルを確認してください。");
            return false;
        }

        Log($"部屋のモデルなどを受け取りました: {assetPath} (ファイル {written} 個)");
        Interlocked.Exchange(ref refreshRequested, 1);
        return true;
    }

    // ------------------------------------------------------------
    // 通信
    // ------------------------------------------------------------

    private HomeSyncResponse Request(HomeSyncRequest request, byte[] body, out byte[] responseBody)
    {
        request.client = clientName;

        using (TcpClient connection = new TcpClient())
        {
            if (!connection.ConnectAsync(server.Address, server.Port).Wait(TimeoutMs))
                throw new IOException("接続できませんでした");

            connection.ReceiveTimeout = 60000;
            connection.SendTimeout = 60000;
            NetworkStream stream = connection.GetStream();
            HomeSyncProtocol.WriteMessage(stream, request, body);
            HomeSyncResponse response = HomeSyncProtocol.ReadMessage<HomeSyncResponse>(stream, out responseBody);

            if (!response.ok)
                throw new IOException(response.error);

            return response;
        }
    }

    private IPEndPoint FindServer()
    {
        if (!string.IsNullOrEmpty(manualAddress))
        {
            try
            {
                IPAddress address = IPAddress.TryParse(manualAddress, out IPAddress parsed)
                    ? parsed
                    : Dns.GetHostAddresses(manualAddress)[0];
                return new IPEndPoint(address, port);
            }
            catch (Exception)
            {
                return null;
            }
        }

        using (UdpClient udp = new UdpClient(0) { EnableBroadcast = true })
        {
            byte[] message = Encoding.UTF8.GetBytes(HomeSyncProtocol.DiscoverMessage);

            foreach (IPAddress target in HomeSyncProtocol.GetBroadcastTargets())
            {
                try
                {
                    udp.Send(message, message.Length, new IPEndPoint(target, HomeSyncProtocol.DiscoveryPort));
                }
                catch (Exception)
                {
                }
            }

            udp.Client.ReceiveTimeout = 1500;

            try
            {
                IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                string reply = Encoding.UTF8.GetString(udp.Receive(ref remote));

                if (!reply.StartsWith(HomeSyncProtocol.ServerMessagePrefix, StringComparison.Ordinal))
                    return null;

                string[] parts = reply.Substring(HomeSyncProtocol.ServerMessagePrefix.Length).Split('|');
                int serverPort = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : port;
                return new IPEndPoint(remote.Address, serverPort);
            }
            catch (SocketException)
            {
                return null;
            }
        }
    }
}
