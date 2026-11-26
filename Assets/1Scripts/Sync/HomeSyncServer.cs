using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// 制御PCで動かす同期サーバー。録画PC(HomeSyncClient)からの要求に答える。
//   - セッション(実験の名前・実験対象者)を配る
//   - このPCのデータの場所の <実験の名前>/Env/ を配る・録画PCが保存した Env を受け取る
//   - Env が参照している部屋のモデルなどのアセット(Assets/ の下のファイルと .meta)を配る
// 受け付けはスレッドで行うので、Unity の API は使わない(セッションは SetSession でメインスレッドから渡す)。
public class HomeSyncServer
{
    public class ClientInfo
    {
        public string name;
        public string address;
        public string status;
        public DateTime lastSeen;
    }

    private readonly string serverName;
    private readonly int port;
    private readonly string dataRoot;
    private readonly HomeSyncHashCache hashCache = new HomeSyncHashCache();
    private readonly HomeSyncAssetIndex assetIndex;
    private readonly string projectRoot;
    private readonly Dictionary<string, ClientInfo> clients = new Dictionary<string, ClientInfo>();
    private readonly object sessionLock = new object();
    private HomeSessionData session = new HomeSessionData();

    private TcpListener listener;
    private UdpClient discovery;
    private Thread acceptThread;
    private Thread discoveryThread;
    private volatile bool running;

    public ConcurrentQueue<string> Logs { get; } = new ConcurrentQueue<string>();
    public string Error { get; private set; }

    public HomeSyncServer(string serverName, int port, string dataRoot, string projectRoot)
    {
        this.serverName = serverName;
        this.port = port;
        this.dataRoot = dataRoot;
        this.projectRoot = projectRoot;
        assetIndex = new HomeSyncAssetIndex(projectRoot);
    }

    public void SetSession(HomeSessionData data)
    {
        lock (sessionLock)
            session = data.Clone();
    }

    public List<ClientInfo> GetClients()
    {
        lock (clients)
            return new List<ClientInfo>(clients.Values);
    }

    public bool Start()
    {
        try
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            discovery = new UdpClient(new IPEndPoint(IPAddress.Any, HomeSyncProtocol.DiscoveryPort)) { EnableBroadcast = true };
        }
        catch (Exception e)
        {
            Error = $"ポート {port} / {HomeSyncProtocol.DiscoveryPort} を開けませんでした(他のUnityで制御PCを動かしていないか確認してください)。{e.Message}";
            Stop();
            return false;
        }

        running = true;
        acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "HomeSyncServer" };
        acceptThread.Start();
        discoveryThread = new Thread(DiscoveryLoop) { IsBackground = true, Name = "HomeSyncDiscovery" };
        discoveryThread.Start();
        Log($"制御PCとして待ち受けを始めました (TCP {port}, 探索 UDP {HomeSyncProtocol.DiscoveryPort})。データの場所: {dataRoot}");
        return true;
    }

    public void Stop()
    {
        running = false;

        try { listener?.Stop(); } catch (Exception) { }
        try { discovery?.Close(); } catch (Exception) { }

        listener = null;
        discovery = null;
    }

    private void Log(string message) => Logs.Enqueue(message);

    private void DiscoveryLoop()
    {
        byte[] reply = Encoding.UTF8.GetBytes($"{HomeSyncProtocol.ServerMessagePrefix}{serverName}|{port}");

        while (running)
        {
            try
            {
                IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = discovery.Receive(ref remote);

                if (Encoding.UTF8.GetString(data) == HomeSyncProtocol.DiscoverMessage)
                    discovery.Send(reply, reply.Length, remote);
            }
            catch (Exception)
            {
                if (!running)
                    return;
            }
        }
    }

    private void AcceptLoop()
    {
        while (running)
        {
            TcpClient connection;

            try
            {
                connection = listener.AcceptTcpClient();
            }
            catch (Exception)
            {
                if (!running)
                    return;

                continue;
            }

            ThreadPool.QueueUserWorkItem(_ => Handle(connection));
        }
    }

    private void Handle(TcpClient connection)
    {
        using (connection)
        {
            connection.ReceiveTimeout = 10000;
            connection.SendTimeout = 60000;
            string address = (connection.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";

            try
            {
                NetworkStream stream = connection.GetStream();
                HomeSyncRequest request = HomeSyncProtocol.ReadMessage<HomeSyncRequest>(stream, out byte[] body);
                HomeSyncResponse response = new HomeSyncResponse { ok = true, serverName = serverName };
                byte[] responseBody = null;

                try
                {
                    responseBody = Dispatch(request, body, address, response);
                }
                catch (Exception e)
                {
                    response.ok = false;
                    response.error = e.Message;
                    Log($"{request.client ?? address} の要求 {request.type} に失敗しました: {e.Message}");
                }

                HomeSyncProtocol.WriteMessage(stream, response, responseBody);
            }
            catch (Exception e)
            {
                if (running)
                    Log($"{address} との通信に失敗しました: {e.Message}");
            }
        }
    }

    private byte[] Dispatch(HomeSyncRequest request, byte[] body, string address, HomeSyncResponse response)
    {
        RememberClient(request, address);

        switch (request.type)
        {
            case HomeSyncProtocol.RequestStatus:
            {
                HomeSessionData current;

                lock (sessionLock)
                    current = session.Clone();

                response.session = current;
                response.envHash = IsValidName(current.experimentName)
                    ? HomeSyncProtocol.ManifestHash(HomeSyncProtocol.BuildManifest(EnvDirectory(current.experimentName), hashCache))
                    : "";
                return null;
            }

            case HomeSyncProtocol.RequestManifest:
                response.files = HomeSyncProtocol.BuildManifest(EnvDirectory(RequireName(request.experiment)), hashCache);
                return null;

            case HomeSyncProtocol.RequestGetFile:
                return File.ReadAllBytes(EnvFile(request));

            case HomeSyncProtocol.RequestPutFile:
                HomeSyncPaths.WriteAllBytesAtomic(EnvFile(request), body);
                Log($"{request.client} から受け取りました: {request.experiment}/Env/{request.path}");
                return null;

            case HomeSyncProtocol.RequestDeleteFile:
            {
                string path = EnvFile(request);

                if (File.Exists(path))
                {
                    File.Delete(path);
                    Log($"{request.client} の指示で削除しました: {request.experiment}/Env/{request.path}");
                }

                return null;
            }

            case HomeSyncProtocol.RequestAsset:
                return PackAsset(request.guid, response);

            default:
                throw new InvalidOperationException($"知らない要求です: {request.type}");
        }
    }

    private void RememberClient(HomeSyncRequest request, string address)
    {
        if (string.IsNullOrEmpty(request.client))
            return;

        lock (clients)
        {
            if (!clients.TryGetValue(request.client, out ClientInfo info))
            {
                info = new ClientInfo { name = request.client };
                clients[request.client] = info;
                Log($"録画PC {request.client} ({address}) がつながりました。");
            }

            info.address = address;
            info.lastSeen = DateTime.Now;

            if (!string.IsNullOrEmpty(request.clientStatus))
                info.status = request.clientStatus;
        }
    }

    // GUID のアセットと、同じフォルダのファイル(.obj なら .mtl やテクスチャ)を .meta ごとまとめて送る
    private byte[] PackAsset(string guid, HomeSyncResponse response)
    {
        if (string.IsNullOrEmpty(guid) || !assetIndex.TryGetPath(guid, out string assetPath))
        {
            assetIndex.Invalidate();

            if (string.IsNullOrEmpty(guid) || !assetIndex.TryGetPath(guid, out assetPath))
                throw new FileNotFoundException($"制御PCのプロジェクトにアセットがありません (guid={guid})");
        }

        if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            throw new InvalidOperationException($"Assets の外のアセットは送れません: {assetPath}");

        string folder = Path.GetDirectoryName(Path.Combine(projectRoot, assetPath));
        List<byte> bytes = new List<byte>();

        foreach (string file in Directory.GetFiles(folder))
        {
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                continue;

            byte[] content = File.ReadAllBytes(file);
            response.files.Add(new HomeSyncFileEntry { path = HomeSyncProtocol.ToRelative(projectRoot, file), size = content.LongLength });
            bytes.AddRange(content);
        }

        // フォルダ自体の .meta(フォルダの GUID をそろえるため)
        string folderMeta = folder + ".meta";

        if (File.Exists(folderMeta))
        {
            byte[] content = File.ReadAllBytes(folderMeta);
            response.files.Add(new HomeSyncFileEntry { path = HomeSyncProtocol.ToRelative(projectRoot, folderMeta), size = content.LongLength });
            bytes.AddRange(content);
        }

        Log($"アセットを送ります: {assetPath} (ファイル {response.files.Count} 個, {bytes.Count / 1024 / 1024.0:F1} MB)");
        return bytes.ToArray();
    }

    private string EnvDirectory(string experimentName) =>
        Path.Combine(dataRoot, experimentName, HomeExperimentPaths.EnvFolderName);

    private string EnvFile(HomeSyncRequest request)
    {
        string dir = EnvDirectory(RequireName(request.experiment));

        if (!HomeSyncProtocol.IsSyncTarget(request.path ?? "") ||
            !HomeSyncProtocol.TryResolveInside(dir, request.path, out string path))
            throw new InvalidOperationException($"Env の外のファイルは扱えません: {request.path}");

        return path;
    }

    private static bool IsValidName(string name) => HomeExperimentPaths.IsValidFolderName(name, out _);

    private static string RequireName(string name)
    {
        if (!HomeExperimentPaths.IsValidFolderName(name, out string error))
            throw new InvalidOperationException(error);

        return name;
    }
}
