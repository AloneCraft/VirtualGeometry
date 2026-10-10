using System;

namespace VirtualGeometry.World{
    /// <summary>
    /// cube-sphere の1つの面の上の、Cell の格子番号(0〜2^階層−1)。96ビットの符号なし整数。
    /// 10^20 m の天体を 1mm まで分けると約77段になり、64ビットでは足りないため。
    /// </summary>
    public readonly struct PlanetCellCoordinate:IEquatable<PlanetCellCoordinate>{
        public const int Bits=96;
        public readonly ulong Low;
        public readonly uint High;

        public PlanetCellCoordinate(ulong low,uint high){
            this.Low=low;this.High=high;
        }

        public static PlanetCellCoordinate Zero=>default;

        /// <summary>2^level−1(その階層の最後の番号)。</summary>
        public static PlanetCellCoordinate Last(int level){
            CheckLevel(level);
            if(level==0){return default;}
            if(level<64){return new PlanetCellCoordinate((1UL<<level)-1,0);}
            if(level==64){return new PlanetCellCoordinate(ulong.MaxValue,0);}
            return new PlanetCellCoordinate(ulong.MaxValue,(uint)((1UL<<(level-64))-1));
        }

        /// <summary>1つ細かい階層の番号(2倍+bit)。</summary>
        public PlanetCellCoordinate Child(int bit){
            if(bit!=0&&bit!=1){throw new ArgumentOutOfRangeException(nameof(bit));}
            if((this.High&0x80000000u)!=0){throw new OverflowException("PlanetCellCoordinate exceeds 96 bits.");}
            ulong low=(this.Low<<1)|(ulong)bit;
            uint high=(this.High<<1)|(uint)(this.Low>>63);
            return new PlanetCellCoordinate(low,high);
        }

        /// <summary>1つ粗い階層の番号(半分)。</summary>
        public PlanetCellCoordinate Parent(){
            ulong low=(this.Low>>1)|((ulong)(this.High&1u)<<63);
            return new PlanetCellCoordinate(low,this.High>>1);
        }

        /// <summary>最下位ビット(子のどちら側か)。</summary>
        public int LowestBit=>(int)(this.Low&1UL);

        public PlanetCellCoordinate Increment(){
            ulong low=this.Low+1;
            uint high=this.High;
            if(low==0){high++;}
            return new PlanetCellCoordinate(low,high);
        }

        public PlanetCellCoordinate Decrement(){
            if(this.IsZero){throw new OverflowException("PlanetCellCoordinate is already zero.");}
            uint high=this.High;
            if(this.Low==0){high--;}
            return new PlanetCellCoordinate(this.Low-1,high);
        }

        /// <summary>同じ階層で反対側から数えた番号(2^level−1−this)。</summary>
        public PlanetCellCoordinate Complement(int level){
            PlanetCellCoordinate last=Last(level);
            ulong low=last.Low-this.Low;
            uint high=last.High-this.High;
            if(this.Low>last.Low){high--;}
            return new PlanetCellCoordinate(low,high);
        }

        public bool IsZero=>this.Low==0&&this.High==0;

        /// <summary>この階層で表せる範囲(0〜2^level−1)にあるか。</summary>
        public bool IsWithin(int level){
            CheckLevel(level);
            PlanetCellCoordinate last=Last(level);
            if(this.High!=last.High){return this.High<last.High;}
            return this.Low<=last.Low;
        }

        /// <summary>this/2^level を float で近似する。CPU の Cell の選択の幾何に使う(細かい階層では精度が落ちる)。</summary>
        public float Fraction(int level){
            CheckLevel(level);
            float value=(float)this.High*18446744073709551616f+(float)this.Low;
            return value*MathF.Pow(2f,-level);
        }

        public bool Equals(PlanetCellCoordinate other){
            return this.Low==other.Low&&this.High==other.High;
        }

        public override bool Equals(object obj){
            return obj is PlanetCellCoordinate other&&this.Equals(other);
        }

        public override int GetHashCode(){
            return HashCode.Combine(this.Low,this.High);
        }

        public override string ToString(){
            if(this.High==0){return this.Low.ToString();}
            return this.High.ToString("x")+":"+this.Low.ToString("x16");
        }

        private static void CheckLevel(int level){
            if(level<0||level>Bits){throw new ArgumentOutOfRangeException(nameof(level));}
        }
    }
}
