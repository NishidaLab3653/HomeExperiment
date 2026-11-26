using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Azure.Kinect.BodyTracking;
using UnityEngine;

// シーン6(6ArrivalCheck)用。シーン5(5GazeExport)の書き出しから「物体への到達」を見つけ、到達の条件を確かめながら決める。
//
// 読むもの(Skeleton/<実験対象者>/Analysis/Export/<骨格>/):
//   <人物>_gaze.csv / <人物>_distance.csv / <人物>_skeleton.csv / export_params.json(対象物体の中心と大きさ)
//
// 到達の決め方(インスペクターの Arrival Conditions。変えるとすぐ計算し直す):
//   1. きっかけ(Trigger)
//        Approach … leaveDistance m より離れていたのを見た後に nearDistance m 以内に入った時刻(骨盤から物体の箱までの水平距離)。
//                    映っていない時間が missingResetSec 秒を超えたら数え直す(Python の analyze_*.py の approach と同じ)
//        Touch    … 手・手先・親指のどれかが物体の箱(+ touchMargin m)に touchMinSec 秒以上入った時刻(touchGapSec 秒以下の触れ直しは同じ1回)
//   2. 近づく速さ   … きっかけの approachSpeedWindowSec 秒前からの距離の縮み方(m/s)が minApproachSpeed 以上(0 で使わない)
//   3. 滞在         … きっかけの後、dwellRadius m 以内に dwellSec 秒以上とどまる(dwellToleranceSec 秒以下の一時的な外れ・映っていない時間は続けて数える)
//   4. 体の向き     … きっかけの faceWindowStartSec 秒後から faceWindowSec 秒間の、肩の正面から物体への水平の角度の平均が maxFaceAngle 度以下
//   5. 注視         … きっかけから lookWindowSec 秒間の注視可能性(視線と物体のふちの角度に σ のガウス)の平均が minLookPossibility 以上
//      4 と 5 を両方使うときは orientationRule(どちらか / 両方)
//   6. 触れる       … (Approach のとき)きっかけから touchWithinSec 秒以内に手で触れた
//   7. 重複         … 同じ物体への到達が minIntervalSameObjectSec 秒以内に続いたら、後のものは数えない
// 候補(きっかけ)ごとに、どの条件で落ちたかを画面に出す。
//
// 確かめ方: 左の一覧で候補を選ぶと、その少し前から骨格を再生する(時刻は骨格データの時刻 = 動画の記録の時刻)。
//   右下には、シーン1で記録した生データ(Raw~/<ID>.mkv)のカラー映像を同じ時刻で出す(HomeMkvVideo。ボタンでキネクトを切り替え)。
//   ○ / × ボタンで「本当に到達か」を付けておくと(arrival_labels.csv に残る)、今の条件との一致を数える。
//
// 保存(Save ボタン。Export フォルダに書く):
//   arrival_definition.json … 今の条件(Python の解析で --arrival unity にすると、この条件で決めた到達を使う)
//   arrivals.csv            … 全候補と、条件ごとの値・採用したか・理由・○×
//   arrival_labels.csv      … ○ / × のラベル
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeArrivalCheck : MonoBehaviour, IHomeDataRootProvider
{
    public enum Trigger
    {
        Approach,
        Touch
    }

    public enum OrientationRule
    {
        Either,
        Both
    }

    [System.Serializable]
    public class ArrivalConditions
    {
        [Header("1. きっかけ")]
        [Tooltip("Approach: 離れていた後に近づいた時刻 / Touch: 手で触れた時刻")]
        public Trigger trigger = Trigger.Approach;
        [Tooltip("Approach: この距離(m)以内に入ったら到達のきっかけ(骨盤から物体の箱までの水平距離)")]
        public float nearDistance = 0.4f;
        [Tooltip("Approach: 一度この距離(m)より離れたのを見てから近づいたものだけ数える")]
        public float leaveDistance = 1.0f;
        [Tooltip("Approach: 映っていない時間がこの秒数を超えたら、離れていたかを数え直す")]
        public float missingResetSec = 1.0f;

        [Header("2. 近づく速さ")]
        [Tooltip("きっかけの approachSpeedWindowSec 秒前からの距離の縮み方(m/s)がこれ以上。0 で使わない")]
        public float minApproachSpeed = 0f;
        public float approachSpeedWindowSec = 2f;

        [Header("3. 滞在")]
        public bool useDwell = true;
        [Tooltip("とどまっているとする距離(m)")]
        public float dwellRadius = 0.6f;
        [Tooltip("きっかけの後、dwellRadius 以内にとどまる秒数")]
        public float dwellSec = 3f;
        [Tooltip("この秒数以下の一時的な外れ・映っていない時間は、とどまっているとみなす")]
        public float dwellToleranceSec = 0f;

        [Header("4. 体の向き")]
        public bool useFacing = true;
        [Tooltip("肩の正面から物体への水平の角度の平均がこれ以下なら「向いている」(度)")]
        public float maxFaceAngle = 60f;
        [Tooltip("きっかけの何秒後から測るか")]
        public float faceWindowStartSec = 0f;
        [Tooltip("何秒間の平均か")]
        public float faceWindowSec = 2f;

        [Header("5. 注視")]
        public bool useLooking = true;
        [Tooltip("注視可能性の平均がこれ以上なら「見ている」(0〜1)")]
        public float minLookPossibility = 0.3f;
        [Tooltip("きっかけから何秒間の平均か")]
        public float lookWindowSec = 3f;
        [Tooltip("視線の推定誤差 σ(度)。Python の解析(--sigma)と同じ")]
        public float sigma = 10.14f;
        [Tooltip("体の向きと注視を両方使うとき: Either = どちらかを満たせばよい / Both = 両方")]
        public OrientationRule orientationRule = OrientationRule.Either;

        [Header("6. 触れる")]
        [Tooltip("Approach のとき: きっかけから touchWithinSec 秒以内に手で触れたものだけ")]
        public bool requireTouch = false;
        public float touchWithinSec = 3f;
        [Tooltip("手(手・手先・親指)が物体の箱からこの距離(m)以内なら触れている。0 = 箱の中")]
        public float touchMargin = 0f;
        [Tooltip("触れている状態がこの秒数以上続いたら1回")]
        public float touchMinSec = 0.3f;
        [Tooltip("これ以下の間をおいた触れ直しは同じ1回(秒)")]
        public float touchGapSec = 3f;

        [Header("7. 重複")]
        [Tooltip("同じ物体への到達がこの秒数以内に続いたら、後のものは数えない(0 で使わない)")]
        public float minIntervalSameObjectSec = 0f;

        [Header("その他")]
        [Tooltip("頭と肩の向きを滑らかにする幅(秒。その時刻までの平均)")]
        public float smoothSec = 0.3f;
    }

    [Header("Data Location")]
    [Tooltip("データのルートフォルダ。空なら既定の Assets/Data/HomeExperiment。例: D:\\HomeExperiment")]
    [HomeDataRootFolder]
    [SerializeField] private string dataRootDirectory = "";

    [Header("Subject")]
    [HomeExperimentFolder(HomeExperimentFolderAttribute.Kind.Subject)]
    [SerializeField] private string subjectName = "";
    [Tooltip("シーン5の書き出しのフォルダ名(Analysis/Export/<ここ>)。空なら filtered_HeadJoints、無ければ最初に見つかったもの")]
    [SerializeField] private string exportName = "";

    [Header("Arrival Conditions")]
    [SerializeField] private ArrivalConditions conditions = new ArrivalConditions();

    [Header("Playback")]
    [Tooltip("一覧で候補を選んだとき、何秒前から再生するか")]
    [SerializeField] private float preRollSec = 5f;
    [SerializeField] private float playbackSpeed = 1f;
    [SerializeField] private KeyCode playStopKey = KeyCode.Space;

    [Header("Video")]
    [Tooltip("右下に、生データ(Raw~/<ID>.mkv)のカラー映像を同じ時刻で表示する")]
    [SerializeField] private bool showVideo = true;
    [Tooltip("最初に表示するキネクトのID。空なら見つかった最初のもの(画面のボタンで切り替えられる)")]
    [SerializeField] private string videoKinectId = "";
    [Tooltip("映像の表示の幅(ピクセル)")]
    [SerializeField] private float videoWidth = 560f;

    [Header("Visual")]
    [SerializeField] private GameObject jointPrefab;
    [SerializeField] private Material lineMaterial;
    [SerializeField] private float jointScale = 0.05f;
    [SerializeField] private float lineWidth = 0.02f;
    [SerializeField] private Color[] personColors = { new Color(1f, 0.5f, 0.1f), new Color(0.2f, 0.6f, 1f), Color.green, Color.magenta };

    private static readonly string[] HandJoints = { "HandLeft", "HandTipLeft", "ThumbLeft", "HandRight", "HandTipRight", "ThumbRight" };
    private const string DefinitionFile = "arrival_definition.json";
    private const string ArrivalsFile = "arrivals.csv";
    private const string LabelsFile = "arrival_labels.csv";

    // ------------------------------------------------------------
    // Data
    // ------------------------------------------------------------
    private class Target
    {
        public string name;
        public Vector3 center;
        public Vector3 size;
        public float radius;
    }

    private class Person
    {
        public string name;
        public Color color;
        public int count;
        public float[] time;
        public bool[] present;
        public bool[] hasGaze;
        public Vector3[] origin;
        public Vector3[] direction;
        public Vector3[] smoothDirection;
        public Vector3[] forward;
        public bool[] hasForward;
        public Vector3[] shoulderLeft;
        public Vector3[] shoulderRight;
        public float[][] distance;       // [target][frame]
        public float[][] faceAngle;      // [target][frame]
        public float[][] look;           // [target][frame]
        public Vector3[][] hands;        // [frame][hand joint]
        public string[] jointNames;
        public Vector3[] joints;         // [frame * joint count + joint]
        public int[] jointConfidence;
        public bool hasSkeleton;
        public HomeSkeletonBodyVisual visual;
        public readonly List<HomeSkeletonJoint> frameJoints = new List<HomeSkeletonJoint>();
    }

    private class Candidate
    {
        public Person person;
        public int target;
        public float time;
        public float approachSpeed = float.NaN;
        public float dwell = float.NaN;
        public float face = float.NaN;
        public float look = float.NaN;
        public float touchDelay = float.NaN;
        public bool accepted;
        public string reason = "";
        public int label = -1;   // -1: 無し / 1: 本当の到達(○) / 0: 違う(×)
    }

    private HomeEnvLoader env;
    private readonly List<Target> targets = new List<Target>();
    private readonly List<Person> persons = new List<Person>();
    private readonly List<Candidate> candidates = new List<Candidate>();
    // ラベル(人物, 物体, 時刻)。計算し直しても残す
    private readonly List<(string person, string target, float time, int label)> labels = new List<(string, string, float, int)>();
    private string exportFolder;
    private string status = "";
    private bool dirty = true;
    private bool loaded;

    private float playTime;
    private float startTime;
    private float endTime;
    private bool playing;
    private Candidate selected;
    private int personFilter = -1;   // -1: 全員
    private bool showRejected = true;
    private Vector2 listScroll;
    private Transform visualRoot;
    private LineRenderer selectedLine;
    private LineRenderer selectedBox;
    private GUIStyle rowStyle;
    private GUIStyle smallStyle;
    private string rawDirectory;
    private readonly List<string> videoKinects = new List<string>();
    private HomeMkvVideo video;
    private string videoError = "";

    public string DataRootDirectory => dataRootDirectory;

    private void Awake()
    {
        env = GetComponent<HomeEnvLoader>();
    }

    private void Start()
    {
        loaded = Load();
        dirty = true;
    }

    private void OnValidate()
    {
        dirty = true;
    }

    private void Update()
    {
        if (!loaded)
            return;

        if (dirty)
        {
            Compute();
            dirty = false;
        }

        if (Input.GetKeyDown(playStopKey))
            playing = !playing;

        if (playing)
        {
            playTime += Time.deltaTime * playbackSpeed;

            if (playTime >= endTime)
            {
                playTime = endTime;
                playing = false;
            }
        }

        ApplyFrame();

        if (video != null)
        {
            video.Request(playTime);
            video.UpdateTexture();
        }
    }

    private void OnDestroy()
    {
        video?.Dispose();
        video = null;
    }

    // ------------------------------------------------------------
    // Video
    // ------------------------------------------------------------
    private void FindVideos()
    {
        videoKinects.Clear();
        rawDirectory = HomeExperimentPaths.GetRawDirectory(dataRootDirectory, env.ExperimentName, subjectName);

        if (!showVideo || !Directory.Exists(rawDirectory))
        {
            videoError = showVideo ? $"生データ(Raw~)がありません: {rawDirectory}" : "";
            return;
        }

        foreach (string path in Directory.GetFiles(rawDirectory, "*_raw_index.json"))
        {
            string id = Path.GetFileName(path).Replace("_raw_index.json", "");

            if (File.Exists(Path.Combine(rawDirectory, $"{id}.mkv")))
                videoKinects.Add(id);
        }

        videoKinects.Sort();

        if (videoKinects.Count == 0)
        {
            videoError = $"生データの MKV がありません: {rawDirectory}";
            return;
        }

        string first = videoKinects.Contains(videoKinectId.Trim()) ? videoKinectId.Trim() : videoKinects[0];
        OpenVideo(first);
    }

    private void OpenVideo(string kinectId)
    {
        video?.Dispose();
        video = HomeMkvVideo.Open(rawDirectory, kinectId, out videoError);

        if (video != null)
            Debug.Log($"[HomeArrivalCheck] Kinect{kinectId} の映像を表示します: {video.Path}");
        else
            Debug.LogWarning($"[HomeArrivalCheck] {videoError}");
    }

    private void DrawVideo(Rect timelineArea)
    {
        if (!showVideo)
            return;

        float width = Mathf.Min(videoWidth, Screen.width * 0.45f);
        Texture2D texture = video?.Texture;
        float aspect = texture != null && texture.width > 2 ? (float)texture.height / texture.width : 9f / 16f;
        float height = width * aspect;
        Rect frame = new Rect(Screen.width - width - 10, timelineArea.y - height - 30, width, height + 26);
        GUI.Box(frame, "");

        GUILayout.BeginArea(new Rect(frame.x + 4, frame.y + 2, frame.width - 8, 22));
        GUILayout.BeginHorizontal();

        foreach (string id in videoKinects)
        {
            bool current = video != null && video.KinectId == id;

            if (GUILayout.Toggle(current, $"Kinect{id}", GUI.skin.button, GUILayout.Width(70)) && !current)
                OpenVideo(id);
        }

        string info = video == null ? videoError :
            playTime < video.FirstTime - 0.1f || playTime > video.LastTime + 0.1f ? "この時刻の映像はありません" :
            float.IsNaN(video.ShownTime) ? "読み込み中…" :
            $"映像 {TimeText(video.ShownTime)}" + (Mathf.Abs(video.ShownTime - playTime) > 0.2f ? "(読み込み中)" : "");
        GUILayout.Label(info, smallStyle);
        GUILayout.EndHorizontal();
        GUILayout.EndArea();

        if (texture != null && texture.width > 2 && video != null && !float.IsNaN(video.ShownTime))
            GUI.DrawTexture(new Rect(frame.x + 2, frame.y + 24, width - 4, height), texture, ScaleMode.ScaleToFit);
    }

    // ------------------------------------------------------------
    // Load
    // ------------------------------------------------------------
    private bool Load()
    {
        if (!HomeExperimentPaths.IsValidRootDirectory(dataRootDirectory, out string error) ||
            !HomeExperimentPaths.IsValidFolderName(env.ExperimentName, out error) ||
            !HomeExperimentPaths.IsValidFolderName(subjectName, out error))
        {
            status = $"データのフォルダ/実験の名前/実験対象者を正しく指定してください。{error}";
            Debug.LogError($"[HomeArrivalCheck] {status}");
            return false;
        }

        string exportRoot = Path.GetDirectoryName(HomeExperimentPaths.GetExportDirectory(dataRootDirectory, env.ExperimentName, subjectName, "x"));
        string name = exportName.Trim();

        if (string.IsNullOrEmpty(name))
        {
            if (Directory.Exists(Path.Combine(exportRoot, "filtered_HeadJoints")))
                name = "filtered_HeadJoints";
            else if (Directory.Exists(exportRoot))
            {
                foreach (string dir in Directory.GetDirectories(exportRoot))
                {
                    if (File.Exists(Path.Combine(dir, "export_params.json")))
                    {
                        name = Path.GetFileName(dir);
                        break;
                    }
                }
            }
        }

        exportFolder = Path.Combine(exportRoot, name);
        string paramsPath = Path.Combine(exportFolder, "export_params.json");

        if (string.IsNullOrEmpty(name) || !File.Exists(paramsPath))
        {
            status = $"シーン5の書き出しがありません。シーン5で書き出してください: {exportFolder}";
            Debug.LogError($"[HomeArrivalCheck] {status}");
            return false;
        }

        ExportParams p = JsonUtility.FromJson<ExportParams>(File.ReadAllText(paramsPath));

        if (p == null || p.targets == null || p.targetCenters == null || p.targetSizes == null || p.targetCenters.Count != p.targets.Count)
        {
            status = "export_params.json に対象物体の中心と大きさがありません。シーン5で書き出し直してください。";
            Debug.LogError($"[HomeArrivalCheck] {status}");
            return false;
        }

        targets.Clear();

        for (int i = 0; i < p.targets.Count; i++)
        {
            Vector3 size = p.targetSizes[i];
            targets.Add(new Target
            {
                name = p.targets[i],
                center = p.targetCenters[i],
                size = size,
                radius = (size.x + size.y + size.z) / 3f / 2f
            });
        }

        visualRoot = new GameObject("ArrivalCheckVisual").transform;
        HomeSkeletonBodyVisual.Style style = new HomeSkeletonBodyVisual.Style
        {
            jointPrefab = jointPrefab,
            lineMaterial = lineMaterial,
            jointScale = jointScale,
            lineWidth = lineWidth,
            showHeadDirection = true
        };

        persons.Clear();

        for (int i = 0; i < p.persons.Count; i++)
        {
            Person person = LoadPerson(p.persons[i], personColors.Length > 0 ? personColors[i % personColors.Length] : Color.white);

            if (person == null)
                continue;

            if (person.hasSkeleton)
                person.visual = new HomeSkeletonBodyVisual(visualRoot, person.name, person.color, style);

            persons.Add(person);
        }

        if (persons.Count == 0)
        {
            status = "人物の書き出しを読めませんでした。Console を確認してください。";
            return false;
        }

        startTime = persons[0].time.Length > 0 ? persons[0].time[0] : 0f;
        endTime = persons[0].time.Length > 0 ? persons[0].time[persons[0].count - 1] : 0f;
        playTime = startTime;

        selectedLine = CreateLine("SelectedLine", 2, 0.02f);
        selectedBox = CreateLine("SelectedBox", 16, 0.015f);

        LoadLabels();
        FindVideos();
        Debug.Log($"[HomeArrivalCheck] {env.ExperimentName}/{subjectName}/{name}: 人物 {persons.Count} 人, 対象物体 {targets.Count} 個 を読み込みました。");
        return true;
    }

    private Person LoadPerson(string name, Color color)
    {
        string gazePath = Path.Combine(exportFolder, $"{name}_gaze.csv");
        string distancePath = Path.Combine(exportFolder, $"{name}_distance.csv");

        if (!File.Exists(gazePath) || !File.Exists(distancePath))
        {
            Debug.LogError($"[HomeArrivalCheck] {name} の gaze.csv / distance.csv がありません: {exportFolder}");
            return null;
        }

        Csv gaze = Csv.Read(gazePath);
        Csv distance = Csv.Read(distancePath);
        int n = gaze.rows.Count;

        if (!gaze.Has("shoulderLeftX"))
        {
            Debug.LogError($"[HomeArrivalCheck] {name}_gaze.csv に肩の位置がありません。シーン5で書き出し直してください。");
            return null;
        }

        Person person = new Person
        {
            name = name,
            color = color,
            count = n,
            time = new float[n],
            present = new bool[n],
            hasGaze = new bool[n],
            origin = new Vector3[n],
            direction = new Vector3[n],
            distance = new float[targets.Count][]
        };

        person.shoulderLeft = new Vector3[n];
        person.shoulderRight = new Vector3[n];

        for (int i = 0; i < n; i++)
        {
            person.time[i] = gaze.Float(i, "timeSec");
            person.present[i] = gaze.Float(i, "present") == 1f;
            person.origin[i] = gaze.Vector(i, "origin");
            person.direction[i] = gaze.Vector(i, "direction");
            person.hasGaze[i] = person.present[i] && gaze.Float(i, "hasGaze") == 1f && IsFinite(person.direction[i]);
            person.shoulderLeft[i] = gaze.Vector(i, "shoulderLeft");
            person.shoulderRight[i] = gaze.Vector(i, "shoulderRight");
        }

        for (int t = 0; t < targets.Count; t++)
        {
            person.distance[t] = new float[n];
            string column = "dist_" + targets[t].name;
            bool has = distance.Has(column);

            for (int i = 0; i < n; i++)
                person.distance[t][i] = has && i < distance.rows.Count && person.present[i] ? distance.Float(i, column) : float.NaN;
        }

        LoadSkeleton(person, Path.Combine(exportFolder, $"{name}_skeleton.csv"));
        return person;
    }

    private void LoadSkeleton(Person person, string path)
    {
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[HomeArrivalCheck] {person.name}_skeleton.csv が無いため、骨格の表示と手で触れる判定はできません(シーン5で書き出し直すと使えます)。");
            return;
        }

        // 行が多い(全フレーム × 32 関節 × 4 列)ので、全部を文字列で持たずに1行ずつ数値にする
        using (StreamReader reader = new StreamReader(path, Encoding.UTF8))
        {
            string headerLine = reader.ReadLine();

            if (headerLine == null)
                return;

            string[] header = headerLine.TrimStart('﻿').Split(',');
            Dictionary<string, int> columns = new Dictionary<string, int>();

            for (int c = 0; c < header.Length; c++)
                columns[header[c]] = c;

            List<string> names = new List<string>();

            for (int j = 0; j < (int)JointId.Count; j++)
            {
                string jointName = ((JointId)j).ToString();

                if (columns.ContainsKey(jointName + "X"))
                    names.Add(jointName);
            }

            int n = person.count;
            int[] start = names.ConvertAll(x => columns[x + "X"]).ToArray();
            int[] handIndex = System.Array.ConvertAll(HandJoints, h => names.IndexOf(h));
            person.jointNames = names.ToArray();
            person.joints = new Vector3[n * names.Count];
            person.jointConfidence = new int[n * names.Count];
            person.hands = new Vector3[n][];
            string line;
            int i = 0;

            while (i < n && (line = reader.ReadLine()) != null)
            {
                if (line.Length == 0)
                    continue;

                string[] f = line.Split(',');
                person.hands[i] = new Vector3[HandJoints.Length];

                for (int j = 0; j < names.Count; j++)
                {
                    int c = start[j];
                    person.joints[i * names.Count + j] = new Vector3(Parse(f, c), Parse(f, c + 1), Parse(f, c + 2));
                    float confidence = Parse(f, c + 3);
                    person.jointConfidence[i * names.Count + j] = float.IsNaN(confidence) ? -1 : (int)confidence;
                }

                for (int h = 0; h < HandJoints.Length; h++)
                    person.hands[i][h] = handIndex[h] >= 0 ? person.joints[i * names.Count + handIndex[h]] : new Vector3(float.NaN, float.NaN, float.NaN);

                i++;
            }

            person.hasSkeleton = names.Count > 0;
        }
    }

    private static float Parse(string[] fields, int index) =>
        index < fields.Length && float.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN;

    // 頭の向きと肩の正面を、その時刻までの smoothSec 秒の平均で滑らかにする(Python の解析と同じく、未来のデータは使わない)
    private void PrepareOrientation(Person person)
    {
        Vector3[] shoulderLeft = person.shoulderLeft;
        Vector3[] shoulderRight = person.shoulderRight;
        int n = person.count;
        float window = Mathf.Max(conditions.smoothSec, 0f);
        person.smoothDirection = new Vector3[n];
        person.forward = new Vector3[n];
        person.hasForward = new bool[n];
        Vector3 sum = Vector3.zero;
        int start = 0;

        for (int i = 0; i < n; i++)
        {
            if (person.hasGaze[i])
                sum += person.direction[i].normalized;

            while (person.time[i] - person.time[start] > window)
            {
                if (person.hasGaze[start])
                    sum -= person.direction[start].normalized;
                start++;
            }

            person.smoothDirection[i] = person.hasGaze[i] && sum.sqrMagnitude > 1e-8f ? sum.normalized : Vector3.zero;
        }

        Vector3 forwardSum = Vector3.zero;
        start = 0;
        Vector3[] raw = new Vector3[n];
        bool[] rawOk = new bool[n];

        for (int i = 0; i < n; i++)
        {
            Vector3 right = shoulderRight[i] - shoulderLeft[i];
            right.y = 0f;

            if (person.present[i] && IsFinite(right) && right.sqrMagnitude > 1e-6f)
            {
                Vector3 f = Vector3.Cross(right.normalized, Vector3.up);
                Vector3 head = person.smoothDirection[i];
                head.y = 0f;

                // 肩の線に垂直な向きは2つあるので、頭の向きに近い方を正面にする
                if (head.sqrMagnitude > 1e-6f && Vector3.Dot(f, head) < 0f)
                    f = -f;

                raw[i] = f;
                rawOk[i] = true;
            }
        }

        for (int i = 0; i < n; i++)
        {
            if (rawOk[i])
                forwardSum += raw[i];

            while (person.time[i] - person.time[start] > window)
            {
                if (rawOk[start])
                    forwardSum -= raw[start];
                start++;
            }

            person.hasForward[i] = rawOk[i] && forwardSum.sqrMagnitude > 1e-8f;
            person.forward[i] = person.hasForward[i] ? forwardSum.normalized : Vector3.zero;
        }
    }

    // ------------------------------------------------------------
    // Compute
    // ------------------------------------------------------------
    private void Compute()
    {
        ArrivalConditions c = conditions;
        float sigma = Mathf.Max(c.sigma, 0.01f);

        foreach (Person person in persons)
        {
            PrepareOrientation(person);
            person.faceAngle = new float[targets.Count][];
            person.look = new float[targets.Count][];

            for (int t = 0; t < targets.Count; t++)
            {
                Target target = targets[t];
                float[] face = new float[person.count];
                float[] look = new float[person.count];

                for (int i = 0; i < person.count; i++)
                {
                    Vector3 from = person.origin[i];
                    Vector3 flat = target.center - from;
                    flat.y = 0f;
                    face[i] = person.hasForward[i] && IsFinite(from) && flat.sqrMagnitude > 1e-6f ? Vector3.Angle(person.forward[i], flat) : float.NaN;

                    if (person.hasGaze[i] && person.smoothDirection[i] != Vector3.zero && IsFinite(from))
                    {
                        Vector3 toTarget = target.center - from;
                        float distance = toTarget.magnitude;
                        float theta = Vector3.Angle(person.smoothDirection[i], toTarget);
                        float rho = Mathf.Asin(Mathf.Clamp01(target.radius / Mathf.Max(distance, 1e-6f))) * Mathf.Rad2Deg;
                        float e = Mathf.Max(theta - rho, 0f);
                        look[i] = Mathf.Exp(-e * e / (2f * sigma * sigma));
                    }
                    else
                    {
                        look[i] = float.NaN;
                    }
                }

                person.faceAngle[t] = face;
                person.look[t] = look;
            }
        }

        candidates.Clear();

        foreach (Person person in persons)
        {
            for (int t = 0; t < targets.Count; t++)
            {
                List<float> touches = person.hasSkeleton ? Touches(person, t) : new List<float>();
                List<float> triggers = c.trigger == Trigger.Touch ? touches : Approaches(person, t);

                foreach (float time in triggers)
                {
                    Candidate candidate = new Candidate { person = person, target = t, time = time };
                    Evaluate(candidate, touches);
                    candidates.Add(candidate);
                }
            }
        }

        candidates.Sort((a, b) => a.time.CompareTo(b.time));

        // 重複: 同じ人物・同じ物体への到達が続いたら後のものを落とす
        if (c.minIntervalSameObjectSec > 0f)
        {
            Dictionary<(Person, int), float> last = new Dictionary<(Person, int), float>();

            foreach (Candidate candidate in candidates)
            {
                if (!candidate.accepted)
                    continue;

                if (last.TryGetValue((candidate.person, candidate.target), out float previous) &&
                    candidate.time - previous < c.minIntervalSameObjectSec)
                {
                    candidate.accepted = false;
                    candidate.reason = $"重複(前の到達から {candidate.time - previous:F1} 秒)";
                    continue;
                }

                last[(candidate.person, candidate.target)] = candidate.time;
            }
        }

        foreach (Candidate candidate in candidates)
            candidate.label = FindLabel(candidate);

        if (selected != null)
            selected = candidates.Find(x => x.person == selected.person && x.target == selected.target && Mathf.Abs(x.time - selected.time) < 0.05f);

        int accepted = candidates.FindAll(x => x.accepted).Count;
        status = $"候補 {candidates.Count} / 到達 {accepted}";
    }

    // leaveDistance より離れていたのを見た後に nearDistance 以内に入った時刻
    private List<float> Approaches(Person person, int t)
    {
        List<float> result = new List<float>();
        float[] d = person.distance[t];
        bool armed = false;
        float lastSeen = float.NegativeInfinity;

        for (int i = 0; i < person.count; i++)
        {
            if (!person.present[i] || float.IsNaN(d[i]))
                continue;

            if (person.time[i] - lastSeen > conditions.missingResetSec)
                armed = false;

            lastSeen = person.time[i];

            if (d[i] > conditions.leaveDistance)
                armed = true;
            else if (d[i] <= conditions.nearDistance && armed)
            {
                result.Add(person.time[i]);
                armed = false;
            }
        }

        return result;
    }

    // 手が物体の箱(+ touchMargin)に入っていた区間の始まり(touchGapSec 以下の途切れはつなげ、touchMinSec 以上続いたもの)
    private List<float> Touches(Person person, int t)
    {
        List<float> result = new List<float>();
        Target target = targets[t];
        Vector3 half = target.size * 0.5f;
        float margin = Mathf.Max(conditions.touchMargin, 0f);
        float episodeStart = float.NaN;
        float episodeEnd = float.NaN;

        for (int i = 0; i < person.count; i++)
        {
            bool touching = false;

            if (person.present[i] && person.hands[i] != null)
            {
                foreach (Vector3 hand in person.hands[i])
                {
                    if (!IsFinite(hand))
                        continue;

                    Vector3 outside = Vector3.Max(Abs(hand - target.center) - half, Vector3.zero);

                    if (outside.magnitude <= margin)
                    {
                        touching = true;
                        break;
                    }
                }
            }

            if (!touching)
                continue;

            float time = person.time[i];

            if (!float.IsNaN(episodeStart) && time - episodeEnd <= conditions.touchGapSec)
            {
                episodeEnd = time;
                continue;
            }

            if (!float.IsNaN(episodeStart) && episodeEnd - episodeStart >= conditions.touchMinSec)
                result.Add(episodeStart);

            episodeStart = time;
            episodeEnd = time;
        }

        if (!float.IsNaN(episodeStart) && episodeEnd - episodeStart >= conditions.touchMinSec)
            result.Add(episodeStart);

        return result;
    }

    private void Evaluate(Candidate candidate, List<float> touches)
    {
        ArrivalConditions c = conditions;
        Person person = candidate.person;
        int t = candidate.target;
        float time = candidate.time;
        int i0 = IndexAt(person, time);
        float[] d = person.distance[t];
        List<string> failed = new List<string>();

        // 近づく速さ
        float before = ValueNear(person, d, time - c.approachSpeedWindowSec);
        candidate.approachSpeed = !float.IsNaN(before) && !float.IsNaN(d[i0]) && c.approachSpeedWindowSec > 0f
            ? (before - d[i0]) / c.approachSpeedWindowSec
            : float.NaN;

        if (c.minApproachSpeed > 0f && !(candidate.approachSpeed >= c.minApproachSpeed))
            failed.Add(float.IsNaN(candidate.approachSpeed) ? "近づく速さ: データなし" : $"近づく速さ {candidate.approachSpeed:F2} < {c.minApproachSpeed:F2} m/s");

        // 滞在
        candidate.dwell = DwellTime(person, d, i0);

        if (c.useDwell && candidate.dwell < c.dwellSec)
            failed.Add($"滞在 {candidate.dwell:F1} < {c.dwellSec:F1} 秒");

        // 体の向き / 注視
        candidate.face = Mean(person, person.faceAngle[t], time + c.faceWindowStartSec, time + c.faceWindowStartSec + c.faceWindowSec);
        candidate.look = Mean(person, person.look[t], time, time + c.lookWindowSec);
        bool facing = !float.IsNaN(candidate.face) && candidate.face <= c.maxFaceAngle;
        bool looking = !float.IsNaN(candidate.look) && candidate.look >= c.minLookPossibility;
        string faceText = float.IsNaN(candidate.face) ? "体の向き: データなし" : $"体の向き {candidate.face:F0} > {c.maxFaceAngle:F0} 度";
        string lookText = float.IsNaN(candidate.look) ? "注視: データなし" : $"注視 {candidate.look:F2} < {c.minLookPossibility:F2}";

        if (c.useFacing && c.useLooking)
        {
            if (c.orientationRule == OrientationRule.Either && !facing && !looking)
                failed.Add($"{faceText} かつ {lookText}");
            else if (c.orientationRule == OrientationRule.Both)
            {
                if (!facing)
                    failed.Add(faceText);
                if (!looking)
                    failed.Add(lookText);
            }
        }
        else if (c.useFacing && !facing)
            failed.Add(faceText);
        else if (c.useLooking && !looking)
            failed.Add(lookText);

        // 触れる
        float delay = float.NaN;

        foreach (float touch in touches)
        {
            if (touch >= time - 0.01f && touch - time <= c.touchWithinSec)
            {
                delay = touch - time;
                break;
            }
        }

        candidate.touchDelay = c.trigger == Trigger.Touch ? 0f : delay;

        if (c.trigger == Trigger.Approach && c.requireTouch)
        {
            if (!person.hasSkeleton)
                failed.Add("触れる: 骨格(skeleton.csv)なし");
            else if (float.IsNaN(delay))
                failed.Add($"{c.touchWithinSec:F1} 秒以内に触れていない");
        }

        candidate.accepted = failed.Count == 0;
        candidate.reason = string.Join(" / ", failed);
    }

    // i0 から dwellRadius 以内にとどまっていた秒数(dwellToleranceSec 以下の外れ・映っていない時間は続けて数える)
    private float DwellTime(Person person, float[] d, int i0)
    {
        float start = person.time[i0];
        float lastInside = start;
        float outsideSince = float.NaN;

        for (int i = i0; i < person.count; i++)
        {
            float time = person.time[i];
            bool inside = person.present[i] && !float.IsNaN(d[i]) && d[i] <= conditions.dwellRadius;

            if (inside)
            {
                lastInside = time;
                outsideSince = float.NaN;
                continue;
            }

            if (float.IsNaN(outsideSince))
                outsideSince = time;

            if (time - outsideSince > conditions.dwellToleranceSec)
                break;
        }

        return lastInside - start;
    }

    private float Mean(Person person, float[] values, float from, float to)
    {
        int i = IndexAt(person, from);
        float sum = 0f;
        int n = 0;

        for (; i < person.count && person.time[i] <= to; i++)
        {
            if (person.time[i] < from || float.IsNaN(values[i]))
                continue;

            sum += values[i];
            n++;
        }

        return n > 0 ? sum / n : float.NaN;
    }

    private float ValueNear(Person person, float[] values, float time)
    {
        int i = IndexAt(person, time);

        for (int k = 0; k < 15; k++)
        {
            if (i - k >= 0 && !float.IsNaN(values[i - k]) && Mathf.Abs(person.time[i - k] - time) <= 0.5f)
                return values[i - k];
            if (i + k < person.count && !float.IsNaN(values[i + k]) && Mathf.Abs(person.time[i + k] - time) <= 0.5f)
                return values[i + k];
        }

        return float.NaN;
    }

    private static int IndexAt(Person person, float time)
    {
        int index = System.Array.BinarySearch(person.time, time);

        if (index < 0)
            index = ~index;

        return Mathf.Clamp(index, 0, person.count - 1);
    }

    // ------------------------------------------------------------
    // Labels
    // ------------------------------------------------------------
    private int FindLabel(Candidate candidate)
    {
        foreach (var l in labels)
        {
            if (l.person == candidate.person.name && l.target == targets[candidate.target].name && Mathf.Abs(l.time - candidate.time) <= 0.5f)
                return l.label;
        }

        return -1;
    }

    private void SetLabel(Candidate candidate, int label)
    {
        string personName = candidate.person.name;
        string targetName = targets[candidate.target].name;
        labels.RemoveAll(l => l.person == personName && l.target == targetName && Mathf.Abs(l.time - candidate.time) <= 0.5f);

        if (label >= 0)
            labels.Add((personName, targetName, candidate.time, label));

        candidate.label = label;
        SaveLabels();
    }

    private void LoadLabels()
    {
        labels.Clear();
        string path = Path.Combine(exportFolder, LabelsFile);

        if (!File.Exists(path))
            return;

        Csv csv = Csv.Read(path);

        for (int i = 0; i < csv.rows.Count; i++)
            labels.Add((csv.Text(i, "person"), csv.Text(i, "object"), csv.Float(i, "timeSec"), (int)csv.Float(i, "label")));

        Debug.Log($"[HomeArrivalCheck] ○×のラベル {labels.Count} 件を読み込みました: {path}");
    }

    private void SaveLabels()
    {
        StringBuilder sb = new StringBuilder("person,object,timeSec,label\n");

        foreach (var l in labels)
            sb.Append(CsvField(l.person)).Append(',').Append(CsvField(l.target)).Append(',').Append(F(l.time)).Append(',').Append(l.label).Append('\n');

        File.WriteAllText(Path.Combine(exportFolder, LabelsFile), sb.ToString(), new UTF8Encoding(true));
    }

    // ------------------------------------------------------------
    // Save
    // ------------------------------------------------------------
    private void Save()
    {
        try
        {
            ArrivalDefinition definition = new ArrivalDefinition
            {
                createdAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                experimentName = env.ExperimentName,
                subjectName = subjectName,
                exportName = Path.GetFileName(exportFolder),
                description = Describe(),
                conditions = conditions,
                trigger = conditions.trigger.ToString().ToLowerInvariant(),
                candidateCount = candidates.Count,
                arrivalCount = candidates.FindAll(x => x.accepted).Count
            };

            File.WriteAllText(Path.Combine(exportFolder, DefinitionFile), JsonUtility.ToJson(definition, true), new UTF8Encoding(false));

            StringBuilder sb = new StringBuilder("person,object,timeSec,timeText,accepted,reason,label,approachSpeed,dwellSec,faceAngleDeg,lookPossibility,touchDelaySec\n");

            foreach (Candidate x in candidates)
            {
                sb.Append(CsvField(x.person.name)).Append(',').Append(CsvField(targets[x.target].name)).Append(',')
                  .Append(F(x.time)).Append(',').Append(TimeText(x.time)).Append(',').Append(x.accepted ? 1 : 0).Append(',')
                  .Append(CsvField(x.reason)).Append(',').Append(x.label >= 0 ? x.label.ToString() : "").Append(',')
                  .Append(F(x.approachSpeed)).Append(',').Append(F(x.dwell)).Append(',').Append(F(x.face)).Append(',')
                  .Append(F(x.look)).Append(',').Append(F(x.touchDelay)).Append('\n');
            }

            File.WriteAllText(Path.Combine(exportFolder, ArrivalsFile), sb.ToString(), new UTF8Encoding(true));
            SaveLabels();
            HomeExperimentPaths.RefreshAssetDatabase();
            status = $"保存しました({definition.arrivalCount} 回の到達): {exportFolder}";
            Debug.Log($"[HomeArrivalCheck] {status}\n到達の条件: {definition.description}");
        }
        catch (System.Exception e)
        {
            status = $"保存できませんでした: {e.Message}";
            Debug.LogError($"[HomeArrivalCheck] {status}\n{e}");
        }
    }

    // 今の条件の説明(Python のレポートにもこの文を出す)
    private string Describe()
    {
        ArrivalConditions c = conditions;
        List<string> parts = new List<string>();

        parts.Add(c.trigger == Trigger.Approach
            ? $"{c.leaveDistance:g} m より離れていた後に {c.nearDistance:g} m 以内に入った時刻(骨盤から物体の箱までの水平距離)"
            : $"手・手先・親指のどれかが物体の箱{(c.touchMargin > 0f ? $"から {c.touchMargin:g} m 以内" : "の中")}に {c.touchMinSec:g} 秒以上入った時刻({c.touchGapSec:g} 秒以下の触れ直しは同じ1回)");

        if (c.minApproachSpeed > 0f)
            parts.Add($"その {c.approachSpeedWindowSec:g} 秒前から {c.minApproachSpeed:g} m/s 以上で近づいている");
        if (c.useDwell)
            parts.Add($"その後 {c.dwellRadius:g} m 以内に {c.dwellSec:g} 秒以上とどまる" + (c.dwellToleranceSec > 0f ? $"({c.dwellToleranceSec:g} 秒以下の外れは続けて数える)" : ""));

        string face = $"体が物体を向いている(きっかけの {c.faceWindowStartSec:g} 秒後から {c.faceWindowSec:g} 秒の肩の正面から物体への角度の平均 ≤ {c.maxFaceAngle:g} 度)";
        string look = $"物体を見ている(きっかけから {c.lookWindowSec:g} 秒の注視可能性の平均 ≥ {c.minLookPossibility:g}、σ {c.sigma:g} 度)";

        if (c.useFacing && c.useLooking)
            parts.Add(c.orientationRule == OrientationRule.Either ? $"{face}か{look}" : $"{face}かつ{look}");
        else if (c.useFacing)
            parts.Add(face);
        else if (c.useLooking)
            parts.Add(look);

        if (c.trigger == Trigger.Approach && c.requireTouch)
            parts.Add($"{c.touchWithinSec:g} 秒以内に手で触れた");
        if (c.minIntervalSameObjectSec > 0f)
            parts.Add($"同じ物体への到達が {c.minIntervalSameObjectSec:g} 秒以内に続いたら後のものは数えない");

        return string.Join("、", parts);
    }

    // ------------------------------------------------------------
    // Playback / visual
    // ------------------------------------------------------------
    private void ApplyFrame()
    {
        foreach (Person person in persons)
        {
            if (person.visual == null)
                continue;

            int i = IndexAt(person, playTime);

            if (!person.present[i] || Mathf.Abs(person.time[i] - playTime) > 0.5f)
            {
                person.visual.SetVisible(false);
                continue;
            }

            person.frameJoints.Clear();
            int count = person.jointNames.Length;

            for (int j = 0; j < count; j++)
            {
                Vector3 position = person.joints[i * count + j];

                if (IsFinite(position))
                    person.frameJoints.Add(new HomeSkeletonJoint { jointId = person.jointNames[j], position = position, confidence = person.jointConfidence[i * count + j] });
            }

            person.visual.Apply(person.frameJoints);
        }

        bool show = selected != null;
        selectedLine.enabled = show;
        selectedBox.enabled = show;

        if (!show)
            return;

        Person p = selected.person;
        Target target = targets[selected.target];
        int k = IndexAt(p, playTime);
        Vector3 from = p.hasSkeleton ? JointPosition(p, k, "Pelvis") : p.origin[k];
        float distance = p.distance[selected.target][k];
        Color color = float.IsNaN(distance) ? Color.gray : distance <= conditions.nearDistance ? Color.green :
                      distance <= conditions.dwellRadius ? Color.yellow : Color.white;
        selectedLine.enabled = IsFinite(from) && p.present[k];

        if (selectedLine.enabled)
        {
            selectedLine.SetPosition(0, from);
            selectedLine.SetPosition(1, target.center);
            selectedLine.startColor = color;
            selectedLine.endColor = color;
        }

        // 物体の箱(床の高さの枠と上の枠)
        Vector3 h = target.size * 0.5f;
        Vector3 c0 = target.center;
        Vector3[] corners =
        {
            c0 + new Vector3(-h.x, -h.y, -h.z), c0 + new Vector3(h.x, -h.y, -h.z), c0 + new Vector3(h.x, -h.y, h.z), c0 + new Vector3(-h.x, -h.y, h.z),
            c0 + new Vector3(-h.x, -h.y, -h.z), c0 + new Vector3(-h.x, h.y, -h.z), c0 + new Vector3(h.x, h.y, -h.z), c0 + new Vector3(h.x, -h.y, -h.z),
            c0 + new Vector3(h.x, h.y, -h.z), c0 + new Vector3(h.x, h.y, h.z), c0 + new Vector3(h.x, -h.y, h.z), c0 + new Vector3(h.x, h.y, h.z),
            c0 + new Vector3(-h.x, h.y, h.z), c0 + new Vector3(-h.x, -h.y, h.z), c0 + new Vector3(-h.x, h.y, h.z), c0 + new Vector3(-h.x, h.y, -h.z)
        };
        selectedBox.SetPositions(corners);
        selectedBox.startColor = color;
        selectedBox.endColor = color;
    }

    private static Vector3 JointPosition(Person person, int frame, string joint)
    {
        int j = System.Array.IndexOf(person.jointNames, joint);
        return j >= 0 ? person.joints[frame * person.jointNames.Length + j] : person.origin[frame];
    }

    private LineRenderer CreateLine(string name, int count, float width)
    {
        LineRenderer line = new GameObject(name).AddComponent<LineRenderer>();
        line.transform.SetParent(visualRoot, false);
        line.sharedMaterial = lineMaterial != null ? lineMaterial : new Material(Shader.Find("Sprites/Default"));
        line.positionCount = count;
        line.startWidth = width;
        line.endWidth = width;
        line.enabled = false;
        return line;
    }

    private void Select(Candidate candidate)
    {
        selected = candidate;
        playTime = Mathf.Max(startTime, candidate.time - preRollSec);
        playing = true;
    }

    // ------------------------------------------------------------
    // GUI
    // ------------------------------------------------------------
    private void OnGUI()
    {
        if (rowStyle == null)
        {
            rowStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = false, richText = true };
            smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true, richText = true };
        }

        float width = 620f;
        GUILayout.BeginArea(new Rect(10, 10, width, Screen.height - 90), GUI.skin.box);
        GUILayout.Label($"6ArrivalCheck  {env?.ExperimentName} / {subjectName} / {Path.GetFileName(exportFolder ?? "")}");
        GUILayout.Label(status, smallStyle);

        if (!loaded)
        {
            GUILayout.EndArea();
            return;
        }

        GUILayout.Label($"条件: {Describe()}", smallStyle);

        // 人物の切り替えと件数
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(personFilter < 0, "全員", GUI.skin.button, GUILayout.Width(60)))
            personFilter = -1;
        for (int i = 0; i < persons.Count; i++)
        {
            if (GUILayout.Toggle(personFilter == i, persons[i].name, GUI.skin.button, GUILayout.Width(90)))
                personFilter = i;
        }
        GUILayout.FlexibleSpace();
        showRejected = GUILayout.Toggle(showRejected, "不採用も表示");
        GUILayout.EndHorizontal();

        List<Candidate> shown = candidates.FindAll(x => (personFilter < 0 || x.person == persons[personFilter]) && (showRejected || x.accepted));
        List<Candidate> inPerson = candidates.FindAll(x => personFilter < 0 || x.person == persons[personFilter]);
        StringBuilder counts = new StringBuilder();

        foreach (Target target in targets)
        {
            int n = inPerson.FindAll(x => x.accepted && targets[x.target] == target).Count;
            counts.Append($"{target.name} {n}  ");
        }

        int tp = inPerson.FindAll(x => x.accepted && x.label == 1).Count;
        int fp = inPerson.FindAll(x => x.accepted && x.label == 0).Count;
        int fn = inPerson.FindAll(x => !x.accepted && x.label == 1).Count;
        int tn = inPerson.FindAll(x => !x.accepted && x.label == 0).Count;
        GUILayout.Label($"到達 {inPerson.FindAll(x => x.accepted).Count} / 候補 {inPerson.Count}   物体ごと: {counts}", smallStyle);
        GUILayout.Label($"○×との一致: 採用して○ {tp} / 採用して× <color=#ff8080>{fp}</color> / 落として○ <color=#ff8080>{fn}</color> / 落として× {tn}" +
                        (tp + fp > 0 ? $"   採用のうち正しい {100f * tp / (tp + fp):F0}%" : "") +
                        (tp + fn > 0 ? $"   ○のうち拾えた {100f * tp / (tp + fn):F0}%" : ""), smallStyle);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save", GUILayout.Width(80)))
            Save();
        if (GUILayout.Button("Recompute", GUILayout.Width(90)))
            dirty = true;
        GUILayout.Label("条件はインスペクターで変える(すぐ計算し直す)。○ = 本当の到達 / × = 違う", smallStyle);
        GUILayout.EndHorizontal();

        listScroll = GUILayout.BeginScrollView(listScroll);

        foreach (Candidate x in shown)
        {
            GUILayout.BeginHorizontal();
            string mark = x == selected ? "▶" : " ";
            string head = x.accepted ? "<color=#80ff80>到達</color>" : "<color=#a0a0a0>不採用</color>";
            string values = $"速さ {F1(x.approachSpeed)} 滞在 {F1(x.dwell)}s 向き {F0(x.face)}° 注視 {F2(x.look)}" +
                            (float.IsNaN(x.touchDelay) ? "" : $" 触れ {x.touchDelay:F1}s");

            if (GUILayout.Button($"{mark} {TimeText(x.time)}  {x.person.name} → {targets[x.target].name}  {head}", rowStyle, GUILayout.Width(330)))
                Select(x);

            GUILayout.Label(values, rowStyle, GUILayout.Width(190));

            if (GUILayout.Toggle(x.label == 1, "○", GUI.skin.button, GUILayout.Width(26)) != (x.label == 1))
                SetLabel(x, x.label == 1 ? -1 : 1);
            if (GUILayout.Toggle(x.label == 0, "×", GUI.skin.button, GUILayout.Width(26)) != (x.label == 0))
                SetLabel(x, x.label == 0 ? -1 : 0);

            GUILayout.EndHorizontal();

            if (!x.accepted && !string.IsNullOrEmpty(x.reason))
                GUILayout.Label($"      <color=#c0c0c0>{x.reason}</color>", smallStyle);
        }

        GUILayout.EndScrollView();
        GUILayout.EndArea();

        DrawTimeline(shown);
        DrawSelectedInfo();
        DrawVideo(new Rect(10, Screen.height - 72, Screen.width - 20, 62));
    }

    private void DrawTimeline(List<Candidate> shown)
    {
        Rect area = new Rect(10, Screen.height - 72, Screen.width - 20, 62);
        GUI.Box(area, "");
        GUILayout.BeginArea(new Rect(area.x + 6, area.y + 4, area.width - 12, area.height - 8));
        GUILayout.BeginHorizontal();

        if (GUILayout.Button(playing ? "Stop" : "Play", GUILayout.Width(60)))
            playing = !playing;

        GUILayout.Label($"{TimeText(playTime)} / {TimeText(endTime)}", GUILayout.Width(150));
        GUILayout.Label("速さ", GUILayout.Width(30));
        playbackSpeed = GUILayout.HorizontalSlider(playbackSpeed, 0.1f, 4f, GUILayout.Width(100));
        GUILayout.Label($"{playbackSpeed:F1}x", GUILayout.Width(40));

        if (GUILayout.Button("-5s", GUILayout.Width(40)))
            playTime = Mathf.Max(startTime, playTime - 5f);
        if (GUILayout.Button("+5s", GUILayout.Width(40)))
            playTime = Mathf.Min(endTime, playTime + 5f);

        GUILayout.EndHorizontal();
        GUILayout.EndArea();

        Rect bar = new Rect(area.x + 10, area.y + 34, area.width - 20, 16);
        float newTime = GUI.HorizontalSlider(bar, playTime, startTime, endTime);

        if (Mathf.Abs(newTime - playTime) > 1e-3f)
            playTime = newTime;

        // 候補の印(緑 = 到達 / 灰 = 不採用 / 赤枠 = ○×と食い違い)
        float span = Mathf.Max(endTime - startTime, 1e-3f);
        Color previous = GUI.color;

        foreach (Candidate x in shown)
        {
            float px = bar.x + (x.time - startTime) / span * bar.width;
            bool mismatch = (x.accepted && x.label == 0) || (!x.accepted && x.label == 1);
            GUI.color = mismatch ? Color.red : x.accepted ? Color.green : Color.gray;
            GUI.DrawTexture(new Rect(px - 1, bar.y - 8, 2, 8), Texture2D.whiteTexture);
        }

        GUI.color = previous;
    }

    // 選んだ候補の、今の時刻の距離・体の向き・注視
    private void DrawSelectedInfo()
    {
        if (selected == null)
            return;

        Person p = selected.person;
        int t = selected.target;
        int i = IndexAt(p, playTime);
        float since = playTime - selected.time;
        string text = $"{p.name} → {targets[t].name}(きっかけ {TimeText(selected.time)}、今 {(since >= 0 ? "+" : "")}{since:F1} 秒)\n" +
                      $"距離 {F2(p.distance[t][i])} m(近い {conditions.nearDistance:g} / 滞在 {conditions.dwellRadius:g})  " +
                      $"体の向き {F0(p.faceAngle[t][i])}°(≤ {conditions.maxFaceAngle:g})  注視 {F2(p.look[t][i])}(≥ {conditions.minLookPossibility:g})\n" +
                      (selected.accepted ? "→ 到達" : $"→ 不採用: {selected.reason}");
        GUIContent content = new GUIContent(text);
        float width = 520f;
        float height = smallStyle.CalcHeight(content, width) + 10f;
        GUI.Box(new Rect(Screen.width - width - 10, 10, width, height), "");
        GUI.Label(new Rect(Screen.width - width - 5, 14, width - 10, height), content, smallStyle);
    }

    // ------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------
    private static bool IsFinite(Vector3 v) =>
        !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

    private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    private static string TimeText(float seconds)
    {
        if (float.IsNaN(seconds))
            return "";

        int m = Mathf.FloorToInt(seconds / 60f);
        return $"{m:00}:{seconds - m * 60f:00.0}";
    }

    private static string F(float v) => float.IsNaN(v) ? "" : v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string F0(float v) => float.IsNaN(v) ? "-" : v.ToString("F0");
    private static string F1(float v) => float.IsNaN(v) ? "-" : v.ToString("F1");
    private static string F2(float v) => float.IsNaN(v) ? "-" : v.ToString("F2");

    private static string CsvField(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    [System.Serializable]
    private class ExportParams
    {
        public List<string> persons = new List<string>();
        public List<string> targets = new List<string>();
        public List<Vector3> targetCenters = new List<Vector3>();
        public List<Vector3> targetSizes = new List<Vector3>();
    }

    [System.Serializable]
    private class ArrivalDefinition
    {
        public string createdAt;
        public string experimentName;
        public string subjectName;
        public string exportName;
        public string description;
        public string trigger;
        public int candidateCount;
        public int arrivalCount;
        public ArrivalConditions conditions;
    }

    // シーン5の CSV(1行目が列名。"" で囲んだ値に対応)
    private class Csv
    {
        public readonly Dictionary<string, int> columns = new Dictionary<string, int>();
        public readonly List<string[]> rows = new List<string[]>();

        public static Csv Read(string path)
        {
            Csv csv = new Csv();
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);

            if (lines.Length == 0)
                return csv;

            string[] header = Split(lines[0].TrimStart('\uFEFF'));

            for (int i = 0; i < header.Length; i++)
                csv.columns[header[i]] = i;

            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length > 0)
                    csv.rows.Add(Split(lines[i]));
            }

            return csv;
        }

        public bool Has(string column) => columns.ContainsKey(column);

        public string Text(int row, string column)
        {
            if (!columns.TryGetValue(column, out int index) || row >= rows.Count || index >= rows[row].Length)
                return "";

            return rows[row][index];
        }

        public float Float(int row, string column)
        {
            string text = Text(row, column);
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : float.NaN;
        }

        public Vector3 Vector(int row, string prefix) =>
            new Vector3(Float(row, prefix + "X"), Float(row, prefix + "Y"), Float(row, prefix + "Z"));

        private static string[] Split(string line)
        {
            if (line.IndexOf('"') < 0)
                return line.Split(',');

            List<string> fields = new List<string>();
            StringBuilder current = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];

                if (quoted)
                {
                    if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else if (ch == '"')
                        quoted = false;
                    else
                        current.Append(ch);
                }
                else if (ch == '"')
                    quoted = true;
                else if (ch == ',')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                    current.Append(ch);
            }

            fields.Add(current.ToString());
            return fields.ToArray();
        }
    }
}
