using System;
using UnityEngine;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>番号の子の割り当ての結果。型は <see cref="VirtualObject.ChildTemplates"/> の index か、直接のプレハブ。</summary>
    public struct ChildAssignment{
        /// <summary>子の型の index(−1 なら子を作らない。<see cref="Prefab"/> があればそちらを使う)。</summary>
        public int TemplateIndex;
        /// <summary>型の代わりに使う、VirtualObject を付けたプレハブ(上書き)。</summary>
        public VirtualObject Prefab;
        /// <summary>子に渡すパラメータ(場の値、強さなど。型が使う)。</summary>
        public Vector4 Parameters;
        /// <summary>この割り当てを決めた層の名前(Editor の表示用)。</summary>
        public string Source;

        public static ChildAssignment Empty=>new ChildAssignment{TemplateIndex=-1};

        /// <summary>子を作るか。</summary>
        public bool HasChild=>this.Prefab!=null||this.TemplateIndex>=0;
    }

    /// <summary>割り当て規則に渡す値。</summary>
    public readonly struct ChildAssignmentContext{
        public readonly VirtualObjectNode Node;
        public readonly int Number;
        public readonly VirtualObjectPart Part;
        /// <summary>部分の原点の、ルートの局所の位置(Logical)。場や領域の標本化に使う。</summary>
        public readonly LogicalVector3 RootLocalOrigin;
        /// <summary>番号ごとに決定的な乱数の種(ノードの種と番号から作る)。</summary>
        public readonly uint Seed;

        public ChildAssignmentContext(VirtualObjectNode node,int number,in VirtualObjectPart part,in LogicalVector3 rootLocalOrigin,uint seed){
            this.Node=node;this.Number=number;this.Part=part;this.RootLocalOrigin=rootLocalOrigin;this.Seed=seed;
        }
    }

    /// <summary>
    /// 割り当て規則の層。番号ごとに、層を並びの順に評価し、後の層が前の結果を上書きする(上書きしないときは result を変えない)。
    /// 組み込み: 既定(<see cref="DefaultTemplateRule"/>)、場による分類(<see cref="FieldClassificationRule"/>)、領域(<see cref="RegionRule"/>)、番号ごとの上書き(<see cref="OverrideTableRule"/>)。
    /// </summary>
    public interface IChildAssignmentRule{
        void Apply(in ChildAssignmentContext context,ref ChildAssignment result);
    }

    /// <summary>層を順に評価する。</summary>
    public static class ChildAssignments{
        public static ChildAssignment Evaluate(IChildAssignmentRule[] rules,in ChildAssignmentContext context){
            ChildAssignment result=ChildAssignment.Empty;
            if(rules==null){return result;}
            for(int i=0;i<rules.Length;i++){
                if(rules[i]==null){continue;}
                rules[i].Apply(in context,ref result);
            }
            return result;
        }

        /// <summary>ノードの種と番号から、番号ごとに決定的な種を作る。</summary>
        public static uint Mix(uint seed,int number){
            uint value=seed^(uint)number*0x9E3779B9u;
            value^=value>>16;value*=0x7FEB352Du;value^=value>>15;value*=0x846CA68Bu;value^=value>>16;
            return value;
        }
    }

    /// <summary>既定: 全番号に同じ型。</summary>
    [Serializable]
    public sealed class DefaultTemplateRule:IChildAssignmentRule{
        public int TemplateIndex;

        public void Apply(in ChildAssignmentContext context,ref ChildAssignment result){
            result.TemplateIndex=this.TemplateIndex;result.Prefab=null;result.Source="default";
        }
    }

    /// <summary>番号ごとの上書きの表。指定した番号だけ、型・プレハブ・パラメータを変える(子を作らない指定もできる)。</summary>
    [Serializable]
    public sealed class OverrideTableRule:IChildAssignmentRule{
        [Serializable]
        public struct Row{
            public int Number;
            /// <summary>型の index(−1 なら子を作らない。Prefab があればそちらを使う)。</summary>
            public int TemplateIndex;
            public VirtualObject Prefab;
            public Vector4 Parameters;
        }

        public Row[] Rows=Array.Empty<Row>();

        public void Apply(in ChildAssignmentContext context,ref ChildAssignment result){
            for(int i=0;i<this.Rows.Length;i++){
                if(this.Rows[i].Number!=context.Number){continue;}
                result.TemplateIndex=this.Rows[i].TemplateIndex;result.Prefab=this.Rows[i].Prefab;result.Parameters=this.Rows[i].Parameters;result.Source="override";
            }
        }

        /// <summary>番号の行(無ければ −1)。</summary>
        public int IndexOf(int number){
            for(int i=0;i<this.Rows.Length;i++){
                if(this.Rows[i].Number==number){return i;}
            }
            return -1;
        }
    }

    /// <summary>領域: ルートの局所の位置が球の中なら、型を割り当てる(都市の範囲、火山の範囲など)。</summary>
    [Serializable]
    public sealed class RegionRule:IChildAssignmentRule{
        /// <summary>球の中心(ルートの局所の位置、m、10 進の文字列)。</summary>
        public string CenterX="0";
        public string CenterY="0";
        public string CenterZ="0";
        public float Radius=1000f;
        public int TemplateIndex;
        public VirtualObject Prefab;
        public Vector4 Parameters;

        public void Apply(in ChildAssignmentContext context,ref ChildAssignment result){
            if(!Omni.Logical.Scalar.TryParse(this.CenterX,out Omni.Logical.Scalar x)||!Omni.Logical.Scalar.TryParse(this.CenterY,out Omni.Logical.Scalar y)||
                !Omni.Logical.Scalar.TryParse(this.CenterZ,out Omni.Logical.Scalar z)){
                return;
            }
            var center=new LogicalVector3(x,y,z);
            if(!LogicalVector3.TrySubtract(context.RootLocalOrigin,center,out LogicalVector3 difference)||!difference.TryToFloat3(out Unity.Mathematics.float3 offset)){return;}
            if(Unity.Mathematics.math.length(offset)>this.Radius){return;}
            result.TemplateIndex=this.TemplateIndex;result.Prefab=this.Prefab;result.Parameters=this.Parameters;result.Source="region";
        }
    }

    /// <summary>
    /// 場(Field)の標本。ルートの局所の位置で、分類の値(整数の種類と、強さなど)を返す。
    /// 惑星では、全球の格子 + 局所の Override(<c>PlanetRasterLayer</c> と同じ形)の分類の格子を想定する。
    /// </summary>
    public interface IClassificationField{
        /// <summary>分類(種類の番号。−1 は値が無い)と、型に渡すパラメータ。</summary>
        bool TrySample(in LogicalVector3 rootLocalPosition,out int classification,out Vector4 parameters);
    }

    /// <summary>場による分類: 場の値(種類の番号)ごとに型を割り当てる(海、火山、都市など)。</summary>
    [Serializable]
    public sealed class FieldClassificationRule:IChildAssignmentRule{
        [Serializable]
        public struct Mapping{
            public int Classification;
            public int TemplateIndex;
            public VirtualObject Prefab;
        }

        [SerializeReference] public IClassificationField Field;
        public Mapping[] Mappings=Array.Empty<Mapping>();

        public void Apply(in ChildAssignmentContext context,ref ChildAssignment result){
            if(this.Field==null||!this.Field.TrySample(in context.RootLocalOrigin,out int classification,out Vector4 parameters)){return;}
            for(int i=0;i<this.Mappings.Length;i++){
                if(this.Mappings[i].Classification!=classification){continue;}
                result.TemplateIndex=this.Mappings[i].TemplateIndex;result.Prefab=this.Mappings[i].Prefab;result.Parameters=parameters;result.Source="field";
                return;
            }
        }
    }
}
