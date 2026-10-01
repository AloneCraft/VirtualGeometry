using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VirtualizedGeometry{
    public readonly struct VirtualGeometryLodSelectionContext{
        public Matrix4x4 ObjectToWorld{get;}
        public Vector3 CameraPosition{get;}
        public bool Orthographic{get;}
        public float VerticalFieldOfViewDegrees{get;}
        public float OrthographicSize{get;}
        public int PixelHeight{get;}
        public float NearPlane{get;}
        public float TargetErrorPixels{get;}
        public float Hysteresis{get;}

        public VirtualGeometryLodSelectionContext(Matrix4x4 objectToWorld,Vector3 cameraPosition,bool orthographic,float verticalFieldOfViewDegrees,
            float orthographicSize,int pixelHeight,float nearPlane,float targetErrorPixels,float hysteresis){
            if(!orthographic&&(verticalFieldOfViewDegrees<=0||verticalFieldOfViewDegrees>=180)){throw new ArgumentOutOfRangeException(nameof(verticalFieldOfViewDegrees));}
            if(orthographic&&orthographicSize<=0){throw new ArgumentOutOfRangeException(nameof(orthographicSize));}
            if(pixelHeight<=0){throw new ArgumentOutOfRangeException(nameof(pixelHeight));}
            if(!float.IsFinite(nearPlane)||nearPlane<=0){throw new ArgumentOutOfRangeException(nameof(nearPlane));}
            if(!float.IsFinite(targetErrorPixels)||targetErrorPixels<0){throw new ArgumentOutOfRangeException(nameof(targetErrorPixels));}
            if(!float.IsFinite(hysteresis)||hysteresis<0||hysteresis>=1){throw new ArgumentOutOfRangeException(nameof(hysteresis));}
            this.ObjectToWorld=objectToWorld;this.CameraPosition=cameraPosition;this.Orthographic=orthographic;
            this.VerticalFieldOfViewDegrees=verticalFieldOfViewDegrees;this.OrthographicSize=orthographicSize;this.PixelHeight=pixelHeight;
            this.NearPlane=nearPlane;this.TargetErrorPixels=targetErrorPixels;this.Hysteresis=hysteresis;
        }
    }

    /// <summary>選択がstreamingへ返すpage単位のfeedback。Missingは不足pageの要求、それ以外は利用中pageの保持。</summary>
    public readonly struct VirtualGeometryPageFeedback{
        public uint PageId{get;}
        public float Priority{get;}
        public bool Missing{get;}

        public VirtualGeometryPageFeedback(uint pageId,float priority,bool missing){
            this.PageId=pageId;this.Priority=priority;this.Missing=missing;
        }
    }

    public sealed class VirtualGeometryLodSelectionResult{
        public int[] SelectedNodes{get;}
        public int[] SelectedClusters{get;}
        public int[] RefinedNodes{get;}
        public VirtualGeometryPageFeedback[] Feedback{get;}

        internal VirtualGeometryLodSelectionResult(List<int> nodes,List<int> clusters,List<int> refined,List<VirtualGeometryPageFeedback> feedback){
            this.SelectedNodes=nodes.ToArray();this.SelectedClusters=clusters.ToArray();this.RefinedNodes=refined.ToArray();this.Feedback=feedback.ToArray();
        }
    }

    /// <summary>GPU選択と同じ誤差式・置換規則・streaming feedback規則を持つCPU参照実装。</summary>
    public static class VirtualGeometryLodSelector{
        public static float CalculateScreenError(VirtualGeometryHierarchyNode node,VirtualGeometryLodSelectionContext context){
            if(!float.IsFinite(node.GeometricError)||node.GeometricError<0||!float.IsFinite(node.BoundsRadius)||node.BoundsRadius<0){
                throw new InvalidDataException("Hierarchy node has an invalid error or radius.");
            }
            float scale=MaximumScale(context.ObjectToWorld);
            float worldError=node.GeometricError*scale;
            if(context.Orthographic){return worldError*context.PixelHeight/(2f*context.OrthographicSize);}
            Vector3 worldCenter=context.ObjectToWorld.MultiplyPoint3x4(node.BoundsCenter);
            float worldRadius=node.BoundsRadius*scale;
            float distance=Mathf.Max(context.NearPlane,Vector3.Distance(context.CameraPosition,worldCenter)-worldRadius);
            float projectionScale=0.5f*context.PixelHeight/Mathf.Tan(context.VerticalFieldOfViewDegrees*Mathf.Deg2Rad*0.5f);
            return worldError*projectionScale/distance;
        }

        public static VirtualGeometryLodSelectionResult Select(VirtualGeometryPage page,VirtualGeometryLodSelectionContext context,
            Func<uint,bool> isPageResident,ISet<int> previouslyRefined){
            if(page==null){throw new ArgumentNullException(nameof(page));}
            if(isPageResident==null){throw new ArgumentNullException(nameof(isPageResident));}
            if(page.Nodes.Length==0||page.RootNodeIndices.Length!=page.MaterialCount){throw new InvalidDataException("Page has no valid hierarchy roots.");}
            ValidateStructure(page);
            List<int> selectedNodes=new List<int>();
            List<int> selectedClusters=new List<int>();
            List<int> refinedNodes=new List<int>();
            List<VirtualGeometryPageFeedback> feedback=new List<VirtualGeometryPageFeedback>();
            for(int material=0;material<page.RootNodeIndices.Length;material++){
                uint root=page.RootNodeIndices[material];
                if(root==uint.MaxValue){continue;}
                Traverse(page,checked((int)root),context,isPageResident,previouslyRefined,selectedNodes,selectedClusters,refinedNodes,feedback,true);
            }
            return new VirtualGeometryLodSelectionResult(selectedNodes,selectedClusters,refinedNodes,feedback);
        }

        private static void Traverse(VirtualGeometryPage page,int nodeIndex,VirtualGeometryLodSelectionContext context,Func<uint,bool> isPageResident,
            ISet<int> previouslyRefined,List<int> selectedNodes,List<int> selectedClusters,List<int> refinedNodes,List<VirtualGeometryPageFeedback> feedback,bool isRoot){
            VirtualGeometryHierarchyNode node=page.Nodes[nodeIndex];
            bool childrenResident=node.ChildCount>0;
            for(uint i=0;i<node.ChildCount&&childrenResident;i++){childrenResident=isPageResident(page.Nodes[node.FirstChild+i].PageId);}
            bool wasRefined=previouslyRefined!=null&&previouslyRefined.Contains(nodeIndex);
            float threshold=context.TargetErrorPixels*(wasRefined?1f-context.Hysteresis:1f+context.Hysteresis);
            float screenError=CalculateScreenError(node,context);
            bool wantsRefinement=node.ChildCount>0&&screenError>threshold;
            bool refine=childrenResident&&wantsRefinement;
            if(isRoot){feedback.Add(new VirtualGeometryPageFeedback(node.PageId,screenError,false));}
            if(wantsRefinement){
                // 子pageの優先度は、それを必要とする親のscreen errorに揃える。
                // 誤差は親>=子なので、現在のcutが依存するpageは子孫の要求より低くならない。
                for(uint i=0;i<node.ChildCount;i++){
                    uint childPage=page.Nodes[node.FirstChild+i].PageId;
                    feedback.Add(new VirtualGeometryPageFeedback(childPage,screenError,!isPageResident(childPage)));
                }
            }
            if(refine){
                refinedNodes.Add(nodeIndex);
                for(uint i=0;i<node.ChildCount;i++){
                    Traverse(page,checked((int)(node.FirstChild+i)),context,isPageResident,previouslyRefined,selectedNodes,selectedClusters,refinedNodes,feedback,false);
                }
            }else{
                if(!isPageResident(node.PageId)){throw new InvalidDataException("A selected hierarchy node is not resident and has no valid fallback.");}
                selectedNodes.Add(nodeIndex);
                for(uint i=0;i<node.ClusterCount;i++){selectedClusters.Add(checked((int)(node.FirstCluster+i)));}
            }
        }

        private static void ValidateStructure(VirtualGeometryPage page){
            byte[] state=new byte[page.Nodes.Length];
            for(int material=0;material<page.RootNodeIndices.Length;material++){
                uint root=page.RootNodeIndices[material];
                if(root==uint.MaxValue){continue;}
                if(root>=page.Nodes.Length){throw new InvalidDataException("Hierarchy root index is out of range.");}
                ValidateStructureNode(page,checked((int)root),material,state);
            }
            for(int i=0;i<state.Length;i++){if(state[i]!=2){throw new InvalidDataException("Hierarchy contains an unreachable node.");}}
        }

        private static void ValidateStructureNode(VirtualGeometryPage page,int nodeIndex,int material,byte[] state){
            if(state[nodeIndex]==1){throw new InvalidDataException("Hierarchy contains a cycle.");}
            if(state[nodeIndex]==2){throw new InvalidDataException("Hierarchy node has multiple parents or roots.");}
            state[nodeIndex]=1;
            VirtualGeometryHierarchyNode node=page.Nodes[nodeIndex];
            ValidateNodeRanges(page,node);
            if(node.MaterialSlot!=material){throw new InvalidDataException("Hierarchy crosses a material boundary.");}
            uint expectedLeaf=node.LeafStart;
            uint covered=0;
            for(uint i=0;i<node.ChildCount;i++){
                int childIndex=checked((int)(node.FirstChild+i));
                VirtualGeometryHierarchyNode child=page.Nodes[childIndex];
                if(child.LeafStart!=expectedLeaf||child.GeometricError>node.GeometricError+1e-6f){throw new InvalidDataException("Hierarchy child coverage or error is inconsistent.");}
                ValidateStructureNode(page,childIndex,material,state);
                expectedLeaf=checked(expectedLeaf+child.LeafCount);
                covered=checked(covered+child.LeafCount);
            }
            if(node.ChildCount>0&&covered!=node.LeafCount){throw new InvalidDataException("Hierarchy children do not exactly cover their parent.");}
            state[nodeIndex]=2;
        }

        private static void ValidateNodeRanges(VirtualGeometryPage page,VirtualGeometryHierarchyNode node){
            if(node.ClusterCount==0||(ulong)node.FirstCluster+node.ClusterCount>(ulong)page.Clusters.Length){throw new InvalidDataException("Hierarchy cluster range is invalid.");}
            if(node.ChildCount>0&&(ulong)node.FirstChild+node.ChildCount>(ulong)page.Nodes.Length){throw new InvalidDataException("Hierarchy child range is invalid.");}
            if(node.LeafCount==0){throw new InvalidDataException("Hierarchy node covers no leaves.");}
        }

        private static float MaximumScale(Matrix4x4 matrix){
            float x=new Vector3(matrix.m00,matrix.m10,matrix.m20).magnitude;
            float y=new Vector3(matrix.m01,matrix.m11,matrix.m21).magnitude;
            float z=new Vector3(matrix.m02,matrix.m12,matrix.m22).magnitude;
            float scale=Mathf.Max(x,Mathf.Max(y,z));
            if(!float.IsFinite(scale)){throw new ArgumentException("Object transform has a non-finite scale.",nameof(matrix));}
            return scale;
        }
    }
}
