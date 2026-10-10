using System;
using UnityEngine;

namespace VirtualGeometry.World{
    /// <summary>cube-sphere の四分木の1つの Cell(面、階層、格子番号)。</summary>
    public readonly struct PlanetCellKey:IEquatable<PlanetCellKey>{
        /// <summary>同じ階層の隣の向き。0:−u(x−1)、1:+u(x+1)、2:−v(y−1)、3:+v(y+1)。</summary>
        public const int DirectionCount=4;

        public readonly int Face;
        public readonly int Level;
        public readonly PlanetCellCoordinate X;
        public readonly PlanetCellCoordinate Y;

        public PlanetCellKey(int face,int level,PlanetCellCoordinate x,PlanetCellCoordinate y){
            if(face<0||face>=CubeSphere.FaceCount){throw new ArgumentOutOfRangeException(nameof(face));}
            if(level<0||level>PlanetCellCoordinate.Bits){throw new ArgumentOutOfRangeException(nameof(level));}
            if(!x.IsWithin(level)||!y.IsWithin(level)){throw new ArgumentOutOfRangeException(nameof(x),"The coordinate is outside the level.");}
            this.Face=face;this.Level=level;this.X=x;this.Y=y;
        }

        public static PlanetCellKey Root(int face){
            return new PlanetCellKey(face,0,PlanetCellCoordinate.Zero,PlanetCellCoordinate.Zero);
        }

        /// <summary>子(index は 0〜3。bit0 が x、bit1 が y)。</summary>
        public PlanetCellKey Child(int index){
            if(index<0||index>3){throw new ArgumentOutOfRangeException(nameof(index));}
            return new PlanetCellKey(this.Face,this.Level+1,this.X.Child(index&1),this.Y.Child((index>>1)&1));
        }

        public PlanetCellKey Parent(){
            if(this.Level==0){throw new InvalidOperationException("A root cell has no parent.");}
            return new PlanetCellKey(this.Face,this.Level-1,this.X.Parent(),this.Y.Parent());
        }

        /// <summary>この Cell が親の何番目の子か(0〜3)。</summary>
        public int ChildIndex=>this.X.LowestBit|(this.Y.LowestBit<<1);

        /// <summary>
        /// 同じ階層の隣の Cell。面の境界をまたぐときは、隣の面の格子番号へ整数で写す(浮動小数の誤差を持ち込まない)。
        /// 面 f の辺の点は n_f ± u_f + b·v_f(または n_f + a·u_f ± v_f)。隣の面 g は法線がその ±u_f(±v_f)の面で、
        /// g の座標は、n_f の成分が g の辺(±1)に、辺に沿った成分(b または a)が g のもう一方の座標(±)になる。
        /// </summary>
        public PlanetCellKey Neighbor(int direction){
            if(direction<0||direction>=DirectionCount){throw new ArgumentOutOfRangeException(nameof(direction));}
            PlanetCellCoordinate last=PlanetCellCoordinate.Last(this.Level);
            if(direction==0&&!this.X.IsZero){return new PlanetCellKey(this.Face,this.Level,this.X.Decrement(),this.Y);}
            if(direction==1&&!this.X.Equals(last)){return new PlanetCellKey(this.Face,this.Level,this.X.Increment(),this.Y);}
            if(direction==2&&!this.Y.IsZero){return new PlanetCellKey(this.Face,this.Level,this.X,this.Y.Decrement());}
            if(direction==3&&!this.Y.Equals(last)){return new PlanetCellKey(this.Face,this.Level,this.X,this.Y.Increment());}

            Vector3Int n=CubeSphere.Normal(this.Face);
            Vector3Int crossing;
            Vector3Int along;
            PlanetCellCoordinate alongIndex;
            if(direction==0){
                crossing=-CubeSphere.U(this.Face);along=CubeSphere.V(this.Face);alongIndex=this.Y;
            }else if(direction==1){
                crossing=CubeSphere.U(this.Face);along=CubeSphere.V(this.Face);alongIndex=this.Y;
            }else if(direction==2){
                crossing=-CubeSphere.V(this.Face);along=CubeSphere.U(this.Face);alongIndex=this.X;
            }else{
                crossing=CubeSphere.V(this.Face);along=CubeSphere.U(this.Face);alongIndex=this.X;
            }
            int target=CubeSphere.FaceWithNormal(crossing);
            Vector3Int tu=CubeSphere.U(target);Vector3Int tv=CubeSphere.V(target);
            int normalOnU=Dot(n,tu);
            PlanetCellCoordinate x;
            PlanetCellCoordinate y;
            if(normalOnU!=0){
                x=Edge(normalOnU,last);
                y=Along(Dot(along,tv),alongIndex,this.Level);
            }else{
                y=Edge(Dot(n,tv),last);
                x=Along(Dot(along,tu),alongIndex,this.Level);
            }
            return new PlanetCellKey(target,this.Level,x,y);
        }

        /// <summary>この Cell の範囲が other の範囲を含むか(other が自分か子孫)。</summary>
        public bool Contains(in PlanetCellKey other){
            if(other.Face!=this.Face||other.Level<this.Level){return false;}
            PlanetCellKey ancestor=other;
            while(ancestor.Level>this.Level){ancestor=ancestor.Parent();}
            return ancestor.Equals(this);
        }

        private static int Dot(Vector3Int a,Vector3Int b){
            return a.x*b.x+a.y*b.y+a.z*b.z;
        }

        private static PlanetCellCoordinate Edge(int sign,PlanetCellCoordinate last){
            if(sign>0){return last;}
            return PlanetCellCoordinate.Zero;
        }

        private static PlanetCellCoordinate Along(int sign,PlanetCellCoordinate index,int level){
            if(sign>0){return index;}
            return index.Complement(level);
        }

        public bool Equals(PlanetCellKey other){
            return this.Face==other.Face&&this.Level==other.Level&&this.X.Equals(other.X)&&this.Y.Equals(other.Y);
        }

        public override bool Equals(object obj){
            return obj is PlanetCellKey other&&this.Equals(other);
        }

        public override int GetHashCode(){
            return HashCode.Combine(this.Face,this.Level,this.X,this.Y);
        }

        public override string ToString(){
            return "F"+this.Face+"/L"+this.Level+"/"+this.X+","+this.Y;
        }
    }
}
