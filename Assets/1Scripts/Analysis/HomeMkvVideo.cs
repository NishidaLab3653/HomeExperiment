using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

// シーン1で記録した生データ(Raw~/<ID>.mkv)のカラー映像を、骨格データと同じ時刻(recordingTimeSec)で表示するための読み込み。
// シーン6(6ArrivalCheck)で使う。
//
// 時刻の対応は <ID>_raw_index.json(各フレームの recordingTimeSec とキネクト本体のタイムスタンプ)で取り、
// MKV をキネクト本体の時刻でシークする。MKV の読み込みは別スレッドで行い、カラーは MJPG のまま受け取って
// メインスレッドで Texture2D.LoadImage する(Update ごとに呼ぶ)。
public class HomeMkvVideo : IDisposable
{
    public string KinectId { get; }
    public string Path { get; }
    public Texture2D Texture { get; private set; }
    // 今の表示しているフレームの recordingTimeSec(まだ無ければ NaN)
    public float ShownTime { get; private set; } = float.NaN;
    public float FirstTime { get; }
    public float LastTime { get; }
    public string Error { get; private set; } = "";

    private readonly float[] indexTime;
    private readonly long[] indexColorUsec;
    private readonly Thread thread;
    private readonly object bufferLock = new object();
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private volatile bool running = true;
    private long targetUsec = -1;
    private byte[] pendingJpeg;
    private long pendingUsec;
    private bool hasPending;

    // 開けなければ null(理由は error)
    public static HomeMkvVideo Open(string rawDirectory, string kinectId, out string error)
    {
        error = "";
        string mkv = System.IO.Path.Combine(rawDirectory, $"{kinectId}.mkv");
        string indexPath = System.IO.Path.Combine(rawDirectory, $"{kinectId}_raw_index.json");

        if (!File.Exists(mkv) || !File.Exists(indexPath))
        {
            error = $"Kinect{kinectId} の MKV / raw_index.json がありません: {rawDirectory}";
            return null;
        }

        foreach (char c in mkv)
        {
            if (c > 127)
            {
                error = $"MKV のパスに英数字以外が入っているため開けません(k4arecord の制限): {mkv}";
                return null;
            }
        }

        HomeRawIndex index = JsonUtility.FromJson<HomeRawIndex>(File.ReadAllText(indexPath));

        if (index == null || index.frames == null || index.frames.Count == 0)
        {
            error = $"raw_index.json にフレームがありません: {indexPath}";
            return null;
        }

        if (k4a_playback_open(mkv, out IntPtr handle) != 0)
        {
            error = $"MKV を開けませんでした: {mkv}";
            return null;
        }

        return new HomeMkvVideo(kinectId, mkv, handle, index);
    }

    private HomeMkvVideo(string kinectId, string path, IntPtr handle, HomeRawIndex index)
    {
        KinectId = kinectId;
        Path = path;
        int n = index.frames.Count;
        indexTime = new float[n];
        indexColorUsec = new long[n];

        for (int i = 0; i < n; i++)
        {
            indexTime[i] = index.frames[i].recordingTimeSec;
            indexColorUsec[i] = index.frames[i].colorTimestampUsec >= 0 ? index.frames[i].colorTimestampUsec : index.frames[i].depthTimestampUsec;
        }

        FirstTime = indexTime[0];
        LastTime = indexTime[n - 1];
        Texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
        thread = new Thread(() => ReadLoop(handle)) { IsBackground = true, Name = $"HomeMkvVideo_{kinectId}" };
        thread.Start();
    }

    // 表示したい時刻(recordingTimeSec)。範囲外なら false
    public bool Request(float recordingTime)
    {
        if (recordingTime < FirstTime - 0.1f || recordingTime > LastTime + 0.1f)
            return false;

        int i = Array.BinarySearch(indexTime, recordingTime);

        if (i < 0)
        {
            i = ~i;

            // 近い方のフレーム
            if (i > 0 && (i >= indexTime.Length || recordingTime - indexTime[i - 1] < indexTime[i] - recordingTime))
                i--;
        }

        i = Mathf.Clamp(i, 0, indexTime.Length - 1);
        long usec = indexColorUsec[i];

        if (Interlocked.Exchange(ref targetUsec, usec) != usec)
            wake.Set();

        return true;
    }

    // メインスレッドから毎フレーム呼ぶ。新しいフレームがあればテクスチャにする
    public void UpdateTexture()
    {
        byte[] jpeg;
        long usec;

        lock (bufferLock)
        {
            if (!hasPending)
                return;

            jpeg = pendingJpeg;
            usec = pendingUsec;
            hasPending = false;
        }

        if (Texture.LoadImage(jpeg, false))
            ShownTime = TimeOf(usec);
    }

    private float TimeOf(long usec)
    {
        int i = Array.BinarySearch(indexColorUsec, usec);

        if (i < 0)
            i = Mathf.Clamp(~i, 0, indexColorUsec.Length - 1);

        return indexTime[i] + (usec - indexColorUsec[i]) * 1e-6f;
    }

    // ------------------------------------------------------------
    // 読み込み(別スレッド)
    // ------------------------------------------------------------
    private void ReadLoop(IntPtr handle)
    {
        long current = -1;   // 最後に読んだカラーのタイムスタンプ
        long served = -1;    // 最後に応えた target

        try
        {
            while (running)
            {
                long target = Interlocked.Read(ref targetUsec);

                if (target < 0 || target == served)
                {
                    wake.WaitOne(50);
                    continue;
                }

                // 戻るとき・1 秒より先へ飛ぶときはシークする。それ以外は順に読む
                if (current < 0 || target < current - 1000 || target - current > 1_000_000)
                {
                    if (k4a_playback_seek_timestamp(handle, Math.Max(target - 1000, 0), SeekDeviceTime) != 0)
                    {
                        Error = "MKV をシークできませんでした";
                        wake.WaitOne(200);
                        continue;
                    }
                }

                // target 以上のカラーが出るまで読む(途中のフレームは飛ばす。target が変わったらそこで止める)
                while (running)
                {
                    int result = k4a_playback_get_next_capture(handle, out IntPtr capture);

                    if (result != 0)
                    {
                        // 終わりまで読んだ(次に戻るときはシークする)
                        current = long.MaxValue;
                        served = target;
                        break;
                    }

                    try
                    {
                        IntPtr image = k4a_capture_get_color_image(capture);

                        if (image == IntPtr.Zero)
                            continue;

                        try
                        {
                            long usec = (long)k4a_image_get_device_timestamp_usec(image);
                            current = usec;

                            if (usec + 1000 < target && Interlocked.Read(ref targetUsec) == target)
                                continue;

                            int size = (int)k4a_image_get_size(image).ToUInt64();
                            byte[] jpeg = new byte[size];
                            Marshal.Copy(k4a_image_get_buffer(image), jpeg, 0, size);

                            lock (bufferLock)
                            {
                                pendingJpeg = jpeg;
                                pendingUsec = usec;
                                hasPending = true;
                            }

                            served = target;
                            break;
                        }
                        finally
                        {
                            k4a_image_release(image);
                        }
                    }
                    finally
                    {
                        k4a_capture_release(capture);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Error = e.Message;
            Debug.LogError($"[HomeMkvVideo] Kinect{KinectId}: 映像を読めませんでした。\n{e}");
        }
        finally
        {
            k4a_playback_close(handle);
        }
    }

    public void Dispose()
    {
        running = false;
        wake.Set();
        thread?.Join(2000);

        if (Texture != null)
            UnityEngine.Object.Destroy(Texture);
    }

    // ------------------------------------------------------------
    // Native
    // ------------------------------------------------------------
    private const string RecordDll = "k4arecord";
    private const string SensorDll = "k4a";
    private const int SeekDeviceTime = 2;   // K4A_PLAYBACK_SEEK_DEVICE_TIME

    [DllImport(RecordDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int k4a_playback_open(string path, out IntPtr playbackHandle);

    [DllImport(RecordDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int k4a_playback_get_next_capture(IntPtr playbackHandle, out IntPtr captureHandle);

    [DllImport(RecordDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int k4a_playback_seek_timestamp(IntPtr playbackHandle, long offsetUsec, int origin);

    [DllImport(RecordDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void k4a_playback_close(IntPtr playbackHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr k4a_capture_get_color_image(IntPtr captureHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void k4a_capture_release(IntPtr captureHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void k4a_image_release(IntPtr imageHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong k4a_image_get_device_timestamp_usec(IntPtr imageHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern UIntPtr k4a_image_get_size(IntPtr imageHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr k4a_image_get_buffer(IntPtr imageHandle);
}
