using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServerLibM
{

    /// <summary>
    /// DateTimeStartEndGroupM : Distinct한 기간들의 그룹 (이 클래스는 생성되는 순간 시간리스트의 중복을 제거 한다)
    /// Date 시간을 Start와 End로 쌍으로 구성해서 특정 시간대의 Add와 Sub를 실행하는 클래스 임
    /// </summary>
    public class DateTimeStartEndGroupM
    {
        public List<DateTimeStartEndM> TimeList { get; set; }
        public bool bSorted { get; set; }

        public DateTimeStartEndGroupM(IEnumerable<DateTimeStartEndM> timeList)
        {
            TimeList = timeList.ToList();   // copy
            DistinctDateTimeStartEnd();
        }

        public DateTimeStartEndGroupM(DateTimeStartEndM dateTimeStartEnd) : this(new List<DateTimeStartEndM> { dateTimeStartEnd })
        {
            ;
        }

        public void Sort()
        {
            if (bSorted == true)
                return;

            TimeList.Sort(DateTimeStartEndM.Compare);
            bSorted = true;
        }

        public int Count()
        {
            return TimeList.Count;
        }

        public DateTimeStartEndM this[int idx]
        {
            get
            {
                return TimeList[idx];
            }
        }

        public void Split(int idx, int iCnt, int iSplitIntervalMin)
        {
            var target = TimeList[idx];
            TimeList.RemoveAt(idx);
            TimeList.AddRange(target.Split(iCnt, iSplitIntervalMin));

            DistinctDateTimeStartEnd();
        }

        public void Split(int idx, IEnumerable<double> percentList, int iSplitIntervalMin)
        {
            var target = TimeList[idx];
            TimeList.RemoveAt(idx);
            var splited = target.Split(percentList, iSplitIntervalMin);

            TimeList.AddRange(splited.TimeList);
            DistinctDateTimeStartEnd();
        }


        public void DistinctDateTimeStartEnd()
        {
            TimeList = DistinctDateTimeStartEnd(TimeList);
            bSorted = true;

        }

        /// <summary>
        /// DateTimeStartEndM 리스트에서 중복된 시간 범위를 합쳐서 중복이 하나도 없는 시간 범위로 만듬 (내부적으로 dateTimeStartEndList sort해서 add처리)
        /// </summary>
        /// <param name="dateTimeStartEndList"></param>
        /// <returns></returns>
        static public List<DateTimeStartEndM> DistinctDateTimeStartEnd(IEnumerable<DateTimeStartEndM> dateTimeStartEndList)
        {
            if (dateTimeStartEndList.Count() < 0)
            {
                throw new ArgumentException($"Count가 0임:{dateTimeStartEndList.Count()}");
            }

            List<DateTimeStartEndM> rtn = dateTimeStartEndList.ToList();
            rtn.Sort(DateTimeStartEndM.Compare);

            int startIdx = -1;
            int endIdx = -1;
            List<int> startIdxList = new List<int>();
            List<int> endIdxList = new List<int>();
            List<DateTimeStartEndM> endStartEndList = new List<DateTimeStartEndM>();

            int iLoop = rtn.Count() - 1;    // 다음꺼까지 계산하므로 -1            
            DateTimeStartEndM compareStart = rtn[0];    // 비교 기준
            DateTimeStartEndM endStartEnd = null;


            for (int i = 0; i < iLoop; i++)
            {
                var type = compareStart.GetOverlapTypeTo(rtn[i + 1]);
                if (type != DateTimeStartEndM.TIME_OVERLAP_TYPE.NONE && type != DateTimeStartEndM.TIME_OVERLAP_TYPE.ERROR)
                {
                    if (startIdx == -1)
                    {
                        startIdx = i;
                    }

                    if (type == DateTimeStartEndM.TIME_OVERLAP_TYPE.INCLUE)  // 앞에꺼가 기간이 더 길면
                    {
                        endStartEnd = rtn[i];
                    }
                    else
                    {
                        compareStart = rtn[i];
                        endStartEnd = rtn[i + 1];
                    }

                    endIdx = i + 1;

                }
                else
                {
                    if (startIdx != -1)
                    {
                        startIdxList.Add(startIdx);
                        endIdxList.Add(endIdx);
                        endStartEndList.Add(endStartEnd);

                        startIdx = -1; // 초기화
                    }

                }
            }

            if (startIdx != -1) // 젤 마지막이 Overlap된거면
            {
                startIdxList.Add(startIdx);
                endIdxList.Add(endIdx);
                endStartEndList.Add(endStartEnd);
            }


            var newRtn = new List<DateTimeStartEndM>(); // 리턴값
            int j = 0;
            int idxBefore = 0;
            for (int k = 0; k < startIdxList.Count(); k++)
            {
                idxBefore = startIdxList[k];    // overlap 시작 인덱스
                for (; j < idxBefore; j++) // overlap 전까지 add
                {
                    newRtn.Add(rtn[j]);
                }

                // overlap 된 내용 머지 해서 넣기
                var merge = MergeDateTimeStartEnd(rtn[idxBefore], endStartEndList[idxBefore]);
                newRtn.Add(merge);

                j = endIdxList[k] + 1;

                if (j >= rtn.Count())
                    break;

            }

            for (; j < rtn.Count(); j++) // 마지막 더하기
            {
                newRtn.Add(rtn[j]);
            }

            return newRtn;
        }

        /// <summary>
        /// start와 end 날짜를 강제로 하나로 병합
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        static DateTimeStartEndM MergeDateTimeStartEnd(DateTimeStartEndM start, DateTimeStartEndM end)
        {
            return new DateTimeStartEndM(start.Start, end.End);
        }

        /// <summary>
        /// 리스트 더하기, 더하면서 중복 모두 제거 됨
        /// </summary>
        /// <param name="addGroup"></param>
        /// <returns></returns>
        public void Add(DateTimeStartEndM dateTimeStartEnd)
        {
            TimeList.Add(dateTimeStartEnd);
            DistinctDateTimeStartEnd();
        }

        public void Add(DateTimeStartEndGroupM dateTimeStartEndGroup)
        {
            TimeList.AddRange(dateTimeStartEndGroup.TimeList);
            DistinctDateTimeStartEnd();
            bSorted = true;
        }

        //public static List<DateTimeStartEndM> Add(IEnumerable<DateTimeStartEndM> firstList, IEnumerable<DateTimeStartEndM> secondList)
        //{
        //    List<DateTimeStartEndM> rtn = new List<DateTimeStartEndM>();
        //    rtn.AddRange(firstList);
        //    rtn.AddRange(secondList);
        //    rtn = DistinctDateTimeStartEnd(rtn); // 다시 중복 없앰            

        //    return rtn;
        //}

    }


    /// <summary>
    /// 시작 Date와 끝 Date를 가지고 기간을 나타내는 클래스
    /// </summary>
    public class DateTimeStartEndM
    {
        public enum TIME_OVERLAP_TYPE { NONE, HEAD_OVERLAP, TAIL_OVERLAP, INCLUE, COVER, ERROR }
        public DateTimeStartEndM(DateTime start, DateTime end)
        {
            Start = start;
            End = end;
        }

        public DateTimeStartEndM(DateTime start, TimeSpan duration) // 시작시간과 지난 시간
        {
            Start = start;
            End = Start + duration;
        }

        /// <summary>
        /// 랜덤 Percent 리스트을 얻어온다
        /// </summary>
        /// <param name="iCnt">리스트 얻어올 개수</param>
        /// <param name="useRandomRate">전체(100%)에서 랜덤으로 사용할 %분 (30이라면 70%는 균등배정, 나머지 30만 랜덤하게)</param>
        /// <returns></returns>
        static public List<double> GetRandomPercent(int iCnt, int useRandomRate)
        {
            int minPercent = (100 - useRandomRate) / iCnt;  // 최소 percent (의미는 random으로 사용할 비율인 useRandomRate을 
            List<double> result = new List<double>();
            int leftPercent = 100;
            int rnd = 0;
            for (int i = 0; i < iCnt; i++)
            {
                if (leftPercent >= minPercent && i != iCnt - 1)
                {
                    rnd = new Random().Next(minPercent, leftPercent - (minPercent * (iCnt - (i + 1))));
                }
                else
                {
                    rnd = leftPercent;
                }
                result.Add((double)rnd); // 숫자 담음
                leftPercent -= rnd;
            }

            return result;
        }

        public DateTime Start { get; set; }
        public DateTime End { get; set; }

        /// <summary>
        /// 시작과 끝의 TimeSpan 구하기
        /// </summary>
        /// <returns></returns>
        public TimeSpan GetTimeSpan()
        {
            return End - Start;
        }

        static DateTimeStartEndM Zero { get { return new DateTimeStartEndM(DateTime.MinValue, DateTime.MinValue); } }

        public bool IsZero()
        {
            if (Start.CompareTo(DateTime.MinValue) == 0 && End.CompareTo(DateTime.MinValue) == 0)
                return true;

            return false;
        }


        /// <summary>
        /// 시간 기간을 개수만큼 쪼갠다 (동일하게)
        /// </summary>
        /// <param name="iCnt"></param>
        /// <returns></returns>        
        public List<DateTimeStartEndM> Split(int iCnt, int iSplitIntervalMin)  // 기간과 기간 간격은 iSplitIntervalMin
        {
            List<DateTimeStartEndM> rtn = new List<DateTimeStartEndM>();

            var tSpan = GetTimeSpan();
            var totalMin = tSpan.TotalMinutes - (iSplitIntervalMin * (iCnt - 1));


            double durMin = (int)(totalMin / iCnt);  // 총 시간

            TimeSpan spanDur;
            DateTime tempStart = Start;
            for (int i = 0; i < iCnt; i++)
            {
                spanDur = TimeSpan.FromMinutes(durMin);
                rtn.Add(new DateTimeStartEndM(tempStart, spanDur));
                tempStart = tempStart + spanDur + TimeSpan.FromMinutes(iSplitIntervalMin); // 기간과 기간사이 iSplitIntervalMin분 간격을 준다 (안그럼 add할 때 합쳐져 버림)
            }

            return rtn;
        }

        /// <summary>
        /// 시간 기간을 개수만큼 쪼갠다 (동일하게)
        /// </summary>
        /// <param name="iCnt"></param>
        /// <returns></returns>        
        public DateTimeStartEndGroupM Split(IEnumerable<double> percentList, int iSplitIntervalMin)
        {
            if (percentList.Sum() != 100f)
                throw new ArgumentException($"합이 100%가 아님 {percentList.Sum()}");

            List<DateTimeStartEndM> rtn = new List<DateTimeStartEndM>();

            int iCnt = percentList.Count();
            var tSpan = GetTimeSpan();
            var totalMin = tSpan.TotalMinutes - (iSplitIntervalMin * (iCnt - 1));   // 인터벌 시간 빼기

            TimeSpan spanDur;
            DateTime tempStart = Start;
            for (int i = 0; i < iCnt; i++)
            {
                double durMin = (int)((totalMin / 100f) * percentList.ElementAt(i)); // 분단위 절삭
                spanDur = TimeSpan.FromMinutes(durMin);
                rtn.Add(new DateTimeStartEndM(tempStart, spanDur));
                tempStart = tempStart + spanDur + TimeSpan.FromMinutes(iSplitIntervalMin);   // 기간과 기간사이 iSplitIntervalMin분 간격을 준다 (안그럼 add할 때 합쳐져 버림)
            }

            return new DateTimeStartEndGroupM(rtn);
        }


        /// <summary>
        /// DateTimeStartEndM 리스트에서 중복된 시간 범위가 있는지 검사
        /// </summary>
        /// <param name="dateTimeStartEndList"></param>
        /// <returns></returns>
        static public bool CheckOverlapTypeDateTimeStartEnd(IEnumerable<DateTimeStartEndM> dateTimeStartEndList)
        {
            var temp = dateTimeStartEndList.ToArray();
            var temp2 = dateTimeStartEndList.ToArray();

            for (int i = 0; i < temp.Length; i++)
            {
                for (int k = i + 1; k < temp2.Length; k++)
                {
                    var type = temp[i].GetOverlapTypeTo(temp2[k]);
                    if (type != TIME_OVERLAP_TYPE.NONE)
                    {
                        return true;
                    }
                }
            }

            return false;
        }



        /// <summary>
        /// 현재 기간과, 매개변수로 주어지는 기간을 비교후 어떻게 기간이 겹치는지 
        /// </summary>
        /// <param name="compDateTimeStartEnd"></param>
        /// <returns></returns>
        public TIME_OVERLAP_TYPE GetOverlapTypeTo(DateTimeStartEndM compDateTimeStartEnd)
        {
            if (Start.CompareTo(compDateTimeStartEnd.Start) >= 0 && End.CompareTo(compDateTimeStartEnd.End) <= 0)  // 매개변수 기간이 원래 기간을 덮을 때 
            {
                return TIME_OVERLAP_TYPE.COVER;
            }
            else if (Start.CompareTo(compDateTimeStartEnd.End) > 0 || End.CompareTo(compDateTimeStartEnd.Start) < 0)    // 두 기간이 전혀 겹치지 않을 때
            {
                return TIME_OVERLAP_TYPE.NONE;
            }
            else if (Start.CompareTo(compDateTimeStartEnd.Start) >= 0 && End.CompareTo(compDateTimeStartEnd.End) > 0) // 매개변수 기간이 앞부분 겹칠 때
            {
                return TIME_OVERLAP_TYPE.HEAD_OVERLAP;
            }
            else if (Start.CompareTo(compDateTimeStartEnd.Start) < 0 && End.CompareTo(compDateTimeStartEnd.End) > 0)    // 매개변수 기간이 포함되어 질 때
            {
                return TIME_OVERLAP_TYPE.INCLUE;
            }
            else if (Start.CompareTo(compDateTimeStartEnd.Start) < 0 && End.CompareTo(compDateTimeStartEnd.End) <= 0)   // 매개변수 기간이 앞부분 뒷부분 겹칠 때
            {
                return TIME_OVERLAP_TYPE.TAIL_OVERLAP;
            }



            Debug.WriteLine("버그:" + TIME_OVERLAP_TYPE.ERROR);   // 버그 상황
            return TIME_OVERLAP_TYPE.ERROR;
        }

        /// <summary>
        /// 더하면서 정렬함
        /// </summary>
        /// <param name="second"></param>
        /// <returns></returns>
        //public List<DateTimeStartEndM> Add (DateTimeStartEndM second)
        //{
        //    List<DateTimeStartEndM> rtn = new List<DateTimeStartEndM>();
        //    if(IsZero())
        //    {
        //        rtn.Add(this);
        //        return rtn;
        //    }

        //    if(second.IsZero())
        //    {
        //        rtn.Add(this);
        //        return rtn;
        //    }            

        //    var overlapType = GetOverlapTypeTo(second);
        //    if(overlapType == TIME_OVERLAP_TYPE.NONE)
        //    {
        //        rtn.Add(this);
        //        rtn.Add(second);
        //    }
        //    else if(overlapType == TIME_OVERLAP_TYPE.HEAD_OVERLAP)
        //    {
        //        rtn.Add(new DateTimeStartEndM(second.Start, End));
        //    }
        //    else if (overlapType == TIME_OVERLAP_TYPE.INCLUE)
        //    {
        //        rtn.Add(this);
        //    }
        //    else if(overlapType == TIME_OVERLAP_TYPE.TAIL_OVERLAP)
        //    {
        //        rtn.Add(new DateTimeStartEndM(Start, second.End));
        //    }            
        //    else if (overlapType == TIME_OVERLAP_TYPE.COVER)
        //    {
        //        rtn.Add(second);
        //    }

        //    rtn.Sort(DateTimeStartEndM.Compare); // 소팅                        
        //    return rtn;
        //}


        // 쏘팅 하지 않음 주의!!
        public DateTimeStartEndGroupM Add(DateTimeStartEndM second)
        {
            DateTimeStartEndGroupM newRtn;
            List<DateTimeStartEndM> rtn = new List<DateTimeStartEndM>();
            if (IsZero())
            {
                rtn.Add(this);
                newRtn = new DateTimeStartEndGroupM(rtn);
                return newRtn;
            }

            if (second.IsZero())
            {
                rtn.Add(this);
                newRtn = new DateTimeStartEndGroupM(rtn);
                return newRtn;
            }

            var overlapType = GetOverlapTypeTo(second);
            if (overlapType == TIME_OVERLAP_TYPE.NONE)
            {
                rtn.Add(this);
                rtn.Add(second);
            }
            else if (overlapType == TIME_OVERLAP_TYPE.HEAD_OVERLAP)
            {
                rtn.Add(new DateTimeStartEndM(second.Start, End));
            }
            else if (overlapType == TIME_OVERLAP_TYPE.INCLUE)
            {
                rtn.Add(this);
            }
            else if (overlapType == TIME_OVERLAP_TYPE.TAIL_OVERLAP)
            {
                rtn.Add(new DateTimeStartEndM(Start, second.End));
            }
            else if (overlapType == TIME_OVERLAP_TYPE.COVER)
            {
                rtn.Add(second);
            }

            newRtn = new DateTimeStartEndGroupM(rtn);
            return newRtn;
        }

        /// <summary>
        /// 정렬을 위한 비교 함수
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        static public int Compare(DateTimeStartEndM x, DateTimeStartEndM y)
        {
            if (x.Start.CompareTo(y.Start) == 0 && x.End.CompareTo(y.End) == 0)
                return 0;
            else if (x.Start.CompareTo(y.Start) < 0)
                return -1;
            else if (x.Start.CompareTo(y.Start) == 0 && x.End.CompareTo(y.End) < 0)
                return -1;

            return 1;
        }



        /// <summary>
        /// 리스트를 빼기
        /// </summary>
        /// <param name="timeGroup"></param>
        /// <returns></returns>
        public DateTimeStartEndGroupM Sub(DateTimeStartEndGroupM timeGroup)
        {
            DateTimeStartEndGroupM newRtn;

            List<DateTimeStartEndM> rtn = new List<DateTimeStartEndM>();


            DateTimeStartEndM temp = this; // 시작 값
            foreach (var dateStartEnd in timeGroup.TimeList)
            {
                var sub = temp.Sub(dateStartEnd);
                if (sub.Count() > 1) // Inclue 2개 분리 (뒤에꺼만 계산 하면 됨 
                {
                    rtn.Add(sub.TimeList[0]); // 처음꺼 넣음                    
                    temp = sub.TimeList[1];
                }
                else
                {
                    temp = sub.TimeList[0];
                }

            }

            rtn.Add(temp);
            newRtn = new DateTimeStartEndGroupM(rtn);
            return newRtn;
        }

        /// <summary>
        /// 시간 범위에서 시간범위를 빼기
        /// </summary>
        /// <param name="second"></param>
        /// <returns></returns>
        public DateTimeStartEndGroupM Sub(DateTimeStartEndM second)
        {
            DateTimeStartEndGroupM newRtn;
            List<DateTimeStartEndM> rtn = new List<DateTimeStartEndM>();
            if (IsZero())
            {
                rtn.Add(this);
                newRtn = new DateTimeStartEndGroupM(rtn);
                return newRtn;
            }

            if (second.IsZero())
            {
                rtn.Add(this);
                newRtn = new DateTimeStartEndGroupM(rtn);
                return newRtn;
            }

            var overlapType = GetOverlapTypeTo(second);

            if (overlapType == TIME_OVERLAP_TYPE.NONE)
            {
                rtn.Add(this);
            }
            else if (overlapType == TIME_OVERLAP_TYPE.HEAD_OVERLAP)
            {
                rtn.Add(new DateTimeStartEndM(second.End, End));
            }
            else if (overlapType == TIME_OVERLAP_TYPE.INCLUE)
            {
                rtn.Add(new DateTimeStartEndM(Start, second.Start));
                rtn.Add(new DateTimeStartEndM(second.End, End));

            }
            else if (overlapType == TIME_OVERLAP_TYPE.TAIL_OVERLAP)
            {
                rtn.Add(new DateTimeStartEndM(Start, second.Start));
            }
            else if (overlapType == TIME_OVERLAP_TYPE.COVER)    // 매개변수 기간이 겹치면 원래기간의 시작date와 끝Date값을 동일하게 
            {
                rtn.Add(new DateTimeStartEndM(Start, Start));
            }

            newRtn = new DateTimeStartEndGroupM(rtn);
            return newRtn;
        }
    }
}
