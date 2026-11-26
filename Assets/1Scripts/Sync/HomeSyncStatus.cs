using System;
using System.Collections.Generic;

// 同期の状態を、エディタ側(HomeSyncService)から再生中の画面(シーンマネージャー)へ渡すための入れ物。
// 同期そのものはエディタで動いている(プレイ中も止まらない)ので、画面はここを読むだけにする。
// 画面からの「今すぐ同期」の依頼もここを通す。
public static class HomeSyncStatus
{
    public class ClientInfo
    {
        public string name;
        public string address;
        public string status;
        public string[] kinectSerials = new string[0];
        public DateTime lastSeen;

        public bool IsAlive => (DateTime.Now - lastSeen).TotalSeconds < 10;
    }

    // 同期が動いているか(制御PC・録画PCのとき)
    public static bool Running;
    public static string Error = "";

    // 録画PC: 制御PCとつながっているか・状態の説明・制御PCの名前・最後に同期した時刻
    public static bool Connected;
    public static string Status = "";
    public static string ServerDescription = "";
    public static string LastSyncedAt = "";

    // 制御PC: つながっている録画PC
    public static List<ClientInfo> Clients = new List<ClientInfo>();

    // 最近のログ(新しいものが後ろ)
    public static readonly List<string> Logs = new List<string>();

    // 画面からの依頼
    public static bool SyncNowRequested;

    public static void AddLog(string message)
    {
        lock (Logs)
        {
            Logs.Add($"{DateTime.Now:HH:mm:ss} {message}");

            if (Logs.Count > 200)
                Logs.RemoveRange(0, Logs.Count - 200);
        }
    }

    public static List<string> RecentLogs(int count)
    {
        lock (Logs)
            return Logs.GetRange(Math.Max(0, Logs.Count - count), Math.Min(count, Logs.Count));
    }
}
