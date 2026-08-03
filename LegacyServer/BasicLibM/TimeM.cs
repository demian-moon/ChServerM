using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace EcsServerLibM
{
	public class TimeM
	{
		static StringBuilderM sbStatic = new StringBuilderM();
		static List<DateTime> _timeListStatic = new List<DateTime>();

		long _startTimeStamp = 0;

		long _totalTimeStamp = 0;
		long _iCntTimeStamp = 1;
		string _name;

		StringBuilderM sb = new StringBuilderM();

		public TimeM(string name)
		{
			_name = name;
		}
		public void StartTimeCheck()
		{
			//_startTimeStamp = Stopwatch.GetTimestamp();
		}

		public void EndTimeCheck()
		{
			//_totalTimeStamp += ServerTimeM.GetElapsedTick(_startTimeStamp);

			//if(new Random().Next(1, 500) == 1)
			//    Debug.WriteLine($"[{_name}] 평균 걸린 시간Ms: {ServerTimeM.GTickToMs((long)((double)_totalTimeStamp / (double)_iCntTimeStamp))} 토탈걸린시간/회수:{_totalTimeStamp} / {_iCntTimeStamp}");
			_iCntTimeStamp++;
		}

		Random rndMc = new Random();

		public double LapTimeCheck()
		{
			if (_startTimeStamp == 0)
			{
				_startTimeStamp = Stopwatch.GetTimestamp();
				return 0;
			}
			else
			{

				var curElapsedTick = TickTimeM.GetElapsedTick(_startTimeStamp);

				if (curElapsedTick == 0)
				{
					Debug.WriteLine($"{TickTimeM.GTick} {_startTimeStamp} = {TickTimeM.GTick - _startTimeStamp}");
					_totalTimeStamp -= curElapsedTick;
				}

				_totalTimeStamp += curElapsedTick;
				//if (rndMc.Next(1, 1000) == 1)
				//Debug.WriteLine($"[{_name}] 지금 걸린시간Ms {ServerTimeM.GTickToMs(curElapsedTick)} tick : {curElapsedTick} - 평균 걸린 시간Ms: {ServerTimeM.GTickToMs((long)((double)_totalTimeStamp / (double)_iCntTimeStamp ) )} 토탈걸린시간/회수:{_totalTimeStamp} / {_iCntTimeStamp}");
				_iCntTimeStamp++;
				_startTimeStamp = Stopwatch.GetTimestamp();

				return TickTimeM.GTickToMs(curElapsedTick);
			}
		}




		static public void TimeCheckStatic()
		{
			_timeListStatic.Add(DateTime.Now);
		}

		static public void GetTimeResult()
		{
			int iCount = _timeListStatic.Count;
			if (iCount % 2 != 0)
			{
				sbStatic.AppendLine("시작과 엔드 타임쌍이 맞지 않습니다.");
				sbStatic.Write();
				sbStatic.Clear();
				return;
			}

			DateTime time1;
			DateTime time2;
			TimeSpan tSpan;
			for (int i = 0, num = 1; i < iCount; i += 2, num++)
			{
				time1 = _timeListStatic[i];
				time2 = _timeListStatic[i + 1];

				tSpan = time2 - time1;
				sbStatic.AppendLine($"{num.ToString()} 번째 지난 시간은: {tSpan.ToString()} 시작시간:{time1.ToString()} 종료시간:{time2.ToString()}");
				sbStatic.Write();
				sbStatic.Clear();
			}
		}

		static public void Clear()
		{
			_timeListStatic.Clear();
		}
	}

	public class TestSec
	{
		public static int userNum = 5000;
		static int time1 = 0;
		static bool bStart = false;

		static int iCheckNum = 0;

		public enum TestSecMode { normal, average, multiply_user }
		/// <summary>
		/// 
		/// </summary>
		/// <param name="iMode">true면 기본 횟수, false면 평균</param>
		/// <param name="cntSendMsg"></param>
		static public void Check(TestSecMode iMode, string msg, int cntSendMsg)
		{
			if (bStart == false)
			{
				iCheckNum = CalcMsgCount(iMode, cntSendMsg);
				Debug.WriteLine($"{msg} :측정 횟수: {iCheckNum.ToString("#,###0")}");
				TimeM.TimeCheckStatic();
				bStart = true;

			}
		}


		static int CalcMsgCount(TestSecMode iMode, int cntSendMsg)
		{
			if (iMode == TestSecMode.average)
			{
				return ((userNum + 1) * userNum / 2) * cntSendMsg;
			}
			else if (iMode == TestSecMode.multiply_user)
			{
				return userNum * cntSendMsg;
			}
			else if (iMode == TestSecMode.normal)
			{
				return cntSendMsg;
			}

			return cntSendMsg;

		}

		public enum TestSecResultType { lock_mode, normal }
		static public void Result(TestSecResultType iType, string str)
		{
			if (bStart == true)
			{
				if (iType == TestSecResultType.lock_mode)
				{
					Interlocked.Increment(ref time1);
				}
				else
				{
					time1++;
				}

				if (time1 % 500000 == 1)
					Debug.WriteLine($"메세지받은것:{time1} ");

				if (time1 == iCheckNum)
				{
					TimeM.TimeCheckStatic();
					Debug.WriteLine(str);
					TimeM.GetTimeResult();
					time1 = 0;
					bStart = false;
				}
			}
		}
	}
}
