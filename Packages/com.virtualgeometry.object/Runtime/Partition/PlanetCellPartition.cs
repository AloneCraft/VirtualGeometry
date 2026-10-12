using System;
using UnityEngine;
using VirtualGeometry.World;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>
    /// 惑星の cube-sphere の Cell の四分木で分ける。根のノードは 6 面(番号 0..5)、Cell のノードは 4 つの子(番号 = 子の index)。
    /// 子のノードの状態は <see cref="PlanetCellKey"/>。部分の原点は、ノードの Cell の原点からの差を Logical で引いたもの(大きな惑星でも mm の精度)。
    /// 子の型には、同じ <see cref="PlanetCellPartition"/> を持つ Cell のノード(再帰)か、地表の詳細の型を割り当てる。
    /// </summary>
    [Serializable]
    public sealed class PlanetCellPartition:IVirtualObjectPartition{
        /// <summary>惑星の半径(m、10 進の文字列。Logical に変換する)。</summary>
        public string Radius="600000";
        /// <summary>Cell の境界に足す、地形の高さの上界(m)。</summary>
        [Min(0f)] public float HeightAmplitude;
        /// <summary>これより深い Cell は分けない。</summary>
        [Range(0,PlanetCellFrame.MaximumLevel)] public int MaximumLevel=20;

        private LogicalScalar radius;
        private string parsedRadius;

        private bool TryRadius(out LogicalScalar value){
            if(this.parsedRadius!=this.Radius){
                if(!LogicalScalar.TryParse(this.Radius,out this.radius)){
                    value=default;
                    return false;
                }
                this.parsedRadius=this.Radius;
            }
            value=this.radius;
            return true;
        }

        public int GetPartCount(VirtualObjectNode node){
            if(!(node.PartitionState is PlanetCellKey key)){return CubeSphere.FaceCount;}
            if(key.Level>=this.MaximumLevel){return 0;}
            return 4;
        }

        public bool TryGetPart(VirtualObjectNode node,int number,out VirtualObjectPart part){
            part=default;
            if(!this.TryRadius(out LogicalScalar value)){return false;}
            if(!(this.CreateChildState(node,number) is PlanetCellKey child)){return false;}
            if(!PlanetCellFrame.TryCreate(in child,in value,out PlanetCellFrame childFrame)){return false;}
            LogicalVector3 parentOrigin=default;
            Vector3 parentApproximate=Vector3.zero;
            if(node.PartitionState is PlanetCellKey key){
                if(!PlanetCellFrame.TryCreate(in key,in value,out PlanetCellFrame frame)){return false;}
                parentOrigin=frame.Origin;parentApproximate=frame.ApproximateOrigin;
            }
            if(!LogicalVector3.TrySubtract(childFrame.Origin,parentOrigin,out LogicalVector3 offset)){return false;}
            float radiusMeters=float.Parse(this.Radius,System.Globalization.CultureInfo.InvariantCulture);
            // Cell の対角の半分(弧の長さ)+ 地形の高さの上界。
            float half=radiusMeters*Mathf.PI*0.25f*Mathf.Pow(2f,-child.Level)*1.5f;
            part=new VirtualObjectPart(in offset,childFrame.ApproximateOrigin-parentApproximate,half+this.HeightAmplitude,childFrame.ApproximateOrigin.normalized);
            return true;
        }

        public object CreateChildState(VirtualObjectNode node,int number){
            if(node.PartitionState is PlanetCellKey key){
                if(number<0||number>3||key.Level>=this.MaximumLevel){return null;}
                return key.Child(number);
            }
            if(number<0||number>=CubeSphere.FaceCount){return null;}
            return PlanetCellKey.Root(number);
        }

        public bool IsPartVisible(VirtualObjectNode node,int number,Vector3 viewerLocal){
            return true;
        }
    }
}
