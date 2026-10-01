using UnityEditor;
namespace VirtualizedGeometry.Editor{
    /// <summary>managed domain破棄前にGraphicsBufferと非同期要求を明示的に解放する。</summary>
    [InitializeOnLoad]
    internal static class VirtualGeometryEditorLifecycle{
        static VirtualGeometryEditorLifecycle(){AssemblyReloadEvents.beforeAssemblyReload+=VirtualGeometryRenderer.ReleaseResourcesForReload;Undo.undoRedoPerformed+=RefreshRenderers;}
        private static void RefreshRenderers(){foreach(var renderer in UnityEngine.Object.FindObjectsByType<VirtualGeometryRenderer>(UnityEngine.FindObjectsInactive.Include)){renderer.RefreshAsset();}}
    }
}