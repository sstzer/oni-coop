namespace OniCoop
{
    using System.Collections;
    using System.Collections.Generic;
    using System.Reflection;
    using HarmonyLib;
    using UnityEngine;

    /// <summary>Scripted test (-coop-autotest cmds): drive the real tools on each side and log what both worlds got.
    /// The guest digs 6 cells, orders 3 tiles and toggles pause twice; the host digs 3 other cells.</summary>
    internal static class AutoTest
    {
        public const byte ChShot = 7;
        public const byte ChPhase = 18; // motion test: the server's phase markers

        // ================================================================== motion test (-coop-autotest motion)
        // The server runs phases (speeds, pausing, saving), the guest does what a player does with its own view
        // (zoom out, pan). Both measure what a player would see as stutter (Puppets.ProbeReport) per phase.

        private static MonoBehaviour motionRunner;
        private static string guestPhase;
        private static Coroutine panning;

        private static readonly string[] PhaseNames = { "1x", "3x", "3x-zoomed-out", "2x-panning", "speed-switching", "2x-server-saves" };
        private static readonly int[] PhaseSpeed = { 0, 2, 2, 1, -1, 1 };
        private static readonly float[] PhaseSecs = { 25f, 25f, 25f, 25f, 20f, 25f };

        private static void SetRunning(int speed)
        {
            SpeedControlScreen sc = SpeedControlScreen.Instance;
            if (speed < 0) { if (!sc.IsPaused) sc.Pause(false); return; }
            sc.SetSpeed(speed);
            for (int i = 0; i < 4 && sc.IsPaused; i++) sc.Unpause(false);
        }

        private static void SendPhase(string name)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(name);
            CommandBus.Client.SendRelay(OniCoop.Net.Proto.TargetAll, ChPhase, b);
        }

        private static IEnumerator HostMotion(CoopSettings s)
        {
            FastSave.SelfCheck();
            float until = Time.realtimeSinceStartup + 180f;
            while (EntitySync.GuestsInGame == 0 && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(1f);
            Log.Info("motion: a guest is in; settling 20 s");
            SetRunning(0);
            yield return new WaitForSecondsRealtime(20f);
            for (int i = 0; i < PhaseNames.Length; i++)
            {
                string name = PhaseNames[i];
                SetRunning(PhaseSpeed[i] < 0 ? 0 : PhaseSpeed[i]);
                SendPhase(name);
                Puppets.ProbeReset();
                float start = Time.realtimeSinceStartup, end = start + PhaseSecs[i];
                bool saved = false;
                int step = 0;
                while (Time.realtimeSinceStartup < end)
                {
                    float t = Time.realtimeSinceStartup - start;
                    if (name == "speed-switching" && t >= step * 1.5f)
                    {
                        int[] cycle = { -1, 0, 2, -1, 1, 2, 0 }; // pause, 1x, 3x, pause, 2x, 3x, 1x
                        SetRunning(cycle[step % cycle.Length]);
                        step++;
                    }
                    if (name == "2x-server-saves" && !saved && t >= 8f)
                    {
                        saved = true;
                        string file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(s.autoLoad), "coop-motion-test.sav");
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        SaveLoader.Instance.Save(file, false, false);
                        Log.Info($"motion: the server saved in {sw.ElapsedMilliseconds} ms");
                    }
                    yield return null;
                }
                Log.Info($"motion {name} (server): {Puppets.ProbeReport(true)}");
            }
            SendPhase("end");
            SetRunning(0);
        }

        /// <summary>Guest: the server says which phase this is now.</summary>
        public static void OnPhase(byte[] data)
        {
            string name = System.Text.Encoding.UTF8.GetString(data);
            if (guestPhase != null) Log.Info($"motion {guestPhase} (guest): {Puppets.ProbeReport(false)}");
            Puppets.ProbeReset();
            guestPhase = name == "end" ? null : name;
            if (panning != null && motionRunner != null) { motionRunner.StopCoroutine(panning); panning = null; }
            CameraController cam = CameraController.Instance;
            if (cam == null) return;
            int anchor = Anchor();
            Vector3 at = Grid.CellToPosCCC(anchor, Grid.SceneLayer.Move);
            if (name == "3x-zoomed-out")
            {
                float max = Traverse.Create(cam).Field("maxOrthographicSize").GetValue<float>();
                cam.SnapTo(at, max);
                Log.Info($"motion: guest zoomed out to {max}");
            }
            else if (name == "2x-panning" && motionRunner != null) panning = motionRunner.StartCoroutine(Pan(cam, at));
            else cam.SnapTo(at, 10f);
        }

        /// <summary>Guest: slide the view across the map like a player holding a pan key, with a jump now and then.</summary>
        private static IEnumerator Pan(CameraController cam, Vector3 from)
        {
            float speed = 45f, dir = 1f, nextJump = Time.realtimeSinceStartup + 6f;
            Vector3 p = from;
            float w = Grid.WidthInCells, h = Grid.HeightInCells;
            while (true)
            {
                p.x += dir * speed * Time.unscaledDeltaTime;
                if (p.x > w - 20f || p.x < 20f) { dir = -dir; p.y = Mathf.Clamp(p.y + 15f, 20f, h - 20f); }
                if (Time.realtimeSinceStartup >= nextJump)
                {
                    nextJump = Time.realtimeSinceStartup + 6f;
                    p = new Vector3(Random.Range(30f, w - 30f), Random.Range(30f, h - 30f), p.z);
                }
                cam.SnapTo(p, 12f);
                yield return null;
            }
        }
        private static bool started;
        private static int syncShots;
        private static readonly List<int> digCells = new List<int>(), tileCells = new List<int>(), hostDigCells = new List<int>();

        public static void Tick(MonoBehaviour runner, CoopSettings s, float t)
        {
            if (started || (s.autotest != "cmds" && s.autotest != "motion") || !CommandBus.Active || t < 20f) return;
            started = true;
            if (s.autotest == "motion") { if (s.IsHost) runner.StartCoroutine(HostMotion(s)); else motionRunner = runner; return; }
            shotRunner = runner;
            runner.StartCoroutine(s.IsHost ? HostScenario(s) : GuestScenario());
        }

        /// <summary>Remember which cells the other side's orders were about (instead of guessing them).</summary>
        public static void Observe(CommandBus.Command c)
        {
            if (c.kind == CommandBus.Kind.DragCells && c.tool == "DigTool")
            {
                List<int> into = c.issuer == 0 ? hostDigCells : digCells;
                into.Clear();
                into.AddRange(c.cells);
            }
            else if (c.kind == CommandBus.Kind.Build && c.issuer != 0 && !tileCells.Contains(c.cells[0])) tileCells.Add(c.cells[0]);
        }

        private static readonly List<KeyValuePair<byte, float>> pendingShots = new List<KeyValuePair<byte, float>>();

        /// <summary>Host's signal [n][host time]: the host captures at once, a guest when its mirror shows that same
        /// host moment, so the two screenshots / dumps should show the same world.</summary>
        public static void SyncShot(CoopSettings s, byte[] data)
        {
            if (data.Length < 5) return;
            float at = System.BitConverter.ToSingle(data, 1);
            // a guest whose timeline already shows that moment (the signal itself waits for the timeline now): the
            // duplicants first, before the slow dumps below move the timeline on
            bool capturedNow = false;
            if (!s.IsHost && Puppets.RenderTime >= at)
            {
                Capture(s, data[0], $" (showing host t={Puppets.RenderTime:F2} for {at:F2}, {Puppets.BufferMs:F0} ms buffer)");
                if (shotRunner != null && !Application.isBatchMode) shotRunner.StartCoroutine(WorkShot(s, data[0]));
                capturedNow = true;
            }
            // objects: the signal comes after every record sent before it, so both sides show the same moment now
            WriteDump(s, data[0], "entities", EntitySync.Dump());
            WriteDump(s, data[0], "buildings", BuildingSync.Dump(false));
            WriteDump(s, data[0], "settings", SettingsDump());
            WriteDump(s, data[0], "global", GlobalSync.Dump());
            WriteDump(s, data[0], "status", StatusDump());
            WriteDump(s, data[0], "report", ReportSync.Dump());
            WriteDump(s, data[0], "operation", OperationSync.Dump());
            if (s.IsHost) Capture(s, data[0], "");
            else if (!capturedNow) pendingShots.Add(new KeyValuePair<byte, float>(data[0], at));
            if (!s.IsHost) ReleaseHold();
        }

        /// <summary>Guest: a sync signal arrived (it waits for the timeline before it is handled): hold the timeline at its
        /// moment until the duplicants are captured there.</summary>
        public static void ShotComing(byte[] data)
        {
            if (data.Length < 5) return;
            float at = System.BitConverter.ToSingle(data, 1);
            Puppets.TestHoldAt = Puppets.TestHoldAt < 0f ? at : Mathf.Min(Puppets.TestHoldAt, at);
        }

        private static void ReleaseHold()
        {
            float min = -1f;
            foreach (var p in pendingShots) if (min < 0f || p.Value < min) min = p.Value;
            Puppets.TestHoldAt = min;
        }

        /// <summary>Guest, every frame.</summary>
        public static void PendingShots(CoopSettings s)
        {
            for (int i = pendingShots.Count - 1; i >= 0; i--)
            {
                if (Puppets.RenderTime < pendingShots[i].Value) continue;
                Capture(s, pendingShots[i].Key, $" (showing host t={Puppets.RenderTime:F2} for {pendingShots[i].Value:F2}, {Puppets.BufferMs:F0} ms buffer)");
                if (shotRunner != null && !Application.isBatchMode) shotRunner.StartCoroutine(WorkShot(s, pendingShots[i].Key));
                pendingShots.RemoveAt(i);
                ReleaseHold();
            }
        }

        private static MonoBehaviour shotRunner;

        /// <summary>Guest: a close-up of a duplicant at work (tool in hand, beam, sparks: PuppetLooks), next frame,
        /// then the camera goes back.</summary>
        private static IEnumerator WorkShot(CoopSettings s, byte n)
        {
            yield return null;
            CameraController cam = CameraController.Instance;
            if (cam == null) yield break;
            foreach (MinionIdentity m in Components.LiveMinionIdentities.Items)
            {
                var aeh = m != null ? m.GetComponent<AnimEventHandler>() : null;
                if (aeh == null || !aeh.GetContext().IsValid) continue;
                Vector3 back = cam.transform.GetPosition();
                float size = cam.OrthographicSize;
                cam.SnapTo(m.transform.GetPosition(), 6f);
                yield return null;
                string dir = System.IO.Path.Combine(GameBridge.CoopDir, "shots");
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, $"{s.name}_sync{n}_work.png"));
                var kbac = m.GetComponent<KBatchedAnimController>();
                Log.Info($"autotest: work close-up sync{n}: {m.GetProperName()} {(kbac != null && kbac.CurrentAnim != null ? kbac.CurrentAnim.name : "")} context {aeh.GetContext()}");
                yield return null;
                yield return null;
                cam.SnapTo(back, size);
                yield break;
            }
        }

        private static void WriteDump(CoopSettings s, byte n, string what, string text)
        {
            string dir = System.IO.Path.Combine(GameBridge.CoopDir, "shots");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"{s.name}_sync{n}_{what}.txt"), text);
        }

        private static void Capture(CoopSettings s, byte n, string note)
        {
            string dir = System.IO.Path.Combine(GameBridge.CoopDir, "shots");
            System.IO.Directory.CreateDirectory(dir);
            if (!Application.isBatchMode) ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, $"{s.name}_sync{n}.png"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"{s.name}_sync{n}.txt"), Puppets.Dump());
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"{s.name}_sync{n}_looks.txt"), PuppetLooks.Dump());
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"{s.name}_sync{n}_anims.txt"), BuildingSync.Dump(true));
            Log.Info($"autotest: sync{n}{note} | {Report()}");
            var sb = new System.Text.StringBuilder();
            int k = 0;
            foreach (MinionIdentity m in Components.LiveMinionIdentities.Items)
            {
                if (m == null || k++ >= 6) continue;
                Vector3 p = m.transform.GetPosition();
                var kbac = m.GetComponent<KBatchedAnimController>();
                sb.Append($" {m.GetProperName()}@({p.x:F1},{p.y:F1}){(kbac != null && kbac.CurrentAnim != null ? kbac.CurrentAnim.name : "")}");
            }
            Log.Info($"autotest: sync{n} dupes:{sb}");
        }

        private static int Anchor()
        {
            GameObject pad = GameUtil.GetTelepad(0);
            return pad != null ? Grid.PosToCell(pad) : Grid.PosToCell(Camera.main.transform.position);
        }

        private static void Look(int cell)
        {
            if (CameraController.Instance != null) CameraController.Instance.SnapTo(Grid.CellToPosCCC(cell, Grid.SceneLayer.Front));
        }

        /// <summary>Solid natural cells (diggable) near the anchor, in a fixed scan order so both sides agree.</summary>
        private static void FindDig(int anchor, int skip, int count, List<int> into)
        {
            into.Clear();
            Grid.CellToXY(anchor, out int ax, out int ay);
            int seen = 0;
            // square rings around the anchor, each ring scanned in a fixed order
            for (int r = 2; r < 60 && into.Count < count; r++)
                for (int dy = -r; dy <= r && into.Count < count; dy++)
                    for (int dx = -r; dx <= r && into.Count < count; dx++)
                    {
                        if (System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy)) != r) continue;
                        int c = Grid.XYToCell(ax + dx, ay + dy);
                        if (!Grid.IsValidCell(c) || !Grid.Solid[c] || Grid.Foundation[c] || Grid.Objects[c, 7] != null) continue;
                        if (Grid.Objects[c, (int)ObjectLayer.Building] != null || Grid.Objects[c, (int)ObjectLayer.FoundationTile] != null) continue;
                        if (seen++ < skip) continue;
                        into.Add(c);
                    }
        }

        /// <summary>Three empty cells in a row (no solid, no building, no tile).</summary>
        private static void FindTiles(int anchor, List<int> into)
        {
            into.Clear();
            Grid.CellToXY(anchor, out int ax, out int ay);
            for (int dy = 2; dy < 25; dy++)
                for (int dx = -15; dx < 15; dx++)
                {
                    bool ok = true;
                    for (int i = 0; i < 3 && ok; i++)
                    {
                        int c = Grid.XYToCell(ax + dx + i, ay + dy);
                        ok = Grid.IsValidCell(c) && !Grid.Solid[c] && !Grid.Foundation[c]
                             && Grid.Objects[c, (int)ObjectLayer.Building] == null && Grid.Objects[c, (int)ObjectLayer.FoundationTile] == null
                             && Grid.Objects[c, (int)ObjectLayer.Backwall] == null && !Grid.Element[c].IsLiquid;
                    }
                    if (!ok) continue;
                    for (int i = 0; i < 3; i++) into.Add(Grid.XYToCell(ax + dx + i, ay + dy));
                    return;
                }
        }

        private static void Drag(DragTool tool, List<int> cells)
        {
            MethodInfo m = AccessTools.Method(tool.GetType(), "OnDragTool", new[] { typeof(int), typeof(int) });
            for (int i = 0; i < cells.Count; i++) m.Invoke(tool, new object[] { cells[i], i });
        }

        public static string Report()
        {
            int dig = 0, hostDig = 0, tiles = 0;
            foreach (int c in digCells) if (Grid.Objects[c, 7] != null) dig++;
            foreach (int c in hostDigCells) if (Grid.Objects[c, 7] != null) hostDig++;
            foreach (int c in tileCells)
            {
                GameObject go = Grid.Objects[c, (int)ObjectLayer.Building];
                if (go != null && go.GetComponent<Constructable>() != null) tiles++;
            }
            var cellsNow = new System.Text.StringBuilder();
            foreach (int c in tileCells)
            {
                GameObject go = Grid.Objects[c, (int)ObjectLayer.Building];
                cellsNow.Append($" {c}:{(go != null ? go.PrefabID().Name : "-")}{(Grid.Foundation[c] ? "/foundation" : "")}{(Grid.Solid[c] ? "/solid" : "")}");
            }
            return $"guest dig orders {dig}/{digCells.Count}, host dig orders {hostDig}/{hostDigCells.Count}, tile orders {tiles}/{tileCells.Count}, tile cells{cellsNow}" +
                   $" | puppets: host {Puppets.HostReport()}; guest {Puppets.GuestReport()}, missing anim files {Puppets.MissingAnimFiles}" +
                   $" | entities: {(CommandBus.IsHost ? EntitySync.HostReport() : EntitySync.GuestReport())}" +
                   $" | {BuildingSync.Report()} | {GlobalSync.Report()} | {StatusSync.Report()} | {ReportSync.Report()}" +
                   $" | {PuppetLooks.Report(CommandBus.IsHost)} | {OperationSync.Report(CommandBus.IsHost)} | {TextParts.Report()} | {Feedback.Report(CommandBus.IsHost)} | {GcPolicy.Report()} | {FastSave.Report()}" +
                   $" | cells: host sent {WorldSync.HostCellsSent} ({WorldSync.HostBytesSent / 1024} KB), guest got {WorldSync.GuestCellsReceived}, applied {WorldSync.GuestCellsApplied}, pinned {WorldSync.GuestCellsPinned}";
        }

        // ------------------------------------------------------------------ side screens / management screens

        /// <summary>Guest: act like a player in the UI (inside a fake UI dispatch, as a click would be).</summary>
        private static IEnumerator GuestUi()
        {
            var done = new List<string>();
            System.Action<string, System.Action> act = (what, a) =>
            {
                Calls.UiDispatch = true;
                try { a(); done.Add(what); }
                catch (System.Exception e) { Log.Warn($"autotest ui: {what} failed: {e.Message}"); }
                finally { Calls.UiDispatch = false; }
            };
            BuildingComplete Find<T>() where T : class
            {
                foreach (BuildingComplete b in Components.BuildingCompletes.Items)
                    if (b != null && b.GetComponent<T>() != null) return b;
                return null;
            }
            var cap = Find<IUserControlledCapacity>();
            if (cap != null) act($"capacity of {cap.name} -> 1234", () => cap.GetComponent<IUserControlledCapacity>().UserMaxCapacity = 1234f);
            var door = Find<Door>();
            if (door != null) act($"door {door.name} -> locked", () => door.GetComponent<Door>().QueueStateChange(Door.ControlState.Locked));
            var en = Find<BuildingEnabledButton>();
            if (en != null)
            {
                var button = (System.Action)System.Delegate.CreateDelegate(typeof(System.Action), en.GetComponent<BuildingEnabledButton>(),
                    AccessTools.Method(typeof(BuildingEnabledButton), "OnMenuToggle"));
                act($"disable button on {en.name}", () => Calls.MenuClick(button));
            }
            Tech next = null;
            foreach (Tech t in Db.Get().Techs.resources)
                if (!t.IsComplete() && t.ArePrerequisitesComplete() && (Research.Instance.GetActiveResearch() == null || Research.Instance.GetActiveResearch().tech != t)) { next = t; break; }
            if (next != null) act($"research {next.Id}", () => Research.Instance.SetActiveResearch(next, true));
            MinionIdentity dupe = Components.LiveMinionIdentities.Items.Count > 0 ? Components.LiveMinionIdentities.Items[0] : null;
            if (dupe != null)
            {
                var cc = dupe.GetComponent<ChoreConsumer>();
                int prio = cc.GetPersonalPriority(Db.Get().ChoreGroups.Dig) == 5 ? 1 : 5;
                act($"{dupe.name} digging priority -> {prio}", () => cc.SetPersonalPriority(Db.Get().ChoreGroups.Dig, prio));
                var food = dupe.GetComponent<ConsumableConsumer>();
                bool allowed = food.IsPermitted("FieldRation");
                act($"{dupe.name} field ration -> {!allowed}", () => food.SetPermitted("FieldRation", !allowed));
            }
            List<Schedule> schedules = ScheduleManager.Instance.GetSchedules();
            if (schedules.Count > 0) act("schedule 0 block 0 -> recreation", () => schedules[0].SetBlockGroup(0, Db.Get().ScheduleGroups.Recreation));
            foreach (ComplexFabricator f in Object.FindObjectsOfType<ComplexFabricator>())
            {
                ComplexRecipe[] recipes = f.GetRecipes();
                if (recipes.Length == 0) continue;
                act($"fabricator {f.name}: one more {recipes[0].id}", () => f.IncrementRecipeQueueCount(recipes[0]));
                break;
            }
            // panels that set fields directly: open the real side screen and move its slider, as a player would
            var timerB = Find<LogicTimerSensor>();
            if (timerB != null)
            {
                SelectTool.Instance.Select(timerB.GetComponent<KSelectable>());
                yield return new WaitForSecondsRealtime(1f);
                var ts = Object.FindObjectOfType<TimerSideScreen>();
                if (ts != null) act($"timer {timerB.name}: on 37 s, off 23 s", () => { ts.onDurationSlider.value = 37f; ts.offDurationSlider.value = 23f; });
                else Log.Warn("autotest ui: the timer's side screen didn't open");
            }
            var rangeB = Find<LogicTimeOfDaySensor>();
            if (rangeB != null)
            {
                SelectTool.Instance.Select(rangeB.GetComponent<KSelectable>());
                yield return new WaitForSecondsRealtime(1f);
                var rs = Object.FindObjectOfType<TimeRangeSideScreen>();
                if (rs != null) act($"time range {rangeB.name}: start 0.30, duration 0.45", () => { rs.startTime.value = 0.3f; rs.duration.value = 0.45f; });
                else Log.Warn("autotest ui: the time range side screen didn't open");
            }
            SelectTool.Instance.Select(null);
            Log.Info($"autotest ui: {done.Count} actions: {string.Join("; ", done.ToArray())} | {Calls.Report()}");
            yield return GuestPrint();
            yield return GuestReject();
            Log.Info("autotest templates: " + EntitySync.TemplateSelfTest(typeof(StoredMinionIdentity)) + "; " + EntitySync.TemplateSelfTest(typeof(MinionIdentity)));
            Log.Info("autotest guest: template self-test (a state machine restoring later, outside a record): StickerBomb.StatesInstance mapping " +
                     (KSerialization.Manager.HasDeserializationMapping(typeof(StickerBomb.StatesInstance)) ? "ok" : "MISSING"));
            yield return new WaitForSecondsRealtime(5f);
            Log.Info($"autotest ui: after 5 s | {Calls.Report()}");
        }

        /// <summary>Guest: open the printing pod when the host's round is there, look at the cards, print the first.</summary>
        private static IEnumerator GuestPrint()
        {
            float until = Time.realtimeSinceStartup + 40f;
            while (!Immigration.Instance.ImmigrantsAvailable && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(1f);
            yield return new WaitForSecondsRealtime(2f); // the host's offers follow its "available"
            Telepad pad = Object.FindObjectOfType<Telepad>();
            if (pad == null || !Immigration.Instance.ImmigrantsAvailable) { Log.Warn("autotest print: no printables"); yield break; }
            int before = Components.LiveMinionIdentities.Count;
            ImmigrantScreen.InitializeImmigrantScreen(pad);
            yield return new WaitForSecondsRealtime(1.5f);
            var containers = Traverse.Create(ImmigrantScreen.instance).Field("containers").GetValue<List<ITelepadDeliverableContainer>>();
            var names = new List<string>();
            CharacterContainer first = null;
            foreach (var c in containers)
            {
                if (c is CharacterContainer cc) { names.Add(cc.Stats?.Name ?? "?"); if (first == null) first = cc; }
                else if (c is CarePackageContainer cp) names.Add(cp.carePackageInstanceData?.info?.id ?? "?");
            }
            Log.Info($"autotest print: the pod screen shows: {string.Join(", ", names.ToArray())}");
            if (first == null) yield break;
            string chosen = first.Stats.Name;
            first.SelectDeliverable();
            AccessTools.Method(typeof(ImmigrantScreen), "OnProceed").Invoke(ImmigrantScreen.instance, null);
            yield return new WaitForSecondsRealtime(6f);
            bool here = false;
            foreach (MinionIdentity m in Components.LiveMinionIdentities.Items) if (m != null && m.GetProperName() == chosen) here = true;
            Log.Info($"autotest print: chose {chosen}; duplicants {before} -> {Components.LiveMinionIdentities.Count}, {chosen} {(here ? "is here" : "is NOT here")} | {Printing.Report()}");
        }

        /// <summary>For the sync test: the status items on every building / duplicant / critter, and the notifications.</summary>
        public static string StatusDump()
        {
            var sb = new System.Text.StringBuilder();
            void One(KMonoBehaviour o)
            {
                if (o == null) return;
                var sel = o.GetComponent<KSelectable>();
                var k = o.GetComponent<KPrefabID>();
                if (sel == null || k == null || sel.GetStatusItemGroup() == null) return;
                var ids = new List<string>();
                foreach (StatusItemGroup.Entry e in sel.GetStatusItemGroup()) if (e.item != null) ids.Add(e.item.Id);
                ids.Sort(System.StringComparer.Ordinal);
                sb.Append("S ").Append(k.InstanceID).Append(' ').Append(k.PrefabTag.Name).Append(' ').Append(string.Join(",", ids.ToArray())).Append('\n');
            }
            foreach (BuildingComplete b in Components.BuildingCompletes.Items) One(b);
            foreach (Brain b in Components.Brains.Items) One(b);
            var map = Traverse.Create(KPrefabIDTracker.Get()).Field("prefabIdMap").GetValue<Dictionary<int, KPrefabID>>();
            if (map != null)
                foreach (var kv in map)
                {
                    var fx = kv.Value != null ? kv.Value.GetComponent<Klei.AI.Effects>() : null;
                    if (fx == null || kv.Key >= EntitySync.LocalIdBase) continue;
                    var fxList = Traverse.Create(fx).Field("effects").GetValue<List<Klei.AI.EffectInstance>>();
                    if (fxList == null || fxList.Count == 0) continue;
                    var parts = new List<string>();
                    foreach (var ei in fxList) parts.Add(ei.effect.Id + ":" + ei.timeRemaining.ToString("0"));
                    parts.Sort(System.StringComparer.Ordinal);
                    sb.Append("F ").Append(kv.Key).Append(' ').Append(kv.Value.PrefabTag.Name).Append(' ').Append(string.Join(",", parts.ToArray())).Append('\n');
                    if (!CommandBus.IsHost) sb.Append("G ").Append(kv.Key).Append(' ').Append(StatusSync.ServerEffectsOf(kv.Key)).Append('\n');
                }
            var list = Traverse.Create(NotificationManager.Instance).Field("notifications").GetValue<List<Notification>>();
            if (list != null)
            {
                var titles = new List<string>();
                foreach (Notification n in list) if (n != null) titles.Add(n.titleText);
                titles.Sort(System.StringComparer.Ordinal);
                foreach (string t in titles) sb.Append("N ").Append(t.Replace('\n', ' ')).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>For the sync test: the settings the UI changes, for every object that has them.</summary>
        public static string SettingsDump()
        {
            var sb = new System.Text.StringBuilder();
            foreach (BuildingComplete b in Components.BuildingCompletes.Items)
            {
                if (b == null) continue;
                int id = b.GetComponent<KPrefabID>().InstanceID;
                var cap = b.GetComponent<IUserControlledCapacity>();
                if (cap != null) sb.Append($"CAP {id} {cap.UserMaxCapacity:F1}\n");
                var door = b.GetComponent<Door>();
                if (door != null) sb.Append($"DOOR {id} {door.RequestedState}\n");
                var en = b.GetComponent<BuildingEnabledButton>();
                if (en != null) sb.Append($"EN {id} {en.IsEnabled}\n");
                var fab = b.GetComponent<ComplexFabricator>();
                if (fab != null) foreach (ComplexRecipe r in fab.GetRecipes()) { int n = fab.GetRecipeQueueCount(r); if (n != 0) sb.Append($"FAB {id} {r.id} {n}\n"); }
                var timer = b.GetComponent<LogicTimerSensor>();
                if (timer != null) sb.Append($"TIMER {id} {timer.onDuration:F1} {timer.offDuration:F1} {timer.displayCyclesMode}\n");
                var range = b.GetComponent<LogicTimeOfDaySensor>();
                if (range != null) sb.Append($"RANGE {id} {range.startTime:F3} {range.duration:F3}\n");
                var a = b.GetComponent<Assignable>();
                if (a != null && a.assignee != null) sb.Append($"ASSIGN {id} {(a.assignee.GetSoleOwner() != null ? a.assignee.GetSoleOwner().GetComponent<KPrefabID>().InstanceID : -1)}\n");
            }
            sb.Append($"RES {(Research.Instance.GetActiveResearch() != null ? Research.Instance.GetActiveResearch().tech.Id : "-")}\n");
            foreach (MinionIdentity m in Components.LiveMinionIdentities.Items)
            {
                if (m == null) continue;
                int id = m.GetComponent<KPrefabID>().InstanceID;
                sb.Append($"PRIO {id} {m.GetComponent<ChoreConsumer>().GetPersonalPriority(Db.Get().ChoreGroups.Dig)}\n");
                sb.Append($"FOOD {id} {m.GetComponent<ConsumableConsumer>().IsPermitted("FieldRation")}\n");
            }
            List<Schedule> schedules = ScheduleManager.Instance.GetSchedules();
            for (int i = 0; i < schedules.Count; i++)
            {
                var blocks = new List<string>();
                foreach (ScheduleBlock bl in schedules[i].GetBlocks()) blocks.Add(bl.GroupId);
                sb.Append($"SCHED {i} {string.Join(",", blocks.ToArray())}\n");
            }
            return sb.ToString();
        }

        private static int finishedCell = -1;

        /// <summary>Host: complete one of the guest's tile construction sites as if a duplicant had built it (materials
        /// put in, then the game's own completion), so the guest must swap the site for the finished tile.</summary>
        private static void FinishOneTile()
        {
            foreach (int c in tileCells)
            {
                GameObject go = Grid.Objects[c, (int)ObjectLayer.Building];
                var site = go != null ? go.GetComponent<Constructable>() : null;
                if (site == null) continue;
                try
                {
                    var storage = site.GetComponent<Storage>();
                    storage.AddOre(SimHashes.SandStone, 100f, 293f, byte.MaxValue, 0);
                    AccessTools.Method(typeof(Constructable), "OnCompleteWork").Invoke(site, new object[] { null });
                    finishedCell = c;
                    Log.Info($"autotest host: finished the construction at {c}");
                }
                catch (System.Exception e) { Log.Warn("autotest host: finishing a construction failed: " + e); }
                return;
            }
            Log.Warn("autotest host: no construction site to finish");
        }

        private static void RemoveThatTile()
        {
            if (finishedCell < 0) return;
            GameObject go = Grid.Objects[finishedCell, (int)ObjectLayer.Building];
            var d = go != null ? go.GetComponent<Deconstructable>() : null;
            if (d == null) { Log.Warn($"autotest host: nothing to deconstruct at {finishedCell}"); return; }
            List<GameObject> drops = d.ForceDestroyAndGetMaterials();
            Log.Info($"autotest host: deconstructed {go.PrefabID().Name} at {finishedCell}, dropped {drops?.Count ?? 0} items");
        }

        /// <summary>Host: a building for the guest's panels to change (this save has none of these), on a free cell
        /// near the anchor.</summary>
        private static void BuildForTest(int anchor, string id)
        {
            BuildingDef def = Assets.GetBuildingDef(id);
            if (def == null) return;
            for (int r = 3; r < 20; r++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int c = Grid.OffsetCell(anchor, dx, r);
                    if (!Grid.IsValidCell(c) || Grid.Solid[c] || Grid.Objects[c, (int)ObjectLayer.Building] != null) continue;
                    try
                    {
                        GameObject go = def.Build(c, Orientation.Neutral, null, new List<Tag> { SimHashes.Copper.CreateTag() }, 293f, playsound: false);
                        Log.Info($"autotest host: built {id} #{go.GetComponent<KPrefabID>().InstanceID} at {c}");
                    }
                    catch (System.Exception e) { Log.Warn($"autotest host: building {id} failed: {e.Message}"); }
                    return;
                }
        }

        private static bool secondRound;

        /// <summary>Guest: the second round comes; reject all of it, as the confirm button does.</summary>
        private static IEnumerator GuestReject()
        {
            float until = Time.realtimeSinceStartup + 90f;
            while (!Immigration.Instance.ImmigrantsAvailable && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(1f);
            yield return new WaitForSecondsRealtime(2f);
            Telepad pad = Object.FindObjectOfType<Telepad>();
            if (pad == null || !Immigration.Instance.ImmigrantsAvailable) { Log.Warn("autotest reject: no second round came"); yield break; }
            int before = Components.LiveMinionIdentities.Count;
            ImmigrantScreen.InitializeImmigrantScreen(pad);
            yield return new WaitForSecondsRealtime(1.5f);
            Calls.UiDispatch = true;
            try { AccessTools.Method(typeof(ImmigrantScreen), "OnRejectionConfirmed").Invoke(ImmigrantScreen.instance, null); }
            catch (System.Exception e) { Log.Warn("autotest reject: " + (e.InnerException ?? e).Message); }
            finally { Calls.UiDispatch = false; }
            yield return new WaitForSecondsRealtime(4f);
            Log.Info($"autotest reject: rejected all; still available here: {Immigration.Instance.ImmigrantsAvailable}; duplicants {before} -> {Components.LiveMinionIdentities.Count} | {Printing.Report()}");
        }

        private static MinionStorage storedIn;
        private static System.Guid storedMinion = System.Guid.Empty;

        /// <summary>Host: what boarding a rocket does to a duplicant (the game's own MinionStorage), preferring one
        /// wearing a suit.</summary>
        private static void StoreOneMinion()
        {
            storedIn = Components.MinionStorages.Count > 0 ? Components.MinionStorages.Items[0] : null;
            if (storedIn == null) { Log.Warn("autotest host: no minion storage (rocket) to store a duplicant in"); return; }
            MinionIdentity pick = null;
            foreach (MinionIdentity m in Components.LiveMinionIdentities.Items)
            {
                if (m == null) continue;
                Equipment eq = m.GetEquipment();
                bool suit = eq != null && eq.IsSlotOccupied(Db.Get().AssignableSlots.Suit);
                if (pick == null || suit) pick = m;
                if (suit) break;
            }
            if (pick == null) return;
            try
            {
                int id = pick.GetComponent<KPrefabID>().InstanceID;
                string name = pick.name;
                storedIn.SerializeMinion(pick.gameObject);
                List<MinionStorage.Info> info = storedIn.GetStoredMinionInfo();
                storedMinion = info[info.Count - 1].id;
                Log.Info($"autotest host: stored {name} #{id} in {storedIn.name}");
            }
            catch (System.Exception e) { Log.Warn("autotest host: storing a duplicant failed: " + e); }
        }

        private static void ReleaseThatMinion()
        {
            if (storedIn == null || storedMinion == System.Guid.Empty) return;
            try
            {
                GameObject m = storedIn.DeserializeMinion(storedMinion, storedIn.transform.GetPosition());
                Log.Info($"autotest host: released {(m != null ? m.name + " #" + m.GetComponent<KPrefabID>().InstanceID : "nothing")} from {storedIn.name}");
            }
            catch (System.Exception e) { Log.Warn("autotest host: releasing the duplicant failed: " + e); }
        }

        private static IEnumerator GuestScenario()
        {
            int anchor = Anchor();
            Look(anchor);
            FindDig(anchor, 0, 6, digCells);
            FindTiles(anchor, tileCells);
            FindDig(anchor, 6, 3, hostDigCells); // the host picks these (same scan, after the guest's)
            Log.Info($"autotest guest: anchor {anchor}, dig [{string.Join(",", digCells.ConvertAll(x => x.ToString()).ToArray())}], tiles [{string.Join(",", tileCells.ConvertAll(x => x.ToString()).ToArray())}]");
            yield return null;

            PlayerController.Instance.ActivateTool(DigTool.Instance); // sets the dig tool's default filters
            yield return null;
            Drag(DigTool.Instance, digCells);
            yield return null; // end of frame: the batch goes out
            PlayerController.Instance.ActivateTool(SelectTool.Instance);
            Log.Info("autotest guest: dig sent; locally right now: " + Report());

            BuildingDef tile = Assets.GetBuildingDef("Tile");
            BuildTool.Instance.Activate(tile, new List<Tag> { SimHashes.SandStone.CreateTag() });
            yield return null;
            MethodInfo tryBuild = AccessTools.Method(typeof(BuildTool), "TryBuild");
            foreach (int c in tileCells) tryBuild.Invoke(BuildTool.Instance, new object[] { c });
            PlayerController.Instance.ActivateTool(SelectTool.Instance);
            Log.Info("autotest guest: tiles sent; locally right now: " + Report());

            bool before = SpeedControlScreen.Instance.IsPaused;
            SpeedControlScreen.Instance.TogglePause();
            yield return new WaitForSecondsRealtime(3f);
            Log.Info($"autotest guest: pause toggled by request: was {(before ? "paused" : "running")}, now {(SpeedControlScreen.Instance.IsPaused ? "paused" : "running")}");
            SpeedControlScreen.Instance.TogglePause();
            yield return new WaitForSecondsRealtime(3f);
            Log.Info($"autotest guest: toggled back: now {(SpeedControlScreen.Instance.IsPaused ? "paused" : "running")}");
            if (SpeedControlScreen.Instance.IsPaused)
            {
                SpeedControlScreen.Instance.TogglePause(); // leave the shared game running for the comparison below
                yield return new WaitForSecondsRealtime(2f);
            }

            yield return new WaitForSecondsRealtime(4f);
            Log.Info("autotest guest: result: " + Report() + $" | sent {CommandBus.Sent}, replayed {CommandBus.Replayed}");
            yield return GuestUi();
        }

        private static IEnumerator HostScenario(CoopSettings s)
        {
            FastSave.SelfCheck();
            int anchor = Anchor();
            Look(anchor);
            if (Immigration.Instance != null && !Immigration.Instance.ImmigrantsAvailable) Immigration.Instance.timeBeforeSpawn = 0f; // printables now
            BuildForTest(anchor, "LogicTimerSensor");
            BuildForTest(anchor, "LogicTimeOfDaySensor");
            // wait for the guest's orders (dig + 3 tiles), at most 90 s
            float until = Time.realtimeSinceStartup + 90f;
            while (CommandBus.Executed < 4 && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(1f);
            var candidates = new List<int>();
            FindDig(anchor, 0, 12, candidates);
            hostDigCells.Clear();
            foreach (int c in candidates) if (!digCells.Contains(c) && hostDigCells.Count < 3) hostDigCells.Add(c);
            PlayerController.Instance.ActivateTool(DigTool.Instance);
            yield return null;
            Drag(DigTool.Instance, hostDigCells);
            yield return null;
            PlayerController.Instance.ActivateTool(SelectTool.Instance);
            yield return new WaitForSecondsRealtime(10f);
            Log.Info("autotest host: result: " + Report() + $" | executed for guests {CommandBus.Executed}, sent {CommandBus.Sent}");
            for (int i = 0; i < 6; i++)
            {
                yield return new WaitForSecondsRealtime(8f);
                if (i == 1) FinishOneTile();   // between shots 1 and 2: a construction completes on the host
                if (i == 2) StoreOneMinion();  // between shots 2 and 3: a duplicant goes into the rocket's storage
                if (i == 3) RemoveThatTile();  // between shots 3 and 4: and that building is deconstructed
                if (i == 4) ReleaseThatMinion(); // between shots 4 and 5: and comes back out
                // once the guest printed from the first round, a second round for it to reject
                if (Printing.Printed > 0 && Printing.Rejected == 0 && !secondRound && Immigration.Instance != null && !Immigration.Instance.ImmigrantsAvailable)
                {
                    secondRound = true;
                    Immigration.Instance.timeBeforeSpawn = 0f;
                    Log.Info("autotest host: a second printing round, for the guest to reject");
                }
                if (i == 5) Log.Info($"autotest host: {Printing.Report()}, duplicants {Components.LiveMinionIdentities.Count}");
                byte n = (byte)(++syncShots);
                var msg = new byte[5];
                msg[0] = n;
                System.BitConverter.GetBytes(Puppets.Clock).CopyTo(msg, 1); // the clock the puppet timeline uses
                EntitySync.FlushNow();
                BuildingSync.FlushNow(System.BitConverter.ToSingle(msg, 1)); // as of the signal's moment
                GlobalSync.FlushNow();
                StatusSync.FlushNow();
                ReportSync.FlushNow();
                OperationSync.FlushNow();
                Log.Info(StatusSync.ByteReport());
                CommandBus.Client.SendRelay(OniCoop.Net.Proto.TargetAll, ChShot, msg);
                SyncShot(s, msg);
            }
            // the host saves (backup on the relay), then loses the relay for a moment (the guest pauses, then resyncs)
            string backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(s.autoLoad), "coop-backup-test.sav");
            SaveLoader.Instance.Save(backup, true, false);
            Log.Info("autotest host: saved " + backup);
            yield return new WaitForSecondsRealtime(10f);
            Log.Info("autotest host: dropping the connection to the relay");
            CoopRuntime.Instance.TestDrop();
        }
    }
}
