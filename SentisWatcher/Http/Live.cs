using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace SentisWatcher.Http
{
    /// <summary>
    /// What the pages show of the game as it is now, not from the records: who is online, the structures and their
    /// power. The game is read on its own thread: a request leaves a job, the plugin's update runs it within the
    /// frame, the request's thread waits for the answer. The list of structures is kept a few seconds, so a page
    /// open in several browsers costs the game one pass.
    /// </summary>
    public static class Live
    {
        private const int WaitMs = 10000;
        private const int ListKeepMs = 5000;
        private const int DetailKeepMs = 5000;
        private const int MaxUnpowered = 100;
        // a block asks for power from this on, MW (0.1 W): below it is rounding, not a consumer
        private const float Asks = 0.0000001f;
        // What the reads may take of one frame, ms: a read that needs more goes on in the next frames (a structure of
        // tens of thousands of blocks is some tens of ms of work - a visible stall, taken in one frame).
        private const double FrameBudgetMs = 1.0;

        /// <summary>What the last read of this request's thread took of the game thread, ms in all (the answer's X-Game-Ms).</summary>
        [ThreadStatic] public static double GameMs;

        /// <summary>The frames that read was spread over and the most it took of one frame, ms (X-Game-Frames, X-Game-Frame-Ms).</summary>
        [ThreadStatic] public static int GameFrames;
        [ThreadStatic] public static double GameFrameMs;

        /// <summary>The result of a read: what its steps yield last. Anything else they yield is a place to pause.</summary>
        private sealed class Answer
        {
            public object Value;
        }

        private sealed class Job
        {
            public IEnumerator<object> Steps;
            public ManualResetEventSlim Done;
            public volatile bool Abandoned;
            public object Result;
            public Exception Failed;
            public long Ticks, WorstFrame, FrameStarted, InFrame;
            public int Frames;
        }

        private static readonly ConcurrentQueue<Job> Jobs = new ConcurrentQueue<Job>();
        private static Job _current;

        /// <summary>
        /// Game thread, every frame: the reads the requests left, a step at a time until the frame's budget is spent;
        /// the rest waits for the next frame.
        /// </summary>
        public static void Tick()
        {
            if (_current == null && Jobs.IsEmpty) return;
            var frame = System.Diagnostics.Stopwatch.GetTimestamp();
            var budget = (long)(FrameBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            var now = frame;
            do
            {
                if (_current == null)
                {
                    if (!Jobs.TryDequeue(out _current)) return;
                    if (_current.Abandoned) { _current = null; continue; }
                }
                var job = _current;
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var first = job.Ticks == 0 || job.FrameStarted != frame;
                var finished = false;
                try
                {
                    if (job.Abandoned) finished = true;
                    else if (!job.Steps.MoveNext()) finished = true;
                    else if (job.Steps.Current is Answer answer)
                    {
                        job.Result = answer.Value;
                        finished = true;
                    }
                }
                catch (Exception e)
                {
                    job.Failed = e;
                    finished = true;
                }
                now = System.Diagnostics.Stopwatch.GetTimestamp();
                job.Ticks += now - started;
                if (first) { job.Frames++; job.FrameStarted = frame; job.InFrame = 0; }
                job.InFrame += now - started;
                if (job.InFrame > job.WorstFrame) job.WorstFrame = job.InFrame;
                if (finished)
                {
                    _current = null;
                    try { job.Steps.Dispose(); }
                    catch (Exception) { }
                    try { job.Done.Set(); }
                    catch (ObjectDisposedException) { }
                }
            } while (now - frame < budget);
        }

        private static object OnGameThread(IEnumerable<object> steps)
        {
            using (var done = new ManualResetEventSlim())
            {
                var job = new Job { Steps = steps.GetEnumerator(), Done = done };
                Jobs.Enqueue(job);
                if (!done.Wait(WaitMs))
                {
                    job.Abandoned = true;
                    throw new TimeoutException("the game did not answer in " + WaitMs / 1000 + " s (loading, paused or hung)");
                }
                if (job.Failed != null) throw new InvalidOperationException(job.Failed.Message, job.Failed);
                GameMs = job.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                GameFrames = job.Frames;
                GameFrameMs = job.WorstFrame * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                return job.Result;
            }
        }

        // ------------------------------------------------------------------ players

        /// <summary>The players online: name, faction, what they control and where.</summary>
        public static object Online() => OnGameThread(OnlineSteps());

        private static IEnumerable<object> OnlineSteps()
        {
            var players = new List<Dictionary<string, object>>();
            var session = MySession.Static;
            if (session?.Players == null)
            {
                yield return new Answer { Value = players };
                yield break;
            }
            foreach (var player in session.Players.GetOnlinePlayers())
            {
                // wolves and spiders are players to the game
                if (Recording.Wildlife.IsAnimal(player)) continue;
                var identity = player.Identity;
                var id = identity?.IdentityId ?? 0;
                var faction = id == 0 ? null : session.Factions.TryGetPlayerFaction(id);
                var controlled = player.Controller?.ControlledEntity?.Entity;
                var grid = (controlled as MyCubeBlock)?.CubeGrid;
                var position = controlled?.PositionComp?.GetPosition() ?? player.Character?.PositionComp?.GetPosition();
                players.Add(new Dictionary<string, object>
                {
                    ["name"] = player.DisplayName ?? identity?.DisplayName ?? "",
                    ["identity"] = id.ToString(),
                    ["faction"] = faction?.Tag,
                    ["real"] = player.IsRealPlayer,
                    ["dead"] = player.Character == null || player.Character.IsDead,
                    ["grid"] = grid?.DisplayName,
                    ["gridId"] = grid?.EntityId.ToString(),
                    ["x"] = position?.X, ["y"] = position?.Y, ["z"] = position?.Z,
                });
            }
            yield return new Answer { Value = players.OrderBy(p => (string)p["name"], StringComparer.OrdinalIgnoreCase).ToList() };
        }

        // ------------------------------------------------------------------ structures

        private static object _list;
        private static long _listAt;
        private static readonly object ListLock = new object();
        private static readonly System.Diagnostics.Stopwatch Uptime = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>The grids of one structure: what shares power with the grid (rotors, pistons, connectors that pass it).</summary>
        private static List<MyCubeGrid> GroupOf(MyCubeGrid grid)
        {
            var nodes = MyCubeGridGroups.Static?.GetGroups(GridLinkTypeEnum.Electrical)?.GetGroupNodes(grid);
            return nodes != null && nodes.Count > 0 ? nodes : new List<MyCubeGrid> { grid };
        }

        private static MyCubeGrid Main(List<MyCubeGrid> group) =>
            group.OrderByDescending(g => g.BlocksCount).ThenBy(g => g.EntityId).First();

        private static bool Real(MyCubeGrid grid) => grid != null && !grid.Closed && !grid.MarkedForClose && grid.Physics != null && !grid.IsPreview;

        /// <summary>
        /// Every structure, briefly: its grids and blocks, the owner, the power from the game's own distributor
        /// (no pass over the blocks: that is for one structure, on demand).
        /// </summary>
        public static object Structures()
        {
            lock (ListLock)
            {
                if (_list != null && Uptime.ElapsedMilliseconds - _listAt < ListKeepMs) return _list;
                _list = OnGameThread(ListSteps());
                _listAt = Uptime.ElapsedMilliseconds;
                return _list;
            }
        }

        private static IEnumerable<object> ListSteps()
        {
            {
                {
                    var result = new List<Dictionary<string, object>>();
                    var seen = new HashSet<long>();
                    var electricity = MyResourceDistributorComponent.ElectricityId;
                    // the grids as they are now: the pass goes on over frames, and the world changes between them
                    var grids = MyEntities.GetEntities().OfType<MyCubeGrid>().ToArray();
                    var n = 0;
                    foreach (var grid in grids)
                    {
                        if ((++n & 31) == 0) yield return null;
                        if (!Real(grid) || seen.Contains(grid.EntityId)) continue;
                        var group = GroupOf(grid).Where(Real).ToList();
                        if (group.Count == 0) continue;
                        foreach (var g in group) seen.Add(g.EntityId);
                        var main = Main(group);
                        var owner = main.BigOwners != null && main.BigOwners.Count > 0 ? main.BigOwners[0] : 0;
                        var distributor = main.GridSystems?.ResourceDistributor;
                        var position = main.PositionComp.GetPosition();
                        result.Add(new Dictionary<string, object>
                        {
                            ["id"] = main.EntityId.ToString(),
                            ["name"] = main.DisplayName ?? "",
                            ["grids"] = group.Count,
                            ["blocks"] = group.Sum(g => g.BlocksCount),
                            ["pcu"] = group.Sum(g => g.BlocksPCU),
                            ["large"] = main.GridSizeEnum == VRage.Game.MyCubeSize.Large,
                            ["static"] = group.Any(g => g.IsStatic),
                            ["owner"] = owner == 0 ? null : Recording.Identities.NameOf(owner),
                            ["faction"] = owner == 0 ? null : MySession.Static?.Factions?.TryGetPlayerFaction(owner)?.Tag,
                            ["x"] = position.X, ["y"] = position.Y, ["z"] = position.Z,
                            ["available"] = distributor?.MaxAvailableResourceByType(electricity, main) ?? 0f,
                            ["required"] = distributor?.TotalRequiredInputByType(electricity, main) ?? 0f,
                            ["state"] = distributor?.ResourceStateByType(electricity, false, main).ToString(),
                        });
                    }
                    yield return new Answer { Value = result };
                }
            }
        }

        private sealed class Kind
        {
            public string Name;
            public int Count, Working, Unpowered;
            public float Now, Max;
        }

        /// <summary>
        /// One structure in full: its grids, its blocks by type, and the electricity - what makes it and how much,
        /// what takes it and how much, the batteries' charge, and the blocks that ask for power and do not get it.
        /// </summary>
        public static object Structure(long id)
        {
            lock (DetailLock)
            {
                var now = Uptime.ElapsedMilliseconds;
                foreach (var old in Details.Where(d => now - d.Value.At >= DetailKeepMs).Select(d => d.Key).ToList()) Details.Remove(old);
                if (Details.TryGetValue(id, out var kept)) return kept.Value;
                var value = OnGameThread(StructureSteps(id));
                Details[id] = (Uptime.ElapsedMilliseconds, value);
                return value;
            }
        }

        // one structure is read once in a few seconds, however many pages show it
        private static readonly Dictionary<long, (long At, object Value)> Details = new Dictionary<long, (long, object)>();
        private static readonly object DetailLock = new object();

        private static IEnumerable<object> StructureSteps(long id)
        {
            if (!(MyEntities.GetEntityById(id) is MyCubeGrid asked) || !Real(asked))
            {
                yield return new Answer();
                yield break;
            }
            var group = GroupOf(asked).Where(Real).ToList();
            var main = Main(group);
            var electricity = MyResourceDistributorComponent.ElectricityId;

            var blocks = new Dictionary<string, int>();
            var sources = new Dictionary<string, Kind>();
            var sinks = new Dictionary<string, Kind>();
            var unpowered = new List<Dictionary<string, object>>();
            float stored = 0, capacity = 0, batteryOut = 0, batteryIn = 0;
            // the reactors' fuel: what they burn at the present output (kg an hour), what lies in them and elsewhere
            var fuels = new Dictionary<VRage.Game.MyDefinitionId, string>();
            var reactors = new HashSet<long>();
            double burn = 0, fuelInReactors = 0, fuelStored = 0;
            var holders = new List<MyCubeBlock>();
            int unpoweredCount = 0, consumers = 0;

            Kind Of(Dictionary<string, Kind> into, string name)
            {
                if (!into.TryGetValue(name, out var kind)) into[name] = kind = new Kind { Name = name };
                return kind;
            }

            var step = 0;
            foreach (var grid in group)
            {
                // the pass goes on over frames: the grid may be gone by its turn, and its blocks are taken as they
                // are at this moment (a block removed meanwhile is skipped)
                if (!Real(grid)) continue;
                foreach (var slim in grid.GetBlocks().ToArray())
                {
                    if ((++step & 255) == 0) yield return null;
                    var name = slim.BlockDefinition?.DisplayNameText ?? slim.BlockDefinition?.Id.SubtypeName ?? "?";
                    blocks.TryGetValue(name, out var count);
                    blocks[name] = count + 1;
                }
                // What takes power as a system of the grid, not as a block: the gyroscopes together, the conveyors,
                // the thrusters (one sink a group of them).
                void System(string name, MyResourceSinkComponent sink, int count)
                {
                    if (sink == null || count == 0 || !sink.AcceptedResources.Contains(electricity)) return;
                    var required = sink.RequiredInputByType(electricity);
                    var kind = Of(sinks, name);
                    kind.Count += count;
                    kind.Max += required;
                    kind.Now += sink.CurrentInputByType(electricity);
                    if (required <= Asks) return;
                    consumers++;
                    kind.Working += count;
                    if (sink.IsPoweredByType(electricity)) return;
                    kind.Unpowered += count;
                    unpoweredCount++;
                    if (unpowered.Count < MaxUnpowered)
                        unpowered.Add(new Dictionary<string, object>
                        {
                            ["name"] = name, ["type"] = name, ["grid"] = grid.DisplayName,
                            ["required"] = required, ["supplied"] = sink.SuppliedRatioByType(electricity),
                        });
                }
                var systems = grid.GridSystems;
                if (systems?.GyroSystem != null) System("Gyroscopes", systems.GyroSystem.ResourceSink, systems.GyroSystem.GyroCount);
                if (systems?.ConveyorSystem != null)
                    System("Conveyor system", systems.ConveyorSystem.ResourceSink, systems.ConveyorSystem.ResourceSink?.RequiredInputByType(electricity) > 0 ? 1 : 0);
                var thrust = grid.Components.Get<MyEntityThrustComponent>();
                if (thrust != null)
                {
                    var thrustSinks = new Dictionary<MyResourceSinkComponent, int>();
                    foreach (var block in grid.GetFatBlocks())
                    {
                        if (!(block is MyThrust)) continue;
                        var sink = thrust.ResourceSink(block);
                        if (sink == null) continue;
                        thrustSinks.TryGetValue(sink, out var n);
                        thrustSinks[sink] = n + 1;
                    }
                    foreach (var pair in thrustSinks) System("Thrusters", pair.Key, pair.Value);
                }

                if (!Real(grid)) continue;
                foreach (var block in grid.GetFatBlocks().ToArray())
                {
                    if ((++step & 31) == 0) yield return null;
                    if (block.Closed || block.MarkedForClose) continue;
                    var name = block.BlockDefinition?.DisplayNameText ?? block.BlockDefinition?.Id.SubtypeName ?? "?";
                    var source = block.Components.Get<MyResourceSourceComponent>();
                    if (source != null && source.ResourceTypes.Contains(electricity))
                    {
                        var kind = Of(sources, name);
                        kind.Count++;
                        var now = source.CurrentOutputByType(electricity);
                        kind.Now += now;
                        if (source.Enabled && block.IsWorking)
                        {
                            kind.Working++;
                            kind.Max += source.MaxOutputByType(electricity);
                        }
                    }
                    if (block is MyBatteryBlock battery)
                    {
                        stored += battery.CurrentStoredPower;
                        capacity += battery.MaxStoredPower;
                        if (source != null) batteryOut += source.CurrentOutputByType(electricity);
                        batteryIn += battery.ResourceSink?.CurrentInputByType(electricity) ?? 0f;
                    }
                    if (block.HasInventory) holders.Add(block);
                    if (block is MyReactor reactor && reactor.BlockDefinition?.FuelInfos != null && reactor.BlockDefinition.FuelInfos.Length > 0)
                    {
                        // as MyReactor.ConsumeFuel: the share of the full output times the fuel a second at full output
                        var fuel = reactor.BlockDefinition.FuelInfos[0];
                        fuels[fuel.FuelId] = fuel.FuelDefinition?.DisplayNameText ?? fuel.FuelId.SubtypeName;
                        reactors.Add(reactor.EntityId);
                        if (reactor.IsWorking && source != null && reactor.BlockDefinition.MaxPowerOutput > 0)
                            burn += source.CurrentOutputByType(electricity) / reactor.BlockDefinition.MaxPowerOutput * fuel.ConsumptionPerSecond_Items * 3600.0;
                    }
                    var sink = block.Components.Get<MyResourceSinkComponent>();
                    if (sink != null && sink.AcceptedResources.Contains(electricity))
                    {
                        var required = sink.RequiredInputByType(electricity);
                        var kind = Of(sinks, name);
                        kind.Count++;
                        kind.Max += required;
                        kind.Now += sink.CurrentInputByType(electricity);
                        if (required <= Asks) continue;
                        consumers++;
                        kind.Working++;
                        if (sink.IsPoweredByType(electricity)) continue;
                        kind.Unpowered++;
                        unpoweredCount++;
                        if (unpowered.Count < MaxUnpowered)
                            unpowered.Add(new Dictionary<string, object>
                            {
                                ["name"] = Recording.InventorySweep.BlockName(block),
                                ["type"] = name,
                                ["grid"] = grid.DisplayName,
                                ["required"] = required,
                                ["supplied"] = sink.SuppliedRatioByType(electricity),
                            });
                    }
                }
            }

            List<Dictionary<string, object>> Rows(Dictionary<string, Kind> of, bool withUnpowered) => of.Values
                .OrderByDescending(k => k.Now).ThenByDescending(k => k.Max).ThenBy(k => k.Name)
                .Select(k =>
                {
                    var row = new Dictionary<string, object> { ["name"] = k.Name, ["count"] = k.Count, ["working"] = k.Working, ["now"] = k.Now, ["max"] = k.Max };
                    if (withUnpowered) row["unpowered"] = k.Unpowered;
                    return row;
                }).ToList();

            // the fuel in the reactors and in the other inventories of the structure (the reactors pull it by conveyor)
            if (fuels.Count > 0)
                foreach (var holder in holders)
                {
                    if ((++step & 31) == 0) yield return null;
                    if (holder.Closed) continue;
                    for (var i = 0; i < holder.InventoryCount; i++)
                    {
                        if (!(holder.GetInventory(i) is Sandbox.Game.MyInventory inventory)) continue;
                        foreach (var fuel in fuels.Keys)
                        {
                            var amount = (double)inventory.GetItemAmount(fuel);
                            if (reactors.Contains(holder.EntityId)) fuelInReactors += amount;
                            else fuelStored += amount;
                        }
                    }
                }
            if (!Real(main))
            {
                yield return new Answer();
                yield break;
            }

            var owner = main.BigOwners != null && main.BigOwners.Count > 0 ? main.BigOwners[0] : 0;
            var distributor = main.GridSystems?.ResourceDistributor;
            var position = main.PositionComp.GetPosition();
            // what the game's distributor counts over what was found by name: the wheels and whatever else asks
            var other = (distributor?.TotalRequiredInputByType(electricity, main) ?? 0f) - sinks.Values.Sum(k => k.Max);
            if (other > 0.000001f)
            {
                var kind = Of(sinks, "Other grid systems");
                kind.Count = kind.Working = 1;
                kind.Max = kind.Now = other;
                consumers++;
            }
            yield return new Answer { Value = new Dictionary<string, object>
            {
                ["id"] = main.EntityId.ToString(),
                ["name"] = main.DisplayName ?? "",
                ["owner"] = owner == 0 ? null : Recording.Identities.NameOf(owner),
                ["faction"] = owner == 0 ? null : MySession.Static?.Factions?.TryGetPlayerFaction(owner)?.Tag,
                ["x"] = position.X, ["y"] = position.Y, ["z"] = position.Z,
                ["grids"] = group.Where(Real).OrderByDescending(g => g.BlocksCount).Select(g => new Dictionary<string, object>
                {
                    ["id"] = g.EntityId.ToString(), ["name"] = g.DisplayName ?? "", ["blocks"] = g.BlocksCount, ["pcu"] = g.BlocksPCU,
                    ["large"] = g.GridSizeEnum == VRage.Game.MyCubeSize.Large, ["static"] = g.IsStatic,
                    ["mass"] = g.Physics?.Mass ?? 0f,
                }).ToList(),
                ["blocks"] = blocks.OrderByDescending(p => p.Value).ThenBy(p => p.Key)
                    .Select(p => new Dictionary<string, object> { ["name"] = p.Key, ["count"] = p.Value }).ToList(),
                ["power"] = new Dictionary<string, object>
                {
                    ["state"] = distributor?.ResourceStateByType(electricity, false, main).ToString(),
                    ["produced"] = sources.Values.Sum(k => k.Now),
                    ["capacity"] = sources.Values.Sum(k => k.Max),
                    ["consumed"] = sinks.Values.Sum(k => k.Now),
                    ["required"] = sinks.Values.Sum(k => k.Max),
                    ["stored"] = stored,
                    ["storage"] = capacity,
                    ["batteryOut"] = batteryOut,
                    ["batteryIn"] = batteryIn,
                    ["fuel"] = fuels.Count == 0 ? null : new Dictionary<string, object>
                    {
                        ["name"] = string.Join(", ", fuels.Values.Distinct()),
                        ["reactors"] = reactors.Count,
                        ["inReactors"] = fuelInReactors,
                        ["stored"] = fuelStored,
                        ["burn"] = burn,
                    },
                    ["consumers"] = consumers,
                    ["unpoweredCount"] = unpoweredCount,
                    ["sources"] = Rows(sources, false),
                    ["sinks"] = Rows(sinks, true),
                    ["unpowered"] = unpowered,
                },
            } };
        }
    }
}
