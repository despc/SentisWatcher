using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Sandbox.Game.World;

namespace SentisWatcher.Ledger
{
    /// <summary>
    /// Who put items into an inventory when no context says so: read from the call stack. The first frame
    /// outside the game names a plugin or a mod; a game method the ledger knows names a source (a store, a
    /// contract, a script); anything else is "unknown:" and the game method that did it. Stacks are costly,
    /// so at most <see cref="PerSecond"/> are read a second (about one a frame); the frequent paths all have
    /// contexts and never get here. Past that, what the thread's last stack in the same frame said is taken
    /// (a plugin filling a hundred containers does it in one frame), else the source stays "untraced".
    /// </summary>
    public static class Sources
    {
        public const int PerSecond = 60;
        public const string Untraced = "untraced";

        [ThreadStatic] private static ulong _frame;
        [ThreadStatic] private static string _last;

        private static readonly ConcurrentDictionary<Assembly, string> Sides = new ConcurrentDictionary<Assembly, string>();
        private static readonly object Gate = new object();
        private static long _window;
        private static int _inWindow;

        public static string Classify()
        {
            var frame = Sandbox.MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            if (!Allow()) return _last != null && _frame == frame ? _last : Untraced;
            _frame = frame;
            return _last = Read();
        }

        private static string Read()
        {
            var trace = new StackTrace(3, false);
            string firstGame = null;
            for (var i = 0; i < trace.FrameCount; i++)
            {
                var method = trace.GetFrame(i)?.GetMethod();
                var type = method?.DeclaringType;
                if (type == null)
                {
                    // a game method patched by a plugin runs as a copy of its own, named after it
                    var patched = FromPatched(method?.Name);
                    var knownPatched = patched == null ? null : Known(patched.Value.Type, patched.Value.Method);
                    if (knownPatched != null) return knownPatched;
                    continue;
                }
                var side = Side(type.Assembly);
                if (side == null) continue;
                if (side != Game) return side;
                var known = Known(type.Name, method.Name);
                if (known != null) return known;
                if (firstGame == null && !Plumbing(type.Name)) firstGame = type.Name + "." + method.Name;
            }
            return "unknown:" + (firstGame ?? "?");
        }

        /// <summary>A game type and method the ledger knows the meaning of; null for any other.</summary>
        public static string Known(string type, string method)
        {
            switch (type)
            {
                case "MyStoreBlock": return "store";
                case "MySessionComponentContractSystem":
                case "MyContract":
                case "MyContractSalvage":
                case "MyContractFind":
                case "MyContractObtainAndDeliver": return "contract";
                case "MyVisualScriptLogicProvider":
                case "MyCampaignSessionComponent":
                case "MySessionComponentScriptSharedStorage": return "script";
                case "MyStationResourcesGenerator":
                case "MyAgentDefinition":
                case "MyHumanoidBotDefinition":
                case "MyBotDefinition":
                case "MySessionComponentEconomy":
                case "MyStation":
                case "MyGlobalEncountersGenerator":
                case "MyEncounterGenerator":
                case "MyNeutralShipSpawner":
                case "MyPirateAntennas": return "npc";
                case "MyReactor": return "reactor";
                // a block that makes items out of light and water: the algae farm (MySolarFoodGenerator calls
                // MyItemProducerComponent.Produce, into its own inventory or down the conveyors)
                case "MyItemProducerComponent":
                case "MySolarFoodGenerator": return "farm";
                case "MyEntityInventorySpawnComponent": return "bag";
                case "MySpaceRespawnComponent":
                case "MyRespawnComponentBase":
                case "MyRespawnComponent":
                case "MyMedicalRoom":
                case "MySurvivalKit": return "respawn";
                case "MyTradingManager": return "trade";
                case "MyPrefabManager": return "prefab";
                case "MyFloatingObjects":
                case "MyFloatingObject": return "pickup";
                case "MyProjectorBase":
                case "MyCubeBuilder": return "build";
                case "MyCharacter":
                    return method.Contains("Die") || method.Contains("Kill") || method.Contains("Bag") ? "bag" : null;
                case "MyCubeGrid":
                    if (method.Contains("Paste")) return "paste";
                    if (method.Contains("Split") || method.Contains("Merge") || method.Contains("MoveBlocks")) return "split";
                    return null;
                default:
                    return type.StartsWith("MyGuiScreenDebug") || type.StartsWith("MyGuiScreenAdmin") ? "admin" : null;
            }
        }

        /// <summary>The game types <see cref="Known"/> knows, the longest names first (one may begin with another).</summary>
        private static readonly string[] KnownTypes =
        new[]{
            "MyStoreBlock", "MySessionComponentContractSystem", "MyContract", "MyContractSalvage", "MyContractFind",
            "MyContractObtainAndDeliver", "MyVisualScriptLogicProvider", "MyCampaignSessionComponent",
            "MySessionComponentScriptSharedStorage", "MyStationResourcesGenerator", "MyAgentDefinition", "MyHumanoidBotDefinition",
            "MyBotDefinition", "MySessionComponentEconomy", "MyStation", "MyGlobalEncountersGenerator", "MyEncounterGenerator",
            "MyNeutralShipSpawner", "MyPirateAntennas", "MyReactor", "MyItemProducerComponent", "MySolarFoodGenerator", "MyEntityInventorySpawnComponent", "MySpaceRespawnComponent",
            "MyRespawnComponentBase", "MyRespawnComponent", "MyMedicalRoom", "MySurvivalKit", "MyTradingManager", "MyPrefabManager",
            "MyFloatingObjects", "MyFloatingObject", "MyProjectorBase", "MyCubeBuilder", "MyCharacter", "MyCubeGrid",
        }.OrderByDescending(t => t.Length).ToArray();

        /// <summary>
        /// The game type and method a patched copy stands for, from its name ("Patched_Sandbox.Game.World.MyPlayerSpawnAt_0":
        /// the namespace, then the type and the method run together); null when it names none of <see cref="KnownTypes"/>.
        /// </summary>
        public static (string Type, string Method)? FromPatched(string name)
        {
            if (string.IsNullOrEmpty(name) || !name.StartsWith("Patched_")) return null;
            var last = name.Substring(name.LastIndexOf('.') + 1);
            var end = last.LastIndexOf('_');
            if (end > 0) last = last.Substring(0, end);
            foreach (var type in KnownTypes)
                if (last.StartsWith(type, StringComparison.Ordinal) && last.Length > type.Length && char.IsUpper(last[type.Length]))
                    return (type, last.Substring(type.Length));
            return null;
        }

        /// <summary>Game types that only carry a call through and say nothing about where the items came from.</summary>
        private static bool Plumbing(string type) =>
            type.StartsWith("MyInventory") || type == "MyEntities" || type == "MyEntity" || type.StartsWith("MyEntityComponent") ||
            type == "MyComponentContainer" || type == "MyEntityExtensions" || type.StartsWith("<");

        private const string Game = "game";

        /// <summary>"game" for the game and Torch, null for the runtime and this plugin, else "plugin:X" or "mod:X".</summary>
        private static string Side(Assembly assembly) => Sides.GetOrAdd(assembly, a =>
        {
            if (a == typeof(Sources).Assembly) return null;
            var name = a.GetName().Name ?? "?";
            if (name == "mscorlib" || name.StartsWith("System") || name.StartsWith("Microsoft") || name == "0Harmony" || name.StartsWith("Mono.") ||
                name == "NLog" || name == "Newtonsoft.Json" || name.StartsWith("protobuf") || name.StartsWith("ProtoBuf")) return null;
            if (name.StartsWith("Sandbox") || name.StartsWith("VRage") || name.StartsWith("SpaceEngineers") || name == "HavokWrapper" ||
                name == "Torch" || name.StartsWith("Torch.")) return Game;
            var mod = MyScriptManager.Static?.Scripts?.Values.Contains(a) == true;
            return (mod ? "mod:" : "plugin:") + FlowSet.Clean(name);
        });

        private static bool Allow()
        {
            var second = Stopwatch.GetTimestamp() / Stopwatch.Frequency;
            lock (Gate)
            {
                if (second != _window)
                {
                    _window = second;
                    _inWindow = 0;
                }
                return ++_inWindow <= PerSecond;
            }
        }
    }
}
