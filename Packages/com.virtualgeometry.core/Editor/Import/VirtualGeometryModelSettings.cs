using System;
using UnityEngine;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// モデル単位のVirtual Geometry設定。ModelImporter.userDataへ{"virtualGeometry":{...}}として保存する。
    /// userDataは他のツールも使える領域のため、空か自分が書いた内容のときだけ読み書きし、他者の内容は上書きしない。
    /// </summary>
    [Serializable]
    internal sealed class VirtualGeometryModelSettings{
        [SerializeField] private bool enabled;
        [SerializeField] private int maxTrianglesPerCluster=64;
        [SerializeField] private int hierarchyFanout=4;
        [SerializeField] private float simplificationRatio=0.5f;
        [SerializeField] private int positionQuantizationBits=16;

        [Serializable]
        private sealed class Envelope{
            public VirtualGeometryModelSettings virtualGeometry;
        }

        public bool Enabled{get=>this.enabled;set=>this.enabled=value;}
        public int MaxTrianglesPerCluster{get=>this.maxTrianglesPerCluster;set=>this.maxTrianglesPerCluster=value;}
        public int HierarchyFanout{get=>this.hierarchyFanout;set=>this.hierarchyFanout=value;}
        public float SimplificationRatio{get=>this.simplificationRatio;set=>this.simplificationRatio=value;}
        public int PositionQuantizationBits{get=>this.positionQuantizationBits;set=>this.positionQuantizationBits=value;}

        public VirtualGeometryModelSettings Clone(){return (VirtualGeometryModelSettings)this.MemberwiseClone();}

        public VirtualGeometryBuildSettings ToBuildSettings(){
            return new VirtualGeometryBuildSettings{MaxTrianglesPerCluster=this.maxTrianglesPerCluster,HierarchyFanout=this.hierarchyFanout,
                SimplificationRatio=this.simplificationRatio,PositionQuantizationBits=this.positionQuantizationBits,GenerateHierarchy=true,GenerateStreamingPages=true};
        }

        /// <summary>範囲外の値を変換器が受け付ける範囲へ丸める。</summary>
        public void Clamp(){
            this.maxTrianglesPerCluster=Mathf.Clamp(this.maxTrianglesPerCluster,1,128);
            this.hierarchyFanout=Mathf.Clamp(this.hierarchyFanout,2,8);
            this.simplificationRatio=Mathf.Clamp(this.simplificationRatio,0.1f,0.9f);
            if(this.positionQuantizationBits!=0){this.positionQuantizationBits=Mathf.Clamp(this.positionQuantizationBits,8,24);}
        }

        public bool SameAs(VirtualGeometryModelSettings other){
            return other!=null&&this.Write()==other.Write();
        }

        public string Write(){
            return JsonUtility.ToJson(new Envelope{virtualGeometry=this});
        }

        /// <summary>userDataから設定を読む。他のツールの内容ならfalseを返し、settingsは既定値になる。</summary>
        public static bool TryRead(string userData,out VirtualGeometryModelSettings settings){
            settings=new VirtualGeometryModelSettings();
            if(string.IsNullOrWhiteSpace(userData)){return true;}
            Envelope envelope;
            try{
                envelope=JsonUtility.FromJson<Envelope>(userData);
            }catch(ArgumentException){
                return false;
            }
            if(envelope==null||envelope.virtualGeometry==null){return false;}
            // 自分が書いた形と完全に一致するときだけ自分の内容とみなす。他のキーが混ざっていれば上書きで消してしまうため扱わない。
            if(envelope.virtualGeometry.Write()!=userData.Trim()){return false;}
            settings=envelope.virtualGeometry;
            return true;
        }
    }
}
