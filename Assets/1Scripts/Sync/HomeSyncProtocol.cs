using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

// 制御PC(HomeSyncServer)と録画PC(HomeSyncClient)のあいだのやり取りの決まり。
//
// - 録画PCは UDP のブロードキャストで制御PCを探す(DiscoveryPort)。制御PCのアドレスを指定していれば探さない
// - やり取りは TCP(DefaultPort)。1回の要求ごとに接続し、要求1つ・応答1つを送って切る
// - 1つのメッセージ = [ヘッダーの長さ int32][ヘッダー JSON(UTF-8)][本体の長さ int64][本体(ファイルの中身など)]
// - LANの中だけで使う前提で、認証はしない
//
// 将来(スイッチ・NAS・制御PCをそろえた構成)でも、同じ要求をそのまま使える(手順書の「制御サーバー」)。
public static class HomeSyncProtocol
{
    public const int DefaultPort = 50610;
    public const int DiscoveryPort = 50611;
    public const string DiscoverMessage = "HOME_SYNC_DISCOVER";
    public const string ServerMessagePrefix = "HOME_SYNC_SERVER|";

    public const string RequestStatus = "status";
    public const string RequestManifest = "manifest";
    public const string RequestGetFile = "get";
    public const string RequestPutFile = "put";
    public const string RequestDeleteFile = "delete";
    public const string RequestAsset = "asset";

    private const int MaxHeaderBytes = 4 * 1024 * 1024;
    private const long MaxBodyBytes = 2L * 1024 * 1024 * 1024;

    public static void WriteMessage(Stream stream, object header, byte[] body)
    {
        byte[] headerBytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(header));
        long bodyLength = body != null ? body.LongLength : 0;

        using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(headerBytes.Length);
            writer.Write(headerBytes);
            writer.Write(bodyLength);

            if (bodyLength > 0)
                writer.Write(body);

            writer.Flush();
        }
    }

    public static T ReadMessage<T>(Stream stream, out byte[] body)
    {
        using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
        {
            int headerLength = reader.ReadInt32();

            if (headerLength <= 0 || headerLength > MaxHeaderBytes)
                throw new IOException($"ヘッダーの長さが不正です: {headerLength}");

            string json = Encoding.UTF8.GetString(ReadExactly(reader, headerLength));
            long bodyLength = reader.ReadInt64();

            if (bodyLength < 0 || bodyLength > MaxBodyBytes)
                throw new IOException($"本体の長さが不正です: {bodyLength}");

            body = bodyLength > 0 ? ReadExactly(reader, (int)bodyLength) : Array.Empty<byte>();
            return JsonUtility.FromJson<T>(json);
        }
    }

    private static byte[] ReadExactly(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);

        if (bytes.Length != length)
            throw new EndOfStreamException("接続が途中で切れました。");

        return bytes;
    }

    // ------------------------------------------------------------
    // ファイルの一覧(マニフェスト)
    // ------------------------------------------------------------

    // 同期しないファイル: Unity が作る .meta(PCごとに GUID が変わる)、書きかけの一時ファイル、衝突したときに残したファイル
    public static bool IsSyncTarget(string relativePath)
    {
        string name = Path.GetFileName(relativePath);
        return !name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) &&
               !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
               name.IndexOf(".conflict-", StringComparison.OrdinalIgnoreCase) < 0;
    }

    // dir の下のファイル(サブフォルダも含む)を、相対パス('/' 区切り)とハッシュの一覧にする
    public static List<HomeSyncFileEntry> BuildManifest(string dir, HomeSyncHashCache cache)
    {
        List<HomeSyncFileEntry> files = new List<HomeSyncFileEntry>();

        if (!Directory.Exists(dir))
            return files;

        foreach (string path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            string relative = ToRelative(dir, path);

            if (!IsSyncTarget(relative))
                continue;

            try
            {
                FileInfo info = new FileInfo(path);
                files.Add(new HomeSyncFileEntry { path = relative, size = info.Length, hash = cache.GetHash(info) });
            }
            catch (IOException)
            {
                // 書き込み中などで読めないファイルは次の回に回す
            }
        }

        files.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
        return files;
    }

    // 一覧全体のハッシュ。どこか1つでも変われば変わる
    public static string ManifestHash(List<HomeSyncFileEntry> files)
    {
        StringBuilder text = new StringBuilder();

        foreach (HomeSyncFileEntry file in files)
            text.Append(file.path).Append(':').Append(file.hash).Append('\n');

        return HashBytes(Encoding.UTF8.GetBytes(text.ToString()));
    }

    public static string HashBytes(byte[] bytes)
    {
        using (SHA1 sha1 = SHA1.Create())
            return ToHex(sha1.ComputeHash(bytes));
    }

    public static string HashFile(string path)
    {
        using (SHA1 sha1 = SHA1.Create())
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            return ToHex(sha1.ComputeHash(stream));
    }

    private static string ToHex(byte[] hash)
    {
        StringBuilder text = new StringBuilder(hash.Length * 2);

        foreach (byte b in hash)
            text.Append(b.ToString("x2"));

        return text.ToString();
    }

    public static string ToRelative(string dir, string path) =>
        path.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/');

    // 相対パスが dir の外を指していないか(「..」や絶対パスを送られても dir の外には書かない)
    public static bool TryResolveInside(string dir, string relativePath, out string fullPath)
    {
        fullPath = null;

        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return false;

        string root = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;

        fullPath = candidate;
        return true;
    }

    // ------------------------------------------------------------
    // ネットワーク
    // ------------------------------------------------------------

    // 255.255.255.255 だけだと、ネットワークが複数(有線+Wi-Fi など)あるPCでは1つにしか出ないことがあるので、
    // 各ネットワークのブロードキャストアドレス(192.168.1.255 / 169.254.255.255 など)にも送る
    public static List<IPAddress> GetBroadcastTargets()
    {
        List<IPAddress> result = new List<IPAddress> { IPAddress.Broadcast };

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    byte[] address = info.Address.GetAddressBytes();
                    byte[] mask = info.IPv4Mask != null && !info.IPv4Mask.Equals(IPAddress.Any)
                        ? info.IPv4Mask.GetAddressBytes()
                        : new byte[] { 255, 255, 0, 0 };

                    for (int i = 0; i < 4; i++)
                        address[i] = (byte)(address[i] | ~mask[i]);

                    IPAddress broadcast = new IPAddress(address);

                    if (!result.Contains(broadcast))
                        result.Add(broadcast);
                }
            }
        }
        catch (Exception)
        {
            // ネットワークの一覧が取れなくても 255.255.255.255 には送る
        }

        return result;
    }

    // このPCのIPv4アドレス(ネットワークの名前つき)。同期ウィンドウに表示する
    public static List<string> GetLocalAddresses()
    {
        List<string> result = new List<string>();

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily == AddressFamily.InterNetwork)
                        result.Add($"{info.Address} ({nic.Name})");
                }
            }
        }
        catch (Exception)
        {
        }

        return result;
    }
}

[Serializable]
public class HomeSyncRequest
{
    public string type;
    public string client;
    // 録画PCの状態(制御PCの一覧に表示する)
    public string clientStatus;
    // 録画PCにつないでいるキネクトのシリアル番号(デバイスの番号順。制御PCのキネクトの割り当てに使う)
    public string[] clientKinectSerials;
    public string experiment;
    public string path;
    public string guid;
}

[Serializable]
public class HomeSyncResponse
{
    public bool ok;
    public string error;
    public string serverName;
    public HomeSessionData session;
    // session.experimentName の Env の一覧のハッシュ(status のとき)
    public string envHash;
    public List<HomeSyncFileEntry> files = new List<HomeSyncFileEntry>();
}

[Serializable]
public class HomeSyncFileEntry
{
    public string path;
    public long size;
    public string hash;
}

// ファイルの大きさと更新日時が変わっていなければ、前に計算したハッシュを使う(毎回全部読まないため)
public class HomeSyncHashCache
{
    private struct Entry
    {
        public long size;
        public DateTime stamp;
        public string hash;
    }

    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

    public string GetHash(FileInfo info)
    {
        lock (entries)
        {
            if (entries.TryGetValue(info.FullName, out Entry entry) &&
                entry.size == info.Length && entry.stamp == info.LastWriteTimeUtc)
                return entry.hash;
        }

        string hash = HomeSyncProtocol.HashFile(info.FullName);

        lock (entries)
            entries[info.FullName] = new Entry { size = info.Length, stamp = info.LastWriteTimeUtc, hash = hash };

        return hash;
    }
}

// <プロジェクト>/Assets の下の .meta を読んで、GUID → アセットのパスを引けるようにする(AssetDatabase を使わないのでスレッドから使える)
public class HomeSyncAssetIndex
{
    private readonly string projectRoot;
    private Dictionary<string, string> guidToPath;

    public HomeSyncAssetIndex(string projectRoot)
    {
        this.projectRoot = projectRoot;
    }

    public void Invalidate() => guidToPath = null;

    // 見つかれば <プロジェクト> からの相対パス(Assets/...)
    public bool TryGetPath(string guid, out string assetPath)
    {
        if (guidToPath == null)
            Build();

        return guidToPath.TryGetValue(guid, out assetPath);
    }

    private void Build()
    {
        Dictionary<string, string> map = new Dictionary<string, string>();
        string assets = Path.Combine(projectRoot, "Assets");

        foreach (string meta in Directory.GetFiles(assets, "*.meta", SearchOption.AllDirectories))
        {
            // 記録データのフォルダには部屋のモデルは無いので読まない(数が多いと遅いため)
            if (meta.IndexOf(Path.DirectorySeparatorChar + "Data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            try
            {
                foreach (string line in File.ReadLines(meta))
                {
                    if (line.StartsWith("guid: "))
                    {
                        string assetFile = meta.Substring(0, meta.Length - ".meta".Length);
                        map[line.Substring(6).Trim()] = HomeSyncProtocol.ToRelative(projectRoot, assetFile);
                        break;
                    }
                }
            }
            catch (IOException)
            {
            }
        }

        guidToPath = map;
    }
}
