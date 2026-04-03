using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using UnityEngine;

namespace MainGameVoiceFaceEventBridge
{
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInProcess("KoikatsuSunshine")]
    internal sealed class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.kks.maingame.voicefaceeventbridge";
        public const string PluginName = "MainGameVoiceFaceEventBridge";
        public const string Version = "0.3.1";

        internal static Plugin Instance { get; private set; }
        internal static new ManualLogSource Logger { get; private set; }
        internal static string PluginDir { get; private set; }
        internal static string LogFilePath { get; private set; }
        internal static PluginSettings Settings { get; private set; }
        internal static HSceneProc CurrentProc { get; private set; }

        private static readonly object FileLogLock = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly FieldInfo LstFemaleField = AccessTools.Field(typeof(HSceneProc), "lstFemale");
        private static readonly FieldInfo FaceField = AccessTools.Field(typeof(HSceneProc), "face");
        private static readonly FieldInfo Face1Field = AccessTools.Field(typeof(HSceneProc), "face1");
        private static readonly FieldInfo LstUseAnimInfoField = AccessTools.Field(typeof(HSceneProc), "lstUseAnimInfo");
        private const string StandingBackCategoryName = "立後背位系";
        private const string StandingCategoryName = "立位系";
        private const string BackCategoryName = "後背位系";
        private static readonly Dictionary<string, string[]> PoseCategoryAliases =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { "正常位系", new[] { "正常位" } },
                { "騎乗位系", new[] { "騎乗位", "騎乗" } },
                { "背面騎乗位系", new[] { "背面騎乗位", "背面騎乗", "逆騎乗位", "逆騎乗" } },
                { "後背位系", new[] { "後背位", "立ちバック", "バック" } },
                { "立後背位系", new[] { "立後背位", "立位後背位", "立ちバック", "立位バック", "standing doggy", "standing from behind" } },
                { "座位系", new[] { "座位", "椅子" } },
                { "測位系", new[] { "側位", "横向き", "横" } },
                { "立位系", new[] { "立位", "立ち", "駅弁" } },
                { "フェラ系", new[] { "フェラ", "フェラチオ" } },
                { "手コキ系", new[] { "手コキ" } },
                { "パイズリ系", new[] { "パイズリ" } },
                { "クンニ系", new[] { "クンニ" } },
                { "69系", new[] { "69" } },
                { "足コキ系", new[] { "足コキ" } },
                { "顔面騎乗系", new[] { "顔面騎乗" } },
                { "キス・愛撫系", new[] { "キス", "愛撫" } },
            };
        private static readonly string[] NormalMissionaryInterlockKeywords = { "密着", "しがみ" };
        private static readonly string[] NormalMissionaryInterlockPoseNames = { "Missionary Interlock" };
        private static readonly string[] StandingWallPreferredPoseNames =
        {
            "壁対面片足上げ",
            "Wall Standing Split",
            "Wall Standings Split"
        };
        private static readonly string[] StandingBackStandingKeywords = { "立位", "立ち", "立って", "駅弁", "壁", "Standing", "standing" };
        private static readonly string[] StandingBackBackKeywords = { "後背位", "後背", "バック", "後ろから", "背後", "doggy", "Doggystyle", "from behind", "From Behind" };
        private const string PoseScoreRulesFileName = "pose_score_rules.json";
        private const int PoseScoreRulesCurrentVersion = 3;
        private const int DefaultPoseScoreBase = 20;
        private const int DefaultPoseAdoptThreshold = 35;
        private const int DefaultPoseForceThreshold = 60;
        private const int DefaultBlankMapAddHttpPort = 55982;
        private static readonly string[] DefaultVideoExtensions =
        {
            ".mp4", ".wmv", ".avi", ".mkv", ".mov", ".m4v", ".webm"
        };

        [DataContract]
        private sealed class PoseKeywordScoreToken
        {
            [DataMember(Name = "keyword")]
            public string Keyword;

            [DataMember(Name = "score")]
            public int Score;
        }

        [DataContract]
        private sealed class PoseKeywordScoreRule
        {
            [DataMember(Name = "id")]
            public string RuleId;

            [DataMember(Name = "category")]
            public string Category;

            [DataMember(Name = "poseNames")]
            public string[] PoseNames;

            [DataMember(Name = "tokens")]
            public PoseKeywordScoreToken[] Tokens;

            [DataMember(Name = "priority")]
            public int Priority;

            [DataMember(Name = "enabled", EmitDefaultValue = false)]
            public bool? Enabled;
        }

        [DataContract]
        private sealed class PoseScoreRulesFile
        {
            [DataMember(Name = "version")]
            public int Version;

            [DataMember(Name = "enabled", EmitDefaultValue = false)]
            public bool? Enabled;

            [DataMember(Name = "rulesEnabled", EmitDefaultValue = false)]
            public bool? RulesEnabled;

            [DataMember(Name = "inferRulesEnabled", EmitDefaultValue = false)]
            public bool? InferRulesEnabled;

            [DataMember(Name = "scoreBase")]
            public int ScoreBase;

            [DataMember(Name = "adoptThreshold")]
            public int AdoptThreshold;

            [DataMember(Name = "forceThreshold")]
            public int ForceThreshold;

            [DataMember(Name = "rules")]
            public List<PoseKeywordScoreRule> Rules;

            [DataMember(Name = "inferRules")]
            public List<PoseCategoryInferRule> InferRules;

            [DataMember(Name = "categoryEnabled", EmitDefaultValue = false)]
            public Dictionary<string, bool> CategoryEnabled;
        }

        [DataContract]
        private sealed class PoseCategoryInferRule
        {
            [DataMember(Name = "id")]
            public string RuleId;

            [DataMember(Name = "targetCategory")]
            public string TargetCategory;

            [DataMember(Name = "priority")]
            public int Priority;

            [DataMember(Name = "requiredAll")]
            public string[] RequiredAll;

            [DataMember(Name = "requiredAny")]
            public string[] RequiredAny;

            [DataMember(Name = "excludeAny")]
            public string[] ExcludeAny;

            [DataMember(Name = "enabled", EmitDefaultValue = false)]
            public bool? Enabled;
        }

        [DataContract]
        private sealed class BlankMapAddSettingsSnapshot
        {
            [DataMember(Name = "FolderPlayPath")]
            public string FolderPlayPath;

            [DataMember(Name = "HttpEnabled", EmitDefaultValue = false)]
            public bool? HttpEnabled;

            [DataMember(Name = "HttpPort", EmitDefaultValue = false)]
            public int? HttpPort;
        }

        private sealed class PoseScoreMatch
        {
            public string Category;
            public PoseKeywordScoreRule Rule;
            public List<PoseCategoryEntry> Candidates;
            public int Score;
            public int LongestMatch;
            public int Priority;
            public int MatchedTokenCount;
        }

        private static readonly PoseKeywordScoreRule[] PoseKeywordScoreRules =
        {
            // 正常位系
            CreatePoseScoreRule(
                "normal_missionary_interlock",
                "正常位系",
                220,
                new[] { "Missionary Interlock" },
                CreatePoseScoreToken("密着", 40),
                CreatePoseScoreToken("しがみ", 40)),
            CreatePoseScoreRule(
                "normal_breeding_press",
                "正常位系",
                210,
                new[] { "種付けプレス" },
                CreatePoseScoreToken("種付け", 40),
                CreatePoseScoreToken("孕ませ", 40)),
            CreatePoseScoreRule(
                "normal_kaikyaku",
                "正常位系",
                120,
                new[] { "開脚正常位" },
                CreatePoseScoreToken("開脚", 25),
                CreatePoseScoreToken("開", 10),
                CreatePoseScoreToken("脚", 10),
                CreatePoseScoreToken("足", 10)),
            CreatePoseScoreRule(
                "normal_manguri",
                "正常位系",
                160,
                new[] { "マングリ正常位" },
                CreatePoseScoreToken("マングリ", 40)),
            CreatePoseScoreRule(
                "normal_table",
                "正常位系",
                115,
                new[] { "卓球台正常位" },
                CreatePoseScoreToken("台", 25)),
            CreatePoseScoreRule(
                "normal_knee_hold",
                "正常位系",
                116,
                new[] { "膝抱え正常位" },
                CreatePoseScoreToken("膝", 10),
                CreatePoseScoreToken("抱え", 10)),
            CreatePoseScoreRule(
                "normal_leg_open_wide",
                "正常位系",
                117,
                new[] { "大股開き正常位" },
                CreatePoseScoreToken("股", 25),
                CreatePoseScoreToken("開き", 25),
                CreatePoseScoreToken("開", 10)),
            CreatePoseScoreRule(
                "normal_hip_hold",
                "正常位系",
                116,
                new[] { "腰抱え正常位" },
                CreatePoseScoreToken("腰", 10),
                CreatePoseScoreToken("抱え", 10)),
            CreatePoseScoreRule(
                "normal_bridge",
                "正常位系",
                116,
                new[] { "ブリッジ正常位" },
                CreatePoseScoreToken("ブリッジ", 25)),
            CreatePoseScoreRule(
                "normal_beachball",
                "正常位系",
                116,
                new[] { "ビーチボール正常位" },
                CreatePoseScoreToken("ビーチ", 25),
                CreatePoseScoreToken("ボール", 10)),

            // 後背位系
            CreatePoseScoreRule(
                "back_arm_pull",
                "後背位系",
                170,
                new[] { "腕引っ張り後背位", "Doggystyle Hair-Pull Forced", "Doggy Standing Arm-Grab" },
                CreatePoseScoreToken("腕", 10),
                CreatePoseScoreToken("手", 10),
                CreatePoseScoreToken("引っ張", 40)),
            CreatePoseScoreRule(
                "back_push_against",
                "後背位系",
                175,
                new[] { "押し付けバック" },
                CreatePoseScoreToken("壁", 25),
                CreatePoseScoreToken("押し付け", 40)),
            CreatePoseScoreRule(
                "back_fence",
                "後背位系",
                174,
                new[] { "フェンス後背位" },
                CreatePoseScoreToken("フェンス", 40)),
            CreatePoseScoreRule(
                "back_banana",
                "後背位系",
                118,
                new[] { "バナナボート後背位" },
                CreatePoseScoreToken("バナナ", 25),
                CreatePoseScoreToken("ボート", 10)),
            CreatePoseScoreRule(
                "back_net",
                "後背位系",
                118,
                new[] { "ネット後背位" },
                CreatePoseScoreToken("ネット", 25)),
            CreatePoseScoreRule(
                "back_prone",
                "後背位系",
                118,
                new[] { "伏せ後背位" },
                CreatePoseScoreToken("伏せ", 25)),
            CreatePoseScoreRule(
                "back_float_ring",
                "後背位系",
                118,
                new[] { "浮き輪後背位" },
                CreatePoseScoreToken("浮き輪", 25),
                CreatePoseScoreToken("浮き", 10),
                CreatePoseScoreToken("輪", 10)),
            CreatePoseScoreRule(
                "back_leg_hold",
                "後背位系",
                118,
                new[] { "足抱え後背位" },
                CreatePoseScoreToken("足", 10),
                CreatePoseScoreToken("抱え", 10)),

            // 騎乗位系
            CreatePoseScoreRule(
                "cowgirl_hug",
                "騎乗位系",
                180,
                new[] { "Cowgirl Hug" },
                CreatePoseScoreToken("密着", 40)),
            CreatePoseScoreRule(
                "cowgirl_sofa",
                "騎乗位系",
                118,
                new[] { "ソファ騎乗位" },
                CreatePoseScoreToken("ソファ", 25)),
            CreatePoseScoreRule(
                "cowgirl_hand_hold",
                "騎乗位系",
                118,
                new[] { "手つなぎ騎乗位", "Hand-Holding Cowgirl 2", "Hand-Holding Cowgirl 3" },
                CreatePoseScoreToken("手", 10),
                CreatePoseScoreToken("つなぎ", 25)),
            CreatePoseScoreRule(
                "cowgirl_nipple",
                "騎乗位系",
                118,
                new[] { "乳首責め騎乗位", "Cowgirl Nipple Torture 2" },
                CreatePoseScoreToken("乳首", 40),
                CreatePoseScoreToken("責め", 25)),
            CreatePoseScoreRule(
                "cowgirl_banana",
                "騎乗位系",
                118,
                new[] { "バナナボート騎乗位" },
                CreatePoseScoreToken("バナナ", 25),
                CreatePoseScoreToken("ボート", 10)),

            // 背面騎乗位系
            CreatePoseScoreRule(
                "reverse_cowgirl_wall_hand",
                "背面騎乗位系",
                150,
                new[] { "壁手つき背面騎乗位", "Reverse Cowgirl", "Reverse Cowgirl 3", "Reverse Cowgirl 4", "Reverse Cowgirl 5" },
                CreatePoseScoreToken("壁", 40),
                CreatePoseScoreToken("手つき", 25),
                CreatePoseScoreToken("手", 10),
                CreatePoseScoreToken("つき", 10),
                CreatePoseScoreToken("背中", 25),
                CreatePoseScoreToken("後ろ", 25),
                CreatePoseScoreToken("跨", 25)),

            // 側位(測位)系
            CreatePoseScoreRule(
                "side_desk",
                "測位系",
                118,
                new[] { "机側位" },
                CreatePoseScoreToken("机", 25)),
            CreatePoseScoreRule(
                "side_back",
                "測位系",
                118,
                new[] { "背面側位" },
                CreatePoseScoreToken("背面", 25),
                CreatePoseScoreToken("後ろ", 25)),
            CreatePoseScoreRule(
                "side_princess",
                "測位系",
                180,
                new[] { "お姫様抱っこ側位", "Princess Hug Side Position 2" },
                CreatePoseScoreToken("お姫様", 40),
                CreatePoseScoreToken("抱っこ", 25)),

            // 座位系
            CreatePoseScoreRule(
                "sitting_floor_face",
                "座位系",
                118,
                new[] { "床対面座位" },
                CreatePoseScoreToken("床", 25),
                CreatePoseScoreToken("対面", 25)),
            CreatePoseScoreRule(
                "sitting_foot_hook_face",
                "座位系",
                118,
                new[] { "足掛け対面座位" },
                CreatePoseScoreToken("足掛け", 25),
                CreatePoseScoreToken("足", 10),
                CreatePoseScoreToken("掛け", 10),
                CreatePoseScoreToken("対面", 25)),
            CreatePoseScoreRule(
                "sitting_floor_back",
                "座位系",
                118,
                new[] { "床背面座位" },
                CreatePoseScoreToken("床", 25),
                CreatePoseScoreToken("背面", 25)),
            CreatePoseScoreRule(
                "sitting_seiza_back",
                "座位系",
                118,
                new[] { "正座背面座位" },
                CreatePoseScoreToken("正座", 25),
                CreatePoseScoreToken("背面", 25)),
            CreatePoseScoreRule(
                "sitting_knee_up_back",
                "座位系",
                118,
                new[] { "膝立て背面座位" },
                CreatePoseScoreToken("膝立て", 25),
                CreatePoseScoreToken("膝", 10),
                CreatePoseScoreToken("立て", 10),
                CreatePoseScoreToken("背面", 25)),

            // 立位系 / 立後背位系
            CreatePoseScoreRule(
                "standing_wall_priority",
                "立位系",
                170,
                new[] { "壁対面片足上げ", "Wall Standing Split", "Wall Standings Split" },
                CreatePoseScoreToken("壁", 40)),
            CreatePoseScoreRule(
                "standing_table",
                "立位系",
                118,
                new[] { "卓球台立位" },
                CreatePoseScoreToken("台", 25)),
            CreatePoseScoreRule(
                "standing_back_default",
                "立後背位系",
                140,
                new[] { "立ちバック", "Standing Doggystyle", "Standing From Behind 2", "Doggy Standing Arm-Grab", "Wall Kneeling Doggystyle" },
                CreatePoseScoreToken("立ち", 10),
                CreatePoseScoreToken("立位", 10),
                CreatePoseScoreToken("後ろ", 25),
                CreatePoseScoreToken("バック", 25),
                CreatePoseScoreToken("背後", 25))
        };

        private static readonly string[] PoseInferCommonExclude = { "しない", "じゃない", "やめる", "止める", "やらない" };
        private static readonly string[] PoseInferCowgirlExclude = { "しない", "じゃない", "やめる", "止める", "やらない", "背面", "逆騎乗", "後ろ向き" };
        private static readonly PoseCategoryInferRule[] PoseCategoryInferenceRules =
        {
            // 正常位系
            CreatePoseInferRule(
                "infer_normal_supine_leg_open",
                "正常位系",
                160,
                new[] { "仰向け", "脚" },
                new[] { "開", "広げ" },
                PoseInferCommonExclude),
            CreatePoseInferRule(
                "infer_normal_supine_foot_open",
                "正常位系",
                160,
                new[] { "仰向け", "足" },
                new[] { "開", "広げ" },
                PoseInferCommonExclude),
            CreatePoseInferRule(
                "infer_normal_plain_word",
                "正常位系",
                150,
                new string[0],
                new[] { "正常位", "missionary", "Missionary" },
                PoseInferCommonExclude),
            CreatePoseInferRule(
                "infer_normal_hug_interlock",
                "正常位系",
                155,
                new[] { "密着" },
                new[] { "しがみ", "抱きつ", "抱き合" },
                PoseInferCommonExclude),
            CreatePoseInferRule(
                "infer_normal_seed_press",
                "正常位系",
                170,
                new string[0],
                new[] { "種付け", "孕ませ" },
                PoseInferCommonExclude),

            // 後背位系
            CreatePoseInferRule(
                "infer_back_plain_word",
                "後背位系",
                155,
                new string[0],
                new[] { "後背位", "バック", "後ろから", "背後", "doggy", "Doggystyle", "from behind", "From Behind" },
                PoseInferCommonExclude),
            CreatePoseInferRule(
                "infer_back_arm_pull",
                "後背位系",
                170,
                new[] { "引っ張" },
                new[] { "腕", "手" },
                PoseInferCommonExclude),
            CreatePoseInferRule(
                "infer_back_wall_push",
                "後背位系",
                175,
                new[] { "壁" },
                new[] { "押し付け", "押しつけ", "押し当て", "押し当てる" },
                PoseInferCommonExclude),
            CreatePoseInferRule(
                "infer_back_fours",
                "後背位系",
                145,
                new string[0],
                new[] { "四つん這い", "四つんばい", "伏せ", "うつ伏せ" },
                PoseInferCommonExclude),

            // 騎乗位系
            CreatePoseInferRule(
                "infer_cowgirl_plain_word",
                "騎乗位系",
                155,
                new string[0],
                new[] { "騎乗位", "騎乗" },
                PoseInferCowgirlExclude),
            CreatePoseInferRule(
                "infer_cowgirl_mount_motion",
                "騎乗位系",
                150,
                new string[0],
                new[] { "またが", "跨", "上に乗", "乗って", "乗る" },
                PoseInferCowgirlExclude),
            CreatePoseInferRule(
                "infer_cowgirl_hug_motion",
                "騎乗位系",
                165,
                new[] { "密着" },
                new[] { "またが", "跨", "上に乗", "乗って", "乗る" },
                PoseInferCowgirlExclude),

            // 背面騎乗位系
            CreatePoseInferRule(
                "infer_reverse_cowgirl_plain_word",
                "背面騎乗位系",
                165,
                new string[0],
                new[] { "背面騎乗位", "背面騎乗", "逆騎乗位", "逆騎乗" },
                PoseInferCommonExclude),
            CreatePoseInferRule(
                "infer_reverse_cowgirl_direction_mount",
                "背面騎乗位系",
                160,
                new string[0],
                new[] { "後ろ向き", "背中を向け", "背中向け", "後ろ向いて", "後ろ", "背中" },
                PoseInferCommonExclude),
            CreatePoseInferRule(
                "infer_reverse_cowgirl_mount_word",
                "背面騎乗位系",
                150,
                new[] { "後ろ" },
                new[] { "またが", "跨", "乗って", "乗る" },
                PoseInferCommonExclude),

            // 測位系
            CreatePoseInferRule(
                "infer_side_plain_word",
                "測位系",
                150,
                new string[0],
                new[] { "側位", "横向き", "横で", "横から" },
                PoseInferCommonExclude),
            CreatePoseInferRule(
                "infer_side_princess",
                "測位系",
                170,
                new string[0],
                new[] { "お姫様", "抱っこ", "姫抱っこ" },
                PoseInferCommonExclude),
            CreatePoseInferRule(
                "infer_side_lie_down",
                "測位系",
                140,
                new[] { "横" },
                new[] { "寝", "絡め" },
                PoseInferCommonExclude),

            // 座位系
            CreatePoseInferRule(
                "infer_sitting_plain_word",
                "座位系",
                150,
                new string[0],
                new[] { "座位", "座って", "座る", "座ったまま", "椅子" },
                PoseInferCommonExclude),
            CreatePoseInferRule(
                "infer_sitting_face",
                "座位系",
                155,
                new[] { "対面" },
                new[] { "座位", "座って", "椅子", "床", "向かい合" },
                PoseInferCommonExclude),
            CreatePoseInferRule(
                "infer_sitting_back",
                "座位系",
                155,
                new string[0],
                new[] { "背面座位", "正座", "膝立て", "床背面", "正座背面", "膝立て背面" },
                PoseInferCommonExclude),

            // 立位系
            CreatePoseInferRule(
                "infer_standing_plain_word",
                "立位系",
                150,
                new string[0],
                new[] { "立位", "立って", "立ったまま", "駅弁" },
                PoseInferCommonExclude),
            CreatePoseInferRule(
                "infer_standing_wall",
                "立位系",
                165,
                new[] { "壁" },
                new[] { "立位", "立って", "片足", "立ったまま" },
                PoseInferCommonExclude),

            // 立後背位系
            CreatePoseInferRule(
                "infer_standing_back_plain_word",
                "立後背位系",
                170,
                new string[0],
                new[] { "立ちバック", "立位バック", "Standing Doggystyle", "standing doggy", "standing from behind" },
                PoseInferCommonExclude),
            CreatePoseInferRule(
                "infer_standing_back_combo",
                "立後背位系",
                165,
                new[] { "後ろ" },
                new[] { "立って", "立位", "立ち", "背後", "バック" },
                PoseInferCommonExclude),
            CreatePoseInferRule(
                "infer_standing_back_wall",
                "立後背位系",
                175,
                new[] { "壁" },
                new[] { "後ろ", "背後", "バック", "押し付け" },
                PoseInferCommonExclude)
        };

        private readonly Queue<ExternalVoiceFaceCommand> _commandQueue = new Queue<ExternalVoiceFaceCommand>();
        private readonly object _queueLock = new object();
        private readonly System.Random _random = new System.Random();
        private readonly Dictionary<string, float> _blockLogCooldownByKey = new Dictionary<string, float>();

        private readonly List<Tuple<float, Action>> _delayedActions = new List<Tuple<float, Action>>();
        private readonly Dictionary<string, List<PoseCategoryEntry>> _poseEntriesByCategory =
            new Dictionary<string, List<PoseCategoryEntry>>(StringComparer.Ordinal);
        private int _poseScoreBase = DefaultPoseScoreBase;
        private int _poseAdoptThreshold = DefaultPoseAdoptThreshold;
        private int _poseForceThreshold = DefaultPoseForceThreshold;
        private List<PoseKeywordScoreRule> _poseKeywordScoreRules = new List<PoseKeywordScoreRule>();
        private List<PoseCategoryInferRule> _poseCategoryInferRules = new List<PoseCategoryInferRule>();
        private bool _poseChangeEnabled = true;
        private bool _poseRulesEnabled = true;
        private bool _poseInferRulesEnabled = true;
        private bool _poseCategoriesExpanded = false;
        private bool _poseRulesExpanded = false;
        private bool _poseInferRulesExpanded = false;
        private readonly Dictionary<string, bool> _poseCategoryEnabled = new Dictionary<string, bool>(StringComparer.Ordinal);
        private string _poseScoreRulesFilePath;
        private static Type _configurationManagerType;
        private static MethodInfo _configurationManagerBuildSettingListMethod;

        private const string PoseSonyuClassifiedFileName = "pose_sonyu_classified.json";
        private const string PoseHoushiClassifiedFileName = "pose_houshi_classified.json";
        private static readonly string[] SonyuCategoryNames =
        {
            "正常位系",
            "騎乗位系",
            "背面騎乗位系",
            "後背位系",
            "座位系",
            "測位系",
            "立位系",
            "立後背位系"
        };
        private static readonly string[] HoushiCategoryNames =
        {
            "フェラ系",
            "手コキ系",
            "パイズリ系",
            "クンニ系",
            "69系",
            "足コキ系",
            "顔面騎乗系",
            "キス・愛撫系"
        };
        private const string PoseControlSectionName = "体位制御";
        private const string PoseControlCategoriesSectionName = "体位制御.カテゴリ";
        private const string PoseControlRulesSectionName = "体位制御.ルール";
        private const string PoseControlInferRulesSectionName = "体位制御.推定";

        [DataContract]
        private sealed class PoseClassificationFile
        {
            [DataMember(Name = "categories")]
            public Dictionary<string, List<PoseClassificationItem>> Categories;
        }

        [DataContract]
        private sealed class PoseClassificationItem
        {
            [DataMember(Name = "nameAnimation")]
            public string NameAnimation;

            [DataMember(Name = "modeInt")]
            public int ModeInt;
        }

        private sealed class PoseCategoryEntry
        {
            public string NameAnimation;
            public int ModeInt;
        }

        private Harmony _harmony;
        private ExternalPipeServer _pipeServer;
        private ExternalVoicePlayer _externalVoicePlayer;
        private float _nextProcProbeTime;
        private float _blockGameVoiceUntil;
        private bool _voiceProcStopOverridden;
        private bool _voiceProcStopOriginal;

        private ConfigEntry<bool> _cfgEnabled;
        private ConfigEntry<KeyboardShortcut> _cfgReloadKey;
        private ConfigEntry<float> _cfgPlaybackVolume;
        private ConfigEntry<float> _cfgFemalePlaybackVolume;
        private ConfigEntry<float> _cfgExternalPlaybackPitch;

        // ClothesDetection
        private ConfigEntry<string> _cfgTopKeywords;
        private ConfigEntry<string> _cfgBottomKeywords;
        private ConfigEntry<string> _cfgBraKeywords;
        private ConfigEntry<string> _cfgShortsKeywords;
        private ConfigEntry<string> _cfgGlovesKeywords;
        private ConfigEntry<string> _cfgPanthoseKeywords;
        private ConfigEntry<string> _cfgSocksKeywords;
        private ConfigEntry<string> _cfgShoesKeywords;
        private ConfigEntry<string> _cfgRemoveKeywords;
        private ConfigEntry<string> _cfgShiftKeywords;
        private ConfigEntry<string> _cfgPutOnKeywords;
        private ConfigEntry<string> _cfgRemoveAllKeywords;
        private ConfigEntry<string> _cfgPutOnAllKeywords;
        private ConfigEntry<string> _cfgCoordPattern;
        private ConfigEntry<bool> _cfgEnableVideoPlaybackByResponseText;
        private ConfigEntry<bool> _cfgPoseChangeEnabled;
        private ConfigEntry<bool> _cfgPoseRulesEnabled;
        private ConfigEntry<bool> _cfgPoseInferRulesEnabled;
        private ConfigEntry<bool> _cfgPoseCategoriesExpanded;
        private ConfigEntry<bool> _cfgPoseRulesExpanded;
        private ConfigEntry<bool> _cfgPoseInferRulesExpanded;
        private readonly Dictionary<string, ConfigEntry<bool>> _cfgPoseCategoryEnabledEntries = new Dictionary<string, ConfigEntry<bool>>(StringComparer.Ordinal);
        private readonly Dictionary<string, ConfigEntry<bool>> _cfgPoseRuleEnabledEntries = new Dictionary<string, ConfigEntry<bool>>(StringComparer.Ordinal);
        private readonly Dictionary<string, ConfigEntry<bool>> _cfgPoseInferRuleEnabledEntries = new Dictionary<string, ConfigEntry<bool>>(StringComparer.Ordinal);
        private readonly Dictionary<ConfigEntryBase, ConfigurationManager.ConfigurationManagerAttributes> _cfgPoseReadonlyAttributes =
            new Dictionary<ConfigEntryBase, ConfigurationManager.ConfigurationManagerAttributes>();
        private bool _suppressConfigChangeEvent;
        private bool _suppressPoseConfigChangeEvent;

        private void Awake()
        {
            Instance = this;
            Logger = base.Logger;
            PluginDir = Path.GetDirectoryName(Info.Location);
            LogFilePath = Path.Combine(PluginDir, PluginName + ".log");

            Directory.CreateDirectory(PluginDir);
            File.WriteAllText(
                LogFilePath,
                $"[{DateTime.Now:HH:mm:ss}] === {PluginName} {Version} started ==={Environment.NewLine}",
                Utf8NoBom);

            Settings = SettingsStore.LoadOrCreate(PluginDir, Log, LogWarn, LogError);
            BindConfigEntries();
            ApplySettingsToConfigEntries(Settings);
            SaveConfigFile(reason: "awake");
            LoadPoseScoreRules();
            LoadPoseCategoryEntries();
            _externalVoicePlayer = new ExternalVoicePlayer(Log, LogWarn, LogError);
            LogGuardSettings("awake");

            _harmony = new Harmony(GUID);
            _harmony.PatchAll(typeof(Plugin));
            VoiceGuardHooks.Apply(_harmony, Log, LogWarn, LogError);

            StartOrRestartPipeServer(forceRestart: true);
            Log("awake complete");
        }

        private void OnDestroy()
        {
            StopPipeServer();

            try
            {
                _harmony?.UnpatchSelf();
                _harmony = null;
            }
            catch (Exception ex)
            {
                LogWarn("unpatch failed: " + ex.Message);
            }

            if (_externalVoicePlayer != null)
            {
                _externalVoicePlayer.Dispose();
                _externalVoicePlayer = null;
            }

            CurrentProc = null;
        }

        private void Update()
        {
            if (Settings != null && Settings.EnableCtrlRReload && IsReloadKeyDown())
            {
                ReloadSettings();
            }

            bool wasExternalPlaying = _externalVoicePlayer != null && _externalVoicePlayer.IsPlaying;
            _externalVoicePlayer?.Update();
            bool isExternalPlaying = _externalVoicePlayer != null && _externalVoicePlayer.IsPlaying;
            if (wasExternalPlaying && !isExternalPlaying)
            {
                RestoreVoiceProcStopIfNeeded();
            }

            if (!ShouldBlockGameVoiceEvents())
            {
                RestoreVoiceProcStopIfNeeded();
            }

            if (Settings == null || !Settings.Enabled)
            {
                return;
            }

            ProcessDelayedActions();
            DrainIncomingCommands(8);
        }

        private bool IsReloadKeyDown()
        {
            KeyboardShortcut reloadKey = ResolveReloadKey();
            return reloadKey.IsDown();
        }

        private void BindConfigEntries()
        {
            _cfgEnabled = Config.Bind(
                "General",
                "Enabled",
                true,
                "プラグイン機能を有効化する。");

            _cfgReloadKey = Config.Bind(
                "Input",
                "ReloadKey",
                new KeyboardShortcut(KeyCode.R, KeyCode.LeftControl),
                "設定再読込キー。");

            _cfgPlaybackVolume = Config.Bind(
                "Audio",
                "PlaybackVolume",
                1.0f,
                new ConfigDescription(
                    "外部音声の基本音量（0.0 - 1.0）。",
                    new AcceptableValueRange<float>(0f, 1f)));

            _cfgFemalePlaybackVolume = Config.Bind(
                "Audio",
                "FemalePlaybackVolume",
                1.0f,
                new ConfigDescription(
                    "女の子読み上げ時の音量（-1でPlaybackVolumeを使用）。",
                    new AcceptableValueRange<float>(-1f, 1f)));

            _cfgExternalPlaybackPitch = Config.Bind(
                "Audio",
                "ExternalPlaybackPitch",
                1.0f,
                new ConfigDescription(
                    "外部音声再生ピッチ（0.1 - 3.0）。",
                    new AcceptableValueRange<float>(0.1f, 3f)));

            const string cdSec = "ClothesDetection";
            _cfgTopKeywords      = Config.Bind(cdSec, "TopKeywords",       "上着,ジャケット,トップス",                          "トップス系ワード（カンマ区切り）");
            _cfgBottomKeywords   = Config.Bind(cdSec, "BottomKeywords",    "スカート,ホットパンツ,ミニスカ,ボトムス,ズボン,パンツ", "ボトムス系ワード");
            _cfgBraKeywords      = Config.Bind(cdSec, "BraKeywords",       "ブラ",                                               "ブラ系ワード");
            _cfgShortsKeywords   = Config.Bind(cdSec, "ShortsKeywords",    "パンティー,パンティ",                                 "パンティ系ワード");
            _cfgGlovesKeywords   = Config.Bind(cdSec, "GlovesKeywords",    "グローブ,手袋",                                      "手袋系ワード");
            _cfgPanthoseKeywords = Config.Bind(cdSec, "PanthoseKeywords",  "ガーターベルト,パンスト,ガーター",                    "パンスト系ワード");
            _cfgSocksKeywords    = Config.Bind(cdSec, "SocksKeywords",     "ストッキング,ニーハイ,靴下",                          "靴下系ワード");
            _cfgShoesKeywords    = Config.Bind(cdSec, "ShoesKeywords",     "ハイヒール,スニーカー,サンダル,ヒール,靴",            "靴系ワード");
            _cfgRemoveKeywords   = Config.Bind(cdSec, "RemoveKeywords",    "脱ぐね,脱いじゃう",                                  "脱衣トリガーワード");
            _cfgShiftKeywords    = Config.Bind(cdSec, "ShiftKeywords",     "ずらすね,半脱ぎにするね",                             "ずらしトリガーワード");
            _cfgPutOnKeywords    = Config.Bind(cdSec, "PutOnKeywords",     "着るね,付けるね",                                    "着用トリガーワード");
            _cfgRemoveAllKeywords= Config.Bind(cdSec, "RemoveAllKeywords", "全裸になるね,全部脱ぐね,全部脱いじゃう",              "全脱ぎトリガーワード");
            _cfgPutOnAllKeywords = Config.Bind(cdSec, "PutOnAllKeywords",  "全部着るね",                                         "全着用トリガーワード");
            _cfgCoordPattern     = Config.Bind(cdSec, "CoordPattern",      "に着替えるね",                                       "着替えトリガーパターン");
            _cfgEnableVideoPlaybackByResponseText = Config.Bind(
                "VideoPlayback",
                "EnableVideoPlaybackByResponseText",
                true,
                "response_text解析で動画再生（\"流す\"など）を有効化する。");

            RegisterConfigEntryEvents();
        }

        private void RegisterConfigEntryEvents()
        {
            HookConfigEntryEvent(_cfgEnabled, restartPipe: true);
            HookConfigEntryEvent(_cfgReloadKey, restartPipe: false);
            HookConfigEntryEvent(_cfgPlaybackVolume, restartPipe: false);
            HookConfigEntryEvent(_cfgFemalePlaybackVolume, restartPipe: false);
            HookConfigEntryEvent(_cfgExternalPlaybackPitch, restartPipe: false);
            HookConfigEntryEvent(_cfgTopKeywords, restartPipe: false);
            HookConfigEntryEvent(_cfgBottomKeywords, restartPipe: false);
            HookConfigEntryEvent(_cfgBraKeywords, restartPipe: false);
            HookConfigEntryEvent(_cfgShortsKeywords, restartPipe: false);
            HookConfigEntryEvent(_cfgGlovesKeywords, restartPipe: false);
            HookConfigEntryEvent(_cfgPanthoseKeywords, restartPipe: false);
            HookConfigEntryEvent(_cfgSocksKeywords, restartPipe: false);
            HookConfigEntryEvent(_cfgShoesKeywords, restartPipe: false);
            HookConfigEntryEvent(_cfgRemoveKeywords, restartPipe: false);
            HookConfigEntryEvent(_cfgShiftKeywords, restartPipe: false);
            HookConfigEntryEvent(_cfgPutOnKeywords, restartPipe: false);
            HookConfigEntryEvent(_cfgRemoveAllKeywords, restartPipe: false);
            HookConfigEntryEvent(_cfgPutOnAllKeywords, restartPipe: false);
            HookConfigEntryEvent(_cfgCoordPattern, restartPipe: false);
            HookConfigEntryEvent(_cfgEnableVideoPlaybackByResponseText, restartPipe: false);
        }

        private void EnsurePoseControlConfigEntries()
        {
            EnsurePoseGlobalConfigEntry();
            EnsurePoseCategoryConfigEntries();
            EnsurePoseRuleConfigEntries();
            EnsurePoseInferRuleConfigEntries();
            UpdatePoseControlReadOnlyState();
        }

        private void EnsurePoseGlobalConfigEntry()
        {
            if (_cfgPoseChangeEnabled != null)
            {
                return;
            }

            _cfgPoseChangeEnabled = Config.Bind(
                PoseControlSectionName,
                "【最上位】体位変更を有効化",
                _poseChangeEnabled,
                BuildPoseControlConfigDescription("このチェックがOFFの間は、体位変更の全項目が無効（グレーアウト）になります。", order: 1100, readOnly: false, isAdvanced: false));
            RegisterPoseReadonlyAttribute(_cfgPoseChangeEnabled);
            _cfgPoseChangeEnabled.SettingChanged += (_, __) =>
            {
                if (_suppressPoseConfigChangeEvent)
                {
                    return;
                }

                _poseChangeEnabled = _cfgPoseChangeEnabled != null && _cfgPoseChangeEnabled.Value;
                if (_poseChangeEnabled)
                {
                    ExpandPoseSectionsOnEnable();
                }
                UpdatePoseControlReadOnlyState();
                RefreshConfigurationManagerSettingList("pose-global-toggle");
                SaveCurrentPoseScoreRulesToFile("config-manager:pose-global");
            };
        }

        private void EnsurePoseCategoryConfigEntries()
        {
            EnsurePoseCategorySectionControlEntries();

            var categories = _poseCategoryEnabled.Keys
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            int order = 900;
            foreach (string category in categories)
            {
                if (string.IsNullOrWhiteSpace(category))
                {
                    continue;
                }

                if (_cfgPoseCategoryEnabledEntries.ContainsKey(category))
                {
                    order--;
                    continue;
                }

                bool enabled = IsPoseCategoryEnabled(category);
                var entry = Config.Bind(
                    PoseControlCategoriesSectionName,
                    category,
                    enabled,
                    BuildPoseControlConfigDescription($"カテゴリ '{category}' の有効/無効。", order, readOnly: !_poseChangeEnabled));
                _cfgPoseCategoryEnabledEntries[category] = entry;
                RegisterPoseReadonlyAttribute(entry);
                entry.SettingChanged += (_, __) =>
                {
                    if (_suppressPoseConfigChangeEvent)
                    {
                        return;
                    }

                    _poseCategoryEnabled[category] = entry.Value;
                    RefreshConfigurationManagerSettingList("pose-category-toggle");
                    SaveCurrentPoseScoreRulesToFile("config-manager:pose-category");
                };
                order--;
            }
        }

        private void EnsurePoseCategorySectionControlEntries()
        {
            if (_cfgPoseCategoriesExpanded == null)
            {
                _cfgPoseCategoriesExpanded = Config.Bind(
                    PoseControlCategoriesSectionName,
                    "【表示】カテゴリ一覧",
                    _poseCategoriesExpanded,
                    BuildPoseControlToggleButtonDescription(
                        openLabel: "カテゴリ一覧を開く",
                        closeLabel: "カテゴリ一覧を閉じる",
                        order: 990,
                        readOnly: !_poseChangeEnabled));
                RegisterPoseReadonlyAttribute(_cfgPoseCategoriesExpanded);
                _cfgPoseCategoriesExpanded.SettingChanged += (_, __) =>
                {
                    if (_suppressPoseConfigChangeEvent)
                    {
                        return;
                    }

                    _poseCategoriesExpanded = _cfgPoseCategoriesExpanded != null && _cfgPoseCategoriesExpanded.Value;
                    UpdatePoseControlReadOnlyState();
                    RefreshConfigurationManagerSettingList("pose-category-expand-toggle");
                };
            }
            _poseCategoriesExpanded = _cfgPoseCategoriesExpanded != null && _cfgPoseCategoriesExpanded.Value;
        }

        private void EnsurePoseRuleConfigEntries()
        {
            EnsurePoseRuleSectionControlEntries();

            var rules = (_poseKeywordScoreRules ?? new List<PoseKeywordScoreRule>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.RuleId))
                .OrderByDescending(r => r.Priority)
                .ThenBy(r => r.RuleId, StringComparer.Ordinal)
                .ToArray();

            int order = 800;
            int index = 1;
            foreach (PoseKeywordScoreRule rule in rules)
            {
                if (_cfgPoseRuleEnabledEntries.ContainsKey(rule.RuleId))
                {
                    order--;
                    index++;
                    continue;
                }

                bool enabled = rule.Enabled != false;
                string key = BuildPoseRuleEntryLabel(rule, index);
                string ruleId = rule.RuleId;
                var entry = Config.Bind(
                    PoseControlRulesSectionName,
                    key,
                    enabled,
                    BuildPoseControlConfigDescription($"[{rule.Category}] ID={ruleId}", order, readOnly: !_poseChangeEnabled || !_poseRulesEnabled));
                _cfgPoseRuleEnabledEntries[ruleId] = entry;
                RegisterPoseReadonlyAttribute(entry);
                entry.SettingChanged += (_, __) =>
                {
                    if (_suppressPoseConfigChangeEvent)
                    {
                        return;
                    }

                    PoseKeywordScoreRule target = _poseKeywordScoreRules.FirstOrDefault(x => x != null && string.Equals(x.RuleId, ruleId, StringComparison.Ordinal));
                    if (target != null)
                    {
                        target.Enabled = entry.Value;
                    }
                    RefreshConfigurationManagerSettingList("pose-rule-toggle");
                    SaveCurrentPoseScoreRulesToFile("config-manager:pose-rule");
                };
                order--;
                index++;
            }
        }

        private void EnsurePoseRuleSectionControlEntries()
        {
            if (_cfgPoseRulesEnabled == null)
            {
                _cfgPoseRulesEnabled = Config.Bind(
                    PoseControlRulesSectionName,
                    "【全体】ルールを有効化",
                    _poseRulesEnabled,
                    BuildPoseControlConfigDescription("体位ルール一覧をまとめてON/OFFします。", order: 1000, readOnly: false, isAdvanced: false));
                RegisterPoseReadonlyAttribute(_cfgPoseRulesEnabled);
                _cfgPoseRulesEnabled.SettingChanged += (_, __) =>
                {
                    if (_suppressPoseConfigChangeEvent)
                    {
                        return;
                    }

                    _poseRulesEnabled = _cfgPoseRulesEnabled != null && _cfgPoseRulesEnabled.Value;
                    if (_poseRulesEnabled && !_poseRulesExpanded)
                    {
                        SetPoseSectionExpanded(ref _poseRulesExpanded, _cfgPoseRulesExpanded, true);
                    }
                    UpdatePoseControlReadOnlyState();
                    RefreshConfigurationManagerSettingList("pose-rules-global-toggle");
                    SaveCurrentPoseScoreRulesToFile("config-manager:pose-rules-global");
                };
            }

            if (_cfgPoseRulesExpanded == null)
            {
                _cfgPoseRulesExpanded = Config.Bind(
                    PoseControlRulesSectionName,
                    "【表示】ルール一覧",
                    _poseRulesExpanded,
                    BuildPoseControlToggleButtonDescription(
                        openLabel: "ルール一覧を開く",
                        closeLabel: "ルール一覧を閉じる",
                        order: 990,
                        readOnly: !_poseChangeEnabled || !_poseRulesEnabled));
                RegisterPoseReadonlyAttribute(_cfgPoseRulesExpanded);
                _cfgPoseRulesExpanded.SettingChanged += (_, __) =>
                {
                    if (_suppressPoseConfigChangeEvent)
                    {
                        return;
                    }

                    _poseRulesExpanded = _cfgPoseRulesExpanded != null && _cfgPoseRulesExpanded.Value;
                    UpdatePoseControlReadOnlyState();
                    RefreshConfigurationManagerSettingList("pose-rules-expand-toggle");
                };
            }
            _poseRulesExpanded = _cfgPoseRulesExpanded != null && _cfgPoseRulesExpanded.Value;
        }

        private void EnsurePoseInferRuleConfigEntries()
        {
            EnsurePoseInferSectionControlEntries();

            var rules = (_poseCategoryInferRules ?? new List<PoseCategoryInferRule>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.RuleId))
                .OrderByDescending(r => r.Priority)
                .ThenBy(r => r.RuleId, StringComparer.Ordinal)
                .ToArray();

            int order = 700;
            int index = 1;
            foreach (PoseCategoryInferRule rule in rules)
            {
                if (_cfgPoseInferRuleEnabledEntries.ContainsKey(rule.RuleId))
                {
                    order--;
                    index++;
                    continue;
                }

                bool enabled = rule.Enabled != false;
                string key = BuildPoseInferRuleEntryLabel(rule, index);
                string ruleId = rule.RuleId;
                var entry = Config.Bind(
                    PoseControlInferRulesSectionName,
                    key,
                    enabled,
                    BuildPoseControlConfigDescription($"[{rule.TargetCategory}] ID={ruleId}", order, readOnly: !_poseChangeEnabled || !_poseInferRulesEnabled));
                _cfgPoseInferRuleEnabledEntries[ruleId] = entry;
                RegisterPoseReadonlyAttribute(entry);
                entry.SettingChanged += (_, __) =>
                {
                    if (_suppressPoseConfigChangeEvent)
                    {
                        return;
                    }

                    PoseCategoryInferRule target = _poseCategoryInferRules.FirstOrDefault(x => x != null && string.Equals(x.RuleId, ruleId, StringComparison.Ordinal));
                    if (target != null)
                    {
                        target.Enabled = entry.Value;
                    }
                    RefreshConfigurationManagerSettingList("pose-infer-toggle");
                    SaveCurrentPoseScoreRulesToFile("config-manager:pose-infer-rule");
                };
                order--;
                index++;
            }
        }

        private void EnsurePoseInferSectionControlEntries()
        {
            if (_cfgPoseInferRulesEnabled == null)
            {
                _cfgPoseInferRulesEnabled = Config.Bind(
                    PoseControlInferRulesSectionName,
                    "【全体】推定ルールを有効化",
                    _poseInferRulesEnabled,
                    BuildPoseControlConfigDescription("体位推定ルール一覧をまとめてON/OFFします。", order: 1000, readOnly: false, isAdvanced: false));
                RegisterPoseReadonlyAttribute(_cfgPoseInferRulesEnabled);
                _cfgPoseInferRulesEnabled.SettingChanged += (_, __) =>
                {
                    if (_suppressPoseConfigChangeEvent)
                    {
                        return;
                    }

                    _poseInferRulesEnabled = _cfgPoseInferRulesEnabled != null && _cfgPoseInferRulesEnabled.Value;
                    if (_poseInferRulesEnabled && !_poseInferRulesExpanded)
                    {
                        SetPoseSectionExpanded(ref _poseInferRulesExpanded, _cfgPoseInferRulesExpanded, true);
                    }
                    UpdatePoseControlReadOnlyState();
                    RefreshConfigurationManagerSettingList("pose-infer-global-toggle");
                    SaveCurrentPoseScoreRulesToFile("config-manager:pose-infer-global");
                };
            }

            if (_cfgPoseInferRulesExpanded == null)
            {
                _cfgPoseInferRulesExpanded = Config.Bind(
                    PoseControlInferRulesSectionName,
                    "【表示】推定ルール一覧",
                    _poseInferRulesExpanded,
                    BuildPoseControlToggleButtonDescription(
                        openLabel: "推定ルール一覧を開く",
                        closeLabel: "推定ルール一覧を閉じる",
                        order: 990,
                        readOnly: !_poseChangeEnabled || !_poseInferRulesEnabled));
                RegisterPoseReadonlyAttribute(_cfgPoseInferRulesExpanded);
                _cfgPoseInferRulesExpanded.SettingChanged += (_, __) =>
                {
                    if (_suppressPoseConfigChangeEvent)
                    {
                        return;
                    }

                    _poseInferRulesExpanded = _cfgPoseInferRulesExpanded != null && _cfgPoseInferRulesExpanded.Value;
                    UpdatePoseControlReadOnlyState();
                    RefreshConfigurationManagerSettingList("pose-infer-expand-toggle");
                };
            }
            _poseInferRulesExpanded = _cfgPoseInferRulesExpanded != null && _cfgPoseInferRulesExpanded.Value;
        }

        private ConfigDescription BuildPoseControlToggleButtonDescription(string openLabel, string closeLabel, int order, bool readOnly)
        {
            var attrs = new ConfigurationManager.ConfigurationManagerAttributes
            {
                Order = order,
                ReadOnly = readOnly,
                HideSettingName = true,
                HideDefaultButton = true
            };
            attrs.CustomDrawer = entryBase =>
            {
                var boolEntry = entryBase as ConfigEntry<bool>;
                if (boolEntry == null)
                {
                    return;
                }

                bool isOpen = boolEntry.Value;
                string buttonText = isOpen ? closeLabel : openLabel;
                bool prevEnabled = GUI.enabled;
                if (attrs.ReadOnly == true)
                {
                    GUI.enabled = false;
                }

                if (GUILayout.Button(buttonText, GUILayout.ExpandWidth(true)))
                {
                    boolEntry.Value = !boolEntry.Value;
                }

                GUI.enabled = prevEnabled;
            };
            return new ConfigDescription(string.Empty, null, attrs);
        }

        private ConfigDescription BuildPoseControlConfigDescription(string description, int order, bool readOnly, bool? browsable = null, bool? isAdvanced = null)
        {
            var attrs = new ConfigurationManager.ConfigurationManagerAttributes
            {
                Order = order,
                ReadOnly = readOnly
            };
            if (browsable.HasValue)
            {
                attrs.Browsable = browsable.Value;
            }
            if (isAdvanced.HasValue)
            {
                attrs.IsAdvanced = isAdvanced.Value;
            }
            return new ConfigDescription(description, null, attrs);
        }

        private void RegisterPoseReadonlyAttribute(ConfigEntryBase entryBase)
        {
            if (entryBase == null)
            {
                return;
            }

            var tags = entryBase.Description != null ? entryBase.Description.Tags : null;
            if (tags == null)
            {
                return;
            }

            foreach (object tag in tags)
            {
                var attr = tag as ConfigurationManager.ConfigurationManagerAttributes;
                if (attr != null)
                {
                    _cfgPoseReadonlyAttributes[entryBase] = attr;
                    break;
                }
            }
        }

        private void UpdatePoseControlReadOnlyState()
        {
            bool showCategories = _poseCategoriesExpanded;
            bool showRules = _poseRulesExpanded;
            bool showInferRules = _poseInferRulesExpanded;

            foreach (var pair in _cfgPoseReadonlyAttributes)
            {
                if (pair.Key == null || pair.Value == null)
                {
                    continue;
                }

                // グローバルトグルは常に編集可能
                if (_cfgPoseChangeEnabled != null && ReferenceEquals(pair.Key, _cfgPoseChangeEnabled))
                {
                    pair.Value.ReadOnly = false;
                    pair.Value.Browsable = true;
                    continue;
                }
                if (_cfgPoseCategoriesExpanded != null && ReferenceEquals(pair.Key, _cfgPoseCategoriesExpanded))
                {
                    pair.Value.ReadOnly = !_poseChangeEnabled;
                    pair.Value.Browsable = true;
                    continue;
                }
                if (_cfgPoseRulesEnabled != null && ReferenceEquals(pair.Key, _cfgPoseRulesEnabled))
                {
                    pair.Value.ReadOnly = !_poseChangeEnabled;
                    pair.Value.Browsable = true;
                    continue;
                }
                if (_cfgPoseRulesExpanded != null && ReferenceEquals(pair.Key, _cfgPoseRulesExpanded))
                {
                    pair.Value.ReadOnly = !_poseChangeEnabled || !_poseRulesEnabled;
                    pair.Value.Browsable = true;
                    continue;
                }
                if (_cfgPoseInferRulesEnabled != null && ReferenceEquals(pair.Key, _cfgPoseInferRulesEnabled))
                {
                    pair.Value.ReadOnly = !_poseChangeEnabled;
                    pair.Value.Browsable = true;
                    continue;
                }
                if (_cfgPoseInferRulesExpanded != null && ReferenceEquals(pair.Key, _cfgPoseInferRulesExpanded))
                {
                    pair.Value.ReadOnly = !_poseChangeEnabled || !_poseInferRulesEnabled;
                    pair.Value.Browsable = true;
                    continue;
                }

                bool isCategoryEntry = _cfgPoseCategoryEnabledEntries.Values.Any(v => ReferenceEquals(v, pair.Key));
                bool isRuleEntry = _cfgPoseRuleEnabledEntries.Values.Any(v => ReferenceEquals(v, pair.Key));
                bool isInferRuleEntry = _cfgPoseInferRuleEnabledEntries.Values.Any(v => ReferenceEquals(v, pair.Key));
                if (isCategoryEntry)
                {
                    pair.Value.ReadOnly = !_poseChangeEnabled;
                    pair.Value.Browsable = showCategories;
                    continue;
                }
                if (isRuleEntry)
                {
                    pair.Value.ReadOnly = !_poseChangeEnabled || !_poseRulesEnabled;
                    pair.Value.Browsable = showRules;
                    continue;
                }
                if (isInferRuleEntry)
                {
                    pair.Value.ReadOnly = !_poseChangeEnabled || !_poseInferRulesEnabled;
                    pair.Value.Browsable = showInferRules;
                    continue;
                }

                pair.Value.ReadOnly = !_poseChangeEnabled;
                pair.Value.Browsable = true;
            }
        }

        private void ExpandPoseSectionsOnEnable()
        {
            SetPoseSectionExpanded(ref _poseCategoriesExpanded, _cfgPoseCategoriesExpanded, true);
            SetPoseSectionExpanded(ref _poseRulesExpanded, _cfgPoseRulesExpanded, true);
            SetPoseSectionExpanded(ref _poseInferRulesExpanded, _cfgPoseInferRulesExpanded, true);
        }

        private void SetPoseSectionExpanded(ref bool stateField, ConfigEntry<bool> entry, bool value)
        {
            if (stateField == value && (entry == null || entry.Value == value))
            {
                return;
            }

            bool previousSuppress = _suppressPoseConfigChangeEvent;
            _suppressPoseConfigChangeEvent = true;
            try
            {
                stateField = value;
                if (entry != null)
                {
                    entry.Value = value;
                }
            }
            finally
            {
                _suppressPoseConfigChangeEvent = previousSuppress;
            }
        }

        private void RefreshConfigurationManagerSettingList(string reason)
        {
            try
            {
                if (_configurationManagerType == null)
                {
                    _configurationManagerType = Type.GetType("ConfigurationManager.ConfigurationManager, ConfigurationManager");
                    if (_configurationManagerType == null)
                    {
                        return;
                    }
                }

                if (_configurationManagerBuildSettingListMethod == null)
                {
                    _configurationManagerBuildSettingListMethod =
                        _configurationManagerType.GetMethod("BuildSettingList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (_configurationManagerBuildSettingListMethod == null)
                    {
                        return;
                    }
                }

                UnityEngine.Object[] managers = UnityEngine.Object.FindObjectsOfType(_configurationManagerType);
                if (managers == null || managers.Length <= 0)
                {
                    return;
                }

                foreach (UnityEngine.Object manager in managers)
                {
                    _configurationManagerBuildSettingListMethod.Invoke(manager, null);
                }
            }
            catch (Exception ex)
            {
                LogWarn("[pose-cfgui] refresh failed reason=" + reason + " message=" + ex.Message);
            }
        }

        private void SyncPoseControlConfigEntriesFromRuntime()
        {
            _suppressPoseConfigChangeEvent = true;
            try
            {
                if (_cfgPoseChangeEnabled != null)
                {
                    _cfgPoseChangeEnabled.Value = _poseChangeEnabled;
                }
                if (_cfgPoseRulesEnabled != null)
                {
                    _cfgPoseRulesEnabled.Value = _poseRulesEnabled;
                }
                if (_cfgPoseInferRulesEnabled != null)
                {
                    _cfgPoseInferRulesEnabled.Value = _poseInferRulesEnabled;
                }
                foreach (var pair in _cfgPoseCategoryEnabledEntries)
                {
                    bool enabled = IsPoseCategoryEnabled(pair.Key);
                    if (pair.Value != null)
                    {
                        pair.Value.Value = enabled;
                    }
                }

                foreach (var pair in _cfgPoseRuleEnabledEntries)
                {
                    PoseKeywordScoreRule rule = _poseKeywordScoreRules.FirstOrDefault(r => r != null && string.Equals(r.RuleId, pair.Key, StringComparison.Ordinal));
                    bool enabled = rule == null || rule.Enabled != false;
                    if (pair.Value != null)
                    {
                        pair.Value.Value = enabled;
                    }
                }

                foreach (var pair in _cfgPoseInferRuleEnabledEntries)
                {
                    PoseCategoryInferRule rule = _poseCategoryInferRules.FirstOrDefault(r => r != null && string.Equals(r.RuleId, pair.Key, StringComparison.Ordinal));
                    bool enabled = rule == null || rule.Enabled != false;
                    if (pair.Value != null)
                    {
                        pair.Value.Value = enabled;
                    }
                }
            }
            finally
            {
                _suppressPoseConfigChangeEvent = false;
                UpdatePoseControlReadOnlyState();
            }
        }

        private void HookConfigEntryEvent<T>(ConfigEntry<T> entry, bool restartPipe)
        {
            if (entry == null)
            {
                return;
            }

            entry.SettingChanged += (_, __) => OnConfigEntryChanged(restartPipe);
        }

        private void OnConfigEntryChanged(bool restartPipe)
        {
            if (_suppressConfigChangeEvent)
            {
                return;
            }

            ApplyConfigEntryOverridesToSettings();
            SaveSettingsToConfigJson("config-manager");
            Settings = SettingsStore.LoadOrCreate(PluginDir, Log, LogWarn, LogError);
            ApplySettingsToConfigEntries(Settings);
            SaveConfigFile(reason: "config-manager-sync");
            Log("[cfg] change applied and persisted to config.json");
            if (restartPipe)
            {
                StartOrRestartPipeServer(forceRestart: false);
            }
        }

        private void ApplySettingsToConfigEntries(PluginSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            _suppressConfigChangeEvent = true;
            try
            {
                if (_cfgEnabled != null) _cfgEnabled.Value = settings.Enabled;
                if (_cfgReloadKey != null) _cfgReloadKey.Value = ParseKeyboardShortcut(settings.ReloadKey, _cfgReloadKey.Value);
                if (_cfgPlaybackVolume != null) _cfgPlaybackVolume.Value = Mathf.Clamp01(settings.PlaybackVolume);
                if (_cfgFemalePlaybackVolume != null) _cfgFemalePlaybackVolume.Value = Mathf.Clamp(settings.FemalePlaybackVolume, -1f, 1f);
                if (_cfgExternalPlaybackPitch != null) _cfgExternalPlaybackPitch.Value = Mathf.Clamp(settings.ExternalPlaybackPitch, 0.1f, 3f);

                if (_cfgTopKeywords != null) _cfgTopKeywords.Value = settings.TopKeywords ?? _cfgTopKeywords.Value;
                if (_cfgBottomKeywords != null) _cfgBottomKeywords.Value = settings.BottomKeywords ?? _cfgBottomKeywords.Value;
                if (_cfgBraKeywords != null) _cfgBraKeywords.Value = settings.BraKeywords ?? _cfgBraKeywords.Value;
                if (_cfgShortsKeywords != null) _cfgShortsKeywords.Value = settings.ShortsKeywords ?? _cfgShortsKeywords.Value;
                if (_cfgGlovesKeywords != null) _cfgGlovesKeywords.Value = settings.GlovesKeywords ?? _cfgGlovesKeywords.Value;
                if (_cfgPanthoseKeywords != null) _cfgPanthoseKeywords.Value = settings.PanthoseKeywords ?? _cfgPanthoseKeywords.Value;
                if (_cfgSocksKeywords != null) _cfgSocksKeywords.Value = settings.SocksKeywords ?? _cfgSocksKeywords.Value;
                if (_cfgShoesKeywords != null) _cfgShoesKeywords.Value = settings.ShoesKeywords ?? _cfgShoesKeywords.Value;
                if (_cfgRemoveKeywords != null) _cfgRemoveKeywords.Value = settings.RemoveKeywords ?? _cfgRemoveKeywords.Value;
                if (_cfgShiftKeywords != null) _cfgShiftKeywords.Value = settings.ShiftKeywords ?? _cfgShiftKeywords.Value;
                if (_cfgPutOnKeywords != null) _cfgPutOnKeywords.Value = settings.PutOnKeywords ?? _cfgPutOnKeywords.Value;
                if (_cfgRemoveAllKeywords != null) _cfgRemoveAllKeywords.Value = settings.RemoveAllKeywords ?? _cfgRemoveAllKeywords.Value;
                if (_cfgPutOnAllKeywords != null) _cfgPutOnAllKeywords.Value = settings.PutOnAllKeywords ?? _cfgPutOnAllKeywords.Value;
                if (_cfgCoordPattern != null) _cfgCoordPattern.Value = settings.CoordPattern ?? _cfgCoordPattern.Value;
                if (_cfgEnableVideoPlaybackByResponseText != null) _cfgEnableVideoPlaybackByResponseText.Value = settings.EnableVideoPlaybackByResponseText;
            }
            finally
            {
                _suppressConfigChangeEvent = false;
            }
        }

        private void ApplyConfigEntryOverridesToSettings()
        {
            if (Settings == null)
            {
                return;
            }

            Settings.Enabled = _cfgEnabled != null ? _cfgEnabled.Value : Settings.Enabled;
            Settings.ReloadKey = ResolveReloadKey().ToString();
            Settings.PlaybackVolume = _cfgPlaybackVolume != null ? Mathf.Clamp01(_cfgPlaybackVolume.Value) : Settings.PlaybackVolume;
            Settings.FemalePlaybackVolume = _cfgFemalePlaybackVolume != null
                ? Mathf.Clamp(_cfgFemalePlaybackVolume.Value, -1f, 1f)
                : Settings.FemalePlaybackVolume;
            Settings.ExternalPlaybackPitch = _cfgExternalPlaybackPitch != null
                ? Mathf.Clamp(_cfgExternalPlaybackPitch.Value, 0.1f, 3f)
                : Settings.ExternalPlaybackPitch;

            if (_cfgTopKeywords != null) Settings.TopKeywords = _cfgTopKeywords.Value;
            if (_cfgBottomKeywords != null) Settings.BottomKeywords = _cfgBottomKeywords.Value;
            if (_cfgBraKeywords != null) Settings.BraKeywords = _cfgBraKeywords.Value;
            if (_cfgShortsKeywords != null) Settings.ShortsKeywords = _cfgShortsKeywords.Value;
            if (_cfgGlovesKeywords != null) Settings.GlovesKeywords = _cfgGlovesKeywords.Value;
            if (_cfgPanthoseKeywords != null) Settings.PanthoseKeywords = _cfgPanthoseKeywords.Value;
            if (_cfgSocksKeywords != null) Settings.SocksKeywords = _cfgSocksKeywords.Value;
            if (_cfgShoesKeywords != null) Settings.ShoesKeywords = _cfgShoesKeywords.Value;
            if (_cfgRemoveKeywords != null) Settings.RemoveKeywords = _cfgRemoveKeywords.Value;
            if (_cfgShiftKeywords != null) Settings.ShiftKeywords = _cfgShiftKeywords.Value;
            if (_cfgPutOnKeywords != null) Settings.PutOnKeywords = _cfgPutOnKeywords.Value;
            if (_cfgRemoveAllKeywords != null) Settings.RemoveAllKeywords = _cfgRemoveAllKeywords.Value;
            if (_cfgPutOnAllKeywords != null) Settings.PutOnAllKeywords = _cfgPutOnAllKeywords.Value;
            if (_cfgCoordPattern != null) Settings.CoordPattern = _cfgCoordPattern.Value;
            if (_cfgEnableVideoPlaybackByResponseText != null) Settings.EnableVideoPlaybackByResponseText = _cfgEnableVideoPlaybackByResponseText.Value;

            Settings.Normalize();
        }

        private void SaveSettingsToConfigJson(string reason)
        {
            if (Settings == null || string.IsNullOrWhiteSpace(PluginDir))
            {
                return;
            }

            try
            {
                SettingsStore.SaveToDefault(PluginDir, Settings);
            }
            catch (Exception ex)
            {
                LogWarn("[settings] save failed reason=" + reason + " message=" + ex.Message);
            }
        }

        private static KeyboardShortcut ParseKeyboardShortcut(string value, KeyboardShortcut fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            string[] tokens = value.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length <= 0 || !TryParseKeyCode(tokens[0], out var main))
            {
                return fallback;
            }

            if (tokens.Length == 1)
            {
                return new KeyboardShortcut(main);
            }

            var modifiers = new List<KeyCode>();
            for (int i = 1; i < tokens.Length; i++)
            {
                if (TryParseKeyCode(tokens[i], out var modifier))
                {
                    modifiers.Add(modifier);
                }
            }

            return modifiers.Count > 0
                ? new KeyboardShortcut(main, modifiers.ToArray())
                : new KeyboardShortcut(main);
        }

        private static bool TryParseKeyCode(string token, out KeyCode keyCode)
        {
            return Enum.TryParse((token ?? string.Empty).Trim(), true, out keyCode);
        }

        private KeyboardShortcut ResolveReloadKey()
        {
            if (_cfgReloadKey != null)
            {
                return _cfgReloadKey.Value;
            }

            return new KeyboardShortcut(KeyCode.R, KeyCode.LeftControl);
        }

        private void ReloadSettings()
        {
            string oldPipe = Settings?.PipeName ?? string.Empty;
            Settings = SettingsStore.LoadOrCreate(PluginDir, Log, LogWarn, LogError);
            ApplySettingsToConfigEntries(Settings);
            SaveConfigFile(reason: "reload");
            LoadPoseScoreRules();
            LoadPoseCategoryEntries();
            LogGuardSettings("reload");

            bool pipeChanged = !string.Equals(
                oldPipe,
                Settings?.PipeName ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);

            StartOrRestartPipeServer(forceRestart: pipeChanged);
            Log("settings reloaded by Ctrl+R");
        }

        private void SaveConfigFile(string reason)
        {
            try
            {
                Config.Save();
            }
            catch (Exception ex)
            {
                LogWarn("[cfg] save failed reason=" + reason + " message=" + ex.Message);
            }
        }

        private void StartOrRestartPipeServer(bool forceRestart)
        {
            PluginSettings s = Settings;
            if (s == null || !s.Enabled || !s.EnablePipeServer)
            {
                StopPipeServer();
                return;
            }

            string pipeName = CommandParser.NormalizePipeName(s.PipeName);
            if (_pipeServer != null && !forceRestart && _pipeServer.IsForPipe(pipeName))
            {
                return;
            }

            StopPipeServer();

            _pipeServer = new ExternalPipeServer(
                pipeName,
                OnPipeLineReceived,
                Log,
                LogWarn,
                LogError);

            _pipeServer.Start();
            Log("[pipe] listening name=" + pipeName);
        }

        private void StopPipeServer()
        {
            if (_pipeServer == null)
            {
                return;
            }

            _pipeServer.Stop();
            _pipeServer = null;
        }

        private void OnPipeLineReceived(string line)
        {
            if (!CommandParser.TryParseIncoming(line, Settings, out var command, out var reason))
            {
                if (Settings != null && Settings.VerboseLog)
                {
                    LogWarn("[pipe] ignore command: " + reason);
                }
                return;
            }

            EnqueueCommand(command);
        }

        private void EnqueueCommand(ExternalVoiceFaceCommand command)
        {
            int capacity = Math.Max(1, Settings?.MaxQueuedCommands ?? 1);
            lock (_queueLock)
            {
                while (_commandQueue.Count >= capacity)
                {
                    _commandQueue.Dequeue();
                    if (Settings != null && Settings.VerboseLog)
                    {
                        LogWarn("[pipe] queue overflow, oldest command dropped");
                    }
                }

                _commandQueue.Enqueue(command);
            }
        }

        private void DrainIncomingCommands(int maxPerFrame)
        {
            for (int i = 0; i < maxPerFrame; i++)
            {
                ExternalVoiceFaceCommand command;
                lock (_queueLock)
                {
                    if (_commandQueue.Count <= 0)
                    {
                        return;
                    }

                    command = _commandQueue.Dequeue();
                }

                HandleCommand(command);
            }
        }

        private void HandleCommand(ExternalVoiceFaceCommand command)
        {
            var settings = Settings;
            if (command == null || settings == null)
            {
                return;
            }

            if (command.IsStop())
            {
                _externalVoicePlayer?.Stop("external stop");
                _blockGameVoiceUntil = 0f;
                RestoreVoiceProcStopIfNeeded();
                return;
            }

            if (string.Equals(command.type, "coord", StringComparison.OrdinalIgnoreCase))
            {
                HandleCoordCommand(command);
                return;
            }

            if (string.Equals(command.type, "clothes", StringComparison.OrdinalIgnoreCase))
            {
                HandleClothesCommand(command);
                return;
            }

            if (string.Equals(command.type, "response_text", StringComparison.OrdinalIgnoreCase))
            {
                HandleResponseTextCommand(command);
                return;
            }

            if (string.Equals(command.type, "pose", StringComparison.OrdinalIgnoreCase))
            {
                HandlePoseCommand(command);
                return;
            }

            HSceneProc proc = FindCurrentProc();
            if (proc == null)
            {
                if (settings.IgnoreCommandOutsideHScene)
                {
                    if (settings.VerboseLog)
                    {
                        LogWarn("[cmd] dropped because H scene is inactive");
                    }
                    return;
                }

                LogWarn("[cmd] HSceneProc not found");
                return;
            }

            int requestedMain = command.ResolveMain(settings.TargetMainIndex);
            int main = ClampMainIndex(proc, requestedMain);
            ChaControl female = ResolveFemale(proc, main);
            if (female == null)
            {
                LogWarn("[cmd] female not found main=" + main);
                return;
            }

            bool keepCurrentFaceMode;
            int face = ResolveFace(command, settings, out keepCurrentFaceMode);
            int voiceKind = command.ResolveVoiceKind(settings.DefaultVoiceKind);
            int action = command.ResolveAction(settings.DefaultAction);
            string audioPath = NormalizeAudioPath(command.ResolveAudioPath());
            if (!string.IsNullOrWhiteSpace(audioPath))
            {
                BeginExternalVoiceGuard(proc, female, main);

                if (!keepCurrentFaceMode)
                {
                    TryApplyFace(proc, main, female, face, voiceKind, action);
                }

                bool interruptCurrent = command.ResolveInterrupt(settings.DefaultInterruptCurrent);
                bool deleteAfterPlay = command.ResolveDeleteAfterPlay(settings.DeleteAudioAfterPlayback);
                float defaultVolume = ResolveExternalAudioDefaultVolume(settings);
                float volume = command.ResolveVolume(defaultVolume);
                float playbackPitch = command.ResolvePitch(settings.ExternalPlaybackPitch);

                bool playedAudio = _externalVoicePlayer != null && _externalVoicePlayer.Play(
                    audioPath,
                    female,
                    interruptCurrent,
                    deleteAfterPlay,
                    volume,
                    playbackPitch);

                if (playedAudio)
                {
                    Log(
                        "[cmd] played audio"
                        + " main=" + main
                        + " face=" + face
                        + " keepCurrentFace=" + keepCurrentFaceMode
                        + " voiceKind=" + voiceKind
                        + " action=" + action
                        + " volume=" + volume
                        + " pitch=" + playbackPitch
                        + " audioPath=" + audioPath);
                }
                else
                {
                    LogWarn("[cmd] audio play failed path=" + audioPath);
                    _blockGameVoiceUntil = 0f;
                    RestoreVoiceProcStopIfNeeded();
                }

                return;
            }

            if (proc.voice == null)
            {
                LogWarn("[cmd] HVoiceCtrl is not ready");
                return;
            }

            int voiceNo = ResolveVoiceNo(proc, main);
            if (voiceNo < 0)
            {
                LogWarn("[cmd] voiceNo not found main=" + main);
                return;
            }

            string assetBundle = command.ResolveAssetBundle(settings.DefaultAssetBundle);
            string assetName = command.ResolveAssetName(settings.DefaultAssetName);
            if (string.IsNullOrWhiteSpace(assetBundle) || string.IsNullOrWhiteSpace(assetName))
            {
                LogWarn("[cmd] assetBundle or assetName is empty");
                return;
            }

            int eyeNeck = command.ResolveEyeNeck(settings.DefaultEyeNeck);
            float pitch = command.ResolvePitch(settings.DefaultPitch);
            float fadeTime = command.ResolveFadeTime(settings.DefaultFadeTime);

            var voiceSetting = new Illusion.Game.Utils.Voice.Setting
            {
                assetBundleName = assetBundle,
                assetName = assetName,
                no = voiceNo,
                pitch = pitch,
                fadeTime = fadeTime,
                voiceTrans = female.transform,
                settingNo = -1,
                isAsync = true,
                isPlayEndDelete = true
            };

            bool result = proc.voice.PlayVoice(
                female,
                voiceSetting,
                face,
                eyeNeck,
                voiceKind,
                action,
                main);

            if (result)
            {
                Log(
                    "[cmd] played"
                    + " main=" + main
                    + " voiceNo=" + voiceNo
                    + " face=" + face
                    + " keepCurrentFace=" + keepCurrentFaceMode
                    + " eyeneck=" + eyeNeck
                    + " voiceKind=" + voiceKind
                    + " action=" + action
                    + " asset=" + assetName);
            }
            else
            {
                LogWarn("[cmd] PlayVoice returned false asset=" + assetName);
            }
        }

        internal bool ShouldBlockGameVoiceEvents()
        {
            var s = Settings;
            if (s == null || !s.Enabled || !s.BlockGameVoiceWhileExternalPlaying)
            {
                return false;
            }

            if (_externalVoicePlayer != null && _externalVoicePlayer.IsPlaying)
            {
                return true;
            }

            return Time.unscaledTime < _blockGameVoiceUntil;
        }

        internal void OnGameVoiceEventBlocked(string point)
        {
            var s = Settings;
            if (s == null || !s.VerboseLog || string.IsNullOrWhiteSpace(point))
            {
                return;
            }

            float now = Time.unscaledTime;
            if (_blockLogCooldownByKey.TryGetValue(point, out var nextAllowed) && now < nextAllowed)
            {
                return;
            }

            _blockLogCooldownByKey[point] = now + 1f;
            Log("[block] " + point);
        }

        private void BeginExternalVoiceGuard(HSceneProc proc, ChaControl female, int main)
        {
            var s = Settings;
            if (s != null)
            {
                float until = Time.unscaledTime + s.ExternalPlayPreBlockSeconds;
                if (until > _blockGameVoiceUntil)
                {
                    _blockGameVoiceUntil = until;
                }
            }

            if (s == null || !s.StopGameVoiceBeforeExternalPlay)
            {
                return;
            }

            TryStopGameVoice(proc, female, main);
        }

        private void TryStopGameVoice(HSceneProc proc, ChaControl female, int main)
        {
            try
            {
                int targets = 0;
                int stoppedTargets = 0;
                int residualBefore = 0;
                int residualAfter = 0;

                if (proc != null && proc.flags != null && proc.flags.transVoiceMouth != null)
                {
                    for (int i = 0; i < proc.flags.transVoiceMouth.Length; i++)
                    {
                        Transform voiceTrans = proc.flags.transVoiceMouth[i];
                        if (voiceTrans == null)
                        {
                            continue;
                        }

                        targets++;
                        bool wasPlaying = Manager.Voice.IsPlay(voiceTrans);
                        if (wasPlaying)
                        {
                            residualBefore++;
                        }

                        int voiceNo = -1;
                        if (proc.flags.lstHeroine != null && i >= 0 && i < proc.flags.lstHeroine.Count && proc.flags.lstHeroine[i] != null)
                        {
                            voiceNo = proc.flags.lstHeroine[i].voiceNo;
                        }

                        if (voiceNo >= 0)
                        {
                            Manager.Voice.Stop(voiceNo, voiceTrans);
                        }

                        Manager.Voice.Stop(voiceTrans);

                        if (Manager.Voice.IsPlay(voiceTrans))
                        {
                            if (voiceNo >= 0)
                            {
                                Manager.Voice.Stop(voiceNo, voiceTrans);
                            }
                            Manager.Voice.Stop(voiceTrans);
                        }

                        if (!Manager.Voice.IsPlay(voiceTrans))
                        {
                            stoppedTargets++;
                        }
                        else
                        {
                            residualAfter++;
                        }
                    }
                }

                if (female != null)
                {
                    Manager.Voice.Stop(female.transform);
                    ClearLipSyncSource(female);
                }

                if (proc != null && proc.voice != null)
                {
                    if (!_voiceProcStopOverridden)
                    {
                        _voiceProcStopOriginal = proc.voice.isVoicePrcoStop;
                        _voiceProcStopOverridden = true;
                    }

                    proc.voice.isVoicePrcoStop = true;
                }

                if (residualAfter > 0)
                {
                    Manager.Voice.StopAll(false);
                }

                bool anyPlayingAfter = Manager.Voice.IsPlay();
                Log(
                    "[guard] force-stop game voice before external play"
                    + " main=" + main
                    + " targets=" + targets
                    + " residualBefore=" + residualBefore
                    + " stoppedTargets=" + stoppedTargets
                    + " residualAfter=" + residualAfter
                    + " anyPlayingAfter=" + anyPlayingAfter);
            }
            catch (Exception ex)
            {
                LogWarn("[guard] stop game voice failed: " + ex.Message);
            }
        }

        private static void ClearLipSyncSource(ChaControl female)
        {
            if (female == null)
            {
                return;
            }

            try
            {
                female.SetLipSync(null);
            }
            catch
            {
            }
        }

        private static float ResolveExternalAudioDefaultVolume(PluginSettings settings)
        {
            if (settings == null)
            {
                return 1f;
            }

            if (settings.FemalePlaybackVolume >= 0f)
            {
                return Mathf.Clamp01(settings.FemalePlaybackVolume);
            }

            return Mathf.Clamp01(settings.PlaybackVolume);
        }

        private void LogGuardSettings(string reason)
        {
            var s = Settings;
            if (s == null)
            {
                return;
            }

            Log(
                "[guard] settings"
                + " reason=" + reason
                + " stopBefore=" + s.StopGameVoiceBeforeExternalPlay
                + " blockWhilePlay=" + s.BlockGameVoiceWhileExternalPlaying
                + " preBlockSec=" + s.ExternalPlayPreBlockSeconds
                + " vol=" + s.PlaybackVolume
                + " femaleVol=" + s.FemalePlaybackVolume
                + " extPitch=" + s.ExternalPlaybackPitch);
        }

        private void RestoreVoiceProcStopIfNeeded()
        {
            if (!_voiceProcStopOverridden)
            {
                return;
            }

            try
            {
                HSceneProc proc = CurrentProc ?? FindCurrentProc();
                if (proc != null && proc.voice != null)
                {
                    proc.voice.isVoicePrcoStop = _voiceProcStopOriginal;
                }
            }
            catch (Exception ex)
            {
                LogWarn("[guard] restore isVoicePrcoStop failed: " + ex.Message);
            }
            finally
            {
                _voiceProcStopOverridden = false;
                _voiceProcStopOriginal = false;
            }
        }

        // ----------------------------------------------------------------
        // 遅延アクション処理
        // ----------------------------------------------------------------

        private void ProcessDelayedActions()
        {
            float now = Time.unscaledTime;
            for (int i = _delayedActions.Count - 1; i >= 0; i--)
            {
                if (now >= _delayedActions[i].Item1)
                {
                    try { _delayedActions[i].Item2(); }
                    catch (Exception ex) { LogWarn("[delayed] 実行失敗: " + ex.Message); }
                    _delayedActions.RemoveAt(i);
                }
            }
        }

        // ----------------------------------------------------------------
        // response_text コマンド: 生テキストをパースして着替え/着衣を遅延実行
        // ----------------------------------------------------------------

        private void HandleResponseTextCommand(ExternalVoiceFaceCommand command)
        {
            string text = (command.text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text)) return;

            Log($"[response_text] received len={text.Length} preview={text.Substring(0, Math.Min(60, text.Length))}");

            float executeAt = Time.unscaledTime + Mathf.Max(0f, command.delaySeconds);
            int main = command.ResolveMain(Settings?.TargetMainIndex ?? 0);

            string coordName = ParseCoordFromText(text);
            if (!string.IsNullOrEmpty(coordName))
            {
                Log($"[response_text] coord matched: '{coordName}', scheduled delay={command.delaySeconds:F1}s");
                string cn = coordName;
                int m = main;
                _delayedActions.Add(Tuple.Create(executeAt, (Action)(() =>
                {
                    HandleCoordCommand(new ExternalVoiceFaceCommand { type = "coord", coordName = cn, main = m });
                })));
            }
            else
            {
                Log("[response_text] no coord keyword matched");
            }

            if (TryPickPoseFromText(text, out var poseName, out var poseMode, out var poseCategory))
            {
                Log($"[response_text] pose category matched: '{poseCategory}' -> '{poseName}' (mode={poseMode}), scheduled delay={command.delaySeconds:F1}s");
                string pn = poseName;
                int pm = poseMode;
                int m = main;
                _delayedActions.Add(Tuple.Create(executeAt, (Action)(() =>
                {
                    HandlePoseCommand(new ExternalVoiceFaceCommand { type = "pose", poseName = pn, poseMode = pm, main = m });
                })));
            }
            else
            {
                Log("[response_text] no pose keyword matched");
            }

            ClothesItem[] clothesItems = ParseClothesFromText(text);
            if (clothesItems != null && clothesItems.Length > 0)
            {
                Log($"[response_text] clothes matched {clothesItems.Length} items, scheduled delay={command.delaySeconds:F1}s");
                ClothesItem[] ci = clothesItems;
                int m = main;
                _delayedActions.Add(Tuple.Create(executeAt, (Action)(() =>
                {
                    HandleClothesCommand(new ExternalVoiceFaceCommand { type = "clothes", clothesItems = ci, main = m });
                })));
            }
            else
            {
                Log("[response_text] no clothes keywords matched");
            }

            if (TrySelectVideoFileNameFromText(text, out string videoFileName, out int httpPort, out string videoReason))
            {
                string selectedVideo = videoFileName;
                int selectedPort = httpPort;
                _delayedActions.Add(Tuple.Create(executeAt, (Action)(() =>
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        PostVideoPlayByFileName(selectedVideo, selectedPort);
                    });
                })));
                Log($"[response_text] video matched: filename='{selectedVideo}' port={selectedPort}, scheduled delay={command.delaySeconds:F1}s");
            }
            else
            {
                Log("[response_text] no video keyword matched (" + videoReason + ")");
            }
        }

        private static PoseKeywordScoreToken CreatePoseScoreToken(string keyword, int score)
        {
            return new PoseKeywordScoreToken
            {
                Keyword = keyword,
                Score = score
            };
        }

        private static PoseKeywordScoreRule CreatePoseScoreRule(
            string ruleId,
            string category,
            int priority,
            string[] poseNames,
            params PoseKeywordScoreToken[] tokens)
        {
            return new PoseKeywordScoreRule
            {
                RuleId = ruleId,
                Category = category,
                Priority = priority,
                PoseNames = poseNames ?? new string[0],
                Tokens = tokens ?? new PoseKeywordScoreToken[0]
            };
        }

        private static PoseCategoryInferRule CreatePoseInferRule(
            string ruleId,
            string targetCategory,
            int priority,
            string[] requiredAll,
            string[] requiredAny,
            string[] excludeAny)
        {
            return new PoseCategoryInferRule
            {
                RuleId = ruleId,
                TargetCategory = targetCategory,
                Priority = priority,
                RequiredAll = requiredAll ?? new string[0],
                RequiredAny = requiredAny ?? new string[0],
                ExcludeAny = excludeAny ?? new string[0]
            };
        }

        private void LoadPoseScoreRules()
        {
            string path = Path.Combine(PluginDir ?? string.Empty, PoseScoreRulesFileName);
            _poseScoreRulesFilePath = path;
            try
            {
                PoseScoreRulesFile file;
                bool wroteBack = false;
                if (!File.Exists(path))
                {
                    file = CreateDefaultPoseScoreRulesFile();
                    SavePoseScoreRulesFile(path, file);
                    wroteBack = true;
                    Log($"[pose-score] default file created: {path}");
                }
                else
                {
                    file = DeserializePoseScoreRulesFile(path);
                    if (TryMigratePoseScoreRulesFile(file))
                    {
                        SavePoseScoreRulesFile(path, file);
                        wroteBack = true;
                        Log($"[pose-score] migrated file updated: {path}");
                    }
                }

                ApplyPoseScoreRulesFile(file, source: path);
                EnsurePoseControlConfigEntries();
                SyncPoseControlConfigEntriesFromRuntime();
                if (wroteBack)
                {
                    Log($"[pose-score] active file saved: {path}");
                }
            }
            catch (Exception ex)
            {
                LogWarn("[pose-score] load failed, fallback to built-in defaults. message=" + ex.Message);
                ApplyPoseScoreRulesFile(CreateDefaultPoseScoreRulesFile(), source: "built-in-default");
                EnsurePoseControlConfigEntries();
                SyncPoseControlConfigEntriesFromRuntime();
            }
        }

        private bool TryMigratePoseScoreRulesFile(PoseScoreRulesFile file)
        {
            if (file == null)
            {
                return false;
            }

            bool changed = false;
            if (file.Version < PoseScoreRulesCurrentVersion)
            {
                file.Version = PoseScoreRulesCurrentVersion;
                changed = true;
            }

            if (!file.Enabled.HasValue)
            {
                file.Enabled = true;
                changed = true;
            }
            if (!file.RulesEnabled.HasValue)
            {
                file.RulesEnabled = true;
                changed = true;
            }
            if (!file.InferRulesEnabled.HasValue)
            {
                file.InferRulesEnabled = true;
                changed = true;
            }

            if (file.Rules != null)
            {
                foreach (PoseKeywordScoreRule rule in file.Rules)
                {
                    if (rule != null && !rule.Enabled.HasValue)
                    {
                        rule.Enabled = true;
                        changed = true;
                    }
                }
            }

            if (file.InferRules == null)
            {
                file.InferRules = new List<PoseCategoryInferRule>();
                changed = true;
            }

            int customInferIndex = 0;
            var inferRuleIdSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (PoseCategoryInferRule rule in file.InferRules)
            {
                if (rule == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(rule.RuleId))
                {
                    rule.RuleId = "infer_custom_" + customInferIndex++;
                    changed = true;
                }
                if (!rule.Enabled.HasValue)
                {
                    rule.Enabled = true;
                    changed = true;
                }
                inferRuleIdSet.Add(rule.RuleId);
            }

            foreach (PoseCategoryInferRule builtIn in PoseCategoryInferenceRules.Where(r => r != null))
            {
                if (string.IsNullOrWhiteSpace(builtIn.RuleId) || inferRuleIdSet.Contains(builtIn.RuleId))
                {
                    continue;
                }

                file.InferRules.Add(ClonePoseInferRule(builtIn));
                inferRuleIdSet.Add(builtIn.RuleId);
                changed = true;
            }

            if (file.CategoryEnabled == null)
            {
                file.CategoryEnabled = new Dictionary<string, bool>(StringComparer.Ordinal);
                changed = true;
            }

            var allCategories = new HashSet<string>(StringComparer.Ordinal);
            if (file.Rules != null)
            {
                foreach (PoseKeywordScoreRule rule in file.Rules)
                {
                    if (rule != null && !string.IsNullOrWhiteSpace(rule.Category))
                    {
                        allCategories.Add(rule.Category);
                    }
                }
            }
            if (file.InferRules != null)
            {
                foreach (PoseCategoryInferRule rule in file.InferRules)
                {
                    if (rule != null && !string.IsNullOrWhiteSpace(rule.TargetCategory))
                    {
                        allCategories.Add(rule.TargetCategory);
                    }
                }
            }

            foreach (string category in allCategories)
            {
                if (!file.CategoryEnabled.ContainsKey(category))
                {
                    file.CategoryEnabled[category] = true;
                    changed = true;
                }
            }

            return changed;
        }

        private void ApplyPoseScoreRulesFile(PoseScoreRulesFile file, string source)
        {
            if (file == null)
            {
                file = CreateDefaultPoseScoreRulesFile();
                source = "built-in-default(null)";
            }

            _poseChangeEnabled = file.Enabled != false;
            _poseRulesEnabled = file.RulesEnabled != false;
            _poseInferRulesEnabled = file.InferRulesEnabled != false;
            _poseScoreBase = file.ScoreBase > 0 ? file.ScoreBase : DefaultPoseScoreBase;
            _poseAdoptThreshold = file.AdoptThreshold > 0 ? file.AdoptThreshold : DefaultPoseAdoptThreshold;
            _poseForceThreshold = file.ForceThreshold > 0 ? file.ForceThreshold : DefaultPoseForceThreshold;
            if (_poseForceThreshold < _poseAdoptThreshold)
            {
                _poseForceThreshold = _poseAdoptThreshold;
            }

            var normalized = new List<PoseKeywordScoreRule>();
            if (file.Rules != null)
            {
                foreach (PoseKeywordScoreRule rule in file.Rules)
                {
                    if (rule == null || string.IsNullOrWhiteSpace(rule.Category))
                    {
                        continue;
                    }

                    string[] poseNames = (rule.PoseNames ?? new string[0])
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (poseNames.Length <= 0)
                    {
                        continue;
                    }

                    PoseKeywordScoreToken[] tokens = (rule.Tokens ?? new PoseKeywordScoreToken[0])
                        .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Keyword))
                        .Select(t => new PoseKeywordScoreToken
                        {
                            Keyword = t.Keyword,
                            Score = t.Score > 0 ? t.Score : 1
                        })
                        .ToArray();
                    if (tokens.Length <= 0)
                    {
                        continue;
                    }

                    normalized.Add(new PoseKeywordScoreRule
                    {
                        RuleId = string.IsNullOrWhiteSpace(rule.RuleId) ? ("rule_" + normalized.Count) : rule.RuleId,
                        Category = rule.Category,
                        Priority = rule.Priority,
                        PoseNames = poseNames,
                        Tokens = tokens,
                        Enabled = rule.Enabled != false
                    });
                }
            }

            var normalizedInferRules = new List<PoseCategoryInferRule>();
            if (file.InferRules != null)
            {
                foreach (PoseCategoryInferRule rule in file.InferRules)
                {
                    if (rule == null || string.IsNullOrWhiteSpace(rule.TargetCategory))
                    {
                        continue;
                    }

                    string[] requiredAll = (rule.RequiredAll ?? new string[0])
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    string[] requiredAny = (rule.RequiredAny ?? new string[0])
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    string[] excludeAny = (rule.ExcludeAny ?? new string[0])
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();

                    if (requiredAll.Length <= 0 && requiredAny.Length <= 0)
                    {
                        continue;
                    }

                    normalizedInferRules.Add(new PoseCategoryInferRule
                    {
                        RuleId = string.IsNullOrWhiteSpace(rule.RuleId) ? ("infer_" + normalizedInferRules.Count) : rule.RuleId,
                        TargetCategory = rule.TargetCategory,
                        Priority = rule.Priority,
                        RequiredAll = requiredAll,
                        RequiredAny = requiredAny,
                        ExcludeAny = excludeAny,
                        Enabled = rule.Enabled != false
                    });
                }
            }

            _poseKeywordScoreRules = normalized;
            _poseCategoryInferRules = normalizedInferRules;
            RebuildPoseCategoryEnabledMap(file.CategoryEnabled);
            Log($"[pose-score] loaded rules={_poseKeywordScoreRules.Count} inferRules={_poseCategoryInferRules.Count} rulesEnabled={_poseRulesEnabled} inferEnabled={_poseInferRulesEnabled} scoreBase={_poseScoreBase} adopt={_poseAdoptThreshold} force={_poseForceThreshold} source={source}");
        }

        private void RebuildPoseCategoryEnabledMap(Dictionary<string, bool> fromFile)
        {
            _poseCategoryEnabled.Clear();

            var categorySet = new HashSet<string>(StringComparer.Ordinal);
            foreach (PoseKeywordScoreRule rule in _poseKeywordScoreRules)
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.Category))
                {
                    continue;
                }

                categorySet.Add(rule.Category);
            }

            foreach (PoseCategoryInferRule rule in _poseCategoryInferRules)
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.TargetCategory))
                {
                    continue;
                }

                categorySet.Add(rule.TargetCategory);
            }

            foreach (string category in categorySet)
            {
                bool enabled = true;
                if (fromFile != null && fromFile.TryGetValue(category, out bool v))
                {
                    enabled = v;
                }

                _poseCategoryEnabled[category] = enabled;
            }
        }

        private PoseScoreRulesFile CreateDefaultPoseScoreRulesFile()
        {
            var rules = PoseKeywordScoreRules
                .Where(r => r != null)
                .Select(ClonePoseScoreRule)
                .ToList();
            var inferRules = PoseCategoryInferenceRules
                .Where(r => r != null)
                .Select(ClonePoseInferRule)
                .ToList();
            var categoryEnabled = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var c in rules.Select(r => r.Category).Concat(inferRules.Select(r => r.TargetCategory)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
            {
                categoryEnabled[c] = true;
            }

            return new PoseScoreRulesFile
            {
                Version = PoseScoreRulesCurrentVersion,
                Enabled = true,
                RulesEnabled = true,
                InferRulesEnabled = true,
                ScoreBase = DefaultPoseScoreBase,
                AdoptThreshold = DefaultPoseAdoptThreshold,
                ForceThreshold = DefaultPoseForceThreshold,
                Rules = rules,
                InferRules = inferRules,
                CategoryEnabled = categoryEnabled
            };
        }

        private static PoseKeywordScoreRule ClonePoseScoreRule(PoseKeywordScoreRule rule)
        {
            if (rule == null)
            {
                return null;
            }

            return new PoseKeywordScoreRule
            {
                RuleId = rule.RuleId,
                Category = rule.Category,
                Priority = rule.Priority,
                Enabled = rule.Enabled != false,
                PoseNames = (rule.PoseNames ?? new string[0]).ToArray(),
                Tokens = (rule.Tokens ?? new PoseKeywordScoreToken[0])
                    .Where(t => t != null)
                    .Select(t => new PoseKeywordScoreToken
                    {
                        Keyword = t.Keyword,
                        Score = t.Score
                    })
                    .ToArray()
            };
        }

        private static PoseCategoryInferRule ClonePoseInferRule(PoseCategoryInferRule rule)
        {
            if (rule == null)
            {
                return null;
            }

            return new PoseCategoryInferRule
            {
                RuleId = rule.RuleId,
                TargetCategory = rule.TargetCategory,
                Priority = rule.Priority,
                Enabled = rule.Enabled != false,
                RequiredAll = (rule.RequiredAll ?? new string[0]).ToArray(),
                RequiredAny = (rule.RequiredAny ?? new string[0]).ToArray(),
                ExcludeAny = (rule.ExcludeAny ?? new string[0]).ToArray()
            };
        }

        private static PoseScoreRulesFile DeserializePoseScoreRulesFile(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException("pose_score_rules.json is empty");
            }

            var serializer = new DataContractJsonSerializer(
                typeof(PoseScoreRulesFile),
                new DataContractJsonSerializerSettings
                {
                    UseSimpleDictionaryFormat = true
                });
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using (var ms = new MemoryStream(bytes))
            {
                PoseScoreRulesFile file = serializer.ReadObject(ms) as PoseScoreRulesFile;
                if (file == null)
                {
                    throw new InvalidDataException("pose_score_rules.json parse returned null");
                }

                return file;
            }
        }

        private static void SavePoseScoreRulesFile(string path, PoseScoreRulesFile file)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var serializer = new DataContractJsonSerializer(
                typeof(PoseScoreRulesFile),
                new DataContractJsonSerializerSettings
                {
                    UseSimpleDictionaryFormat = true
                });
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, file);
                string json = Encoding.UTF8.GetString(ms.ToArray());
                File.WriteAllText(path, json, Utf8NoBom);
            }
        }

        private void SaveCurrentPoseScoreRulesToFile(string reason)
        {
            string path = _poseScoreRulesFilePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(PluginDir ?? string.Empty, PoseScoreRulesFileName);
            }

            try
            {
                var file = new PoseScoreRulesFile
                {
                    Version = PoseScoreRulesCurrentVersion,
                    Enabled = _poseChangeEnabled,
                    RulesEnabled = _poseRulesEnabled,
                    InferRulesEnabled = _poseInferRulesEnabled,
                    ScoreBase = _poseScoreBase,
                    AdoptThreshold = _poseAdoptThreshold,
                    ForceThreshold = _poseForceThreshold,
                    CategoryEnabled = new Dictionary<string, bool>(_poseCategoryEnabled, StringComparer.Ordinal),
                    Rules = (_poseKeywordScoreRules ?? new List<PoseKeywordScoreRule>())
                        .Where(r => r != null)
                        .Select(ClonePoseScoreRule)
                        .ToList(),
                    InferRules = (_poseCategoryInferRules ?? new List<PoseCategoryInferRule>())
                        .Where(r => r != null)
                        .Select(ClonePoseInferRule)
                        .ToList()
                };

                SavePoseScoreRulesFile(path, file);
                Log("[pose-score] saved by " + reason);
            }
            catch (Exception ex)
            {
                LogWarn("[pose-score] save failed reason=" + reason + " message=" + ex.Message);
            }
        }

        private void EnsurePoseClassificationFilesFromProc(HSceneProc proc)
        {
            if (proc == null || string.IsNullOrWhiteSpace(PluginDir))
            {
                return;
            }

            bool created = false;
            created |= EnsureSinglePoseClassificationFile(proc, PoseSonyuClassifiedFileName, isSonyu: true);
            created |= EnsureSinglePoseClassificationFile(proc, PoseHoushiClassifiedFileName, isSonyu: false);

            if (created)
            {
                LoadPoseCategoryEntries();
            }
        }

        private bool EnsureSinglePoseClassificationFile(HSceneProc proc, string fileName, bool isSonyu)
        {
            if (proc == null || string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(PluginDir))
            {
                return false;
            }

            string path = Path.Combine(PluginDir, fileName);
            if (File.Exists(path))
            {
                return false;
            }

            try
            {
                var lists = LstUseAnimInfoField?.GetValue(proc) as List<HSceneProc.AnimationListInfo>[];
                if (lists == null)
                {
                    LogWarn("[pose-classify] auto-create skipped (lstUseAnimInfo is null): " + fileName);
                    return false;
                }

                Dictionary<string, List<PoseClassificationItem>> categories = isSonyu
                    ? BuildAutoSonyuPoseCategories(lists)
                    : BuildAutoHoushiPoseCategories(lists);

                SavePoseClassification(path, categories);
                int entryCount = categories.Sum(x => x.Value != null ? x.Value.Count : 0);
                Log($"[pose-classify] auto-created: {path} categories={categories.Count} entries={entryCount}");
                return true;
            }
            catch (Exception ex)
            {
                LogWarn("[pose-classify] auto-create failed file=" + fileName + " message=" + ex.Message);
                return false;
            }
        }

        private static Dictionary<string, List<PoseClassificationItem>> BuildAutoSonyuPoseCategories(List<HSceneProc.AnimationListInfo>[] lists)
        {
            var categories = CreateCategoryMap(SonyuCategoryNames);
            if (lists == null)
            {
                return categories;
            }

            for (int mode = 0; mode < lists.Length; mode++)
            {
                if (!IsSonyuMode(mode))
                {
                    continue;
                }

                var list = lists[mode];
                if (list == null)
                {
                    continue;
                }

                for (int i = 0; i < list.Count; i++)
                {
                    var info = list[i];
                    if (info == null || string.IsNullOrWhiteSpace(info.nameAnimation))
                    {
                        continue;
                    }

                    string category = ClassifySonyuPoseCategory(info.nameAnimation);
                    AddPoseClassificationItem(categories, category, info.nameAnimation, mode);
                }
            }

            return categories;
        }

        private static Dictionary<string, List<PoseClassificationItem>> BuildAutoHoushiPoseCategories(List<HSceneProc.AnimationListInfo>[] lists)
        {
            var categories = CreateCategoryMap(HoushiCategoryNames);
            if (lists == null)
            {
                return categories;
            }

            for (int mode = 0; mode < lists.Length; mode++)
            {
                if (!IsHoushiMode(mode))
                {
                    continue;
                }

                var list = lists[mode];
                if (list == null)
                {
                    continue;
                }

                for (int i = 0; i < list.Count; i++)
                {
                    var info = list[i];
                    if (info == null || string.IsNullOrWhiteSpace(info.nameAnimation))
                    {
                        continue;
                    }

                    string category = ClassifyHoushiPoseCategory(info.nameAnimation);
                    AddPoseClassificationItem(categories, category, info.nameAnimation, mode);
                }
            }

            return categories;
        }

        private static Dictionary<string, List<PoseClassificationItem>> CreateCategoryMap(string[] categoryNames)
        {
            var map = new Dictionary<string, List<PoseClassificationItem>>(StringComparer.Ordinal);
            if (categoryNames == null)
            {
                return map;
            }

            for (int i = 0; i < categoryNames.Length; i++)
            {
                string category = categoryNames[i];
                if (string.IsNullOrWhiteSpace(category))
                {
                    continue;
                }

                if (!map.ContainsKey(category))
                {
                    map[category] = new List<PoseClassificationItem>();
                }
            }

            return map;
        }

        private static bool IsSonyuMode(int mode)
        {
            return mode == 2 || mode == 7 || mode == 9;
        }

        private static bool IsHoushiMode(int mode)
        {
            return mode == 1 || mode == 6 || mode == 8;
        }

        private static string ClassifySonyuPoseCategory(string poseName)
        {
            string name = poseName ?? string.Empty;

            if (ContainsAnyCategoryKeyword(name, "逆騎乗", "背面騎乗", "後ろ向き", "Reverse Cowgirl"))
            {
                return "背面騎乗位系";
            }
            if (ContainsAnyCategoryKeyword(name, "騎乗", "Cowgirl", "またが", "跨"))
            {
                return "騎乗位系";
            }
            if (ContainsAnyCategoryKeyword(name, "側位", "横", "Side", "Princess Hug"))
            {
                return "測位系";
            }
            if (ContainsAnyCategoryKeyword(name, "座位", "椅子", "床", "正座", "膝立て", "Sitting"))
            {
                return "座位系";
            }

            bool standing = ContainsAnyCategoryKeyword(name, "立ち", "立位", "Standing", "駅弁", "Wall");
            bool back = ContainsAnyCategoryKeyword(name, "バック", "後背", "後ろ", "doggy", "Doggystyle", "from behind", "フェンス");
            if (standing && back)
            {
                return "立後背位系";
            }
            if (back)
            {
                return "後背位系";
            }
            if (standing)
            {
                return "立位系";
            }

            return "正常位系";
        }

        private static string ClassifyHoushiPoseCategory(string poseName)
        {
            string name = poseName ?? string.Empty;

            if (ContainsAnyCategoryKeyword(name, "69", "シックスナイン", "sixty"))
            {
                return "69系";
            }
            if (ContainsAnyCategoryKeyword(name, "フェラ", "口", "oral", "blow", "咥", "しゃぶ"))
            {
                return "フェラ系";
            }
            if (ContainsAnyCategoryKeyword(name, "パイズリ", "boob", "titty", "乳"))
            {
                return "パイズリ系";
            }
            if (ContainsAnyCategoryKeyword(name, "クンニ", "cunni", "舐"))
            {
                return "クンニ系";
            }
            if (ContainsAnyCategoryKeyword(name, "顔面騎乗", "face sit"))
            {
                return "顔面騎乗系";
            }
            if (ContainsAnyCategoryKeyword(name, "足コキ", "leg", "foot"))
            {
                return "足コキ系";
            }
            if (ContainsAnyCategoryKeyword(name, "手コキ", "hand", "手"))
            {
                return "手コキ系";
            }

            return "キス・愛撫系";
        }

        private static bool ContainsAnyCategoryKeyword(string text, params string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(text) || keywords == null || keywords.Length <= 0)
            {
                return false;
            }

            for (int i = 0; i < keywords.Length; i++)
            {
                string keyword = keywords[i];
                if (ContainsKeyword(text, keyword))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddPoseClassificationItem(
            Dictionary<string, List<PoseClassificationItem>> categories,
            string category,
            string nameAnimation,
            int modeInt)
        {
            if (categories == null || string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(nameAnimation))
            {
                return;
            }

            if (!categories.TryGetValue(category, out var list))
            {
                list = new List<PoseClassificationItem>();
                categories[category] = list;
            }

            bool exists = list.Any(x =>
                x != null &&
                x.ModeInt == modeInt &&
                string.Equals(x.NameAnimation, nameAnimation, StringComparison.Ordinal));
            if (exists)
            {
                return;
            }

            list.Add(new PoseClassificationItem
            {
                NameAnimation = nameAnimation,
                ModeInt = modeInt
            });
        }

        private static void SavePoseClassification(string path, Dictionary<string, List<PoseClassificationItem>> categories)
        {
            var root = new PoseClassificationFile
            {
                Categories = categories ?? new Dictionary<string, List<PoseClassificationItem>>(StringComparer.Ordinal)
            };

            var serializer = new DataContractJsonSerializer(
                typeof(PoseClassificationFile),
                new DataContractJsonSerializerSettings
                {
                    UseSimpleDictionaryFormat = true
                });

            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, root);
                string json = Encoding.UTF8.GetString(ms.ToArray());
                File.WriteAllText(path, json, Utf8NoBom);
            }
        }

        private void LoadPoseCategoryEntries()
        {
            _poseEntriesByCategory.Clear();
            int added = 0;
            added += AppendPoseCategoriesFromFile(PoseSonyuClassifiedFileName);
            added += AppendPoseCategoriesFromFile(PoseHoushiClassifiedFileName);
            int standingBackAdded = BuildStandingBackPoseCategory();
            Log($"[pose-classify] loaded categories={_poseEntriesByCategory.Count} entries={added} standingBackEntriesAdded={standingBackAdded}");
        }

        private int AppendPoseCategoriesFromFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(PluginDir) || string.IsNullOrWhiteSpace(fileName))
            {
                return 0;
            }

            string path = Path.Combine(PluginDir, fileName);
            if (!File.Exists(path))
            {
                LogWarn("[pose-classify] file not found: " + path);
                return 0;
            }

            try
            {
                PoseClassificationFile root = DeserializePoseClassification(path);
                if (root == null)
                {
                    LogWarn("[pose-classify] root parse null: " + fileName);
                    return 0;
                }

                if (root.Categories == null)
                {
                    LogWarn("[pose-classify] categories parse null: " + fileName);
                    return 0;
                }

                if (root.Categories.Count <= 0)
                {
                    LogWarn("[pose-classify] categories empty: " + fileName);
                    return 0;
                }

                int added = 0;
                foreach (var pair in root.Categories)
                {
                    string category = pair.Key;
                    if (string.IsNullOrWhiteSpace(category) || pair.Value == null)
                    {
                        continue;
                    }

                    if (!_poseEntriesByCategory.TryGetValue(category, out var list))
                    {
                        list = new List<PoseCategoryEntry>();
                        _poseEntriesByCategory[category] = list;
                    }

                    foreach (var item in pair.Value)
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.NameAnimation))
                        {
                            continue;
                        }

                        bool exists = list.Any(x =>
                            x != null &&
                            x.ModeInt == item.ModeInt &&
                            string.Equals(x.NameAnimation, item.NameAnimation, StringComparison.Ordinal));
                        if (exists)
                        {
                            continue;
                        }

                        list.Add(new PoseCategoryEntry
                        {
                            NameAnimation = item.NameAnimation,
                            ModeInt = item.ModeInt
                        });
                        added++;
                    }
                }

                return added;
            }
            catch (Exception ex)
            {
                LogWarn("[pose-classify] load failed file=" + fileName + " message=" + ex.Message);
                return 0;
            }
        }

        private static PoseClassificationFile DeserializePoseClassification(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var serializer = new DataContractJsonSerializer(
                typeof(PoseClassificationFile),
                new DataContractJsonSerializerSettings
                {
                    UseSimpleDictionaryFormat = true
                });
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using (var ms = new MemoryStream(bytes))
            {
                return serializer.ReadObject(ms) as PoseClassificationFile;
            }
        }

        private int BuildStandingBackPoseCategory()
        {
            if (!_poseEntriesByCategory.TryGetValue(StandingCategoryName, out var standingEntries) ||
                !_poseEntriesByCategory.TryGetValue(BackCategoryName, out var backEntries) ||
                standingEntries == null || backEntries == null ||
                standingEntries.Count <= 0 || backEntries.Count <= 0)
            {
                return 0;
            }

            if (!_poseEntriesByCategory.TryGetValue(StandingBackCategoryName, out var standingBackEntries))
            {
                standingBackEntries = new List<PoseCategoryEntry>();
                _poseEntriesByCategory[StandingBackCategoryName] = standingBackEntries;
            }

            var backKeySet = new HashSet<string>(
                backEntries
                    .Where(e => e != null && !string.IsNullOrWhiteSpace(e.NameAnimation))
                    .Select(BuildPoseEntryKey),
                StringComparer.Ordinal);

            int added = 0;
            foreach (var entry in standingEntries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.NameAnimation))
                {
                    continue;
                }

                string key = BuildPoseEntryKey(entry);
                if (!backKeySet.Contains(key))
                {
                    continue;
                }

                bool exists = standingBackEntries.Any(x =>
                    x != null &&
                    x.ModeInt == entry.ModeInt &&
                    string.Equals(x.NameAnimation, entry.NameAnimation, StringComparison.Ordinal));
                if (exists)
                {
                    continue;
                }

                standingBackEntries.Add(new PoseCategoryEntry
                {
                    NameAnimation = entry.NameAnimation,
                    ModeInt = entry.ModeInt
                });
                added++;
            }

            return added;
        }

        private static string BuildPoseEntryKey(PoseCategoryEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.NameAnimation))
            {
                return string.Empty;
            }

            return $"{entry.ModeInt}|{entry.NameAnimation}";
        }

        private bool TryPickPoseFromText(string text, out string poseName, out int poseMode, out string poseCategory)
        {
            poseName = null;
            poseMode = -1;
            poseCategory = null;

            if (string.IsNullOrWhiteSpace(text) || _poseEntriesByCategory.Count <= 0)
            {
                return false;
            }

            if (!_poseChangeEnabled)
            {
                return false;
            }

            int bestAliasLen = -1;
            string bestCategory = null;

            if (TryResolveStandingBackCategory(text, out string standingBackCategory))
            {
                bestAliasLen = int.MaxValue;
                bestCategory = standingBackCategory;
                Log($"[pose] category override matched: {bestCategory} (reason=standing+back)");
            }

            foreach (var pair in _poseEntriesByCategory)
            {
                if (!string.IsNullOrWhiteSpace(bestCategory) &&
                    string.Equals(bestCategory, StandingBackCategoryName, StringComparison.Ordinal))
                {
                    break;
                }

                string[] aliases = ResolvePoseCategoryAliases(pair.Key);
                foreach (string alias in aliases)
                {
                    if (string.IsNullOrWhiteSpace(alias))
                    {
                        continue;
                    }

                    if (text.IndexOf(alias, StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    if (alias.Length > bestAliasLen)
                    {
                        bestAliasLen = alias.Length;
                        bestCategory = pair.Key;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(bestCategory) &&
                TryResolveInferredPoseCategory(text, out string inferredCategory, out string inferredRuleId))
            {
                bestCategory = inferredCategory;
                bestAliasLen = 0;
                Log($"[pose] category inferred: {bestCategory} (rule={inferredRuleId})");
            }

            if (string.IsNullOrWhiteSpace(bestCategory) &&
                TryPickPoseFromGlobalTokenScore(text, out var globalCategory, out var globalPose, out var globalMatch))
            {
                poseCategory = globalCategory;
                poseName = globalPose?.NameAnimation;
                poseMode = globalPose != null ? globalPose.ModeInt : -1;
                if (!string.IsNullOrWhiteSpace(poseName))
                {
                    string level = globalMatch != null && globalMatch.Score >= _poseForceThreshold ? "force" : "prefer";
                    Log($"[pose] scored-{level} category+pose inferred category={poseCategory} rule={globalMatch?.Rule?.RuleId} score={globalMatch?.Score} pose={poseName}");
                    return true;
                }
            }

            if (string.IsNullOrWhiteSpace(bestCategory))
            {
                return false;
            }

            if (!_poseEntriesByCategory.TryGetValue(bestCategory, out var entries) || entries == null || entries.Count <= 0)
            {
                return false;
            }

            PoseCategoryEntry selected = TryPickPreferredPoseEntry(bestCategory, text, entries);
            if (selected == null)
            {
                int index;
                lock (_random)
                {
                    index = _random.Next(entries.Count);
                }

                selected = entries[index];
            }

            poseName = selected?.NameAnimation;
            poseMode = selected != null ? selected.ModeInt : -1;
            poseCategory = bestCategory;

            return !string.IsNullOrWhiteSpace(poseName);
        }

        private bool TryResolveStandingBackCategory(string text, out string category)
        {
            category = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (!_poseEntriesByCategory.TryGetValue(StandingBackCategoryName, out var entries) || entries == null || entries.Count <= 0)
            {
                return false;
            }

            bool hasStandingCue = ContainsAny(text, StandingBackStandingKeywords);
            bool hasBackCue = ContainsAny(text, StandingBackBackKeywords);
            if (!hasStandingCue || !hasBackCue)
            {
                return false;
            }

            category = StandingBackCategoryName;
            return true;
        }

        private bool TryResolveInferredPoseCategory(string text, out string category, out string matchedRuleId)
        {
            category = null;
            matchedRuleId = null;
            if (string.IsNullOrWhiteSpace(text) || _poseCategoryInferRules == null || _poseCategoryInferRules.Count <= 0)
            {
                return false;
            }
            if (!_poseInferRulesEnabled)
            {
                return false;
            }

            PoseCategoryInferRule bestRule = null;
            int bestPriority = int.MinValue;
            int bestSpecificity = int.MinValue;

            foreach (PoseCategoryInferRule rule in _poseCategoryInferRules)
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.TargetCategory))
                {
                    continue;
                }

                if (rule.Enabled == false)
                {
                    continue;
                }

                if (!IsPoseCategoryEnabled(rule.TargetCategory))
                {
                    continue;
                }

                if (!_poseEntriesByCategory.ContainsKey(rule.TargetCategory))
                {
                    continue;
                }

                if (!IsInferRuleMatch(text, rule))
                {
                    continue;
                }

                int specificity =
                    (rule.RequiredAll?.Length ?? 0) * 100 +
                    (rule.RequiredAny?.Length ?? 0) * 10 +
                    (rule.ExcludeAny?.Length ?? 0);

                bool better = rule.Priority > bestPriority ||
                              (rule.Priority == bestPriority && specificity > bestSpecificity);
                if (!better)
                {
                    continue;
                }

                bestRule = rule;
                bestPriority = rule.Priority;
                bestSpecificity = specificity;
            }

            if (bestRule == null)
            {
                return false;
            }

            category = bestRule.TargetCategory;
            matchedRuleId = bestRule.RuleId;
            return true;
        }

        private bool IsInferRuleMatch(string text, PoseCategoryInferRule rule)
        {
            if (rule == null || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string[] excludeAny = rule.ExcludeAny ?? new string[0];
            foreach (string kw in excludeAny)
            {
                if (ContainsKeyword(text, kw))
                {
                    return false;
                }
            }

            string[] requiredAll = rule.RequiredAll ?? new string[0];
            foreach (string kw in requiredAll)
            {
                if (!ContainsKeyword(text, kw))
                {
                    return false;
                }
            }

            string[] requiredAny = rule.RequiredAny ?? new string[0];
            if (requiredAny.Length <= 0)
            {
                return true;
            }

            foreach (string kw in requiredAny)
            {
                if (ContainsKeyword(text, kw))
                {
                    return true;
                }
            }

            return false;
        }

        private PoseCategoryEntry TryPickPreferredPoseEntry(
            string category,
            string text,
            List<PoseCategoryEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(text) || entries == null || entries.Count <= 0)
            {
                return null;
            }
            if (!_poseRulesEnabled)
            {
                return null;
            }

            PoseCategoryEntry scoredPreferred = TryPickPoseEntryByTokenScore(category, text, entries);
            if (scoredPreferred != null)
            {
                return scoredPreferred;
            }

            if (string.Equals(category, "正常位系", StringComparison.Ordinal) &&
                ContainsAny(text, NormalMissionaryInterlockKeywords))
            {
                PoseCategoryEntry normalPreferred = PickRandomPoseEntryByNames(entries, NormalMissionaryInterlockPoseNames);
                if (normalPreferred != null)
                {
                    Log($"[pose] preferred matched category={category} reason=密着/しがみ pose={normalPreferred.NameAnimation}");
                    return normalPreferred;
                }
            }

            if (string.Equals(category, "立位系", StringComparison.Ordinal) &&
                text.IndexOf("壁", StringComparison.Ordinal) >= 0)
            {
                PoseCategoryEntry standingPreferred = PickRandomPoseEntryByNames(entries, StandingWallPreferredPoseNames);
                if (standingPreferred != null)
                {
                    Log($"[pose] preferred matched category={category} reason=壁 pose={standingPreferred.NameAnimation}");
                    return standingPreferred;
                }
            }

            return null;
        }

        private PoseCategoryEntry TryPickPoseEntryByTokenScore(string category, string text, List<PoseCategoryEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(text) || entries == null || entries.Count <= 0)
            {
                return null;
            }

            if (!TryFindBestScoreMatchForCategory(category, text, entries, out PoseScoreMatch bestMatch))
            {
                return null;
            }

            PoseCategoryEntry selected;
            lock (_random)
            {
                selected = bestMatch.Candidates[_random.Next(bestMatch.Candidates.Count)];
            }

            string level = bestMatch.Score >= _poseForceThreshold ? "force" : "prefer";
            Log($"[pose] scored-{level} matched category={category} rule={bestMatch.Rule.RuleId} score={bestMatch.Score} longest={bestMatch.LongestMatch} pose={selected?.NameAnimation}");
            return selected;
        }

        private bool TryPickPoseFromGlobalTokenScore(string text, out string category, out PoseCategoryEntry selected, out PoseScoreMatch match)
        {
            category = null;
            selected = null;
            match = null;

            if (string.IsNullOrWhiteSpace(text) || _poseKeywordScoreRules == null || _poseKeywordScoreRules.Count <= 0)
            {
                return false;
            }

            var candidateCategories = _poseKeywordScoreRules
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Category))
                .Select(r => r.Category)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (candidateCategories.Length <= 0)
            {
                return false;
            }

            PoseScoreMatch bestMatch = null;
            foreach (string candidateCategory in candidateCategories)
            {
                if (!_poseEntriesByCategory.TryGetValue(candidateCategory, out var entries) || entries == null || entries.Count <= 0)
                {
                    continue;
                }

                if (!TryFindBestScoreMatchForCategory(candidateCategory, text, entries, out PoseScoreMatch current))
                {
                    continue;
                }

                if (IsBetterScoreMatch(current, bestMatch))
                {
                    bestMatch = current;
                }
            }

            if (bestMatch == null || bestMatch.Candidates == null || bestMatch.Candidates.Count <= 0)
            {
                return false;
            }

            lock (_random)
            {
                selected = bestMatch.Candidates[_random.Next(bestMatch.Candidates.Count)];
            }

            category = bestMatch.Category;
            match = bestMatch;
            return selected != null;
        }

        private bool TryFindBestScoreMatchForCategory(string category, string text, List<PoseCategoryEntry> entries, out PoseScoreMatch bestMatch)
        {
            bestMatch = null;
            if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(text) || entries == null || entries.Count <= 0)
            {
                return false;
            }
            if (!_poseRulesEnabled)
            {
                return false;
            }

            if (!IsPoseCategoryEnabled(category))
            {
                return false;
            }

            if (_poseKeywordScoreRules == null || _poseKeywordScoreRules.Count <= 0)
            {
                return false;
            }

            foreach (var rule in _poseKeywordScoreRules)
            {
                if (rule == null || rule.Tokens == null || rule.Tokens.Length <= 0 || rule.PoseNames == null || rule.PoseNames.Length <= 0)
                {
                    continue;
                }

                if (rule.Enabled == false)
                {
                    continue;
                }

                if (!IsScoreRuleCategoryMatch(category, rule.Category))
                {
                    continue;
                }

                var candidates = FindPoseEntriesByNames(entries, rule.PoseNames);
                if (candidates.Count <= 0)
                {
                    continue;
                }

                int score = _poseScoreBase;
                int matchedTokenCount = 0;
                int longestMatch = 0;

                foreach (var token in rule.Tokens)
                {
                    if (token == null || string.IsNullOrWhiteSpace(token.Keyword))
                    {
                        continue;
                    }

                    if (!ContainsKeyword(text, token.Keyword))
                    {
                        continue;
                    }

                    matchedTokenCount++;
                    score += token.Score;
                    if (token.Keyword.Length > longestMatch)
                    {
                        longestMatch = token.Keyword.Length;
                    }
                }

                if (matchedTokenCount <= 0 || score < _poseAdoptThreshold)
                {
                    continue;
                }

                var currentMatch = new PoseScoreMatch
                {
                    Category = category,
                    Rule = rule,
                    Candidates = candidates,
                    Score = score,
                    LongestMatch = longestMatch,
                    Priority = rule.Priority,
                    MatchedTokenCount = matchedTokenCount
                };

                if (IsBetterScoreMatch(currentMatch, bestMatch))
                {
                    bestMatch = currentMatch;
                }
            }

            return bestMatch != null;
        }

        private static bool IsBetterScoreMatch(PoseScoreMatch current, PoseScoreMatch best)
        {
            if (current == null)
            {
                return false;
            }

            if (best == null)
            {
                return true;
            }

            return current.Score > best.Score ||
                   (current.Score == best.Score && current.LongestMatch > best.LongestMatch) ||
                   (current.Score == best.Score && current.LongestMatch == best.LongestMatch && current.Priority > best.Priority) ||
                   (current.Score == best.Score && current.LongestMatch == best.LongestMatch && current.Priority == best.Priority && current.MatchedTokenCount > best.MatchedTokenCount);
        }

        private static bool IsScoreRuleCategoryMatch(string selectedCategory, string ruleCategory)
        {
            if (string.IsNullOrWhiteSpace(selectedCategory) || string.IsNullOrWhiteSpace(ruleCategory))
            {
                return false;
            }

            if (string.Equals(selectedCategory, ruleCategory, StringComparison.Ordinal))
            {
                return true;
            }

            // 立後背位系が選ばれたときは、後背位系ルールも併用して優先体位を拾う
            if (string.Equals(selectedCategory, StandingBackCategoryName, StringComparison.Ordinal) &&
                string.Equals(ruleCategory, BackCategoryName, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private bool IsPoseCategoryEnabled(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                return false;
            }

            if (_poseCategoryEnabled.TryGetValue(category, out bool enabled))
            {
                return enabled;
            }

            return true;
        }

        private static bool ContainsKeyword(string text, string keyword)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(keyword))
            {
                return false;
            }

            bool asciiOnly = keyword.All(c => c <= sbyte.MaxValue);
            StringComparison comparison = asciiOnly ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return text.IndexOf(keyword, comparison) >= 0;
        }

        private PoseCategoryEntry PickRandomPoseEntryByNames(List<PoseCategoryEntry> entries, string[] preferredNames)
        {
            if (entries == null || entries.Count <= 0 || preferredNames == null || preferredNames.Length <= 0)
            {
                return null;
            }

            var matched = FindPoseEntriesByNames(entries, preferredNames);

            if (matched.Count <= 0)
            {
                return null;
            }

            lock (_random)
            {
                return matched[_random.Next(matched.Count)];
            }
        }

        private static List<PoseCategoryEntry> FindPoseEntriesByNames(List<PoseCategoryEntry> entries, string[] preferredNames)
        {
            if (entries == null || entries.Count <= 0 || preferredNames == null || preferredNames.Length <= 0)
            {
                return new List<PoseCategoryEntry>();
            }

            var exact = entries
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.NameAnimation))
                .Where(e => preferredNames.Any(name =>
                    !string.IsNullOrWhiteSpace(name) &&
                    string.Equals(e.NameAnimation, name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (exact.Count > 0)
            {
                return exact;
            }

            return entries
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.NameAnimation))
                .Where(e => preferredNames.Any(name =>
                    !string.IsNullOrWhiteSpace(name) &&
                    e.NameAnimation.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
        }

        private static string BuildPoseRuleEntryLabel(PoseKeywordScoreRule rule, int index)
        {
            string category = rule != null && !string.IsNullOrWhiteSpace(rule.Category) ? rule.Category : "未分類";
            string[] tokens = (rule?.Tokens ?? new PoseKeywordScoreToken[0])
                .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Keyword))
                .Select(t => t.Keyword)
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            string hint = tokens.Length > 0
                ? string.Join("+", tokens)
                : (rule?.RuleId ?? "rule");
            hint = TruncateForConfigLabel(hint, 16);
            return $"{index:D2} {category}:{hint}";
        }

        private static string BuildPoseInferRuleEntryLabel(PoseCategoryInferRule rule, int index)
        {
            string category = rule != null && !string.IsNullOrWhiteSpace(rule.TargetCategory) ? rule.TargetCategory : "未分類";
            string hint = ExtractInferRuleHint(rule);
            hint = TruncateForConfigLabel(hint, 18);
            return $"{index:D2} {category}:{hint}";
        }

        private static string ExtractInferRuleHint(PoseCategoryInferRule rule)
        {
            string[] reqAll = rule?.RequiredAll ?? new string[0];
            string[] reqAny = rule?.RequiredAny ?? new string[0];
            if (reqAll.Length > 0 && reqAny.Length > 0)
            {
                return reqAll[0] + "+" + reqAny[0];
            }
            if (reqAll.Length > 0)
            {
                return reqAll[0];
            }
            if (reqAny.Length > 0)
            {
                return reqAny[0];
            }
            return rule?.RuleId ?? "infer";
        }

        private static string TruncateForConfigLabel(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            if (maxLength <= 0 || text.Length <= maxLength)
            {
                return text;
            }

            return text.Substring(0, maxLength) + "…";
        }

        private static string[] ResolvePoseCategoryAliases(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                return new string[0];
            }

            var aliases = new List<string>();
            if (PoseCategoryAliases.TryGetValue(category, out var mapped) && mapped != null)
            {
                aliases.AddRange(mapped.Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            aliases.Add(category);
            if (category.EndsWith("系", StringComparison.Ordinal) && category.Length > 1)
            {
                aliases.Add(category.Substring(0, category.Length - 1));
            }

            return aliases
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(x => x.Length)
                .ToArray();
        }

        private bool TrySelectVideoFileNameFromText(string text, out string selectedFileName, out int httpPort, out string reason)
        {
            selectedFileName = null;
            reason = "unknown";
            PluginSettings settings = Settings;
            httpPort = settings != null ? settings.BlankMapAddHttpPort : DefaultBlankMapAddHttpPort;

            if (settings == null)
            {
                reason = "settings unavailable";
                return false;
            }

            if (!settings.EnableVideoPlaybackByResponseText)
            {
                reason = "video response-text playback disabled";
                return false;
            }

            string[] triggerKeywords = SplitKeywords(settings.VideoPlaybackTriggerKeywords);
            if (triggerKeywords.Length <= 0)
            {
                triggerKeywords = new[] { "流す" };
            }

            if (!ContainsAny(text, triggerKeywords))
            {
                reason = "trigger keyword not found";
                return false;
            }

            if (!TryLoadBlankMapAddFolderInfo(settings, out string folderPath, out int resolvedPort, out string loadReason))
            {
                httpPort = resolvedPort;
                reason = loadReason;
                return false;
            }

            httpPort = resolvedPort;
            string[] tokens = BuildVideoMatchTokens(text, triggerKeywords);
            if (tokens.Length <= 0)
            {
                reason = "video name token not found";
                return false;
            }

            HashSet<string> allowedExt = BuildVideoExtensionSet(settings.VideoFileExtensions);
            string[] allFiles;
            try
            {
                allFiles = Directory.GetFiles(folderPath);
            }
            catch (Exception ex)
            {
                reason = "folder scan failed: " + ex.Message;
                return false;
            }

            var matchedFileNames = new List<string>();
            for (int i = 0; i < allFiles.Length; i++)
            {
                string path = allFiles[i];
                string ext = Path.GetExtension(path) ?? string.Empty;
                if (allowedExt.Count > 0 && !allowedExt.Contains(ext))
                {
                    continue;
                }

                string fileName = Path.GetFileName(path);
                string baseName = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                if (ContainsKeyword(text, baseName) || ContainsKeyword(text, fileName))
                {
                    matchedFileNames.Add(fileName);
                    continue;
                }

                for (int t = 0; t < tokens.Length; t++)
                {
                    string token = tokens[t];
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        continue;
                    }

                    if (ContainsKeyword(baseName, token) || ContainsKeyword(fileName, token))
                    {
                        matchedFileNames.Add(fileName);
                        break;
                    }
                }
            }

            if (matchedFileNames.Count <= 0)
            {
                reason = "no partial filename match";
                return false;
            }

            lock (_random)
            {
                selectedFileName = matchedFileNames[_random.Next(matchedFileNames.Count)];
            }

            reason = "matched candidates=" + matchedFileNames.Count;
            return true;
        }

        private bool TryLoadBlankMapAddFolderInfo(PluginSettings settings, out string folderPath, out int httpPort, out string reason)
        {
            folderPath = string.Empty;
            reason = "unknown";
            httpPort = settings != null ? settings.BlankMapAddHttpPort : DefaultBlankMapAddHttpPort;

            if (settings == null)
            {
                reason = "settings unavailable";
                return false;
            }

            string configuredPath = string.IsNullOrWhiteSpace(settings.BlankMapAddSettingsRelativePath)
                ? "..\\MainGameBlankMapAdd\\MapAddSettings.json"
                : settings.BlankMapAddSettingsRelativePath.Trim();

            string settingsPath;
            try
            {
                settingsPath = Path.IsPathRooted(configuredPath)
                    ? Path.GetFullPath(configuredPath)
                    : Path.GetFullPath(Path.Combine(PluginDir ?? string.Empty, configuredPath));
            }
            catch (Exception ex)
            {
                reason = "invalid blank map settings path: " + ex.Message;
                return false;
            }

            if (!File.Exists(settingsPath))
            {
                reason = "blank map settings not found: " + settingsPath;
                return false;
            }

            BlankMapAddSettingsSnapshot snapshot;
            try
            {
                string json = File.ReadAllText(settingsPath, Encoding.UTF8);
                var serializer = new DataContractJsonSerializer(typeof(BlankMapAddSettingsSnapshot));
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                using (var ms = new MemoryStream(bytes))
                {
                    snapshot = serializer.ReadObject(ms) as BlankMapAddSettingsSnapshot;
                }
            }
            catch (Exception ex)
            {
                reason = "blank map settings parse failed: " + ex.Message;
                return false;
            }

            if (snapshot == null)
            {
                reason = "blank map settings parse returned null";
                return false;
            }

            if (snapshot.HttpEnabled.HasValue && !snapshot.HttpEnabled.Value)
            {
                reason = "blank map http disabled";
                return false;
            }

            if (snapshot.HttpPort.HasValue && snapshot.HttpPort.Value > 0 && snapshot.HttpPort.Value <= 65535)
            {
                httpPort = snapshot.HttpPort.Value;
            }

            if (httpPort <= 0 || httpPort > 65535)
            {
                httpPort = DefaultBlankMapAddHttpPort;
            }

            string rawFolder = (snapshot.FolderPlayPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(rawFolder))
            {
                reason = "FolderPlayPath is empty";
                return false;
            }

            try
            {
                folderPath = Path.IsPathRooted(rawFolder)
                    ? Path.GetFullPath(rawFolder)
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(settingsPath) ?? string.Empty, rawFolder));
            }
            catch (Exception ex)
            {
                reason = "folder path invalid: " + ex.Message;
                return false;
            }

            if (!Directory.Exists(folderPath))
            {
                reason = "folder not found: " + folderPath;
                return false;
            }

            reason = "ok";
            return true;
        }

        private void PostVideoPlayByFileName(string fileName, int httpPort)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                LogWarn("[video] filename is empty");
                return;
            }

            PluginSettings settings = Settings;
            string endpointPath = settings != null ? settings.VideoPlayEndpointPath : "/videoroom/play";
            if (string.IsNullOrWhiteSpace(endpointPath))
            {
                endpointPath = "/videoroom/play";
            }

            endpointPath = endpointPath.Trim();
            if (!endpointPath.StartsWith("/", StringComparison.Ordinal))
            {
                endpointPath = "/" + endpointPath;
            }

            int port = httpPort;
            if (port <= 0 || port > 65535)
            {
                port = settings != null ? settings.BlankMapAddHttpPort : DefaultBlankMapAddHttpPort;
            }
            if (port <= 0 || port > 65535)
            {
                port = DefaultBlankMapAddHttpPort;
            }

            string url = "http://127.0.0.1:" + port + endpointPath;
            string payload = "{\"filename\":\"" + EscapeJsonValue(fileName) + "\"}";
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.Timeout = 1500;
                request.ReadWriteTimeout = 1500;

                byte[] body = Encoding.UTF8.GetBytes(payload);
                request.ContentLength = body.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(body, 0, body.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string responseText = reader.ReadToEnd();
                    Log($"[video] play sent filename='{fileName}' status={(int)response.StatusCode} port={port}");
                    if (!string.IsNullOrWhiteSpace(responseText))
                    {
                        Log("[video] play response: " + TrimPreview(responseText, 120));
                    }
                }
            }
            catch (WebException webEx)
            {
                string detail = webEx.Message;
                try
                {
                    if (webEx.Response != null)
                    {
                        using (var stream = webEx.Response.GetResponseStream())
                        using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                        {
                            string body = reader.ReadToEnd();
                            if (!string.IsNullOrWhiteSpace(body))
                            {
                                detail += " body=" + TrimPreview(body, 120);
                            }
                        }
                    }
                }
                catch { }
                LogWarn("[video] play request failed: " + detail + " url=" + url);
            }
            catch (Exception ex)
            {
                LogWarn("[video] play request error: " + ex.Message + " url=" + url);
            }
        }

        private static string[] BuildVideoMatchTokens(string text, string[] triggerKeywords)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return tokens.ToArray();
            }

            string[] keywords = triggerKeywords ?? new string[0];
            for (int i = 0; i < keywords.Length; i++)
            {
                string keyword = keywords[i];
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    continue;
                }

                int start = 0;
                while (true)
                {
                    int idx = text.IndexOf(keyword, start, StringComparison.Ordinal);
                    if (idx < 0)
                    {
                        break;
                    }

                    int beforeStart = Math.Max(0, idx - 32);
                    string before = text.Substring(beforeStart, idx - beforeStart);
                    string beforeToken = ExtractTailVideoToken(before);
                    if (!string.IsNullOrWhiteSpace(beforeToken))
                    {
                        tokens.Add(beforeToken);
                    }

                    int afterStart = idx + keyword.Length;
                    int afterLength = Math.Min(32, Math.Max(0, text.Length - afterStart));
                    if (afterLength > 0)
                    {
                        string after = text.Substring(afterStart, afterLength);
                        string afterToken = ExtractHeadVideoToken(after);
                        if (!string.IsNullOrWhiteSpace(afterToken))
                        {
                            tokens.Add(afterToken);
                        }
                    }

                    start = idx + keyword.Length;
                }
            }

            return tokens
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string ExtractTailVideoToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string tail = text.Trim();
            char[] separators = { ' ', '　', '\t', '\r', '\n', '、', '。', '！', '!', '？', '?', ',', '，', '・', '」', '』', '"', '\'' };
            int cut = tail.LastIndexOfAny(separators);
            if (cut >= 0 && cut < tail.Length - 1)
            {
                tail = tail.Substring(cut + 1);
            }

            tail = tail.Trim();
            if (string.IsNullOrWhiteSpace(tail))
            {
                return string.Empty;
            }

            string[] suffixes =
            {
                "を", "で", "に", "へ", "は", "が", "と", "ね", "よ",
                "動画", "ビデオ", "再生", "して", "する"
            };
            bool changed = true;
            while (changed && !string.IsNullOrWhiteSpace(tail))
            {
                changed = false;
                for (int i = 0; i < suffixes.Length; i++)
                {
                    string suffix = suffixes[i];
                    if (tail.Length > suffix.Length && tail.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        tail = tail.Substring(0, tail.Length - suffix.Length).Trim();
                        changed = true;
                    }
                }
            }

            return tail.Length >= 2 ? tail : string.Empty;
        }

        private static string ExtractHeadVideoToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string head = text.Trim();
            if (string.IsNullOrWhiteSpace(head))
            {
                return string.Empty;
            }

            string[] prefixes =
            {
                "を", "で", "に", "へ", "は", "が", "と", "ね", "よ",
                "動画", "ビデオ", "再生", "して", "する"
            };
            bool changed = true;
            while (changed && !string.IsNullOrWhiteSpace(head))
            {
                changed = false;
                for (int i = 0; i < prefixes.Length; i++)
                {
                    string prefix = prefixes[i];
                    if (head.Length > prefix.Length && head.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        head = head.Substring(prefix.Length).TrimStart();
                        changed = true;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(head))
            {
                return string.Empty;
            }

            char[] separators = { ' ', '　', '\t', '\r', '\n', '、', '。', '！', '!', '？', '?', ',', '，', '・', '」', '』', '"', '\'' };
            int cut = head.IndexOfAny(separators);
            if (cut > 0)
            {
                head = head.Substring(0, cut);
            }
            else if (cut == 0)
            {
                return string.Empty;
            }

            head = head.Trim();
            return head.Length >= 2 ? head : string.Empty;
        }

        private static HashSet<string> BuildVideoExtensionSet(string csv)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] parts = SplitKeywords(csv);
            for (int i = 0; i < parts.Length; i++)
            {
                string ext = parts[i];
                if (string.IsNullOrWhiteSpace(ext))
                {
                    continue;
                }

                ext = ext.Trim();
                if (!ext.StartsWith(".", StringComparison.Ordinal))
                {
                    ext = "." + ext;
                }
                set.Add(ext);
            }

            if (set.Count <= 0)
            {
                for (int i = 0; i < DefaultVideoExtensions.Length; i++)
                {
                    set.Add(DefaultVideoExtensions[i]);
                }
            }

            return set;
        }

        private static string TrimPreview(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            if (maxLength <= 0 || text.Length <= maxLength)
            {
                return text;
            }

            return text.Substring(0, maxLength);
        }

        private static string EscapeJsonValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private string ParseCoordFromText(string text)
        {
            string[] patterns = SplitKeywords(_cfgCoordPattern?.Value ?? "に着替えるね");
            foreach (string pattern in patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;

                int idx = text.IndexOf(pattern, StringComparison.Ordinal);
                if (idx <= 0) continue;

                string before = text.Substring(0, idx);
                int nameStart = 0;
                for (int i = before.Length - 1; i >= 0; i--)
                {
                    char c = before[i];
                    if (c == '、' || c == '。' || c == '！' || c == '♡' || c == ' ' || c == '　' || c == '\n' || c == '\r')
                    {
                        nameStart = i + 1;
                        break;
                    }
                }
                string name = before.Substring(nameStart).Trim();
                if (!string.IsNullOrEmpty(name)) return name;
            }
            return null;
        }

        private ClothesItem[] ParseClothesFromText(string text)
        {
            if (ContainsAny(text, SplitKeywords(_cfgRemoveAllKeywords?.Value ?? "全裸になるね,全部脱ぐね,全部脱いじゃう")))
            {
                var all = new ClothesItem[8];
                for (int i = 0; i < 8; i++) all[i] = new ClothesItem { kind = i, state = 3 };
                return all;
            }
            if (ContainsAny(text, SplitKeywords(_cfgPutOnAllKeywords?.Value ?? "全部着るね")))
            {
                var all = new ClothesItem[8];
                for (int i = 0; i < 8; i++) all[i] = new ClothesItem { kind = i, state = 0 };
                return all;
            }

            string[][] partKeywords = {
                SplitKeywords(_cfgTopKeywords?.Value      ?? "上着,ジャケット,トップス"),
                SplitKeywords(_cfgBottomKeywords?.Value   ?? "スカート,ホットパンツ,ミニスカ,ボトムス,ズボン,パンツ"),
                SplitKeywords(_cfgBraKeywords?.Value      ?? "ブラ"),
                SplitKeywords(_cfgShortsKeywords?.Value   ?? "パンティー,パンティ"),
                SplitKeywords(_cfgGlovesKeywords?.Value   ?? "グローブ,手袋"),
                SplitKeywords(_cfgPanthoseKeywords?.Value ?? "ガーターベルト,パンスト,ガーター"),
                SplitKeywords(_cfgSocksKeywords?.Value    ?? "ストッキング,ニーハイ,靴下"),
                SplitKeywords(_cfgShoesKeywords?.Value    ?? "ハイヒール,スニーカー,サンダル,ヒール,靴"),
            };

            string[] removeKws = SplitKeywords(_cfgRemoveKeywords?.Value ?? "脱ぐね,脱いじゃう");
            string[] shiftKws  = SplitKeywords(_cfgShiftKeywords?.Value  ?? "ずらすね,半脱ぎにするね");
            string[] putOnKws  = SplitKeywords(_cfgPutOnKeywords?.Value  ?? "着るね,付けるね");

            var results = new Dictionary<int, ClothesItem>();
            var actionGroups = new[] {
                Tuple.Create(removeKws,  3),
                Tuple.Create(shiftKws,  -1),
                Tuple.Create(putOnKws,   0),
            };

            foreach (var group in actionGroups)
            {
                string[] actionKws = group.Item1;
                int state = group.Item2;
                foreach (string actionKw in actionKws)
                {
                    int aIdx = 0;
                    while (true)
                    {
                        int pos = text.IndexOf(actionKw, aIdx, StringComparison.Ordinal);
                        if (pos < 0) break;
                        aIdx = pos + 1;

                        int ctxStart = Math.Max(0, pos - 25);
                        string ctx = text.Substring(ctxStart, pos - ctxStart + actionKw.Length);

                        for (int kind = 0; kind < partKeywords.Length; kind++)
                        {
                            if (results.ContainsKey(kind)) continue;
                            foreach (string kw in partKeywords[kind])
                            {
                                if (ctx.IndexOf(kw, StringComparison.Ordinal) >= 0)
                                {
                                    results[kind] = new ClothesItem { kind = kind, state = state };
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            if (results.Count == 0) return null;
            var arr = new ClothesItem[results.Count];
            int j = 0;
            foreach (var v in results.Values) arr[j++] = v;
            return arr;
        }

        private static string[] SplitKeywords(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return new string[0];
            string[] parts = csv.Split(',');
            var list = new List<string>();
            foreach (string p in parts)
            {
                string t = p.Trim();
                if (!string.IsNullOrEmpty(t)) list.Add(t);
            }
            return list.ToArray();
        }

        private static bool ContainsAny(string text, string[] keywords)
        {
            foreach (string kw in keywords)
                if (text.IndexOf(kw, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // ----------------------------------------------------------------
        // pose コマンド: nameAnimation で体位を切り替える
        // ----------------------------------------------------------------

        private void HandlePoseCommand(ExternalVoiceFaceCommand command)
        {
            string name = (command.poseName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name))
            {
                LogWarn("[pose] poseName is empty");
                return;
            }

            HSceneProc proc = CurrentProc;
            if (proc == null)
            {
                LogWarn("[pose] HSceneProc not available");
                return;
            }

            var lists = LstUseAnimInfoField?.GetValue(proc) as List<HSceneProc.AnimationListInfo>[];
            if (lists == null)
            {
                LogWarn("[pose] lstUseAnimInfo not found");
                return;
            }

            bool filterMode = command.poseMode >= 0 && System.Enum.IsDefined(typeof(HFlag.EMode), command.poseMode);
            HFlag.EMode targetMode = filterMode ? (HFlag.EMode)command.poseMode : HFlag.EMode.none;

            int bestScore = int.MinValue;
            HSceneProc.AnimationListInfo best = null;

            for (int i = 0; i < lists.Length; i++)
            {
                var list = lists[i];
                if (list == null) continue;
                for (int j = 0; j < list.Count; j++)
                {
                    var c = list[j];
                    if (c == null) continue;
                    if (filterMode && c.mode != targetMode) continue;

                    int score = 0;
                    if (filterMode) score += 500;

                    if (string.Equals(c.nameAnimation, name, StringComparison.OrdinalIgnoreCase))
                        score += 1000;
                    else if (!string.IsNullOrWhiteSpace(c.nameAnimation) &&
                             c.nameAnimation.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 400;
                    else
                        continue;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = c;
                    }
                }
            }

            if (best == null)
            {
                LogWarn($"[pose] not found: name={name} mode={(filterMode ? command.poseMode.ToString() : "any")}");
                return;
            }

            proc.flags.selectAnimationListInfo = best;
            proc.flags.click = HFlag.ClickKind.actionChange;
            Log($"[pose] → id={best.id} mode={best.mode} name={best.nameAnimation}");
        }

        // ----------------------------------------------------------------
        // clothes コマンド: 各部位の着衣状態を変える
        // ----------------------------------------------------------------

        private void HandleClothesCommand(ExternalVoiceFaceCommand command)
        {
            if (command.clothesItems == null || command.clothesItems.Length == 0)
            {
                LogWarn("[clothes] clothesItems が空です");
                return;
            }

            ChaControl female = null;
            HSceneProc proc = FindCurrentProc();
            if (proc != null)
            {
                int main = ClampMainIndex(proc, command.ResolveMain(Settings?.TargetMainIndex ?? 0));
                female = ResolveFemale(proc, main);
            }
            if (female == null)
            {
                female = UnityEngine.Object.FindObjectsOfType<ChaControl>()
                    .FirstOrDefault(c => c != null && c.sex == 1 && c.visibleAll);
            }
            if (female == null)
            {
                LogWarn("[clothes] ChaControl が見つかりません");
                return;
            }

            foreach (var item in command.clothesItems)
            {
                if (item.kind < 0 || item.kind > 8) continue;
                try
                {
                    if (item.state < 0)
                    {
                        ApplyShiftClothesState(female, item.kind);
                    }
                    else
                    {
                        female.SetClothesState(item.kind, (byte)item.state);
                        Log($"[clothes] SetClothesState kind={item.kind} state={item.state}");
                    }
                }
                catch (Exception ex)
                {
                    LogWarn($"[clothes] 失敗 kind={item.kind}: {ex.Message}");
                }
            }
        }

        private void ApplyShiftClothesState(ChaControl female, int kind)
        {
            byte currentState = 0;
            try
            {
                if (female?.fileStatus?.clothesState != null && kind >= 0 && kind < female.fileStatus.clothesState.Length)
                {
                    currentState = female.fileStatus.clothesState[kind];
                }
            }
            catch
            {
                currentState = 0;
            }

            byte targetState = ResolveShiftTargetState(female, kind, currentState);
            female.SetClothesState(kind, targetState, next: false);
            Log($"[clothes] Shift kind={kind} current={currentState} target={targetState}");
        }

        private static byte ResolveShiftTargetState(ChaControl female, int kind, byte currentState)
        {
            bool hasState1 = female != null && female.IsClothesStateType(kind, 1);
            bool hasState2 = female != null && female.IsClothesStateType(kind, 2);

            if (!hasState1 && !hasState2)
            {
                return currentState <= 3 ? currentState : (byte)0;
            }

            if (currentState <= 0)
            {
                return hasState1 ? (byte)1 : (byte)2;
            }

            if (currentState == 1)
            {
                return hasState2 ? (byte)2 : (byte)1;
            }

            if (currentState == 2)
            {
                return 2;
            }

            return hasState2 ? (byte)2 : (byte)1;
        }

        // ----------------------------------------------------------------
        // coord コマンド: 衣装名で検索して着替える
        // ----------------------------------------------------------------

        private static readonly string[] CoordTypeNames = { "plain", "swim", "pajamas", "bathing" };

        private void HandleCoordCommand(ExternalVoiceFaceCommand command)
        {
            string coordName = (command.coordName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(coordName))
            {
                LogWarn("[coord] coordName が空です");
                return;
            }

            // ChaControl を取得: HScene → フォールバック
            ChaControl female = null;
            HSceneProc proc = FindCurrentProc();
            if (proc != null)
            {
                int main = ClampMainIndex(proc, command.ResolveMain(Settings?.TargetMainIndex ?? 0));
                female = ResolveFemale(proc, main);
            }
            if (female == null)
            {
                female = UnityEngine.Object.FindObjectsOfType<ChaControl>()
                    .FirstOrDefault(c => c != null && c.sex == 1 && c.visibleAll);
            }
            if (female == null)
            {
                LogWarn("[coord] ChaControl が見つかりません");
                return;
            }

            int coordIndex = TryFindCoordIndexByName(female, coordName);
            if (coordIndex < 0)
            {
                LogWarn($"[coord] '{coordName}' に一致するコーデが見つかりません");
                return;
            }

            try
            {
                female.ChangeCoordinateTypeAndReload((ChaFileDefine.CoordinateType)coordIndex, true);
                Log($"[coord] 着替え完了: '{coordName}' → slot={coordIndex}");
            }
            catch (Exception ex)
            {
                LogWarn($"[coord] ChangeCoordinateTypeAndReload 失敗: {ex.Message}");
            }
        }

        private static int TryFindCoordIndexByName(ChaControl female, string name)
        {
            string nameLower = name.ToLowerInvariant();
            var coordSlots = female.chaFile?.coordinate;
            if (coordSlots == null) return -1;

            // MoreOutfitsController のリフレクション準備
            MonoBehaviour moCtrl = null;
            MethodInfo moGetName = null;
            try
            {
                moCtrl = female.gameObject.GetComponents<MonoBehaviour>()
                    .FirstOrDefault(c => c.GetType().Name == "MoreOutfitsController");
                if (moCtrl != null)
                    moGetName = moCtrl.GetType().GetMethod("GetCoodinateName",
                        BindingFlags.Public | BindingFlags.Instance);
            }
            catch { }

            // 完全一致を優先、次に部分一致
            int partialMatch = -1;
            for (int i = 0; i < coordSlots.Length; i++)
            {
                string slotName = GetCoordSlotName(i, moCtrl, moGetName).ToLowerInvariant();
                if (slotName == nameLower) return i;
                if (partialMatch < 0 && (slotName.Contains(nameLower) || nameLower.Contains(slotName)))
                    partialMatch = i;
            }
            return partialMatch;
        }

        private static string GetCoordSlotName(int index, MonoBehaviour moCtrl, MethodInfo moGetName)
        {
            if (index < CoordTypeNames.Length) return CoordTypeNames[index];
            if (moCtrl != null && moGetName != null)
            {
                try { return (moGetName.Invoke(moCtrl, new object[] { index }) as string) ?? index.ToString(); }
                catch { }
            }
            return index.ToString();
        }

        private string NormalizeAudioPath(string incomingPath)
        {
            if (string.IsNullOrWhiteSpace(incomingPath))
            {
                return string.Empty;
            }

            try
            {
                string path = incomingPath.Trim().Trim('"');
                if (!Path.IsPathRooted(path))
                {
                    path = Path.Combine(PluginDir ?? string.Empty, path);
                }

                return Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                LogWarn("normalize audio path failed: " + ex.Message);
                return string.Empty;
            }
        }

        private bool TryApplyFace(HSceneProc proc, int main, ChaControl female, int face, int voiceKind, int action)
        {
            if (face < 0)
            {
                return true;
            }

            object faceCtrl = GetFaceCtrlByFemaleIndex(proc, main);
            if (faceCtrl == null)
            {
                LogWarn("[cmd] faceCtrl not found main=" + main);
                return false;
            }

            try
            {
                var setFaceMethod = AccessTools.Method(faceCtrl.GetType(), "SetFace", new[] { typeof(int), typeof(ChaControl), typeof(int), typeof(int) });
                if (setFaceMethod == null)
                {
                    LogWarn("[cmd] SetFace method not found on " + faceCtrl.GetType().FullName);
                    return false;
                }

                object resultObj = setFaceMethod.Invoke(faceCtrl, new object[] { face, female, voiceKind, action });
                bool ok = resultObj is bool && (bool)resultObj;
                if (!ok)
                {
                    LogWarn("[cmd] SetFace failed face=" + face + " main=" + main);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LogWarn("[cmd] SetFace exception: " + ex.Message);
                return false;
            }
        }

        private static object GetFaceCtrlByFemaleIndex(HSceneProc proc, int femaleIndex)
        {
            if (proc == null)
            {
                return null;
            }

            object face1 = Face1Field?.GetValue(proc);
            if (femaleIndex == 1 && face1 != null)
            {
                return face1;
            }

            return FaceField?.GetValue(proc);
        }

        private int ResolveFace(
            ExternalVoiceFaceCommand command,
            PluginSettings settings,
            out bool keepCurrentFaceMode)
        {
            keepCurrentFaceMode = command.ResolveKeepCurrentFace(settings.KeepCurrentFaceByDefault);
            if (keepCurrentFaceMode)
            {
                return -1;
            }

            int directFace = command.ResolveFace(-1);
            if (directFace >= 0)
            {
                return directFace;
            }

            int randomFromCommand = TrySelectRandomFace(command.faces);
            if (randomFromCommand >= 0)
            {
                return randomFromCommand;
            }

            if (settings.DefaultFace >= 0)
            {
                return settings.DefaultFace;
            }

            int randomFromSettings = TrySelectRandomFace(settings.RandomFaceCandidates);
            if (randomFromSettings >= 0)
            {
                return randomFromSettings;
            }

            return 0;
        }

        private int TrySelectRandomFace(int[] source)
        {
            if (source == null || source.Length == 0)
            {
                return -1;
            }

            int[] valid = new int[source.Length];
            int count = 0;
            for (int i = 0; i < source.Length; i++)
            {
                int face = source[i];
                if (face < 0)
                {
                    continue;
                }

                valid[count] = face;
                count++;
            }

            if (count <= 0)
            {
                return -1;
            }

            lock (_random)
            {
                return valid[_random.Next(count)];
            }
        }

        private HSceneProc FindCurrentProc()
        {
            if (CurrentProc != null)
            {
                return CurrentProc;
            }

            float now = Time.unscaledTime;
            if (now < _nextProcProbeTime)
            {
                return null;
            }

            _nextProcProbeTime = now + 1f;
            CurrentProc = UnityEngine.Object.FindObjectOfType<HSceneProc>();
            return CurrentProc;
        }

        private static int ClampMainIndex(HSceneProc proc, int index)
        {
            if (proc == null || proc.flags == null || proc.flags.lstHeroine == null || proc.flags.lstHeroine.Count <= 0)
            {
                return index < 0 ? 0 : index;
            }

            if (index < 0)
            {
                return 0;
            }

            if (index >= proc.flags.lstHeroine.Count)
            {
                return proc.flags.lstHeroine.Count - 1;
            }

            return index;
        }

        private static ChaControl ResolveFemale(HSceneProc proc, int main)
        {
            if (proc == null || LstFemaleField == null)
            {
                return null;
            }

            try
            {
                IList females = LstFemaleField.GetValue(proc) as IList;
                if (females == null || females.Count <= 0)
                {
                    return null;
                }

                int index = main;
                if (index < 0)
                {
                    index = 0;
                }
                if (index >= females.Count)
                {
                    index = females.Count - 1;
                }

                return females[index] as ChaControl;
            }
            catch (Exception ex)
            {
                LogWarn("resolve female failed: " + ex.Message);
                return null;
            }
        }

        private static int ResolveVoiceNo(HSceneProc proc, int main)
        {
            if (proc == null || proc.flags == null || proc.flags.lstHeroine == null)
            {
                return -1;
            }

            if (main < 0 || main >= proc.flags.lstHeroine.Count || proc.flags.lstHeroine[main] == null)
            {
                return -1;
            }

            return proc.flags.lstHeroine[main].voiceNo;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HSceneProc), "CreateListAnimationFileName")]
        private static void CreateListAnimationFileNamePostfix(HSceneProc __instance)
        {
            CurrentProc = __instance;
            if (Instance != null)
            {
                Instance.EnsurePoseClassificationFilesFromProc(__instance);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HSceneProc), "OnDestroy")]
        private static void HSceneOnDestroyPostfix(HSceneProc __instance)
        {
            if (CurrentProc == __instance)
            {
                if (Instance != null)
                {
                    Instance._blockGameVoiceUntil = 0f;
                    Instance.RestoreVoiceProcStopIfNeeded();
                }
                CurrentProc = null;
                Log("released HSceneProc at OnDestroy");
            }
        }

        internal static void Log(string message)
        {
            Logger?.LogInfo(message);
            AppendFileLog(message);
        }

        internal static void LogWarn(string message)
        {
            Logger?.LogWarning(message);
            AppendFileLog("[WARN] " + message);
        }

        internal static void LogError(string message)
        {
            Logger?.LogError(message);
            AppendFileLog("[ERROR] " + message);
        }

        private static void AppendFileLog(string message)
        {
            if (string.IsNullOrEmpty(LogFilePath))
            {
                return;
            }

            try
            {
                lock (FileLogLock)
                {
                    File.AppendAllText(
                        LogFilePath,
                        $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}",
                        Utf8NoBom);
                }
            }
            catch
            {
            }
        }
    }
}
