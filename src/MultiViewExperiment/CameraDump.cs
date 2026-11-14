using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace MultiViewExperiment
{
    /// <summary>Writes the whole live camera stack to the log so later iterations work from real values.</summary>
    internal static class CameraDump
    {
        public static string LayerNames(int mask)
        {
            if (mask == -1) return "Everything";
            if (mask == 0) return "Nothing";
            var names = new List<string>();
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                string n = LayerMask.LayerToName(i);
                names.Add(string.IsNullOrEmpty(n) ? "#" + i : n);
            }
            return string.Join("|", names);
        }

        public static string Build(Camera mrtCamera)
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== MultiView camera dump ====");
            sb.AppendLine($"Unity {Application.unityVersion}, gfx {SystemInfo.graphicsDeviceType} '{SystemInfo.graphicsDeviceName}', colorSpace {QualitySettings.activeColorSpace}");
            sb.AppendLine($"Screen {Screen.width}x{Screen.height}, Grid {Grid.WidthInCells}x{Grid.HeightInCells}, DLC1(SpacedOut)={DlcManager.IsExpansion1Active()}");
            sb.AppendLine($"Camera.main='{(Camera.main ? Camera.main.name : "null")}', Game.MainCamera='{(Game.MainCamera ? Game.MainCamera.name : "null")}', MRT camera='{(mrtCamera ? mrtCamera.name : "NOT FOUND")}'");

            var cc = CameraController.Instance;
            if (cc != null)
            {
                cc.GetWorldCamera(out Vector2I off, out Vector2I size);
                sb.AppendLine($"CameraController pos={cc.transform.position} ortho={cc.OrthographicSize:F2} activeWorld offset={off} size={size}");
                try
                {
                    var t = Traverse.Create(cc);
                    int tl = t.Field("timelapseCameraCullingMask").GetValue<LayerMask>().value;
                    int tlo = t.Field("timelapseOverlayCameraCullingMask").GetValue<LayerMask>().value;
                    sb.AppendLine($"timelapseCameraCullingMask=0x{tl:X8} [{LayerNames(tl)}]");
                    sb.AppendLine($"timelapseOverlayCameraCullingMask=0x{tlo:X8} [{LayerNames(tlo)}]");
                }
                catch (Exception e)
                {
                    sb.AppendLine("timelapse masks unreadable: " + e.Message);
                }
            }

            sb.AppendLine("Layers: " + string.Join(", ",
                Enumerable.Range(0, 32).Where(i => !string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                          .Select(i => $"{i}={LayerMask.LayerToName(i)}")));

            // Includes disabled cameras (timelapse ones); skip prefab assets that are not in a scene.
            var cams = Resources.FindObjectsOfTypeAll<Camera>()
                                .Where(c => c.gameObject.scene.IsValid())
                                .OrderBy(c => c.depth).ToList();
            foreach (var c in cams)
            {
                var rt = c.targetTexture;
                var comps = c.GetComponents<Component>()
                             .Where(x => x != null && !(x is Transform) && !(x is Camera))
                             .Select(x => x.GetType().Name);
                sb.AppendLine(
                    $"- '{c.name}' enabled={c.enabled}/{c.gameObject.activeInHierarchy} depth={c.depth} tag={c.tag} " +
                    $"parent='{(c.transform.parent ? c.transform.parent.name : "-")}' " +
                    $"clear={c.clearFlags} bg={c.backgroundColor} ortho={c.orthographic}/{c.orthographicSize:F2} " +
                    $"pos={c.transform.position} near/far={c.nearClipPlane}/{c.farClipPlane} rect={c.rect} " +
                    $"path={c.renderingPath} hdr={c.allowHDR} msaa={c.allowMSAA} sort={c.transparencySortMode} " +
                    $"cmdBufs={c.commandBufferCount} target={(rt ? $"{rt.name}({rt.width}x{rt.height},{rt.format})" : "screen")}");
                sb.AppendLine($"    mask=0x{c.cullingMask:X8} [{LayerNames(c.cullingMask)}]");
                sb.AppendLine($"    components=[{string.Join(", ", comps)}]");
            }
            sb.AppendLine("==== end camera dump ====");
            return sb.ToString();
        }
    }
}
