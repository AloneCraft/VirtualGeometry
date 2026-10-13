using System;
using System.Collections.Generic;

namespace Omni.PlanetSurface{
    /// <summary>
    /// Cube-sphereの表示葉と隣接制約を管理します。月面LODの生成完了時だけ状態を確定し、
    /// 作成途中の子パッチを表示葉へ混ぜません。地形評価・Unityオブジェクトから独立しています。
    /// </summary>
    public sealed class SurfaceLodTopology{
        private readonly HashSet<SurfaceTileKey> leaves=new();
        private readonly int maximumLeaves;
        public IReadOnlyCollection<SurfaceTileKey> Leaves=>this.leaves;

        /// <summary>表示中の葉を変えず、次の接続関係だけを準備するための独立コピーです。</summary>
        public SurfaceLodTopology Copy(){
            var result=new SurfaceLodTopology(this.maximumLeaves);
            result.leaves.Clear();result.leaves.UnionWith(this.leaves);return result;
        }

        /// <summary>6面の根から開始します。最大表示数は6以上を指定してください。</summary>
        public SurfaceLodTopology(int maximumLeaves){
            if(maximumLeaves<6){throw new ArgumentOutOfRangeException(nameof(maximumLeaves));}
            this.maximumLeaves=maximumLeaves;
            for(int face=0;face<6;face++){this.leaves.Add(new SurfaceTileKey(face,0,0,0));}
        }

        /// <summary>天体方向を覆う現在の表示葉を返します。</summary>
        public SurfaceTileKey FindLeaf(SurfaceVector direction){
            FaceUv(direction,out int face,out double u,out double v);
            for(int level=0;level<=20;level++){
                int scale=1<<level;
                var key=new SurfaceTileKey(face,level,Math.Clamp((int)(u*scale),0,scale-1),
                    Math.Clamp((int)(v*scale),0,scale-1));
                if(this.leaves.Contains(key)){return key;}
            }
            throw new InvalidOperationException("表示葉に穴があります。");
        }

        /// <summary>辺の外側にある葉を返します。segmentは親辺を4分割した位置です。</summary>
        public SurfaceTileKey Neighbour(SurfaceTileKey key,int edge,int segment){
            if(edge<0||edge>3||segment<0||segment>3){throw new ArgumentOutOfRangeException(nameof(edge));}
            double scale=1<<key.Level;
            double u=-1+2*(key.X+0.5)/scale;
            double v=-1+2*(key.Y+0.5)/scale;
            double along=((segment+0.5)/4-0.5)*2/scale;
            double outside=(1+1e-6)/scale;
            switch(edge){
                case 0:u+=along;v-=outside;break;
                case 1:u+=outside;v+=along;break;
                case 2:u+=along;v+=outside;break;
                default:u-=outside;v+=along;break;
            }
            return this.FindLeaf(SurfaceGeometry.CubeDirection(key.Face,u,v));
        }

        /// <summary>分割前に必要な粗い隣接葉を返します。自身なら直ちに分割できます。</summary>
        public SurfaceTileKey FirstSplitDependency(SurfaceTileKey requested){
            if(!this.leaves.Contains(requested)){throw new ArgumentException("分割対象が表示葉ではありません。");}
            for(int edge=0;edge<4;edge++){
                for(int segment=0;segment<4;segment++){
                    SurfaceTileKey neighbour=this.Neighbour(requested,edge,segment);
                    if(neighbour.Level<requested.Level){return this.FirstSplitDependency(neighbour);}
                }
            }
            return requested;
        }

        /// <summary>親を4葉へ確定します。生成中は呼ばず、全子パッチが利用可能になってから呼びます。</summary>
        public bool TrySplit(SurfaceTileKey key){
            if(!this.leaves.Contains(key)||key.Level>=20||this.leaves.Count+3>this.maximumLeaves){return false;}
            for(int edge=0;edge<4;edge++){
                for(int segment=0;segment<4;segment++){
                    if(this.Neighbour(key,edge,segment).Level<key.Level){return false;}
                }
            }
            this.leaves.Remove(key);
            for(int y=0;y<2;y++){
                for(int x=0;x<2;x++){this.leaves.Add(key.Child(x,y));}
            }
            return true;
        }

        /// <summary>4子が葉で、外周の隣接差を守れる場合だけ親へ統合します。</summary>
        public bool TryMerge(SurfaceTileKey parent){
            if(!this.CanMerge(parent)){return false;}
            for(int y=0;y<2;y++){
                for(int x=0;x<2;x++){this.leaves.Remove(parent.Child(x,y));}
            }
            this.leaves.Add(parent);return true;
        }

        /// <summary>状態を変えずに統合可能か調べます。描画スケジューラの優先順位付けに使います。</summary>
        public bool CanMerge(SurfaceTileKey parent){
            for(int y=0;y<2;y++){
                for(int x=0;x<2;x++){
                    if(!this.leaves.Contains(parent.Child(x,y))){return false;}
                }
            }
            for(int edge=0;edge<4;edge++){
                for(int segment=0;segment<4;segment++){
                    if(this.Neighbour(parent,edge,segment).Level>parent.Level+1){return false;}
                }
            }
            return true;
        }

        /// <summary>整数階層上の親キーを返します。</summary>
        public static SurfaceTileKey Parent(SurfaceTileKey child){
            if(child.Level<=0){throw new ArgumentOutOfRangeException(nameof(child));}
            return new SurfaceTileKey(child.Face,child.Level-1,child.X/2,child.Y/2);
        }

        /// <summary>Cubeの面選択をdoubleだけで行います。戻り値は面内の[0,1]です。</summary>
        public static void FaceUv(SurfaceVector direction,out int face,out double u,out double v){
            if(!double.IsFinite(direction.X)||!double.IsFinite(direction.Y)||!double.IsFinite(direction.Z)||
                (direction.X==0&&direction.Y==0&&direction.Z==0)){
                throw new ArgumentOutOfRangeException(nameof(direction));
            }
            double ax=Math.Abs(direction.X),ay=Math.Abs(direction.Y),az=Math.Abs(direction.Z);
            double a,b;
            if(ay>=ax&&ay>=az){
                face=0;if(direction.Y<0){face=1;}
                a=direction.X/ay;b=direction.Z/ay;
            }else if(ax>=az){
                face=2;if(direction.X<0){face=3;}
                a=direction.Y/ax;b=direction.Z/ax;
            }else{
                face=4;if(direction.Z<0){face=5;}
                a=direction.X/az;b=direction.Y/az;
            }
            u=Math.Clamp(a*0.5+0.5,0,1);v=Math.Clamp(b*0.5+0.5,0,1);
        }
    }
}
