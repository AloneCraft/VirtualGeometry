using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// 変換済みassetをScene view/Hierarchyへドラッグすると、代替MeshのMeshFilter・MeshRendererとVGの部品(非表示)を持つGameObjectを作る。
    /// MeshやPrefabをドラッグして置くのと同じ操作にし、既存GameObjectは変更しない。通常はモデル自体をドラッグすればよい。
    /// </summary>
    [InitializeOnLoad]
    internal static class VirtualGeometryDragAndDrop{
        private const string UndoName="Create Virtual Geometry";

        static VirtualGeometryDragAndDrop(){
            DragAndDrop.AddDropHandlerV2(SceneDrop);
            DragAndDrop.AddDropHandlerV2(HierarchyDrop);
        }

        internal static DragAndDropVisualMode SceneDrop(Object dropUpon,Vector3 worldPosition,Vector2 viewportPosition,Transform parentForDraggedObjects,bool perform){
            List<VirtualGeometryMeshAsset> assets=DraggedAssets();
            if(assets==null){return DragAndDropVisualMode.None;}
            if(perform){DragAndDrop.AcceptDrag();Create(assets,parentForDraggedObjects,worldPosition,-1);}
            return DragAndDropVisualMode.Copy;
        }

        internal static DragAndDropVisualMode HierarchyDrop(EntityId dropTargetEntityId,HierarchyDropFlags dropMode,Transform parentForDraggedObjects,bool perform){
            List<VirtualGeometryMeshAsset> assets=DraggedAssets();
            if(assets==null){return DragAndDropVisualMode.None;}
            if(perform){
                DragAndDrop.AcceptDrag();
                Transform parent=parentForDraggedObjects;int sibling=-1;
                var target=EditorUtility.EntityIdToObject(dropTargetEntityId) as GameObject;
                if(parent==null&&target!=null){
                    // 項目の上へ落とせば子に、項目の間へ落とせばその項目の直後に置く。
                    if((dropMode&HierarchyDropFlags.DropUpon)!=0){
                        parent=target.transform;
                    }else{
                        parent=target.transform.parent;sibling=target.transform.GetSiblingIndex()+1;
                    }
                }
                Vector3 position=Vector3.zero;
                if(parent!=null){position=parent.position;}
                Create(assets,parent,position,sibling);
            }
            return DragAndDropVisualMode.Copy;
        }

        // ドラッグ中の全てがVG assetのときだけ扱う。他の種類が混ざればUnity既定の処理へ任せる。
        private static List<VirtualGeometryMeshAsset> DraggedAssets(){
            Object[] dragged=DragAndDrop.objectReferences;
            if(dragged==null||dragged.Length==0){return null;}
            var assets=new List<VirtualGeometryMeshAsset>();
            foreach(Object item in dragged){
                if(!(item is VirtualGeometryMeshAsset asset)){return null;}
                assets.Add(asset);
            }
            return assets;
        }

        private static void Create(List<VirtualGeometryMeshAsset> assets,Transform parent,Vector3 position,int sibling){
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName(UndoName);
            var created=new List<Object>();
            foreach(VirtualGeometryMeshAsset asset in assets){
                var go=new GameObject(asset.name);
                if(parent!=null){go.transform.SetParent(parent,false);}
                go.transform.position=position;
                if(sibling>=0){go.transform.SetSiblingIndex(sibling+created.Count);}
                GameObjectUtility.EnsureUniqueNameForSibling(go);
                go.AddComponent<VirtualGeometryRenderer>().SetMeshAsset(asset);
                Undo.RegisterCreatedObjectUndo(go,UndoName);
                created.Add(go);
            }
            Undo.CollapseUndoOperations(group);
            Selection.objects=created.ToArray();
        }
    }
}
