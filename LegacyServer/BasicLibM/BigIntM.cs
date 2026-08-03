using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServerLibM
{
    class BigIntM
    {
        public string GetValue()
        {
            string rtn = _strInt; ;
            if (_bPlus)
                return rtn;


            return "-" + _strInt;
        }

        string _strInt;
        bool _bPlus = true;          // plus = true, minus = false

        public BigIntM(int n)
        {
            var str = n.ToString();
            if (n < 0)
            {
                _bPlus = false;
                _strInt = str.Substring(1);
            }
            else
            {
                _strInt = str;
            }
        }

        public BigIntM(string strInt)
        {
            if (strInt.StartsWith("-"))
            {
                _bPlus = false;
                _strInt = strInt.Substring(1);
            }
            else
            {
                _strInt = strInt;
            }
        }


        // 어떤 숫자가 더 큰지 결정
        // 부호는 상관없으니 주의
        public int Compare(BigIntM b)   // 같으면 0,  a가 크면 0 <,  b가 크면 0 >             
        {
            string aStr = "", bStr = "";

            aStr = _strInt;
            bStr = b._strInt;

            bool bSame = true;
            if (aStr.Length > bStr.Length)
            {
                return 1;
            }
            else if (aStr.Length == bStr.Length)
            {
                for (int i = 0; i < aStr.Length; i++)
                {
                    int aNum = int.Parse(aStr[i].ToString());
                    int bNum = int.Parse(bStr[i].ToString());

                    if (aNum > bNum)
                    {
                        return 1;
                    }
                    else if (aNum < bNum)
                    {
                        return -1;
                    }
                }
            }
            else
            {
                return -1;
            }

            return 0;
        }

        static BigIntM _Calc(bool bCalcPlus, BigIntM big, BigIntM small)
        {

            int lenLong = 0, lenShort = 0, numLong, numShort, numMul, numLeft, upNum, writeNum = 0, numQuotitent, totalNum, idxLong, idxShort;
            string strLong = "", strShort = "";

            strLong = big._strInt;
            strShort = small._strInt;

            lenLong = strLong.Length;
            lenShort = strShort.Length;

            StringBuilder sb = new StringBuilder();

            upNum = 0;
            for (int i = 1; i <= lenLong; i++)
            {
                idxLong = lenLong - i;
                idxShort = lenShort - i;

                numLong = int.Parse(strLong[idxLong].ToString());

                if (idxShort >= 0)
                    numShort = int.Parse(strShort[idxShort].ToString());
                else
                    numShort = 0;

                if (bCalcPlus) // 플러스 계산이면
                {
                    totalNum = numLong + numShort + upNum;
                    upNum = totalNum / 10;
                }
                else // 마이너스 계산이면
                {
                    if (numLong - numShort + upNum >= 0)
                    {
                        totalNum = numLong - numShort + upNum;
                        upNum = 0;
                    }
                    else
                    {
                        totalNum = numLong - numShort + upNum + 10; // 10을 빌려옴
                        upNum = -1;
                    }
                }

                writeNum = totalNum % 10;
                sb.Insert(0, writeNum);
            }

            if (upNum > 0)
            {
                sb.Insert(0, upNum);
            }

            if (bCalcPlus)  // 서로 같은 부호만 bCalcPlus true, 즉 더하기로 호출됨!!
            {
                if (big._bPlus == false)
                {
                    sb.Insert(0, "-");
                }
            }
            else // 서로 다른 부호만 bCalcPlus false, 즉 빼기로 호출 됨!!
            {
                if (big._bPlus == false)
                {
                    sb.Insert(0, "-");
                }
            }


            return new BigIntM(sb.ToString());

        }


        public static BigIntM operator +(BigIntM a, BigIntM b)
        {

            int iComp = a.Compare(b);
            bool bSame = false;

            BigIntM big = null;
            BigIntM small = null;
            if (iComp > 0)
            {
                big = a;
                small = b;
            }
            else if (iComp < 0)
            {
                big = b;
                small = a;
            }
            else
            {
                bSame = true;
            }

            BigIntM rtn = null;

            if (big._bPlus != small._bPlus)  // 부호가 서로 다르고
            {
                if (bSame == true)   // 둘이 같으면
                {
                    rtn = new BigIntM(0);
                }
                else // 큰거 에서 작은거 빼기
                {
                    rtn = _Calc(false, big, small);
                }
            }
            else  // 부호가 서로 같으면 서로 더하기
            {
                rtn = _Calc(true, big, small);
            }

            return rtn;
        }

        public static BigIntM operator -(BigIntM a)
        {
            a._bPlus = a._bPlus ? false : true;
            return a;
        }

        public static BigIntM operator -(BigIntM a, BigIntM b)
        {
            return a + (-b);
        }

        public static BigIntM operator -(BigIntM a, int b)
        {
            return a + (new BigIntM(-b));
        }


        public static BigIntM operator ^(BigIntM a, int b)
        {
            if (b == 0)
                return new BigIntM(1);

            BigIntM rtn = new BigIntM(1);
            for (int i = 0; i < b; i++)
            {
                rtn = rtn * a;
            }

            return rtn;
        }

        //public (string) operator + (BigInteger a, BigInteger b)
        //{
        //    return "";
        //}
        static public BigIntM operator *(BigIntM a, BigIntM b)
        {
            int lenLong = 0, lenShort = 0, numLong, numShort, numMul, numLeft, upNum, writeNum = 0, numQuotitent, totalNum, idxLong, idxShort;
            string strLong = "", strShort = "";

            int iComp = a.Compare(b);
            if (iComp > 0)
            {
                strLong = a._strInt;
                lenLong = a._strInt.Length;
                strShort = b._strInt;
                lenShort = b._strInt.Length;
            }
            else
            {
                strLong = b._strInt;
                lenLong = b._strInt.Length;
                strShort = a._strInt;
                lenShort = a._strInt.Length;
            }

            upNum = 0;
            BigIntM rtn = new BigIntM(0);

            for (int k = 1; k <= lenShort; k++)
            {
                idxShort = lenShort - k;
                numShort = int.Parse(strShort[idxShort].ToString());
                StringBuilder sb = new StringBuilder();

                for (int i = 1; i <= lenLong; i++)
                {
                    idxLong = lenLong - i;
                    numLong = int.Parse(strLong[idxLong].ToString());

                    totalNum = numLong * numShort + upNum;

                    upNum = totalNum / 10;
                    writeNum = totalNum % 10;
                    sb.Insert(0, writeNum);
                }

                if (upNum > 0)
                {
                    sb.Insert(0, upNum);
                }

                rtn = rtn + new BigIntM(sb.ToString());
            }

            // 서로 다른 부호인지 검사
            if (a._bPlus != b._bPlus)
            {
                rtn = -rtn;
            }
            return rtn;
        }
    }
}

}
